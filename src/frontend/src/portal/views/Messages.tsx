import * as React from "react";
import { Loader2, Send } from "lucide-react";
import {
  listMessages,
  readConversation,
  sendMessage,
  UnauthorizedError,
  type Conversation,
  type DirectMessage,
  type Person,
  type Whoami,
} from "../../shared/api";
import { useT } from "../../shared/i18n";
import { Button } from "../ui/button";

// Talking to the other people on this machine.
//
// The agent is not here and cannot be. Messages are human-only end to end, reads
// included — an agent token has no business in someone's private exchange, and
// there is no feature that wants one there. When an agent should be able to send
// on its owner's behalf, that is a surface with an approval attached, which is a
// different thing from widening this.

function initials(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? "")
    .join("");
}

export function Messages({ whoami }: { whoami: Whoami }) {
  const t = useT();
  const [conversations, setConversations] = React.useState<Conversation[]>([]);
  const [people, setPeople] = React.useState<Person[]>([]);
  const [activeSlug, setActiveSlug] = React.useState<string | null>(null);
  const [messages, setMessages] = React.useState<DirectMessage[]>([]);
  const [draft, setDraft] = React.useState("");
  const [sending, setSending] = React.useState(false);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const endRef = React.useRef<HTMLDivElement | null>(null);

  const refresh = React.useCallback(async () => {
    try {
      const listing = await listMessages();
      setConversations(listing.conversations);
      setPeople(listing.people);
    } catch (listError) {
      if (listError instanceof UnauthorizedError) throw listError;
      setError(t("portal.messages.error"));
    } finally {
      setLoading(false);
    }
  }, [t]);

  React.useEffect(() => {
    void refresh();
  }, [refresh]);

  // Polling, because there is no live stream yet (#41). Only while the tab is
  // visible: a background tab waking the runtime every few seconds is a cost
  // nobody asked for, on a machine that may be someone's laptop.
  React.useEffect(() => {
    let timer: number | undefined;
    const tick = async () => {
      if (document.visibilityState !== "visible") return;
      await refresh();
      if (activeSlug) {
        try {
          const detail = await readConversation(activeSlug);
          setMessages(detail.messages);
        } catch {
          // A failed poll is not worth a visible error; the next one may work.
        }
      }
    };
    timer = window.setInterval(() => void tick(), 5000);
    const onVisible = () => void tick();
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [refresh, activeSlug]);

  React.useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [messages]);

  const open = React.useCallback(
    async (slug: string) => {
      setActiveSlug(slug);
      setError(null);
      try {
        const detail = await readConversation(slug);
        setMessages(detail.messages);
        // Opening cleared the unread count on the server; reflect it here rather
        // than waiting for the next poll to catch up.
        await refresh();
      } catch (openError) {
        if (openError instanceof UnauthorizedError) throw openError;
        setError(t("portal.messages.error"));
      }
    },
    [refresh, t],
  );

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    const text = draft.trim();
    if (!text || !activeSlug || sending) return;
    setSending(true);
    setError(null);
    try {
      const sent = await sendMessage(activeSlug, text);
      setMessages((current) => [...current, sent]);
      setDraft("");
      await refresh();
    } catch (sendError) {
      if (sendError instanceof UnauthorizedError) throw sendError;
      setError(t("portal.messages.sendError"));
    } finally {
      setSending(false);
    }
  }

  // Everyone you could talk to: the people you already have a conversation with,
  // then everyone else on the machine. One list, so starting a new conversation is
  // not a different gesture from continuing an old one.
  const talked = new Set(conversations.map((conversation) => conversation.withSlug));
  const others = people.filter((person) => !talked.has(person.slug));

  return (
    <section className="flex min-h-[60vh] flex-col gap-4 md:flex-row">
      <aside className="md:w-64 md:shrink-0">
        <h2 className="text-2xl font-semibold">{t("portal.nav.messages")}</h2>
        <nav aria-label={t("portal.nav.messages")} className="mt-3 flex flex-col gap-1">
          {conversations.map((conversation) => (
            <button
              key={conversation.withSlug}
              type="button"
              onClick={() => void open(conversation.withSlug)}
              aria-current={conversation.withSlug === activeSlug ? "true" : undefined}
              className={
                "flex min-h-14 items-center gap-3 rounded-lg px-3 py-2 text-left transition-colors " +
                (conversation.withSlug === activeSlug
                  ? "bg-slate-900 text-white dark:bg-slate-100 dark:text-slate-950"
                  : "text-slate-700 hover:bg-slate-200 dark:text-slate-200 dark:hover:bg-slate-800")
              }
            >
              <span
                aria-hidden="true"
                className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-slate-200 text-xs font-semibold text-slate-700 dark:bg-slate-800 dark:text-slate-200"
              >
                {initials(conversation.withDisplay)}
              </span>
              <span className="min-w-0 flex-1">
                <span className="block break-words font-medium">{conversation.withDisplay}</span>
                <span className="block truncate text-xs opacity-70">
                  {conversation.lastFromSlug === whoami.slug ? t("portal.messages.youPrefix") : ""}
                  {conversation.lastText}
                </span>
              </span>
              {conversation.unread > 0 ? (
                <span className="shrink-0 rounded-full bg-red-600 px-2 py-0.5 text-xs font-semibold text-white">
                  {conversation.unread}
                </span>
              ) : null}
            </button>
          ))}

          {others.length > 0 ? (
            <>
              <p className="mt-3 px-3 text-xs font-medium uppercase tracking-wide text-slate-500 dark:text-slate-400">
                {t("portal.messages.everyoneElse")}
              </p>
              {others.map((person) => (
                <button
                  key={person.slug}
                  type="button"
                  onClick={() => void open(person.slug)}
                  aria-current={person.slug === activeSlug ? "true" : undefined}
                  className={
                    "flex min-h-11 items-center gap-3 rounded-lg px-3 py-2 text-left text-sm transition-colors " +
                    (person.slug === activeSlug
                      ? "bg-slate-900 text-white dark:bg-slate-100 dark:text-slate-950"
                      : "text-slate-700 hover:bg-slate-200 dark:text-slate-200 dark:hover:bg-slate-800")
                  }
                >
                  <span
                    aria-hidden="true"
                    className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-slate-200 text-xs font-semibold text-slate-700 dark:bg-slate-800 dark:text-slate-200"
                  >
                    {initials(person.displayName)}
                  </span>
                  <span className="min-w-0 break-words">{person.displayName}</span>
                </button>
              ))}
            </>
          ) : null}

          {!loading && conversations.length === 0 && others.length === 0 ? (
            <p className="px-3 text-sm leading-6 text-slate-600 dark:text-slate-300">
              {t("portal.messages.nobody")}
            </p>
          ) : null}
        </nav>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        {activeSlug === null ? (
          <p className="max-w-prose text-sm leading-6 text-slate-600 dark:text-slate-300">
            {t("portal.messages.lead")}
          </p>
        ) : (
          <>
            <div className="flex flex-1 flex-col gap-3">
              {messages.map((message) => (
                <div
                  key={message.id}
                  className={
                    message.fromSlug === whoami.slug
                      ? "self-end max-w-[85%] rounded-2xl bg-slate-900 px-4 py-2 text-white dark:bg-slate-100 dark:text-slate-950"
                      : "self-start max-w-[85%] rounded-2xl border border-slate-200 bg-white px-4 py-2 dark:border-slate-800 dark:bg-slate-900"
                  }
                >
                  <p className="whitespace-pre-wrap break-words text-sm leading-6">{message.text}</p>
                </div>
              ))}
              <div ref={endRef} />
            </div>

            {error ? (
              <p role="alert" className="mt-3 text-sm text-red-600 dark:text-red-400">
                {error}
              </p>
            ) : null}

            <form onSubmit={submit} className="mt-4 flex flex-wrap items-end gap-2">
              <label className="min-w-0 flex-1">
                <span className="sr-only">{t("portal.messages.inputLabel")}</span>
                <textarea
                  value={draft}
                  onChange={(event) => setDraft(event.target.value)}
                  placeholder={t("portal.messages.placeholder")}
                  rows={2}
                  maxLength={4000}
                  disabled={sending}
                  className="min-h-11 w-full resize-y rounded-lg border border-slate-300 bg-white px-3 py-2 text-slate-950 outline-none focus:border-slate-500 focus:ring-2 focus:ring-slate-300 disabled:opacity-60 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-50"
                />
              </label>
              <Button type="submit" disabled={sending || !draft.trim()}>
                {sending ? (
                  <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
                ) : (
                  <Send className="h-4 w-4" aria-hidden="true" />
                )}
                {t("portal.messages.send")}
              </Button>
            </form>
          </>
        )}
      </div>
    </section>
  );
}
