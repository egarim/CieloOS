import React from "react";
import { createRoot } from "react-dom/client";
import { ArrowRight, Bot, Check, Cpu, Download, KeyRound, Loader2, ShieldCheck, Terminal, User, UserPlus, X } from "lucide-react";
import "./styles.css";

type Branding = {
  productName: string;
  shortName: string;
  companyName: string;
  supportName: string;
  agentName: string;
  chatUrl: string;
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
type HomeFile = { owner: string; path: string; content: string; truncated: boolean; size: number; binary: boolean };
type Whoami = { slug: string; display: string; kind: string; homes: string[] };
type ConsoleView = { sessionId: string; screen: string; available: boolean; detail?: string | null };
type LoopStep = { step: number; text: string | null; submit: boolean; done: boolean; note?: string | null; decision: string; reason: string };
type AgentRunResult = { sessionId: string; goal: string; completed: boolean; stopReason: string; steps: LoopStep[] };

type Desk = { slug: string; label: string; isSelf: boolean };

type ModelProvider = {
  id: string;
  displayName: string;
  kind: string;
  baseUrl: string;
  model: string;
  capabilities: string[];
  locality: string;
  hasKey: boolean;
  managed: boolean;
};
type ModelsData = { providers: ModelProvider[]; defaults: { chat: string | null; vision: string | null } };
type ProviderForm = {
  preset: string;
  displayName: string;
  kind: string;
  baseUrl: string;
  model: string;
  apiKey: string;
  capabilities: string[];
  locality: string;
  defaultChat: boolean;
  defaultVision: boolean;
};

// Presets prefill the add-provider form; every one speaks the OpenAI chat format
// (Bearer + /chat/completions), which is what the model brain calls.
const PROVIDER_PRESETS: Record<string, { label: string; kind: string; baseUrl: string; model: string; locality: string; capabilities: string[] }> = {
  deepseek: { label: "DeepSeek", kind: "openai-compatible", baseUrl: "https://api.deepseek.com", model: "deepseek-chat", locality: "cloud", capabilities: ["chat"] },
  azure: { label: "Azure OpenAI", kind: "azure-openai", baseUrl: "https://<resource>.openai.azure.com/openai/v1", model: "gpt-4.1-mini", locality: "cloud", capabilities: ["chat", "vision"] },
  openai: { label: "OpenAI-compatible", kind: "openai-compatible", baseUrl: "https://api.openai.com/v1", model: "gpt-4o-mini", locality: "cloud", capabilities: ["chat", "vision"] },
  ollama: { label: "Ollama (local)", kind: "ollama", baseUrl: "http://127.0.0.1:11434/v1", model: "llama3.1", locality: "on-box", capabilities: ["chat"] }
};

const emptyProviderForm: ProviderForm = {
  preset: "deepseek",
  displayName: "DeepSeek",
  kind: "openai-compatible",
  baseUrl: "https://api.deepseek.com",
  model: "deepseek-chat",
  apiKey: "",
  capabilities: ["chat"],
  locality: "cloud",
  defaultChat: true,
  defaultVision: false
};

const emptyBranding: Branding = {
  productName: "Workspace Runtime",
  shortName: "Runtime",
  companyName: "Workspace Runtime Labs",
  supportName: "Support",
  agentName: "Assistant",
  chatUrl: ""
};

const TOKEN_KEY = "runtime.token";

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
  // First-run setup: null while we ask the runtime whether it has an owner yet.
  const [setupClaimed, setSetupClaimed] = React.useState<boolean | null>(null);
  const [setupName, setSetupName] = React.useState("");
  const [setupError, setSetupError] = React.useState<string | null>(null);
  const [setupBusy, setSetupBusy] = React.useState(false);
  const [agents, setAgents] = React.useState<AgentProfile[]>([]);
  const [approvals, setApprovals] = React.useState<ApprovalView[]>([]);
  const [auditEvents, setAuditEvents] = React.useState<AuditEvent[]>([]);
  const [spreadsheetRevision, setSpreadsheetRevision] = React.useState<number | null>(null);
  const [lastResult, setLastResult] = React.useState<{ decision: string; reason: string } | null>(null);
  const [sessions, setSessions] = React.useState<SessionView[]>([]);
  const [sessionsAvailable, setSessionsAvailable] = React.useState(true);
  const [newSessionProfile, setNewSessionProfile] = React.useState("agent-console");
  const [whoami, setWhoami] = React.useState<Whoami | null>(null);
  const [selectedDesk, setSelectedDesk] = React.useState<string | null>(null);
  const [view, setView] = React.useState<"desk" | "models">("desk");
  const [models, setModels] = React.useState<ModelsData | null>(null);
  const [providerForm, setProviderForm] = React.useState<ProviderForm>(emptyProviderForm);
  const [modelsMsg, setModelsMsg] = React.useState<{ kind: "ok" | "err"; text: string } | null>(null);
  const [modelsBusy, setModelsBusy] = React.useState(false);
  const [teammateOpen, setTeammateOpen] = React.useState(false);
  const [teammateName, setTeammateName] = React.useState("");
  const [teammateResult, setTeammateResult] = React.useState<{ slug: string; token: string } | null>(null);
  const [teammateError, setTeammateError] = React.useState<string | null>(null);
  const [teammateBusy, setTeammateBusy] = React.useState(false);
  const [filesOwner, setFilesOwner] = React.useState<string>("");
  const [filesPath, setFilesPath] = React.useState("");
  const [listing, setListing] = React.useState<HomeListing | null>(null);
  const [filePreview, setFilePreview] = React.useState<HomeFile | null>(null);
  const [filesError, setFilesError] = React.useState<string | null>(null);
  const [filesMode, setFilesMode] = React.useState<"home" | "shared">("home");
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
      // Only the revision is kept: approving a spreadsheet change sends it back so
      // the runtime can refuse a decision made against a sheet that has since moved.
      load<SurfaceState>("/api/surfaces/spreadsheet/state", (value) => setSpreadsheetRevision(value.revision)),
      load<ModelsData>("/api/models", setModels),
      load<Whoami>("/api/whoami", setWhoami)
    ]);

    if (unauthorized) signOut();
  }, [signOut]);

  React.useEffect(() => {
    api<Branding>("/api/branding").then(setBranding).catch(() => undefined);
  }, []);

  // While signed out, ask whether this machine has an owner yet. Unclaimed →
  // show the first-run wizard instead of the token-login screen. Public route,
  // no token required.
  React.useEffect(() => {
    if (token) return;
    let alive = true;
    fetch("/api/setup/status")
      .then((response) => (response.ok ? response.json() : null))
      .then((data: { claimed: boolean } | null) => {
        if (alive && data) setSetupClaimed(Boolean(data.claimed));
      })
      .catch(() => alive && setSetupClaimed(true)); // unreachable → fall back to login
    return () => {
      alive = false;
    };
  }, [token]);

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
    setFilesMode("home");
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

  // First-run claim: create THIS machine's owner. On success the returned bearer
  // token is stored and we drop straight into the signed-in app (no separate
  // login step). Loopback-only and single-winner are enforced by the runtime.
  async function createOwner() {
    const name = setupName.trim();
    if (!name || setupBusy) return;
    setSetupBusy(true);
    setSetupError(null);
    try {
      const response = await fetch("/api/setup/claim", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name })
      });
      if (response.ok) {
        const data = (await response.json()) as { slug: string; token: string };
        window.localStorage.setItem(TOKEN_KEY, data.token);
        setToken(data.token);
        return;
      }
      if (response.status === 403) {
        setSetupError("Setup can only run on the machine itself. Open this panel on the box, or over your SSH tunnel.");
      } else if (response.status === 409) {
        setSetupClaimed(true); // someone else just claimed it — fall back to login
        setSetupError("This machine already has an owner. Sign in with a token instead.");
      } else {
        let message = "Could not create the account.";
        try {
          message = ((await response.json()) as { error?: string }).error ?? message;
        } catch {
          // non-JSON error body; keep the default
        }
        setSetupError(message);
      }
    } catch {
      setSetupError("The runtime is unreachable.");
    } finally {
      setSetupBusy(false);
    }
  }

  // Pull a human-readable error out of an api() failure (it throws the raw
  // response body, which is JSON {error}).
  function extractError(error: unknown): string {
    const raw = error instanceof Error ? error.message : String(error);
    try {
      return (JSON.parse(raw) as { error?: string }).error ?? raw;
    } catch {
      return raw;
    }
  }

  const refreshModels = React.useCallback(async () => {
    try {
      setModels(await api<ModelsData>("/api/models"));
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
    }
  }, [signOut]);

  function applyPreset(key: string) {
    const preset = PROVIDER_PRESETS[key];
    if (!preset) return;
    setProviderForm((form) => ({
      ...form,
      preset: key,
      displayName: preset.label,
      kind: preset.kind,
      baseUrl: preset.baseUrl,
      model: preset.model,
      locality: preset.locality,
      capabilities: preset.capabilities,
      defaultVision: preset.capabilities.includes("vision") ? form.defaultVision : false
    }));
  }

  function toggleCapability(capability: string) {
    setProviderForm((form) => ({
      ...form,
      capabilities: form.capabilities.includes(capability)
        ? form.capabilities.filter((value) => value !== capability)
        : [...form.capabilities, capability]
    }));
  }

  // Add a model provider from the panel. On success it is usable immediately —
  // no restart — because the registry reads the provider store live.
  async function addProvider() {
    if (modelsBusy) return;
    const form = providerForm;
    if (!form.displayName.trim() || !form.baseUrl.trim() || !form.model.trim() || form.capabilities.length === 0) {
      setModelsMsg({ kind: "err", text: "Name, base URL, model, and at least one capability are required." });
      return;
    }
    setModelsBusy(true);
    setModelsMsg(null);
    const defaultFor = [form.defaultChat ? "chat" : null, form.defaultVision ? "vision" : null].filter(Boolean);
    try {
      await api("/api/models", {
        method: "POST",
        body: JSON.stringify({
          displayName: form.displayName.trim(),
          kind: form.kind,
          baseUrl: form.baseUrl.trim(),
          model: form.model.trim(),
          apiKey: form.apiKey.trim() || null,
          capabilities: form.capabilities,
          locality: form.locality,
          defaultFor
        })
      });
      setModelsMsg({ kind: "ok", text: `Added ${form.displayName.trim()}. Your agent can use it now.` });
      setProviderForm({ ...emptyProviderForm });
      await refreshModels();
      await refresh();
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else setModelsMsg({ kind: "err", text: extractError(error) });
    } finally {
      setModelsBusy(false);
    }
  }

  async function deleteProvider(id: string) {
    try {
      await api(`/api/models/${encodeURIComponent(id)}`, { method: "DELETE" });
      setModelsMsg({ kind: "ok", text: `Removed ${id}.` });
      await refreshModels();
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else setModelsMsg({ kind: "err", text: extractError(error) });
    }
  }

  async function makeDefault(capability: string, providerId: string) {
    try {
      await api("/api/models/defaults", { method: "POST", body: JSON.stringify({ capability, providerId }) });
      await refreshModels();
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else setModelsMsg({ kind: "err", text: extractError(error) });
    }
  }

  // Invite a teammate. The runtime returns their bearer token for you to hand
  // over (it is also written to a 0600 file on the box).
  async function addTeammate() {
    const name = teammateName.trim();
    if (!name || teammateBusy) return;
    setTeammateBusy(true);
    setTeammateError(null);
    try {
      const result = await api<{ slug: string; token: string }>("/api/users", {
        method: "POST",
        body: JSON.stringify({ name })
      });
      setTeammateResult(result);
      setTeammateName("");
      await refresh();
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else setTeammateError(extractError(error));
    } finally {
      setTeammateBusy(false);
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

  // The per-owner shared workspace (~/shared): you and your agents meet here.
  async function browseShared(path: string) {
    setFilePreview(null);
    try {
      const query = path ? `?path=${encodeURIComponent(path)}` : "";
      const next = await api<HomeListing>(`/api/shared/list${query}`);
      setListing(next);
      setFilesPath(path);
      setFilesError(null);
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else {
        setListing(null);
        setFilesPath(path);
        setFilesError("No shared workspace yet — open a session (its ~/shared provisions it).");
      }
    }
  }

  async function openHomeEntry(entry: HomeEntry) {
    const childPath = filesPath ? `${filesPath}/${entry.name}` : entry.name;
    if (entry.kind === "directory") {
      await (filesMode === "shared" ? browseShared(childPath) : browseHome(filesOwner, childPath));
      return;
    }
    try {
      const url = filesMode === "shared"
        ? `/api/shared/read?path=${encodeURIComponent(childPath)}`
        : `/api/home/${encodeURIComponent(filesOwner)}/read?path=${encodeURIComponent(childPath)}`;
      setFilePreview(await api<HomeFile>(url));
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
    }
  }

  // Taking a file off the machine. Every call carries the bearer token, so this
  // cannot be a plain <a href> — fetch the bytes with the header, then hand the
  // browser a blob to save under the file's own name.
  async function downloadEntry(name: string, path: string) {
    const url = filesMode === "shared"
      ? `/api/shared/download?path=${encodeURIComponent(path)}`
      : `/api/home/${encodeURIComponent(filesOwner)}/download?path=${encodeURIComponent(path)}`;
    try {
      const response = await fetch(url, { headers: { Authorization: `Bearer ${readToken() ?? ""}` } });
      if (response.status === 401) {
        signOut();
        return;
      }
      if (!response.ok) {
        setFilesError(`Could not download '${name}'.`);
        return;
      }
      const href = URL.createObjectURL(await response.blob());
      const anchor = document.createElement("a");
      anchor.href = href;
      anchor.download = name;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      // Revoking immediately can race the browser's own read of the blob, so let
      // the save finish first; the URL is per-download and dies with the page.
      window.setTimeout(() => URL.revokeObjectURL(href), 60_000);
    } catch (error) {
      setFilesError(String(error));
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
        body: JSON.stringify({ requestHash: approval.requestHash, observedRevision: spreadsheetRevision })
      });
      setLastResult({ decision: result.decision, reason: result.reason });
      await refresh();
    } catch (error) {
      if (error instanceof UnauthorizedError) signOut();
      else setLastResult({ decision: "Deny", reason: String(error) });
    }
  }

  if (!token) {
    const topbar = (
      <header className="topbar">
        <div>
          <h1>{branding.productName}</h1>
          <p>{branding.companyName}</p>
        </div>
      </header>
    );

    // Still asking the runtime whether it has an owner — avoid flashing the wrong screen.
    if (setupClaimed === null) {
      return (
        <main>
          {topbar}
          <section className="login panel setupLoading" data-automation-id="setup-loading">
            <p className="muted"><Loader2 size={14} className="spin" /> Connecting to the runtime…</p>
          </section>
        </main>
      );
    }

    // Fresh machine, no owner yet → the first-run wizard.
    if (setupClaimed === false) {
      return (
        <main>
          {topbar}
          <section className="login panel setup" data-automation-id="setup">
            <p className="eyebrow">First run</p>
            <h2>Claim this machine</h2>
            <p className="lead">
              This machine has no owner yet. Create yours — it becomes the first account, with its
              own agent and workspace. You can add an AI provider later; the OS runs without one.
            </p>
            <label>
              Your name
              <input
                data-automation-id="setup-name"
                autoFocus
                value={setupName}
                placeholder="e.g. Joche Ojeda"
                disabled={setupBusy}
                onChange={(event) => setSetupName(event.target.value)}
                onKeyDown={(event) => event.key === "Enter" && createOwner()}
              />
            </label>
            <div className="setupActions">
              <button data-automation-id="setup-create" disabled={setupBusy || !setupName.trim()} onClick={createOwner}>
                {setupBusy ? <><Loader2 size={16} className="spin" /> Creating…</> : <><UserPlus size={16} /> Create account & enter <ArrowRight size={16} /></>}
              </button>
            </div>
            {setupError && <p className="decision deny" data-automation-id="setup-error">{setupError}</p>}
            <p className="muted small setupNote">
              <ShieldCheck size={12} /> Setup only accepts requests from the machine itself
              (localhost or your SSH tunnel), and only until the first owner is created.
            </p>
          </section>
        </main>
      );
    }

    // Claimed machine → sign in with a token.
    return (
      <main>
        {topbar}
        <section className="login panel" data-automation-id="login">
          <h2>Session</h2>
          <p className="muted">
            Paste your session token. The runtime writes each identity's token to{" "}
            <code>.data/secrets/&lt;slug&gt;.token</code>; the <code>create-owner</code> CLI also prints it on claim.
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
  // /v1/agent resolves the owner's FIRST agent, so the chat link cannot follow the
  // selected desk; only warn about that when more than one agent could be confused.
  const agentDeskCount = desks.filter((candidate) => !candidate.isSelf).length;
  const deskSessions = sessions.filter((session) => session.owner === selectedDesk);
  const deskConsole = deskSessions.find((session) => session.kind === "console" && session.status === "running") ?? null;
  const deskAudit = auditEvents
    .filter((event) => event.principal === selectedDesk || event.onBehalfOf === selectedDesk)
    .slice(0, 8);
  const pendingApprovals = approvals.filter((approval) => approval.status === "Pending");

  return (
    <main>
      <header className="topbar">
        <div>
          <h1>{branding.productName}</h1>
          <p>{branding.companyName}</p>
        </div>
        <div className="topbarRight">
          {models && (
            <span className="modelStatus" title="Chat model — configure in Models">
              <Cpu size={13} />{" "}
              {models.defaults.chat
                ? models.providers.find((provider) => provider.id === models.defaults.chat)?.displayName ?? models.defaults.chat
                : "no model — add one"}
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
        <button className={view === "models" ? "active" : ""} onClick={() => setView("models")}>Models</button>
      </nav>

      {/* Session commands and approvals both report through here; without it a
          refused or failed command would fail silently. */}
      {lastResult && (
        <p className={`decision ${lastResult.decision.toLowerCase()}`} data-automation-id="decision">
          {lastResult.decision}: {lastResult.reason}
        </p>
      )}

      {view === "models" ? (
        <ModelsView
          models={models}
          form={providerForm}
          setForm={setProviderForm}
          applyPreset={applyPreset}
          toggleCapability={toggleCapability}
          addProvider={addProvider}
          deleteProvider={deleteProvider}
          makeDefault={makeDefault}
          message={modelsMsg}
          busy={modelsBusy}
        />
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
            <div className="teammate">
              <button className="ghost addTeammateToggle" data-automation-id="teammate-toggle" onClick={() => setTeammateOpen((open) => !open)}>
                {teammateOpen ? "− Cancel" : "+ Add teammate"}
              </button>
              {teammateOpen && (
                <div className="teammateForm">
                  <p className="muted small">Create another user on this machine — you'll get a token to hand them.</p>
                  <input
                    data-automation-id="teammate-name"
                    value={teammateName}
                    placeholder="their name"
                    disabled={teammateBusy}
                    onChange={(event) => setTeammateName(event.target.value)}
                    onKeyDown={(event) => event.key === "Enter" && addTeammate()}
                  />
                  <button data-automation-id="teammate-add" disabled={teammateBusy || !teammateName.trim()} onClick={addTeammate}>
                    {teammateBusy ? <><Loader2 size={14} className="spin" /> …</> : "Create user"}
                  </button>
                  {teammateError && <p className="decision deny small">{teammateError}</p>}
                  {teammateResult && (
                    <div className="teammateToken" data-automation-id="teammate-result">
                      <p className="muted small">Created <strong>{teammateResult.slug}</strong>. Share this token with them:</p>
                      <code className="tokenValue" data-automation-id="teammate-token">{teammateResult.token}</code>
                    </div>
                  )}
                </div>
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
                <h2>{filesMode === "shared" ? "Shared workspace" : `Home — ${filesOwner || selectedDesk}`}</h2>
                <div className="filesToggle">
                  <button
                    className={`segBtn${filesMode === "home" ? " active" : ""}`}
                    data-automation-id="files-home"
                    onClick={() => { setFilesMode("home"); browseHome(selectedDesk ?? filesOwner, ""); }}
                  >
                    Home
                  </button>
                  <button
                    className={`segBtn${filesMode === "shared" ? " active" : ""}`}
                    data-automation-id="files-shared"
                    onClick={() => { setFilesMode("shared"); browseShared(""); }}
                  >
                    Shared
                  </button>
                </div>
                {filesMode === "shared" && (
                  <p className="muted small">You and your agents share this. Drop inputs here; the agent leaves results here. (~/shared)</p>
                )}
                <div className="crumbs">
                  {homeCrumbs().map((crumb, index) => (
                    <span key={crumb.path}>
                      {index > 0 && <span className="sep">/</span>}
                      <button
                        className="crumb"
                        onClick={() => filesMode === "shared" ? browseShared(crumb.path) : browseHome(filesOwner, crumb.path)}
                      >
                        {crumb.label}
                      </button>
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
                        <div key={entry.name} className="fileRowWrap">
                          <button
                            className="fileRow"
                            data-automation-id={`file-${entry.name}`}
                            onClick={() => openHomeEntry(entry)}
                          >
                            <span className="fileKind">{entry.kind === "directory" ? "▸" : "·"}</span>
                            <span className="fileName">{entry.name}</span>
                            <span className="fileSize">{entry.kind === "directory" ? "" : `${entry.size} B`}</span>
                          </button>
                          {(entry.kind === "file" || entry.kind === "link") && (
                            <button
                              className="fileDownload"
                              data-automation-id={`download-${entry.name}`}
                              title={`Download ${entry.name}`}
                              onClick={() => downloadEntry(entry.name, filesPath ? `${filesPath}/${entry.name}` : entry.name)}
                            >
                              <Download size={14} aria-hidden />
                              <span className="srOnly">Download {entry.name}</span>
                            </button>
                          )}
                        </div>
                      ))
                    )}
                  </div>
                )}
                {filePreview && (
                  <div className="filePreview">
                    <p className="muted small">
                      {filePreview.path}{filePreview.truncated ? " (truncated)" : ""}
                      <button
                        className="fileDownloadLink"
                        data-automation-id="preview-download"
                        onClick={() => downloadEntry(filePreview.path.split("/").pop() ?? filePreview.path, filePreview.path)}
                      >
                        <Download size={13} aria-hidden /> Download
                      </button>
                    </p>
                    {filePreview.binary ? (
                      <p className="muted small">
                        A binary file ({filePreview.size} B) — download it to open in the right application.
                      </p>
                    ) : (
                      <pre>{filePreview.content}</pre>
                    )}
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

            {desk && !desk.isSelf && (
              <div className="panel chat" data-automation-id="chat">
                <h2><Bot size={13} /> Chat</h2>
                {branding.chatUrl ? (
                  <>
                    <p className="muted small">
                      The full chat runs in CieloOS Chat. Every message runs your agent's console
                      loop — it uses its tools and operates the OS — through the same policy-checked
                      bus as the rest of the panel.
                    </p>
                    {agentDeskCount > 1 && (
                      // The chat client carries one identity and /v1/agent resolves the owner's
                      // FIRST agent, so the link cannot follow the selected desk. Say so rather
                      // than implying per-desk routing. Real fix tracked in #9.
                      <p className="muted small" data-automation-id="chat-agent-caveat">
                        Note: chat always talks to your first agent, not the desk selected here —
                        per-agent routing needs the per-user credentials tracked in issue #9.
                      </p>
                    )}
                    <a
                      className="chatLink"
                      data-automation-id="chat-open"
                      href={branding.chatUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      <Bot size={16} /> Open CieloOS Chat
                    </a>
                    <p className="muted small">
                      Opens {branding.chatUrl} in a new tab.
                    </p>
                  </>
                ) : (
                  // No chat deployed: say how to get one rather than linking nowhere.
                  <div data-automation-id="chat-unconfigured">
                    <p className="muted small">
                      No chat UI is configured on this machine. CieloOS serves an OpenAI-compatible
                      API for one at <code>/v1/agent</code>; point a client at it and set
                      <code> Chat__Url</code> to that client's address to link it here.
                    </p>
                  </div>
                )}
              </div>
            )}
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

function ModelsView({
  models,
  form,
  setForm,
  applyPreset,
  toggleCapability,
  addProvider,
  deleteProvider,
  makeDefault,
  message,
  busy
}: {
  models: ModelsData | null;
  form: ProviderForm;
  setForm: React.Dispatch<React.SetStateAction<ProviderForm>>;
  applyPreset: (key: string) => void;
  toggleCapability: (capability: string) => void;
  addProvider: () => void;
  deleteProvider: (id: string) => void;
  makeDefault: (capability: string, id: string) => void;
  message: { kind: "ok" | "err"; text: string } | null;
  busy: boolean;
}) {
  const providers = models?.providers ?? [];
  const defaults = models?.defaults ?? { chat: null, vision: null };

  return (
    <section className="grid" data-automation-id="models">
      <div className="panel">
        <h2><Cpu size={15} /> Model providers</h2>
        <p className="muted small">
          The brains your agents use. The OS runs with none; add one and it's usable immediately — no restart.
          A <strong>default</strong> is what an agent uses unless it has its own choice.
        </p>
        {providers.length === 0 ? (
          <p className="muted">No providers yet — add one on the right to give your agents a brain.</p>
        ) : (
          <div className="providerList">
            {providers.map((provider) => (
              <div className="providerCard" key={provider.id} data-automation-id={`provider-${provider.id}`}>
                <div className="providerHead">
                  <strong>{provider.displayName}</strong>
                  <span className="tag">{provider.locality}</span>
                  {!provider.managed && <span className="tag muted">built-in</span>}
                </div>
                <p className="muted small providerMeta">
                  <code>{provider.model}</code> · {provider.kind}
                  {provider.hasKey ? " · key set" : provider.locality === "on-box" ? " · keyless" : " · no key"}
                </p>
                <div className="capRow">
                  {provider.capabilities.map((capability) => {
                    const isDefault = defaults[capability as "chat" | "vision"] === provider.id;
                    return (
                      <span key={capability} className={`capBadge${isDefault ? " isDefault" : ""}`}>
                        {capability}{isDefault ? " · default" : ""}
                      </span>
                    );
                  })}
                </div>
                <div className="actions">
                  {["chat", "vision"].filter((capability) => provider.capabilities.includes(capability) && defaults[capability as "chat" | "vision"] !== provider.id).map((capability) => (
                    <button key={capability} className="ghost" onClick={() => makeDefault(capability, provider.id)}>
                      Make {capability} default
                    </button>
                  ))}
                  {provider.managed && (
                    <button className="danger" data-automation-id={`provider-delete-${provider.id}`} onClick={() => deleteProvider(provider.id)}>
                      <X size={14} /> Remove
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="panel">
        <h2>Add a provider</h2>
        <div className="presetRow">
          {Object.entries(PROVIDER_PRESETS).map(([key, preset]) => (
            <button
              key={key}
              className={`segBtn${form.preset === key ? " active" : ""}`}
              data-automation-id={`preset-${key}`}
              onClick={() => applyPreset(key)}
            >
              {preset.label}
            </button>
          ))}
        </div>
        <label className="field">
          Display name
          <input data-automation-id="provider-name" value={form.displayName} onChange={(event) => setForm({ ...form, displayName: event.target.value })} />
        </label>
        <label className="field">
          Base URL
          <input data-automation-id="provider-baseurl" value={form.baseUrl} onChange={(event) => setForm({ ...form, baseUrl: event.target.value })} />
        </label>
        <label className="field">
          Model
          <input data-automation-id="provider-model" value={form.model} onChange={(event) => setForm({ ...form, model: event.target.value })} />
        </label>
        <label className="field">
          API key {form.locality === "on-box" ? <span className="muted small">(local models are usually keyless)</span> : null}
          <input data-automation-id="provider-key" type="password" placeholder="stored 0600, never shown again" value={form.apiKey} onChange={(event) => setForm({ ...form, apiKey: event.target.value })} />
        </label>
        <div className="capChoose">
          <span className="muted small">Capabilities</span>
          {["chat", "vision", "embedding"].map((capability) => (
            <label key={capability} className="checkRow">
              <input type="checkbox" checked={form.capabilities.includes(capability)} onChange={() => toggleCapability(capability)} /> {capability}
            </label>
          ))}
        </div>
        <label className="field">
          Locality
          <select value={form.locality} onChange={(event) => setForm({ ...form, locality: event.target.value })}>
            <option value="cloud">cloud</option>
            <option value="remote-self-hosted">remote-self-hosted</option>
            <option value="on-box">on-box</option>
          </select>
        </label>
        <div className="capChoose">
          <span className="muted small">Set as default for</span>
          <label className="checkRow">
            <input type="checkbox" disabled={!form.capabilities.includes("chat")} checked={form.defaultChat && form.capabilities.includes("chat")} onChange={(event) => setForm({ ...form, defaultChat: event.target.checked })} /> chat
          </label>
          <label className="checkRow">
            <input type="checkbox" disabled={!form.capabilities.includes("vision")} checked={form.defaultVision && form.capabilities.includes("vision")} onChange={(event) => setForm({ ...form, defaultVision: event.target.checked })} /> vision
          </label>
        </div>
        <div className="setupActions">
          <button data-automation-id="provider-add" disabled={busy} onClick={addProvider}>
            {busy ? <><Loader2 size={16} className="spin" /> Adding…</> : <><Check size={16} /> Add provider</>}
          </button>
        </div>
        {message && <p className={`decision ${message.kind === "ok" ? "allow" : "deny"}`} data-automation-id="models-message">{message.text}</p>}
      </div>
    </section>
  );
}

createRoot(document.getElementById("root")!).render(<App />);
