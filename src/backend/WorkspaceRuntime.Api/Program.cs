using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var installedRuntimeRoot = Environment.GetEnvironmentVariable("WORKSPACE_RUNTIME_ROOT");
var repositoryRoot = !string.IsNullOrWhiteSpace(installedRuntimeRoot)
    ? Path.GetFullPath(installedRuntimeRoot)
    : File.Exists(Path.Combine(builder.Environment.ContentRootPath, "config", "branding.json"))
        ? builder.Environment.ContentRootPath
        : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", ".."));
var brandingPath = Path.Combine(repositoryRoot, "config", "branding.json");
builder.Configuration.AddJsonFile(brandingPath, optional: true, reloadOnChange: true);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("ETag"));
});

// Demo population (joche/yulia) is OPT-IN. A shipped, provider-free install has
// NO users until the first owner claims it; only a demo/dev image sets this on
// (Runtime:SeedDemo=true or LUNOS_DEMO=1). Default OFF for a clean first-run.
static bool IsTruthy(string? value) =>
    value is not null && (value == "1"
        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Equals("on", StringComparison.OrdinalIgnoreCase));
var demoEnabled = IsTruthy(builder.Configuration["Runtime:SeedDemo"] ?? Environment.GetEnvironmentVariable("LUNOS_DEMO"));

var databaseProvider = (builder.Configuration["Database:Provider"] ?? "sqlite").ToLowerInvariant();
switch (databaseProvider)
{
    case "memory":
        builder.Services.AddSingleton<IRuntimeStore>(_ => new InMemoryRuntimeStore(demoEnabled));
        break;

    case "postgres":
        var postgresConnection = builder.Configuration["Database:PostgresConnection"]
            ?? throw new InvalidOperationException("Database:PostgresConnection is required when Database:Provider is postgres.");
        builder.Services.AddDbContextFactory<RuntimeDbContext>(options => options.UseNpgsql(postgresConnection));
        builder.Services.AddSingleton<IRuntimeStore>(sp => new EfRuntimeStore(sp.GetRequiredService<IDbContextFactory<RuntimeDbContext>>(), demoEnabled));
        break;

    case "sqlite":
        var sqlitePath = builder.Configuration["Database:SqlitePath"];
        if (string.IsNullOrWhiteSpace(sqlitePath))
        {
            var dataRoot = Path.Combine(repositoryRoot, ".data");
            Directory.CreateDirectory(dataRoot);
            sqlitePath = Path.Combine(dataRoot, "workspace-runtime.db");
        }
        builder.Services.AddDbContextFactory<RuntimeDbContext>(options => options.UseSqlite($"Data Source={sqlitePath}"));
        builder.Services.AddSingleton<IRuntimeStore>(sp => new EfRuntimeStore(sp.GetRequiredService<IDbContextFactory<RuntimeDbContext>>(), demoEnabled));
        break;

    default:
        throw new InvalidOperationException($"Unknown Database:Provider '{databaseProvider}'. Use sqlite, postgres, or memory.");
}

var secretsPath = builder.Configuration["Auth:SecretsPath"];
if (string.IsNullOrWhiteSpace(secretsPath))
{
    secretsPath = Path.Combine(repositoryRoot, ".data", "secrets");
}

builder.Services.AddSingleton<ITokenAuthenticator>(provider =>
    new IdentityTokenAuthenticator(secretsPath, provider.GetRequiredService<IRuntimeStore>()));
// First-run setup: the loopback-gated, single-winner owner claim.
builder.Services.AddSingleton<ISetupService>(provider =>
    new SetupService(provider.GetRequiredService<IRuntimeStore>(), provider.GetRequiredService<ITokenAuthenticator>()));
builder.Services.AddSingleton<ISurfaceRegistry>(_ => new FileSurfaceRegistry(repositoryRoot));
builder.Services.AddSingleton<IExampleCatalog>(_ => new FileExampleCatalog(repositoryRoot));
builder.Services.AddSingleton<ExampleRunner>();
builder.Services.AddSingleton<IRuntimeEventStream, ChannelRuntimeEventStream>();
builder.Services.AddSingleton<IPolicyEngine, ManifestPolicyEngine>();
builder.Services.AddSingleton<SpreadsheetSandboxExecutor>();
builder.Services.AddSingleton<SessionOrchestrator>(sp => new SessionOrchestrator(
    new SessionBackendOptions
    {
        PodmanPath = builder.Configuration["Sessions:PodmanPath"] ?? "podman",
        Image = builder.Configuration["Sessions:Image"] ?? "localhost/lunos-desktop:latest",
        ViewportPort = int.TryParse(builder.Configuration["Sessions:ViewportPort"], out var vp) ? vp : 3000,
        // The desktop chat launcher needs a URL reachable FROM the session container,
        // where the host's loopback is not the host. Default: take Chat:Url and swap a
        // loopback host for podman's host alias. Override with Sessions:ChatUrl.
        ChatUrl = builder.Configuration["Sessions:ChatUrl"]
            ?? SessionReachableChatUrl(builder.Configuration["Chat:Url"])
    },
    owner => Ownership.RootUserSlug(owner, sp.GetRequiredService<IRuntimeStore>()),
    // A session's desktop image follows the desk profile of the USER who owns it —
    // an agent inherits its owner's toolchain, because it works on the same desk.
    owner =>
    {
        var store = sp.GetRequiredService<IRuntimeStore>();
        var rootSlug = Ownership.RootUserSlug(owner, store) ?? owner;
        var user = store.Users.FirstOrDefault(candidate => candidate.Slug == rootSlug);
        return DeskProfiles.Resolve(user?.DeskProfile);
    }));
builder.Services.AddSingleton<ISessionBackend>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<IConsoleBackend>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<IDesktopBackend>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<IBrowserBackend>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<IRecorderBackend>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<IHomeBrowser>(provider => new PodmanHomeBrowser(new SessionBackendOptions
{
    PodmanPath = builder.Configuration["Sessions:PodmanPath"] ?? "podman"
}));
builder.Services.AddSingleton<ISurfaceExecutor>(provider => provider.GetRequiredService<SpreadsheetSandboxExecutor>());
builder.Services.AddSingleton<ISurfaceExecutor>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<ISurfaceExecutor>(provider => new ConsoleSurfaceExecutor(provider.GetRequiredService<IConsoleBackend>()));
builder.Services.AddSingleton<ISurfaceExecutor>(provider => new DesktopSurfaceExecutor(provider.GetRequiredService<IDesktopBackend>()));
builder.Services.AddSingleton<ISurfaceExecutor>(provider => new BrowserSurfaceExecutor(provider.GetRequiredService<IBrowserBackend>()));
builder.Services.AddSingleton<ISurfaceExecutor>(provider => new RecorderSurfaceExecutor(provider.GetRequiredService<IRecorderBackend>()));
// V0.6 per-session input grant: a time-boxed lease that upgrades desktop
// typing/keys to Allow (in-memory: a restart drops all input authority).
builder.Services.AddSingleton<ISessionInputGrants, InMemorySessionInputGrants>();
builder.Services.AddSingleton<ISessionVisionConsent, InMemorySessionVisionConsent>();
builder.Services.AddSingleton<ISurfaceExecutor>(provider => new SessionInputExecutor(
    provider.GetRequiredService<ISessionInputGrants>(),
    provider.GetRequiredService<ISessionVisionConsent>()));
builder.Services.AddSingleton<SurfaceExecutorRouter>(provider => new SurfaceExecutorRouter(provider.GetServices<ISurfaceExecutor>()));
builder.Services.AddSingleton<ISandboxedToolExecutor>(provider => provider.GetRequiredService<SurfaceExecutorRouter>());
builder.Services.AddSingleton<IDryRunToolExecutor>(provider => provider.GetRequiredService<SurfaceExecutorRouter>());
builder.Services.AddSingleton<AgentRuntime>();
// The ledger behind issue #14: every model call recorded, per-desk and per-agent
// ceilings enforced. Registered before the loops so they can take it.
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<ISessionStore>(sp => databaseProvider == "memory"
    ? new InMemorySessionStore()
    : new EfSessionStore(sp.GetRequiredService<IDbContextFactory<RuntimeDbContext>>()));
builder.Services.AddSingleton<IApiKeyStore>(sp => databaseProvider == "memory"
    ? new InMemoryApiKeyStore()
    : new EfApiKeyStore(sp.GetRequiredService<IDbContextFactory<RuntimeDbContext>>()));
builder.Services.AddSingleton<ITokenLedger>(sp => databaseProvider == "memory"
    // The memory provider registers no DbContext at all, and the loops ask the
    // ledger about the budget on every step — an unresolvable service there would
    // fail the run rather than skip the accounting.
    ? new InMemoryTokenLedger()
    : new EfTokenLedger(sp.GetRequiredService<IDbContextFactory<RuntimeDbContext>>()));
builder.Services.AddSingleton<ConsoleAgentLoop>();

// Model providers, tagged by capability (chat / vision) and locality, resolved
// through a layered registry: agent -> user -> OS (see docs/model-config.md).
// Keys are read from config/env and never logged. gpt-4.1-mini serves BOTH chat
// and vision. The local Bonsai provider is keyless and on-box.
var providerProfiles = new List<ProviderProfile>();

var deepseekKey = builder.Configuration["Inference:Deepseek:ApiKey"]
    ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
if (!string.IsNullOrWhiteSpace(deepseekKey))
{
    providerProfiles.Add(new ProviderProfile(
        "deepseek", "DeepSeek", "openai-compatible",
        builder.Configuration["Inference:Deepseek:BaseUrl"] ?? "https://api.deepseek.com",
        builder.Configuration["Inference:Deepseek:Model"] ?? "deepseek-chat",
        new HashSet<string> { "chat" }, "cloud", deepseekKey));
}

var azureKey = builder.Configuration["Inference:Azure:ApiKey"]
    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
if (!string.IsNullOrWhiteSpace(azureKey))
{
    providerProfiles.Add(new ProviderProfile(
        builder.Configuration["Inference:Azure:Provider"] ?? "gpt-4.1-mini",
        "Azure OpenAI gpt-4.1-mini", "azure-openai",
        builder.Configuration["Inference:Azure:BaseUrl"] ?? "https://sivar-aoai-eus.openai.azure.com/openai/v1",
        builder.Configuration["Inference:Azure:Model"] ?? "gpt-4.1-mini",
        new HashSet<string> { "chat", "vision" }, "cloud", azureKey));
}

// The on-box option: local Bonsai over llama.cpp (keyless). Registered ONLY when
// enabled — otherwise a fresh machine with no model server would resolve to it
// and every agent turn would fail with a connection error. Off by default (no
// weights are bundled in the test release); a no-cloud image sets
// Inference:Local:Enabled=true once the local server is present.
var localEnabled = IsTruthy(builder.Configuration["Inference:Local:Enabled"]);
if (localEnabled)
{
    providerProfiles.Add(new ProviderProfile(
        "local-bonsai", "PrismML Bonsai 4B", "local-llamacpp",
        builder.Configuration["Inference:Local:BaseUrl"] ?? "http://127.0.0.1:8080/v1",
        builder.Configuration["Inference:Local:Model"] ?? "bonsai",
        new HashSet<string> { "chat" }, "on-box", null));
}

// OS defaults per capability. Behavior-neutral for now: prefer DeepSeek if
// configured (today's effective default); a shipped no-cloud image sets
// Inference:DefaultChatProvider=local-bonsai instead.
var osModelDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var chatDefault = builder.Configuration["Inference:DefaultChatProvider"]
    ?? providerProfiles.FirstOrDefault(profile => profile.Id == "deepseek")?.Id
    ?? providerProfiles.FirstOrDefault(profile => profile.Serves("chat"))?.Id;
