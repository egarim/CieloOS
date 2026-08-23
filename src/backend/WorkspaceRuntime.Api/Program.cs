using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

var databaseProvider = (builder.Configuration["Database:Provider"] ?? "sqlite").ToLowerInvariant();
switch (databaseProvider)
{
    case "memory":
        builder.Services.AddSingleton<IRuntimeStore, InMemoryRuntimeStore>();
        break;

    case "postgres":
        var postgresConnection = builder.Configuration["Database:PostgresConnection"]
            ?? throw new InvalidOperationException("Database:PostgresConnection is required when Database:Provider is postgres.");
        builder.Services.AddDbContextFactory<RuntimeDbContext>(options => options.UseNpgsql(postgresConnection));
        builder.Services.AddSingleton<IRuntimeStore, EfRuntimeStore>();
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
        builder.Services.AddSingleton<IRuntimeStore, EfRuntimeStore>();
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
builder.Services.AddSingleton<ISurfaceRegistry>(_ => new FileSurfaceRegistry(repositoryRoot));
builder.Services.AddSingleton<IRuntimeEventStream, ChannelRuntimeEventStream>();
builder.Services.AddSingleton<IPolicyEngine, ManifestPolicyEngine>();
builder.Services.AddSingleton<SpreadsheetSandboxExecutor>();
builder.Services.AddSingleton<SessionOrchestrator>(sp => new SessionOrchestrator(
    new SessionBackendOptions
    {
        PodmanPath = builder.Configuration["Sessions:PodmanPath"] ?? "podman",
        Image = builder.Configuration["Sessions:Image"] ?? "docker.io/accetto/ubuntu-vnc-xfce-g3:latest",
        ViewportPort = int.TryParse(builder.Configuration["Sessions:ViewportPort"], out var vp) ? vp : 6901
    },
    owner => Ownership.RootUserSlug(owner, sp.GetRequiredService<IRuntimeStore>())));
builder.Services.AddSingleton<ISessionBackend>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<IConsoleBackend>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<IDesktopBackend>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<IHomeBrowser>(provider => new PodmanHomeBrowser(new SessionBackendOptions
{
    PodmanPath = builder.Configuration["Sessions:PodmanPath"] ?? "podman"
}));
builder.Services.AddSingleton<ISurfaceExecutor>(provider => provider.GetRequiredService<SpreadsheetSandboxExecutor>());
builder.Services.AddSingleton<ISurfaceExecutor>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<ISurfaceExecutor>(provider => new ConsoleSurfaceExecutor(provider.GetRequiredService<IConsoleBackend>()));
builder.Services.AddSingleton<ISurfaceExecutor>(provider => new DesktopSurfaceExecutor(provider.GetRequiredService<IDesktopBackend>()));
builder.Services.AddSingleton<SurfaceExecutorRouter>(provider => new SurfaceExecutorRouter(provider.GetServices<ISurfaceExecutor>()));
builder.Services.AddSingleton<ISandboxedToolExecutor>(provider => provider.GetRequiredService<SurfaceExecutorRouter>());
builder.Services.AddSingleton<IDryRunToolExecutor>(provider => provider.GetRequiredService<SurfaceExecutorRouter>());
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddSingleton<ConsoleAgentLoop>();

// The console loop's brain(s). Each configured model provider (OpenAI-compatible)
// becomes a named brain; an agent's InferenceProvider selects which one drives it,
// so DeepSeek and Azure OpenAI (gpt-4.1-mini) can run side by side on one runtime.
// With no key configured, a deterministic recipe brain stands in so the loop still
// works end-to-end. Keys are read from config/env and never logged.
var brainProviders = new Dictionary<string, ModelBrainOptions>(StringComparer.OrdinalIgnoreCase);

var deepseekKey = builder.Configuration["Inference:Deepseek:ApiKey"]
    ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
if (!string.IsNullOrWhiteSpace(deepseekKey))
{
    brainProviders["deepseek"] = new ModelBrainOptions
    {
        BaseUrl = builder.Configuration["Inference:Deepseek:BaseUrl"] ?? "https://api.deepseek.com",
        Model = builder.Configuration["Inference:Deepseek:Model"] ?? "deepseek-chat",
        ApiKey = deepseekKey
    };
}

var azureKey = builder.Configuration["Inference:Azure:ApiKey"]
    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
if (!string.IsNullOrWhiteSpace(azureKey))
{
    // Azure OpenAI's OpenAI-compatible /openai/v1 endpoint speaks the same chat API
    // (Bearer auth, model = deployment name in the body), so the same
    // ModelConsoleBrain drives it with no code change — only config differs.
    var azureProvider = builder.Configuration["Inference:Azure:Provider"] ?? "gpt-4.1-mini";
    brainProviders[azureProvider] = new ModelBrainOptions
    {
        BaseUrl = builder.Configuration["Inference:Azure:BaseUrl"]
            ?? "https://sivar-aoai-eus.openai.azure.com/openai/v1",
        Model = builder.Configuration["Inference:Azure:Model"] ?? "gpt-4.1-mini",
        ApiKey = azureKey
    };
}

var defaultBrainProvider = brainProviders.ContainsKey("deepseek")
    ? "deepseek"
    : brainProviders.Keys.FirstOrDefault() ?? "";

builder.Services.AddSingleton<IConsoleBrainRegistry>(sp =>
{
    var brains = new Dictionary<string, IConsoleAgentBrain>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, options) in brainProviders)
    {
        brains[name] = new ModelConsoleBrain(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, options);
    }
    return new ConsoleBrainRegistry(
        brains,
        new RecipeConsoleBrain(),
        defaultBrainProvider,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConsoleBrainRegistry>());
});
// A default single brain for any injection site that does not select per-agent.
builder.Services.AddSingleton<IConsoleAgentBrain>(sp =>
    sp.GetRequiredService<IConsoleBrainRegistry>().Resolve(null).Brain);

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

