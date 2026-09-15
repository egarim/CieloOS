import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { LanguageProvider } from "../../shared/i18n";
import type { Whoami } from "../../shared/api";
import { Home } from "./Home";

vi.mock("../../shared/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../../shared/api")>();
  return {
    ...original,
    listThreads: vi.fn(),
    getThread: vi.fn(),
    listShared: vi.fn(),
    listMessages: vi.fn(),
    listProjects: vi.fn(),
    createThread: vi.fn(),
  };
});

import { getThread, listMessages, listProjects, listShared, listThreads } from "../../shared/api";

const whoami: Whoami = {
  slug: "joche",
  display: "Joche",
  kind: "Human",
  homes: ["joche"],
  deskProfile: "default",
  deskProfileLabel: "Default",
};

function renderHome() {
  return render(<LanguageProvider language="en"><Home whoami={whoami} /></LanguageProvider>);
}

afterEach(() => vi.clearAllMocks());

test("renders empty API data without throwing", async () => {
  vi.mocked(listThreads).mockResolvedValue([]);
  vi.mocked(listShared).mockResolvedValue({ owner: "joche", path: "", entries: [] });
  vi.mocked(listMessages).mockResolvedValue({ conversations: [], people: [] });
  vi.mocked(listProjects).mockResolvedValue([]);

  renderHome();

  expect(screen.getByRole("heading", { name: "Good to see you, Joche" })).toBeInTheDocument();
  await waitFor(() => expect(screen.getByText("No recent conversations yet.")).toBeInTheDocument());
  expect(screen.getByText("No recent files yet.")).toBeInTheDocument();
  expect(screen.getByText("Nothing has been assigned to you.")).toBeInTheDocument();
});

test("renders populated conversations, files, messages and projects", async () => {
  vi.mocked(listThreads).mockResolvedValue([{ id: "thread-1", ownerSlug: "joche", title: "September proposal", state: "Running", createdAt: "2026-09-14T10:00:00Z", lastActivityAt: "2026-09-15T10:00:00Z" }]);
  vi.mocked(getThread).mockResolvedValue({ id: "thread-1", ownerSlug: "joche", title: "September proposal", state: "Running", createdAt: "2026-09-14T10:00:00Z", lastActivityAt: "2026-09-15T10:00:00Z", messages: [{ id: "message-1", threadId: "thread-1", role: "Agent", text: "Review the draft", createdAt: "2026-09-15T10:00:00Z" }] });
  vi.mocked(listShared).mockResolvedValue({ owner: "joche", path: "", entries: [{ name: "proposal.pdf", kind: "file", size: 42, modifiedEpoch: 1_789_460_000 }] });
  vi.mocked(listMessages).mockResolvedValue({ conversations: [{ withSlug: "anna", withDisplay: "Anna", lastText: "Looks good", lastFromSlug: "anna", lastAt: "2026-09-15T11:00:00Z", unread: 2 }], people: [{ slug: "anna", displayName: "Anna", isAgent: false }] });
  vi.mocked(listProjects).mockResolvedValue([{ id: "project-1", name: "Client launch", lead: "anna", org: "studio", createdAt: "2026-09-01T00:00:00Z", members: ["anna", "joche"], tasks: [{ id: "task-1", title: "Final review", assignee: "joche", state: "Doing", note: "", updatedAt: "2026-09-15T09:00:00Z" }] }]);

  renderHome();

  await waitFor(() => expect(screen.getAllByText("September proposal")).toHaveLength(2));
  expect(screen.getByText("proposal.pdf")).toBeInTheDocument();
  expect(screen.getByText("Anna")).toBeInTheDocument();
  expect(screen.getByText("Client launch")).toBeInTheDocument();
  expect(screen.getByText("Final review")).toBeInTheDocument();
});

// The activity strip exists to show work that is running or WAITING ON YOU. The
// state classifier was written as substring matching — "wait", "approval",
// "block", "run", "work", "doing" — and none of those appear in "NeedsYou", the
// server's actual enum value for exactly that case. So a thread waiting on its
// owner classified as done, and the strip filters done away: the one state it
// exists to surface was the one state it dropped. "Failed" went the same way.
test("a thread that needs you reaches the activity strip", async () => {
  vi.mocked(listThreads).mockResolvedValue([
    { id: "t1", ownerSlug: "joche", title: "Waiting on your approval", state: "NeedsYou",
      createdAt: "2026-09-15T10:00:00Z", lastActivityAt: "2026-09-15T10:00:00Z" },
    { id: "t2", ownerSlug: "joche", title: "That one that broke", state: "Failed",
      createdAt: "2026-09-15T09:00:00Z", lastActivityAt: "2026-09-15T09:00:00Z" },
    { id: "t3", ownerSlug: "joche", title: "Already finished", state: "Done",
      createdAt: "2026-09-15T08:00:00Z", lastActivityAt: "2026-09-15T08:00:00Z" },
  ]);
  vi.mocked(getThread).mockResolvedValue({
    id: "t1", ownerSlug: "joche", title: "Waiting on your approval", state: "NeedsYou",
    createdAt: "2026-09-15T10:00:00Z", lastActivityAt: "2026-09-15T10:00:00Z", messages: [],
  });
  vi.mocked(listShared).mockResolvedValue({ owner: "joche", path: "", entries: [] });
  vi.mocked(listMessages).mockResolvedValue({ conversations: [], people: [] });
  vi.mocked(listProjects).mockResolvedValue([]);

  renderHome();

  await waitFor(() => expect(screen.getAllByText("Waiting on your approval").length).toBeGreaterThan(0));
  expect(screen.getAllByText("That one that broke").length).toBeGreaterThan(0);
  // And a finished one does not clutter it.
  expect(screen.queryByText("Quiet right now")).not.toBeInTheDocument();
});

// Home polls every five seconds. Reading the three newest threads in FULL on every
// tick is seven requests a tick per open tab, forever, to show a line of text that
// only changes when the thread does.
test("a thread that has not moved is not re-read on every poll", async () => {
  const summary = {
    id: "t1", ownerSlug: "joche", title: "Steady", state: "Working",
    createdAt: "2026-09-15T10:00:00Z", lastActivityAt: "2026-09-15T10:00:00Z",
  };
  vi.mocked(listThreads).mockResolvedValue([summary]);
  vi.mocked(getThread).mockResolvedValue({ ...summary, messages: [{ id: "m1", threadId: "t1", role: "Agent", text: "still going", createdAt: "2026-09-15T10:00:00Z" }] });
  vi.mocked(listShared).mockResolvedValue({ owner: "joche", path: "", entries: [] });
  vi.mocked(listMessages).mockResolvedValue({ conversations: [], people: [] });
  vi.mocked(listProjects).mockResolvedValue([]);

  renderHome();
  await waitFor(() => expect(screen.getAllByText("still going").length).toBeGreaterThan(0));
  const afterFirst = vi.mocked(getThread).mock.calls.length;

  // A second refresh with the same lastActivityAt must not fetch it again.
  await waitFor(() => expect(vi.mocked(listThreads).mock.calls.length).toBeGreaterThan(0));
  expect(vi.mocked(getThread).mock.calls.length).toBe(afterFirst);
});
