import * as React from "react";
import {
  ApiError,
  listApprovals,
  resolveApproval,
  type ApprovalView,
} from "../shared/api";
import { useT } from "../shared/i18n";
import { Button } from "./ui/button";

function lowerFirst(value: string): string {
  if (!value) return value;
  return value.charAt(0).toLocaleLowerCase() + value.slice(1);
}

function sortedPending(items: ApprovalView[]): ApprovalView[] {
  return items
    .filter((item) => item.status === "Pending")
    .sort((left, right) => left.createdAt.localeCompare(right.createdAt));
}

function hostOf(value: string): string {
  try {
    return new URL(value).host;
  } catch {
    return value;
  }
}

function SpecificThing({ approval }: { approval: ApprovalView }) {
  const t = useT();
  const args = approval.pendingRequest?.arguments ?? {};
  const url = args.url?.trim();

  if (url) {
    return (
      <div className="rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-700 dark:bg-slate-950">
        <p className="text-xs font-medium uppercase tracking-wide text-slate-500 dark:text-slate-400">
          {t("portal.permission.host")}
        </p>
        <p className="break-all text-2xl font-semibold leading-tight text-slate-950 dark:text-slate-50">
          {hostOf(url)}
        </p>
        <p className="mt-2 break-all text-sm text-slate-600 dark:text-slate-300">
          {t("portal.permission.fullUrl")}: {url}
        </p>
      </div>
    );
  }

  if (approval.pendingRequest?.operation === "set-cell" && args.address) {
    return (
      <div className="rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-700 dark:bg-slate-950">
        <p className="break-all text-2xl font-semibold leading-tight text-slate-950 dark:text-slate-50">
          {t("portal.permission.cellLabel", { address: args.address })}
        </p>
        <p className="mt-2 break-all text-sm text-slate-600 dark:text-slate-300">
          {t("portal.permission.cellWillBecome", { value: args.value ?? "—" })}
        </p>
      </div>
    );
  }

  return (
    <p className="break-words text-lg font-medium text-slate-950 dark:text-slate-50">
      {approval.title ?? t("portal.permission.unknownAction")}
    </p>
  );
}

