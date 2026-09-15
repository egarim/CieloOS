import * as React from "react";
import { Loader2, Plus, UserMinus } from "lucide-react";
import {
  addProjectMember,
  assignTask,
  createProject,
  listPeople,
  listProjects,
  readProjectReports,
  removeProjectMember,
  reportTask,
  UnauthorizedError,
  type Project,
  type ProjectReport,
  type ProjectTask,
  type TaskState,
  type Whoami,
} from "../../shared/api";
import { useT } from "../../shared/i18n";
import { Button } from "../ui/button";

const STATES: TaskState[] = ["Todo", "Doing", "Blocked", "Done"];

// One view, two shapes, chosen by the caller's role IN EACH PROJECT rather than
// by a flag from the server. The server already sends only what the caller may
// see; a role flag would be a second answer to "what may this person do", and two
// answers eventually disagree.
export function Projects({ whoami }: { whoami: Whoami }) {
  const t = useT();
  const [projects, setProjects] = React.useState<Project[]>([]);
  const [people, setPeople] = React.useState<{ slug: string; displayName: string }[]>([]);
  const [activeId, setActiveId] = React.useState<string | null>(null);
  const [reports, setReports] = React.useState<ProjectReport[]>([]);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  const [newName, setNewName] = React.useState("");
  const [taskTitle, setTaskTitle] = React.useState("");
  const [taskAssignee, setTaskAssignee] = React.useState("");
  const [addSlug, setAddSlug] = React.useState("");
  const [drafts, setDrafts] = React.useState<Record<string, { state: TaskState; text: string }>>({});

  const refresh = React.useCallback(async () => {
    try {
      const [loadedProjects, loadedPeople] = await Promise.all([listProjects(), listPeople()]);
      setProjects(loadedProjects);
      setPeople(loadedPeople);
      setError(null);
    } catch (problem) {
      if (problem instanceof UnauthorizedError) throw problem;
      setError(t("portal.projects.error"));
    } finally {
      setLoading(false);
    }
  }, [t]);

  React.useEffect(() => {
    void refresh();
    // Guarded on visibility like every other view here: a laptop with the portal
    // open in a background tab should not be asking every five seconds forever.
    const timer = window.setInterval(() => {
      if (document.visibilityState === "visible") void refresh();
    }, 5000);
    return () => window.clearInterval(timer);
  }, [refresh]);

  const active = projects.find((project) => project.id === activeId) ?? null;

  React.useEffect(() => {
    if (!active) {
      setReports([]);
      return;
    }
    readProjectReports(active.id).then(setReports).catch(() => setReports([]));
  }, [active?.id, active?.tasks.length]);

  async function run(work: () => Promise<unknown>) {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await work();
      await refresh();
    } catch (problem) {
      if (problem instanceof UnauthorizedError) throw problem;
      setError(problem instanceof Error ? problem.message : String(problem));
    } finally {
      setBusy(false);
    }
  }

  // A slug that no longer names anybody renders as the slug. Nobody is deleted
  // here yet, but a project outlives the people on it, and `undefined.displayName`
  // would white out the whole portal — there is no error boundary above this.
  const nameOf = (slug: string) =>
    people.find((person) => person.slug === slug)?.displayName ?? slug;

  const isLead = (project: Project) => project.lead === whoami.slug;
  const mine = (task: ProjectTask) => task.assignee === whoami.slug;

  if (loading) {
    return (
      <section className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300">
        <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
        {t("portal.projects.loading")}
      </section>
    );
  }

  return (
    <section className="flex flex-col gap-6">
      <div>
        <h2 className="text-2xl font-semibold">{t("portal.nav.projects")}</h2>
        <p className="mt-1 max-w-prose text-sm leading-6 text-slate-600 dark:text-slate-300">
          {t("portal.projects.lead")}
        </p>
      </div>

      {error ? (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400">{error}</p>
      ) : null}

      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(event) => {
          event.preventDefault();
          const name = newName.trim();
          if (!name) return;
          void run(async () => {
            await createProject(name);
            setNewName("");
          });
        }}
      >
        <label className="min-w-0 flex-1">
          <span className="sr-only">{t("portal.projects.newName")}</span>
          <input
            value={newName}
            onChange={(event) => setNewName(event.target.value)}
            placeholder={t("portal.projects.newName")}
            disabled={busy}
            className="min-h-11 w-full rounded-lg border border-slate-300 bg-white px-3 text-slate-950 outline-none focus:border-slate-500 disabled:opacity-60 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-50"
          />
        </label>
        <Button type="submit" disabled={busy || !newName.trim()}>
          <Plus className="h-4 w-4" aria-hidden="true" />
          {t("portal.projects.create")}
        </Button>
      </form>

      {projects.length === 0 ? (
        <p className="text-sm text-slate-600 dark:text-slate-300">{t("portal.projects.empty")}</p>
      ) : null}

      <div className="flex flex-col gap-4">
        {projects.map((project) => {
          const lead = isLead(project);
          const open = project.id === activeId;
          return (
            <article
              key={project.id}
              className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"
            >
              <button
                type="button"
                className="flex w-full items-center justify-between gap-3 text-left"
                aria-expanded={open}
                onClick={() => setActiveId(open ? null : project.id)}
              >
                <span className="min-w-0">
                  <span className="block truncate font-medium">{project.name}</span>
                  <span className="block text-xs text-slate-500 dark:text-slate-400">
                    {lead ? t("portal.projects.youLead") : t("portal.projects.ledBy", { name: nameOf(project.lead) })}
                    {" · "}
                    {t("portal.projects.memberCount", { count: String(project.members.length) })}
                  </span>
                </span>
                <span className="shrink-0 text-xs text-slate-500 dark:text-slate-400">
                  {project.tasks.filter((task) => task.state === "Done").length}/{project.tasks.length}
                </span>
              </button>

              {open ? (
                <div className="mt-4 flex flex-col gap-4 border-t border-slate-200 pt-4 dark:border-slate-800">
                  {project.tasks.length === 0 ? (
                    <p className="text-sm text-slate-600 dark:text-slate-300">{t("portal.projects.noTasks")}</p>
                  ) : (
                    <ul className="flex flex-col gap-3">
                      {project.tasks.map((task) => (
                        <li key={task.id} className="rounded-xl border border-slate-200 p-3 dark:border-slate-800">
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <span className="min-w-0 break-words font-medium">{task.title}</span>
                            <span className="text-xs text-slate-500 dark:text-slate-400">
                              {nameOf(task.assignee)} · {t(`portal.projects.state.${task.state.toLowerCase()}`)}
                            </span>
                          </div>
                          {task.note ? (
                            <p className="mt-1 whitespace-pre-wrap break-words text-sm text-slate-600 dark:text-slate-300">
                              {task.note}
                            </p>
                          ) : null}

                          {/* Only the assignee gets the form. The server refuses
                              anyone else, including the lead — this is that rule
                              made visible rather than re-implemented. */}
                          {mine(task) ? (
                            <form
                              className="mt-3 flex flex-wrap items-end gap-2"
                              onSubmit={(event) => {
                                event.preventDefault();
                                const draft = drafts[task.id] ?? { state: task.state, text: "" };
                                void run(async () => {
                                  await reportTask(task.id, draft.state, draft.text);
                                  setDrafts((current) => ({ ...current, [task.id]: { state: draft.state, text: "" } }));
                                });
                              }}
                            >
                              <label>
                                <span className="sr-only">{t("portal.projects.state")}</span>
                                <select
                                  value={(drafts[task.id] ?? { state: task.state }).state}
                                  disabled={busy}
                                  onChange={(event) =>
                                    setDrafts((current) => ({
                                      ...current,
                                      [task.id]: {
                                        state: event.target.value as TaskState,
                                        text: current[task.id]?.text ?? "",
                                      },
                                    }))
                                  }
                                  className="min-h-11 rounded-lg border border-slate-300 bg-white px-2 dark:border-slate-700 dark:bg-slate-950"
                                >
                                  {STATES.map((state) => (
                                    <option key={state} value={state}>
                                      {t(`portal.projects.state.${state.toLowerCase()}`)}
                                    </option>
                                  ))}
                                </select>
                              </label>
                              <label className="min-w-0 flex-1">
                                <span className="sr-only">{t("portal.projects.note")}</span>
                                <input
                                  value={drafts[task.id]?.text ?? ""}
                                  placeholder={t("portal.projects.note")}
                                  disabled={busy}
                                  onChange={(event) =>
                                    setDrafts((current) => ({
                                      ...current,
                                      [task.id]: {
                                        state: current[task.id]?.state ?? task.state,
                                        text: event.target.value,
                                      },
                                    }))
                                  }
                                  className="min-h-11 w-full rounded-lg border border-slate-300 bg-white px-3 dark:border-slate-700 dark:bg-slate-950"
                                />
                              </label>
                              <Button type="submit" disabled={busy}>{t("portal.projects.report")}</Button>
                            </form>
                          ) : null}
                        </li>
                      ))}
                    </ul>
                  )}

                  {lead ? (
                    <div className="flex flex-col gap-3 border-t border-slate-200 pt-4 dark:border-slate-800">
                      <form
                        className="flex flex-wrap items-end gap-2"
                        onSubmit={(event) => {
                          event.preventDefault();
                          const title = taskTitle.trim();
                          if (!title || !taskAssignee) return;
                          void run(async () => {
                            await assignTask(project.id, title, taskAssignee);
                            setTaskTitle("");
                          });
                        }}
                      >
                        <label className="min-w-0 flex-1">
                          <span className="sr-only">{t("portal.projects.taskTitle")}</span>
                          <input
                            value={taskTitle}
                            onChange={(event) => setTaskTitle(event.target.value)}
                            placeholder={t("portal.projects.taskTitle")}
                            disabled={busy}
                            className="min-h-11 w-full rounded-lg border border-slate-300 bg-white px-3 dark:border-slate-700 dark:bg-slate-950"
                          />
                        </label>
                        <label>
                          <span className="sr-only">{t("portal.projects.assignTo")}</span>
                          <select
                            value={taskAssignee}
                            disabled={busy}
                            onChange={(event) => setTaskAssignee(event.target.value)}
                            className="min-h-11 rounded-lg border border-slate-300 bg-white px-2 dark:border-slate-700 dark:bg-slate-950"
                          >
                            <option value="">{t("portal.projects.assignTo")}</option>
                            {project.members.map((slug) => (
                              <option key={slug} value={slug}>{nameOf(slug)}</option>
                            ))}
                          </select>
                        </label>
                        <Button type="submit" disabled={busy || !taskTitle.trim() || !taskAssignee}>
                          {t("portal.projects.assign")}
                        </Button>
                      </form>

                      <div className="flex flex-wrap items-center gap-2">
                        {project.members.map((slug) => (
                          <span
                            key={slug}
                            className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-3 py-1 text-xs dark:bg-slate-800"
                          >
                            {nameOf(slug)}
                            {slug === project.lead ? null : (
                              <button
                                type="button"
                                aria-label={t("portal.projects.remove", { name: nameOf(slug) })}
                                disabled={busy}
                                onClick={() => void run(() => removeProjectMember(project.id, slug))}
                                className="text-slate-500 hover:text-red-600"
                              >
                                <UserMinus className="h-3 w-3" aria-hidden="true" />
                              </button>
                            )}
                          </span>
                        ))}
                      </div>

                      <form
                        className="flex flex-wrap items-end gap-2"
                        onSubmit={(event) => {
                          event.preventDefault();
                          if (!addSlug) return;
                          void run(async () => {
                            await addProjectMember(project.id, addSlug);
                            setAddSlug("");
                          });
                        }}
                      >
                        <label>
                          <span className="sr-only">{t("portal.projects.addMember")}</span>
                          <select
                            value={addSlug}
                            disabled={busy}
                            onChange={(event) => setAddSlug(event.target.value)}
                            className="min-h-11 rounded-lg border border-slate-300 bg-white px-2 dark:border-slate-700 dark:bg-slate-950"
                          >
                            <option value="">{t("portal.projects.addMember")}</option>
                            {people
                              .filter((person) => !project.members.includes(person.slug))
                              .map((person) => (
                                <option key={person.slug} value={person.slug}>{person.displayName}</option>
                              ))}
                          </select>
                        </label>
                        <Button type="submit" disabled={busy || !addSlug}>{t("portal.projects.add")}</Button>
                      </form>
                    </div>
                  ) : null}

                  {reports.length > 0 ? (
                    <div className="border-t border-slate-200 pt-4 dark:border-slate-800">
                      <h3 className="text-sm font-medium">{t("portal.projects.trail")}</h3>
                      <ul className="mt-2 flex flex-col gap-1">
                        {reports.map((report) => (
                          <li key={report.id} className="text-xs text-slate-600 dark:text-slate-300">
                            <strong>{nameOf(report.author)}</strong>
                            {" · "}
                            {t(`portal.projects.state.${report.state.toLowerCase()}`)}
                            {report.text ? ` — ${report.text}` : ""}
                          </li>
                        ))}
                      </ul>
                    </div>
                  ) : null}
                </div>
              ) : null}
            </article>
          );
        })}
      </div>
    </section>
  );
}
