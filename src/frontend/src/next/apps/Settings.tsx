// Settings — the administrative half of CieloOS, gathered into one app.
//
// The old panel spread this across a "Models" tab and a handful of corners: the
// AI provider list sat beside the password form, the spend ceiling beside the API
// keys, and adding a teammate lived under the desk list on a different screen. It
// is all one job — deciding what this machine may do and who may ask it — so it is
// one window here.
//
// Two things shape the layout:
//
//   1. A machine with no AI provider can do almost nothing, so an empty provider
//      list is the FIRST-RUN MOMENT, not a warning. It takes the whole width, it
//      says what connecting one buys you, and the form it points at is right there.
//   2. A new key's secret exists for exactly one render. It is shown as a thing
//      that is about to disappear — with a copy button and an explicit "this is the
//      only time" — rather than as another row in a list.
//
// Nothing in here goes through the command bus, so nothing in here can come back
// asking for permission; onApproval is part of the shell contract and is accepted
// unused rather than pretended at.

import * as React from "react";
import {
  Check,
  Copy,
  Cpu,
  KeyRound,
  Languages,
  Loader2,
  LogOut,
  UserPlus,
  Wallet,
  X,
} from "lucide-react";
import {
  api,
  UnauthorizedError,
  type ApprovalRecord,
  type SessionView,
  type Whoami,
} from "../../shared/api";
import { readable, serverText } from "../../shared/plain";
import { LANGUAGES, resolveLanguage, useLanguage, useT, type Language } from "../../shared/i18n";

// ---------------------------------------------------------------- shapes

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

type ModelsData = {
  providers: ModelProvider[];
  defaults: { chat: string | null; vision: string | null };
};

type ApiKeyView = {
  id: string;
  name: string;
  createdAt: string;
  expiresAt: string | null;
  revokedAt: string | null;
  lastUsedAt: string | null;
  live: boolean;
};

type UsageEntry = {
  occurredAt: string;
  providerId: string;
  model: string;
  locality: string;
  promptTokens: number;
  completionTokens: number;
};

type UsageView = {
  month: string;
  deskSubject: string;
  desk: number;
  agent: number;
  machine: number;
  deskLimit: number;
  machineLimit: number;
  recent: UsageEntry[];
};

// One option from /api/desk-profiles — what a teammate's workspace is built
// from. A person is only ever shown the label and description the runtime sends,
// never the identifier the API is named after.
type SetupOption = {
  id: string;
  label: string;
  description: string;
  isDefault: boolean;
  imageReady: boolean;
  buildStatus: string;
};

// Loading, ready, failed — the three honest states, kept per section so one dead
// endpoint cannot blank the whole window.
type Loaded<T> =
  | { state: "loading" }
  | { state: "ready"; data: T }
  | { state: "failed"; reason: string };

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

export type SettingsProps = {
  whoami: Whoami | null;
  sessions: SessionView[];
  reload: () => void;
  onApproval: (approval: ApprovalRecord) => void;
  notify: (message: string) => void;
};

// Presets prefill the add form; every one speaks the OpenAI chat format
// (Bearer + /chat/completions), which is what the runtime calls.
const PROVIDER_PRESETS: Record<
  string,
  { labelKey: string; kind: string; baseUrl: string; model: string; locality: string; capabilities: string[] }
> = {
  deepseek: {
    labelKey: "presets.deepseek",
    kind: "openai-compatible",
    baseUrl: "https://api.deepseek.com",
    model: "deepseek-chat",
    locality: "cloud",
    capabilities: ["chat"],
  },
  azure: {
    labelKey: "presets.azure",
    kind: "azure-openai",
    baseUrl: "https://<resource>.openai.azure.com/openai/v1",
    model: "gpt-4.1-mini",
    locality: "cloud",
    capabilities: ["chat", "vision"],
  },
  openai: {
    labelKey: "presets.openai",
    kind: "openai-compatible",
    baseUrl: "https://api.openai.com/v1",
    model: "gpt-4o-mini",
    locality: "cloud",
    capabilities: ["chat", "vision"],
  },
  ollama: {
    labelKey: "presets.ollama",
    kind: "ollama",
    baseUrl: "http://127.0.0.1:11434/v1",
    model: "llama3.1",
    locality: "on-box",
    capabilities: ["chat"],
  },
};

const EMPTY_FORM: ProviderForm = {
  preset: "deepseek",
  displayName: "DeepSeek",
  kind: "openai-compatible",
  baseUrl: "https://api.deepseek.com",
  model: "deepseek-chat",
  apiKey: "",
  capabilities: ["chat"],
  locality: "cloud",
  defaultChat: true,
  defaultVision: false,
};

const CAPABILITIES = ["chat", "vision", "embedding"] as const;
const LOCALITIES = ["cloud", "remote-self-hosted", "on-box"] as const;
const MIN_PASSWORD = 10;

// The runtime throws the raw response body, which is JSON {error}. Pull the
// sentence out of it so a failure can say what failed instead of showing a blob.
// Whether that sentence is fit to show is decided separately, inside the
// component, where the replacement line can be translated.
const rawReason = serverText;

