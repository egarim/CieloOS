// Files — the person's own things, and the workspace they share with Cielo.
//
// Two stores sit behind this one window: each desk's own space
// (/api/home/{owner}/…) and the space a person shares with the agents they own
// (/api/shared/…). The panel this replaces put them behind a segmented toggle
// bolted onto a desk page; here they are simply *places*, listed side by side,
// because from the desktop "my files" and "the files Cielo and I pass back and
// forth" are two locations, not two modes of one screen.
//
// Three things this file is careful about:
//
//  1. Downloads cannot be a plain <a href>. The session cookie is only honoured
//     when X-Cielo-Panel rides along, so the bytes are fetched with
//     authHeaders() and handed to the browser as a blob. Three fetches once
//     shipped without that header and 401'd for everyone on the default login.
//     The same is true of a <video src>: the browser would fetch it without the
//     header, so a film is fetched here and played from a blob URL.
//
//  2. The runtime refuses to preview binary files on purpose — a spreadsheet
//     decoded as text looks like a corrupt file. So it is said plainly instead.
//
//  3. Nothing here runs a command, so no approval can arise from this window.
//     onApproval is accepted for the shell's uniform contract and deliberately
//     never called; if this view ever grows a write action, it is the prop that
//     hands the request to the shell rather than growing a dialog of its own.

import * as React from "react";
import {
  AlertTriangle,
  ChevronRight,
  CornerLeftUp,
  Download,
  FileText,
  Film,
  Folder,
  Link2,
  Loader2,
  Play,
  RotateCw,
  X,
} from "lucide-react";
import { UnauthorizedError, api, authHeaders } from "../../shared/api";
import { carriesMachineWords, serverText } from "../../shared/plain";
import type { ApprovalRecord, HomeEntry, SessionView, Whoami } from "../../shared/api";
import { useLanguage, useT } from "../../shared/i18n";

type ShellProps = {
  whoami: Whoami | null;
  sessions: SessionView[];
  reload: () => void;
  onApproval: (approval: ApprovalRecord) => void;
  notify: (message: string) => void;
};

type HomeListing = { owner: string; path: string; entries: HomeEntry[] };
type HomeFile = { owner: string; path: string; content: string; truncated: boolean; size: number; binary: boolean };

type Place = { kind: "home"; owner: string } | { kind: "shared" };

type Listing =
  | { status: "loading" }
  | { status: "ready"; entries: HomeEntry[] }
  | { status: "failed"; message: string; absent: boolean };

type Preview =
  | { status: "loading"; name: string; path: string }
  | { status: "ready"; name: string; path: string; file: HomeFile }
  | { status: "failed"; name: string; path: string; message: string };

type FilmState = {
  name: string;
  path: string;
  status: "loading" | "ready" | "failed";
  url: string | null;
  message: string | null;
};

// What the browser will actually try to decode. .mov is included because the
// common case is H.264 in a QuickTime container, which most browsers play — and
// when one does not, the <video> error path says so rather than sitting blank.
const PLAYABLE = /\.(mp4|m4v|webm|ogv|mov)$/i;

// Vocabulary the runtime uses internally and the desktop never shows a person.
// A server error is data, not copy: if one of these words is in it, the detail is
// dropped rather than passed through to the screen.
const placeKey = (place: Place) => (place.kind === "shared" ? "shared" : `home:${place.owner}`);

const endpointFor = (place: Place) =>
  place.kind === "shared" ? "/api/shared" : `/api/home/${encodeURIComponent(place.owner)}`;

const join = (parent: string, name: string) => (parent ? `${parent}/${name}` : name);

function formatSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "";
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

// The server hands back {"error":"…"} for the cases it knows about. Pull the
// sentence out of the envelope; keep it short; refuse to repeat machine words.
function detailOf(error: unknown): string {
  const text = serverText(error);
  if (!text) return "";
  // Withheld rather than translated: an answer written in the machine's own
  // vocabulary is not one to repeat at a person. The caller has a "…Unknown"
  // line ready for exactly this, so an empty string here is a decision, not a
  // missing value.
  if (carriesMachineWords(text)) return "";
  return text.length > 220 ? `${text.slice(0, 217)}…` : text;
}

