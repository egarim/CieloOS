import React from "react";
import { CirclePlay, Loader2, Monitor, RotateCw, TriangleAlert } from "lucide-react";
import {
  api,
  UnauthorizedError,
  type ApprovalRecord,
  type ExampleRun,
  type ExampleSummary,
  type SessionView,
  type Whoami,
} from "../../shared/api";
import { readable, serverText } from "../../shared/plain";
import { useT } from "../../shared/i18n";

// EXAMPLES — things this machine can do, that you press rather than read.
//
// Two properties carried over from the panel this replaces, because they are the
// whole point of the app:
//
//   - The steps are scripted, so progress is a REAL POSITION. "Step 3 of 6", never
//     a spinner, and never a bare percentage: a percentage is a number nobody can
//     check, and the step number is the same thing the run reports about itself.
//   - When a step needs consent the run STOPS and asks. That pause is the most
//     honest thing these demos show.
//
// What changed: the asking no longer happens here. The desktop owns permission —
// one request, one dialog, wherever you are — so this view hands the request to the
// shell and then does what everything else on the desktop does while Cielo waits:
// says so plainly, and offers a way back to the request it set aside.

type Translate = ReturnType<typeof useT>;

type Props = {
  whoami: Whoami | null;
  sessions: SessionView[];
  reload: () => void;
  onApproval: (approval: ApprovalRecord) => void;
  notify: (message: string) => void;
};

// A failure has to say what failed. The runtime answers errors as a JSON body
// with an `error` field, and `new Error(await response.text())` turns that into a
// message that reads `{"error":"…"}` on screen — technically the truth, and
// useless. Unwrap it; keep whatever came back if it is not that shape.
function explain(problem: unknown, t: Translate): string {
  if (problem instanceof UnauthorizedError) return t("errors.sessionTokenRejected");
  // fetch rejects with a TypeError when it never reached the machine at all.
  if (problem instanceof TypeError) return t("errors.runtimeUnreachable");
  const raw = serverText(problem);
  if (!raw) return t("errors.runtimeUnreachable");
  // Some of what comes back is a sentence and some of it is the runtime
  // explaining itself in its own vocabulary. The second kind is withheld rather
  // than repeated at a person who has never seen those words.
  return readable(raw, t("errors.wordedForTheMachine"));
}

// How a step it already took is written down. Colour belongs to the record, never
// to a choice being offered — and a refusal is the machine working, not breaking,
// so it is amber. Red is kept for the one thing that is actually wrong: a failure.
function outcomeLook(outcome: string): { tone: "good" | "hold" | "bad" | "mute"; key: string } {
  switch (outcome) {
    case "done":
      return { tone: "good", key: "examples.outcomeDone" };
    case "approved":
      return { tone: "good", key: "examples.outcomeApproved" };
    case "observed":
      return { tone: "mute", key: "examples.outcomeObserved" };
    case "refused":
      return { tone: "hold", key: "examples.outcomeRefused" };
    case "declined":
      return { tone: "hold", key: "examples.outcomeDeclined" };
    default:
      return { tone: "bad", key: "examples.outcomeFailed" };
  }
}