var brainRegistry = app.Services.GetRequiredService<IConsoleBrainRegistry>();
app.Logger.LogInformation(
    "Console brains registered: [{Providers}]; default '{Default}'.",
    string.Join(", ", brainRegistry.ProviderNames), defaultBrainProvider);

app.UseCors();

// Principal resolution: every route not explicitly public requires a bearer
// token; approval verbs additionally require the human principal.
app.Use(async (context, next) =>
{
    var level = AccessPolicy.Required(context.Request.Path, context.Request.Method);
    if (level != AccessLevel.Public)
    {
        var authenticator = context.RequestServices.GetRequiredService<ITokenAuthenticator>();
        var header = context.Request.Headers.Authorization.ToString();
        var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authenticator.Authenticate(header["Bearer ".Length..])
            : null;

        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A valid bearer token is required." });
            return;
        }

        if (level == AccessLevel.HumanOnly && principal.Kind != PrincipalKind.Human)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This operation requires a human principal." });
            return;
        }

        context.Items["principal"] = principal;
    }

    await next();
});

app.MapGet("/", () => Results.Redirect("/api/branding"));

app.MapGet("/api/branding", (IConfiguration configuration) =>
{
    var section = configuration.GetSection("Branding");
    return Results.Ok(new
    {
        productName = section["ProductName"] ?? "Workspace Runtime",
        shortName = section["ShortName"] ?? "Runtime",
        companyName = section["CompanyName"] ?? "Workspace Runtime Labs",
        supportName = section["SupportName"] ?? "Support",
        agentName = section["AgentName"] ?? "Assistant"
    });
});

app.MapGet("/api/users", (IRuntimeStore store) => store.Users);
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