if (chatDefault is not null)
{
    osModelDefaults["chat"] = chatDefault;
}
var visionDefault = builder.Configuration["Inference:DefaultVisionProvider"]
    ?? providerProfiles.FirstOrDefault(profile => profile.Serves("vision"))?.Id;
if (visionDefault is not null)
{
    osModelDefaults["vision"] = visionDefault;
}

builder.Services.AddSingleton<IUserModelConfig, EmptyUserModelConfig>();
// The mutable provider layer (the models surface): runtime-added providers + OS
// default overrides, persisted 0600. The registry reads it live, so a provider
// added from the panel is usable with no restart.
builder.Services.AddSingleton<IProviderConfigStore>(_ => new ProviderConfigStore(secretsPath));
builder.Services.AddSingleton<IModelRegistry>(sp =>
    new ModelRegistry(providerProfiles, osModelDefaults, sp.GetRequiredService<IUserModelConfig>(), sp.GetRequiredService<IProviderConfigStore>()));

// The fallback brain when no chat provider resolves for an agent. On a demo image
// that is the deterministic RecipeConsoleBrain (shows the loop with no model); on
// a real, provider-free install it is the UnconfiguredBrain, which ends the turn
// with an honest "set a key and restart" message instead of a connection error.
IConsoleAgentBrain fallbackBrain = demoEnabled ? new RecipeConsoleBrain() : new UnconfiguredBrain();
builder.Services.AddSingleton<IConsoleBrainRegistry>(sp => new ConsoleBrainRegistry(
    sp.GetRequiredService<IModelRegistry>(),
    fallbackBrain,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConsoleBrainRegistry>(),
    // Without this the brains are built with a plain HttpClient and nothing is
    // ever counted — the ledger exists and stays empty.
    sp.GetRequiredService<ITokenLedger>()));
// A default single brain for any injection site that does not select per-agent.
builder.Services.AddSingleton<IConsoleAgentBrain>(sp =>
    sp.GetRequiredService<IConsoleBrainRegistry>().ResolveDefault().Brain);

// The desktop brain is resolved per agent through the model registry: a `chat`
// provider grounds on the AT-SPI element list (default, no screenshot), and a
// `vision` provider (if any) is the screenshot fallback. AT-SPI-first means the
// default path needs no VLM and nothing leaves the box.
builder.Services.AddSingleton<DesktopAgentLoop>();
builder.Services.AddSingleton<IDesktopBrainRegistry>(sp => new DesktopBrainRegistry(
    sp.GetRequiredService<IModelRegistry>(),
    sp.GetRequiredService<ILoggerFactory>(),
    sp.GetRequiredService<ITokenLedger>()));

// Watches each owner's shared inbox.md (e.g. from the desktop "Message Agent"
// launcher) and dispatches new messages to their agent, reply in outbox.md.
builder.Services.AddHostedService<WorkspaceRuntime.Api.SharedInboxWatcher>();
builder.Services.AddHttpClient<ILocalInferenceRouter, LocalInferenceRouter>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.AddSingleton<ILocalInferenceRegistry>(_ =>
{
    return new FileLocalInferenceRegistry(repositoryRoot);
});

var app = builder.Build();

// Eager bootstrap: run migrations and seed identities FIRST, then mint their
// tokens, then load surface manifests (fail fast on a malformed contract) —
// a fresh install must have its token files before anyone can present one,
// and the token authenticator resolves slugs against the seeded identities.
_ = app.Services.GetRequiredService<IRuntimeStore>().Users;
_ = app.Services.GetRequiredService<ITokenAuthenticator>();
_ = app.Services.GetRequiredService<ISurfaceRegistry>();

var modelRegistry = app.Services.GetRequiredService<IModelRegistry>();
app.Logger.LogInformation(
    "Model providers: [{Providers}]; defaults chat='{Chat}', vision='{Vision}'.",
    string.Join(", ", modelRegistry.Providers.Select(p => $"{p.Id}[{string.Join('/', p.Capabilities)}]")),
    modelRegistry.OsDefault("chat") ?? "none", modelRegistry.OsDefault("vision") ?? "none");

app.UseCors();

// Serve the built panel (a self-contained image boots straight to it, no Vite).
// The static assets are public and served BEFORE the auth middleware, so a fresh
// machine can load the first-run wizard with no token; the API calls the panel
// then makes still pass the bearer/loopback gates. Absent (dev without a build),
// this is skipped and "/" falls through to the /api/branding redirect + Vite.
var panelPath = builder.Configuration["Panel:Path"];
if (string.IsNullOrWhiteSpace(panelPath))
{
    panelPath = Path.Combine(repositoryRoot, "panel");
}
var panelServed = Directory.Exists(panelPath) && File.Exists(Path.Combine(panelPath, "index.html"));
if (panelServed)
{
    var panelFiles = new PhysicalFileProvider(Path.GetFullPath(panelPath));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = panelFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = panelFiles });
    app.Logger.LogInformation("Serving panel from {PanelPath}.", panelPath);
}

// Principal resolution: every route not explicitly public requires a bearer
// token; approval verbs additionally require the human principal.
app.Use(async (context, next) =>
{
    var level = AccessPolicy.Required(context.Request.Path, context.Request.Method);
    if (level != AccessLevel.Public)
    {
        var store = context.RequestServices.GetRequiredService<IRuntimeStore>();
        var now = DateTimeOffset.UtcNow;
        RuntimePrincipal? principal = null;

        // 1. A signed-in person, via the session cookie. Only honoured with the
        //    panel header: a cross-site form post cannot set a custom header
        //    without a preflight we never grant, so this is what stops the cookie
        //    from being a CSRF hole (issue #9).
        var cookie = context.Request.Cookies[CredentialFormat.SessionCookie];
        if (!string.IsNullOrEmpty(cookie) && context.Request.Headers.ContainsKey(CredentialFormat.PanelHeader))
        {
            var sessions = context.RequestServices.GetRequiredService<ISessionStore>();
            if (sessions.Resolve(cookie, now) is { } session)
            {
                var user = store.Users.FirstOrDefault(candidate => candidate.Id == session.UserId);
                if (user is not null)
                {
                    sessions.Touch(session.Id, now);
                    principal = new RuntimePrincipal(PrincipalKind.Human, user.Id, user.Slug, user.DisplayName);
                    context.Items["session"] = session.Id;
                }
            }
        }

        var header = context.Request.Headers.Authorization.ToString();
        var bearer = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : "";

        // 2. A program, via a named revocable key. Checked before the legacy token
        //    so an integration never needs the owner's own credential.
        if (principal is null && bearer.StartsWith(CredentialFormat.ApiKeyPrefix, StringComparison.Ordinal))
        {
            var keys = context.RequestServices.GetRequiredService<IApiKeyStore>();
            if (keys.Resolve(bearer, now) is { } key)
            {
                var user = store.Users.FirstOrDefault(candidate => candidate.Id == key.OwnerUserId);
                if (user is not null)
                {
                    keys.MarkUsed(key.Id, now);
                    principal = new RuntimePrincipal(PrincipalKind.Human, user.Id, user.Slug, user.DisplayName);
                    context.Items["apiKey"] = key.Id;
                }
            }
        }

        // 3. The legacy identity token: still how AGENTS authenticate, and still
        //    accepted for people so an upgrade does not lock anyone out. It is a
        //    capability, not a login — deterministic and eternal — which is the
        //    whole reason the two above exist.
        if (principal is null && bearer.Length > 0)
        {
            principal = context.RequestServices.GetRequiredService<ITokenAuthenticator>().Authenticate(bearer);
        }

        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Sign in, or present an API key or identity token." });
            return;
        }

        if (level == AccessLevel.HumanOnly && principal.Kind != PrincipalKind.Human)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This operation requires a human principal." });
            return;
        }

        // An API key acts AS a person but is not one. Human-only routes are the
        // owner's own decisions — minting and revoking credentials, signing out
        // everywhere, adding users, approving an agent's request — and a leaked
        // integration key must not be able to make them. Otherwise the key that
        // exists so the chat need not hold the owner's credential would be able
        // to do everything that credential could.
        if (level == AccessLevel.HumanOnly && context.Items.ContainsKey("apiKey"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "An API key cannot do this. Sign in as yourself for credential and owner actions."
            });
            return;
        }

        context.Items["principal"] = principal;
    }

    await next();
});

// When the panel is served, "/" is index.html (via UseDefaultFiles) — registering
// a "/" endpoint here would make routing claim it and skip the static file. Only
// map the dev redirect when there is no panel to serve.
if (!panelServed)
{
    app.MapGet("/", () => Results.Redirect("/api/branding"));
}

app.MapGet("/api/branding", (IConfiguration configuration) =>
{
    var section = configuration.GetSection("Branding");
    return Results.Ok(new
    {
        productName = section["ProductName"] ?? "Workspace Runtime",
        shortName = section["ShortName"] ?? "Runtime",
        companyName = section["CompanyName"] ?? "Workspace Runtime Labs",
        supportName = section["SupportName"] ?? "Support",
        agentName = section["AgentName"] ?? "Assistant",
        // Where the full chat UI lives (Open WebUI talking to /v1/agent). Empty when
        // it is not deployed - the panel then explains how to enable it instead of
        // linking nowhere. Set with Chat__Url.
        chatUrl = configuration["Chat:Url"] ?? ""
    });
});

// First-run setup. Public (a fresh machine has no token yet), but claim is guarded
// in-handler: only from loopback, and only while unclaimed (single-winner).
// `owner` is for on-box services that must act as the owner (the chat UI reads it
// to find whose token to present) and is withheld from remote callers, on the same
// loopback rule that governs the claim itself — a name is not a secret, but an
// unclaimed box should not announce who lives there. It is null once the box has
// more than one human: see SetupService.OwnerSlug for why guessing is worse.
app.MapGet("/api/setup/status", (HttpContext context, ISetupService setup) => Results.Ok(new
{
    claimed = setup.IsClaimed(),
    owner = IsLoopback(context.Connection.RemoteIpAddress) ? setup.OwnerSlug() : null
}));

app.MapPost("/api/setup/claim", (ClaimRequest? request, HttpContext context, ISetupService setup, ISessionBackend sessions) =>
{
    var result = setup.Claim(request?.Name, IsLoopback(context.Connection.RemoteIpAddress), request?.DeskProfile);
    if (result.Outcome == ClaimOutcome.Ok)
    {
        // "Built on first use" has to mean it: choosing a developer desk in the
        // wizard and then being told the image is missing, with the only build
        // button hidden in a form about teammates, is not a first use.
        StartDeskImageBuildIfMissing(request?.DeskProfile, sessions);
    }

    return result.Outcome switch
    {
        ClaimOutcome.Ok => Results.Ok(new { result.Slug, result.Token }),
        ClaimOutcome.AlreadyClaimed => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
        ClaimOutcome.Forbidden => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.BadRequest(new { error = result.Error })
    };
});

// What a desk can be created as. Public, like /api/branding: the claim wizard has
// to offer the choice before anyone has a token.
app.MapGet("/api/desk-profiles", async (ISessionBackend sessions, CancellationToken cancellationToken) =>
{
    var profiles = new List<object>();
    foreach (var profile in DeskProfiles.All)
    {
        // The panel says "this desk needs an image that is not built yet" rather
        // than letting a person pick a profile and meet a failure at session time.
        // Ready means BOTH halves of the desk: the desktop the person opens and
        // the console their agent works in.
        var ready = (!profile.NeedsOwnImage || await sessions.ImageExistsAsync(profile.Image, cancellationToken))
            && (!profile.NeedsOwnConsoleImage || await sessions.ImageExistsAsync(profile.ConsoleImage, cancellationToken));
        profiles.Add(new
        {
            profile.Id,
            profile.Label,
            profile.Description,
            isDefault = profile.Id == DeskProfiles.DefaultId,
            imageReady = ready,
            buildStatus = (sessions as SessionOrchestrator)?.BuildStatus(profile.Id) ?? ""
        });
    }

    return Results.Ok(profiles);
});

