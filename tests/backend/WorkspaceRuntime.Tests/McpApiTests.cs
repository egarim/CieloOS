using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The MCP server is the one way a foreign engine reaches this machine. These go
// over real HTTP rather than calling the projection directly, because the thing
// worth pinning is the whole path: a JSON-RPC body in, the policy bus in the
// middle, an audit line out.
public class McpApiTests
{
    [Fact]
    public async Task Tools_are_the_surface_manifests_and_nothing_else()
    {
        await using var api = await StartAsync();

        var tools = (await CallAsync(api, "tools/list")).GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToList();

        Assert.Contains("spreadsheet.set-cell", names);
        Assert.Contains("spreadsheet.clear", names);

        // session-input.grant is requiresHuman. Offering it would spend an engine a
        // step to discover the bus refuses it, and the refusal is not negotiable by
        // a token, so it is never listed.
        Assert.DoesNotContain(names, name => name!.StartsWith("session-input.grant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tool_carries_the_manifests_own_schema_and_its_own_idea_of_destructive()
    {
        await using var api = await StartAsync();
        var tools = (await CallAsync(api, "tools/list")).GetProperty("result").GetProperty("tools");

        var setCell = Tool(tools, "spreadsheet.set-cell");
        var clear = Tool(tools, "spreadsheet.clear");

        // The schema is the manifest's, verbatim — not a translation of it.
        var schema = setCell.GetProperty("inputSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("address", out _));

        // reversible:true -> not destructive; reversible:false -> destructive. One
        // source, so an engine and its owner are told the same thing.
        Assert.False(setCell.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.True(clear.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.False(setCell.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    [Fact]
    public async Task An_allowed_call_goes_through_the_bus_and_lands_on_the_audit_trail()
    {
        await using var api = await StartAsync();

        var response = await CallAsync(api, "tools/call", new
        {
            name = "spreadsheet.set-cell",
            arguments = new { address = "A1", value = "42" }
        });

        var result = response.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Single(api.Executor.Executed);
        Assert.Equal("spreadsheet", api.Executor.Executed[0].ToolName);
        Assert.Equal("set-cell", api.Executor.Executed[0].Operation);

        // The audit is keyed on what WE dispatched. An engine may call our tools by
        // another name — OpenClaw registers them with a mangled one — and a trail
        // written from the caller's spelling would record what the engine called
        // things rather than what happened here.
        Assert.Contains(api.Store.AuditEvents, audit => audit.Action == "spreadsheet.set-cell");
    }

    [Fact]
    public async Task Approval_comes_back_immediately_saying_nothing_happened()
    {
        await using var api = await StartAsync();

        var result = (await CallAsync(api, "tools/call", new
        {
            name = "spreadsheet.clear",
            arguments = new { }
        })).GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;

        // The phrasing is load-bearing, not decoration. Told only that a call
        // failed, a model retries or claims success anyway; told plainly that the
        // action did not happen, it says so and asks. Measured against OpenClaw:
        // this wording produced "nothing was created... approve the write and I'll
        // create it right away", where holding the call open produced "Done".
        Assert.Contains("Nothing has changed", text, StringComparison.Ordinal);
        Assert.Contains("approv", text, StringComparison.OrdinalIgnoreCase);

        // And it must be true: the executor was never reached.
        Assert.Empty(api.Executor.Executed);
    }

    [Fact]
    public async Task A_tool_this_machine_does_not_have_is_refused_and_says_so()
    {
        await using var api = await StartAsync();

        foreach (var name in new[] { "cielo__cielo-write_file", "spreadsheet", "nope.nope", "" })
        {
            var result = (await CallAsync(api, "tools/call", new { name, arguments = new { } })).GetProperty("result");
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Contains("nothing was done", result.GetProperty("content")[0].GetProperty("text").GetString()!, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(api.Executor.Executed);
    }

    [Fact]
    public async Task Initialize_speaks_the_clients_protocol_version()
    {
        await using var api = await StartAsync();

        var result = (await CallAsync(api, "initialize", new { protocolVersion = "2024-11-05" })).GetProperty("result");

        // An engine speaking an older protocol is still an engine.
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task A_notification_is_not_answered()
    {
        await using var api = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", method = "notifications/initialized" })
        };
        request.Headers.Add("X-Test-Principal", "joche");
        var response = await api.Client.SendAsync(request);

        // Answering a one-way message with a result is a protocol error, not a
        // courtesy.
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
    }

    private static JsonElement Tool(JsonElement tools, string name) =>
        tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == name);

    private static async Task<JsonElement> CallAsync(TestMcpApi api, string method, object? parameters = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(parameters is null
                ? new { jsonrpc = "2.0", id = 1, method }
                : (object)new { jsonrpc = "2.0", id = 1, method, @params = parameters })
        };
        request.Headers.Add("X-Test-Principal", "joche");
        var response = await api.Client.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<TestMcpApi> StartAsync()
    {
        var store = new InMemoryRuntimeStore();
        var executor = new RecordingExecutor();
        var runtime = new AgentRuntime(store, TestRepository.PolicyEngine(), executor, TestRepository.Surfaces());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IRuntimeStore>(store);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(TestRepository.Surfaces());
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var slug = context.Request.Headers["X-Test-Principal"].ToString();
            var user = store.Users.FirstOrDefault(candidate => candidate.Slug == slug);
            if (user is not null)
            {
                context.Items["principal"] = new RuntimePrincipal(PrincipalKind.Human, user.Id, user.Slug, user.DisplayName);
            }
            await next();
        });

        McpApi.Map(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        return new TestMcpApi(app, new HttpClient { BaseAddress = new Uri(address) }, store, executor);
    }

    // Records rather than executes: these tests are about the MCP layer and the
    // bus, and an executor that really wrote a spreadsheet would make "did the
    // policy stop it" harder to read, not easier.
    private sealed class RecordingExecutor : ISandboxedToolExecutor
    {
        public List<ToolRequest> Executed { get; } = new();

        public Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            Executed.Add(request);
            return Task.FromResult(new ToolExecutionResult(true, $"{request.ToolName}.{request.Operation} done.", null));
        }
    }

    private sealed class TestMcpApi : IAsyncDisposable
    {
        public TestMcpApi(WebApplication app, HttpClient client, InMemoryRuntimeStore store, RecordingExecutor executor)
        {
            App = app;
            Client = client;
            Store = store;
            Executor = executor;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public InMemoryRuntimeStore Store { get; }
        public RecordingExecutor Executor { get; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}
