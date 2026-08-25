import * as React from "react";
import {
  api,
  UnauthorizedError,
  type ApprovalRecord,
  type AuditEvent,
  type SessionView,
  type Whoami,
} from "../../shared/api";
import { carriesMachineWords } from "../../shared/plain";
import { useLanguage, useT } from "../../shared/i18n";

// ACTIVITY — what Cielo did, in plain language.
//
// The record the runtime keeps is written for the machine: raw command names
// (`browser.navigate`), and notes that are engineering prose. This view is the
// human-readable half of that record, and it exists to make two things true:
//
//   1. Every entry names BOTH actors — the person answerable for it and the
//      agent that carried it out. The runtime writes them as a pair; a view
//      that showed only one would hide half of who is responsible.
//   2. An answer is kept together with the reason that was on screen when it
//      was given, so someone can come back later and check whether they were
//      told the truth.
//
// A Blocked entry is not a fault. It is the machine refusing, and it reads that
// way here: refusing is the system working, not failing.

type Props = {
  whoami: Whoami | null;
  sessions: SessionView[];
  reload: () => void;
  onApproval: (approval: ApprovalRecord) => void;
  notify: (message: string) => void;
};

/* ----------------------------------------------------------------------- */
/* The command vocabulary                                                    */
/* ----------------------------------------------------------------------- */

// Every command the runtime can write down today, in the words a person would
// use. A command missing from this map must never render as a blank line, so
// anything unknown falls through to a readable sentence built from the raw
// name — new commands ship before this file learns about them.
const ACTION_KEYS: Record<string, string> = {
  "browser.navigate": "activity.action.browser.navigate",
  "browser.click": "activity.action.browser.click",
  "browser.back": "activity.action.browser.back",
  "console.type": "activity.action.console.type",
  "desktop.click": "activity.action.desktop.click",
  "desktop.double_click": "activity.action.desktop.doubleClick",
  "desktop.type": "activity.action.desktop.type",
  "desktop.key": "activity.action.desktop.key",
  "recorder.start": "activity.action.recorder.start",
  "recorder.stop": "activity.action.recorder.stop",
  "session.create": "activity.action.session.create",
  "session.destroy": "activity.action.session.destroy",
  "session.inhabit": "activity.action.session.inhabit",
  "session-input.grant": "activity.action.sessionInput.grant",
  "session-input.revoke": "activity.action.sessionInput.revoke",
  "session-input.grant-vision": "activity.action.sessionInput.grantVision",
  "session-input.revoke-vision": "activity.action.sessionInput.revokeVision",
  "spreadsheet.set-cell": "activity.action.spreadsheet.setCell",
  "spreadsheet.sum": "activity.action.spreadsheet.sum",
  "spreadsheet.clear": "activity.action.spreadsheet.clear",
  "approval.reject": "activity.action.approval.reject",
  "auth.login": "activity.action.auth.login",
  "auth.logout": "activity.action.auth.logout",
  "auth.logout-all": "activity.action.auth.logoutAll",
  "auth.password": "activity.action.auth.password",
  "auth.key.create": "activity.action.auth.keyCreate",
  "auth.key.revoke": "activity.action.auth.keyRevoke",
  "user.add": "activity.action.user.add",
  "model.add": "activity.action.model.add",
  "model.remove": "activity.action.model.remove",
  "usage.limit": "activity.action.usage.limit",
  "desk.image.build": "activity.action.desk.imageBuild",
  "home.download": "activity.action.home.download",
  "shared.download": "activity.action.shared.download",
  "runtime.seed": "activity.action.runtime.seed",
};

// Why Cielo stops for a kind of action. This is the reasoning for the KIND,
// not for one instance of it — the same words every time, which is exactly what
// the request card puts on screen for the same command. Keying it on the action
// name means the two are the same function of the same input and cannot quietly
// drift apart; the instance-specific half of a request card (which address,
// which keystrokes) is never reconstructed here, because it is not in the
// record and inventing it would be the very lie this view exists to catch.
const WHY_KEYS: Record<string, string> = {
  "browser.navigate": "activity.why.browser.navigate",
  "desktop.type": "activity.why.desktop.type",
  "desktop.key": "activity.why.desktop.key",
  "session.destroy": "activity.why.session.destroy",
  "spreadsheet.clear": "activity.why.spreadsheet.clear",
};