const wideStyle: React.CSSProperties = { gridColumn: "1 / -1" };
const rowStyle: React.CSSProperties = {
  alignItems: "center",
  borderTop: "1px solid var(--hairline)",
  display: "flex",
  gap: "12px",
  justifyContent: "space-between",
  padding: "10px 0",
};
const smallButtonStyle: React.CSSProperties = { fontSize: "12px", minHeight: "32px", padding: "0 10px" };
const heroGlyphStyle: React.CSSProperties = {
  alignItems: "center",
  background: "var(--accent-soft)",
  borderRadius: "14px",
  color: "var(--accent)",
  display: "inline-flex",
  height: "48px",
  justifyContent: "center",
  marginBottom: "14px",
  width: "48px",
};
const secretStyle: React.CSSProperties = {
  background: "var(--paper)",
  border: "1px solid var(--hold)",
  borderRadius: "10px",
  marginTop: "12px",
  padding: "12px 14px",
};

export default function Settings({ whoami, reload, notify }: SettingsProps) {
  const t = useT();
  const activeLanguage = useLanguage();

  // Some of what the runtime sends back is a sentence ("A provider with that name
  // already exists."); some of it is written for whoever is reading the source
  // ("This operation requires a human principal."). The second kind is withheld
  // rather than repeated at someone who has never seen those words, and this line
  // stands in its place. It is still a failure that names itself as one.
  const reasonOf = React.useCallback(
    (error: unknown) => readable(rawReason(error), t("errors.wordedForTheMachine")),
    [t],
  );

  // ------------------------------------------------------------ state
  const [models, setModels] = React.useState<Loaded<ModelsData>>({ state: "loading" });
  const [form, setForm] = React.useState<ProviderForm>({ ...EMPTY_FORM });
  const [providerBusy, setProviderBusy] = React.useState(false);
  const [providerError, setProviderError] = React.useState<string | null>(null);
  const nameInput = React.useRef<HTMLInputElement | null>(null);

  const [languageBusy, setLanguageBusy] = React.useState<Language | null>(null);
  const [languageError, setLanguageError] = React.useState<string | null>(null);

  const [usage, setUsage] = React.useState<Loaded<UsageView>>({ state: "loading" });
  const [ceiling, setCeiling] = React.useState("");
  const [ceilingError, setCeilingError] = React.useState<string | null>(null);

  const [currentPassword, setCurrentPassword] = React.useState("");
  const [newPassword, setNewPassword] = React.useState("");
  const [passwordNote, setPasswordNote] = React.useState<{ good: boolean; text: string } | null>(null);
  const [askSignOut, setAskSignOut] = React.useState(false);

  const [keys, setKeys] = React.useState<Loaded<ApiKeyView[]>>({ state: "loading" });
  const [keyName, setKeyName] = React.useState("");
  const [keyError, setKeyError] = React.useState<string | null>(null);
  const [secret, setSecret] = React.useState<{ name: string; value: string } | null>(null);

  const [setups, setSetups] = React.useState<Loaded<SetupOption[]>>({ state: "loading" });
  const [teammateOpen, setTeammateOpen] = React.useState(false);
  const [teammateSetup, setTeammateSetup] = React.useState("");
  const [teammateName, setTeammateName] = React.useState("");
  const [teammateBusy, setTeammateBusy] = React.useState(false);
  const [teammateError, setTeammateError] = React.useState<string | null>(null);
  const [teammateResult, setTeammateResult] = React.useState<{ slug: string; token: string } | null>(null);

  const isHuman = (whoami?.kind ?? "").toLowerCase() === "human";
  const language = resolveLanguage(whoami?.language ?? activeLanguage);

  // Held in a ref so the loaders below stay stable: if they depended on the prop
  // itself, every re-render of the shell would re-run all four fetches.
  const reloadRef = React.useRef(reload);
  React.useEffect(() => {
    reloadRef.current = reload;
  }, [reload]);

  // A rejected session is the shell's problem, not this window's: hand it back
  // rather than drawing a broken settings page over a dead login.
  const guard = React.useCallback((error: unknown) => {
    if (error instanceof UnauthorizedError) {
      reloadRef.current();
      return true;
    }
    return false;
  }, []);

  const number = React.useCallback(
    (value: number) => value.toLocaleString(activeLanguage),
    [activeLanguage],
  );

  // ------------------------------------------------------------ loading

  const loadModels = React.useCallback(async () => {
    try {
      setModels({ state: "ready", data: await api<ModelsData>("/api/models") });
    } catch (error) {
      if (guard(error)) return;
      setModels({ state: "failed", reason: reasonOf(error) });
    }
  }, [guard]);

  const loadUsage = React.useCallback(async () => {
    try {
      setUsage({ state: "ready", data: await api<UsageView>("/api/usage") });
    } catch (error) {
      if (guard(error)) return;
      setUsage({ state: "failed", reason: reasonOf(error) });
    }
  }, [guard]);

  const loadKeys = React.useCallback(async () => {
    try {
      const data = await api<{ keys: ApiKeyView[] }>("/api/keys");
      setKeys({ state: "ready", data: data.keys });
    } catch (error) {
      if (guard(error)) return;
      setKeys({ state: "failed", reason: reasonOf(error) });
    }
  }, [guard]);

  const loadSetups = React.useCallback(async () => {
    try {
      const data = await api<SetupOption[]>("/api/desk-profiles");
      setSetups({ state: "ready", data });
      setTeammateSetup((current) =>
        current || (data.find((option) => option.isDefault)?.id ?? data[0]?.id ?? ""));
    } catch (error) {
      if (guard(error)) return;
      setSetups({ state: "failed", reason: reasonOf(error) });
    }
  }, [guard]);

  React.useEffect(() => {
    void loadModels();
    void loadUsage();
    void loadKeys();
  }, [loadModels, loadUsage, loadKeys]);

  React.useEffect(() => {
    if (isHuman) void loadSetups();
  }, [isHuman, loadSetups]);

  // A setup that is downloading takes minutes. One refresh after starting it
  // would leave this saying "installing" forever, and would leave the button
  // visible — inviting a second download of something already here.
  const building =
    setups.state === "ready" && setups.data.some((option) => option.buildStatus === "building");
  React.useEffect(() => {
    if (!building) return;
    const timer = window.setInterval(() => {
      void loadSetups();
    }, 5000);
    return () => window.clearInterval(timer);
  }, [building, loadSetups]);

  // ------------------------------------------------------------ providers

  const capabilityLabel = (capability: string) =>
    capability === "chat"
      ? t("settings.providers.capabilityChat")
      : capability === "vision"
        ? t("settings.providers.capabilityVision")
        : capability === "embedding"
          ? t("settings.providers.capabilityEmbedding")
          : capability;

  const localityLabel = (locality: string) =>
    locality === "cloud"
      ? t("settings.providers.localityCloud")
      : locality === "remote-self-hosted"
        ? t("settings.providers.localityRemote")
        : locality === "on-box"
          ? t("settings.providers.localityOnBox")
          : locality;

  function applyPreset(key: string) {
    const preset = PROVIDER_PRESETS[key];
    if (!preset) return;
    setForm((current) => ({
      ...current,
      preset: key,
      displayName: t(preset.labelKey),
      kind: preset.kind,
      baseUrl: preset.baseUrl,
      model: preset.model,
      locality: preset.locality,
      capabilities: preset.capabilities,
      defaultVision: preset.capabilities.includes("vision") ? current.defaultVision : false,
    }));
  }

  function toggleCapability(capability: string) {
    setForm((current) => ({
      ...current,
      capabilities: current.capabilities.includes(capability)
        ? current.capabilities.filter((value) => value !== capability)
        : [...current.capabilities, capability],
    }));
  }

  async function addProvider() {
    if (providerBusy) return;
    if (!form.displayName.trim() || !form.baseUrl.trim() || !form.model.trim() || form.capabilities.length === 0) {
      setProviderError(t("addProvider.errorRequiredFields"));
      return;
    }
    setProviderBusy(true);
    setProviderError(null);
    const defaultFor = [form.defaultChat ? "chat" : null, form.defaultVision ? "vision" : null]
      .filter((value): value is string => value !== null);
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
          defaultFor,
        }),
      });
      notify(t("addProvider.providerAdded", { name: form.displayName.trim() }));
      setForm({ ...EMPTY_FORM });
      await loadModels();
      // The shell's own status line names the chat model, so it has to hear too.
      reload();
    } catch (error) {
      if (guard(error)) return;
      setProviderError(reasonOf(error));
    } finally {
      setProviderBusy(false);
    }
  }

  async function removeProvider(provider: ModelProvider) {
    setProviderError(null);
    try {
      await api(`/api/models/${encodeURIComponent(provider.id)}`, { method: "DELETE" });
      notify(t("addProvider.providerRemoved", { id: provider.displayName }));
      await loadModels();
      reload();
    } catch (error) {
      if (guard(error)) return;
      setProviderError(t("settings.providers.removeFailed", { name: provider.displayName, reason: reasonOf(error) }));
    }
  }

  async function makeDefault(capability: string, providerId: string) {
    setProviderError(null);
    try {
      await api("/api/models/defaults", {
        method: "POST",
        body: JSON.stringify({ capability, providerId }),
      });
      await loadModels();
      reload();
    } catch (error) {
      if (guard(error)) return;
      setProviderError(t("settings.providers.defaultFailed", { reason: reasonOf(error) }));
    }
  }

  // ------------------------------------------------------------ language

  async function chooseLanguage(next: Language) {
    if (next === language || languageBusy) return;
    setLanguageBusy(next);
    setLanguageError(null);
    try {
      // TODO(runtime): PATCH /api/users/language does not exist yet. It needs to
      // be added to WorkspaceRuntime.Api — storing the choice on the user and
      // returning it from GET /api/whoami as `language` — because the language a
      // person picks also decides their session's locale and keyboard and the
      // language Cielo answers in, none of which a browser setting reaches. Until
      // it exists this call answers 404 and the line below says so plainly.
      await api("/api/users/language", {
        method: "PATCH",
        body: JSON.stringify({ language: next }),
      });
      notify(t("settings.language.saved"));
      // The whole panel re-reads itself: the shell owns whoami, and every open
      // window takes its words from it.
      reload();
    } catch (error) {
      if (guard(error)) return;
      setLanguageError(t("settings.language.failed", { reason: reasonOf(error) }));
    } finally {
      setLanguageBusy(null);
    }
  }

  // ------------------------------------------------------------ spend

  async function setCeilingValue() {
    if (usage.state !== "ready" || !usage.data.deskSubject) return;
    const digits = ceiling.replace(/[^0-9]/g, "");
    if (digits.length === 0) return;
    const monthlyTokens = Number.parseInt(digits, 10);
    if (Number.isNaN(monthlyTokens)) return;
    setCeilingError(null);
    try {
      await api("/api/usage/limits", {
        method: "POST",
        body: JSON.stringify({ scope: "user", subject: usage.data.deskSubject, monthlyTokens }),
      });
      setCeiling("");
      notify(t("settings.spend.ceilingSaved"));
      await loadUsage();
    } catch (error) {
      if (guard(error)) return;
      setCeilingError(t("settings.spend.ceilingFailed", { reason: reasonOf(error) }));
    }
  }

  // ------------------------------------------------------------ password

  async function setPassword() {
    if (newPassword.length < MIN_PASSWORD) {
      setPasswordNote({ good: false, text: t("settings.password.tooShort", { count: MIN_PASSWORD }) });
      return;
    }
    try {
      await api("/api/auth/password", {
        method: "POST",
        body: JSON.stringify({ currentPassword, newPassword }),
      });
      setCurrentPassword("");
      setNewPassword("");
      setPasswordNote({ good: true, text: t("security.passwordSet") });
    } catch (error) {
      if (guard(error)) return;
      setPasswordNote({ good: false, text: t("settings.password.failed", { reason: reasonOf(error) }) });
    }
  }

  async function signOutEverywhere() {
    try {
      await api("/api/auth/logout-all", { method: "POST" });
      setAskSignOut(false);
      notify(t("settings.signOut.done"));
      // This session went with the others. Telling the shell is what puts the
      // sign-in screen back up.
      reload();
    } catch (error) {
      if (guard(error)) return;
      setPasswordNote({ good: false, text: t("settings.signOut.failed", { reason: reasonOf(error) }) });
    }
  }

  // ------------------------------------------------------------ keys

  async function createKey() {
    const name = keyName.trim();
    if (!name) return;
    setKeyError(null);
    try {
      const created = await api<{ secret: string }>("/api/keys", {
        method: "POST",
        body: JSON.stringify({ name }),
      });
      // This is the only moment the secret exists in the panel. It is held in
      // state, shown once, and dropped the moment the person says they have it.
      setSecret({ name, value: created.secret });
      setKeyName("");
      await loadKeys();
    } catch (error) {
      if (guard(error)) return;
      setKeyError(t("settings.keys.createFailed", { reason: reasonOf(error) }));
    }
  }

  async function revokeKey(key: ApiKeyView) {
    setKeyError(null);
    try {
      await api(`/api/keys/${encodeURIComponent(key.id)}`, { method: "DELETE" });
      notify(t("settings.keys.revoked", { name: key.name }));
      await loadKeys();
    } catch (error) {
      if (guard(error)) return;
      setKeyError(t("settings.keys.revokeFailed", { reason: reasonOf(error) }));
    }
  }

  async function copySecret(value: string) {
    try {
      await navigator.clipboard.writeText(value);
      notify(t("settings.keys.copied"));
    } catch {
      // Clipboard access can be refused, and a button that silently does nothing
      // is worse than one that says to do it by hand.
      setKeyError(t("settings.keys.copyFailed"));
    }
  }

  // ------------------------------------------------------------ teammates

  async function installSetup(id: string) {
    setTeammateError(null);
    try {
      await api(`/api/desk-profiles/${encodeURIComponent(id)}/build`, { method: "POST" });
      await loadSetups();
    } catch (error) {
      if (guard(error)) return;
      setTeammateError(t("settings.people.installFailed", { reason: reasonOf(error) }));
    }
  }

  async function addTeammate() {
    const name = teammateName.trim();
    if (!name || teammateBusy) return;
    setTeammateBusy(true);
    setTeammateError(null);
    try {
      const result = await api<{ slug: string; token: string }>("/api/users", {
        method: "POST",
        body: JSON.stringify({ name, deskProfile: teammateSetup }),
      });
      setTeammateResult(result);
      setTeammateName("");
      reload();
    } catch (error) {
      if (guard(error)) return;
      setTeammateError(t("settings.people.createFailed", { reason: reasonOf(error) }));
    } finally {
      setTeammateBusy(false);
    }
  }

  // ------------------------------------------------------------ render

  const providers = models.state === "ready" ? models.data.providers : [];
  const defaults = models.state === "ready" ? models.data.defaults : { chat: null, vision: null };
  const firstRun = models.state === "ready" && providers.length === 0;
  const chosenSetup =
    setups.state === "ready" ? setups.data.find((option) => option.id === teammateSetup) ?? null : null;

  return (
    <div className="settingsApp" data-automation-id="settings">
      <header className="settingsHead" style={{ marginBottom: "22px" }}>
        <h1 style={{ fontSize: "28px", lineHeight: 1.15, margin: 0 }}>{t("settings.title")}</h1>
        <p className="muted" style={{ marginTop: "8px", maxWidth: "60ch" }}>{t("settings.lead")}</p>
      </header>

      <div className="grid">
        {/* ---------------------------------------------- AI provider */}
        <div
          className="panel settingsProviders"
          style={firstRun ? wideStyle : undefined}
          data-automation-id="settings-providers"
        >
          <h2><Cpu size={15} /> {t("settings.providers.title")}</h2>

          {models.state === "loading" && (
            <p className="muted small"><Loader2 size={14} className="spin" /> {t("settings.providers.loading")}</p>
          )}

          {models.state === "failed" && (
            <p className="decision deny small">{t("settings.providers.failed", { reason: models.reason })}</p>
          )}

          {firstRun && (
            <div data-automation-id="settings-first-run">
              <span style={heroGlyphStyle}><KeyRound size={22} /></span>
              <p style={{ fontSize: "18px", fontWeight: 600 }}>{t("settings.providers.emptyTitle")}</p>
              <p className="muted" style={{ marginTop: "8px", maxWidth: "58ch" }}>
                {t("settings.providers.emptyBody")}
              </p>
              <div className="setupActions" style={{ marginTop: "16px" }}>
                <button
                  data-automation-id="settings-first-run-connect"
                  onClick={() => nameInput.current?.focus()}
                >
                  <Check size={16} /> {t("settings.providers.emptyAction")}
                </button>
              </div>
            </div>
          )}

          {models.state === "ready" && providers.length > 0 && (
            <>
              <p className="muted small">{t("settings.providers.lead")}</p>
              <div className="providerList">
                {providers.map((provider) => (
                  <div className="providerCard" key={provider.id} data-automation-id={`provider-${provider.id}`}>
                    <div className="providerHead">
                      <strong>{provider.displayName}</strong>
                      <span className="tag">{localityLabel(provider.locality)}</span>
                      {!provider.managed && <span className="tag muted">{t("providers.builtInTag")}</span>}
                    </div>
                    <p className="muted small providerMeta">
                      <code>{provider.model}</code> · {provider.kind}
                      {provider.hasKey
                        ? t("providers.keySet")
                        : provider.locality === "on-box"
                          ? t("providers.keyless")
                          : t("providers.noKey")}
                    </p>
                    <div className="capRow">
                      {provider.capabilities.map((capability) => {
                        const isDefault = defaults[capability as "chat" | "vision"] === provider.id;
                        return (
                          <span key={capability} className={`capBadge${isDefault ? " isDefault" : ""}`}>
                            {capabilityLabel(capability)}
                            {isDefault ? t("providers.defaultBadge") : ""}
                          </span>
                        );
                      })}
                    </div>
                    <div className="actions">
                      {(["chat", "vision"] as const)
                        .filter((capability) =>
                          provider.capabilities.includes(capability) && defaults[capability] !== provider.id)
                        .map((capability) => (
                          <button
                            key={capability}
                            className="ghost"
                            style={smallButtonStyle}
                            onClick={() => void makeDefault(capability, provider.id)}
                          >
                            {capability === "chat"
                              ? t("settings.providers.makeDefaultChat")
                              : t("settings.providers.makeDefaultVision")}
                          </button>
                        ))}
                      {provider.managed && (
                        <button
                          className="danger"
                          style={smallButtonStyle}
                          data-automation-id={`provider-delete-${provider.id}`}
                          onClick={() => void removeProvider(provider)}
                        >
                          <X size={14} /> {t("providers.removeButton")}
                        </button>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </>
          )}
        </div>

        {/* ---------------------------------------------- add a provider */}
        <div
          className="panel settingsAddProvider"
          style={firstRun ? wideStyle : undefined}
          data-automation-id="settings-add-provider"
        >
          <h2>{t("addProvider.heading")}</h2>
          <div className="presetRow">
            {Object.entries(PROVIDER_PRESETS).map(([key, preset]) => (
              <button
                key={key}
                className={`segBtn${form.preset === key ? " active" : ""}`}
                data-automation-id={`preset-${key}`}
                onClick={() => applyPreset(key)}
              >
                {t(preset.labelKey)}
              </button>
            ))}
          </div>

          <label className="field">
            {t("addProvider.displayNameLabel")}
            <input
              ref={nameInput}
              data-automation-id="provider-name"
              value={form.displayName}
              onChange={(event) => setForm({ ...form, displayName: event.target.value })}
            />
          </label>
          <label className="field">
            {t("addProvider.baseUrlLabel")}
            <input
              data-automation-id="provider-baseurl"
              value={form.baseUrl}
              onChange={(event) => setForm({ ...form, baseUrl: event.target.value })}
            />
          </label>
          <label className="field">
            {t("addProvider.modelLabel")}
            <input
              data-automation-id="provider-model"
              value={form.model}
              onChange={(event) => setForm({ ...form, model: event.target.value })}
            />
          </label>
          <label className="field">
            {t("addProvider.apiKeyLabel")}{" "}
            {form.locality === "on-box" && (
              <span className="muted small">{t("addProvider.apiKeyKeylessHint")}</span>
            )}
            <input
              data-automation-id="provider-key"
              type="password"
              placeholder={t("addProvider.apiKeyPlaceholder")}
              value={form.apiKey}
              onChange={(event) => setForm({ ...form, apiKey: event.target.value })}
            />
          </label>

          <div className="capChoose">
            <span className="muted small">{t("addProvider.capabilitiesLabel")}</span>
            {CAPABILITIES.map((capability) => (
              <label key={capability} className="checkRow">
                <input
                  type="checkbox"
                  checked={form.capabilities.includes(capability)}
                  onChange={() => toggleCapability(capability)}
                />{" "}
                {capabilityLabel(capability)}
              </label>
            ))}
          </div>

          <label className="field">
            {t("addProvider.localityLabel")}
            <select
              value={form.locality}
              onChange={(event) => setForm({ ...form, locality: event.target.value })}
            >
              {LOCALITIES.map((locality) => (
                <option key={locality} value={locality}>{localityLabel(locality)}</option>
              ))}
            </select>
          </label>

          <div className="capChoose">
            <span className="muted small">{t("addProvider.defaultForLabel")}</span>
            <label className="checkRow">
              <input
                type="checkbox"
                disabled={!form.capabilities.includes("chat")}
                checked={form.defaultChat && form.capabilities.includes("chat")}
                onChange={(event) => setForm({ ...form, defaultChat: event.target.checked })}
              />{" "}
              {capabilityLabel("chat")}
            </label>
            <label className="checkRow">
              <input
                type="checkbox"
                disabled={!form.capabilities.includes("vision")}
                checked={form.defaultVision && form.capabilities.includes("vision")}
                onChange={(event) => setForm({ ...form, defaultVision: event.target.checked })}
              />{" "}
              {capabilityLabel("vision")}
            </label>
          </div>

          <div className="setupActions">
            <button data-automation-id="provider-add" disabled={providerBusy} onClick={() => void addProvider()}>
              {providerBusy
                ? <><Loader2 size={16} className="spin" /> {t("addProvider.adding")}</>
                : <><Check size={16} /> {t("addProvider.submitButton")}</>}
            </button>
          </div>
          {providerError && (
            <p className="decision deny small" data-automation-id="provider-error">{providerError}</p>
          )}
        </div>

        {/* ---------------------------------------------- language */}
        <div className="panel settingsLanguage" data-automation-id="settings-language">
          <h2><Languages size={15} /> {t("settings.language.title")}</h2>
          <p className="muted small">{t("settings.language.lead")}</p>
          <div className="presetRow" style={{ marginTop: "12px" }}>
            {LANGUAGES.map((option) => (
              <button
                key={option.code}
                className={`segBtn${option.code === language ? " active" : ""}`}
                data-automation-id={`language-${option.code}`}
                aria-pressed={option.code === language}
                disabled={languageBusy !== null}
                onClick={() => void chooseLanguage(option.code)}
              >
                {languageBusy === option.code && <Loader2 size={13} className="spin" />} {option.native}
              </button>
            ))}
          </div>
          {languageError && (
            <p className="decision deny small" data-automation-id="language-error">{languageError}</p>
          )}
        </div>

        {/* ---------------------------------------------- spending */}
        <div className="panel settingsSpend" data-automation-id="settings-spend">
          <h2><Wallet size={15} /> {t("settings.spend.title")}</h2>

          {usage.state === "loading" && (
            <p className="muted small"><Loader2 size={14} className="spin" /> {t("settings.spend.loading")}</p>
          )}
          {usage.state === "failed" && (
            <p className="decision deny small">{t("settings.spend.failed", { reason: usage.reason })}</p>
          )}

          {usage.state === "ready" && (
            <>
              <p className="muted small">{t("settings.spend.month", { month: usage.data.month })}</p>
              <p className="muted small" style={{ marginTop: "6px" }}>{t("settings.spend.lead")}</p>

              <div style={{ marginTop: "14px" }}>
                <div className="usageRow">
                  <span>{t("settings.spend.you")}</span>
                  <strong data-automation-id="usage-desk">{number(usage.data.desk)}</strong>
                  <span className="muted small">
                    {usage.data.deskLimit > 0
                      ? t("usage.ofLimit", { limit: number(usage.data.deskLimit) })
                      : t("usage.noCeiling")}
                  </span>
                </div>
                <div className="usageRow">
                  <span>{t("settings.spend.machine")}</span>
                  <strong>{number(usage.data.machine)}</strong>
                  <span className="muted small">
                    {usage.data.machineLimit > 0
                      ? t("usage.ofLimit", { limit: number(usage.data.machineLimit) })
                      : t("usage.noCeiling")}
                  </span>
                </div>
              </div>

              <div className="inline" style={{ marginTop: "14px" }}>
                <label>
                  {t("settings.spend.ceilingLabel")}
                  <input
                    data-automation-id="usage-limit"
                    value={ceiling}
                    placeholder={
                      usage.data.deskLimit > 0 ? String(usage.data.deskLimit) : t("usage.limitPlaceholder")
                    }
                    onChange={(event) => setCeiling(event.target.value)}
                    onKeyDown={(event) => {
                      if (event.key === "Enter") void setCeilingValue();
                    }}
                  />
                </label>
                <button data-automation-id="usage-limit-set" onClick={() => void setCeilingValue()}>
                  {t("usage.setLimitButton")}
                </button>
              </div>
              <p className="muted small" style={{ marginTop: "8px" }}>{t("usage.limitNote")}</p>
              {ceilingError && <p className="decision deny small">{ceilingError}</p>}

              {usage.data.recent.length === 0 ? (
                <p className="muted small" style={{ marginTop: "14px" }}>{t("settings.spend.empty")}</p>
              ) : (
                <div style={{ marginTop: "14px" }}>
                  <p className="muted small">{t("settings.spend.recent")}</p>
                  {usage.data.recent.slice(0, 5).map((entry, index) => (
                    <div className="usageRow" key={`${entry.occurredAt}-${index}`}>
                      <span>{entry.model}</span>
                      <strong>{number(entry.promptTokens + entry.completionTokens)}</strong>
                      <span className="muted small">{localityLabel(entry.locality)}</span>
                    </div>
                  ))}
                </div>
              )}
            </>
          )}
        </div>

        {/* ---------------------------------------------- password + sessions */}
        <div className="panel settingsPassword" data-automation-id="settings-password">
          <h2><KeyRound size={15} /> {t("settings.password.title")}</h2>
          <p className="muted small">{t("settings.password.lead")}</p>

          <label className="field">
            {t("security.currentPasswordLabel")}
            <input
              data-automation-id="password-current"
              type="password"
              value={currentPassword}
              placeholder={t("security.currentPasswordPlaceholder")}
              onChange={(event) => setCurrentPassword(event.target.value)}
            />
          </label>
          <label className="field">
            {t("security.newPasswordLabel")}
            <input
              data-automation-id="password-new"
              type="password"
              value={newPassword}
              placeholder={t("security.newPasswordPlaceholder")}
              onChange={(event) => setNewPassword(event.target.value)}
            />
          </label>
          <div className="setupActions">
            <button
              data-automation-id="password-set"
              disabled={newPassword.length < MIN_PASSWORD}
              onClick={() => void setPassword()}
            >
              {t("security.setPasswordButton")}
            </button>
          </div>
          <p className="muted small" style={{ marginTop: "10px" }}>{t("security.passwordNote")}</p>
          {passwordNote && (
            <p className={`decision ${passwordNote.good ? "allow" : "deny"} small`} data-automation-id="password-note">
              {passwordNote.text}
            </p>
          )}

          <div style={{ borderTop: "1px solid var(--hairline)", marginTop: "18px", paddingTop: "14px" }}>
            <p style={{ fontWeight: 600 }}>{t("settings.signOut.title")}</p>
            <p className="muted small" style={{ marginTop: "6px" }}>{t("settings.signOut.lead")}</p>
            {askSignOut ? (
              <div className="inline" style={{ marginTop: "12px" }}>
                <button
                  className="danger"
                  data-automation-id="logout-all-confirm"
                  onClick={() => void signOutEverywhere()}
                >
                  <LogOut size={14} /> {t("settings.signOut.confirm")}
                </button>
                <button className="ghost" onClick={() => setAskSignOut(false)}>
                  {t("settings.signOut.cancel")}
                </button>
              </div>
            ) : (
              <div className="inline" style={{ marginTop: "12px" }}>
                <button className="ghost" data-automation-id="logout-all" onClick={() => setAskSignOut(true)}>
                  {t("security.signOutEverywhere")}
                </button>
              </div>
            )}
          </div>
        </div>

        {/* ---------------------------------------------- keys for programs */}
        <div className="panel settingsKeys" data-automation-id="settings-keys">
          <h2><KeyRound size={15} /> {t("settings.keys.title")}</h2>
          <p className="muted small">{t("settings.keys.lead")}</p>

          <div className="inline" style={{ marginTop: "12px" }}>
            <label>
              {t("security.keyNameLabel")}
              <input
                data-automation-id="key-name"
                value={keyName}
                placeholder={t("security.keyNamePlaceholder")}
                onChange={(event) => setKeyName(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === "Enter") void createKey();
                }}
              />
            </label>
            <button data-automation-id="key-create" disabled={!keyName.trim()} onClick={() => void createKey()}>
              {t("security.createKeyButton")}
            </button>
          </div>

          {/* Shown once, and said so. The panel drops it the moment it is dismissed. */}
          {secret && (
            <div style={secretStyle} data-automation-id="key-secret">
              <p style={{ color: "var(--hold)", fontWeight: 600 }}>
                {t("settings.keys.secretTitle", { name: secret.name })}
              </p>
              <p className="muted small" style={{ marginTop: "6px" }}>{t("settings.keys.secretBody")}</p>
              <code className="tokenValue">{secret.value}</code>
              <div className="inline" style={{ marginTop: "10px" }}>
                <button className="ghost" style={smallButtonStyle} onClick={() => void copySecret(secret.value)}>
                  <Copy size={14} /> {t("settings.keys.copy")}
                </button>
                <button className="ghost" style={smallButtonStyle} onClick={() => setSecret(null)}>
                  {t("settings.keys.secretDone")}
                </button>
              </div>
            </div>
          )}

          {keys.state === "loading" && (
            <p className="muted small" style={{ marginTop: "12px" }}>
              <Loader2 size={14} className="spin" /> {t("settings.keys.loading")}
            </p>
          )}
          {keys.state === "failed" && (
            <p className="decision deny small">{t("settings.keys.failed", { reason: keys.reason })}</p>
          )}
          {keys.state === "ready" && keys.data.length === 0 && (
            <p className="muted small" style={{ marginTop: "12px" }}>{t("settings.keys.empty")}</p>
          )}
          {keys.state === "ready" && keys.data.length > 0 && (
            <div style={{ marginTop: "12px" }}>
              {keys.data.map((key) => (
                <div style={rowStyle} key={key.id} data-automation-id={`key-${key.id}`}>
                  <span>
                    <strong>{key.name}</strong>{" "}
                    <span className="muted small">
                      {key.live ? t("security.keyLive") : t("security.keyRevoked")} ·{" "}
                      {key.lastUsedAt ? t("security.keyUsed") : t("security.keyUnused")}
                    </span>
                  </span>
                  {key.live && (
                    <button
                      className="ghost"
                      style={smallButtonStyle}
                      aria-label={t("security.revokeKeyLabel", { name: key.name })}
                      data-automation-id={`key-revoke-${key.id}`}
                      onClick={() => void revokeKey(key)}
                    >
                      <X size={14} /> {t("settings.keys.revokeButton")}
                    </button>
                  )}
                </div>
              ))}
            </div>
          )}
          {keyError && <p className="decision deny small">{keyError}</p>}
        </div>

        {/* ---------------------------------------------- teammates */}
        <div className="panel settingsPeople" data-automation-id="settings-people">
          <h2><UserPlus size={15} /> {t("settings.people.title")}</h2>
          <p className="muted small">{t("settings.people.lead")}</p>

          {!isHuman ? (
            <p className="muted small" style={{ marginTop: "12px" }}>{t("settings.people.humanOnly")}</p>
          ) : (
            <div className="teammate">
              <button
                className="ghost addTeammateToggle"
                data-automation-id="teammate-toggle"
                onClick={() => setTeammateOpen((open) => !open)}
              >
                {teammateOpen ? t("teammate.cancel") : t("teammate.toggle")}
              </button>

              {teammateOpen && (
                <div className="teammateForm">
                  <p className="muted small">{t("teammate.hint")}</p>

                  {setups.state === "loading" && (
                    <p className="muted small">
                      <Loader2 size={14} className="spin" /> {t("settings.people.setupsLoading")}
                    </p>
                  )}
                  {setups.state === "failed" && (
                    <p className="decision deny small">
                      {t("settings.people.setupsFailed", { reason: setups.reason })}
                    </p>
                  )}
                  {setups.state === "ready" && (
                    <>
                      <label className="profileChoice">
                        {t("settings.people.setupLabel")}
                        <select
                          data-automation-id="teammate-profile"
                          value={teammateSetup}
                          disabled={teammateBusy}
                          onChange={(event) => setTeammateSetup(event.target.value)}
                        >
                          {setups.data.map((option) => (
                            <option key={option.id} value={option.id}>
                              {option.label}
                              {option.imageReady ? "" : t("settings.people.notInstalled")}
                            </option>
                          ))}
                        </select>
                      </label>
                      {chosenSetup && <p className="muted small">{chosenSetup.description}</p>}
                      {/* A teammate can be created before their setup is here, but they
                          cannot open a screen until it is — so offer the download now
                          rather than letting them discover it later. */}
                      {chosenSetup && !chosenSetup.imageReady && (
                        <button
                          className="ghost"
                          data-automation-id="build-desk-image"
                          disabled={chosenSetup.buildStatus === "building"}
                          onClick={() => void installSetup(chosenSetup.id)}
                        >
                          {chosenSetup.buildStatus === "building"
                            ? <><Loader2 size={14} className="spin" /> {t("settings.people.installing")}</>
                            : t("settings.people.installButton")}
                        </button>
                      )}
                    </>
                  )}

                  <input
                    data-automation-id="teammate-name"
                    value={teammateName}
                    placeholder={t("teammate.namePlaceholder")}
                    disabled={teammateBusy}
                    onChange={(event) => setTeammateName(event.target.value)}
                    onKeyDown={(event) => {
                      if (event.key === "Enter") void addTeammate();
                    }}
                  />
                  <button
                    data-automation-id="teammate-add"
                    disabled={teammateBusy || !teammateName.trim()}
                    onClick={() => void addTeammate()}
                  >
                    {teammateBusy
                      ? <><Loader2 size={14} className="spin" /> {t("addProvider.adding")}</>
                      : t("teammate.createButton")}
                  </button>

                  {teammateError && <p className="decision deny small">{teammateError}</p>}
                  {teammateResult && (
                    <div className="teammateToken" data-automation-id="teammate-result">
                      <p className="muted small">{t("teammate.created", { slug: teammateResult.slug })}</p>
                      <code className="tokenValue" data-automation-id="teammate-token">
                        {teammateResult.token}
                      </code>
                      <p className="muted small" style={{ marginTop: "6px" }}>
                        {t("settings.people.tokenNote")}
                      </p>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
