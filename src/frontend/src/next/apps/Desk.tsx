import * as React from "react";
import {
  ExternalLink,
  Eye,
  KeyRound,
  Loader2,
  Monitor,
  Package,
  Plus,
  RefreshCw,
  Send,
  Terminal,
  TriangleAlert,
  X,
} from "lucide-react";
import {
  api,
  command,
  UnauthorizedError,
  type ApprovalRecord,
  type SessionView,
  type Whoami,
} from "../../shared/api";
import { readable, serverText } from "../../shared/plain";
import { useT } from "../../shared/i18n";

// THE DESK — the app that opens the real Linux screen.
//
// Two kinds of screen live here and they are not the same thing:
//   a desktop  — a graphical seat, reachable only by opening its own browser tab;
//   a terminal — text, which this panel can both READ (the live screen) and WRITE
//                to (a line the person types), because that is the screen Cielo
//                works on and watching it without being able to intervene is a
//                television, not a desk.
//
// Everything that changes a screen rides the command bus, so a refusal or a
// stop-and-ask is a normal answer here, never an exception: closing a screen
// always stops and asks, and the shell owns that dialog. This file must never
// grow one of its own.

export type DeskProps = {
  whoami: Whoami | null;
  sessions: SessionView[];
  reload: () => void;
  onApproval: (approval: ApprovalRecord) => void;
  notify: (message: string) => void;
};

// The live screen of a terminal session, as the runtime reports it. `available`
// false is a readable failure with a `detail`, not an error — a session that is
// still coming up says so rather than throwing.
type ConsoleView = { sessionId: string; screen: string; available: boolean; detail?: string | null };

// What a screen comes installed with. `imageReady` is the only thing that
// decides whether a screen can open on it; `buildStatus` is the running job.
type DeskSetup = {
  id: string;
  label: string;
  description: string;
  isDefault: boolean;
  imageReady: boolean;
  buildStatus: string;
};

// A list that shells out to the container backend goes stale the moment a screen
// is asked for: `create` returns before the container is up, so without a poll
// the row would sit at "coming up" until the person reloaded the whole panel.
const SESSION_POLL_MS = 5_000;
// The terminal is a live screen. Slower than this and typing feels posted rather
// than typed; faster and it is a lot of container exec for no more truth.
const SCREEN_POLL_MS = 2_500;
// A build takes minutes. One refresh after starting it would leave the panel
// saying "building" forever, and the button visible, inviting a second build of
// something already built.
const BUILD_POLL_MS = 5_000;

// The runtime answers failures as JSON `{ error }`; anything else is text. Both
// have to reach the person intact — "Error" tells nobody what broke. What must
// NOT reach them is the runtime explaining itself in its own vocabulary, which
// some of these answers do ("No executor is registered for surface 'session'."),
// so the text passes the machine-words filter on its way out.
const explain = serverText;

