import * as React from "react";
import {
  ArrowRight,
  Bot,
  CircleAlert,
  Clock3,
  FileText,
  Folder,
  Loader2,
  Mail,
  MessageCircle,
  Send,
} from "lucide-react";
import {
  createThread,
  getThread,
  listMessages,
  listProjects,
  listShared,
  listThreads,
  type Conversation,
  type HomeEntry,
  type Person,
  type Project,
  type ThreadSummary,
  type Whoami,
} from "../../shared/api";
import { useT } from "../../shared/i18n";

// The thread summary plus the one line of text Home shows for it. Not
// ThreadDetail: Home never needs a thread's whole message list, and asking for one
// every five seconds is how a dashboard becomes the busiest page in the product.
type HomeThread = ThreadSummary & { latest: string };

type HomeData = {
  threads: HomeThread[];
  files: HomeEntry[];
  conversations: Conversation[];
  people: Person[];
  projects: Project[];
};

const EMPTY: HomeData = { threads: [], files: [], conversations: [], people: [], projects: [] };

function newest<T>(items: T[], dateOf: (item: T) => string | number, limit: number): T[] {
  return [...items]
    .sort((left, right) => new Date(dateOf(right)).getTime() - new Date(dateOf(left)).getTime())
    .slice(0, limit);
}

// ThreadStatus is an enum on the server — Working, NeedsYou, Done, Failed,
// Abandoned — so this is a lookup, not a guess about what the word contains.
//
// It was written as substring sniffing ("wait", "approval", "block", "run",
// "work", "doing"), and NONE of those appear in "NeedsYou". So a thread waiting on
// its owner classified as done, and the activity strip filters done away — the one
// state the strip exists to surface was the one state it dropped. "Failed" and
// "Abandoned" vanished the same way.
const THREAD_STATE: Record<string, "working" | "waiting" | "done"> = {
  working: "working",
  needsyou: "waiting",
  done: "done",
  // A run that failed is not finished work; it is something the owner has to look
  // at. Grouped with waiting so it stays on the strip.
  failed: "waiting",
  abandoned: "done",
};

function stateKey(state: string): "working" | "waiting" | "done" {
  // An unrecognised state means a newer server, and the safe direction is to SHOW
  // it. Hiding something nobody has seen before is how a stuck thread becomes
  // invisible; showing a finished one is a smaller mistake.
  return THREAD_STATE[(state ?? "").toLowerCase()] ?? "working";
}