// The runtime stamps a person's answer onto the entry with one of these three
// openings. They are the only thing in the record that separates "you said no"
// from "the machine said no" — both land as Blocked — so the answer is read
// from them rather than guessed from the outcome.
const ANSWER_MARKS: { prefix: string; answer: Answer }[] = [
  { prefix: "Human rejected approval request.", answer: "refused" },
  { prefix: "Human approved it, but it did not take effect", answer: "allowedNotDone" },
  { prefix: "Human approved request.", answer: "allowed" },
];

// Notes the runtime writes for itself. Some are a plain sentence a person can
// read ("'joche' signed in."); others are the full written reasoning behind a
// rule, which is engineering prose and belongs nowhere near this list. Rather
// than guess, anything carrying the machine's own vocabulary — or running to
// paragraph length — is left out, and the plain-language line above it carries
// the meaning instead.
//
// The vocabulary itself lives in shared/plain so every app withholds the same
// words; these two are extra because only the record carries them.
const MACHINE_WORDS = ["targetssession", "dryrun"];

const MAX_READABLE_NOTE = 200;

function isReadableNote(detail: string): boolean {
  const trimmed = detail.trim();
  if (!trimmed || trimmed.length > MAX_READABLE_NOTE) return false;
  if (carriesMachineWords(trimmed)) return false;
  const lower = trimmed.toLowerCase();
  return !MACHINE_WORDS.some((word) => lower.includes(word));
}

/* ----------------------------------------------------------------------- */
/* Shaping the record into rows                                              */
/* ----------------------------------------------------------------------- */

type Answer = "allowed" | "refused" | "allowedNotDone";

type Row = {
  event: AuditEvent;
  when: Date | null;
  // "waiting"  — asked, still unanswered
  // "answered" — a person answered it; `answer` says how
  // "refused"  — the machine refused it; nobody was asked
  // "done"     — it simply happened
  kind: "waiting" | "answered" | "refused" | "done";
  answer: Answer | null;
  askedAt: Date | null;
};

