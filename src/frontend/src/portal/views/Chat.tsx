import * as React from "react";
import { Loader2, MessageSquarePlus, Send, Terminal } from "lucide-react";
import {
  askAgentStreaming,
  createThread,
  ensureAgentSession,
  getThread,
  listThreads,
  UnauthorizedError,
  type ThreadMessage,
  type ThreadSummary,
  type Whoami,
} from "../../shared/api";
import { useT } from "../../shared/i18n";
import { Button } from "../ui/button";

// A conversation that survives the page.
//
// Both halves are written by the server, not by this component. POST
// /api/threads/{id}/messages derives authorship from who is calling, so a person
// can only ever write a Person message — the agent's reply has to be recorded by
// something that legitimately speaks for the agent. That is the chat endpoint,
// which is why it takes thread_id and does the writing itself.

// The reply is markdown the agent composed partly from pages it read. Rendering it
// as HTML would let a prompt-injected page put elements in the owner's browser, so
// it stays text; only the <details> block the agent appends is stripped, because
// the steps are shown live above instead.
function ReplyText({ text }: { text: string }) {
  const body = text.replace(/<details>[\s\S]*?<\/details>/g, "").trim();
  return (
    <div className="flex flex-col gap-2">
      {body.split(/\n{2,}/).map((paragraph, index) => (
        <p key={index} className="whitespace-pre-wrap break-words text-sm leading-6">
          {paragraph}
        </p>
      ))}
    </div>
  );
}

