import React from "react";
import { createRoot } from "react-dom/client";
import { Bot, Check, Cpu, KeyRound, Loader2, ShieldCheck, Terminal, User, X } from "lucide-react";
import "./styles.css";

type Branding = {
  productName: string;
  shortName: string;
  companyName: string;
  supportName: string;
  agentName: string;
};

type PlatformUser = { id: string; displayName: string; email: string; slug?: string };
type Workspace = { id: string; ownerUserId: string; name: string };
type AgentProfile = { id: string; ownerUserId: string; workspaceId: string; name: string; slug?: string; inferenceProvider: string; grantedTools: string[] };
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
type SessionView = { id: string; owner: string; profile: string; status: string; viewportPort: number; kind: string };
type HomeEntry = { name: string; kind: string; size: number; modifiedEpoch: number };
type HomeListing = { owner: string; path: string; entries: HomeEntry[] };
type HomeFile = { owner: string; path: string; content: string; truncated: boolean; size: number };
type Whoami = { slug: string; display: string; kind: string; homes: string[] };
type ConsoleView = { sessionId: string; screen: string; available: boolean; detail?: string | null };
type LoopStep = { step: number; text: string | null; submit: boolean; done: boolean; note?: string | null; decision: string; reason: string };
type AgentRunResult = { sessionId: string; goal: string; completed: boolean; stopReason: string; steps: LoopStep[] };