// Drive a console session toward a goal: the loop observes the screen, asks the
// brain (model or recipe) for the next action, and submits each keystroke batch
// as a policy-checked, audited `console.type`. Gated on the session owner, like
// observe; the per-keystroke ownership/policy checks still apply inside the loop.
app.MapPost("/api/sessions/{id}/agent-run", async (string id, AgentRunRequest request, HttpContext context, ConsoleAgentLoop loop, IConsoleBrainRegistry brains, ISessionBackend sessions, IRuntimeStore store, IRuntimeEventStream events, CancellationToken cancellationToken) =>
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
    var brain = brains.Resolve(store.GetAgent(agentId).InferenceProvider).Brain;
    var result = await loop.RunAsync(id, request.Goal ?? "", request.MaxSteps ?? 6, caller, userId, agentId, brain, cancellationToken);
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
    return Results.Ok(new { caller.Slug, caller.Display, kind = caller.Kind.ToString(), homes = ownedHomes });
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
// the OS), and the reply is what it writes to ~/shared/outbox.md. Auth is the
// same bearer token — the chat UI is configured with the caller's token, so the
// loop runs as that owner through their agent.
var agentJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.MapGet("/v1/agent/models", () => Results.Ok(new
{
    @object = "list",
    data = new[] { new { id = "lunos-agent", @object = "model", created = 0, owned_by = "lunos" } }
}));

app.MapPost("/v1/agent/chat/completions", async (AgentChatRequest request, HttpContext context, ConsoleAgentLoop loop, IConsoleBrainRegistry brains, ISessionBackend sessions, IHomeBrowser home, IRuntimeStore store, CancellationToken cancellationToken) =>
{
    var caller = Caller(context);
    var (userId, agentId) = ActingAgent(caller, null, store);
    var agent = store.GetAgent(agentId);
    var brain = brains.Resolve(agent.InferenceProvider).Brain;
    var userMessage = request.Messages?.LastOrDefault(message => message.Role == "user")?.Content ?? "";

    var session = (await sessions.ListAsync(cancellationToken))
        .FirstOrDefault(s => string.Equals(s.Owner, agent.Slug, StringComparison.Ordinal) && s.Kind == "console" && s.Status == "running");

    string content;
    if (session is null)
    {
        content = "I don't have a running console session to work in yet. Open one from the agent's desk (Sessions → agent-console) and message me again.";
    }
    else
    {
        var goal =
            $"Your owner sent you this chat message: \"{userMessage}\". Respond helpfully — if it asks you to do " +
            "something, use your tools (websearch, python3, the files in ~ and ~/shared). When finished, write your " +
            "reply by overwriting ~/shared/outbox.md, then you are done.";
        var run = await loop.RunAsync(session.Id, goal, 8, caller, userId, agentId, brain, cancellationToken);

        var owner = Ownership.RootUserSlug(caller.Slug, store);
        var outbox = await home.ReadSharedAsync(owner, "outbox.md", cancellationToken);
        var reply = outbox is not null && !string.IsNullOrWhiteSpace(outbox.Content)
            ? outbox.Content.Trim()
            : run.Steps.LastOrDefault(step => step.Note is not null)?.Note ?? run.StopReason;

        var actions = string.Join("\n", run.Steps
            .Where(step => !step.Done && !string.IsNullOrWhiteSpace(step.Text))
            .Select(step => $"› `{step.Text}`"));
        content = string.IsNullOrEmpty(actions) ? reply : $"{actions}\n\n{reply}";
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
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(Chunk(new { role = "assistant", content }, null), agentJson)}\n\n", cancellationToken);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(Chunk(new { }, "stop"), agentJson)}\n\n", cancellationToken);
        await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
        return Results.Empty;
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

// The user/agent a request acts as, derived from the authenticated identity:
// an agent acts as itself; a human acts through an agent it owns (the one it
// named, if valid, else its first).
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

public sealed record AgentRunRequest(string? Goal, int? MaxSteps, Guid? AgentId);

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
