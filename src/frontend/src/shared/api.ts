// The one way this panel talks to the runtime, extracted so the new desktop shell
// and the panel it replaces share exactly the same transport rather than growing
// two subtly different ones during the changeover.
//
// The header is the CSRF defence and is not optional: the session lives in an
// httpOnly cookie the runtime only honours when X-Cielo-Panel is present, because
// a cross-site form post cannot set a custom header. Every call goes through here
// for that reason — three fetches that skipped it once shipped as 401s for anyone
// using the default login.

export const TOKEN_KEY = "cielo.token";

export class UnauthorizedError extends Error {}

// A non-2xx that is not the 401 above. Carrying the status and parsed body lets
// callers tell a stale preview (409) from a server failure (500) instead of
// string-matching whatever the runtime happened to put in the message.
export class ApiError extends Error {
  readonly status: number;
  readonly body: unknown;

  constructor(status: number, body: unknown) {
    super(typeof body === "string" ? body : JSON.stringify(body));
    this.name = "ApiError";
    this.status = status;
    this.body = body;
  }
}

// The token is held in memory as well as in storage, and memory is what makes
// the guards below honest. A browser with site data blocked throws on setItem,
// and an earlier version of this file swallowed that and claimed sign-in still
// worked "because the header is built from the value passed to api()". It is
// not — authHeaders() re-reads storage, so a blocked write meant every request
// went out with no credential and token sign-in failed as unauthorized, on the
// one screen a person cannot get past to report it.
//
// So: memory is the fallback, storage is the durability. Losing storage costs
// you "stay signed in", not the ability to sign in.
let memoryToken: string | null = null;

export function readToken(): string | null {
  try {
    const stored = window.localStorage.getItem(TOKEN_KEY);
    if (stored) {
      return stored;
    }
  } catch {
    // Fall through to memory.
  }
  return memoryToken;
}

export function writeToken(token: string): void {
  memoryToken = token;
  try {
    window.localStorage.setItem(TOKEN_KEY, token);
  } catch {
    // This page load still works. Only surviving a reload is lost.
  }
}

export function clearToken(): void {
  memoryToken = null;
  try {
    window.localStorage.removeItem(TOKEN_KEY);
  } catch {
    // Nothing was stored, so nothing needs clearing.
  }
}

export function authHeaders(extra: Record<string, string> = {}): Record<string, string> {
  const token = readToken();
  return {
    "X-Cielo-Panel": "1",
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
    ...extra,
  };
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    credentials: "same-origin",
    headers: {
      "Content-Type": "application/json",
      ...authHeaders(),
      ...(init?.headers ?? {}),
    },
  });
  if (response.status === 401) {
    throw new UnauthorizedError("The session token was rejected.");
  }
  if (!response.ok) {
    const text = await response.text();
    let body: unknown = text;
    if (text) {
      try {
        body = JSON.parse(text);
      } catch {
        body = text;
      }
    }
    throw new ApiError(response.status, body);
  }
  return response.json() as Promise<T>;
}

export type ApprovalChange = {
  address: string;
  before: string | null;
  after: string | null;
};

export type ApprovalPreview = {
  supported: boolean;
  summary: string;
  changes: ApprovalChange[];
};

export type PendingApprovalRequest = {
  toolName: string;
  operation: string;
  arguments: Record<string, string>;
};

export type ApprovalView = {
  id: string;
  toolRequestId: string;
  userId: string;
  status: string;
  createdAt: string;
  resolvedAt: string | null;
  requestHash: string;
  reason: string;
  pendingRequest: PendingApprovalRequest | null;
  preview: ApprovalPreview | null;
  title: string | null;
  surfaceName: string | null;
  reversible: boolean | null;
  policyReason: string | null;
};

export const listApprovals = () => api<ApprovalView[]>("/api/approvals");

