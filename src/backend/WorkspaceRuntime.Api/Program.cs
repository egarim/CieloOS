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

builder.Services.AddSingleton<ITokenAuthenticator>(_ => new FileTokenStore(secretsPath));
builder.Services.AddSingleton<ISurfaceRegistry>(_ => new FileSurfaceRegistry(repositoryRoot));
builder.Services.AddSingleton<IRuntimeEventStream, ChannelRuntimeEventStream>();
builder.Services.AddSingleton<IPolicyEngine, ManifestPolicyEngine>();
builder.Services.AddSingleton<SpreadsheetSandboxExecutor>();
builder.Services.AddSingleton<SessionOrchestrator>(_ => new SessionOrchestrator(new SessionBackendOptions
{
    PodmanPath = builder.Configuration["Sessions:PodmanPath"] ?? "podman",
    Image = builder.Configuration["Sessions:Image"] ?? "docker.io/accetto/ubuntu-vnc-xfce-g3:latest",
    ViewportPort = int.TryParse(builder.Configuration["Sessions:ViewportPort"], out var vp) ? vp : 6901
}));
builder.Services.AddSingleton<ISessionBackend>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<IHomeBrowser>(provider => new PodmanHomeBrowser(new SessionBackendOptions
{
    PodmanPath = builder.Configuration["Sessions:PodmanPath"] ?? "podman"
}));
builder.Services.AddSingleton<ISurfaceExecutor>(provider => provider.GetRequiredService<SpreadsheetSandboxExecutor>());
builder.Services.AddSingleton<ISurfaceExecutor>(provider => provider.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<SurfaceExecutorRouter>(provider => new SurfaceExecutorRouter(provider.GetServices<ISurfaceExecutor>()));
builder.Services.AddSingleton<ISandboxedToolExecutor>(provider => provider.GetRequiredService<SurfaceExecutorRouter>());
builder.Services.AddSingleton<IDryRunToolExecutor>(provider => provider.GetRequiredService<SurfaceExecutorRouter>());
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddHttpClient<ILocalInferenceRouter, LocalInferenceRouter>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.AddSingleton<ILocalInferenceRegistry>(_ =>
{
    return new FileLocalInferenceRegistry(repositoryRoot);
});

var app = builder.Build();

// Eager bootstrap: mint tokens, load surface manifests (fail fast on a
// malformed contract), and run database migrations at startup rather than on
// the first request — a fresh install must have its token files before anyone
// can present one.
_ = app.Services.GetRequiredService<ITokenAuthenticator>();
_ = app.Services.GetRequiredService<ISurfaceRegistry>();
_ = app.Services.GetRequiredService<IRuntimeStore>().Users;

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

        if (level == AccessLevel.HumanOnly && principal != RuntimePrincipals.Human)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This operation requires the human principal." });
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

app.MapGet("/api/sessions", async (ISessionBackend sessions, CancellationToken cancellationToken) =>
    await sessions.ListAsync(cancellationToken));

// A read-only view of a principal's persistent home volume — the direct answer
// to "I don't see where the agent's work lives." Reads are policed by auth;
// finer ownership checks land with multi-user identity.
app.MapGet("/api/home/{owner}/list", async (string owner, string? path, IHomeBrowser home, CancellationToken cancellationToken) =>
    await home.ListAsync(owner, path ?? "", cancellationToken) is { } listing
        ? Results.Ok(listing)
        : Results.NotFound(new { error = $"No home volume exists yet for '{owner}'." }));

app.MapGet("/api/home/{owner}/read", async (string owner, string path, IHomeBrowser home, CancellationToken cancellationToken) =>
    await home.ReadAsync(owner, path, cancellationToken) is { } file
        ? Results.Ok(file)
        : Results.NotFound(new { error = "File not found or not readable." }));

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

    var principal = context.Items["principal"] as string;
    var commands = manifest.Commands
        .Where(pair => SurfaceConditions.IsValidNow(pair.Value.ValidWhen, store))
        .Where(pair => principal != RuntimePrincipals.Agent || pair.Value.ExposedToAgent)
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
    var principal = context.Items["principal"] as string ?? RuntimePrincipals.Human;
    var arguments = ToArguments(request.Input);
    var userId = request.UserId ?? store.Users[0].Id;
    var agentId = request.AgentId ?? store.Agents[0].Id;

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
        : $"{principal}:{request.IdempotencyKey}:{RequestHasher.Compute(surfaceId, commandName, arguments)}";
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
    var principal = context.Items["principal"] as string ?? RuntimePrincipals.Human;
    var result = await runtime.SubmitAsync(request, principal, cancellationToken);
    events.Publish(new RuntimeEvent(
        result.Decision == PolicyDecision.RequireApproval ? "approval-pending" : "state-changed",
        store.SpreadsheetRevision,
        DateTimeOffset.UtcNow));
    return result;
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
    var principal = context.Items["principal"] as string ?? RuntimePrincipals.Human;
    try
    {
        var result = await runtime.ResolveApprovalAsync(approvalId, approved, request.RequestHash ?? "", principal, request.ObservedRevision, cancellationToken);
        events.Publish(new RuntimeEvent("approval-resolved", store.SpreadsheetRevision, DateTimeOffset.UtcNow));
        return Results.Ok(result);
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

public sealed record OpenAiCompatChatRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("model")] string? Model,
    [property: System.Text.Json.Serialization.JsonPropertyName("messages")] List<ChatMessageDto> Messages,
    [property: System.Text.Json.Serialization.JsonPropertyName("temperature")] double? Temperature,
    [property: System.Text.Json.Serialization.JsonPropertyName("max_tokens")] int? MaxTokens,
    [property: System.Text.Json.Serialization.JsonPropertyName("response_format")] JsonElement? ResponseFormat);