function Preview({ approval }: { approval: ApprovalView }) {
  const t = useT();

  if (!approval.preview || approval.preview.supported === false) {
    return <p className="text-sm leading-6">{t("portal.permission.noPreview")}</p>;
  }

  const changes = approval.preview.changes;
  return (
    <div className="space-y-3">
      {approval.preview.summary ? (
        <p className="text-sm leading-6">{approval.preview.summary}</p>
      ) : null}
      {changes.length === 0 ? (
        <p className="text-sm leading-6">{t("portal.permission.noChanges")}</p>
      ) : (
        <ul className="space-y-2">
          {changes.map((change) => (
            <li
              key={change.address}
              className="rounded-lg border border-slate-200 p-3 dark:border-slate-700"
            >
              <p className="font-medium text-slate-950 dark:text-slate-50">
                {change.address}
              </p>
              <div className="mt-1 grid grid-cols-1 gap-2 text-sm sm:grid-cols-[auto_1fr_auto_1fr] sm:items-center">
                <span className="text-xs font-medium uppercase tracking-wide text-slate-500 dark:text-slate-400">
                  {t("portal.permission.before")}
                </span>
                <span className="break-all text-slate-700 dark:text-slate-200">
                  {change.before ?? "—"}
                </span>
                <span className="text-xs font-medium uppercase tracking-wide text-slate-500 dark:text-slate-400">
                  {t("portal.permission.after")}
                </span>
                <span className="break-all font-medium text-slate-950 dark:text-slate-50">
                  {change.after ?? "—"}
                </span>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Reversibility({ approval }: { approval: ApprovalView }) {
  const t = useT();
  const tone =
    approval.reversible === false
      ? "border-amber-300 bg-amber-50 text-amber-950 dark:border-amber-800 dark:bg-amber-950/40 dark:text-amber-100"
      : approval.reversible === true
        ? "border-emerald-200 bg-emerald-50 text-emerald-950 dark:border-emerald-800 dark:bg-emerald-950/40 dark:text-emerald-100"
        : "border-slate-200 bg-slate-50 text-slate-700 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-200";

  const text =
    approval.reversible === false
      ? t("portal.permission.reversibleFalse")
      : approval.reversible === true
        ? t("portal.permission.reversibleTrue")
        : t("portal.permission.reversibleNull");

  return (
    <div className={`rounded-xl border p-4 ${tone}`} role="note">
      {text}
    </div>
  );
}

type DecisionAction = "approve" | "reject";

export function PermissionApprovals() {
  const t = useT();
  const [approvals, setApprovals] = React.useState<ApprovalView[]>([]);
  const [busy, setBusy] = React.useState(false);
  const [notice, setNotice] = React.useState<string | null>(null);
  const busyRef = React.useRef(false);
  const loadSequence = React.useRef(0);

  const load = React.useCallback(async () => {
    const sequence = ++loadSequence.current;
    try {
      const next = await listApprovals();
      if (sequence === loadSequence.current) {
        setApprovals(sortedPending(next));
      }
    } catch {
      // Polling is best-effort. A transient failure should not close a dialog
      // the person is already looking at.
    }
  }, []);

  React.useEffect(() => {
    let interval: number | undefined;

    const stop = () => {
      if (interval !== undefined) {
        window.clearInterval(interval);
        interval = undefined;
      }
    };

    const start = () => {
      stop();
      if (document.visibilityState !== "visible") return;
      void load();
      interval = window.setInterval(() => {
        if (document.visibilityState === "visible") {
          void load();
        }
      }, 5000);
    };

    const onVisibilityChange = () => {
      if (document.visibilityState === "visible") {
        start();
      } else {
        stop();
      }
    };

    start();
    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => {
      stop();
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  }, [load]);

  const decide = React.useCallback(
    async (action: DecisionAction) => {
      const current = approvals[0];
      if (!current || busyRef.current) return;

      busyRef.current = true;
      setBusy(true);
      setNotice(null);

      try {
        await resolveApproval(current.id, action, {
          requestHash: current.requestHash,
          observedRevision: null,
        });
        setApprovals((previous) =>
          previous.filter((approval) => approval.id !== current.id),
        );
      } catch (error) {
        if (error instanceof ApiError && error.status === 409) {
          setNotice(t("portal.permission.changed"));
          await load();
        } else if (error instanceof ApiError && error.status === 403) {
          setNotice(t("portal.permission.notYours"));
          await load();
        } else {
          setNotice(t("portal.permission.failed"));
        }
      } finally {
        busyRef.current = false;
        setBusy(false);
      }
    },
    [approvals, load, t],
  );

  const current = approvals[0] ?? null;
  if (!current) return null;

  return (
    <PermissionDialog
      approval={current}
      busy={busy}
      notice={notice}
      waitingCount={approvals.length - 1}
      onDecision={decide}
    />
  );
}

function PermissionDialog({
  approval,
  busy,
  notice,
  waitingCount,
  onDecision,
}: {
  approval: ApprovalView;
  busy: boolean;
  notice: string | null;
  waitingCount: number;
  onDecision: (action: DecisionAction) => void;
}) {
  const t = useT();
  const dialogRef = React.useRef<HTMLDivElement>(null);
  const headingId = React.useId();
  const detailId = React.useId();
  const [whyOpen, setWhyOpen] = React.useState(false);
  const policyReason = approval.policyReason || approval.reason;
  const action = approval.title ?? t("portal.permission.unknownAction");
  const surface = approval.surfaceName ?? t("portal.permission.unknownSurface");

  React.useEffect(() => {
    const previous = document.activeElement;
    dialogRef.current?.focus();
    return () => {
      if (previous instanceof HTMLElement) {
        previous.focus();
      }
    };
  }, [approval.id]);

  const handleKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key === "Enter") {
      event.preventDefault();
      return;
    }

    if (event.key === "Escape") {
      event.preventDefault();
      if (!busy) onDecision("reject");
      return;
    }

    if (event.key !== "Tab") return;

    const dialog = dialogRef.current;
    if (!dialog) return;
    const focusable = Array.from(
      dialog.querySelectorAll<HTMLElement>(
        'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      ),
    );
    if (focusable.length === 0) {
      event.preventDefault();
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  };

  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-slate-950/50 p-4">
      <div
        ref={dialogRef}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={headingId}
        aria-describedby={detailId}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
        className="max-h-[calc(100dvh-2rem)] w-full max-w-xl overflow-y-auto rounded-2xl border border-slate-200 bg-white p-5 text-slate-900 shadow-2xl outline-none dark:border-slate-800 dark:bg-slate-900 dark:text-slate-50"
      >
        <h2 id={headingId} className="text-xl font-semibold leading-snug">
          {t("portal.permission.heading", {
            action: lowerFirst(action),
            surface,
          })}
        </h2>

        <div id={detailId} className="mt-5 space-y-5">
          <section>
            <h3 className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
              {t("portal.permission.specific")}
            </h3>
            <div className="mt-2">
              <SpecificThing approval={approval} />
            </div>
          </section>

          <section>
            <h3 className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
              {t("portal.permission.changes")}
            </h3>
            <div className="mt-2">
              <Preview approval={approval} />
            </div>
          </section>

          <section>
            <h3 className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
              {t("portal.permission.undo")}
            </h3>
            <div className="mt-2">
              <Reversibility approval={approval} />
            </div>
          </section>

          <section>
            <button
              type="button"
              className="inline-flex min-h-11 items-center rounded-lg text-sm font-medium text-slate-600 underline-offset-4 hover:text-slate-950 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-slate-500 dark:text-slate-300 dark:hover:text-white"
              aria-expanded={whyOpen}
              onClick={() => setWhyOpen((open) => !open)}
            >
              {t("portal.permission.whyTitle")}
            </button>
            {whyOpen ? (
              <p className="mt-2 rounded-xl bg-slate-50 p-3 text-sm leading-6 text-slate-600 dark:bg-slate-950 dark:text-slate-300">
                {policyReason}
              </p>
            ) : null}
          </section>
        </div>

        <div className="mt-6 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-end">
          {waitingCount > 0 ? (
            <p className="mr-auto text-sm text-slate-500 dark:text-slate-400">
              {t("portal.permission.waiting", { count: waitingCount })}
            </p>
          ) : null}
          <Button
            type="button"
            disabled={busy}
            onClick={() => onDecision("approve")}
          >
            {t("portal.permission.allow")}
          </Button>
          <Button
            type="button"
            variant="ghost"
            disabled={busy}
            className="border border-red-300 text-red-700 hover:bg-red-50 dark:border-red-900 dark:text-red-300 dark:hover:bg-red-950/40"
            onClick={() => onDecision("reject")}
          >
            {t("portal.permission.dontAllow")}
          </Button>
        </div>

        <div aria-live="polite" className="mt-4">
          {busy ? (
            <p className="text-sm text-slate-500 dark:text-slate-400">
              {t("portal.permission.working")}
            </p>
          ) : null}
          {notice ? (
            <p role="alert" className="text-sm font-medium text-amber-700 dark:text-amber-300">
              {notice}
            </p>
          ) : null}
        </div>
      </div>
    </div>
  );
}