export function Chat({ whoami }: { whoami: Whoami }) {
  const t = useT();
  const [threads, setThreads] = React.useState<ThreadSummary[]>([]);
  const [activeId, setActiveId] = React.useState<string | null>(null);
  const [messages, setMessages] = React.useState<ThreadMessage[]>([]);
  const [draft, setDraft] = React.useState("");
  const [phase, setPhase] = React.useState<"idle" | "waking" | "working">("idle");
  const [steps, setSteps] = React.useState<string[]>([]);
  const [pending, setPending] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const endRef = React.useRef<HTMLDivElement | null>(null);

  const agentSlug = whoami.homes.find((home) => home !== whoami.slug) ?? `${whoami.slug}-agent`;
  const busy = phase !== "idle";

  const refreshThreads = React.useCallback(async () => {
    try {
      setThreads(await listThreads());
    } catch (listError) {
      if (listError instanceof UnauthorizedError) throw listError;
      setError(t("portal.chat.threadsError"));
    }
  }, [t]);

  React.useEffect(() => {
    void refreshThreads();
  }, [refreshThreads]);

  const openThread = React.useCallback(
    async (id: string) => {
      setActiveId(id);
      setSteps([]);
      setPending(null);
      setError(null);
      try {
        const detail = await getThread(id);
        setMessages(detail.messages);
      } catch (openError) {
        if (openError instanceof UnauthorizedError) throw openError;
        setError(t("portal.chat.threadsError"));
      }
    },
    [t],
  );

  React.useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [messages, steps, pending]);

  function startNew() {
    setActiveId(null);
    setMessages([]);
    setSteps([]);
    setPending(null);
    setError(null);
  }

  async function send(event: React.FormEvent) {
    event.preventDefault();
    const text = draft.trim();
    if (!text || busy) return;

    setDraft("");
    setError(null);
    setSteps([]);
    setPending(null);

    try {
      let threadId = activeId;

      // A new conversation is named from its first line, trimmed to something that
      // fits a list. The person is not asked to title it: being made to name a
      // thing before saying it is the kind of ceremony that stops people using it.
      if (!threadId) {
        const title = text.length > 60 ? `${text.slice(0, 57)}...` : text;
        const created = await createThread(title, text);
        threadId = created.id;
        setActiveId(created.id);
        await refreshThreads();
      }

      // Shown immediately. The server is what actually records it, but a message
      // that does not appear until the agent finishes reads as a lost keystroke.
      const optimistic: ThreadMessage = {
        id: `local-${Date.now()}`,
        threadId,
        role: "Person",
        text,
        createdAt: new Date().toISOString(),
      };
      setMessages((current) => [...current, optimistic]);

      setPhase("waking");
      await ensureAgentSession(agentSlug);
      setPhase("working");

      let reply = "";
      const history = [...messages, optimistic].map((message) => ({
        role: message.role === "Person" ? ("user" as const) : ("assistant" as const),
        content: message.text,
      }));

      await askAgentStreaming(history, {
        // The first message of a NEW thread was already stored by createThread, so
        // passing thread_id here would record it twice. Every later message is the
        // endpoint's to write.
        threadId: activeId ? threadId : undefined,
        onStep: (step) => setSteps((current) => [...current, step]),
        onReply: (chunk) => {
          reply += chunk;
          setPending(reply);
        },
      });

      // Re-read rather than trust what was rendered: the thread on the server is
      // the record, and if the write did not happen this is where it shows.
      const detail = await getThread(threadId);
      setMessages(detail.messages);
      setPending(null);
      setSteps([]);
      await refreshThreads();
    } catch (sendError) {
      if (sendError instanceof UnauthorizedError) throw sendError;
      setError(t("portal.chat.error"));
    } finally {
      setPhase("idle");
    }
  }

  return (
    <section className="flex min-h-[60vh] flex-col gap-4 md:flex-row">
      <aside className="md:w-56 md:shrink-0">
        <Button className="w-full" onClick={startNew} disabled={busy}>
          <MessageSquarePlus className="h-4 w-4" aria-hidden="true" />
          {t("portal.chat.newThread")}
        </Button>

        <nav aria-label={t("portal.chat.threads")} className="mt-3 flex flex-col gap-1">
          {threads.map((thread) => (
            <button
              key={thread.id}
              type="button"
              onClick={() => void openThread(thread.id)}
              disabled={busy}
              aria-current={thread.id === activeId ? "true" : undefined}
              className={
                "min-h-11 rounded-lg px-3 py-2 text-left text-sm transition-colors disabled:opacity-60 " +
                (thread.id === activeId
                  ? "bg-slate-900 text-white dark:bg-slate-100 dark:text-slate-950"
                  : "text-slate-700 hover:bg-slate-200 dark:text-slate-200 dark:hover:bg-slate-800")
              }
            >
              <span className="line-clamp-2 break-words">{thread.title}</span>
            </button>
          ))}
        </nav>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <h2 className="text-2xl font-semibold">{t("portal.nav.chat")}</h2>

        {messages.length === 0 && !busy ? (
          <p className="mt-2 max-w-prose text-sm leading-6 text-slate-600 dark:text-slate-300">
            {t("portal.chat.lead")}
          </p>
        ) : null}

        <div className="mt-4 flex flex-1 flex-col gap-4">
          {messages.map((message) => (
            <div
              key={message.id}
              className={
                message.role === "Person"
                  ? "self-end max-w-[85%] rounded-2xl bg-slate-900 px-4 py-3 text-white dark:bg-slate-100 dark:text-slate-950"
                  : "self-start max-w-[85%] rounded-2xl border border-slate-200 bg-white px-4 py-3 dark:border-slate-800 dark:bg-slate-900"
              }
            >
              {message.role === "Person" ? (
                <p className="whitespace-pre-wrap break-words text-sm leading-6">{message.text}</p>
              ) : (
                <ReplyText text={message.text} />
              )}
            </div>
          ))}

          {steps.length > 0 ? (
            <div
              role="log"
              aria-live="polite"
              className="self-start w-full max-w-[85%] rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 dark:border-slate-800 dark:bg-slate-950"
            >
              <p className="flex items-center gap-2 text-sm font-medium">
                <Terminal className="h-4 w-4 shrink-0" aria-hidden="true" />
                {t("portal.chat.doing")}
              </p>
              <ul className="mt-2 flex flex-col gap-1">
                {steps.map((step, index) => (
                  <li
                    key={index}
                    className="overflow-x-auto whitespace-pre font-mono text-xs text-slate-600 dark:text-slate-300"
                  >
                    {step}
                  </li>
                ))}
              </ul>
            </div>
          ) : null}

          {pending ? (
            <div className="self-start max-w-[85%] rounded-2xl border border-slate-200 bg-white px-4 py-3 dark:border-slate-800 dark:bg-slate-900">
              <ReplyText text={pending} />
            </div>
          ) : null}

          {busy && !pending ? (
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
              disabled={busy}
              className="min-h-11 w-full resize-y rounded-lg border border-slate-300 bg-white px-3 py-2 text-slate-950 outline-none focus:border-slate-500 focus:ring-2 focus:ring-slate-300 disabled:opacity-60 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-50"
            />
          </label>
          <Button type="submit" disabled={busy || !draft.trim()}>
            <Send className="h-4 w-4" aria-hidden="true" />
            {t("portal.chat.send")}
          </Button>
        </form>
      </div>
    </section>
  );
}
