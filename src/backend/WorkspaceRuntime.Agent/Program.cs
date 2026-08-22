using System.Net.Http.Json;
using System.Text.Json;

const string DefaultApiUrl = "http://127.0.0.1:5148";
var apiUrl = Environment.GetEnvironmentVariable("WORKSPACE_RUNTIME_API")?.TrimEnd('/') ?? DefaultApiUrl;
using var httpClient = new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromMinutes(3) };
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

// The agent principal token: explicit env var, then the installed VM path,
// then the local development path relative to the working directory.
var token = Environment.GetEnvironmentVariable("WORKSPACE_RUNTIME_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    var tokenFile = Environment.GetEnvironmentVariable("WORKSPACE_RUNTIME_TOKEN_FILE");
    var candidates = new[]
    {
        tokenFile,
        "/var/lib/workspace-runtime/secrets/agent.token",
        Path.Combine(Environment.CurrentDirectory, ".data", "secrets", "agent.token")
    };
    token = candidates
        .Where(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
        .Select(candidate => File.ReadAllText(candidate!).Trim())
        .FirstOrDefault();
}

if (!string.IsNullOrWhiteSpace(token))
{
    httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
}

try
{
    if (args.Length > 0)
    {
        return args[0] switch
        {
            "status" => await ShowStatusAsync(args.Skip(1).Contains("--json")),
            "ask" => await AskOnceAsync(string.Join(' ', args.Skip(1))),
            "observe" => await ObserveAsync(args.Length > 1 ? args[1] : "spreadsheet"),
            "do" => await DoAsync(args.Skip(1).ToArray()),
            "help" or "--help" or "-h" => ShowHelp(),
            _ => Fail($"Unknown command '{args[0]}'. Use: workspace-agent help", 2)
        };
    }

    return await RunInteractiveAsync();
}
catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
{
    return Fail(
        "The runtime rejected the agent token. Set WORKSPACE_RUNTIME_TOKEN, or point WORKSPACE_RUNTIME_TOKEN_FILE at a readable agent.token " +
        "(installed systems keep it at /var/lib/workspace-runtime/secrets/agent.token).", 5);
}
catch (HttpRequestException exception)
{
    return Fail($"Workspace runtime is unavailable at {apiUrl}: {exception.Message}", 3);
}
catch (TaskCanceledException)
{
    return Fail("The local model did not respond before the timeout.", 4);
}

async Task<int> RunInteractiveAsync()
{
    var branding = await ReadAsync<Branding>("/api/branding");
    var messages = NewConversation(branding);

    Console.WriteLine($"{branding.ProductName} Assistant");
    Console.WriteLine("Local-first agent session. Type /help for commands.\n");

    while (true)
    {
        Console.Write("> ");
        var input = Console.ReadLine();
        if (input is null || input.Equals("/quit", StringComparison.OrdinalIgnoreCase) || input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Session closed.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            ShowInteractiveHelp();
            continue;
        }

        if (input.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            await ShowStatusAsync(asJson: false);
            continue;
        }

        if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            messages = NewConversation(branding);
            Console.WriteLine("Conversation cleared.\n");
            continue;
        }

        messages.Add(new ChatMessage("user", input));
        var response = await CompleteAsync(messages);
        if (!response.Forwarded)
        {
            Console.WriteLine($"\nLocal model unavailable: {response.Error}");
            Console.WriteLine($"{RecoveryAdvice(response.Error)}\n");
            messages.RemoveAt(messages.Count - 1);
            continue;
        }

        messages.Add(new ChatMessage("assistant", response.Content));
        Console.WriteLine($"\n{response.Content}\n");
    }
}

async Task<int> AskOnceAsync(string prompt)
{
    if (string.IsNullOrWhiteSpace(prompt))
    {
        return Fail("A prompt is required: workspace-agent ask \"your question\"", 2);
    }

    var branding = await ReadAsync<Branding>("/api/branding");
    var messages = NewConversation(branding);
    messages.Add(new ChatMessage("user", prompt));
    var response = await CompleteAsync(messages);
    if (!response.Forwarded)
    {
        return Fail($"Local model unavailable: {response.Error}. {RecoveryAdvice(response.Error)}", 4);
    }

    Console.WriteLine(response.Content);
    return 0;
}

async Task<int> ShowStatusAsync(bool asJson)
{
    var status = await ReadAsync<InferenceStatus>("/api/inference/status");
    var healthUrl = new Uri(new Uri(status.ActiveProvider.Runtime.OpenAiCompatibleEndpoint), "../health");
    var ready = false;
    string? detail = null;

    try
    {
        using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await healthClient.GetAsync(healthUrl);
        ready = response.IsSuccessStatusCode;
        detail = ready ? "ready" : $"HTTP {(int)response.StatusCode}";
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        detail = exception.Message;
    }

    var result = new
    {
        runtimeApi = apiUrl,
        providerId = status.ActiveProviderId,
        provider = status.ActiveProvider.DisplayName,
        model = status.ActiveProvider.Model.UpstreamId,
        engine = status.ActiveProvider.Runtime.Engine,
        ready,
        detail
    };

    if (asJson)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    }
    else
    {
        Console.WriteLine($"Provider: {result.provider} ({result.providerId})");
        Console.WriteLine($"Engine:   {result.engine}");
        Console.WriteLine($"Model:    {result.model}");
        Console.WriteLine($"State:    {(ready ? "ready" : "not ready")}");
        if (!ready)
        {
            Console.WriteLine("Setup:    sudo workspace-model install");
        }
        Console.WriteLine();
    }

    return 0;
}