export function Home({
  whoami,
  onNavigate,
}: {
  whoami: Whoami;
  onNavigate?: (place: "chat" | "files" | "messages" | "projects") => void;
}) {
  const t = useT();
  const [data, setData] = React.useState<HomeData>(EMPTY);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState(false);
  const [prompt, setPrompt] = React.useState("");
  const [submitting, setSubmitting] = React.useState(false);

  // Last message text per thread, keyed by the activity stamp it was read at.
  //
  // Home polls every five seconds. Fetching the three newest threads in full on
  // every tick makes seven requests per tick per open tab, forever, to show a line
  // of text that only changes when the thread does. Keyed on lastActivityAt, so a
  // thread that has not moved is not re-read and one that has is.
  const latestText = React.useRef(new Map<string, string>());

  const refresh = React.useCallback(async () => {
    const [threadResult, fileResult, messageResult, projectResult] = await Promise.allSettled([
      listThreads(),
      listShared(),
      listMessages(),
      listProjects(),
    ]);

    let hadError = false;
    let threads: HomeThread[] = [];
    if (threadResult.status === "fulfilled") {
      const recent = newest(threadResult.value, (thread) => thread.lastActivityAt, 3);
      const stale = recent.filter((thread) => !latestText.current.has(`${thread.id}@${thread.lastActivityAt}`));
      const details = await Promise.allSettled(stale.map((thread) => getThread(thread.id)));
      details.forEach((result, index) => {
        if (result.status === "fulfilled") {
          latestText.current.set(
            `${stale[index].id}@${stale[index].lastActivityAt}`,
            result.value.messages.at(-1)?.text ?? "");
        } else {
          hadError = true;
        }
      });

      // Bounded: three threads are shown, and a key is only ever added for one of
      // them, but a long-lived tab would otherwise accumulate one entry per edit.
      if (latestText.current.size > 30) {
        latestText.current = new Map([...latestText.current].slice(-10));
      }

      threads = recent.map((thread) => ({
        ...thread,
        messages: [],
        latest: latestText.current.get(`${thread.id}@${thread.lastActivityAt}`) ?? "",
      }));
    } else hadError = true;

    if (fileResult.status === "rejected" || messageResult.status === "rejected" || projectResult.status === "rejected") {
      hadError = true;
    }
    setData({
      threads,
      files: fileResult.status === "fulfilled" ? newest(fileResult.value.entries, (entry) => entry.modifiedEpoch * 1000, 4) : [],
      conversations: messageResult.status === "fulfilled" ? messageResult.value.conversations : [],
      people: messageResult.status === "fulfilled" ? messageResult.value.people : [],
      projects: projectResult.status === "fulfilled" ? projectResult.value : [],
    });
    setError(hadError);
    setLoading(false);
  }, []);

  React.useEffect(() => {
    void refresh();
    const tick = () => {
      if (document.visibilityState === "visible") void refresh();
    };
    const timer = window.setInterval(tick, 5000);
    document.addEventListener("visibilitychange", tick);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", tick);
    };
  }, [refresh]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    const text = prompt.trim();
    if (!text || submitting) return;
    setSubmitting(true);
    try {
      await createThread(text.slice(0, 80), text);
      setPrompt("");
      onNavigate?.("chat");
    } catch {
      setError(true);
    } finally {
      setSubmitting(false);
    }
  }

  const agentSlugs = new Set(data.people.filter((person) => person.isAgent).map((person) => person.slug));
  const humanUnread = data.conversations.filter((item) => item.unread > 0 && !agentSlugs.has(item.withSlug));
  const agentUnread = data.conversations.filter((item) => item.unread > 0 && agentSlugs.has(item.withSlug));
  const assignedProjects = data.projects.filter((project) =>
    project.tasks.some((task) => task.assignee === whoami.slug),
  );
  const active = [
    ...data.threads
      .filter((thread) => stateKey(thread.state) !== "done")
      .map((thread) => ({ id: `thread-${thread.id}`, title: thread.title, state: stateKey(thread.state) })),
    ...assignedProjects.flatMap((project) =>
      project.tasks
        .filter((task) => task.assignee === whoami.slug && (task.state === "Doing" || task.state === "Blocked"))
        .map((task) => ({ id: `task-${task.id}`, title: task.title, state: task.state === "Blocked" ? "waiting" as const : "working" as const })),
    ),
  ].slice(0, 3);

  return (
    <section className="mx-auto max-w-5xl">
      <header className="py-4 sm:py-7">
        <p className="text-sm font-medium text-violet-700 dark:text-violet-300">{t("portal.home.today")}</p>
        <h2 className="mt-1 text-3xl font-semibold tracking-tight sm:text-4xl">
          {t("portal.home.greeting", { name: whoami.display || whoami.slug })}
        </h2>
        <form onSubmit={submit} className="mt-6 flex items-end gap-2 rounded-2xl border border-slate-200 bg-slate-50 p-2 shadow-sm focus-within:border-violet-400 focus-within:ring-2 focus-within:ring-violet-200 dark:border-slate-700 dark:bg-slate-950 dark:focus-within:border-violet-500 dark:focus-within:ring-violet-950">
          <label className="min-w-0 flex-1">
            <span className="sr-only">{t("portal.home.promptLabel")}</span>
            <textarea
              rows={2}
              value={prompt}
              onChange={(event) => setPrompt(event.target.value)}
              placeholder={t("portal.home.prompt")}
              disabled={submitting}
              className="min-h-16 w-full resize-none bg-transparent px-3 py-2 text-base outline-none placeholder:text-slate-500 sm:text-lg dark:placeholder:text-slate-400"
            />
          </label>
          <button type="submit" disabled={!prompt.trim() || submitting} aria-label={t("portal.home.start")} className="mb-1 flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-violet-700 text-white transition-colors hover:bg-violet-800 disabled:opacity-40 dark:bg-violet-500 dark:text-slate-950 dark:hover:bg-violet-400">
            {submitting ? <Loader2 className="h-5 w-5 animate-spin" aria-hidden="true" /> : <Send className="h-5 w-5" aria-hidden="true" />}
          </button>
        </form>
      </header>

      <div className="mb-5 flex min-h-12 items-center gap-3 overflow-hidden rounded-xl border border-slate-200 bg-slate-50 px-4 py-2 dark:border-slate-700 dark:bg-slate-950">
        <Clock3 className="h-4 w-4 shrink-0 text-violet-600 dark:text-violet-300" aria-hidden="true" />
        <span className="shrink-0 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{t("portal.home.activity")}</span>
        {loading ? <span role="status" className="text-sm text-slate-500">{t("portal.home.loading")}</span> : active.length ? (
          <div className="flex min-w-0 gap-2 overflow-x-auto">
            {active.map((item) => <span key={item.id} className="inline-flex shrink-0 items-center gap-2 rounded-full bg-white px-3 py-1 text-sm dark:bg-slate-800"><span className={item.state === "waiting" ? "h-2 w-2 rounded-full bg-amber-500" : "h-2 w-2 rounded-full bg-violet-500"} />{item.title}<span className="text-xs text-slate-500 dark:text-slate-400">{t(`portal.home.${item.state}`)}</span></span>)}
          </div>
        ) : <span className="text-sm text-slate-500 dark:text-slate-400">{t("portal.home.quiet")}</span>}
      </div>

      {error ? <p role="alert" className="mb-4 flex items-center gap-2 text-sm text-amber-700 dark:text-amber-300"><CircleAlert className="h-4 w-4" />{t("portal.home.partialError")}</p> : null}

      <div className="grid gap-4 lg:grid-cols-2">
        <HomeCard title={t("portal.home.continue")} icon={<MessageCircle className="h-5 w-5" />} action={() => onNavigate?.("chat")} actionLabel={t("portal.home.openChat")}>
          {data.threads.length ? data.threads.map((thread) => {
            return <HomeRow key={thread.id} title={thread.title} detail={thread.latest || t(`portal.home.${stateKey(thread.state)}`)} />;
          }) : <Empty>{t("portal.home.noThreads")}</Empty>}
        </HomeCard>

        <HomeCard title={t("portal.home.files")} icon={<Folder className="h-5 w-5" />} action={() => onNavigate?.("files")} actionLabel={t("portal.home.openFiles")}>
          {data.files.length ? data.files.map((entry) => <HomeRow key={entry.name} icon={<FileText className="h-4 w-4" />} title={entry.name} detail={entry.kind === "directory" ? t("portal.files.folder") : new Date(entry.modifiedEpoch * 1000).toLocaleDateString()} />) : <Empty>{t("portal.home.noFiles")}</Empty>}
        </HomeCard>

        <HomeCard title={t("portal.home.messages")} icon={<Mail className="h-5 w-5" />} action={() => onNavigate?.("messages")} actionLabel={t("portal.home.openMessages")}>
          {humanUnread.length ? humanUnread.slice(0, 3).map((conversation) => <HomeRow key={conversation.withSlug} title={conversation.withDisplay || conversation.withSlug} detail={conversation.lastText} badge={String(conversation.unread)} />) : <Empty>{t("portal.home.noHumanMessages")}</Empty>}
          {agentUnread.length ? <div className="mt-3 border-t border-slate-200 pt-3 dark:border-slate-700"><p className="mb-2 flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400"><Bot className="h-4 w-4" />{t("portal.home.agentNotices")}</p>{agentUnread.slice(0, 2).map((conversation) => <HomeRow key={conversation.withSlug} title={conversation.withDisplay || conversation.withSlug} detail={conversation.lastText} badge={String(conversation.unread)} />)}</div> : null}
        </HomeCard>

        <HomeCard title={t("portal.home.projects")} icon={<Clock3 className="h-5 w-5" />} action={() => onNavigate?.("projects")} actionLabel={t("portal.home.openProjects")}>
          {assignedProjects.length ? assignedProjects.slice(0, 3).map((project) => {
            const mine = project.tasks.filter((task) => task.assignee === whoami.slug);
            const done = mine.filter((task) => task.state === "Done").length;
            return <HomeRow key={project.id} title={project.name} detail={t("portal.home.projectProgress", { done: String(done), total: String(mine.length) })} />;
          }) : <Empty>{t("portal.home.noProjects")}</Empty>}
        </HomeCard>
      </div>
    </section>
  );
}