export const resolveApproval = (
  id: string,
  action: "approve" | "reject",
  body: { requestHash: string; observedRevision: number | null },
) =>
  api<unknown>(`/api/approvals/${encodeURIComponent(id)}/${action}`, {
    method: "POST",
    body: JSON.stringify(body),
  });

// A bus command. Returns the decision as well as the result, because
// RequireApproval is a normal outcome here rather than an error — it is how the
// machine asks, and the desktop turns it into a permission request.
export type Decision = "Allow" | "Deny" | "RequireApproval";

export type ApprovalRecord = {
  id: string;
  toolRequestId: string;
  status: string;
  reason: string;
  requestHash: string;
  createdAt: string;
};

export type CommandResult = {
  decision: Decision;
  reason: string;
  execution: { executed: boolean; message: string } | null;
  approval: ApprovalRecord | null;
};

export const command = (surface: string, name: string, input: Record<string, string>) =>
  api<CommandResult>(`/api/surfaces/${surface}/commands/${name}`, {
    method: "POST",
    body: JSON.stringify({ input }),
  });

export type Whoami = {
  slug: string;
  display: string;
  kind: string;
  homes: string[];
  deskProfile: string;
  deskProfileLabel: string;
  language?: string;
};

export type SessionView = {
  id: string;
  owner: string;
  profile: string;
  status: string;
  viewportPort: number;
  kind: string;
};

export type AuditEvent = {
  id: string;
  occurredAt: string;
  action: string;
  outcome: string;
  detail: string;
  principal: string | null;
  onBehalfOf: string | null;
  correlationId: string | null;
};

export type ExampleSummary = {
  id: string;
  title: string;
  summary: string;
  needsSession: boolean;
  steps: number;
};

export type ExampleReport = { number: number; note: string; outcome: string; detail: string };

export type ExampleRun = {
  runId: string;
  exampleId: string;
  title: string;
  sessionId: string | null;
  state: "Running" | "AwaitingApproval" | "Finished" | "Failed";
  step: number;
  totalSteps: number;
  message: string;
  reports: ExampleReport[];
  approvalId?: string | null;
  approvalReason?: string | null;
  approvalHash?: string | null;
};

export type HomeEntry = { name: string; kind: string; size: number; modifiedEpoch: number };

export type Recording = {
  id: string;
  path: string;
  startedAt: string;
  width: number;
  height: number;
  fps: number;
  indicator: boolean;
  elapsedSeconds: number;
  truncated: boolean;
};

// ---------------------------------------------------------------------------
// The shared workspace: the one folder a person and their agent both reach.
//
// This is NOT /api/home/<owner>. That reads the session's home volume, where
// "shared" exists only as an empty mount point — the agent's deliverables are in
// a separate volume and the home listing shows none of them. Asking the wrong one
// makes it look like the agent claimed work it never did.

export type SharedListing = { owner: string; path: string; entries: HomeEntry[] };

export const listShared = (path = "") =>
  api<SharedListing>(`/api/shared/list${path ? `?path=${encodeURIComponent(path)}` : ""}`);

// Downloads cannot be a plain <a href>. The session cookie is only honoured
// alongside the X-Cielo-Panel header, and a link element cannot set one — the
// download would come back 401 with no way to tell the person why. So fetch it
// with the real headers and hand the browser a blob.
export async function downloadShared(path: string): Promise<{ blob: Blob; filename: string }> {
  const response = await fetch(`/api/shared/download?path=${encodeURIComponent(path)}`, {
    credentials: "same-origin",
    headers: authHeaders(),
  });
  if (response.status === 401) {
    throw new UnauthorizedError("The session token was rejected.");
  }
  if (!response.ok) {
    throw new ApiError(response.status, await response.text().catch(() => "Download failed."));
  }
  return { blob: await response.blob(), filename: path.split("/").pop() || "file" };
}

// ---------------------------------------------------------------------------
// Talking to the agent.

export type AgentChatMessage = { role: "user" | "assistant"; content: string };

type AgentChatResponse = { choices?: { message?: { role?: string; content?: string } }[] };