// Building a desk image is an owner's decision (it costs gigabytes and a long
// download), and it returns immediately: the build runs in the background and the
// list endpoint above reports its progress.
app.MapPost("/api/desk-profiles/{id}/build", async (string id, HttpContext context, ISessionBackend sessions, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    if (!DeskProfiles.IsKnown(id))
    {
        return Results.NotFound(new { error = $"No desk profile '{id}'." });
    }

    var profile = DeskProfiles.Resolve(id);

    // Already built is a success, not a reason to spend another twenty minutes:
    // a stale panel must not be able to trigger a rebuild by clicking twice.
    var desktopReady = !profile.NeedsOwnImage || await sessions.ImageExistsAsync(profile.Image, cancellationToken);
    var consoleReady = !profile.NeedsOwnConsoleImage || await sessions.ImageExistsAsync(profile.ConsoleImage, cancellationToken);
    if (desktopReady && consoleReady)
    {
        return Results.Ok(new { profile.Id, status = "built" });
    }
    if (sessions is not SessionOrchestrator orchestrator)
    {
        return Results.Json(new { error = "This runtime cannot build images." }, statusCode: StatusCodes.Status501NotImplemented);
    }

    var imagesRoot = builder.Configuration["Sessions:ProfileImagesPath"] ?? "/var/lib/cielo/images/profiles";
    if (!Directory.Exists(Path.Combine(imagesRoot, profile.Id)))
    {
        return Results.Json(new { error = $"No build context for '{profile.Id}' under {imagesRoot}." }, statusCode: StatusCodes.Status409Conflict);
    }

    var caller = Caller(context);
    orchestrator.StartImageBuild(profile, imagesRoot, VsCodeDebForThisMachine());
    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "desk.image.build", AuditOutcome.Success, $"Started building the '{profile.Id}' desk image."));

    return Results.Accepted($"/api/desk-profiles", new { profile.Id, status = orchestrator.BuildStatus(profile.Id) });
});

// What has been spent, and against what ceiling. Readable by any principal —
// an agent that cannot see its own budget cannot explain why it stopped.
app.MapGet("/api/usage", (HttpContext context, ITokenLedger ledger, IRuntimeStore store) =>
{
    var caller = Caller(context);
    var (userId, agentId) = ActingAgent(caller, null, store);
    // Two numbers, because they answer different questions: what counts against
    // a ceiling, and what the machine actually did.
    var spent = ledger.SpentThisMonth(userId, agentId, billableOnly: true);
    var all = ledger.SpentThisMonth(userId, agentId, billableOnly: false);
    var limits = ledger.Limits;

    return Results.Ok(new
    {
        month = DateTimeOffset.UtcNow.ToString("yyyy-MM"),
        // The caller's own id, so the panel can set a ceiling for this desk
        // without a second round-trip to work out who it is talking about.
        deskSubject = userId,
        desk = spent.User,
        agent = spent.Agent,
        machine = spent.Machine,
        // Including on-box, which never counts against a ceiling.
        deskAll = all.User,
        machineAll = all.Machine,
        // Only the ceilings that bind THIS caller: another desk's budget is not
        // this desk's business.
        limits = limits
            .Where(limit => limit.Scope == TokenLimit.OsScope
                || (limit.Scope == TokenLimit.UserScope && limit.Subject == userId.ToString())
                || (limit.Scope == TokenLimit.AgentScope && limit.Subject == agentId.ToString()))
            .Select(limit => new { limit.Scope, limit.Subject, limit.MonthlyTokens }),
        deskLimit = limits.FirstOrDefault(limit => limit.Scope == TokenLimit.UserScope && limit.Subject == userId.ToString())?.MonthlyTokens ?? 0,
        machineLimit = limits.FirstOrDefault(limit => limit.Scope == TokenLimit.OsScope)?.MonthlyTokens ?? 0,
        recent = ledger.Recent(10, userId).Select(usage => new
        {
            usage.OccurredAt,
            usage.ProviderId,
            usage.Model,
            usage.Locality,
            usage.PromptTokens,
            usage.CompletionTokens
        })
    });
});

// Setting a ceiling is an owner's decision, like adding a provider: it decides
// what someone else's agent may spend.
app.MapPost("/api/usage/limits", (SetTokenLimitRequest? request, HttpContext context, ITokenLedger ledger, IRuntimeStore store) =>
{
    var scope = (request?.Scope ?? "").Trim().ToLowerInvariant();
    if (scope is not (TokenLimit.UserScope or TokenLimit.AgentScope or TokenLimit.OsScope))
    {
        return Results.BadRequest(new { error = "scope must be 'user', 'agent' or 'os'." });
    }

    // Canonical form, not what was typed: a limit stored as "{ABC...}" or in
    // uppercase parses fine and then never matches the id the budget check
    // compares against — a ceiling that silently does nothing.
    var caller = Caller(context);

    // The same ownership boundary as everything else: a human sets budgets for
    // their own desk and the agents they own, and nobody sets one for a desk they
    // do not own. Without this, one teammate could cut off another's agent.
    var canonical = "";
    if (scope == TokenLimit.OsScope)
    {
        // Machine-wide policy is set ON the machine, the same rule the first-owner
        // claim uses — there is no administrator role to check against yet (#9).
        if (!IsLoopback(context.Connection.RemoteIpAddress))
        {
            return Results.Json(
                new { error = "A machine-wide budget can only be set from the machine itself (localhost or your SSH tunnel)." },
                statusCode: StatusCodes.Status403Forbidden);
        }
    }
    else
    {
        var subject = (request?.Subject ?? "").Trim();
        if (!Guid.TryParse(subject, out var subjectId))
        {
            return Results.BadRequest(new { error = $"a '{scope}' limit needs the subject's id." });
        }

        var owned = scope == TokenLimit.UserScope
            ? subjectId == caller.Subject
            : store.Agents.Any(agent => agent.Id == subjectId && agent.OwnerUserId == caller.Subject);

        if (!owned)
        {
            return Results.Json(
                new { error = $"'{caller.Slug}' may only set a budget for its own desk and the agents it owns." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        canonical = subjectId.ToString();
    }
    var tokens = request?.MonthlyTokens ?? 0;
    ledger.SetLimit(new TokenLimit(scope, canonical, tokens));

    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "usage.limit", AuditOutcome.Success,
        tokens > 0
            ? $"Set the {scope} model budget to {tokens:N0} tokens a month{(scope == TokenLimit.OsScope ? "" : $" for {canonical}")}."
            : $"Removed the {scope} model budget{(scope == TokenLimit.OsScope ? "" : $" for {canonical}")}."));

    return Results.Ok(new { scope, subject = canonical, monthlyTokens = tokens });
});

// --- Sign in, sign out, and the credentials programs use (issue #9) ---------
//
// A password proves a person is who they say they are; a session carries that
// proof and can be ended; an API key lets a program act without holding the
// person's own credential. The legacy identity token still works — agents use
// it — but it is a capability, not a login.

app.MapPost("/api/auth/login", (LoginRequest? request, HttpContext context, IRuntimeStore store, IPasswordHasher hasher, ISessionStore sessions, LoginThrottle throttle) =>
{
    var slug = (request?.Slug ?? "").Trim().ToLowerInvariant();
    var password = request?.Password ?? "";
    var now = DateTimeOffset.UtcNow;

    // Counted by SOURCE, never by desk. Blocking per account looks prudent and is
    // an own goal: anyone who knows a desk name could then lock its owner out for
    // fifteen minutes at a time, from anywhere, forever. Throttling the source
    // costs the attacker their own address instead.
    //
    // Checked before anything is verified, so a blocked attempt costs no PBKDF2 —
    // otherwise the throttle becomes the denial-of-service it exists to prevent.
    var throttleKey = $"source:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    if (throttle.RetryAfter(throttleKey, now) is { } wait)
    {
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
        return Results.Json(
            new { error = $"Too many failed sign-ins from here. Try again in {Math.Ceiling(wait.TotalMinutes)} minute(s)." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }
    var user = store.Users.FirstOrDefault(candidate => candidate.Slug == slug);
    var hash = user is null ? null : store.PasswordHashFor(user.Id);

    // One message for "no such user", "no password set" and "wrong password":
    // telling them apart tells an attacker which desks exist and which are
    // unprotected. The failure is also deliberately slow — verifying against a
    // dummy hash — so a missing user is not measurably faster than a wrong one.
    if (user is null || hash is null || !hasher.Verify(password, hash))
    {
        if (user is null || hash is null)
        {
            hasher.Verify(password, hasher.DummyHash);
        }

        throttle.Failed(throttleKey, now);

        // Recorded even though it does not lock anything: an owner should be able
        // to see that someone is guessing at their desk, which is the part of
        // per-account tracking worth keeping.
        if (user is not null)
        {
            store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, null,
                "auth.login", AuditOutcome.Blocked, $"Failed sign-in for '{user.Slug}'."));
        }

        return Results.Json(new { error = "That name and password do not match." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    throttle.Succeeded(throttleKey);

    var (session, secret) = sessions.Create(user.Id, TimeSpan.FromDays(14));
    context.Response.Cookies.Append(CredentialFormat.SessionCookie, secret, SessionCookieOptions(context, session.ExpiresAt));
    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, null,
        "auth.login", AuditOutcome.Success, $"'{user.Slug}' signed in."));

    return Results.Ok(new { user.Slug, user.DisplayName, expiresAt = session.ExpiresAt });
});

app.MapPost("/api/auth/logout", (HttpContext context, ISessionStore sessions, IRuntimeStore store) =>
{
    // Actually ends the session server-side, which is the point: clearing local
    // storage never invalidated anything.
    if (context.Items.TryGetValue("session", out var raw) && raw is Guid sessionId)
    {
        sessions.Revoke(sessionId);
    }

    context.Response.Cookies.Delete(CredentialFormat.SessionCookie);
    var caller = Caller(context);
    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "auth.logout", AuditOutcome.Success, $"'{caller.Slug}' signed out."));
    return Results.Ok(new { signedOut = true });
});

app.MapPost("/api/auth/logout-all", (HttpContext context, ISessionStore sessions, IRuntimeStore store) =>
{
    var caller = Caller(context);
    var count = sessions.RevokeAllFor(caller.Subject);
    context.Response.Cookies.Delete(CredentialFormat.SessionCookie);
    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "auth.logout-all", AuditOutcome.Success, $"'{caller.Slug}' ended {count} session(s)."));
    return Results.Ok(new { endedSessions = count });
});