function HomeCard({ title, icon, action, actionLabel, children }: { title: string; icon: React.ReactNode; action?: () => void; actionLabel: string; children: React.ReactNode }) {
  return <article className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900"><header className="mb-3 flex items-center gap-2"><span className="text-violet-600 dark:text-violet-300">{icon}</span><h3 className="font-semibold">{title}</h3><button type="button" onClick={action} className="ml-auto flex min-h-11 items-center gap-1 px-2 text-sm text-slate-600 hover:text-violet-700 dark:text-slate-300 dark:hover:text-violet-300">{actionLabel}<ArrowRight className="h-4 w-4" aria-hidden="true" /></button></header><div className="flex flex-col gap-1">{children}</div></article>;
}

function HomeRow({ title, detail, icon, badge }: { title: string; detail: string; icon?: React.ReactNode; badge?: string }) {
  return <div className="flex min-w-0 items-center gap-3 rounded-xl px-2 py-2 hover:bg-slate-50 dark:hover:bg-slate-800/70">{icon ? <span className="shrink-0 text-slate-500">{icon}</span> : null}<span className="min-w-0 flex-1"><span className="block truncate text-sm font-medium">{title}</span><span className="block truncate text-xs text-slate-500 dark:text-slate-400">{detail}</span></span>{badge ? <span className="rounded-full bg-violet-700 px-2 py-0.5 text-xs font-semibold text-white dark:bg-violet-400 dark:text-slate-950">{badge}</span> : null}</div>;
}

function Empty({ children }: { children: React.ReactNode }) {
  return <p className="px-2 py-3 text-sm leading-6 text-slate-500 dark:text-slate-400">{children}</p>;
}
