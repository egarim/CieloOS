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
type AuditEvent = { id: string; occurredAt: string; action: string; outcome: string; detail: string; principal?: string | null; onBehalfOf?: string | null };
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
type SessionView = { id: string; owner: string; profile: string; status: string; viewportPort: number; kind: string };
type HomeEntry = { name: string; kind: string; size: number; modifiedEpoch: number };
type HomeListing = { owner: string; path: string; entries: HomeEntry[] };
type HomeFile = { owner: string; path: string; content: string; truncated: boolean; size: number };
type Whoami = { slug: string; display: string; kind: string; homes: string[] };

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
  const [sessions, setSessions] = React.useState<SessionView[]>([]);
  const [sessionsAvailable, setSessionsAvailable] = React.useState(true);
  const [newDesktopOwner, setNewDesktopOwner] = React.useState("joche");
  const [newDesktopProfile, setNewDesktopProfile] = React.useState("agent-console");
  const [whoami, setWhoami] = React.useState<Whoami | null>(null);
  const [filesOwner, setFilesOwner] = React.useState("joche");
  const [filesPath, setFilesPath] = React.useState("");
  const [listing, setListing] = React.useState<HomeListing | null>(null);
  const [filePreview, setFilePreview] = React.useState<HomeFile | null>(null);
  const [filesError, setFilesError] = React.useState<string | null>(null);

  const signOut = React.useCallback(() => {
    window.localStorage.removeItem(TOKEN_KEY);
    setToken(null);
  }, []);

  // Session listing is fetched separately: it shells out to the container
  // backend and can be slower than the in-memory surface reads, and a backend
  // without the session surface should not break the rest of the panel.
  const refreshSessions = React.useCallback(async () => {
    try {
      setSessions(await api<SessionView[]>("/api/sessions"));
      setSessionsAvailable(true);
    } catch (error) {
      if (error instanceof UnauthorizedError) {
        signOut();
        return;
      }
      setSessionsAvailable(false);
    }
  }, [signOut]);

  const refresh = React.useCallback(async () => {
    // Each endpoint is applied independently: one failing (e.g. inference
    // status when no local model is configured) must not blank the whole panel.
    let unauthorized = false;
    const load = async <T,>(path: string, apply: (value: T) => void) => {
      try {
        apply(await api<T>(path));
      } catch (error) {
        if (error instanceof UnauthorizedError) unauthorized = true;
      }
    };

    await Promise.all([
      load<Branding>("/api/branding", setBranding),
      load<PlatformUser[]>("/api/users", setUsers),
      load<Workspace[]>("/api/workspaces", setWorkspaces),
      load<AgentProfile[]>("/api/agents", setAgents),
      load<ApprovalView[]>("/api/approvals", setApprovals),
      load<AuditEvent[]>("/api/audit-events", setAuditEvents),
      load<SurfaceState>("/api/surfaces/spreadsheet/state", setSurface),
      load<SurfaceCommands>("/api/surfaces/spreadsheet/commands", (value) => setCommands(value.commands)),
      load<InferenceStatus>("/api/inference/status", setInferenceStatus),
      load<Whoami>("/api/whoami", setWhoami)
    ]);

    if (unauthorized) signOut();
  }, [signOut]);

  React.useEffect(() => {
    api<Branding>("/api/branding").then(setBranding).catch(() => undefined);
  }, []);

  React.useEffect(() => {
    if (!token) return;
    refresh();
    refreshSessions();
    browseHome(filesOwner, "");

    // Server-sent events over fetch so the Authorization header travels along;
    // any runtime event triggers a coarse refresh (state fetches are ETag-cheap).
    const controller = new AbortController();
    let closed = false;
    (async function subscribe(): Promise<void> {
      while (!closed) {
        try {
          const response = await fetch("/api/events", {
            headers: { Authorization: `Bearer ${readToken() ?? ""}` },
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
              await refreshSessions();
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

  // Once we know who is signed in, default the file browser to their own home.
  React.useEffect(() => {
    if (whoami) {
      setFilesOwner(whoami.slug);
      browseHome(whoami.slug, "");
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [whoami?.slug]);

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

  // Desktop lifecycle rides the same command bus as everything else: create is
  // Allow, destroy is RequireApproval, so it surfaces in the approvals feed.
  async function sessionCommand(name: "create" | "destroy" | "inhabit", input: Record<string, string>) {
    try {
      const result = await api<CommandResult>(`/api/surfaces/session/commands/${name}`, {
        method: "POST",
        body: JSON.stringify({ input })
      });
      setLastResult({ decision: result.decision, reason: result.reason });
      await refresh();
      await refreshSessions();
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else setLastResult({ decision: "Deny", reason: String(error) });
    }
  }

  async function browseHome(owner: string, path: string) {
    setFilePreview(null);
    try {
      const query = path ? `?path=${encodeURIComponent(path)}` : "";
      const next = await api<HomeListing>(`/api/home/${encodeURIComponent(owner)}/list${query}`);
      setListing(next);
      setFilesOwner(owner);
      setFilesPath(path);
      setFilesError(null);
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else {
        setListing(null);
        setFilesError(`No home volume yet for '${owner}'. Create a session for that owner to provision one.`);
      }
    }
  }

  async function openHomeEntry(entry: HomeEntry) {
    const childPath = filesPath ? `${filesPath}/${entry.name}` : entry.name;
    if (entry.kind === "directory") {
      await browseHome(filesOwner, childPath);
      return;
    }
    try {
      setFilePreview(await api<HomeFile>(`/api/home/${encodeURIComponent(filesOwner)}/read?path=${encodeURIComponent(childPath)}`));
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
    }
  }

  function homeCrumbs(): { label: string; path: string }[] {
    const crumbs = [{ label: "~", path: "" }];
    let acc = "";
    for (const part of filesPath.split("/").filter(Boolean)) {
      acc = acc ? `${acc}/${part}` : part;
      crumbs.push({ label: part, path: acc });
    }
    return crumbs;
  }

  // Inhabiting is the governed way to take a seat at an owned agent's session:
  // it records a dual-actor audit entry (you, on behalf of the agent) before
  // opening the viewport.
  async function inhabit(session: SessionView, mode: "shadow" | "become") {
    await sessionCommand("inhabit", { id: session.id, mode });
    watchDesktop(session);
  }

  function watchDesktop(session: SessionView) {
    // Dev topology: the viewport is forwarded to the same host the panel runs
    // on. The production path is a runtime-proxied /api/sessions/{id}/view so a
    // session is reachable only through the authenticated runtime (V0.5).
    window.open(`http://${window.location.hostname}:${session.viewportPort}/`, "_blank", "noopener");
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
            Paste a session token. Local development prints joche's when the runtime starts; each identity has its own at{" "}
            <code>.data/secrets/&lt;slug&gt;.token</code> (joche, yulia, …).
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
        <span className="status" data-automation-id="whoami">
          <ShieldCheck size={18} /> {whoami ? `${whoami.display} · ${whoami.kind.toLowerCase()}` : "…"}
          <button className="signout" data-automation-id="signout" onClick={signOut}>sign out</button>
        </span>
      </header>

      <section className="grid">
        <div className="panel">
          <h2>Signed in</h2>
          <dl>
            <dt>Identity</dt>
            <dd>{whoami?.display ?? "Loading"}</dd>
            <dt>Slug</dt>
            <dd>{whoami?.slug ?? "Loading"}</dd>
            <dt>Homes</dt>
            <dd>{whoami?.homes.join(", ") ?? "Loading"}</dd>
            <dt>Revision</dt>
            <dd>{surface?.revision ?? 0}</dd>
          </dl>
        </div>

        <div className="panel desktops" data-automation-id="desktops">
          <h2>Sessions</h2>
          <p className="muted small">A console or desktop over the owner's home. Create is allowed; destroy requires approval.</p>
          {sessionsAvailable ? (
            <>
              <div className="inline">
                <label>
                  owner
                  <input
                    data-automation-id="desktop-owner"
                    value={newDesktopOwner}
                    onChange={(event) => setNewDesktopOwner(event.target.value)}
                  />
                </label>
                <label>
                  profile
                  <select
                    data-automation-id="desktop-profile"
                    value={newDesktopProfile}
                    onChange={(event) => setNewDesktopProfile(event.target.value)}
                  >
                    <option value="agent-console">agent-console</option>
                    <option value="human-console">human-console</option>
                    <option value="agent-desktop">agent-desktop</option>
                    <option value="human-desktop">human-desktop</option>
                  </select>
                </label>
                <button
                  data-automation-id="desktop-create"
                  onClick={() => sessionCommand("create", { owner: newDesktopOwner, profile: newDesktopProfile })}
                >
                  <Check size={16} /> New session
                </button>
              </div>
              <div className="sessionList">
                {sessions.length === 0 ? (
                  <p className="muted">No sessions running</p>
                ) : (
                  sessions.map((session) => (
                    <div className="sessionCard" key={session.id} data-automation-id={`desktop-${session.id}`}>
                      <div className="sessionMeta">
                        <strong>{session.id}</strong>
                        <span className="tag">{session.kind}</span>
                        <span className="tag">{session.owner}</span>
                        <span className={session.status === "running" ? "allow" : "muted"}>{session.status}</span>
                      </div>
                      <div className="actions">
                        {whoami?.homes.includes(session.owner) ? (
                          <>
                            <button
                              data-automation-id={`shadow-${session.id}`}
                              disabled={session.status !== "running" || !session.viewportPort}
                              onClick={() => inhabit(session, "shadow")}
                            >
                              <ShieldCheck size={16} /> Shadow
                            </button>
                            <button
                              data-automation-id={`become-${session.id}`}
                              disabled={session.status !== "running" || !session.viewportPort}
                              onClick={() => inhabit(session, "become")}
                            >
                              <KeyRound size={16} /> Become
                            </button>
                          </>
                        ) : (
                          <button
                            disabled={session.status !== "running" || !session.viewportPort}
                            onClick={() => watchDesktop(session)}
                          >
                            <ShieldCheck size={16} /> Watch
                          </button>
                        )}
                        <button
                          className="danger"
                          data-automation-id={`destroy-${session.id}`}
                          onClick={() => sessionCommand("destroy", { id: session.id })}
                        >
                          <X size={16} /> Destroy
                        </button>
                      </div>
                    </div>
                  ))
                )}
              </div>
            </>
          ) : (
            <p className="muted">The session surface is not available on this runtime.</p>
          )}
        </div>

        <div className="panel files" data-automation-id="files">
          <h2>Files — {filesOwner}'s home</h2>
          <p className="muted small">The agent's persistent home. It outlives sessions; this is where its work lives.</p>
          <div className="inline">
            <label>
              owner
              <input
                data-automation-id="files-owner"
                value={filesOwner}
                onChange={(event) => setFilesOwner(event.target.value)}
                onKeyDown={(event) => event.key === "Enter" && browseHome(filesOwner, "")}
              />
            </label>
            <button data-automation-id="files-browse" onClick={() => browseHome(filesOwner, "")}>
              <ShieldCheck size={16} /> Browse
            </button>
          </div>
          <div className="crumbs">
            {homeCrumbs().map((crumb, index) => (
              <span key={crumb.path}>
                {index > 0 && <span className="sep">/</span>}
                <button className="crumb" onClick={() => browseHome(filesOwner, crumb.path)}>{crumb.label}</button>
              </span>
            ))}
          </div>
          {filesError && <p className="muted small">{filesError}</p>}
          {listing && (
            <div className="fileTree">
              {listing.entries.length === 0 ? (
                <p className="muted">Empty</p>
              ) : (
                listing.entries.map((entry) => (
                  <button
                    key={entry.name}
                    className="fileRow"
                    data-automation-id={`file-${entry.name}`}
                    onClick={() => openHomeEntry(entry)}
                  >
                    <span className="fileKind">{entry.kind === "directory" ? "▸" : "·"}</span>
                    <span className="fileName">{entry.name}</span>
                    <span className="fileSize">{entry.kind === "directory" ? "" : `${entry.size} B`}</span>
                  </button>
                ))
              )}
            </div>
          )}
          {filePreview && (
            <div className="filePreview">
              <p className="muted small">{filePreview.path}{filePreview.truncated ? " (truncated)" : ""}</p>
              <pre>{filePreview.content}</pre>
            </div>
          )}
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
            <span className="principal">{event.principal ?? ""}{event.onBehalfOf ? ` → ${event.onBehalfOf}` : ""}</span>
            <p>{event.detail}</p>
          </article>
        ))}
      </section>
    </main>
  );
}

createRoot(document.getElementById("root")!).render(<App />);