app.MapPost("/api/auth/password", (SetPasswordRequest? request, HttpContext context, IRuntimeStore store, IPasswordHasher hasher, ISessionStore sessions) =>
{
    var caller = Caller(context);
    var next = request?.NewPassword ?? "";
    if (next.Length < 10)
    {
        // Length over composition rules: a long passphrase beats a short one with
        // a digit and a symbol, and nothing here can check a breach corpus.
        return Results.BadRequest(new { error = "A password needs at least 10 characters." });
    }

    var existing = store.PasswordHashFor(caller.Subject);
    if (existing is not null)
    {
        if (!hasher.Verify(request?.CurrentPassword ?? "", existing))
        {
            return Results.Json(new { error = "The current password does not match." }, statusCode: StatusCodes.Status403Forbidden);
        }
    }
    else if (!IsLoopback(context.Connection.RemoteIpAddress))
    {
        // Upgrading an install: the owner has no password yet and cannot prove one.
        // Setting the first one is therefore loopback-only, the same gate the
        // first-owner claim uses — otherwise anyone holding the (permanent, and
        // possibly leaked) identity token could set it from anywhere.
        return Results.Json(
            new { error = "Set your first password on the machine itself (localhost or your SSH tunnel)." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    store.SetPasswordHash(caller.Subject, hasher.Hash(next));

    // Changing a password ends every other session: that is what a person expects
    // it to do after losing a laptop.
    var ended = sessions.RevokeAllFor(caller.Subject);
    var (session, secret) = sessions.Create(caller.Subject, TimeSpan.FromDays(14));
    context.Response.Cookies.Append(CredentialFormat.SessionCookie, secret, SessionCookieOptions(context, session.ExpiresAt));

    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "auth.password", AuditOutcome.Success,
        existing is null
            ? $"'{caller.Slug}' set a password for the first time."
            : $"'{caller.Slug}' changed their password, ending {ended} session(s)."));

    // "Other" sessions means other than the one being used — and there only IS a
    // current session when the caller came in on a cookie. Called with a legacy
    // identity token, every revoked session was somebody's browser, so subtracting
    // one would under-report it (and say zero when a browser really was signed out).
    var endedOthers = context.Items.ContainsKey("session") ? Math.Max(0, ended - 1) : ended;
    return Results.Ok(new { passwordSet = true, endedSessions = endedOthers });
});

// Named, revocable credentials for programs — the fix for an integration having
// to embed the owner's master token (issue #9, point 6; #8's chat is the case).
app.MapGet("/api/keys", (HttpContext context, IApiKeyStore keys, ISessionStore sessions) =>
{
    var caller = Caller(context);
    return Results.Ok(new
    {
        keys = keys.For(caller.Subject).Select(key => new
        {
            key.Id,
            key.Name,
            key.CreatedAt,
            key.ExpiresAt,
            key.RevokedAt,
            key.LastUsedAt,
            live = key.IsLive(DateTimeOffset.UtcNow)
        }),
        sessions = sessions.For(caller.Subject).Select(session => new
        {
            session.Id,
            session.CreatedAt,
            session.LastSeenAt,
            session.ExpiresAt,
            session.RevokedAt,
            live = session.IsLive(DateTimeOffset.UtcNow)
        })
    });
});

app.MapPost("/api/keys", (CreateApiKeyRequest? request, HttpContext context, IApiKeyStore keys, IRuntimeStore store) =>
{
    var caller = Caller(context);
    var name = (request?.Name ?? "").Trim();
    if (name.Length == 0)
    {
        // A key nobody can identify is a key nobody will ever dare revoke.
        return Results.BadRequest(new { error = "Give the key a name, so you know what to revoke later." });
    }

    var lifetime = request?.ExpiresInDays is > 0 ? TimeSpan.FromDays(request.ExpiresInDays.Value) : (TimeSpan?)null;
    var (key, secret) = keys.Create(caller.Subject, name, lifetime);

    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "auth.key.create", AuditOutcome.Success, $"'{caller.Slug}' created API key '{key.Name}'."));

    // The secret is returned exactly once and never stored — only its hash is.
    return Results.Ok(new { key.Id, key.Name, key.ExpiresAt, secret });
});

app.MapDelete("/api/keys/{id}", (Guid id, HttpContext context, IApiKeyStore keys, IRuntimeStore store) =>
{
    var caller = Caller(context);
    if (!keys.Revoke(id, caller.Subject))
    {
        return Results.NotFound(new { error = "No such key of yours." });
    }

    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "auth.key.revoke", AuditOutcome.Success, $"'{caller.Slug}' revoked API key {id}."));
    return Results.Ok(new { revoked = id });
});

app.MapGet("/api/users", (IRuntimeStore store) => store.Users);

// Add a teammate (an existing owner invites another user). Human-only (enforced
// in AccessPolicy). Returns the new user's slug + bearer token for the owner to
// hand over; the token file is also written 0600 on the box.
app.MapPost("/api/users", (AddUserRequest? request, HttpContext context, ISetupService setup, IRuntimeStore store, ISessionBackend sessions) =>
{
    var result = setup.AddUser(request?.Name, request?.DeskProfile);
    if (result.Outcome == AddUserOutcome.Ok)
    {
        // Same as the claim: a desk created is a desk that should become usable
        // without anyone having to know an image needs building.
        StartDeskImageBuildIfMissing(request?.DeskProfile, sessions);

        // What kind of desk someone was given is part of who did what: it decides
        // the toolchain they get and what their agent may do.
        var profile = DeskProfiles.Resolve(request?.DeskProfile);
        var caller = Caller(context);
        store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
            "user.add", AuditOutcome.Success, $"Added '{result.Slug}' as a {profile.Label} desk."));
    }

    return result.Outcome switch
    {
        AddUserOutcome.Ok => Results.Ok(new { result.Slug, result.Token }),
        AddUserOutcome.Conflict => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.BadRequest(new { error = result.Error })
    };
});

// The models surface: list/add/remove providers and set OS defaults per capability.
// GET is AnyPrincipal; mutations are human-only (AccessPolicy). API keys are NEVER
// returned (only a hasKey flag), never logged, never placed in audit detail.
app.MapGet("/api/models", (IModelRegistry registry, IProviderConfigStore store) =>
{
    var managed = store.All().Select(profile => profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var providers = registry.Providers
        .OrderBy(profile => profile.Id, StringComparer.Ordinal)
        .Select(profile => new
        {
            profile.Id,
            profile.DisplayName,
            profile.Kind,
            profile.BaseUrl,
            profile.Model,
            capabilities = profile.Capabilities.OrderBy(capability => capability, StringComparer.Ordinal).ToArray(),
            profile.Locality,
            hasKey = !string.IsNullOrWhiteSpace(profile.ApiKey),
            managed = managed.Contains(profile.Id)
        });
    return Results.Ok(new
    {
        providers,
        defaults = new { chat = registry.OsDefault("chat"), vision = registry.OsDefault("vision") }
    });
});

app.MapPost("/api/models", (AddProviderRequest request, HttpContext context, IProviderConfigStore store, IModelRegistry registry, IRuntimeStore runtimeStore) =>
{
    var displayName = (request.DisplayName ?? "").Trim();
    var baseUrl = (request.BaseUrl ?? "").Trim();
    var model = (request.Model ?? "").Trim();
    var kind = string.IsNullOrWhiteSpace(request.Kind) ? "openai-compatible" : request.Kind!.Trim();
    var locality = string.IsNullOrWhiteSpace(request.Locality) ? "cloud" : request.Locality!.Trim().ToLowerInvariant();
    var capabilities = (request.Capabilities ?? new List<string>())
        .Select(capability => capability.Trim().ToLowerInvariant())
        .Where(capability => capability.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var allowedCapabilities = new[] { "chat", "vision", "embedding" };
    var allowedLocalities = new[] { "on-box", "remote-self-hosted", "cloud" };

    if (displayName.Length == 0) return Results.BadRequest(new { error = "A display name is required." });
    if (model.Length == 0) return Results.BadRequest(new { error = "A model name is required." });
    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https"))
        return Results.BadRequest(new { error = "Base URL must be an absolute http(s) URL." });
    if (capabilities.Count == 0) return Results.BadRequest(new { error = "Select at least one capability." });
    if (capabilities.Any(capability => !allowedCapabilities.Contains(capability)))
        return Results.BadRequest(new { error = $"Capabilities must be among: {string.Join(", ", allowedCapabilities)}." });
    if (!allowedLocalities.Contains(locality))
        return Results.BadRequest(new { error = $"Locality must be one of: {string.Join(", ", allowedLocalities)}." });

    var apiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey!.Trim();
    var added = store.Add(new ProviderDraft(displayName, kind, baseUrl, model, capabilities, locality, apiKey));

    foreach (var capability in (request.DefaultFor ?? new List<string>()).Select(value => value.Trim().ToLowerInvariant()))
    {
        if (added.Serves(capability)) store.SetOsDefault(capability, added.Id);
    }

    var caller = Caller(context);
    runtimeStore.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "model.add", AuditOutcome.Success,
        $"Added provider '{added.DisplayName}' ({added.Kind}, {string.Join('/', added.Capabilities)}, {added.Locality})."));

    return Results.Ok(new { added.Id, added.DisplayName, capabilities = added.Capabilities });
});

app.MapDelete("/api/models/{id}", (string id, HttpContext context, IProviderConfigStore store, IRuntimeStore runtimeStore) =>
{
    if (!store.Remove(id))
    {
        return Results.NotFound(new { error = $"No runtime-added provider '{id}' (built-in providers can't be removed here)." });
    }
    var caller = Caller(context);
    runtimeStore.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "model.remove", AuditOutcome.Success, $"Removed provider '{id}'."));
    return Results.Ok(new { removed = id });
});