function toDate(value: string): Date | null {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

function readAnswer(detail: string): Answer | null {
  const mark = ANSWER_MARKS.find((candidate) => detail.startsWith(candidate.prefix));
  return mark ? mark.answer : null;
}

function buildRows(events: AuditEvent[]): Row[] {
  // Entries that share a correlation share a request: the "we stopped to ask"
  // entry and the "you answered" entry are two halves of one decision. Joining
  // them is what lets an answer carry the reason that was on screen when it was
  // given.
  const answeredRequests = new Set<string>();
  const askedAt = new Map<string, Date | null>();
  for (const event of events) {
    if (!event.correlationId) continue;
    if (event.outcome === "PendingApproval") {
      askedAt.set(event.correlationId, toDate(event.occurredAt));
    } else if (readAnswer(event.detail)) {
      answeredRequests.add(event.correlationId);
    }
  }

  const rows: Row[] = [];
  for (const event of events) {
    // A request that has since been answered is not a second entry: the answer
    // carries it. Left in, every decision would be counted twice.
    if (
      event.outcome === "PendingApproval" &&
      event.correlationId &&
      answeredRequests.has(event.correlationId)
    ) {
      continue;
    }

    const answer = readAnswer(event.detail);
    const kind: Row["kind"] =
      event.outcome === "PendingApproval"
        ? "waiting"
        : answer
          ? "answered"
          : event.outcome === "Blocked"
            ? "refused"
            : "done";

    rows.push({
      event,
      when: toDate(event.occurredAt),
      kind,
      answer,
      askedAt: event.correlationId ? askedAt.get(event.correlationId) ?? null : null,
    });
  }
  return rows;
}

/* ----------------------------------------------------------------------- */
/* The view                                                                  */
/* ----------------------------------------------------------------------- */

type Load =
  | { state: "loading" }
  | { state: "ready"; events: AuditEvent[] }
  | { state: "failed"; message: string; expired: boolean };

export default function Activity({ whoami, reload, notify }: Props) {
  const t = useT();
  const language = useLanguage();
  const [load, setLoad] = React.useState<Load>({ state: "loading" });
  const [onlyMyDecisions, setOnlyMyDecisions] = React.useState(false);
  const [refreshing, setRefreshing] = React.useState(false);
  const seen = React.useRef<number | null>(null);

  const fetchEvents = React.useCallback(async (): Promise<AuditEvent[] | null> => {
    try {
      const events = await api<AuditEvent[]>("/api/audit-events");
      setLoad({ state: "ready", events });
      return events;
    } catch (error) {
      if (error instanceof UnauthorizedError) {
        setLoad({ state: "failed", message: t("activity.sessionEnded"), expired: true });
        reload();
        return null;
      }
      const message = error instanceof Error && error.message.trim() ? error.message.trim() : "";
      setLoad({
        state: "failed",
        message: message ? t("activity.failedBody", { message }) : t("activity.failedUnreachable"),
        expired: false,
      });
      return null;
    }
  }, [reload, t]);

  React.useEffect(() => {
    let alive = true;
    void (async () => {
      const events = await fetchEvents();
      if (alive && events) seen.current = events.length;
    })();
    return () => {
      alive = false;
    };
  }, [fetchEvents]);

  const refresh = React.useCallback(async () => {
    setRefreshing(true);
    const events = await fetchEvents();
    setRefreshing(false);
    if (!events) return;
    // The shell's shared state was read at the same moment as this list; asking
    // it to catch up keeps the two from disagreeing about what has happened.
    reload();
    const previous = seen.current;
    const added = previous === null ? 0 : Math.max(0, events.length - previous);
    seen.current = events.length;
    notify(added > 0 ? t("activity.refreshedNew", { count: added }) : t("activity.refreshedNone"));
  }, [fetchEvents, notify, reload, t]);

  const rows = React.useMemo(
    () => (load.state === "ready" ? buildRows(load.events) : []),
    [load],
  );
  const answeredRows = React.useMemo(() => rows.filter((row) => row.kind === "answered"), [rows]);
  const visible = onlyMyDecisions ? answeredRows : rows;

  const time = React.useMemo(
    () => new Intl.DateTimeFormat(language, { hour: "2-digit", minute: "2-digit" }),
    [language],
  );
  const dayThisYear = React.useMemo(
    () => new Intl.DateTimeFormat(language, { weekday: "long", day: "numeric", month: "long" }),
    [language],
  );
  const dayOlder = React.useMemo(
    () => new Intl.DateTimeFormat(language, { day: "numeric", month: "long", year: "numeric" }),
    [language],
  );
  const dateAndTime = React.useMemo(
    () =>
      new Intl.DateTimeFormat(language, {
        day: "numeric",
        month: "long",
        hour: "2-digit",
        minute: "2-digit",
      }),
    [language],
  );

  // A request can be answered on a day after it was asked. A bare clock time
  // would then say the wrong thing, so the date comes with it.
  const sameDay = (a: Date, b: Date) =>
    a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();

  const dayLabel = React.useCallback(
    (when: Date | null): string => {
      if (!when) return t("activity.unknownTime");
      const now = new Date();
      const midnight = (date: Date) => new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
      const days = Math.round((midnight(now) - midnight(when)) / 86400000);
      if (days === 0) return t("activity.today");
      if (days === 1) return t("activity.yesterday");
      return when.getFullYear() === now.getFullYear() ? dayThisYear.format(when) : dayOlder.format(when);
    },
    [dayOlder, dayThisYear, t],
  );

  // Group by day, keeping the runtime's order (newest first) inside each day.
  const days = React.useMemo(() => {
    const out: { key: string; label: string; rows: Row[] }[] = [];
    for (const row of visible) {
      const key = row.when
        ? `${row.when.getFullYear()}-${row.when.getMonth()}-${row.when.getDate()}`
        : "unknown";
      const last = out[out.length - 1];
      if (last && last.key === key) last.rows.push(row);
      else out.push({ key, label: dayLabel(row.when), rows: [row] });
    }
    return out;
  }, [dayLabel, visible]);

  /* --- who did it ------------------------------------------------------- */

  // Both actors, every time. `principal` is who drove the action; `onBehalfOf`
  // is the agent whose seat was used, and it is only filled in when a person
  // drove it. An agent acting on its own leaves `onBehalfOf` empty — the person
  // answerable is then whoever owns that agent, which is this account when the
  // agent is one of theirs, and unknown otherwise. Unknown is said out loud
  // rather than filled in with a guess.
  const nameFor = React.useCallback(
    (slug: string) => (whoami && slug === whoami.slug ? whoami.display || slug : slug),
    [whoami],
  );
  const ownsAgent = React.useCallback(
    (slug: string) => Boolean(whoami && slug !== whoami.slug && whoami.homes.includes(slug)),
    [whoami],
  );

  const whoLine = React.useCallback(
    (event: AuditEvent): string => {
      const actor = event.principal?.trim();
      const agent = event.onBehalfOf?.trim();
      if (actor && agent) return t("activity.actors.both", { person: nameFor(actor), agent });
      if (actor && ownsAgent(actor)) {
        return t("activity.actors.agentAlone", {
          agent: actor,
          person: whoami?.display || whoami?.slug || actor,
        });
      }
      if (actor && whoami && actor === whoami.slug) {
        return t("activity.actors.personAlone", { person: nameFor(actor) });
      }
      if (actor) return t("activity.actors.agentOnly", { agent: actor });
      return t("activity.actors.unrecorded");
    },
    [nameFor, ownsAgent, t, whoami],
  );

  /* --- one row ---------------------------------------------------------- */

  const headline = (action: string): string => {
    const key = ACTION_KEYS[action];
    if (key) return t(key);
    // Never a blank line for a command this file has not met: the raw name,
    // made as readable as it can be without pretending to know what it means.
    return t("activity.action.unknown", { name: action.replace(/[._-]+/g, " ").trim() || action });
  };

  const statusOf = (row: Row): { label: string; tone: "done" | "no" | "wait" } => {
    if (row.kind === "waiting") return { label: t("activity.status.waiting"), tone: "wait" };
    if (row.answer === "allowed") return { label: t("activity.status.youAllowed"), tone: "done" };
    if (row.answer === "refused") return { label: t("activity.status.youRefused"), tone: "no" };
    if (row.answer === "allowedNotDone") return { label: t("activity.status.allowedNotDone"), tone: "wait" };
    if (row.kind === "refused") return { label: t("activity.status.refused"), tone: "no" };
    return { label: t("activity.status.done"), tone: "done" };
  };

  const renderRow = (row: Row) => {
    const { event } = row;
    const status = statusOf(row);
    const why = WHY_KEYS[event.action] ? t(WHY_KEYS[event.action]) : t("activity.why.fallback");
    const note = isReadableNote(event.detail) ? event.detail.trim() : null;

    return (
      <article className="cact-row" key={event.id}>
        <i className={`cact-dot cact-${status.tone}`} aria-hidden="true" />
        <div className="cact-body">
          <strong>{headline(event.action)}</strong>
          <span className={`cact-status cact-${status.tone}`}>{status.label}</span>
          <p className="cact-who">{whoLine(event)}</p>

          {row.kind === "refused" && <p className="cact-plain">{t("activity.refusedLine")}</p>}
          {row.answer === "allowedNotDone" && (
            <p className="cact-plain">{t("activity.allowedNotDoneLine")}</p>
          )}

          {(row.kind === "answered" || row.kind === "waiting") && (
            <span className="cact-shown">
              <b>{row.kind === "waiting" ? t("activity.reasonShowing") : t("activity.reasonShown")}</b>
              {why}
            </span>
          )}

          {row.kind === "answered" && row.askedAt && (
            <p className="cact-asked">
              {t("activity.askedAt", {
                time:
                  row.when && sameDay(row.askedAt, row.when)
                    ? time.format(row.askedAt)
                    : dateAndTime.format(row.askedAt),
              })}
            </p>
          )}

          {note && (
            <p className="cact-note">
              <b>{row.kind === "refused" ? t("activity.whyRefused") : t("activity.recordedNote")}</b>
              {note}
            </p>
          )}
          {!note && row.kind === "refused" && (
            <span className="cact-shown">
              <b>{t("activity.whyRefused")}</b>
              {why}
            </span>
          )}
        </div>
        <time className="cact-time" dateTime={event.occurredAt}>
          {row.when ? time.format(row.when) : t("activity.unknownTime")}
        </time>
      </article>
    );
  };

  /* --- the page --------------------------------------------------------- */

  return (
    <section className="cact" data-automation-id="activity">
      <style>{CSS}</style>
      <h2>{t("activity.title")}</h2>
      <p className="cact-lead">{t("activity.lead")}</p>

      <div className="cact-bar">
        <div className="cact-seg" role="group" aria-label={t("activity.filterLabel")}>
          <button type="button" aria-pressed={!onlyMyDecisions} onClick={() => setOnlyMyDecisions(false)}>
            {t("activity.filterAll")}
          </button>
          <button
            type="button"
            aria-pressed={onlyMyDecisions}
            onClick={() => setOnlyMyDecisions(true)}
          >
            {t("activity.filterMine", { count: answeredRows.length })}
          </button>
        </div>
        <button type="button" className="cact-refresh" onClick={refresh} disabled={refreshing}>
          {refreshing ? t("activity.refreshing") : t("activity.refresh")}
        </button>
      </div>

      {load.state === "loading" && <p className="cact-quiet">{t("activity.loading")}</p>}

      {load.state === "failed" && (
        <div className="cact-empty cact-broken">
          <h3>{load.expired ? t("activity.sessionEndedTitle") : t("activity.failedTitle")}</h3>
          <p>{load.message}</p>
          {!load.expired && (
            <button type="button" className="cact-refresh" onClick={refresh} disabled={refreshing}>
              {refreshing ? t("activity.refreshing") : t("activity.retry")}
            </button>
          )}
        </div>
      )}

      {load.state === "ready" && visible.length === 0 && (
        <div className="cact-empty">
          <h3>{onlyMyDecisions ? t("activity.emptyAnsweredTitle") : t("activity.emptyTitle")}</h3>
          <p>{onlyMyDecisions ? t("activity.emptyAnsweredBody") : t("activity.emptyBody")}</p>
        </div>
      )}

      {load.state === "ready" &&
        days.map((day) => (
          <div key={day.key}>
            <div className="cact-day">
              <span>{day.label}</span>
              <i aria-hidden="true" />
            </div>
            <div className="cact-list">{day.rows.map(renderRow)}</div>
          </div>
        ))}
    </section>
  );
}

const CSS = `
.cact { color: var(--ink, #0f172a); font-size: 13px; }
.cact h2 { font: 560 28px/1.15 Fraunces, "Iowan Old Style", Georgia, serif;
  letter-spacing: -.02em; margin: 0 0 6px; }
.cact-lead { color: var(--ink-soft, #64748b); font-size: 13px; line-height: 1.55;
  max-width: 62ch; margin: 0 0 20px; }

.cact-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; margin-bottom: 6px; }
.cact-seg { display: inline-flex; border: 1px solid var(--hairline, #e2e8f0); border-radius: 999px;
  background: var(--panel, #fff); overflow: hidden; }
.cact-seg button { border: 0; background: transparent; padding: 7px 14px; font: inherit;
  font-size: 11.5px; font-weight: 600; color: var(--ink-soft, #64748b); cursor: pointer; }
.cact-seg button:hover { color: var(--ink, #0f172a); }
.cact-seg button[aria-pressed="true"] { background: var(--accent, #4f46e5); color: #fff; }
.cact-refresh { margin-left: auto; border: 1px solid var(--hairline, #e2e8f0);
  background: var(--panel, #fff); color: var(--ink, #0f172a); border-radius: 999px;
  padding: 7px 14px; font: inherit; font-size: 11.5px; font-weight: 600; cursor: pointer; }
.cact-refresh:hover:enabled { border-color: #cbd5e1; background: #f8fafc; }
.cact-refresh:disabled { opacity: .55; cursor: default; }

.cact-quiet { color: var(--ink-soft, #64748b); font-size: 12.5px; margin: 20px 0 0; }

.cact-day { display: flex; align-items: center; gap: 12px; margin: 24px 0 9px; }
.cact-day span { font-size: 11px; letter-spacing: .14em; text-transform: uppercase;
  color: var(--ink-soft, #64748b); font-weight: 700; }
.cact-day i { flex: 1; height: 1px; background: var(--hairline, #e2e8f0); }

.cact-list { border: 1px solid var(--hairline, #e2e8f0); border-radius: 14px;
  background: var(--panel, #fff); overflow: hidden; }
.cact-row { display: grid; grid-template-columns: 10px 1fr auto; align-items: start; gap: 11px;
  padding: 14px 15px; border-bottom: 1px solid var(--hairline, #e2e8f0); font-size: 12px; }
.cact-row:last-child { border-bottom: 0; }
.cact-dot { width: 7px; height: 7px; border-radius: 50%; margin-top: 6px; flex: none; }
.cact-dot.cact-done { background: var(--allow, #059669); }
.cact-dot.cact-no { background: var(--deny, #dc2626); }
.cact-dot.cact-wait { background: var(--hold, #b45309); }

.cact-body { min-width: 0; }
.cact-body strong { font-size: 12.5px; line-height: 1.4; }
.cact-status { display: inline-block; margin-left: 9px; font-size: 10.5px; font-weight: 700;
  letter-spacing: .04em; vertical-align: 1px; }
.cact-status.cact-done { color: var(--allow, #059669); }
.cact-status.cact-no { color: var(--deny, #dc2626); }
.cact-status.cact-wait { color: var(--hold, #b45309); }

.cact-who { margin: 4px 0 0; color: var(--ink-soft, #64748b); font-size: 11px; line-height: 1.5; }
.cact-plain { margin: 5px 0 0; font-size: 11.5px; line-height: 1.5; color: rgba(15, 23, 42, .72); }
.cact-asked { margin: 5px 0 0; color: var(--ink-soft, #64748b); font-size: 10.5px; }

.cact-shown { display: block; margin-top: 7px; padding: 8px 10px; border-radius: 8px;
  background: #f8fafc; border: 1px solid var(--hairline, #e2e8f0);
  color: rgba(15, 23, 42, .72); font-size: 11px; line-height: 1.55; overflow-wrap: anywhere; }
.cact-shown b, .cact-note b { color: var(--ink-soft, #64748b); font-weight: 600; display: block;
  font-size: 9.5px; letter-spacing: .1em; text-transform: uppercase; margin-bottom: 3px; }
.cact-note { margin: 7px 0 0; color: rgba(15, 23, 42, .72); font-size: 11px; line-height: 1.55;
  overflow-wrap: anywhere; }

.cact-time { color: var(--ink-soft, #64748b); font-size: 11px; white-space: nowrap; padding-top: 1px;
  font-variant-numeric: tabular-nums; }

.cact-empty { margin-top: 18px; padding: 34px 22px; text-align: center;
  border: 1px dashed #c7d2fe; border-radius: 16px; background: #f8faff; }
.cact-empty h3 { margin: 0 0 7px; font: 560 20px Fraunces, "Iowan Old Style", Georgia, serif; }
.cact-empty p { margin: 0 auto 4px; max-width: 46ch; color: var(--ink-soft, #64748b);
  font-size: 12px; line-height: 1.55; }
.cact-broken { border-color: #fbcfcf; background: #fffaf9; }
.cact-broken .cact-refresh { margin: 14px auto 0; display: block; }

@media (max-width: 560px) {
  .cact-row { grid-template-columns: 10px 1fr; }
  .cact-time { grid-column: 2; padding-top: 6px; }
}
`;