export default function Desk({ whoami, sessions, reload, onApproval, notify }: DeskProps) {
  const t = useT();

  // The shell's copy is the starting point; this view keeps its own because it
  // polls, and because a failure to LIST screens has to be visible here — the
  // shell holding an older successful answer would otherwise hide it.
  const [live, setLive] = React.useState<SessionView[] | null>(null);
  const [listError, setListError] = React.useState<string | null>(null);
  const [listLoading, setListLoading] = React.useState(true);

  const [owner, setOwner] = React.useState<string>("");
  const [kind, setKind] = React.useState<"desktop" | "console">("desktop");
  const [openBusy, setOpenBusy] = React.useState(false);
  const [openError, setOpenError] = React.useState<string | null>(null);

  const [busyId, setBusyId] = React.useState<string | null>(null);
  const [rowError, setRowError] = React.useState<{ id: string; text: string } | null>(null);
  // A tab opened from an async continuation can be held back by the browser, and
  // a seat taken with nothing to show for it is worse than no seat: keep the
  // address so the person can open it themselves.
  const [heldBack, setHeldBack] = React.useState<{ id: string; url: string } | null>(null);

  const [selected, setSelected] = React.useState<string | null>(null);
  const [screen, setScreen] = React.useState<ConsoleView | null>(null);
  const [screenError, setScreenError] = React.useState<string | null>(null);
  const [screenLoading, setScreenLoading] = React.useState(false);
  const [draft, setDraft] = React.useState("");
  const [typing, setTyping] = React.useState(false);
  const [typeError, setTypeError] = React.useState<string | null>(null);

  const [setups, setSetups] = React.useState<DeskSetup[] | null>(null);
  const [setupsError, setSetupsError] = React.useState<string | null>(null);
  const [buildingId, setBuildingId] = React.useState<string | null>(null);
  const [setupError, setSetupError] = React.useState<string | null>(null);

  const rows = live ?? sessions;

  // What the runtime said, when it said it in words a person can read. A refusal
  // and a backend that would not carry it out both come back as free text, and
  // some of that text is written for whoever is reading the source rather than
  // for the person at the screen. Those are withheld and this line stands in —
  // the alternative is the desktop repeating the machine's vocabulary back at
  // someone who has never seen it.
  const said = React.useCallback(
    (text: string) => readable(text, t("errors.wordedForTheMachine")),
    [t],
  );

  // A rejected token is the shell's business, not this app's: say so in the
  // person's language and ask the shell to re-check, which is what signs out.
  // Once only — three pollers all discovering the same dead cookie must not turn
  // into three reloads a second while the shell is already tearing the app down.
  const toldTheShell = React.useRef(false);
  const failure = React.useCallback(
    (error: unknown): string => {
      if (error instanceof UnauthorizedError) {
        if (!toldTheShell.current) {
          toldTheShell.current = true;
          reload();
        }
        return t("errors.sessionTokenRejected");
      }
      return said(explain(error));
    },
    [reload, said],
  );

  // ---------------------------------------------------------------- sessions

  const refreshSessions = React.useCallback(async () => {
    try {
      setLive(await api<SessionView[]>("/api/sessions"));
      setListError(null);
    } catch (error) {
      setListError(failure(error));
    } finally {
      setListLoading(false);
    }
  }, [failure]);

  React.useEffect(() => {
    refreshSessions();
    const timer = window.setInterval(refreshSessions, SESSION_POLL_MS);
    return () => window.clearInterval(timer);
  }, [refreshSessions]);

  // Default to the person's own desk; if this panel is only ever an agent's,
  // fall back to whatever it can reach rather than an empty picker.
  React.useEffect(() => {
    if (!whoami) return;
    setOwner((current) => (current && whoami.homes.includes(current) ? current : whoami.slug ?? whoami.homes[0] ?? ""));
  }, [whoami]);

  // A person gets a desktop; Cielo gets a terminal. Both remain reachable for
  // either — this only picks the one that is almost always wanted.
  React.useEffect(() => {
    if (!owner || !whoami) return;
    setKind(owner === whoami.slug ? "desktop" : "console");
  }, [owner, whoami]);

  const isMine = (slug: string) => Boolean(whoami?.homes.includes(slug));
  // The agent is Cielo. Its account name only earns a place on screen when there
  // is more than one of them and the name alone would be ambiguous.
  const manyAgents = (whoami?.homes ?? []).filter((slug) => slug !== whoami?.slug).length > 1;
  const ownerLabel = (slug: string) =>
    whoami && slug === whoami.slug
      ? whoami.display || slug
      : manyAgents
        ? `${t("desk.openCielo")} · ${slug}`
        : t("desk.openCielo");

  // Dev topology: the screen is forwarded to the same host this panel is served
  // from. The runtime-proxied path (/api/sessions/{id}/view) replaces this the
  // moment it exists, and then this is the only line that changes.
  const screenUrl = (session: SessionView) =>
    `http://${window.location.hostname}:${session.viewportPort}/`;

  const profileFor = (who: string, of: "desktop" | "console") =>
    `${whoami && who === whoami.slug ? "human" : "agent"}-${of}`;

  async function openScreen() {
    if (!owner || openBusy) return;
    setOpenBusy(true);
    setOpenError(null);
    try {
      const result = await command("session", "create", { owner, profile: profileFor(owner, kind) });
      if (result.decision === "RequireApproval" && result.approval) {
        onApproval(result.approval);
      } else if (result.decision === "Deny") {
        setOpenError(said(result.reason));
      } else if (result.execution && !result.execution.executed) {
        // Allowed, and it still did not happen — the container backend refused.
        // Saying "opened" here would be a lie the person discovers later.
        setOpenError(said(result.execution.message));
      } else {
        notify(t("desk.opened", { who: ownerLabel(owner) }));
      }
      await refreshSessions();
      reload();
    } catch (error) {
      setOpenError(failure(error));
    } finally {
      setOpenBusy(false);
    }
  }

  // Taking a seat at a screen Cielo owns is its own recorded act, not a link:
  // it writes down who sat where, in whose name, before the tab opens.
  async function takeSeat(session: SessionView, mode: "shadow" | "become") {
    setBusyId(session.id);
    setRowError(null);
    setHeldBack(null);
    try {
      const result = await command("session", "inhabit", { id: session.id, mode });
      if (result.decision === "RequireApproval" && result.approval) {
        onApproval(result.approval);
        return;
      }
      if (result.decision === "Deny") {
        setRowError({ id: session.id, text: said(result.reason) });
        return;
      }
      if (result.execution && !result.execution.executed) {
        setRowError({ id: session.id, text: said(result.execution.message) });
        return;
      }
      const url = screenUrl(session);
      window.open(url, "_blank", "noopener");
      setHeldBack({ id: session.id, url });
      reload();
    } catch (error) {
      setRowError({ id: session.id, text: failure(error) });
    } finally {
      setBusyId(null);
    }
  }

  // Closing throws away everything running on the screen, so the machine stops
  // and asks. That answer is handed straight to the shell — the dialog belongs
  // to it, and there is exactly one of them.
  async function closeScreen(session: SessionView) {
    setBusyId(session.id);
    setRowError(null);
    try {
      const result = await command("session", "destroy", { id: session.id });
      if (result.decision === "RequireApproval" && result.approval) {
        onApproval(result.approval);
      } else if (result.decision === "Deny") {
        setRowError({ id: session.id, text: said(result.reason) });
      } else if (result.execution && !result.execution.executed) {
        setRowError({ id: session.id, text: said(result.execution.message) });
      } else {
        notify(t("desk.closed", { id: session.id }));
      }
      await refreshSessions();
      reload();
    } catch (error) {
      setRowError({ id: session.id, text: failure(error) });
    } finally {
      setBusyId(null);
    }
  }

  // ------------------------------------------------------------- the screen

  const terminals = rows.filter((session) => session.kind === "console");
  const current = rows.find((session) => session.id === selected) ?? null;

  // Follow a running terminal by default. A chosen one is kept while it is still
  // running; once it stops, move to whichever terminal is live rather than
  // polling a name the runtime has forgotten.
  React.useEffect(() => {
    const running = terminals.filter((session) => session.status === "running");
    const chosen = terminals.find((session) => session.id === selected);
    if (chosen && (chosen.status === "running" || running.length === 0)) return;
    setSelected(running[0]?.id ?? chosen?.id ?? null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows.map((session) => `${session.id}:${session.kind}:${session.status}`).join(","), selected]);

  React.useEffect(() => {
    setScreen(null);
    setScreenError(null);
    setTypeError(null);
    if (!current || current.kind !== "console" || current.status !== "running") return;

    let alive = true;
    const read = async () => {
      try {
        const view = await api<ConsoleView>(`/api/sessions/${encodeURIComponent(current.id)}/console`);
        if (!alive) return;
        setScreen(view);
        setScreenError(null);
      } catch (error) {
        if (!alive) return;
        setScreenError(failure(error));
      } finally {
        if (alive) setScreenLoading(false);
      }
    };

    setScreenLoading(true);
    read();
    const timer = window.setInterval(read, SCREEN_POLL_MS);
    return () => {
      alive = false;
      window.clearInterval(timer);
    };
  }, [current?.id, current?.status, current?.kind, failure]);

  async function sendLine() {
    // The runtime rejects an empty line as a missing input, so an empty box is
    // not a way to press Return. Better to disable the button than to send
    // something that comes back as a validation failure.
    const text = draft.trim() ? draft : "";
    if (!current || typing || !text || !screen?.available) return;
    setTyping(true);
    setTypeError(null);
    try {
      const result = await command("console", "type", { id: current.id, text, submit: "true" });
      if (result.decision === "RequireApproval" && result.approval) {
        onApproval(result.approval);
      } else if (result.decision === "Deny") {
        setTypeError(said(result.reason));
      } else if (result.execution && !result.execution.executed) {
        setTypeError(said(result.execution.message));
      } else {
        setDraft("");
        // The reply carries the screen after the keystrokes, but glued to a
        // detail line; re-reading is one round trip and unambiguous.
        try {
          setScreen(await api<ConsoleView>(`/api/sessions/${encodeURIComponent(current.id)}/console`));
        } catch {
          // The poll above says the same thing 2.5 seconds later.
        }
      }
    } catch (error) {
      setTypeError(failure(error));
    } finally {
      setTyping(false);
    }
  }

  // ------------------------------------------------------------ desk setups

  const readSetups = React.useCallback(async () => {
    try {
      setSetups(await api<DeskSetup[]>("/api/desk-profiles"));
      setSetupsError(null);
    } catch (error) {
      setSetupsError(failure(error));
    }
  }, [failure]);

  React.useEffect(() => {
    readSetups();
  }, [readSetups]);

  const building = (setups ?? []).filter((setup) => setup.buildStatus === "building");

  React.useEffect(() => {
    if (building.length === 0) return;
    const timer = window.setInterval(readSetups, BUILD_POLL_MS);
    return () => window.clearInterval(timer);
  }, [building.map((setup) => setup.id).join(","), readSetups]);

  async function build(setup: DeskSetup) {
    setBuildingId(setup.id);
    setSetupError(null);
    try {
      await api<{ id: string; status: string }>(`/api/desk-profiles/${encodeURIComponent(setup.id)}/build`, {
        method: "POST",
      });
      notify(t("desk.setupBuildStarted", { label: setup.label }));
      await readSetups();
    } catch (error) {
      setSetupError(failure(error));
    } finally {
      setBuildingId(null);
    }
  }

  // The person's own desk cannot open a screen until its setup is built. Saying
  // so before they press Open is the difference between a wait and a mystery.
  const mySetup = (setups ?? []).find((setup) => setup.id === whoami?.deskProfile);
  const mySetupMissing = Boolean(mySetup && !mySetup.imageReady && owner === whoami?.slug);

  // ---------------------------------------------------------------- render

  const statusText = (session: SessionView) => {
    if (session.status === "running") return t("desk.statusRunning");
    if (session.status === "exited") return t("desk.statusStopped");
    return session.status;
  };

  return (
    <div className="dk">
      <style>{CSS}</style>

      <header>
        <h2>{t("desk.title")}</h2>
        <p className="dk-lead">{t("desk.lead")}</p>
      </header>

      <section className="dk-section" data-automation-id="desktops">
        <div className="dk-sectionHead">
          <h3>{t("desk.openHeading")}</h3>
          <button className="dk-btn dk-quiet" onClick={refreshSessions} disabled={openBusy}>
            <RefreshCw size={13} /> {t("desk.refresh")}
          </button>
        </div>

        <div className="dk-open">
          <label className="dk-field">
            <span>{t("desk.openFor")}</span>
            <select value={owner} onChange={(event) => setOwner(event.target.value)} disabled={!whoami || openBusy}>
              {(whoami?.homes ?? []).map((slug) => (
                <option key={slug} value={slug}>
                  {slug === whoami?.slug ? `${whoami.display || slug} (${t("desk.openYou")})` : ownerLabel(slug)}
                </option>
              ))}
            </select>
          </label>

          <label className="dk-field">
            <span>{t("desk.openKind")}</span>
            <select
              data-automation-id="desktop-profile"
              value={profileFor(owner, kind)}
              onChange={(event) => setKind(event.target.value.endsWith("console") ? "console" : "desktop")}
              disabled={openBusy}
            >
              <option value={profileFor(owner, "desktop")}>{t("desk.kindDesktop")}</option>
              <option value={profileFor(owner, "console")}>{t("desk.kindTerminal")}</option>
            </select>
          </label>

          <button
            className="dk-btn dk-primary"
            data-automation-id="desktop-create"
            onClick={openScreen}
            disabled={!owner || openBusy}
          >
            {openBusy ? <Loader2 size={14} className="dk-spin" /> : <Plus size={14} />}
            {openBusy ? t("desk.opening") : t("desk.openButton")}
          </button>
        </div>

        <p className="dk-note">{kind === "desktop" ? t("desk.kindDesktopHint") : t("desk.kindTerminalHint")}</p>

        {mySetupMissing && mySetup && (
          <p className="dk-warn">
            <TriangleAlert size={14} /> {t("desk.setupNotReady", { label: mySetup.label })}
          </p>
        )}
        {openError && (
          <p className="dk-error" role="alert">
            {t("desk.openFailed", { reason: openError })}
          </p>
        )}

        {listError ? (
          <p className="dk-error" role="alert">
            {t("desk.loadFailed", { reason: listError })}
          </p>
        ) : listLoading && rows.length === 0 ? (
          <p className="dk-empty">
            <Loader2 size={14} className="dk-spin" /> {t("desk.loading")}
          </p>
        ) : rows.length === 0 ? (
          <p className="dk-empty">{t("desk.empty")}</p>
        ) : (
          <div className="dk-list">
            {rows.map((session) => {
              const running = session.status === "running";
              const reachable = running && session.viewportPort > 0;
              const busy = busyId === session.id;
              return (
                <div
                  className={`dk-row${selected === session.id ? " dk-rowOn" : ""}`}
                  key={session.id}
                  data-automation-id={`desktop-${session.id}`}
                >
                  <span className={`dk-glyph${session.kind === "console" ? " dk-glyphTerm" : ""}`}>
                    {session.kind === "console" ? <Terminal size={16} /> : <Monitor size={16} />}
                  </span>

                  <div className="dk-rowMain">
                    <div className="dk-rowTop">
                      <strong>{session.kind === "console" ? t("desk.rowTerminal") : t("desk.rowDesktop")}</strong>
                      <span className="dk-tag">{ownerLabel(session.owner)}</span>
                      <span className={running ? "dk-live" : "dk-off"}>
                        <i /> {statusText(session)}
                      </span>
                    </div>
                    <code className="dk-id">{session.id}</code>
                    {rowError?.id === session.id && (
                      <p className="dk-error" role="alert">
                        {t("desk.actionFailed", { reason: rowError.text })}
                      </p>
                    )}
                    {heldBack?.id === session.id && (
                      <p className="dk-note">
                        {t("desk.blockedPopup")}{" "}
                        <a href={heldBack.url} target="_blank" rel="noopener noreferrer">
                          {heldBack.url}
                        </a>
                      </p>
                    )}
                  </div>

                  <div className="dk-actions">
                    {session.kind === "console" && running && (
                      <button
                        className={`dk-btn${selected === session.id ? " dk-primary" : ""}`}
                        onClick={() => setSelected(session.id)}
                      >
                        <Eye size={14} /> {t("desk.showScreen")}
                      </button>
                    )}
                    {isMine(session.owner) ? (
                      <>
                        <button
                          className="dk-btn"
                          data-automation-id={`shadow-${session.id}`}
                          disabled={!reachable || busy}
                          onClick={() => takeSeat(session, "shadow")}
                        >
                          <Eye size={14} /> {t("desk.watchOver")}
                        </button>
                        <button
                          className="dk-btn"
                          data-automation-id={`become-${session.id}`}
                          disabled={!reachable || busy}
                          onClick={() => takeSeat(session, "become")}
                        >
                          <KeyRound size={14} /> {t("desk.takeOver")}
                        </button>
                      </>
                    ) : (
                      <a
                        className={`dk-btn${reachable ? "" : " dk-disabled"}`}
                        href={reachable ? screenUrl(session) : undefined}
                        target="_blank"
                        rel="noopener noreferrer"
                      >
                        <ExternalLink size={14} /> {t("desk.openScreen")}
                      </a>
                    )}
                    <button
                      className="dk-btn dk-close"
                      data-automation-id={`destroy-${session.id}`}
                      disabled={busy}
                      onClick={() => closeScreen(session)}
                    >
                      {busy ? <Loader2 size={14} className="dk-spin" /> : <X size={14} />}
                      {busy ? t("desk.closing") : t("desk.close")}
                    </button>
                  </div>
                </div>
              );
            })}
          </div>
        )}

        {rows.length > 0 && (
          <>
            <p className="dk-note">{t("desk.seatHint")}</p>
            <p className="dk-note">{t("desk.closeHint")}</p>
          </>
        )}
      </section>

      <section className="dk-section" data-automation-id="console">
        <div className="dk-sectionHead">
          <h3>{t("desk.screenHeading")}</h3>
          {terminals.length > 1 && (
            <select
              className="dk-picker"
              value={selected ?? ""}
              onChange={(event) => setSelected(event.target.value || null)}
            >
              {terminals.map((session) => (
                <option key={session.id} value={session.id}>
                  {ownerLabel(session.owner)} — {session.id}
                </option>
              ))}
            </select>
          )}
        </div>
        <p className="dk-note">{t("desk.screenLead")}</p>

        {!current || current.kind !== "console" ? (
          <p className="dk-empty">{terminals.length === 0 ? t("desk.screenNone") : t("desk.screenPick")}</p>
        ) : current.status !== "running" ? (
          <p className="dk-empty">{t("desk.screenNotRunning")}</p>
        ) : screenError ? (
          <p className="dk-error" role="alert">
            {t("desk.screenUnreadable", { reason: screenError })}
          </p>
        ) : screenLoading && !screen ? (
          <p className="dk-empty">
            <Loader2 size={14} className="dk-spin" /> {t("desk.screenLoading")}
          </p>
        ) : screen && !screen.available ? (
          <p className="dk-error" role="alert">
            {t("desk.screenUnreadable", { reason: screen.detail || t("console.unavailable") })}
          </p>
        ) : (
          <>
            <pre className="dk-screen" data-automation-id="console-screen">
              {screen?.screen?.trimEnd() || t("console.screenEmpty")}
            </pre>
            <div className="dk-type">
              <input
                value={draft}
                placeholder={t("desk.typePlaceholder")}
                disabled={typing}
                maxLength={4096}
                onChange={(event) => setDraft(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === "Enter") sendLine();
                }}
              />
              <button className="dk-btn dk-primary" onClick={sendLine} disabled={typing || !draft.trim()}>
                {typing ? <Loader2 size={14} className="dk-spin" /> : <Send size={14} />}
                {typing ? t("desk.typeSending") : t("desk.typeSend")}
              </button>
            </div>
            {typeError && (
              <p className="dk-error" role="alert">
                {t("desk.typeFailed", { reason: typeError })}
              </p>
            )}
            <p className="dk-note">{t("desk.typeHint")}</p>
          </>
        )}
      </section>

      <section className="dk-section">
        <div className="dk-sectionHead">
          <h3>{t("desk.setupsHeading")}</h3>
        </div>
        <p className="dk-note">{t("desk.setupsLead")}</p>

        {building.length > 0 && (
          <div className="dk-job">
            <Loader2 size={15} className="dk-spin" />
            <div>
              {building.map((setup) => (
                <p key={setup.id}>{t("desk.setupJob", { label: setup.label })}</p>
              ))}
              <span className="dk-bar">
                <i />
              </span>
            </div>
          </div>
        )}

        {setupError && (
          <p className="dk-error" role="alert">
            {t("desk.setupBuildFailedToStart", { reason: setupError })}
          </p>
        )}

        {setupsError ? (
          <p className="dk-error" role="alert">
            {t("desk.setupsFailed", { reason: setupsError })}
          </p>
        ) : setups === null ? (
          <p className="dk-empty">
            <Loader2 size={14} className="dk-spin" /> {t("desk.setupsLoading")}
          </p>
        ) : setups.length === 0 ? (
          <p className="dk-empty">{t("desk.setupsEmpty")}</p>
        ) : (
          <div className="dk-list">
            {setups.map((setup) => {
              const failed = setup.buildStatus.startsWith("failed");
              return (
                <div className="dk-row" key={setup.id}>
                  <span className="dk-glyph dk-glyphSetup">
                    <Package size={16} />
                  </span>
                  <div className="dk-rowMain">
                    <div className="dk-rowTop">
                      <strong>{setup.label}</strong>
                      {setup.isDefault && <span className="dk-tag">{t("desk.setupDefault")}</span>}
                      {setup.imageReady ? (
                        <span className="dk-live">
                          <i /> {t("desk.setupReady")}
                        </span>
                      ) : setup.buildStatus === "building" ? (
                        <span className="dk-hold">
                          <i /> {t("desk.setupBuilding")}
                        </span>
                      ) : (
                        <span className="dk-off">
                          <i /> {t("desk.setupMissing")}
                        </span>
                      )}
                    </div>
                    <p className="dk-desc">{setup.description}</p>
                    {failed && (
                      <p className="dk-error">
                        {t("desk.setupBuildFailed")} <code>{setup.buildStatus}</code>
                      </p>
                    )}
                  </div>
                  <div className="dk-actions">
                    {!setup.imageReady && setup.buildStatus !== "building" && (
                      <button className="dk-btn" disabled={buildingId === setup.id} onClick={() => build(setup)}>
                        {buildingId === setup.id ? <Loader2 size={14} className="dk-spin" /> : <Package size={14} />}
                        {t("desk.setupBuild")}
                      </button>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </section>
    </div>
  );
}

// Scoped to this app, and written against the desktop mockup's language rather
// than the panel it replaces: Fraunces headings, hairline cards, one indigo
// action per row. It borrows the shared tokens where they exist and carries the
// mockup's values as fallbacks so the app still looks right on its own.
const CSS = `
.dk{
  --dk-ink:var(--ink,#0f172a); --dk-soft:var(--ink-soft,#64748b); --dk-line:var(--hairline,#e2e8f0);
  --dk-accent:var(--accent,#4f46e5); --dk-allow:var(--allow,#059669); --dk-hold:var(--hold,#b45309);
  --dk-deny:var(--deny,#dc2626); --dk-panel:var(--panel,#ffffff);
  display:grid;gap:26px;color:var(--dk-ink);font-size:13px;line-height:1.55;
}
/* The panel this app lands in dresses every bare h2 as a small uppercase rule.
   That is the old chrome, not this one: undo it rather than fight it. */
.dk h2{font:600 28px/1.15 Fraunces,"Iowan Old Style",Georgia,serif;letter-spacing:-.02em;
  text-transform:none;color:var(--dk-ink);border:0;padding:0;margin:0 0 6px}
.dk h3{font-size:14px;font-weight:700;margin:0;text-transform:none;letter-spacing:0;color:var(--dk-ink)}
.dk-lead{color:var(--dk-soft);font-size:13px;line-height:1.55;margin:0;max-width:62ch}
.dk-section{display:grid;gap:12px}
.dk-sectionHead{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap}
.dk-note{color:var(--dk-soft);font-size:11.5px;line-height:1.5;margin:0;max-width:70ch}
.dk-note a{color:var(--dk-accent);word-break:break-all}
.dk-empty{color:var(--dk-soft);font-size:12px;margin:0;padding:16px 14px;border:1px dashed var(--dk-line);
  border-radius:14px;display:flex;align-items:center;gap:8px;line-height:1.55}
.dk-error{color:var(--dk-deny);font-size:11.5px;line-height:1.5;margin:4px 0 0;overflow-wrap:anywhere}
.dk-error code{background:rgba(220,38,38,.07);border:1px solid rgba(220,38,38,.18);color:inherit;
  border-radius:6px;padding:1px 5px}
.dk-warn{display:flex;align-items:flex-start;gap:8px;margin:0;padding:10px 12px;border-radius:12px;
  background:#fdf6ec;border:1px solid #f2dfc2;color:#8a5a10;font-size:11.5px;line-height:1.5}
.dk-warn svg{flex:0 0 auto;margin-top:1px}

.dk-open{display:flex;align-items:flex-end;gap:10px;flex-wrap:wrap}
.dk-field{display:grid;gap:4px}
.dk-field span{font-size:9.5px;letter-spacing:.13em;text-transform:uppercase;color:var(--dk-soft);font-weight:700}
.dk-field select,.dk-picker{border:1px solid var(--dk-line);border-radius:10px;height:38px;padding:0 10px;
  background:var(--dk-panel);color:var(--dk-ink);font:inherit;font-size:12px;min-width:170px}

.dk-btn{display:inline-flex;align-items:center;gap:6px;border:1px solid #d7dce6;background:var(--dk-panel);
  color:var(--dk-ink);border-radius:10px;min-height:34px;padding:0 11px;font:inherit;font-size:11.5px;
  font-weight:600;letter-spacing:0;cursor:pointer;text-decoration:none;white-space:nowrap}
.dk-btn:hover:not(:disabled){background:#f6f7fa;border-color:#cbd5e1}
.dk-btn:disabled,.dk-btn.dk-disabled{opacity:.45;cursor:not-allowed;pointer-events:none}
.dk-primary{background:var(--dk-accent);border-color:var(--dk-accent);color:#fff}
.dk-primary:hover:not(:disabled){background:var(--accent-strong,#4338ca);border-color:var(--accent-strong,#4338ca)}
.dk-quiet{border-color:transparent;color:var(--dk-soft);padding:6px 8px}
.dk-close{color:var(--dk-deny)}
.dk-close:hover:not(:disabled){background:#fef2f2;border-color:#fbd5d5}
.dk-spin{animation:dk-turn 1s linear infinite}
@keyframes dk-turn{to{transform:rotate(360deg)}}

.dk-list{border:1px solid var(--dk-line);border-radius:14px;overflow:hidden;background:var(--dk-panel)}
/* Flex rather than a fixed grid: four actions and a long name do not fit side by
   side in a window this app does not control the width of, and a row that squeezes
   its own id into a three-line column is worse than one that wraps. */
.dk-row{display:flex;flex-wrap:wrap;align-items:flex-start;gap:12px;
  padding:13px 14px;border-bottom:1px solid var(--dk-line)}
.dk-row:last-child{border-bottom:0}
.dk-rowOn{background:#f8faff}
.dk-glyph{flex:0 0 auto;width:34px;height:34px;border-radius:11px;display:grid;place-items:center;
  background:#eef2ff;color:var(--dk-accent)}
.dk-glyphTerm{background:#0f172a;color:#e2e8f0}
.dk-glyphSetup{background:#f1f5f9;color:var(--dk-soft)}
.dk-rowMain{flex:1 1 240px;min-width:0}
.dk-rowTop{display:flex;align-items:center;gap:9px;flex-wrap:wrap}
.dk-rowTop strong{font-size:12.5px}
.dk-tag{font-size:10.5px;color:var(--dk-soft);background:#f1f5f9;border-radius:999px;padding:2px 8px;font-weight:600}
.dk-id{display:block;margin-top:4px;background:none;border:0;padding:0;border-radius:0;
  font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:11px;color:var(--dk-soft);
  overflow-wrap:anywhere}
.dk-desc{margin:5px 0 0;color:var(--dk-soft);font-size:11.5px;line-height:1.5}
.dk-live,.dk-off,.dk-hold{display:inline-flex;align-items:center;gap:6px;font-size:11px;font-weight:600}
.dk-live{color:var(--dk-allow)}
.dk-off{color:var(--dk-soft)}
.dk-hold{color:var(--dk-hold)}
.dk-live i,.dk-off i,.dk-hold i{width:6px;height:6px;border-radius:50%;background:currentColor}
.dk-actions{display:flex;gap:7px;flex-wrap:wrap;justify-content:flex-end;align-items:center;
  flex:0 1 auto;margin-left:auto}

.dk-screen{margin:0;border-radius:14px;background:#0f172a;color:#cbd5e1;padding:16px 18px;min-height:190px;
  max-height:420px;overflow:auto;font:12px/1.6 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
  white-space:pre;tab-size:8}
.dk-type{display:flex;gap:8px;margin-top:10px}
.dk-type input{flex:1 1 auto;width:100%;min-width:0;height:38px;border:1px solid var(--dk-line);
  border-radius:10px;padding:0 12px;font:inherit;font-size:12px;background:var(--dk-panel);color:var(--dk-ink)}
.dk-type input:focus{outline:2px solid var(--dk-accent);outline-offset:-1px}

.dk-job{display:flex;gap:11px;align-items:flex-start;padding:12px 14px;border-radius:14px;
  background:#f6f7fd;border:1px solid #e6e8f7;color:var(--dk-ink)}
.dk-job p{margin:0 0 2px;font-size:12px}
.dk-job svg{color:var(--dk-accent);margin-top:1px;flex:0 0 auto}
.dk-bar{display:block;height:4px;background:#e2e8f0;border-radius:4px;margin-top:9px;overflow:hidden}
.dk-bar i{display:block;width:40%;height:100%;background:var(--dk-accent);border-radius:4px;
  animation:dk-slide 1.7s ease-in-out infinite}
@keyframes dk-slide{0%{margin-left:-40%}100%{margin-left:100%}}

@media (max-width:720px){
  .dk-actions{justify-content:flex-start;margin-left:46px}
  .dk-open{flex-direction:column;align-items:stretch}
  .dk-field select,.dk-picker{width:100%}
  .dk-btn{flex:1 1 auto;justify-content:center}
}
`;
