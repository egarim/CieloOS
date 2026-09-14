import * as React from "react";
import { Check, Loader2, Play, Plus, Trash2, TriangleAlert } from "lucide-react";
import {
  askAgent,
  ensureAgentSession,
  UnauthorizedError,
  type Whoami,
} from "../../shared/api";
import { useT } from "../../shared/i18n";
import { Button } from "../ui/button";

// A widget here is the one the brief calls "a shortcut to a task the agent runs".
// It is the kind that actually belongs in this product: a job you ask for often,
// kept as a button, so the second time costs a click instead of a paragraph.
//
// Deliberately not a calculator or a clock. Those are widgets any web page could
// carry; nothing about them needs an operating system with an agent in it.

type SavedTask = { id: string; title: string; prompt: string };

type RunState =
  | { kind: "idle" }
  | { kind: "waking" }
  | { kind: "working" }
  | { kind: "done"; reply: string }
  | { kind: "failed" };

// Per browser, and only per browser. There is no server-side place to keep these
// yet: the shared workspace is read-only from the panel (#48) and nothing else
// stores per-person preferences. So a task saved on a laptop is not there on a
// phone, and clearing site data loses them.
//
// Every access is guarded. A browser with site data blocked throws on all of
// these, and a widget page that white-screens because storage is off would be a
// worse bug than the one it is storing around.
const storageKey = (slug: string) => `cielo.portal.tasks.${slug}`;

function loadTasks(slug: string): SavedTask[] {
  try {
    const raw = window.localStorage.getItem(storageKey(slug));
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    // Written by an older version, or edited by hand in devtools. Anything that
    // is not the shape we expect is dropped rather than rendered as undefined.
    return parsed.filter(
      (item): item is SavedTask =>
        typeof item === "object" &&
        item !== null &&
        typeof (item as SavedTask).id === "string" &&
        typeof (item as SavedTask).title === "string" &&
        typeof (item as SavedTask).prompt === "string",
    );
  } catch {
    return [];
  }
}

function saveTasks(slug: string, tasks: SavedTask[]): void {
  try {
    window.localStorage.setItem(storageKey(slug), JSON.stringify(tasks));
  } catch {
    // The task still works for this visit; it just will not be here tomorrow.
  }
}