export async function askAgent(messages: AgentChatMessage[]): Promise<string> {
  const answer = await api<AgentChatResponse>("/v1/agent/chat/completions", {
    method: "POST",
    body: JSON.stringify({ messages }),
  });
  return answer.choices?.[0]?.message?.content ?? "";
}

// The agent can only work inside a running console session, and without one the
// runtime answers with a sentence telling the person to "open one from the agent's
// desk (Sessions -> agent-console)". That is administrator vocabulary aimed at
// somebody who has a panel; the person in the portal has no idea what was just
// asked of them. So the portal makes sure a session exists and never shows that.
//
// Deliberately NOT done by matching that sentence: it is English prose that can be
// reworded or translated at any time, and a feature that breaks when someone fixes
// a typo is not a feature.
export async function ensureAgentSession(agentSlug: string): Promise<void> {
  const sessions = await api<SessionView[]>("/api/sessions");
  const live = sessions.some(
    (session) =>
      session.owner === agentSlug && session.kind === "console" && session.status === "running",
  );
  if (live) {
    return;
  }
  await command("session", "create", { owner: agentSlug, profile: "agent-console" });
}

// ---------------------------------------------------------------------------
// Threads: where a conversation lives between visits.

export type ThreadSummary = {
  id: string;
  ownerSlug: string;
  title: string;
  state: string;
  createdAt: string;
  lastActivityAt: string;
};

export type ThreadMessage = {
  id: string;
  threadId: string;
  role: "Person" | "Agent";
  text: string;
  createdAt: string;
};

export type ThreadDetail = ThreadSummary & { messages: ThreadMessage[] };

export const listThreads = () => api<ThreadSummary[]>("/api/threads");

export const getThread = (id: string) => api<ThreadDetail>(`/api/threads/${id}`);

export const createThread = (title: string, message: string) =>
  api<ThreadSummary>("/api/threads", {
    method: "POST",
    body: JSON.stringify({ title, message }),
  });