type Desk = { slug: string; label: string; isSelf: boolean };

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
  const [agents, setAgents] = React.useState<AgentProfile[]>([]);
  const [approvals, setApprovals] = React.useState<ApprovalView[]>([]);
  const [auditEvents, setAuditEvents] = React.useState<AuditEvent[]>([]);
  const [surface, setSurface] = React.useState<SurfaceState | null>(null);
  const [commands, setCommands] = React.useState<SurfaceCommand[]>([]);
  const [commandInputs, setCommandInputs] = React.useState<Record<string, string>>(inputDefaults);
  const [inferenceStatus, setInferenceStatus] = React.useState<InferenceStatus | null>(null);
  const [lastResult, setLastResult] = React.useState<{ decision: string; reason: string } | null>(null);
  const [sessions, setSessions] = React.useState<SessionView[]>([]);
  const [sessionsAvailable, setSessionsAvailable] = React.useState(true);
  const [newSessionProfile, setNewSessionProfile] = React.useState("agent-console");
  const [whoami, setWhoami] = React.useState<Whoami | null>(null);
  const [selectedDesk, setSelectedDesk] = React.useState<string | null>(null);
  const [view, setView] = React.useState<"desk" | "surfaces">("desk");
  const [filesOwner, setFilesOwner] = React.useState<string>("");
  const [filesPath, setFilesPath] = React.useState("");
  const [listing, setListing] = React.useState<HomeListing | null>(null);
  const [filePreview, setFilePreview] = React.useState<HomeFile | null>(null);
  const [filesError, setFilesError] = React.useState<string | null>(null);
  const [consoleScreen, setConsoleScreen] = React.useState<string>("");
  const [agentGoal, setAgentGoal] = React.useState("");
  const [agentRunning, setAgentRunning] = React.useState(false);
  const [agentRun, setAgentRun] = React.useState<AgentRunResult | null>(null);

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

  // Read a console session's live screen (the agent's terminal), for the watch view.
  const observeConsole = React.useCallback(async (id: string) => {
    try {
      const view = await api<ConsoleView>(`/api/sessions/${encodeURIComponent(id)}/console`);
      setConsoleScreen(view.available ? view.screen : view.detail ?? "console unavailable");
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
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
  }, [token, refresh, refreshSessions]);

  // Once we know who is signed in, land on a desk: prefer the first owned agent,
  // since the whole point is to make the agent's work visible. Fall back to self.
  React.useEffect(() => {
    if (!whoami) return;
    setSelectedDesk((previous) => {
      if (previous && whoami.homes.includes(previous)) return previous;
      const firstAgent = whoami.homes.find((home) => home !== whoami.slug);
      return firstAgent ?? whoami.homes[0] ?? null;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [whoami?.slug, whoami?.homes.join(",")]);

  // Switching desks re-anchors the file browser on that principal's home and
  // picks a sensible default session profile (a person gets a human session; an
  // agent gets an agent session).
  React.useEffect(() => {
    if (!selectedDesk) return;
    browseHome(selectedDesk, "");
    setNewSessionProfile(selectedDesk === whoami?.slug ? "human-console" : "agent-console");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedDesk]);

  // Poll the selected desk's running console screen so the watch view stays live.
  React.useEffect(() => {
    if (!token || !selectedDesk) return;
    const consoleSession = sessions.find(
      (session) => session.owner === selectedDesk && session.kind === "console" && session.status === "running");
    if (!consoleSession) {
      setConsoleScreen("");
      return;
    }
    observeConsole(consoleSession.id);
    const timer = setInterval(() => observeConsole(consoleSession.id), 2500);
    return () => clearInterval(timer);
  }, [token, selectedDesk, sessions, observeConsole]);

  async function signIn() {
    const candidate = tokenDraft.trim();
    if (!candidate) return;
    window.localStorage.setItem(TOKEN_KEY, candidate);
    try {
      await api<Whoami>("/api/whoami");
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

  // Session lifecycle rides the same command bus as everything else: create is
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
        setFilesOwner(owner);
        setFilesPath(path);
        setFilesError(`No home volume yet for '${owner}'. Open a session for this desk to provision one.`);
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

  // Hand the agent a goal; it drives its own console through the policy-checked
  // bus (each keystroke audited) and returns the step transcript.
  async function runAgent(id: string) {
    const goal = agentGoal.trim();
    if (!goal || agentRunning) return;
    setAgentRunning(true);
    setAgentRun(null);
    try {
      const result = await api<AgentRunResult>(`/api/sessions/${encodeURIComponent(id)}/agent-run`, {
        method: "POST",
        body: JSON.stringify({ goal, maxSteps: 8 })
      });
      setAgentRun(result);
      await observeConsole(id);
      await refresh();
      await refreshSessions();
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else setAgentRun({ sessionId: id, goal, completed: false, stopReason: String(error), steps: [] });
    } finally {
      setAgentRunning(false);
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

  const desks: Desk[] = (whoami?.homes ?? []).map((slug) => {
    const isSelf = slug === whoami?.slug;
    const agent = agents.find((candidate) => candidate.slug === slug);
    const label = isSelf ? whoami?.display ?? slug : agent?.name ?? slug;
    return { slug, label, isSelf };
  });
  const desk = desks.find((candidate) => candidate.slug === selectedDesk) ?? null;
  const deskSessions = sessions.filter((session) => session.owner === selectedDesk);
  const deskConsole = deskSessions.find((session) => session.kind === "console" && session.status === "running") ?? null;
  const deskAudit = auditEvents
    .filter((event) => event.principal === selectedDesk || event.onBehalfOf === selectedDesk)
    .slice(0, 8);
  const pendingApprovals = approvals.filter((approval) => approval.status === "Pending");
  const cells = Object.entries(surface?.state.cells ?? {}).sort(([left], [right]) => left.localeCompare(right));

  return (
    <main>
      <header className="topbar">
        <div>
          <h1>{branding.productName}</h1>
          <p>{branding.companyName}</p>
        </div>
        <div className="topbarRight">
          {inferenceStatus && (
            <span className="modelStatus" title={inferenceStatus.stableEndpoint}>
              <Cpu size={13} /> {inferenceStatus.activeProvider.displayName} · {inferenceStatus.activeProvider.runtime.engine}
            </span>
          )}
          <span className="status" data-automation-id="whoami">
            <ShieldCheck size={18} /> {whoami ? `${whoami.display} · ${whoami.kind.toLowerCase()}` : "…"}
            <button className="signout" data-automation-id="signout" onClick={signOut}>sign out</button>
          </span>
        </div>
      </header>

      <nav className="viewNav">
        <button className={view === "desk" ? "active" : ""} onClick={() => setView("desk")}>Desks</button>
        <button className={view === "surfaces" ? "active" : ""} onClick={() => setView("surfaces")}>Surface playground</button>
        <span className="viewNavHint">conformance demo</span>
      </nav>

      {view === "surfaces" ? (
        <section className="grid">
          <div className="panel">
            <h2>Surface Commands</h2>
            <p className="muted small">
              A worked example, not a feature: the spreadsheet surface is driven through the same manifest-checked
              command bus an agent uses. It proves a typed surface, a human click, and an agent call all pass one policy.
            </p>
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
        </section>
      ) : (
        <section className="workspace">
          <aside className="desks" data-automation-id="desks-rail">
            <h2>Your desks</h2>
            <p className="muted small">You, and the agents you own. Pick one to see its home, sessions, and activity.</p>
            <div className="deskList">
              {desks.length === 0 ? (
                <p className="muted small">Loading…</p>
              ) : (
                desks.map((entry) => (
                  <button
                    key={entry.slug}
                    className={`deskItem${entry.slug === selectedDesk ? " selected" : ""}`}
                    data-automation-id={`desk-${entry.slug}`}
                    onClick={() => setSelectedDesk(entry.slug)}
                  >
                    <span className="deskKind">
                      {entry.isSelf ? <User size={12} /> : <Bot size={12} />} {entry.isSelf ? "you" : "agent"}
                    </span>
                    <span className="deskName">{entry.label}</span>
                    <span className="deskSub">{entry.slug}</span>
                  </button>
                ))
              )}
            </div>
          </aside>

          <div className="deskMain">
            <div className="panel approvalsPanel" data-automation-id="approvals">
              <h2>Approvals — awaiting your consent</h2>
              <p className="muted small">Actions any of your agents parked for a decision. Each binds to the exact request it previews.</p>
              {pendingApprovals.length === 0 ? (
                <p className="muted">Nothing pending.</p>
              ) : (
                pendingApprovals.map((approval) => (
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
                ))
              )}
            </div>

            {desk && (
              <div className="deskHeader">
                <h2>
                  {desk.isSelf ? <User size={18} /> : <Bot size={18} />} {desk.label}
                  <span className="deskTag">{desk.isSelf ? "your desk" : "agent you own"}</span>
                </h2>
                <p className="muted small">
                  Home volume <code>lunos-home-{desk.slug}</code> — it outlives every session; this is where this desk's work lives.
                </p>
              </div>
            )}

            <div className="deskGrid">
              <div className="panel files" data-automation-id="files">
                <h2>Home — {filesOwner || selectedDesk}</h2>
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

              <div className="panel desktops" data-automation-id="desktops">
                <h2>Sessions</h2>
                <p className="muted small">A console or desktop over this desk's home. Create is allowed; destroy asks for your approval.</p>
                {sessionsAvailable ? (
                  <>
                    <div className="inline">
                      <label>
                        profile
                        <select
                          data-automation-id="desktop-profile"
                          value={newSessionProfile}
                          onChange={(event) => setNewSessionProfile(event.target.value)}
                        >
                          <option value="agent-console">agent-console</option>
                          <option value="human-console">human-console</option>
                          <option value="agent-desktop">agent-desktop</option>
                          <option value="human-desktop">human-desktop</option>
                        </select>
                      </label>
                      <button
                        data-automation-id="desktop-create"
                        disabled={!selectedDesk}
                        onClick={() => selectedDesk && sessionCommand("create", { owner: selectedDesk, profile: newSessionProfile })}
                      >
                        <Check size={16} /> Open here
                      </button>
                    </div>
                    <div className="sessionList">
                      {deskSessions.length === 0 ? (
                        <p className="muted">No sessions on this desk.</p>
                      ) : (
                        deskSessions.map((session) => (
                          <div className="sessionCard" key={session.id} data-automation-id={`desktop-${session.id}`}>
                            <div className="sessionMeta">
                              <strong>{session.id}</strong>
                              <span className="tag">{session.kind}</span>
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
            </div>

            {deskConsole && (
              <div className="panel console" data-automation-id="console">
                <h2><Terminal size={13} /> Console — give the agent a task</h2>
                <p className="muted small">
                  The agent drives its own console toward your goal — each keystroke policy-checked and audited. Watch it below.
                </p>
                <div className="inline agentTask">
                  <input
                    data-automation-id="agent-goal"
                    className="agentGoal"
                    value={agentGoal}
                    placeholder="e.g. search the web for the top 10 posts about El Salvador and save them as an Excel file"
                    onChange={(event) => setAgentGoal(event.target.value)}
                    onKeyDown={(event) => event.key === "Enter" && runAgent(deskConsole.id)}
                  />
                  <button
                    data-automation-id="agent-run"
                    disabled={agentRunning || !agentGoal.trim()}
                    onClick={() => runAgent(deskConsole.id)}
                  >
                    {agentRunning ? <><Loader2 size={16} className="spin" /> Working…</> : <><Bot size={16} /> Run task</>}
                  </button>
                </div>
                <pre className="terminal" data-automation-id="console-screen">{consoleScreen || "(screen empty)"}</pre>
                {agentRun && (
                  <div className="agentRun">
                    <p className={`decision ${agentRun.completed ? "allow" : "deny"}`}>
                      {agentRun.completed ? "Completed" : "Stopped"}: {agentRun.stopReason}
                    </p>
                    {agentRun.steps.map((entry) => (
                      <div className="agentStep" key={entry.step}>
                        <span className="tag">{entry.done ? "done" : entry.decision}</span>
                        <code>{entry.done ? entry.note ?? "done" : entry.text}</code>
                        {entry.note && !entry.done && <span className="muted small">{entry.note}</span>}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )}

            <div className="panel audit" data-automation-id="activity">
              <h2>Activity — {selectedDesk}</h2>
              {deskAudit.length === 0 ? (
                <p className="muted">No activity recorded for this desk yet.</p>
              ) : (
                deskAudit.map((event) => (
                  <article key={event.id}>
                    <time>{new Date(event.occurredAt).toLocaleString()}</time>
                    <strong>{event.action}</strong>
                    <span className={event.outcome === "Success" ? "allow" : event.outcome === "Blocked" ? "deny" : "hold"}>
                      {event.outcome}
                    </span>
                    <span className="principal">{event.principal ?? ""}{event.onBehalfOf ? ` → ${event.onBehalfOf}` : ""}</span>
                    <p>{event.detail}</p>
                  </article>
                ))
              )}
            </div>
          </div>
        </section>
      )}
    </main>
  );
}

createRoot(document.getElementById("root")!).render(<App />);