export function Widgets({ whoami }: { whoami: Whoami }) {
  const t = useT();
  const [tasks, setTasks] = React.useState<SavedTask[]>(() => loadTasks(whoami.slug));
  const [runs, setRuns] = React.useState<Record<string, RunState>>({});
  const [adding, setAdding] = React.useState(false);
  const [title, setTitle] = React.useState("");
  const [prompt, setPrompt] = React.useState("");

  const agentSlug = whoami.homes.find((home) => home !== whoami.slug) ?? `${whoami.slug}-agent`;

  const update = React.useCallback(
    (next: SavedTask[]) => {
      setTasks(next);
      saveTasks(whoami.slug, next);
    },
    [whoami.slug],
  );

  function add(event: React.FormEvent) {
    event.preventDefault();
    const cleanTitle = title.trim();
    const cleanPrompt = prompt.trim();
    if (!cleanTitle || !cleanPrompt) return;
    update([
      ...tasks,
      { id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, title: cleanTitle, prompt: cleanPrompt },
    ]);
    setTitle("");
    setPrompt("");
    setAdding(false);
  }

  async function run(task: SavedTask) {
    const current = runs[task.id]?.kind;
    if (current === "waking" || current === "working") return;

    setRuns((state) => ({ ...state, [task.id]: { kind: "waking" } }));
    try {
      await ensureAgentSession(agentSlug);
      setRuns((state) => ({ ...state, [task.id]: { kind: "working" } }));
      const reply = await askAgent([{ role: "user", content: task.prompt }]);
      setRuns((state) => ({ ...state, [task.id]: { kind: "done", reply } }));
    } catch (runError) {
      if (runError instanceof UnauthorizedError) {
        throw runError;
      }
      setRuns((state) => ({ ...state, [task.id]: { kind: "failed" } }));
    }
  }

  return (
    <section>
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-2xl font-semibold">{t("portal.nav.widgets")}</h2>
        <Button className="ml-auto" onClick={() => setAdding((open) => !open)}>
          <Plus className="h-4 w-4" aria-hidden="true" />
          {t("portal.widgets.add")}
        </Button>
      </div>

      <p className="mt-2 max-w-prose text-sm leading-6 text-slate-600 dark:text-slate-300">
        {t("portal.widgets.lead")}
      </p>

      {adding ? (
        <form
          onSubmit={add}
          className="mt-4 flex flex-col gap-3 rounded-xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"
        >
          <label className="flex flex-col gap-1">
            <span className="text-sm font-medium">{t("portal.widgets.titleLabel")}</span>
            <input
              value={title}
              onChange={(event) => setTitle(event.target.value)}
              placeholder={t("portal.widgets.titlePlaceholder")}
              className="min-h-11 rounded-lg border border-slate-300 bg-white px-3 text-slate-950 outline-none focus:border-slate-500 focus:ring-2 focus:ring-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-50"
            />
          </label>
          <label className="flex flex-col gap-1">
            <span className="text-sm font-medium">{t("portal.widgets.promptLabel")}</span>
            <textarea
              value={prompt}
              onChange={(event) => setPrompt(event.target.value)}
              placeholder={t("portal.widgets.promptPlaceholder")}
              rows={3}
              className="w-full resize-y rounded-lg border border-slate-300 bg-white px-3 py-2 text-slate-950 outline-none focus:border-slate-500 focus:ring-2 focus:ring-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-50"
            />
          </label>
          <div className="flex flex-wrap gap-2">
            <Button type="submit" disabled={!title.trim() || !prompt.trim()}>
              {t("portal.widgets.save")}
            </Button>
            <Button type="button" variant="ghost" onClick={() => setAdding(false)}>
              {t("portal.widgets.cancel")}
            </Button>
          </div>
        </form>
      ) : null}

      {tasks.length === 0 && !adding ? (
        <p className="mt-6 max-w-prose text-sm leading-6 text-slate-600 dark:text-slate-300">
          {t("portal.widgets.empty")}
        </p>
      ) : null}

      <div className="mt-4 grid gap-3 sm:grid-cols-2">
        {tasks.map((task) => {
          const state = runs[task.id] ?? { kind: "idle" };
          const running = state.kind === "waking" || state.kind === "working";
          return (
            <article
              key={task.id}
              className="flex flex-col gap-3 rounded-xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"
            >
              <div className="flex items-start gap-2">
                <h3 className="min-w-0 break-words font-semibold">{task.title}</h3>
                <button
                  type="button"
                  onClick={() => update(tasks.filter((other) => other.id !== task.id))}
                  aria-label={t("portal.widgets.remove")}
                  className="ml-auto flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-slate-500 hover:bg-slate-200 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-slate-500 dark:hover:bg-slate-800"
                >
                  <Trash2 className="h-4 w-4" aria-hidden="true" />
                </button>
              </div>

              <p className="min-w-0 break-words text-sm leading-6 text-slate-600 dark:text-slate-300">
                {task.prompt}
              </p>

              <Button className="self-start" onClick={() => void run(task)} disabled={running}>
                {running ? (
                  <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
                ) : (
                  <Play className="h-4 w-4" aria-hidden="true" />
                )}
                {t("portal.widgets.run")}
              </Button>

              {state.kind === "waking" || state.kind === "working" ? (
                <p role="status" className="text-sm text-slate-600 dark:text-slate-300">
                  {state.kind === "waking" ? t("portal.chat.waking") : t("portal.chat.working")}
                </p>
              ) : null}

              {state.kind === "done" ? (
                <div className="flex flex-col gap-1 rounded-lg bg-slate-100 p-3 dark:bg-slate-950">
                  <p className="flex items-center gap-2 text-sm font-medium">
                    <Check className="h-4 w-4 shrink-0" aria-hidden="true" />
                    {t("portal.widgets.done")}
                  </p>
                  <p className="whitespace-pre-wrap break-words text-sm leading-6 text-slate-600 dark:text-slate-300">
                    {state.reply.replace(/<details>[\s\S]*?<\/details>/g, "").trim().slice(0, 400)}
                  </p>
                </div>
              ) : null}

              {state.kind === "failed" ? (
                <p role="alert" className="flex items-center gap-2 text-sm text-red-600 dark:text-red-400">
                  <TriangleAlert className="h-4 w-4 shrink-0" aria-hidden="true" />
                  {t("portal.chat.error")}
                </p>
              ) : null}
            </article>
          );
        })}
      </div>
    </section>
  );
}