// The streaming ask. The endpoint has emitted per-step progress all along — the
// portal simply never asked for it, so a minute of work looked like a frozen page.
//
// Steps are identified by the cielo_step flag the server sets, NOT by sniffing the
// "›" the text happens to start with. A client that recognised progress by a
// character would break silently the day anyone restyled it: commands would just
// start appearing inside the answer.
export async function askAgentStreaming(
  messages: AgentChatMessage[],
  options: { threadId?: string; onStep: (text: string) => void; onReply: (text: string) => void },
): Promise<void> {
  const response = await fetch("/v1/agent/chat/completions", {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ stream: true, messages, thread_id: options.threadId }),
  });
  if (response.status === 401) {
    throw new UnauthorizedError("The session token was rejected.");
  }
  if (!response.ok || !response.body) {
    throw new ApiError(response.status, await response.text().catch(() => "The agent could not be reached."));
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  // SSE frames are separated by a blank line and can be split across reads, so the
  // tail of the buffer is kept rather than parsed. Reading line-by-line as chunks
  // arrive would truncate any reply that happened to straddle a packet boundary.
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    let separator = buffer.indexOf("\n\n");
    while (separator !== -1) {
      const frame = buffer.slice(0, separator).trim();
      buffer = buffer.slice(separator + 2);
      separator = buffer.indexOf("\n\n");

      if (!frame.startsWith("data:")) continue;
      const payload = frame.slice(5).trim();
      if (payload === "[DONE]") return;

      try {
        const parsed = JSON.parse(payload) as {
          cielo_step?: boolean;
          choices?: { delta?: { content?: string; cielo_step?: boolean } }[];
        };
        const delta = parsed.choices?.[0]?.delta;
        const content = delta?.content ?? "";
        if (!content) continue;
        if (delta?.cielo_step === true || parsed.cielo_step === true) {
          options.onStep(content.trim());
        } else {
          options.onReply(content);
        }
      } catch {
        // A frame we cannot parse is skipped rather than thrown: losing one line of
        // progress is not a reason to fail a request the agent already completed.
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Messages: people talking to each other on this machine.
//
// Separate from threads on purpose. A thread is you and YOUR agent; these cross
// between two people, which is the boundary thread scoping exists to enforce.

export type Conversation = {
  withSlug: string;
  withDisplay: string;
  lastText: string;
  lastFromSlug: string;
  lastAt: string;
  unread: number;
};

// isAgent marks your own agent in the directory. It is a different kind of
// correspondent — it works for you rather than beside you — and a list that mixed
// the two without saying so would be the first place somebody assumed the agent
// was a colleague who could be asked to keep a secret.
export type Person = { slug: string; displayName: string; isAgent: boolean };

export type DirectMessage = {
  id: string;
  fromSlug: string;
  toSlug: string;
  text: string;
  createdAt: string;
  readAt: string | null;
};

export const listMessages = () =>
  api<{ conversations: Conversation[]; people: Person[] }>("/api/messages");

// Reading is what marks a conversation read, server-side. That is deliberate: a
// separate "mark read" call is one the client can forget, and a client that
// forgets leaves the other person looking permanently unread.
export const readConversation = (slug: string) =>
  api<{ withSlug: string; messages: DirectMessage[] }>(`/api/messages/${encodeURIComponent(slug)}`);

export const sendMessage = (slug: string, text: string) =>
  api<DirectMessage>(`/api/messages/${encodeURIComponent(slug)}`, {
    method: "POST",
    body: JSON.stringify({ text }),
  });

// ---------------------------------------------------------------------------
// Projects: work somebody handed you, and what you said about it.
//
// The server sends only what the caller may see, so the view decides its own
// shape from the rows it got — you are the manager of a project when its lead is
// you — and never from a role flag. A flag would be a second place the question
// "what may this person do" is answered, and the two would eventually disagree.
// ---------------------------------------------------------------------------

export type TaskState = "Todo" | "Doing" | "Blocked" | "Done";

export type ProjectTask = {
  id: string;
  title: string;
  assignee: string;
  state: TaskState;
  note: string;
  updatedAt: string;
};

export type Project = {
  id: string;
  name: string;
  lead: string;
  org: string;
  createdAt: string;
  members: string[];
  tasks: ProjectTask[];
};

export type ProjectReport = {
  id: string;
  taskId: string;
  author: string;
  state: TaskState;
  text: string;
  createdAt: string;
};

export const listProjects = () => api<Project[]>("/api/projects");

export const readProject = (id: string) => api<Project>(`/api/projects/${encodeURIComponent(id)}`);

export const readProjectReports = (id: string) =>
  api<ProjectReport[]>(`/api/projects/${encodeURIComponent(id)}/reports`);

export const createProject = (name: string) =>
  api<{ id: string; name: string }>("/api/projects", { method: "POST", body: JSON.stringify({ name }) });

export const addProjectMember = (id: string, slug: string) =>
  api(`/api/projects/${encodeURIComponent(id)}/members`, { method: "POST", body: JSON.stringify({ slug }) });

export const removeProjectMember = (id: string, slug: string) =>
  api(`/api/projects/${encodeURIComponent(id)}/members/${encodeURIComponent(slug)}`, { method: "DELETE" });

export const assignTask = (id: string, title: string, assignee: string) =>
  api<ProjectTask>(`/api/projects/${encodeURIComponent(id)}/tasks`, {
    method: "POST",
    body: JSON.stringify({ title, assignee }),
  });

// Only the assignee may call this — the server refuses anyone else, including the
// lead. Progress is what the person doing the work says it is.
export const reportTask = (taskId: string, state: TaskState, text: string) =>
  api(`/api/projects/tasks/${encodeURIComponent(taskId)}/report`, {
    method: "POST",
    body: JSON.stringify({ state, text }),
  });

// Who the caller may see, which the server scopes to their own organization.
export const listPeople = () =>
  api<{ slug: string; displayName: string }[]>("/api/users");
