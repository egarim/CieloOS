import * as React from "react";
import { Download, FileText, Loader2, RefreshCw } from "lucide-react";
import {
  downloadShared,
  listShared,
  UnauthorizedError,
  type HomeEntry,
} from "../../shared/api";
import { useT } from "../../shared/i18n";
import { Button } from "../ui/button";

// What the agent made, and what you put there for it. One folder, both directions.
//
// This reads /api/shared, not /api/home/<owner>. The home volume has a "shared"
// directory too, and it is always empty — it is only a mount point. Listing that
// one shows nothing while the agent's work sits in a different volume entirely,
// which reads exactly like an agent that lied about finishing.

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function Files() {
  const t = useT();
  const [entries, setEntries] = React.useState<HomeEntry[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(true);

  const refresh = React.useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const listing = await listShared();
      setEntries(listing.entries);
    } catch (listError) {
      if (listError instanceof UnauthorizedError) {
        throw listError;
      }
      setError(t("portal.files.error"));
    } finally {
      setLoading(false);
    }
  }, [t]);

  React.useEffect(() => {
    void refresh();
  }, [refresh]);

  async function save(entry: HomeEntry) {
    setBusy(entry.name);
    setError(null);
    try {
      const { blob, filename } = await downloadShared(entry.name);
      // The browser is handed a blob rather than a link to the endpoint: the
      // download needs the panel header, and an <a href> cannot send one.
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = filename;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      // Revoked on the next tick, not immediately: Safari cancels a download
      // whose object URL disappears in the same frame as the click.
      window.setTimeout(() => URL.revokeObjectURL(url), 0);
    } catch (saveError) {
      setError(saveError instanceof UnauthorizedError ? t("portal.files.error") : t("portal.files.downloadError"));
    } finally {
      setBusy(null);
    }
  }

  return (
    <section>
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-2xl font-semibold">{t("portal.nav.files")}</h2>
        <Button variant="ghost" className="ml-auto" onClick={() => void refresh()} disabled={loading}>
          {loading ? (
            <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
          ) : (
            <RefreshCw className="h-4 w-4" aria-hidden="true" />
          )}
          {t("portal.files.refresh")}
        </Button>
      </div>

      <p className="mt-2 max-w-prose text-sm leading-6 text-slate-600 dark:text-slate-300">
        {t("portal.files.lead")}
      </p>

      {error ? (
        <p role="alert" className="mt-4 text-sm text-red-600 dark:text-red-400">
          {error}
        </p>
      ) : null}

      {entries !== null && entries.length === 0 && !loading ? (
        <p className="mt-6 max-w-prose text-sm leading-6 text-slate-600 dark:text-slate-300">
          {t("portal.files.empty")}
        </p>
      ) : null}

      <ul className="mt-4 flex flex-col gap-2">
        {(entries ?? []).map((entry) => (
          <li
            key={entry.name}
            className="flex min-h-14 flex-wrap items-center gap-3 rounded-lg border border-slate-200 bg-white p-3 dark:border-slate-800 dark:bg-slate-900"
          >
            <FileText className="h-5 w-5 shrink-0 text-slate-500" aria-hidden="true" />
            <span className="min-w-0 break-all font-medium">{entry.name}</span>
            <span className="text-sm text-slate-500 dark:text-slate-400">
              {entry.kind === "directory" ? t("portal.files.folder") : formatSize(entry.size)}
            </span>
            {entry.kind === "file" ? (
              <Button
                variant="ghost"
                className="ml-auto"
                onClick={() => void save(entry)}
                disabled={busy === entry.name}
              >
                {busy === entry.name ? (
                  <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
                ) : (
                  <Download className="h-4 w-4" aria-hidden="true" />
                )}
                {t("portal.files.save")}
              </Button>
            ) : null}
          </li>
        ))}
      </ul>
    </section>
  );
}
