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
    throw new Error(await response.text());
  }
  return response.json() as Promise<T>;
}

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