app.MapPost("/api/models/defaults", (SetDefaultRequest request, IProviderConfigStore store, IModelRegistry registry) =>
{
    var capability = (request.Capability ?? "").Trim().ToLowerInvariant();
    var providerId = (request.ProviderId ?? "").Trim();
    if (capability.Length == 0 || providerId.Length == 0)
        return Results.BadRequest(new { error = "capability and providerId are required." });

    var provider = registry.Providers.FirstOrDefault(candidate => string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
    if (provider is null) return Results.NotFound(new { error = $"Unknown provider '{providerId}'." });
    if (!provider.Serves(capability)) return Results.BadRequest(new { error = $"Provider '{providerId}' does not serve '{capability}'." });

    store.SetOsDefault(capability, providerId);
    return Results.Ok(new { capability, providerId });
});

app.MapGet("/api/workspaces", (IRuntimeStore store) => store.Workspaces);
app.MapGet("/api/agents", (IRuntimeStore store) => store.Agents);
app.MapGet("/api/audit-events", (IRuntimeStore store) => store.AuditEvents);
app.MapGet("/api/spreadsheet", (IRuntimeStore store) => store.Spreadsheet);
app.MapGet("/api/inference/status", (ILocalInferenceRegistry registry) => registry.GetStatus());

app.MapGet("/api/approvals", async (IRuntimeStore store, IDryRunToolExecutor dryRun, CancellationToken cancellationToken) =>
{
    var views = new List<object>();
    foreach (var approval in store.Approvals)
    {
        var request = store.FindPendingRequest(approval.Id);
        EffectPreview? preview = null;
        if (approval.Status == ApprovalStatus.Pending && request is not null)
        {
            preview = await dryRun.PreviewAsync(request, cancellationToken);
        }

        views.Add(new
        {
            approval.Id,
            approval.ToolRequestId,
            approval.UserId,
            approval.Status,
            approval.Reason,
            approval.CreatedAt,
            approval.ResolvedAt,
            approval.RequestHash,
            PendingRequest = request is null ? null : new { request.ToolName, request.Operation, request.Arguments },
            Preview = preview
        });
    }

    return Results.Ok(views);
});

app.MapGet("/api/surfaces", (ISurfaceRegistry surfaces) =>
    surfaces.Surfaces.Select(surface => new { surface.Id, surface.DisplayName, surface.Kind }));

// A caller sees only the sessions it can reach — its own and its agents'.
// Without this filter the list leaks every user's session ids, owners, and
// ports to any authenticated principal.
app.MapGet("/api/sessions", async (HttpContext context, ISessionBackend sessions, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var visible = (await sessions.ListAsync(cancellationToken))
        .Where(session => Ownership.CanAccessHome(caller, session.Owner, store));
    return Results.Ok(visible);
});

// Observe a console session's screen — the agent's (and the watching human's)
// read side. Gated on the session owner, like the home browser: a caller may
// only see a session it or its owned agents run (design law 2: reads pass policy).
app.MapGet("/api/sessions/{id}/console", async (string id, HttpContext context, IConsoleBackend console, ISessionBackend sessions, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var target = (await sessions.ListAsync(cancellationToken)).FirstOrDefault(session => session.Id == id);
    if (target is null)
    {
        return Results.NotFound(new { error = $"Session '{id}' not found." });
    }
    if (!Ownership.CanAccessHome(caller, target.Owner, store))
    {
        return Results.Json(new { error = $"'{caller.Slug}' may not observe a session owned by '{target.Owner}'." }, statusCode: StatusCodes.Status403Forbidden);
    }
    return Results.Ok(await console.CaptureAsync(id, cancellationToken));
});

// Observe a DESKTOP session as a PNG screenshot — the gated read that pairs with
// the `desktop` input surface (same ownership rule as console observe).
app.MapGet("/api/sessions/{id}/screenshot", async (string id, HttpContext context, IDesktopBackend desktop, ISessionBackend sessions, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var target = (await sessions.ListAsync(cancellationToken)).FirstOrDefault(session => session.Id == id);
    if (target is null)
    {
        return Results.NotFound(new { error = $"Session '{id}' not found." });
    }
    if (!Ownership.CanAccessHome(caller, target.Owner, store))
    {
        return Results.Json(new { error = $"'{caller.Slug}' may not observe a session owned by '{target.Owner}'." }, statusCode: StatusCodes.Status403Forbidden);
    }
    var shot = await desktop.ScreenshotAsync(id, cancellationToken);
    if (!shot.Ok)
    {
        return Results.Json(new { error = shot.Error }, statusCode: StatusCodes.Status409Conflict);
    }
    context.Response.Headers["X-Screen-Width"] = shot.Width.ToString();
    context.Response.Headers["X-Screen-Height"] = shot.Height.ToString();
    return Results.File(shot.Png, "image/png");
});

// AT-SPI-first observation: the desktop's actionable elements with EXACT boxes,
// so the agent grounds on element ids, not guessed pixels (same ownership rule).
app.MapGet("/api/sessions/{id}/elements", async (string id, HttpContext context, IDesktopBackend desktop, ISessionBackend sessions, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var target = (await sessions.ListAsync(cancellationToken)).FirstOrDefault(session => session.Id == id);
    if (target is null)
    {
        return Results.NotFound(new { error = $"Session '{id}' not found." });
    }
    if (!Ownership.CanAccessHome(caller, target.Owner, store))
    {
        return Results.Json(new { error = $"'{caller.Slug}' may not observe a session owned by '{target.Owner}'." }, statusCode: StatusCodes.Status403Forbidden);
    }
    var elements = await desktop.ElementsAsync(id, cancellationToken);
    return elements.Ok
        ? Results.Ok(new { elements.SessionId, count = elements.Elements.Count, elements.Elements })
        : Results.Json(new { error = elements.Error }, statusCode: StatusCodes.Status409Conflict);
});

// The examples catalogue: what this machine can do, as something you press.
app.MapGet("/api/examples", (IExampleCatalog catalog, ExampleRunner runner, HttpContext context) =>
{
    var caller = Caller(context);
    return Results.Ok(new
    {
        examples = catalog.Examples.Select(example => new
        {
            example.Id,
            example.Title,
            example.Summary,
            example.NeedsSession,
            steps = example.Steps.Count
        }),
        current = runner.Current(caller.Slug)
    });
});

// Where a run has got to. Polled by the panel for the progress bar: the steps are
// scripted, so this is a real position rather than a spinner.
app.MapGet("/api/examples/run", (ExampleRunner runner, HttpContext context) =>
    Results.Ok(new { current = runner.Current(Caller(context).Slug) }));

app.MapPost("/api/examples/{id}/run", async (
    string id, ExampleRunRequest request, HttpContext context,
    IExampleCatalog catalog, ExampleRunner runner, AgentRuntime runtime,
    IRuntimeStore store, ISessionBackend sessions, IBrowserBackend browser,
    IRuntimeEventStream events, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    if (catalog.Find(id) is not { } example)
    {
        return Results.NotFound(new { error = $"No example '{id}'." });
    }

    string? sessionId = null;
    if (example.NeedsSession)
    {
        sessionId = request.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Results.BadRequest(new { error = "This example runs on a desktop session; choose one." });
        }
        var target = (await sessions.ListAsync(cancellationToken)).FirstOrDefault(session => session.Id == sessionId);
        if (target is null)
        {
            return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
        }
        // The bus enforces this too, on every step. Checking here as well means the
        // person gets one clear refusal instead of watching a demo fail at step 1.
        if (!Ownership.CanAccessHome(caller, target.Owner, store))
        {
            return Results.Json(new { error = $"'{caller.Slug}' may not run an example on a session owned by '{target.Owner}'." },
                statusCode: StatusCodes.Status403Forbidden);
        }
    }

    var (userId, agentId) = ActingAgent(caller, request.AgentId, store);
    var run = new ExampleRun(
        Guid.NewGuid().ToString("N"), example.Id, example.Title, sessionId,
        ExampleRunState.Running, 0, example.Steps.Count,
        "Starting…", Array.Empty<ExampleStepReport>());

    if (!runner.TryClaim(caller.Slug, run))
    {
        return Results.Conflict(new { error = "An example is already running. Let it finish, or answer the prompt it is waiting on." });
    }

    // Detached on purpose: a run drives a desktop for a minute or more, and the
    // panel follows it by polling. Holding the request open would tie the demo to
    // one browser tab surviving.
    _ = Task.Run(() => ExampleRunning.RunAsync(example, run, caller, userId, agentId, sessionId,
        runner, runtime, store, browser, events), CancellationToken.None);

    return Results.Ok(new { current = runner.Current(caller.Slug) });
});

// Is this session being recorded, and what has it captured so far? The gated read
// that pairs with the `recorder` surface — and the answer a person deserves before
// they take a seat at someone else's desktop.
app.MapGet("/api/sessions/{id}/recording", async (string id, HttpContext context, IRecorderBackend recorder, ISessionBackend sessions, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var target = (await sessions.ListAsync(cancellationToken)).FirstOrDefault(session => session.Id == id);
    if (target is null)
    {
        return Results.NotFound(new { error = $"Session '{id}' not found." });
    }
    if (!Ownership.CanAccessHome(caller, target.Owner, store))
    {
        return Results.Json(new { error = $"'{caller.Slug}' may not observe a session owned by '{target.Owner}'." }, statusCode: StatusCodes.Status403Forbidden);
    }
    var status = await recorder.RecordingStatusAsync(id, cancellationToken);
    return status.Ok
        ? Results.Ok(new { sessionId = id, status.Running, status.Current })
        : Results.Json(new { error = status.Error }, statusCode: StatusCodes.Status409Conflict);
});

// Observe the BROWSER open in a desktop session: where it is, and the actionable
// elements from the page's own accessibility tree. The gated read that pairs with
// the `browser` surface, same ownership rule as the screenshot.
app.MapGet("/api/sessions/{id}/browser", async (string id, HttpContext context, IBrowserBackend browser, ISessionBackend sessions, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var target = (await sessions.ListAsync(cancellationToken)).FirstOrDefault(session => session.Id == id);
    if (target is null)
    {
        return Results.NotFound(new { error = $"Session '{id}' not found." });
    }
    if (!Ownership.CanAccessHome(caller, target.Owner, store))
    {
        return Results.Json(new { error = $"'{caller.Slug}' may not observe a session owned by '{target.Owner}'." }, statusCode: StatusCodes.Status403Forbidden);
    }
    var elements = await browser.ElementsAsync(id, cancellationToken);
    if (!elements.Ok)
    {
        return Results.Json(new { error = elements.Error }, statusCode: StatusCodes.Status409Conflict);
    }
    // Both halves or neither. If the browser goes away between the two calls, a
    // 200 with an empty title and URL reads as "the page has no address", which is
    // a plausible-looking answer to a question we could not answer at all.
    var page = await browser.StatusAsync(id, cancellationToken);
    if (!page.Ok)
    {
        return Results.Json(new { error = page.Error }, statusCode: StatusCodes.Status409Conflict);
    }
    return Results.Ok(new { sessionId = id, page.Title, page.Url, count = elements.Elements.Count, elements.Elements });
});

// The page's visible text. Deliberately a separate call from the element list:
// this is the one output on the whole bus that is attacker-authored by design, so
// it arrives wrapped and labelled as untrusted data rather than folded silently
// into a perception payload a model might read as instructions.
app.MapGet("/api/sessions/{id}/browser/text", async (string id, HttpContext context, IBrowserBackend browser, ISessionBackend sessions, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var target = (await sessions.ListAsync(cancellationToken)).FirstOrDefault(session => session.Id == id);
    if (target is null)
    {
        return Results.NotFound(new { error = $"Session '{id}' not found." });
    }
    if (!Ownership.CanAccessHome(caller, target.Owner, store))
    {
        return Results.Json(new { error = $"'{caller.Slug}' may not observe a session owned by '{target.Owner}'." }, statusCode: StatusCodes.Status403Forbidden);
    }
    var read = await browser.ReadAsync(id, cancellationToken);
    return read.Ok
        ? Results.Ok(new { sessionId = id, read.Url, read.Text, untrusted = true, prompt = UntrustedPageText.Wrap(read.Url, read.Text) })
        : Results.Json(new { error = read.Error }, statusCode: StatusCodes.Status409Conflict);
});

