import React from "react";
import { createRoot } from "react-dom/client";
import { Check, KeyRound, ShieldCheck, X } from "lucide-react";
import "./styles.css";

type Branding = {
  productName: string;
  shortName: string;
  companyName: string;
  supportName: string;
  agentName: string;
};

type PlatformUser = { id: string; displayName: string; email: string };
type Workspace = { id: string; ownerUserId: string; name: string };
type AgentProfile = { id: string; ownerUserId: string; workspaceId: string; name: string; inferenceProvider: string; grantedTools: string[] };
type CellChange = { address: string; before: string | null; after: string | null };
type EffectPreview = { supported: boolean; summary: string; changes: CellChange[] };
type ApprovalView = {
  id: string;
  status: string;
  reason: string;
  createdAt: string;
  requestHash: string;
  pendingRequest?: { toolName: string; operation: string; arguments: Record<string, string> } | null;
  preview?: EffectPreview | null;
};
type AuditEvent = { id: string; occurredAt: string; action: string; outcome: string; detail: string; principal?: string | null };
type SurfaceCommand = {
  name: string;
  displayName: string;
  decision: string;
  reason: string;
  dryRun: boolean;
  reversible: boolean;
  input: { properties?: Record<string, { type?: string; pattern?: string }>; required?: string[] };
};
type SurfaceCommands = { surface: string; revision: number; commands: SurfaceCommand[] };
type SurfaceState = { surface: string; revision: number; state: { cells: Record<string, string> } };
type CommandResult = { decision: string; reason: string; approval?: ApprovalView | null; revision: number };
type InferenceStatus = {
  activeProviderId: string;
  stableEndpoint: string;
  activeProvider: {
    displayName: string;
    runtime: { engine: string };
  };
};
type ChatResponse = { providerId: string; model: string; content: string; forwarded: boolean; error?: string };

const emptyBranding: Branding = {
  productName: "Workspace Runtime",
  shortName: "Runtime",
  companyName: "Workspace Runtime Labs",
  supportName: "Support",
  agentName: "Assistant"
};

const TOKEN_KEY = "runtime.token";
const inputDefaults: Record<string, string> = { address: "C1", value: "84", source: "A1,A2,C1", target: "D1" };

function readToken(): string | null {
  return window.localStorage.getItem(TOKEN_KEY);
}

class UnauthorizedError extends Error {}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const token = readToken();
  const response = await fetch(path, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...(init?.headers ?? {})
    }
  });
  if (response.status === 401) {
    throw new UnauthorizedError("The session token was rejected.");
  }
  if (!response.ok) {
    throw new Error(await response.text());
  }
  return response.json() as Promise<T>;
}