export default function Examples({ sessions, reload, onApproval, notify }: Props) {
  const t = useT();

  const [examples, setExamples] = React.useState<ExampleSummary[] | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [listError, setListError] = React.useState("");
  const [run, setRun] = React.useState<ExampleRun | null>(null);
  const [runError, setRunError] = React.useState("");
  const [staleError, setStaleError] = React.useState("");
  const [sessionId, setSessionId] = React.useState("");
  const [starting, setStarting] = React.useState("");

  const desktops = React.useMemo(
    () => sessions.filter((candidate) => candidate.kind === "desktop" && candidate.status === "running"),
    [sessions],
  );

  const load = React.useCallback(async () => {
    setLoading(true);
    setListError("");
    try {
      const payload = await api<{ examples: ExampleSummary[]; current: ExampleRun | null }>("/api/examples");
      setExamples(payload.examples ?? []);
      setRun(payload.current);
    } catch (problem) {
      setListError(t("examples.errorList", { detail: explain(problem, t) }));
    } finally {
      setLoading(false);
    }
  }, [t]);

  React.useEffect(() => {
    void load();
  }, [load]);

  // Keep the chosen desktop honest: default to the first one, and move off one
  // that has since been shut down rather than posting a run at a session that is
  // no longer there.
  React.useEffect(() => {
    if (desktops.length === 0) {
      setSessionId((current) => (current ? "" : current));
      return;
    }
    setSessionId((current) => (desktops.some((candidate) => candidate.id === current) ? current : desktops[0].id));
  }, [desktops]);

  const live = run?.state === "Running" || run?.state === "AwaitingApproval";

  // Poll only while a run is live. A demo nobody is running should not keep a
  // request in flight forever.
  React.useEffect(() => {
    if (!live) return;
    let cancelled = false;
    let inFlight = false;
    let failures = 0;
    const timer = window.setInterval(() => {
      if (inFlight) return;
      inFlight = true;
      api<{ current: ExampleRun | null }>("/api/examples/run")
        .then((payload) => {
          if (cancelled) return;
          failures = 0;
          setStaleError("");
          if (payload.current) setRun(payload.current);
        })
        .catch((problem: unknown) => {
          if (cancelled) return;
          // One dropped poll is noise. Three in a row means the position on
          // screen has stopped being true, and saying nothing would leave a
          // frozen progress bar looking like a working one.
          failures += 1;
          if (failures >= 3) setStaleError(t("examples.errorLostRun", { detail: explain(problem, t) }));
        })
        .finally(() => {
          inFlight = false;
        });
    }, 900);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [live, t]);

  // Hand the request to the desktop, which is the only thing that asks. The run
  // carries enough to decide by (id, reason, hash); the runtime has the whole
  // record, so try for that first and fall back to what the run knows.
  const present = React.useCallback(
    async (id: string, reason: string, requestHash: string) => {
      let record: ApprovalRecord | null = null;
      try {
        const pending = await api<ApprovalRecord[]>("/api/approvals");
        record = pending.find((candidate) => candidate.id === id) ?? null;
      } catch {
        record = null;
      }
      onApproval(
        record ?? {
          id,
          // Unknown here rather than invented: only the runtime's own record
          // carries these, and a made-up value would bind to nothing.
          toolRequestId: "",
          status: "Pending",
          reason,
          requestHash,
          createdAt: "",
        },
      );
    },
    [onApproval],
  );

  const handedOff = React.useRef<string | null>(null);
  React.useEffect(() => {
    if (!run || run.state !== "AwaitingApproval" || !run.approvalId) {
      handedOff.current = null;
      return;
    }
    const id = run.approvalId;
    if (handedOff.current === id) return;
    handedOff.current = id;
    void present(id, run.approvalReason ?? "", run.approvalHash ?? "");
  }, [run, present]);

  // A run that ends while you are looking at Files should still reach you, and
  // what it did — a file, a recording, a spreadsheet — is now stale everywhere
  // else in the desktop.
  const lastState = React.useRef<ExampleRun["state"] | null>(null);
  React.useEffect(() => {
    const state = run?.state ?? null;
    const before = lastState.current;
    lastState.current = state;
    if (!run || !state || !before || before === state) return;
    if (state === "Finished") notify(t("examples.notifyFinished", { title: run.title }));
    else if (state === "Failed") notify(t("examples.notifyStopped", { title: run.title }));
    else return;
    reload();
  }, [run, notify, reload, t]);

  async function start(example: ExampleSummary) {
    setRunError("");
    if (example.needsSession && !sessionId) {
      setRunError(t("examples.noDesktop"));
      return;
    }
    setStarting(example.id);
    try {
      const payload = await api<{ current: ExampleRun }>(`/api/examples/${encodeURIComponent(example.id)}/run`, {
        method: "POST",
        body: JSON.stringify({ sessionId: example.needsSession ? sessionId : null }),
      });
      setStaleError("");
      handedOff.current = null;
      setRun(payload.current);
      notify(t("examples.notifyStarted", { title: example.title }));
    } catch (problem) {
      setRunError(t("examples.errorStart", { title: example.title, detail: explain(problem, t) }));
    } finally {
      setStarting("");
    }
  }

  const total = run && run.totalSteps > 0 ? run.totalSteps : 0;
  const position = run ? Math.min(Math.max(run.step, 0), total || run.step) : 0;
  const filled = total > 0 ? Math.round((position / total) * 100) : 0;
  const stepLabel = run ? t("examples.stepProgress", { step: position, total: run.totalSteps }) : "";
  const runsAgain = examples?.find((candidate) => candidate.id === run?.exampleId) ?? null;

  // The chip says what the run IS; the line under the bar says where it has got
  // to. Saying the position twice would be the one place a reader stops trusting
  // either of them.
  const stateChip = (() => {
    if (!run) return null;
    if (run.state === "AwaitingApproval") return { tone: "hold", text: t("examples.stateWaiting") };
    if (run.state === "Finished") return { tone: "good", text: t("examples.stateFinished") };
    if (run.state === "Failed") return { tone: "bad", text: t("examples.stateStopped") };
    return { tone: "live", text: t("examples.stateRunning") };
  })();

  const needsDesktopSomewhere = (examples ?? []).some((example) => example.needsSession);

  return (
    <section className="xex" data-automation-id="examples-app">
      <style href="cielo-examples-app" precedence="default">
        {CSS}
      </style>

      <header>
        <h2>{t("examples.title")}</h2>
        <p className="xex-lead">{t("examples.hint")}</p>
      </header>

      {needsDesktopSomewhere &&
        (desktops.length > 0 ? (
          <div className="xex-strip">
            <Monitor size={16} strokeWidth={1.8} aria-hidden="true" />
            <label htmlFor="xex-desktop">{t("examples.sessionLabel")}</label>
            <select
              id="xex-desktop"
              value={sessionId}
              disabled={live}
              onChange={(event) => setSessionId(event.target.value)}
            >
              {desktops.map((candidate) => (
                <option key={candidate.id} value={candidate.id}>
                  {candidate.id}
                </option>
              ))}
            </select>
          </div>
        ) : (
          <p className="xex-strip hold" data-automation-id="examples-no-desktop">
            <Monitor size={16} strokeWidth={1.8} aria-hidden="true" />
            {t("examples.noDesktop")}
          </p>
        ))}

      {runError && (
        <p className="xex-fail" role="alert">
          {runError}
        </p>
      )}

      {run && stateChip && (
        <article className={`xex-run ${run.state.toLowerCase()}`} data-automation-id="example-run">
          <div className="xex-run-top">
            <div>
              <strong>{run.title}</strong>
              {run.sessionId && <span className="xex-on">{t("examples.runningOn", { session: run.sessionId })}</span>}
            </div>
            <span className={`xex-chip ${stateChip.tone}`}>
              {run.state === "Running" && <Loader2 className="xex-spin" size={12} strokeWidth={2.2} aria-hidden="true" />}
              {stateChip.text}
            </span>
          </div>

          <div>
            <div
              className="xex-track"
              role="progressbar"
              aria-label={t("examples.progressLabel")}
              aria-valuemin={0}
              aria-valuemax={run.totalSteps}
              aria-valuenow={position}
              aria-valuetext={stepLabel}
            >
              <span
                className={`xex-fill ${
                  run.state === "Failed" ? "bad" : run.state === "AwaitingApproval" ? "hold" : run.state === "Finished" ? "good" : ""
                }`}
                style={{ width: `${filled}%` }}
              />
            </div>
            <p className="xex-position">{stepLabel}</p>
          </div>

          {run.message && <p className="xex-message">{run.message}</p>}

          {run.state === "AwaitingApproval" && (
            <div className="xex-wait" data-automation-id="example-approval">
              <strong>{t("examples.approvalHeading")}</strong>
              {run.approvalReason && <p>{run.approvalReason}</p>}
              <p className="xex-quiet">{t("examples.waitingNote")}</p>
              <button
                type="button"
                className="xex-ghost"
                data-automation-id="example-open-request"
                onClick={() => {
                  if (run.approvalId) void present(run.approvalId, run.approvalReason ?? "", run.approvalHash ?? "");
                }}
              >
                {t("examples.lookAtRequest")}
              </button>
            </div>
          )}

          {staleError && (
            <p className="xex-fail" role="status">
              {staleError}
            </p>
          )}

          {run.reports.length > 0 && (
            <div>
              <p className="xex-steps-head">{t("examples.stepsHeading")}</p>
              <ol className="xex-steps">
                {run.reports.map((report) => {
                  const look = outcomeLook(report.outcome);
                  return (
                    <li key={report.number} className="xex-step">
                      <span className={`xex-dot ${look.tone}`} aria-hidden="true" />
                      {/* the step's own number, so a report can be lined up against the position above */}
                      <span className="xex-step-n">{report.number}</span>
                      <span>
                        <span className="xex-step-note">{report.note}</span>
                        {report.detail && <span className="xex-step-detail">{report.detail}</span>}
                      </span>
                      <span className={`xex-tag ${look.tone}`}>{t(look.key)}</span>
                    </li>
                  );
                })}
              </ol>
            </div>
          )}

          {!live && runsAgain && (
            <div className="xex-run-foot">
              <button
                type="button"
                className="xex-ghost"
                onClick={() => void start(runsAgain)}
                disabled={starting !== "" || (runsAgain.needsSession && !sessionId)}
              >
                <RotateCw size={13} strokeWidth={2} aria-hidden="true" />
                {t("examples.runAgain")}
              </button>
              {runsAgain.needsSession && !sessionId && (
                <span className="xex-hold-note">
                  <TriangleAlert size={13} strokeWidth={2} aria-hidden="true" />
                  {t("examples.cardNeedsDesktop")}
                </span>
              )}
            </div>
          )}
        </article>
      )}

      {loading ? (
        <p className="xex-loading">
          <Loader2 className="xex-spin" size={16} strokeWidth={2} aria-hidden="true" />
          {t("examples.loading")}
        </p>
      ) : listError ? (
        <div className="xex-fail" role="alert">
          <p>{listError}</p>
          <button type="button" className="xex-ghost" onClick={() => void load()}>
            <RotateCw size={13} strokeWidth={2} aria-hidden="true" />
            {t("examples.retry")}
          </button>
        </div>
      ) : (examples ?? []).length === 0 ? (
        <div className="xex-empty">
          <h3>{t("examples.notInstalled")}</h3>
          <p>{t("examples.notInstalledFix")}</p>
        </div>
      ) : (
        <>
          {live && <p className="xex-quiet">{t("examples.oneAtATime")}</p>}
          <ul className="xex-cards">
            {(examples ?? []).map((example) => {
              const blocked = example.needsSession && !sessionId;
              return (
                <li key={example.id} className="xex-card">
                  <h3>{example.title}</h3>
                  <p>{example.summary}</p>
                  <span className="xex-meta">
                    {t("examples.stepCount", { count: example.steps })}
                    {example.needsSession ? t("examples.needsSession") : t("examples.runsAnywhere")}
                  </span>
                  <div className="xex-card-foot">
                    <button
                      type="button"
                      className="xex-run-btn"
                      disabled={live || blocked || starting !== ""}
                      onClick={() => void start(example)}
                      data-automation-id={`example-run-${example.id}`}
                    >
                      {starting === example.id ? (
                        <>
                          <Loader2 className="xex-spin" size={13} strokeWidth={2} aria-hidden="true" />
                          {t("examples.starting")}
                        </>
                      ) : (
                        <>
                          <CirclePlay size={13} strokeWidth={2} aria-hidden="true" />
                          {t("examples.runButton")}
                        </>
                      )}
                    </button>
                    {blocked && (
                      <span className="xex-hold-note">
                        <TriangleAlert size={13} strokeWidth={2} aria-hidden="true" />
                        {t("examples.cardNeedsDesktop")}
                      </span>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        </>
      )}
    </section>
  );
}

const CSS = `
.xex { display: grid; gap: 20px; color: var(--ink, #0f172a); }
.xex h2 {
  font-family: Fraunces, "Iowan Old Style", Georgia, serif;
  font-size: 28px; font-weight: 600; letter-spacing: -0.01em; line-height: 1.15; margin: 0 0 6px;
}
.xex h3 { margin: 0; font-size: 14px; }
.xex p { margin: 0; }
.xex-lead { color: var(--ink-soft, #64748b); font-size: 13px; line-height: 1.55; max-width: 62ch; }
.xex-quiet { color: var(--ink-soft, #64748b); font-size: 11.5px; line-height: 1.5; }

.xex-strip {
  display: flex; align-items: center; gap: 9px; flex-wrap: wrap;
  padding: 11px 13px; border-radius: 13px; border: 1px solid var(--hairline, #e2e8f0);
  background: #fbfbfe; font-size: 12px; line-height: 1.5; color: var(--ink-soft, #64748b);
}
.xex-strip.hold { border-color: #f2dfc2; background: #fdf6ec; color: #8a5a10; }
.xex-strip select {
  font: inherit; color: var(--ink, #0f172a); background: #fff;
  border: 1px solid var(--hairline, #e2e8f0); border-radius: 8px; padding: 5px 8px;
}

.xex-fail {
  border: 1px solid #f3c9c9; background: #fef4f4; color: #8f1d1d;
  border-radius: 13px; padding: 12px 14px; font-size: 12.5px; line-height: 1.55;
  display: grid; gap: 10px; justify-items: start;
}

.xex-run {
  border: 1px solid var(--hairline, #e2e8f0); border-radius: 16px; background: var(--panel, #fff);
  padding: 18px; display: grid; gap: 14px; box-shadow: 0 1px 2px rgba(15, 23, 42, 0.04);
}
.xex-run.awaitingapproval { border-color: #f2dfc2; }
.xex-run-top { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
.xex-run-top strong { font-size: 14px; }
.xex-on {
  margin-left: 8px; font-size: 11px; color: var(--ink-soft, #64748b);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}
.xex-chip {
  display: inline-flex; align-items: center; gap: 6px; white-space: nowrap;
  border: 1px solid var(--hairline, #e2e8f0); border-radius: 999px; background: #f8fafc;
  color: var(--ink-soft, #64748b); font-size: 11px; font-weight: 700; padding: 4px 10px;
}
.xex-chip.live { border-color: #dcdcf7; background: #f2f2fd; color: var(--accent, #4f46e5); }
.xex-chip.hold { border-color: #f2dfc2; background: #fdf6ec; color: #8a5a10; }
.xex-chip.good { border-color: #c9e9dc; background: #ecfdf5; color: #046c50; }
.xex-chip.bad { border-color: #f3c9c9; background: #fef4f4; color: #8f1d1d; }

.xex-track { height: 5px; border-radius: 5px; background: #e2e8f0; overflow: hidden; }
.xex-fill {
  display: block; height: 100%; border-radius: 5px;
  background: var(--accent, #4f46e5); transition: width 0.3s ease;
}
.xex-fill.hold { background: var(--hold, #b45309); }
.xex-fill.bad { background: var(--deny, #dc2626); }
.xex-fill.good { background: var(--allow, #059669); }
.xex-position {
  margin-top: 7px; font-size: 11.5px; color: var(--ink-soft, #64748b); font-variant-numeric: tabular-nums;
}
.xex-message { font-size: 13px; line-height: 1.55; overflow-wrap: anywhere; }

.xex-wait { border-left: 3px solid var(--hold, #b45309); padding: 2px 0 2px 12px; display: grid; gap: 6px; justify-items: start; }
.xex-wait strong { font-size: 12.5px; line-height: 1.35; }
.xex-wait p { color: var(--ink-soft, #64748b); font-size: 11.5px; line-height: 1.5; }
.xex-wait .xex-ghost { margin-top: 4px; }

.xex-ghost {
  display: inline-flex; align-items: center; gap: 7px; cursor: pointer;
  border: 1px solid #d7dce6; background: #fff; color: var(--ink, #0f172a);
  border-radius: 10px; padding: 8px 12px; font: inherit; font-size: 11.5px; font-weight: 600;
}
.xex-ghost:hover:not(:disabled) { background: #f6f7fa; border-color: #cbd5e1; }
.xex-ghost:disabled { opacity: 0.45; cursor: default; }

.xex-steps-head {
  font-size: 9.5px; letter-spacing: 0.13em; text-transform: uppercase;
  color: var(--ink-soft, #64748b); font-weight: 700; margin-bottom: 8px;
}
.xex-steps { list-style: none; margin: 0; padding: 0; border: 1px solid var(--hairline, #e2e8f0); border-radius: 13px; overflow: hidden; }
.xex-step {
  display: grid; grid-template-columns: 8px 16px 1fr auto; gap: 11px; align-items: start;
  padding: 11px 13px; border-bottom: 1px solid var(--hairline, #e2e8f0); font-size: 12px;
}
.xex-step:last-child { border-bottom: 0; }
.xex-step-n { font-size: 11px; color: var(--ink-soft, #64748b); font-variant-numeric: tabular-nums; padding-top: 1px; }
.xex-dot { width: 7px; height: 7px; border-radius: 50%; margin-top: 5px; background: var(--allow, #059669); }
.xex-dot.hold { background: var(--hold, #b45309); }
.xex-dot.bad { background: var(--deny, #dc2626); }
.xex-dot.mute { background: #94a3b8; }
.xex-step-note { font-weight: 600; line-height: 1.45; }
.xex-step-detail {
  display: block; margin-top: 5px; padding: 7px 9px; border-radius: 8px;
  background: #f8fafc; border: 1px solid var(--hairline, #e2e8f0);
  color: rgba(15, 23, 42, 0.72); font-size: 11px; line-height: 1.55; overflow-wrap: anywhere;
}
.xex-tag {
  font-size: 9.5px; font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase;
  white-space: nowrap; color: var(--ink-soft, #64748b); padding-top: 2px;
}
.xex-tag.hold { color: #8a5a10; }
.xex-tag.bad { color: var(--deny, #dc2626); }
.xex-tag.good { color: #046c50; }
.xex-run-foot {
  border-top: 1px solid var(--hairline, #e2e8f0); padding-top: 13px;
  display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
}

.xex-cards { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(258px, 1fr)); gap: 14px; }
.xex-card {
  display: flex; flex-direction: column; gap: 8px;
  border: 1px solid var(--hairline, #e2e8f0); border-radius: 14px; background: var(--panel, #fff); padding: 16px;
}
.xex-card p { color: var(--ink-soft, #64748b); font-size: 12px; line-height: 1.5; }
.xex-meta { font-size: 11px; color: var(--ink-soft, #64748b); }
.xex-card-foot { margin-top: auto; padding-top: 8px; display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.xex-run-btn {
  display: inline-flex; align-items: center; gap: 7px; cursor: pointer;
  border: 0; border-radius: 9px; background: var(--accent, #4f46e5); color: #fff;
  padding: 9px 13px; font: inherit; font-size: 11.5px; font-weight: 700;
}
.xex-run-btn:hover:not(:disabled) { background: var(--accent-strong, #4338ca); }
.xex-run-btn:disabled { opacity: 0.45; cursor: default; }
.xex-hold-note { display: inline-flex; align-items: center; gap: 6px; font-size: 11px; color: #8a5a10; }

.xex-empty { border: 1px dashed #c7d2fe; border-radius: 16px; background: #f8faff; padding: 32px 22px; text-align: center; }
.xex-empty h3 { font: 600 20px Fraunces, Georgia, serif; margin-bottom: 7px; }
.xex-empty p { max-width: 46ch; margin: 0 auto; color: var(--ink-soft, #64748b); font-size: 12.5px; line-height: 1.55; }

.xex-loading { display: flex; align-items: center; gap: 10px; padding: 24px 2px; color: var(--ink-soft, #64748b); font-size: 12.5px; }
.xex-spin { animation: xex-spin 1s linear infinite; }
@keyframes xex-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) {
  .xex-spin { animation: none; }
  .xex-fill { transition: none; }
}
@media (max-width: 640px) { .xex-cards { grid-template-columns: 1fr; } }
`;
