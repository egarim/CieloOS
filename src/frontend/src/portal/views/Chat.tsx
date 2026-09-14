import * as React from "react";
import { Loader2, Send } from "lucide-react";
import {
  askAgent,
  ensureAgentSession,
  UnauthorizedError,
  type AgentChatMessage,
  type Whoami,
} from "../../shared/api";
import { useT } from "../../shared/i18n";
import { Button } from "../ui/button";

// Asking the agent for something and getting it back.
//
// Two things happen here that the person must never be told about. The agent can
// only work inside a running console session, and the runtime's own answer when
// there isn't one is "open one from the agent's desk (Sessions -> agent-console)"
// — administrator vocabulary aimed at somebody with a panel. And a request takes
// ten seconds to a minute, which is long enough to look broken if nothing says
// otherwise. So the session is started for them, and the waiting is narrated.

type Turn = AgentChatMessage & { at: number };

// The reply comes back as markdown: tables, bold, and a <details> block listing
// the commands the agent ran. Rendering it as HTML would mean trusting text that
// the agent composed from web pages it read — a prompt-injected page could then
// put markup in your browser. So it is shown as text, with only the structure
// that costs nothing to get wrong: paragraphs, and monospace for the transcript.
function ReplyText({ text }: { text: string }) {
  const withoutDetails = text.replace(/<details>[\s\S]*?<\/details>/g, "").trim();
  return (
    <div className="flex flex-col gap-2">
      {withoutDetails.split(/\n{2,}/).map((paragraph, index) => (
        <p key={index} className="whitespace-pre-wrap break-words text-sm leading-6">
          {paragraph}
        </p>
      ))}
    </div>
  );
}

export function Chat({ whoami }: { whoami: Whoami }) {
  const t = useT();
  const [turns, setTurns] = React.useState<Turn[]>([]);
  const [draft, setDraft] = React.useState("");
  const [phase, setPhase] = React.useState<"idle" | "waking" | "working">("idle");
  const [error, setError] = React.useState<string | null>(null);
  const endRef = React.useRef<HTMLDivElement | null>(null);

  // homes is [your own, ...your agents']. The agent's own home is the second.
  const agentSlug = whoami.homes.find((home) => home !== whoami.slug) ?? `${whoami.slug}-agent`;

  React.useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [turns, phase]);

  async function send(event: React.FormEvent) {
    event.preventDefault();
    const text = draft.trim();
    if (!text || phase !== "idle") {
      return;
    }

    const asked: Turn = { role: "user", content: text, at: Date.now() };
    const history = [...turns, asked];
    setTurns(history);
    setDraft("");
    setError(null);

    try {
      setPhase("waking");
      await ensureAgentSession(agentSlug);

      setPhase("working");
      // The whole conversation goes up, not just the last line: the runtime
      // carries the recent turns into the goal, so context survives.
      const reply = await askAgent(history.map(({ role, content }) => ({ role, content })));
      setTurns((current) => [...current, { role: "assistant", content: reply, at: Date.now() }]);
    } catch (sendError) {
      if (sendError instanceof UnauthorizedError) {
        throw sendError;
      }
      setError(t("portal.chat.error"));
    } finally {
      setPhase("idle");
    }
  }

  return (
    <section className="flex min-h-[60vh] flex-col">
      <h2 className="text-2xl font-semibold">{t("portal.nav.chat")}</h2>

      {turns.length === 0 ? (
        <p className="mt-2 max-w-prose text-sm leading-6 text-slate-600 dark:text-slate-300">
          {t("portal.chat.lead")}
        </p>
      ) : null}

      <div className="mt-4 flex flex-1 flex-col gap-4">
        {turns.map((turn) => (
          <div
            key={`${turn.at}-${turn.role}`}
            className={
              turn.role === "user"
                ? "self-end max-w-[85%] rounded-2xl bg-slate-900 px-4 py-3 text-white dark:bg-slate-100 dark:text-slate-950"
                : "self-start max-w-[85%] rounded-2xl border border-slate-200 bg-white px-4 py-3 dark:border-slate-800 dark:bg-slate-900"
            }
          >
            {turn.role === "user" ? (
              <p className="whitespace-pre-wrap break-words text-sm leading-6">{turn.content}</p>
            ) : (
              <ReplyText text={turn.content} />
            )}
          </div>
        ))}

        {phase !== "idle" ? (
          <div
            role="status"
            className="self-start flex items-center gap-2 rounded-2xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-600 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-300"
          >
            <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
            {phase === "waking" ? t("portal.chat.waking") : t("portal.chat.working")}
          </div>
        ) : null}

        <div ref={endRef} />
      </div>

      {error ? (
        <p role="alert" className="mt-3 text-sm text-red-600 dark:text-red-400">
          {error}
        </p>
      ) : null}

      <form onSubmit={send} className="mt-4 flex flex-wrap items-end gap-2">
        <label className="min-w-0 flex-1">
          <span className="sr-only">{t("portal.chat.inputLabel")}</span>
          <textarea
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            placeholder={t("portal.chat.placeholder")}
            rows={2}
            disabled={phase !== "idle"}
            className="min-h-11 w-full resize-y rounded-lg border border-slate-300 bg-white px-3 py-2 text-slate-950 outline-none focus:border-slate-500 focus:ring-2 focus:ring-slate-300 disabled:opacity-60 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-50"
          />
        </label>
        <Button type="submit" disabled={phase !== "idle" || !draft.trim()}>
          <Send className="h-4 w-4" aria-hidden="true" />
          {t("portal.chat.send")}
        </Button>
      </form>
    </section>
  );
}