// Drive a console session toward a goal: the loop observes the screen, asks the
// brain (model or recipe) for the next action, and submits each keystroke batch
// as a policy-checked, audited `console.type`. Gated on the session owner, like
// observe; the per-keystroke ownership/policy checks still apply inside the loop.
app.MapPost("/api/sessions/{id}/agent-run", async (string id, AgentRunRequest request, HttpContext context, ConsoleAgentLoop loop, IConsoleBrainRegistry brains, ISessionBackend sessions, IRuntimeStore store, IRuntimeEventStream events, IModelRegistry models, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var target = (await sessions.ListAsync(cancellationToken)).FirstOrDefault(session => session.Id == id);
    if (target is null)
    {
        return Results.NotFound(new { error = $"Session '{id}' not found." });
    }
    if (!Ownership.CanAccessHome(caller, target.Owner, store))
    {
        return Results.Json(new { error = $"'{caller.Slug}' may not drive a session owned by '{target.Owner}'." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var (userId, agentId) = ActingAgent(caller, request.AgentId, store);
    var selection = brains.Resolve(store.GetAgent(agentId));
    var result = await loop.RunAsync(id, request.Goal ?? "", request.MaxSteps ?? 6, caller, userId, agentId, selection.Brain, cancellationToken,
        model: BilledModel(selection.Provider, models));
    events.Publish(new RuntimeEvent("state-changed", store.SpreadsheetRevision, DateTimeOffset.UtcNow));
    return Results.Ok(result);
});

// Drive a DESKTOP session toward a goal: the loop observes the AT-SPI element list
// + a screenshot, asks the vision brain for the next action, and submits each
// click/keystroke as a policy-checked, audited `desktop.*` command. Gated on the
// session owner like the console loop; per-action ownership/policy still apply.
app.MapPost("/api/sessions/{id}/desktop-run", async (string id, DesktopRunRequest request, HttpContext context, DesktopAgentLoop loop, IDesktopBrainRegistry desktopBrains, ISessionVisionConsent visionConsent, ISessionBackend sessions, IRuntimeStore store, IRuntimeEventStream events, IModelRegistry models, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var target = (await sessions.ListAsync(cancellationToken)).FirstOrDefault(session => session.Id == id);
    if (target is null)
    {
        return Results.NotFound(new { error = $"Session '{id}' not found." });
    }
    if (!Ownership.CanAccessHome(caller, target.Owner, store))
    {
        return Results.Json(new { error = $"'{caller.Slug}' may not drive a session owned by '{target.Owner}'." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var (userId, agentId) = ActingAgent(caller, request.AgentId, store);
    var cloudVisionAllowed = visionConsent.IsAllowed(id, DateTimeOffset.UtcNow);
    var brain = desktopBrains.Resolve(store.GetAgent(agentId), cloudVisionAllowed);
    // A desktop run can call two providers: the text brain for grounding and,
    // with consent, a vision one. Each request is metered with its own provider
    // (the brains stamp it), but the run-level budget check needs one locality —
    // and it has to be the most expensive possibility, or an on-box grounding
    // brain would exempt a cloud vision run from every ceiling.
    var chatProvider = models.Resolve("chat", store.GetAgent(agentId));
    var visionProvider = cloudVisionAllowed ? models.Resolve("vision", store.GetAgent(agentId)) : null;
    var billedLocality = new[] { chatProvider?.Profile.Locality, visionProvider?.Profile.Locality }
        .Any(locality => locality is not null && !string.Equals(locality, TokenBudget.OnBoxLocality, StringComparison.OrdinalIgnoreCase))
            ? "cloud"
            : TokenBudget.OnBoxLocality;

    var result = await loop.RunAsync(id, request.Goal ?? "", request.MaxSteps ?? 8, caller, userId, agentId, brain, cancellationToken,
        model: chatProvider is null
            ? null
            : new ModelIdentity(chatProvider.Profile.Id, chatProvider.Profile.Model, billedLocality));
    events.Publish(new RuntimeEvent("state-changed", store.SpreadsheetRevision, DateTimeOffset.UtcNow));
    return Results.Ok(result);
});

app.MapGet("/api/whoami", (HttpContext context) =>
{
    var caller = Caller(context);
    var ownedHomes = caller.Kind == PrincipalKind.Human
        ? new[] { caller.Slug }.Concat(
            context.RequestServices.GetRequiredService<IRuntimeStore>().Agents
                .Where(agent => agent.OwnerUserId == caller.Subject)
                .Select(agent => agent.Slug))
        : new[] { caller.Slug };
    // The desk profile travels with whoami so the panel can say what kind of desk
    // this is without a second call, and an agent reports its owner's profile
    // because it works on that owner's desk.
    var runtimeStore = context.RequestServices.GetRequiredService<IRuntimeStore>();
    var rootSlug = Ownership.RootUserSlug(caller.Slug, runtimeStore) ?? caller.Slug;
    var deskProfile = DeskProfiles.Resolve(
        runtimeStore.Users.FirstOrDefault(candidate => candidate.Slug == rootSlug)?.DeskProfile);

    return Results.Ok(new
    {
        caller.Slug,
        caller.Display,
        kind = caller.Kind.ToString(),
        homes = ownedHomes,
        deskProfile = deskProfile.Id,
        deskProfileLabel = deskProfile.Label
    });
});

// A read-only view of a principal's persistent home volume — the direct answer
// to "I don't see where the agent's work lives." A caller may browse only its
// own home and the homes of agents it owns (design law 2: reads pass policy).
app.MapGet("/api/home/{owner}/list", async (string owner, string? path, HttpContext context, IHomeBrowser home, IRuntimeStore store, CancellationToken cancellationToken) =>
    !Ownership.CanAccessHome(Caller(context), owner, store)
        ? Results.Json(new { error = $"'{Caller(context).Slug}' may not browse '{owner}'." }, statusCode: StatusCodes.Status403Forbidden)
        : await home.ListAsync(owner, path ?? "", cancellationToken) is { } listing
            ? Results.Ok(listing)
            : Results.NotFound(new { error = $"No home volume exists yet for '{owner}'." }));

app.MapGet("/api/home/{owner}/read", async (string owner, string path, HttpContext context, IHomeBrowser home, IRuntimeStore store, CancellationToken cancellationToken) =>
    !Ownership.CanAccessHome(Caller(context), owner, store)
        ? Results.Json(new { error = $"'{Caller(context).Slug}' may not browse '{owner}'." }, statusCode: StatusCodes.Status403Forbidden)
        : await home.ReadAsync(owner, path, cancellationToken) is { } file
            ? Results.Ok(file)
            : Results.NotFound(new { error = "File not found or not readable." }));

// The way work leaves the machine. A preview decodes text and stops at 256 KiB, so
// until now the spreadsheet an agent produced could be seen but not taken. Same
// ownership check as the browse endpoints; a download is a read, and reads pass
// policy (design law 2). It is also audited: bytes crossing out of a desk is
// exactly the kind of act the trail exists to answer for.
app.MapGet("/api/home/{owner}/download", async (string owner, string path, HttpContext context, IHomeBrowser home, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    if (!Ownership.CanAccessHome(caller, owner, store))
    {
        return Results.Json(new { error = $"'{caller.Slug}' may not browse '{owner}'." }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (await home.DownloadAsync(owner, path, cancellationToken) is not { } file)
    {
        return Results.NotFound(new { error = "File not found or not readable." });
    }

    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "home.download", AuditOutcome.Success, $"Downloaded '{file.Path}' ({file.Size} bytes) from home of '{owner}'."));
    return Results.File(file.Content, file.ContentType, file.Name);
});

// The caller's shared workspace (lunos-shared-<user>): the collaboration space a
// user and their agents share at ~/shared. The owner is resolved FROM the caller,
// so a caller only ever reaches its own shared space — no cross-user access.
app.MapGet("/api/shared/list", async (string? path, HttpContext context, IHomeBrowser home, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var owner = Ownership.RootUserSlug(Caller(context).Slug, store);
    return await home.ListSharedAsync(owner, path ?? "", cancellationToken) is { } listing
        ? Results.Ok(listing)
        : Results.NotFound(new { error = "No shared workspace yet — open a session to provision it." });
});

app.MapGet("/api/shared/read", async (string path, HttpContext context, IHomeBrowser home, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var owner = Ownership.RootUserSlug(Caller(context).Slug, store);
    return await home.ReadSharedAsync(owner, path, cancellationToken) is { } file
        ? Results.Ok(file)
        : Results.NotFound(new { error = "File not found or not readable." });
});

app.MapGet("/api/shared/download", async (string path, HttpContext context, IHomeBrowser home, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var owner = Ownership.RootUserSlug(caller.Slug, store);
    if (await home.DownloadSharedAsync(owner, path, cancellationToken) is not { } file)
    {
        return Results.NotFound(new { error = "File not found or not readable." });
    }

    store.AppendAudit(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null,
        "shared.download", AuditOutcome.Success, $"Downloaded '{file.Path}' ({file.Size} bytes) from the shared workspace of '{owner}'."));
    return Results.File(file.Content, file.ContentType, file.Name);
});

app.MapGet("/api/surfaces/{surfaceId}/manifest", (string surfaceId, ISurfaceRegistry surfaces) =>
    surfaces.Find(surfaceId) is { } manifest ? Results.Ok(manifest) : Results.NotFound());

app.MapGet("/api/surfaces/{surfaceId}/state", (string surfaceId, HttpContext context, ISurfaceRegistry surfaces, IRuntimeStore store) =>
{
    if (surfaces.Find(surfaceId) is null)
    {
        return Results.NotFound();
    }

    var revision = store.SpreadsheetRevision;
    var etag = $"\"{revision}\"";
    if (context.Request.Headers.IfNoneMatch.ToString() == etag)
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    context.Response.Headers.ETag = etag;
    return Results.Ok(new { surface = surfaceId, revision, state = new { cells = store.Spreadsheet.Cells } });
});

app.MapGet("/api/surfaces/{surfaceId}/commands", (string surfaceId, HttpContext context, ISurfaceRegistry surfaces, IRuntimeStore store) =>
{
    if (surfaces.Find(surfaceId) is not { } manifest)
    {
        return Results.NotFound();
    }

    var principal = Caller(context);
    var commands = manifest.Commands
        .Where(pair => SurfaceConditions.IsValidNow(pair.Value.ValidWhen, store))
        .Where(pair => principal.Kind != PrincipalKind.Agent || pair.Value.ExposedToAgent)
        .Take(8)
        .Select(pair => new
        {
            Name = pair.Key,
            pair.Value.DisplayName,
            Decision = pair.Value.Policy.DefaultDecision,
            pair.Value.Policy.Reason,
            pair.Value.DryRun,
            pair.Value.Reversible,
            pair.Value.Input
        });

    return Results.Ok(new { surface = surfaceId, revision = store.SpreadsheetRevision, commands });
});

// Bounded idempotency cache: repeated submissions of the same key return the
// original result without re-executing. Cleared wholesale when it grows large.
var idempotencyCache = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

app.MapPost("/api/surfaces/{surfaceId}/commands/{commandName}", async (
    string surfaceId,
    string commandName,
    SurfaceCommandRequest request,
    HttpContext context,
    ISurfaceRegistry surfaces,
    IRuntimeStore store,
    AgentRuntime runtime,
    IDryRunToolExecutor dryRunExecutor,
    IRuntimeEventStream events,
    CancellationToken cancellationToken) =>
{
    if (surfaces.Find(surfaceId) is not { } manifest || !manifest.Commands.TryGetValue(commandName, out var command))
    {
        return Results.NotFound(new { error = $"Unknown command '{surfaceId}.{commandName}'." });
    }

    // Principal and input gates are enforced inside AgentRuntime.SubmitAsync —
    // the single choke point every entry path shares. This endpoint only
    // shapes the transport: dry runs, idempotency, and revision preconditions.
    var principal = Caller(context);
    var arguments = ToArguments(request.Input);
    var (userId, agentId) = ActingAgent(principal, request.AgentId, store);

    // A session over a home the caller cannot reach is denied before it starts.
    if (surfaceId == "session" && commandName == "create"
        && arguments.TryGetValue("owner", out var homeOwner)
        && !Ownership.CanAccessHome(principal, homeOwner, store))
    {
        return Results.Json(new { error = $"'{principal.Slug}' may not open a session over '{homeOwner}'." }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (request.DryRun == true)
    {
        if (SurfaceInputValidator.Validate(command.Input, arguments) is { } validationError)
        {
            return Results.BadRequest(new { error = validationError });
        }

        var toolRequest = new ToolRequest(Guid.NewGuid(), userId, agentId, surfaceId, commandName, arguments, DateTimeOffset.UtcNow);
        var preview = await dryRunExecutor.PreviewAsync(toolRequest, cancellationToken);
        return Results.Ok(new { dryRun = true, revision = store.SpreadsheetRevision, preview });
    }

    // Idempotency is scoped to principal + exact request content, and is
    // consulted before the revision precondition so a replayed request
    // returns its original result instead of a spurious conflict.
    var cacheKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
        ? null
        : $"{principal.Slug}:{request.IdempotencyKey}:{RequestHasher.Compute(surfaceId, commandName, arguments)}";
    if (cacheKey is not null && idempotencyCache.TryGetValue(cacheKey, out var cached))
    {
        return Results.Ok(cached);
    }

    ToolRequestResultDto result;
    try
    {
        result = await runtime.SubmitAsync(new SubmitToolRequestDto(userId, agentId, surfaceId, commandName, arguments), principal, request.ExpectedRevision, cancellationToken);
    }
    catch (RevisionMismatchException exception)
    {
        return Results.Json(
            new { error = exception.Message, currentRevision = exception.CurrentRevision },
            statusCode: StatusCodes.Status409Conflict);
    }

    var response = new
    {
        result.Decision,
        result.Reason,
        result.Execution,
        result.Approval,
        revision = store.SpreadsheetRevision
    };

    if (cacheKey is not null)
    {
        if (idempotencyCache.Count > 1024)
        {
            idempotencyCache.Clear();
        }
        idempotencyCache[cacheKey] = response;
    }

    events.Publish(new RuntimeEvent(
        result.Decision == PolicyDecision.RequireApproval ? "approval-pending" : "state-changed",
        store.SpreadsheetRevision,
        DateTimeOffset.UtcNow));
    return Results.Ok(response);
});

app.MapGet("/api/events", async (HttpContext context, IRuntimeEventStream events, CancellationToken cancellationToken) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    await context.Response.WriteAsync("data: {\"type\":\"connected\"}\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);

    await foreach (var runtimeEvent in events.SubscribeAsync(cancellationToken))
    {
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(runtimeEvent)}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
});

app.MapPost("/api/tool-requests", async (SubmitToolRequestDto request, HttpContext context, AgentRuntime runtime, IRuntimeStore store, IRuntimeEventStream events, CancellationToken cancellationToken) =>
{
    // The acting user/agent come from the authenticated identity, not the
    // request body — a caller cannot submit as someone else.
    var principal = Caller(context);
    var (userId, agentId) = ActingAgent(principal, request.AgentId, store);
    var bound = request with { UserId = userId, AgentId = agentId };
    var result = await runtime.SubmitAsync(bound, principal, cancellationToken);
    events.Publish(new RuntimeEvent(
        result.Decision == PolicyDecision.RequireApproval ? "approval-pending" : "state-changed",
        store.SpreadsheetRevision,
        DateTimeOffset.UtcNow));
    return result;
});

// OpenAI-compatible surface for a chat UI (Open WebUI) to talk to the ACTING
// agent: each message runs the console loop (the agent uses its tools + operates
// the OS), and the reply is the note it finishes on. Auth is the
// same bearer token — the chat UI is configured with the caller's token, so the
// loop runs as that owner through their agent.
var agentJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.MapGet("/v1/agent/models", () => Results.Ok(new
{
    @object = "list",
    data = new[] { new { id = "lunos-agent", @object = "model", created = 0, owned_by = "lunos" } }
}));

app.MapPost("/v1/agent/chat/completions", async (AgentChatRequest request, HttpContext context, ConsoleAgentLoop loop, IConsoleBrainRegistry brains, ISessionBackend sessions, IHomeBrowser home, IRuntimeStore store, IModelRegistry models, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var (userId, agentId) = ActingAgent(caller, null, store);
    var agent = store.GetAgent(agentId);
    var selection = brains.Resolve(agent);
    var brain = selection.Brain;
    var billed = BilledModel(selection.Provider, models);
    var messages = request.Messages ?? new List<AgentChatMessage>();
    var userMessage = messages.LastOrDefault(message => message.Role == "user")?.Content ?? "";

    // The chat client sends the whole thread; taking only the last message made the
    // agent forget everything you had just told it. Carry the recent turns into the
    // goal so a conversation is a conversation. Bounded (last few turns, each
    // truncated) because this rides in a prompt, not a context window we control.
    static string Clip(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? "" :
        text.Length <= max ? text.Trim() : text[..max].Trim() + "...";

    var priorTurns = messages
        .Take(Math.Max(0, messages.Count - 1))
        .Where(message => !string.IsNullOrWhiteSpace(message.Content)
            && (message.Role == "user" || message.Role == "assistant"))
        .TakeLast(6)
        .Select(message => $"{(message.Role == "user" ? "Owner" : "You")}: {Clip(message.Content, 600)}")
        .ToList();

    var session = (await sessions.ListAsync(cancellationToken))
        .FirstOrDefault(s => string.Equals(s.Owner, agent.Slug, StringComparison.Ordinal) && s.Kind == "console" && s.Status == "running");

    const string noSession =
        "I don't have a running console session to work in yet. Open one from the agent's desk (Sessions → agent-console) and message me again.";

    // The agent should know where it is. Without this it does not know it has a
    // Linux user, a home, a desktop of its own, or that ~/shared is the one place
    // its owner can see - so it invents paths and tells the owner about files they
    // cannot reach.
    var ownerSlug = Ownership.RootUserSlug(caller.Slug, store);
    var ownerName = store.Users.FirstOrDefault(u => u.Slug == ownerSlug)?.DisplayName ?? ownerSlug;
    var hasDesktop = (await sessions.ListAsync(cancellationToken))
        .Any(s => string.Equals(s.Owner, agent.Slug, StringComparison.Ordinal) && s.Kind == "desktop" && s.Status == "running");

    var whereYouAre =
        $"You are {agent.Name}, an agent running inside CieloOS. Your owner is {ownerName}. " +
        $"You act in your own console session as the Linux user 'root' in the home directory /root, " +
        "which persists across sessions. " +
        "~/shared is a workspace you SHARE with your owner: anything you put there they can see and " +
        "open from their panel, and anything they leave there you can read. Files you write anywhere " +
        "else are private to you and your owner cannot get at them, so put deliverables in ~/shared " +
        "and say so by name. " +
        (hasDesktop
            ? "You also have a graphical desktop session of your own, where the same shared folder is mounted. "
            : "") +
        "\n\n";


    var history = priorTurns.Count == 0
        ? ""
        : "Earlier in this conversation:\n" + string.Join("\n", priorTurns) + "\n\n";

    var goal =
        whereYouAre +
        history +
        $"Your owner sent you this chat message: \"{userMessage}\". Reply to them directly. " +
        "If you can answer from what you already know, just answer — do not touch the console. " +
        "If it needs work on the machine, use your tools (websearch, python3, the files in ~ and " +
        "~/shared), then give the answer. Put your full reply in the note when you finish.";
    // The reply travels as JSON on the finishing step, so newlines, quotes and code
    // blocks survive. outbox.md stays a fallback for agents that still write it — but
    // ONLY if this run actually changed it. The current prompt tells the agent not to
    // write the file, so a run that fails or hits the step limit would otherwise reply
    // with a leftover answer from an earlier conversation.
    async Task<string?> ReadOutboxAsync()
    {
        var owner = Ownership.RootUserSlug(caller.Slug, store);
        var file = await home.ReadSharedAsync(owner, "outbox.md", cancellationToken);
        return file?.Content;
    }

    var outboxBefore = await ReadOutboxAsync();

    async Task<string> ReplyOfAsync(ConsoleLoopResult run)
    {
        var finished = run.Steps.LastOrDefault(step => step.Done && !string.IsNullOrWhiteSpace(step.Note));
        if (finished?.Note is { } note && !string.IsNullOrWhiteSpace(note))
        {
            return note.Trim();
        }

        var outboxAfter = await ReadOutboxAsync();
        if (!string.IsNullOrWhiteSpace(outboxAfter) && !string.Equals(outboxAfter, outboxBefore, StringComparison.Ordinal))
        {
            return outboxAfter.Trim();
        }

        return run.Steps.LastOrDefault(step => step.Note is not null)?.Note ?? run.StopReason;
    }

    if (request.Stream == true)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        object Chunk(object delta, string? finish) => new
        {
            id = "lunos-agent",
            @object = "chat.completion.chunk",
            model = "lunos-agent",
            choices = new[] { new { index = 0, delta, finish_reason = finish } }
        };
        async Task SendAsync(object payload)
        {
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, agentJson)}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }

        await SendAsync(Chunk(new { role = "assistant", content = "" }, null));

        if (session is null)
        {
            await SendAsync(Chunk(new { content = noSession }, null));
        }
        else
        {
            // Emit each command as the agent runs it, so a long turn shows progress
            // instead of a silent wait, then the answer itself.
            var typed = 0;
            var run = await loop.RunAsync(session.Id, goal, 8, caller, userId, agentId, brain, cancellationToken,
                onStep: async step =>
                {
                    if (step.Done || string.IsNullOrWhiteSpace(step.Text)) return;
                    typed++;
                    await SendAsync(Chunk(new { content = $"› `{step.Text}`\n" }, null));
                },
                model: billed);

            var reply = await ReplyOfAsync(run);
            await SendAsync(Chunk(new { content = typed > 0 ? $"\n{reply}" : reply }, null));
        }

        await SendAsync(Chunk(new { }, "stop"));
        await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
        return Results.Empty;
    }

    string content;
    if (session is null)
    {
        content = noSession;
    }
    else
    {
        var run = await loop.RunAsync(session.Id, goal, 8, caller, userId, agentId, brain, cancellationToken, model: billed);
        var reply = await ReplyOfAsync(run);

        var actions = string.Join("\n", run.Steps
            .Where(step => !step.Done && !string.IsNullOrWhiteSpace(step.Text))
            .Select(step => $"› `{step.Text}`"));
        // The answer leads; what the agent typed is kept but folded away - it is
        // context, not the reply itself.
        content = string.IsNullOrEmpty(actions)
            ? reply
            : $"{reply}\n\n<details>\n<summary>What the agent did</summary>\n\n{actions}\n\n</details>";
    }

    return Results.Ok(new
    {
        id = "lunos-agent",
        @object = "chat.completion",
        model = "lunos-agent",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } }
    });
});

app.MapPost("/api/inference/chat", async (LocalChatRequest request, ILocalInferenceRouter router, CancellationToken cancellationToken) =>
    await router.ChatAsync(request, cancellationToken));

// OpenAI-compatible endpoint: clients send snake_case (max_tokens,
// response_format), which the web-default camelCase binding would drop.
app.MapPost("/v1/chat/completions", async (OpenAiCompatChatRequest request, ILocalInferenceRouter router, CancellationToken cancellationToken) =>
    await router.ChatAsync(
        new LocalChatRequest(request.Model, request.Messages, request.Temperature, request.MaxTokens, request.ResponseFormat),
        cancellationToken));

app.MapPost("/api/approvals/{approvalId:guid}/approve", (Guid approvalId, ResolveApprovalRequest request, HttpContext context, AgentRuntime runtime, IRuntimeStore store, IRuntimeEventStream events, CancellationToken cancellationToken) =>
    ResolveAsync(approvalId, approved: true, request, context, runtime, store, events, cancellationToken));

app.MapPost("/api/approvals/{approvalId:guid}/reject", (Guid approvalId, ResolveApprovalRequest request, HttpContext context, AgentRuntime runtime, IRuntimeStore store, IRuntimeEventStream events, CancellationToken cancellationToken) =>
    ResolveAsync(approvalId, approved: false, request, context, runtime, store, events, cancellationToken));

app.Run();

static async Task<IResult> ResolveAsync(
    Guid approvalId,
    bool approved,
    ResolveApprovalRequest request,
    HttpContext context,
    AgentRuntime runtime,
    IRuntimeStore store,
    IRuntimeEventStream events,
    CancellationToken cancellationToken)
{
    var principal = Caller(context);
    try
    {
        var result = await runtime.ResolveApprovalAsync(approvalId, approved, request.RequestHash ?? "", principal, request.ObservedRevision, cancellationToken);
        events.Publish(new RuntimeEvent("approval-resolved", store.SpreadsheetRevision, DateTimeOffset.UtcNow));
        return Results.Ok(result);
    }
    catch (ApprovalOwnershipException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (StaleApprovalException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict);
    }
}

static RuntimePrincipal Caller(HttpContext context) =>
    (RuntimePrincipal)context.Items["principal"]!;

// HttpOnly so a script in the panel's origin cannot read it — the flaw the token
// in localStorage had. Strict so it does not ride along on a cross-site request.
// Secure only under HTTPS, because the default deployment is plain HTTP on
// loopback and a Secure cookie would simply never be sent there.
static CookieOptions SessionCookieOptions(HttpContext context, DateTimeOffset expires) => new()
{
    HttpOnly = true,
    SameSite = SameSiteMode.Strict,
    Secure = context.Request.IsHttps,
    Path = "/",
    Expires = expires
};

// What a run is about to be billed for. Null when the provider is not one the
// registry knows (the recipe fallback, for instance), which is exactly when
// there is nothing to bill.
static ModelIdentity? BilledModel(string providerId, IModelRegistry models)
{
    var provider = models.Providers.FirstOrDefault(candidate =>
        string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
    return provider is null ? null : new ModelIdentity(provider.Id, provider.Model, provider.Locality);
}

// Creating a desk starts its image build if the machine does not have it yet.
//
// Detached from the request on purpose, and it never throws. The identity is
// already persisted and its token minted by the time this runs, so anything that
// could abort the response — a client that disconnects, a podman that hangs — must
// not be on this path: a claimed machine whose owner never received the token
// cannot be claimed again, which is a bricked first install.
static void StartDeskImageBuildIfMissing(string? deskProfileId, ISessionBackend sessions)
{
    var profile = DeskProfiles.Resolve(deskProfileId);
    if ((!profile.NeedsOwnImage && !profile.NeedsOwnConsoleImage) || sessions is not SessionOrchestrator orchestrator)
    {
        return;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            var desktopReady = !profile.NeedsOwnImage
                || await orchestrator.ImageExistsAsync(profile.Image, CancellationToken.None);
            var consoleReady = !profile.NeedsOwnConsoleImage
                || await orchestrator.ImageExistsAsync(profile.ConsoleImage, CancellationToken.None);
            if (desktopReady && consoleReady)
            {
                return;
            }

            var imagesRoot = Environment.GetEnvironmentVariable("Sessions__ProfileImagesPath")
                ?? "/var/lib/cielo/images/profiles";
            if (Directory.Exists(Path.Combine(imagesRoot, profile.Id)))
            {
                orchestrator.StartImageBuild(profile, imagesRoot, VsCodeDebForThisMachine());
            }
        }
        catch
        {
            // A desk that has to be built by hand is a nuisance; a user creation
            // that fails because of it is worse.
        }
    });
}

// VS Code ships a .deb per architecture, and the developer desk image is built on
// the target, so the build has to be told which one — the same problem the
// ONLYOFFICE package has in the session image, solved the same way.
static string VsCodeDebForThisMachine() =>
    RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? "VSCODE_DEB=https://update.code.visualstudio.com/latest/linux-deb-arm64/stable"
        : "VSCODE_DEB=https://update.code.visualstudio.com/latest/linux-deb-x64/stable";

// Loopback = the request originates on the box itself (a local browser, the SSH
// tunnel the panel uses, or the CLI). IPv4-mapped IPv6 (::ffff:127.0.0.1) is
// unwrapped first so a mapped loopback still counts.
static bool IsLoopback(IPAddress? address)
{
    if (address is null)
    {
        return false;
    }

    var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    return IPAddress.IsLoopback(normalized);
}

// The user/agent a request acts as, derived from the authenticated identity:
// an agent acts as itself; a human acts through an agent it owns (the one it
// named, if valid, else its first).

// A chat URL the PANEL uses is written from the host's point of view, where
// loopback means the host. Inside a rootless-podman session, loopback is the
// container itself, so any loopback host — localhost, 127.0.0.0/8, or [::1] —
// has to become podman's host alias. Uri.IsLoopback covers all of those forms;
// string matching on "//localhost" and "//127.0.0.1" did not.
static string SessionReachableChatUrl(string? chatUrl)
{
    if (string.IsNullOrWhiteSpace(chatUrl)) return "";
    if (!Uri.TryCreate(chatUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback) return chatUrl;
    return new UriBuilder(uri) { Host = "host.containers.internal" }.Uri.ToString();
}
static (Guid userId, Guid agentId) ActingAgent(RuntimePrincipal principal, Guid? requestedAgentId, IRuntimeStore store)
{
    if (principal.Kind == PrincipalKind.Agent)
    {
        var self = store.GetAgent(principal.Subject);
        return (self.OwnerUserId, self.Id);
    }

    var owned = store.Agents.Where(agent => agent.OwnerUserId == principal.Subject).ToList();
    var chosen = requestedAgentId is { } requested && owned.Any(agent => agent.Id == requested)
        ? requested
        : owned.Count > 0 ? owned[0].Id : Guid.Empty;
    return (principal.Subject, chosen);
}

static Dictionary<string, string> ToArguments(Dictionary<string, JsonElement>? input)
{
    var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
    if (input is null)
    {
        return arguments;
    }

    foreach (var pair in input)
    {
        // JSON null/undefined values are treated as absent, not as the
        // literal string "null" — the required-input validation catches them.
        if (pair.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            continue;
        }

        arguments[pair.Key] = pair.Value.ValueKind == JsonValueKind.String
            ? pair.Value.GetString() ?? ""
            : pair.Value.GetRawText();
    }

    return arguments;
}

public sealed record SurfaceCommandRequest(
    Dictionary<string, JsonElement>? Input,
    bool? DryRun,
    string? IdempotencyKey,
    long? ExpectedRevision,
    Guid? UserId,
    Guid? AgentId);

public sealed record ResolveApprovalRequest(string? RequestHash, long? ObservedRevision);

public sealed record SetTokenLimitRequest(string? Scope, string? Subject, long? MonthlyTokens);

public sealed record LoginRequest(string? Slug, string? Password);

public sealed record SetPasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record CreateApiKeyRequest(string? Name, int? ExpiresInDays);

public sealed record ClaimRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("deskProfile")] string? DeskProfile = null);

public sealed record AddUserRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("deskProfile")] string? DeskProfile = null);