const isAbsent = (error: unknown) =>
  /no home volume|no shared workspace|does not exist yet|exists yet/i.test(
    error instanceof Error ? error.message : String(error ?? ""));

const isForbidden = (error: unknown) =>
  /may not browse|forbidden/i.test(error instanceof Error ? error.message : String(error ?? ""));

export default function Files({ whoami, sessions, reload, notify }: ShellProps) {
  const t = useT();
  const language = useLanguage();

  const homes = whoami?.homes ?? [];
  const homesKey = homes.join(",");

  const [place, setPlace] = React.useState<Place | null>(null);
  const [path, setPath] = React.useState("");
  const [nonce, setNonce] = React.useState(0);
  const [listing, setListing] = React.useState<Listing>({ status: "loading" });
  const [preview, setPreview] = React.useState<Preview | null>(null);
  const [film, setFilm] = React.useState<FilmState | null>(null);
  const [saving, setSaving] = React.useState<string | null>(null);
  const [actionFailure, setActionFailure] = React.useState<string | null>(null);

  const previewTicket = React.useRef(0);
  const filmTicket = React.useRef(0);
  const filmUrl = React.useRef<string | null>(null);

  const releaseFilm = React.useCallback(() => {
    if (filmUrl.current) {
      URL.revokeObjectURL(filmUrl.current);
      filmUrl.current = null;
    }
  }, []);

  React.useEffect(() => releaseFilm, [releaseFilm]);

  // Land on the person's own space; fall back to the first desk they can read.
  React.useEffect(() => {
    if (!whoami) return;
    setPlace((current) => {
      if (current && (current.kind === "shared" || whoami.homes.includes(current.owner))) return current;
      const first = whoami.homes.includes(whoami.slug) ? whoami.slug : whoami.homes[0];
      return first ? { kind: "home", owner: first } : { kind: "shared" };
    });
  }, [whoami?.slug, homesKey]);

  // The shared space is provisioned by any session belonging to the person or to
  // one of their agents, so "is something running that would create it" is a
  // question about all of their desks, not one.
  const isRunning = (target: Place) =>
    sessions.some(
      (session) =>
        session.status === "running" &&
        (target.kind === "shared" ? homes.includes(session.owner) : session.owner === target.owner));

  const labelFor = (target: Place) => {
    if (target.kind === "shared") return t("files.sharedTitle");
    if (whoami && target.owner === whoami.slug) return t("files.placeYours");
    return target.owner;
  };

  const ownerLabel = place ? labelFor(place) : "";
  const segments = path.split("/").filter(Boolean);
  const inRecordings = place?.kind === "home" && segments[0] === "recordings";

  // ---- copy for the states this window can honestly be in -------------------

  function absentText(target: Place): string {
    const base =
      target.kind === "shared"
        ? t("files.notYetShared")
        : whoami && target.owner === whoami.slug
          ? t("files.notYetYours")
          : t("files.notYetOther", { owner: target.owner });
    return isRunning(target) ? `${base} ${t("files.notYetRetry")}` : base;
  }

  function listFailureText(error: unknown, target: Place): string {
    if (error instanceof UnauthorizedError) return t("errors.sessionTokenRejected");
    if (isForbidden(error)) return t("files.notAllowed", { owner: labelFor(target) });
    if (isAbsent(error)) return absentText(target);
    const detail = detailOf(error);
    return detail ? t("files.listFailed", { detail }) : t("files.listFailedUnknown");
  }

  function emptyText(): string {
    if (inRecordings) return t("files.emptyRecordings");
    if (segments.length > 0) return t("files.emptyFolder");
    if (!place) return t("files.emptyFolder");
    if (place.kind === "shared") return t("files.emptyShared");
    if (whoami && place.owner === whoami.slug) return t("files.emptyYours");
    return t("files.emptyOther", { owner: place.owner });
  }

  function leadText(): string {
    if (!place) return "";
    if (place.kind === "shared") return t("files.leadShared");
    if (whoami && place.owner === whoami.slug) return t("files.leadYours");
    return t("files.leadOther", { owner: place.owner });
  }

  // ---- loading a folder -----------------------------------------------------

  React.useEffect(() => {
    if (!place) return;
    let cancelled = false;
    setListing({ status: "loading" });
    (async () => {
      try {
        const query = path ? `?path=${encodeURIComponent(path)}` : "";
        const result = await api<HomeListing>(`${endpointFor(place)}/list${query}`);
        if (!cancelled) setListing({ status: "ready", entries: result.entries ?? [] });
      } catch (error) {
        if (cancelled) return;
        if (error instanceof UnauthorizedError) reload();
        setListing({
          status: "failed",
          message: listFailureText(error, place),
          absent: isAbsent(error),
        });
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [place ? placeKey(place) : "", path, nonce]);

  function goTo(target: Place) {
    if (place && placeKey(place) === placeKey(target)) return;
    closePanes();
    setPath("");
    setPlace(target);
  }

  function openFolder(next: string) {
    closePanes();
    setPath(next);
  }

  function closePanes() {
    previewTicket.current += 1;
    filmTicket.current += 1;
    setPreview(null);
    releaseFilm();
    setFilm(null);
    setActionFailure(null);
  }

  function refresh() {
    closePanes();
    setNonce((value) => value + 1);
    reload();
  }

  // ---- opening one file -----------------------------------------------------

  async function openEntry(entry: HomeEntry) {
    if (!place) return;
    const target = join(path, entry.name);
    if (entry.kind === "directory") {
      openFolder(target);
      return;
    }
    if (PLAYABLE.test(entry.name)) {
      await playFilm(entry.name, target);
      return;
    }

    filmTicket.current += 1;
    releaseFilm();
    setFilm(null);
    setActionFailure(null);

    const ticket = (previewTicket.current += 1);
    setPreview({ status: "loading", name: entry.name, path: target });
    try {
      const file = await api<HomeFile>(`${endpointFor(place)}/read?path=${encodeURIComponent(target)}`);
      if (ticket !== previewTicket.current) return;
      setPreview({ status: "ready", name: entry.name, path: target, file });
    } catch (error) {
      if (ticket !== previewTicket.current) return;
      if (error instanceof UnauthorizedError) reload();
      const detail = error instanceof UnauthorizedError ? t("errors.sessionTokenRejected") : detailOf(error);
      setPreview({
        status: "failed",
        name: entry.name,
        path: target,
        message: detail
          ? t("files.readFailed", { name: entry.name, detail })
          : t("files.readFailedUnknown", { name: entry.name }),
      });
    }
  }

  // ---- playing a film -------------------------------------------------------
  //
  // The download URL cannot go straight into <video src>: the browser would send
  // no X-Cielo-Panel header and the runtime would refuse it. So the bytes come
  // through the same authenticated fetch a save uses, and the element plays a
  // blob. It buffers the whole file, which is why the wait is announced.

  async function playFilm(name: string, target: string) {
    if (!place) return;
    previewTicket.current += 1;
    setPreview(null);
    setActionFailure(null);
    releaseFilm();

    const ticket = (filmTicket.current += 1);
    setFilm({ name, path: target, status: "loading", url: null, message: null });
    try {
      const response = await fetch(`${endpointFor(place)}/download?path=${encodeURIComponent(target)}`, {
        headers: authHeaders(),
        credentials: "same-origin",
      });
      if (ticket !== filmTicket.current) return;
      if (response.status === 401) {
        reload();
        setFilm({ name, path: target, status: "failed", url: null, message: t("errors.sessionTokenRejected") });
        return;
      }
      if (!response.ok) {
        setFilm({
          name,
          path: target,
          status: "failed",
          url: null,
          message: t("files.filmFetchFailed", { name }),
        });
        return;
      }
      const url = URL.createObjectURL(await response.blob());
      if (ticket !== filmTicket.current) {
        URL.revokeObjectURL(url);
        return;
      }
      filmUrl.current = url;
      setFilm({ name, path: target, status: "ready", url, message: null });
    } catch (error) {
      if (ticket !== filmTicket.current) return;
      setFilm({
        name,
        path: target,
        status: "failed",
        url: null,
        message: detailOf(error) || t("files.filmFetchFailed", { name }),
      });
    }
  }

  // ---- taking a file off the machine ---------------------------------------

  async function save(name: string, target: string) {
    if (!place) return;
    setActionFailure(null);
    setSaving(target);
    try {
      const response = await fetch(`${endpointFor(place)}/download?path=${encodeURIComponent(target)}`, {
        headers: authHeaders(),
        credentials: "same-origin",
      });
      if (response.status === 401) {
        setActionFailure(t("errors.sessionTokenRejected"));
        reload();
        return;
      }
      if (!response.ok) {
        setActionFailure(t("files.errorDownloadFailed", { name }));
        return;
      }
      const href = URL.createObjectURL(await response.blob());
      const anchor = document.createElement("a");
      anchor.href = href;
      anchor.download = name;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      // Revoking immediately can race the browser's own read of the blob, so let
      // the save finish first; the URL is per-download and dies with the page.
      window.setTimeout(() => URL.revokeObjectURL(href), 60_000);
      notify(t("files.savedNotice", { name }));
    } catch (error) {
      setActionFailure(detailOf(error) || t("files.errorDownloadFailed", { name }));
    } finally {
      setSaving(null);
    }
  }

  // ---- rendering ------------------------------------------------------------

  const when = (epoch: number) => {
    if (!epoch) return "";
    try {
      return new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" })
        .format(new Date(epoch * 1000));
    } catch {
      return "";
    }
  };

  const crumbs = [{ label: ownerLabel, path: "" }];
  let walked = "";
  for (const part of segments) {
    walked = join(walked, part);
    crumbs.push({ label: part, path: walked });
  }

  const paneOpen = !!preview || !!film;

  if (!whoami) {
    return (
      <div className="cfiles">
        <style>{CSS}</style>
        <p className="cfiles-state">
          <Loader2 className="cfiles-spin" size={15} aria-hidden /> {t("desks.loading")}
        </p>
      </div>
    );
  }

  return (
    <div className="cfiles" data-automation-id="files">
      <style>{CSS}</style>

      <header className="cfiles-head">
        <h2 className="cfiles-title">{t("files.appTitle")}</h2>
        <p className="cfiles-lead">{leadText()}</p>
      </header>

      <nav className="cfiles-places" aria-label={t("files.appTitle")}>
        {homes.map((slug) => {
          const target: Place = { kind: "home", owner: slug };
          const on = place?.kind === "home" && place.owner === slug;
          // files-home is the id the panel this replaces used for the same
          // destination; keeping it means existing automation still lands.
          const automation = slug === whoami.slug ? "files-home" : `files-place-${slug}`;
          return (
            <button
              key={slug}
              type="button"
              className={`cfiles-place${on ? " on" : ""}`}
              aria-current={on ? "page" : undefined}
              data-automation-id={automation}
              onClick={() => goTo(target)}
            >
              {labelFor(target)}
            </button>
          );
        })}
        <button
          type="button"
          className={`cfiles-place${place?.kind === "shared" ? " on" : ""}`}
          aria-current={place?.kind === "shared" ? "page" : undefined}
          data-automation-id="files-shared"
          onClick={() => goTo({ kind: "shared" })}
        >
          {t("files.sharedTitle")}
        </button>
      </nav>

      <div className="cfiles-bar">
        <div className="cfiles-crumbs">
          {crumbs.map((crumb, index) => (
            <React.Fragment key={crumb.path || "~"}>
              {index > 0 && (
                <span className="cfiles-sep" aria-hidden>
                  <ChevronRight size={13} />
                </span>
              )}
              <button
                type="button"
                className="cfiles-crumb"
                aria-current={index === crumbs.length - 1 ? "page" : undefined}
                onClick={() => openFolder(crumb.path)}
              >
                {crumb.label}
              </button>
            </React.Fragment>
          ))}
        </div>
        <div className="cfiles-tools">
          {segments.length > 0 && (
            <button
              type="button"
              className="cfiles-btn"
              onClick={() => openFolder(segments.slice(0, -1).join("/"))}
            >
              <CornerLeftUp size={14} aria-hidden /> {t("files.upOne")}
            </button>
          )}
          <button type="button" className="cfiles-btn" data-automation-id="files-reload" onClick={refresh}>
            <RotateCw size={14} aria-hidden /> {t("files.reload")}
          </button>
        </div>
      </div>

      {inRecordings && (
        <div className="cfiles-recnote">
          <Film size={16} aria-hidden />
          <div>
            <strong>{t("files.recordingsTitle")}</strong>
            <p>{t("files.recordingsNote")}</p>
          </div>
        </div>
      )}

      {actionFailure && (
        <p className="cfiles-fail" role="status">
          <AlertTriangle size={15} aria-hidden /> {actionFailure}
        </p>
      )}

      <div className={`cfiles-split${paneOpen ? " paired" : ""}`}>
        <section className="cfiles-list" aria-live="polite">
          {listing.status === "loading" && (
            <p className="cfiles-state">
              <Loader2 className="cfiles-spin" size={15} aria-hidden /> {t("desks.loading")}
            </p>
          )}

          {listing.status === "failed" && (
            <div className={`cfiles-state${listing.absent ? "" : " bad"}`}>
              {!listing.absent && <AlertTriangle size={16} aria-hidden />}
              <p>{listing.message}</p>
            </div>
          )}

          {listing.status === "ready" && listing.entries.length === 0 && (
            <div className="cfiles-state">
              <p>{emptyText()}</p>
            </div>
          )}

          {listing.status === "ready" &&
            listing.entries.map((entry) => {
              const target = join(path, entry.name);
              const playable = entry.kind !== "directory" && PLAYABLE.test(entry.name);
              const savable = entry.kind === "file" || entry.kind === "link";
              const selected = preview?.path === target || film?.path === target;
              const recordingsFolder = entry.kind === "directory" && !path && entry.name === "recordings";
              return (
                <div key={entry.name} className={`cfiles-row${selected ? " on" : ""}`}>
                  <button
                    type="button"
                    className="cfiles-open"
                    data-automation-id={`file-${entry.name}`}
                    disabled={entry.kind === "special"}
                    onClick={() => openEntry(entry)}
                  >
                    <span className={`cfiles-glyph${playable ? " film" : ""}`} aria-hidden>
                      {entry.kind === "directory" ? (
                        <Folder size={17} />
                      ) : playable ? (
                        <Film size={17} />
                      ) : entry.kind === "link" ? (
                        <Link2 size={17} />
                      ) : (
                        <FileText size={17} />
                      )}
                    </span>
                    <span className="cfiles-name">
                      {entry.name}
                      {recordingsFolder && <span className="cfiles-sub">{t("files.recordingsRowNote")}</span>}
                      {entry.kind === "special" && <span className="cfiles-sub">{t("files.kindSpecial")}</span>}
                    </span>
                    <span className="cfiles-meta">
                      {entry.kind === "directory" ? when(entry.modifiedEpoch) : formatSize(entry.size)}
                      {entry.kind !== "directory" && entry.modifiedEpoch ? ` · ${when(entry.modifiedEpoch)}` : ""}
                    </span>
                  </button>

                  {playable && (
                    <button
                      type="button"
                      className="cfiles-icon"
                      title={t("files.playAria", { name: entry.name })}
                      data-automation-id={`play-${entry.name}`}
                      onClick={() => playFilm(entry.name, target)}
                    >
                      <Play size={14} aria-hidden />
                      <span className="srOnly">{t("files.playAria", { name: entry.name })}</span>
                    </button>
                  )}

                  {savable && (
                    <button
                      type="button"
                      className="cfiles-icon"
                      title={t("files.downloadEntry", { name: entry.name })}
                      data-automation-id={`download-${entry.name}`}
                      disabled={saving === target}
                      onClick={() => save(entry.name, target)}
                    >
                      {saving === target ? (
                        <Loader2 className="cfiles-spin" size={14} aria-hidden />
                      ) : (
                        <Download size={14} aria-hidden />
                      )}
                      <span className="srOnly">{t("files.downloadEntry", { name: entry.name })}</span>
                    </button>
                  )}
                </div>
              );
            })}
        </section>

        {paneOpen && (
          <aside className="cfiles-pane" data-automation-id="files-pane">
            <div className="cfiles-panehead">
              <span className="cfiles-glyph" aria-hidden>
                {film ? <Film size={16} /> : <FileText size={16} />}
              </span>
              <strong title={film?.path ?? preview?.path}>{film?.name ?? preview?.name}</strong>
              <button type="button" className="cfiles-icon" onClick={closePanes} title={t("files.closePreview")}>
                <X size={14} aria-hidden />
                <span className="srOnly">{t("files.closePreview")}</span>
              </button>
            </div>

            {film && (
              <>
                {film.status === "loading" && (
                  <p className="cfiles-note">
                    <Loader2 className="cfiles-spin" size={14} aria-hidden /> {t("files.filmLoading")}
                  </p>
                )}
                {film.status === "failed" && (
                  <p className="cfiles-fail">
                    <AlertTriangle size={15} aria-hidden /> {film.message}
                  </p>
                )}
                {film.status === "ready" && film.url && (
                  <video
                    className="cfiles-video"
                    src={film.url}
                    controls
                    autoPlay
                    playsInline
                    data-automation-id="film-player"
                    onError={() =>
                      setFilm((current) =>
                        current
                          ? {
                              ...current,
                              status: "failed",
                              message: t("files.filmFailed", { name: current.name }),
                            }
                          : current)}
                  />
                )}
                <div className="cfiles-paneact">
                  <button
                    type="button"
                    className="cfiles-btn"
                    disabled={saving === film.path}
                    onClick={() => save(film.name, film.path)}
                  >
                    <Download size={14} aria-hidden /> {t("files.downloadButton")}
                  </button>
                </div>
              </>
            )}

            {preview && (
              <>
                {preview.status === "loading" && (
                  <p className="cfiles-note">
                    <Loader2 className="cfiles-spin" size={14} aria-hidden /> {t("desks.loading")}
                  </p>
                )}
                {preview.status === "failed" && (
                  <p className="cfiles-fail">
                    <AlertTriangle size={15} aria-hidden /> {preview.message}
                  </p>
                )}
                {preview.status === "ready" && (
                  <>
                    {preview.file.binary ? (
                      <p className="cfiles-note">
                        {t("files.binaryNotice", { size: preview.file.size })}
                      </p>
                    ) : (
                      <>
                        {preview.file.truncated && <p className="cfiles-note">{t("files.truncatedNote")}</p>}
                        <pre className="cfiles-text">{preview.file.content}</pre>
                      </>
                    )}
                    <div className="cfiles-paneact">
                      <button
                        type="button"
                        className="cfiles-btn"
                        data-automation-id="preview-download"
                        disabled={saving === preview.path}
                        onClick={() => save(preview.name, preview.path)}
                      >
                        <Download size={14} aria-hidden /> {t("files.downloadButton")}
                      </button>
                      <span className="cfiles-panemeta">{formatSize(preview.file.size)}</span>
                    </div>
                  </>
                )}
              </>
            )}
          </aside>
        )}
      </div>
    </div>
  );
}

// Scoped to .cfiles so this window can carry the desktop's language — Fraunces
// heading, soft cards, quiet rows — without touching the shared stylesheet that
// the panel being replaced still depends on. The global button rule in
// styles.css paints every button indigo, so it is reset here first.
const CSS = `
.cfiles { color: var(--ink); font-size: 14px; line-height: 1.5; }
.cfiles *, .cfiles *::before, .cfiles *::after { box-sizing: border-box; }

.cfiles button {
  align-items: center; background: transparent; border: 1px solid transparent;
  border-radius: 10px; color: var(--ink); cursor: pointer; display: inline-flex;
  font: inherit; font-size: 12.5px; font-weight: 600; gap: 7px; letter-spacing: 0;
  min-height: 0; padding: 0; text-align: left;
  transition: background 140ms ease, border-color 140ms ease, color 140ms ease;
}
.cfiles button:hover { background: transparent; border-color: transparent; }
.cfiles button:active { transform: none; }
.cfiles button:disabled { background: transparent; border-color: transparent; color: var(--ink-soft); cursor: default; }
.cfiles button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }

.cfiles h2.cfiles-title {
  border: 0; color: var(--ink); font-family: Fraunces, "Iowan Old Style", Georgia, serif;
  font-size: 28px; font-weight: 600; letter-spacing: -0.02em; line-height: 1.12;
  margin: 0 0 6px; padding: 0; text-transform: none;
}
.cfiles .cfiles-lead { color: var(--ink-soft); font-size: 13px; margin: 0; max-width: 64ch; }

.cfiles .cfiles-places { display: flex; flex-wrap: wrap; gap: 8px; margin: 18px 0 14px; }
.cfiles .cfiles-place {
  background: var(--panel); border: 1px solid var(--hairline); border-radius: 999px;
  color: var(--ink-soft); font-size: 12px; padding: 7px 14px;
}
.cfiles .cfiles-place:hover { background: var(--paper); border-color: #cbd5e1; }
.cfiles .cfiles-place.on { background: var(--accent-soft); border-color: #c7d2fe; color: var(--accent-strong); }

.cfiles .cfiles-bar {
  align-items: center; display: flex; flex-wrap: wrap; gap: 10px;
  justify-content: space-between; margin-bottom: 12px;
}
.cfiles .cfiles-crumbs { align-items: center; display: flex; flex-wrap: wrap; gap: 1px; min-width: 0; }
.cfiles .cfiles-crumb {
  border-radius: 7px; color: var(--accent); font-size: 12.5px; font-weight: 600; padding: 3px 6px;
}
.cfiles .cfiles-crumb:hover { background: var(--accent-soft); }
.cfiles .cfiles-crumb[aria-current="page"] { color: var(--ink); }
.cfiles .cfiles-sep { align-items: center; color: #cbd5e1; display: inline-flex; }
.cfiles .cfiles-tools { display: flex; gap: 6px; }
.cfiles .cfiles-btn {
  background: var(--panel); border-color: var(--hairline); color: var(--ink); padding: 7px 12px;
}
.cfiles .cfiles-btn:hover { background: var(--paper); border-color: #cbd5e1; }
.cfiles .cfiles-btn:disabled { background: var(--paper); border-color: var(--hairline); }

.cfiles .cfiles-recnote {
  align-items: flex-start; background: #f7f7fd; border: 1px solid #e5e3f7; border-radius: 14px;
  color: var(--accent-strong); display: flex; gap: 11px; margin-bottom: 14px; padding: 13px 15px;
}
.cfiles .cfiles-recnote strong { color: var(--ink); display: block; font-size: 13px; }
.cfiles .cfiles-recnote p { color: var(--ink-soft); font-size: 12px; margin: 3px 0 0; max-width: 68ch; }

.cfiles .cfiles-fail {
  align-items: flex-start; color: #8a5a10; display: flex; font-size: 12.5px; gap: 9px;
  margin: 0 0 12px; line-height: 1.5;
}
.cfiles .cfiles-fail svg { flex: 0 0 auto; margin-top: 2px; }

.cfiles .cfiles-split { align-items: start; display: grid; gap: 16px; grid-template-columns: minmax(0, 1fr); }
@media (min-width: 940px) {
  .cfiles .cfiles-split.paired { grid-template-columns: minmax(0, 1fr) minmax(0, 1.05fr); }
}

.cfiles .cfiles-list {
  background: var(--panel); border: 1px solid var(--hairline); border-radius: 14px; overflow: hidden;
}
.cfiles .cfiles-row {
  align-items: center; border-bottom: 1px solid var(--hairline); display: flex; gap: 2px; padding-right: 8px;
}
.cfiles .cfiles-row:last-child { border-bottom: 0; }
.cfiles .cfiles-row:hover { background: var(--paper); }
.cfiles .cfiles-row.on { background: var(--accent-soft); }
.cfiles .cfiles-open {
  align-items: center; display: grid; flex: 1; font-size: 13.5px; font-weight: 500; gap: 12px;
  grid-template-columns: 20px minmax(0, 1fr) auto; min-width: 0; padding: 11px 14px;
}
.cfiles .cfiles-open:hover { background: transparent; }
.cfiles .cfiles-glyph { align-items: center; color: var(--accent); display: inline-flex; }
.cfiles .cfiles-glyph.film { color: var(--hold); }
.cfiles .cfiles-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.cfiles .cfiles-sub {
  color: var(--ink-soft); display: block; font-size: 11px; font-weight: 400; margin-top: 2px;
  overflow: hidden; text-overflow: ellipsis;
}
.cfiles .cfiles-meta {
  color: var(--ink-soft); font-size: 11.5px; font-weight: 400; justify-self: end; white-space: nowrap;
}
.cfiles .cfiles-icon { border-radius: 8px; color: var(--ink-soft); padding: 7px; }
.cfiles .cfiles-icon:hover { background: var(--panel); color: var(--accent); }

.cfiles .cfiles-state {
  align-items: center; color: var(--ink-soft); display: flex; flex-direction: column; font-size: 13px;
  gap: 8px; justify-content: center; margin: 0; min-height: 120px; padding: 26px 20px; text-align: center;
}
.cfiles .cfiles-state p { margin: 0; max-width: 46ch; }
.cfiles .cfiles-state.bad { color: #8a5a10; }

.cfiles .cfiles-pane {
  background: var(--panel); border: 1px solid var(--hairline); border-radius: 14px; overflow: hidden;
}
.cfiles .cfiles-panehead {
  align-items: center; border-bottom: 1px solid var(--hairline); display: flex; gap: 10px; padding: 11px 12px 11px 14px;
}
.cfiles .cfiles-panehead strong {
  flex: 1; font-size: 13px; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.cfiles .cfiles-note { color: var(--ink-soft); font-size: 12.5px; margin: 0; padding: 14px; }
.cfiles .cfiles-note svg { vertical-align: -2px; }
.cfiles .cfiles-pane .cfiles-fail { margin: 0; padding: 14px; }
.cfiles .cfiles-video { background: #0b1220; display: block; max-height: 420px; width: 100%; }
.cfiles .cfiles-text {
  background: #f8fafc; border-top: 1px solid var(--hairline); font-size: 12px; margin: 0;
  max-height: 380px; overflow: auto; padding: 14px; white-space: pre-wrap; word-break: break-word;
}
.cfiles .cfiles-paneact {
  align-items: center; border-top: 1px solid var(--hairline); display: flex; gap: 10px; padding: 11px 14px;
}
.cfiles .cfiles-panemeta { color: var(--ink-soft); font-size: 11.5px; margin-left: auto; }

.cfiles .cfiles-spin { animation: cfiles-spin 900ms linear infinite; }
@keyframes cfiles-spin { to { transform: rotate(360deg); } }
`;