async Task<LocalChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages)
{
    using var response = await httpClient.PostAsJsonAsync("/api/inference/chat", new LocalChatRequest(messages, 0.2, 512));
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync<LocalChatResponse>()
        ?? throw new InvalidOperationException("The runtime returned an empty response.");
}

async Task<T> ReadAsync<T>(string path) =>
    await httpClient.GetFromJsonAsync<T>(path)
        ?? throw new InvalidOperationException($"The runtime returned an empty response for '{path}'.");

static List<ChatMessage> NewConversation(Branding branding) =>
[
    new ChatMessage(
        "system",
        $"You are {branding.AgentName}, the local assistant for {branding.ProductName}. " +
        "Be concise, ask before suggesting destructive actions, never claim a tool ran unless the runtime confirms it, " +
        "and prefer local, privacy-preserving actions.")
];

// The observation half of the surface contract: bounded state plus the
// currently-valid commands, exactly what the runtime shows the panel.
async Task<int> ObserveAsync(string surfaceId)
{
    var state = await ReadAsync<JsonElement>($"/api/surfaces/{surfaceId}/state");
    var commands = await ReadAsync<JsonElement>($"/api/surfaces/{surfaceId}/commands");
    Console.WriteLine(JsonSerializer.Serialize(new { state, commands }, jsonOptions));
    return 0;
}

// The action half: dispatch one typed command onto the same bus the panel
// buttons use. Arguments are --arg key=value pairs; --dry-run previews.
async Task<int> DoAsync(string[] doArgs)
{
    if (doArgs.Length < 2)
    {
        return Fail("Usage: workspace-agent do SURFACE COMMAND [--arg key=value ...] [--dry-run]", 2);
    }

    var surfaceId = doArgs[0];
    var commandName = doArgs[1];
    var input = new Dictionary<string, string>();
    var dryRun = false;
    for (var index = 2; index < doArgs.Length; index++)
    {
        if (doArgs[index] == "--dry-run")
        {
            dryRun = true;
        }
        else if (doArgs[index] == "--arg" && index + 1 < doArgs.Length)
        {
            var parts = doArgs[++index].Split('=', 2);
            if (parts.Length == 2)
            {
                input[parts[0]] = parts[1];
            }
        }
    }

    using var response = await httpClient.PostAsJsonAsync(
        $"/api/surfaces/{surfaceId}/commands/{commandName}",
        new { input, dryRun });
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        return Fail($"HTTP {(int)response.StatusCode}: {body}", 4);
    }

    Console.WriteLine(body);
    return 0;
}

static int ShowHelp()
{
    Console.WriteLine("workspace-agent                    Start an interactive local session");
    Console.WriteLine("workspace-agent ask PROMPT         Ask one question");
    Console.WriteLine("workspace-agent status [--json]    Show provider readiness");
    Console.WriteLine("workspace-agent observe [SURFACE]  Read surface state and valid commands (JSON)");
    Console.WriteLine("workspace-agent do SURFACE COMMAND [--arg k=v ...] [--dry-run]");
    Console.WriteLine("                                   Dispatch a typed command through policy");
    return 0;
}

static void ShowInteractiveHelp()
{
    Console.WriteLine("/status  Show local provider readiness");
    Console.WriteLine("/clear   Clear conversation history");
    Console.WriteLine("/quit    Close the session\n");
}

static string RecoveryAdvice(string? error) =>
    error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) is true
        ? "The model may still be warming up; wait a moment and try again."
        : "Run `sudo workspace-model install` to install or repair the configured provider.";

static int Fail(string message, int exitCode)
{
    Console.Error.WriteLine(message);
    return exitCode;
}

sealed record Branding(string ProductName, string AgentName);
sealed record ChatMessage(string Role, string Content);
sealed record LocalChatRequest(IReadOnlyList<ChatMessage> Messages, double Temperature, int MaxTokens);
sealed record LocalChatResponse(string ProviderId, string Model, string Content, bool Forwarded, string? Error);
sealed record InferenceStatus(string ActiveProviderId, InferenceProvider ActiveProvider);
sealed record InferenceProvider(string DisplayName, InferenceModel Model, InferenceRuntime Runtime);
sealed record InferenceModel(string UpstreamId);
sealed record InferenceRuntime(string Engine, string OpenAiCompatibleEndpoint);