public sealed record AddProviderRequest(
    string? DisplayName,
    string? Kind,
    string? BaseUrl,
    string? Model,
    List<string>? Capabilities,
    string? Locality,
    string? ApiKey,
    List<string>? DefaultFor);

public sealed record SetDefaultRequest(string? Capability, string? ProviderId);

public static class ExampleRunning
{
// Walk an example's steps through the ordinary bus, pausing where a human has to
// decide. The pause IS the demo: this machine stops and asks, and watching that
// happen once explains more than a paragraph about it.
public static async Task RunAsync(
    Example example, ExampleRun run, RuntimePrincipal caller, Guid userId, Guid agentId,
    string? sessionId, ExampleRunner runner, AgentRuntime runtime, IRuntimeStore store,
    IBrowserBackend browser, IRuntimeEventStream events)
{
    var owner = caller.Slug;
    var reports = new List<ExampleStepReport>();

    void Publish() => events.Publish(new RuntimeEvent("state-changed", store.SpreadsheetRevision, DateTimeOffset.UtcNow));

    for (var index = 0; index < example.Steps.Count; index++)
    {
        var step = example.Steps[index];
        var number = index + 1;
        var input = ExampleSubstitution.Bind(step.Input, sessionId);

        runner.Update(owner, current => current with
        {
            State = ExampleRunState.Running,
            Step = number,
            Message = step.Note,
            Reports = reports.ToArray(),
        });
        Publish();

        try
        {
            // An "observe" step is a gated READ, never a mutation — it exists so a
            // demo can show what the agent just perceived.
            if (string.Equals(step.Kind, "observe", StringComparison.Ordinal))
            {
                var detail = step.Operation switch
                {
                    "read" => await browser.ReadAsync(sessionId ?? "", CancellationToken.None) is { Ok: true } text
                        ? $"{text.Url} — \"{Shorten(text.Text, 160)}\""
                        : "The page could not be read.",
                    "elements" => await browser.ElementsAsync(sessionId ?? "", CancellationToken.None) is { Ok: true } list
                        ? $"{list.Elements.Count} actionable element(s): "
                          + string.Join(", ", list.Elements.Take(4).Select(element => $"{element.Role} '{element.Name}'"))
                        : "The page could not be observed.",
                    _ => $"Unknown observation '{step.Operation}'."
                };
                reports.Add(new ExampleStepReport(number, step.Note, "observed", detail));
                continue;
            }

            var result = await runtime.SubmitAsync(
                new SubmitToolRequestDto(userId, agentId, step.Surface, step.Operation,
                    new Dictionary<string, string>(input, StringComparer.Ordinal)),
                caller, CancellationToken.None);

            if (result.Decision == PolicyDecision.RequireApproval && result.Approval is { } approval)
            {
                runner.Update(owner, current => current with
                {
                    State = ExampleRunState.AwaitingApproval,
                    Message = step.Note,
                    ApprovalId = approval.Id,
                    ApprovalReason = approval.Reason,
                    ApprovalHash = approval.RequestHash,
                    Reports = reports.ToArray(),
                });
                Publish();

                // Approving RUNS the command, so there is nothing to resubmit —
                // wait for the person, then read what their answer was.
                var resolved = await WaitForApprovalAsync(store, approval.Id);
                if (resolved != ApprovalStatus.Approved)
                {
                    reports.Add(new ExampleStepReport(number, step.Note, "declined",
                        "You said no, so the example stopped here. That is the feature working."));
                    runner.Update(owner, current => current with
                    {
                        State = ExampleRunState.Finished,
                        Message = "Stopped, because you declined a step.",
                        ApprovalId = null, ApprovalReason = null, ApprovalHash = null,
                        Reports = reports.ToArray(),
                    });
                    Publish();
                    return;
                }

                reports.Add(new ExampleStepReport(number, step.Note, "approved", "You approved it, and it ran."));
                runner.Update(owner, current => current with
                {
                    State = ExampleRunState.Running,
                    ApprovalId = null, ApprovalReason = null, ApprovalHash = null,
                });
                continue;
            }

            var executed = result.Execution;
            var outcome = result.Decision == PolicyDecision.Deny ? "refused"
                : executed is { Executed: true } ? "done" : "failed";
            reports.Add(new ExampleStepReport(number, step.Note, outcome,
                executed?.Message ?? result.Reason));

            // A refusal is sometimes the point (example 03 shows one), but a step
            // that simply failed should stop the run rather than cascade.
            if (outcome == "failed")
            {
                runner.Update(owner, current => current with
                {
                    State = ExampleRunState.Failed,
                    Message = $"Step {number} did not work: {Shorten(executed?.Message ?? result.Reason, 200)}",
                    Reports = reports.ToArray(),
                });
                Publish();
                return;
            }
        }
        catch (Exception error)
        {
            reports.Add(new ExampleStepReport(number, step.Note, "failed", error.Message));
            runner.Update(owner, current => current with
            {
                State = ExampleRunState.Failed,
                Message = $"Step {number} threw: {Shorten(error.Message, 200)}",
                Reports = reports.ToArray(),
            });
            Publish();
            return;
        }
    }

    runner.Update(owner, current => current with
    {
        State = ExampleRunState.Finished,
        Step = example.Steps.Count,
        Message = "Finished.",
        Reports = reports.ToArray(),
    });
    Publish();
}

static async Task<ApprovalStatus> WaitForApprovalAsync(IRuntimeStore store, Guid approvalId)
{
    // Ten minutes is long enough to answer a prompt and short enough that a demo
    // nobody answered does not hold the runner forever.
    var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
    while (DateTimeOffset.UtcNow < deadline)
    {
        var approval = store.Approvals.FirstOrDefault(record => record.Id == approvalId);
        if (approval is not null && approval.Status != ApprovalStatus.Pending)
        {
            return approval.Status;
        }
        await Task.Delay(500);
    }
    return ApprovalStatus.Rejected;
}

static string Shorten(string value, int limit) =>
    string.IsNullOrEmpty(value) ? "" : value.Length <= limit ? value : value[..limit] + "…";
}

public sealed record ExampleRunRequest(string? SessionId, Guid? AgentId);

public sealed record AgentRunRequest(string? Goal, int? MaxSteps, Guid? AgentId);
public sealed record DesktopRunRequest(string? Goal, int? MaxSteps, Guid? AgentId);

public sealed record AgentChatMessage(
    [property: System.Text.Json.Serialization.JsonPropertyName("role")] string? Role,
    [property: System.Text.Json.Serialization.JsonPropertyName("content")] string? Content);

public sealed record AgentChatRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("model")] string? Model,
    [property: System.Text.Json.Serialization.JsonPropertyName("messages")] List<AgentChatMessage>? Messages,
    [property: System.Text.Json.Serialization.JsonPropertyName("stream")] bool? Stream);

public sealed record OpenAiCompatChatRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("model")] string? Model,
    [property: System.Text.Json.Serialization.JsonPropertyName("messages")] List<ChatMessageDto> Messages,
    [property: System.Text.Json.Serialization.JsonPropertyName("temperature")] double? Temperature,
    [property: System.Text.Json.Serialization.JsonPropertyName("max_tokens")] int? MaxTokens,
    [property: System.Text.Json.Serialization.JsonPropertyName("response_format")] JsonElement? ResponseFormat);