function App() {
  const [branding, setBranding] = React.useState<Branding>(emptyBranding);
  const [token, setToken] = React.useState<string | null>(readToken());
  const [tokenDraft, setTokenDraft] = React.useState("");
  const [loginError, setLoginError] = React.useState<string | null>(null);
  const [users, setUsers] = React.useState<PlatformUser[]>([]);
  const [workspaces, setWorkspaces] = React.useState<Workspace[]>([]);
  const [agents, setAgents] = React.useState<AgentProfile[]>([]);
  const [approvals, setApprovals] = React.useState<ApprovalView[]>([]);
  const [auditEvents, setAuditEvents] = React.useState<AuditEvent[]>([]);
  const [surface, setSurface] = React.useState<SurfaceState | null>(null);
  const [commands, setCommands] = React.useState<SurfaceCommand[]>([]);
  const [commandInputs, setCommandInputs] = React.useState<Record<string, string>>(inputDefaults);
  const [inferenceStatus, setInferenceStatus] = React.useState<InferenceStatus | null>(null);
  const [prompt, setPrompt] = React.useState("Plan a safe spreadsheet summary workflow.");
  const [chatResponse, setChatResponse] = React.useState<ChatResponse | null>(null);
  const [lastResult, setLastResult] = React.useState<{ decision: string; reason: string } | null>(null);

  const signOut = React.useCallback(() => {
    window.localStorage.removeItem(TOKEN_KEY);
    setToken(null);
  }, []);

  const refresh = React.useCallback(async () => {
    try {
      const [nextBranding, nextUsers, nextWorkspaces, nextAgents, nextApprovals, nextAuditEvents, nextSurface, nextCommands, nextInferenceStatus] = await Promise.all([
        api<Branding>("/api/branding"),
        api<PlatformUser[]>("/api/users"),
        api<Workspace[]>("/api/workspaces"),
        api<AgentProfile[]>("/api/agents"),
        api<ApprovalView[]>("/api/approvals"),
        api<AuditEvent[]>("/api/audit-events"),
        api<SurfaceState>("/api/surfaces/spreadsheet/state"),
        api<SurfaceCommands>("/api/surfaces/spreadsheet/commands"),
        api<InferenceStatus>("/api/inference/status")
      ]);
      setBranding(nextBranding);
      setUsers(nextUsers);
      setWorkspaces(nextWorkspaces);
      setAgents(nextAgents);
      setApprovals(nextApprovals);
      setAuditEvents(nextAuditEvents);
      setSurface(nextSurface);
      setCommands(nextCommands.commands);
      setInferenceStatus(nextInferenceStatus);
    } catch (error) {
      if (error instanceof UnauthorizedError) {
        signOut();
        return;
      }
      throw error;
    }
  }, [signOut]);

  React.useEffect(() => {
    api<Branding>("/api/branding").then(setBranding).catch(() => undefined);
  }, []);

  React.useEffect(() => {
    if (!token) return;
    refresh();

    // Server-sent events over fetch so the Authorization header travels along;
    // any runtime event triggers a coarse refresh (state fetches are ETag-cheap).
    const controller = new AbortController();
    let closed = false;
    (async function subscribe(): Promise<void> {
      while (!closed) {
        try {
          const response = await fetch("/api/events", {
            headers: { Authorization: `Bearer ${readToken()}` },
            signal: controller.signal
          });
          if (response.status === 401 || !response.body) return;
          const reader = response.body.getReader();
          const decoder = new TextDecoder();
          for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            if (decoder.decode(value).includes("data:")) {
              await refresh();
            }
          }
        } catch {
          // connection dropped; retry after a pause
        }
        if (!closed) {
          await new Promise((resolve) => setTimeout(resolve, 3000));
        }
      }
    })();

    return () => {
      closed = true;
      controller.abort();
    };
  }, [token, refresh]);

  async function signIn() {
    const candidate = tokenDraft.trim();
    if (!candidate) return;
    window.localStorage.setItem(TOKEN_KEY, candidate);
    try {
      await api<PlatformUser[]>("/api/users");
      setLoginError(null);
      setTokenDraft("");
      setToken(candidate);
    } catch (error) {
      window.localStorage.removeItem(TOKEN_KEY);
      setLoginError(error instanceof UnauthorizedError ? "That token was rejected." : "The runtime is unreachable.");
    }
  }

  async function dispatch(command: SurfaceCommand) {
    const input: Record<string, string> = {};
    for (const key of Object.keys(command.input.properties ?? {})) {
      input[key] = commandInputs[key] ?? "";
    }
    try {
      const result = await api<CommandResult>(`/api/surfaces/spreadsheet/commands/${command.name}`, {
        method: "POST",
        body: JSON.stringify({ input })
      });
      setLastResult({ decision: result.decision, reason: result.reason });
      await refresh();
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else setLastResult({ decision: "Deny", reason: String(error) });
    }
  }

  async function resolve(approval: ApprovalView, action: "approve" | "reject") {
    try {
      const result = await api<CommandResult>(`/api/approvals/${approval.id}/${action}`, {
        method: "POST",
        body: JSON.stringify({ requestHash: approval.requestHash, observedRevision: surface?.revision ?? null })
      });
      setLastResult({ decision: result.decision, reason: result.reason });
      await refresh();
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else setLastResult({ decision: "Deny", reason: String(error) });
    }
  }

  async function askLocalModel() {
    const response = await api<ChatResponse>("/api/inference/chat", {
      method: "POST",
      body: JSON.stringify({
        messages: [{ role: "user", content: prompt }],
        temperature: 0.2,
        maxTokens: 256
      })
    });
    setChatResponse(response);
  }

  if (!token) {
    return (
      <main>
        <header className="topbar">
          <div>
            <h1>{branding.productName}</h1>
            <p>{branding.companyName}</p>
          </div>
        </header>
        <section className="login panel" data-automation-id="login">
          <h2>Session</h2>
          <p className="muted">
            Paste the human session token. Local development prints it when the runtime starts; inside a VM read{" "}
            <code>/var/lib/workspace-runtime/secrets/human.token</code>.
          </p>
          <div className="inline">
            <label>
              Session token
              <input
                data-automation-id="token-input"
                type="password"
                value={tokenDraft}
                onChange={(event) => setTokenDraft(event.target.value)}
                onKeyDown={(event) => event.key === "Enter" && signIn()}
              />
            </label>
            <button data-automation-id="token-submit" onClick={signIn}>
              <KeyRound size={16} /> Unlock
            </button>
          </div>
          {loginError && <p className="decision deny">{loginError}</p>}
        </section>
      </main>
    );
  }

  const selectedUser = users[0];
  const selectedAgent = agents[0];
  const pendingApprovals = approvals.filter((approval) => approval.status === "Pending");
  const cells = Object.entries(surface?.state.cells ?? {}).sort(([left], [right]) => left.localeCompare(right));

  return (
    <main>
      <header className="topbar">
        <div>
          <h1>{branding.productName}</h1>
          <p>{branding.companyName}</p>
        </div>
        <span className="status" data-automation-id="revision">
          <ShieldCheck size={18} /> rev {surface?.revision ?? 0}
        </span>
      </header>

      <section className="grid">
        <div className="panel">
          <h2>Runtime</h2>
          <dl>
            <dt>User</dt>
            <dd>{selectedUser?.displayName ?? "Loading"}</dd>
            <dt>Workspace</dt>
            <dd>{workspaces[0]?.name ?? "Loading"}</dd>
            <dt>Agent</dt>
            <dd>{selectedAgent?.name ?? branding.agentName}</dd>
            <dt>Inference</dt>
            <dd>{selectedAgent?.inferenceProvider ?? "Loading"}</dd>
          </dl>
        </div>

        <div className="panel">
          <h2>Surface Commands</h2>
          <p className="muted small">Rendered from the surface manifest — the same commands the agent sees.</p>
          <div className="inline">
            {Array.from(new Set(commands.flatMap((command) => Object.keys(command.input.properties ?? {}))))
              .map((key) => (
                <label key={key}>
                  {key}
                  <input
                    data-automation-id={`input-${key}`}
                    value={commandInputs[key] ?? ""}
                    onChange={(event) => setCommandInputs({ ...commandInputs, [key]: event.target.value })}
                  />
                </label>
              ))}
          </div>
          <div className="inline commandRow">
            {commands.map((command) => (
              <button
                key={command.name}
                data-automation-id={`cmd-${command.name}`}
                className={command.decision === "RequireApproval" ? "danger" : ""}
                title={command.reason}
                onClick={() => dispatch(command)}
              >
                {command.decision === "RequireApproval" ? <X size={16} /> : <Check size={16} />} {command.displayName}
              </button>
            ))}
          </div>
          {lastResult && (
            <p className={`decision ${lastResult.decision.toLowerCase()}`} data-automation-id="decision">
              {lastResult.decision}: {lastResult.reason}
            </p>
          )}
        </div>

        <div className="panel">
          <h2>Cells</h2>
          <div className="cells">
            {cells.length === 0 ? <span className="muted">No cells</span> : cells.map(([address, cellValue]) => (
              <div className="cell" key={address}>
                <strong>{address}</strong>
                <span>{cellValue}</span>
              </div>
            ))}
          </div>
        </div>

        <div className="panel">
          <h2>Approvals</h2>
          {pendingApprovals.length === 0 ? <p className="muted">No pending approvals</p> : pendingApprovals.map((approval) => (
            <div className="approval" key={approval.id} data-automation-id={`approval-${approval.id}`}>
              <p>
                <strong>
                  {approval.pendingRequest ? `${approval.pendingRequest.toolName}.${approval.pendingRequest.operation}` : "unknown"}
                </strong>{" "}
                — {approval.reason}
              </p>
              {approval.preview && (
                <div className="diff">
                  <p className="muted small">{approval.preview.summary}</p>
                  {approval.preview.changes.slice(0, 8).map((change) => (
                    <div className="diffRow" key={change.address}>
                      <strong>{change.address}</strong>
                      <span className="before">{change.before ?? "—"}</span>
                      <span className="arrow">→</span>
                      <span className="after">{change.after ?? "—"}</span>
                    </div>
                  ))}
                </div>
              )}
              <p className="hash muted small">binds to {approval.requestHash.slice(0, 12)}…</p>
              <div className="actions">
                <button data-automation-id="approve" onClick={() => resolve(approval, "approve")}><Check size={16} /> Approve</button>
                <button data-automation-id="reject" className="danger" onClick={() => resolve(approval, "reject")}><X size={16} /> Reject</button>
              </div>
            </div>
          ))}
        </div>

        <div className="panel">
          <h2>Local Inference</h2>
          <dl>
            <dt>Provider</dt>
            <dd>{inferenceStatus?.activeProvider.displayName ?? "Loading"}</dd>
            <dt>Runtime</dt>
            <dd>{inferenceStatus?.activeProvider.runtime.engine ?? "Loading"}</dd>
            <dt>Endpoint</dt>
            <dd>{inferenceStatus?.stableEndpoint ?? "Loading"}</dd>
          </dl>
          <div className="promptBox">
            <textarea value={prompt} onChange={(event) => setPrompt(event.target.value)} />
            <button onClick={askLocalModel}><ShieldCheck size={16} /> Ask Local</button>
          </div>
          {chatResponse && (
            <p className={`decision ${chatResponse.forwarded ? "allow" : "deny"}`}>
              {chatResponse.forwarded ? chatResponse.content : chatResponse.error}
            </p>
          )}
        </div>
      </section>

      <section className="panel audit">
        <h2>Audit Log</h2>
        {auditEvents.slice(0, 8).map((event) => (
          <article key={event.id}>
            <time>{new Date(event.occurredAt).toLocaleString()}</time>
            <strong>{event.action}</strong>
            <span className={event.outcome === "Success" ? "allow" : event.outcome === "Blocked" ? "deny" : "hold"}>
              {event.outcome}
            </span>
            <span className="principal">{event.principal ?? ""}</span>
            <p>{event.detail}</p>
          </article>
        ))}
      </section>
    </main>
  );
}

createRoot(document.getElementById("root")!).render(<App />);
