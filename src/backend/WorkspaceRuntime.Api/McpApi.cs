using System.Text.Json;
using System.Text.Json.Nodes;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

// The surface registry, served over MCP.
//
// This is what lets something other than our own loop do work here without also
// deciding what it is allowed to do. An engine gets tools/list and tools/call and
// nothing else: every call lands on the same AgentRuntime.SubmitAsync that a
// person's panel uses, so ownership, the policy decision, approvals and the audit
// trail apply without a second implementation of any of them.
//
// Nothing here is new capability. surfaces/*.surface.json was already an MCP tool
// list — `input` is JSON Schema verbatim, `exposedToAgent` is the filter,
// `mutatesState`/`reversible` are the annotations — it simply had no server.
//
// Transport is streamable-http (JSON-RPC over POST), not SSE. Measured, not
// chosen: OpenClaw's default SSE transport opens with a GET and got a 405, and
// registered only once it was told `--transport streamable-http`.
public static class McpApi
{
    private const string ServerName = "cielo-surfaces";
    private const string ServerVersion = "1";
    private const string DefaultProtocol = "2025-06-18";

    public static void Map(WebApplication app)
    {
        app.MapPost("/mcp", async (JsonElement body, HttpContext context, AgentRuntime runtime, ISurfaceRegistry surfaces, IRuntimeStore store, CancellationToken cancellationToken) =>
        {
            var method = body.TryGetProperty("method", out var rawMethod) ? rawMethod.GetString() ?? "" : "";
            var id = body.TryGetProperty("id", out var rawId) ? JsonNode.Parse(rawId.GetRawText()) : null;

            // Notifications are one-way by definition; answering one with a result
            // is a protocol error, not a courtesy.
            if (method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status202Accepted);
            }

            switch (method)
            {
                case "initialize":
                    // Echo the client's protocol version rather than asserting our
                    // own: an engine that speaks an older one is still an engine.
                    var version = body.TryGetProperty("params", out var parameters)
                        && parameters.TryGetProperty("protocolVersion", out var rawVersion)
                        ? rawVersion.GetString() ?? DefaultProtocol
                        : DefaultProtocol;
                    return Envelope(id, new
                    {
                        protocolVersion = version,
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = ServerName, version = ServerVersion }
                    });

                case "tools/list":
                    return Envelope(id, new { tools = Tools(surfaces) });

                case "tools/call":
                    return await CallAsync(id, body, context, runtime, store, surfaces, cancellationToken);

                default:
                    return Envelope(id, error: new { code = -32601, message = $"Unknown method '{method}'." });
            }
        });
    }

    // Every agent-exposed command, as a tool.
    //
    // RequiresHuman commands are omitted rather than offered-and-refused. A tool an
    // engine can see is a tool it will spend a step discovering it cannot use, and
    // the bus would refuse it anyway — HumanOnly is not negotiable by a token.
    private static List<object> Tools(ISurfaceRegistry surfaces)
    {
        var tools = new List<object>();
        foreach (var surface in surfaces.Surfaces)
        {
            foreach (var (name, command) in surface.Commands)
            {
                if (!command.ExposedToAgent || command.RequiresHuman)
                {
                    continue;
                }

                tools.Add(new
                {
                    name = $"{surface.Id}.{name}",
                    title = command.DisplayName,
                    description = command.Policy.Reason,
                    inputSchema = command.Input,
                    annotations = new
                    {
                        readOnlyHint = !command.MutatesState,
                        // What "destructive" means here is already decided in the
                        // manifest, and it is the same field the approval dialog
                        // shows a person. One source, so an engine and its owner are
                        // told the same thing about the same command.
                        destructiveHint = !command.Reversible
                    }
                });
            }
        }

        return tools;
    }

    private static async Task<IResult> CallAsync(
        JsonNode? id,
        JsonElement body,
        HttpContext context,
        AgentRuntime runtime,
        IRuntimeStore store,
        ISurfaceRegistry surfaces,
        CancellationToken cancellationToken)
    {
        if (!body.TryGetProperty("params", out var parameters))
        {
            return Envelope(id, error: new { code = -32602, message = "Missing params." });
        }

        var called = parameters.TryGetProperty("name", out var rawName) ? rawName.GetString() ?? "" : "";
        var split = called.IndexOf('.');
        if (split <= 0 || split == called.Length - 1)
        {
            return Envelope(id, Failed(NotATool(called)));
        }

        // What we dispatch is what gets audited — never the string the caller used.
        // An engine may rename our tools: OpenClaw registers `cielo.write_file` as
        // `cielo__cielo-write_file` "to keep the tool name provider-safe". An audit
        // trail written from the caller's spelling would be a record of what the
        // engine called things rather than of what happened here.
        var surfaceId = called[..split];
        var operation = called[(split + 1)..];

        if (surfaces.Find(surfaceId) is not { } manifest
            || !manifest.Commands.TryGetValue(operation, out var spec)
            || !spec.ExposedToAgent
            || spec.RequiresHuman)
        {
            return Envelope(id, Failed(NotATool(called)));
        }

        var arguments = parameters.TryGetProperty("arguments", out var raw) && raw.ValueKind == JsonValueKind.Object
            ? SurfaceArguments.From(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw.GetRawText()))
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var caller = (RuntimePrincipal)context.Items["principal"]!;
        var (userId, agentId) = Acting(caller, store);
        if (agentId == Guid.Empty)
        {
            return Envelope(id, Failed("This account has no agent to act as, so nothing was done."));
        }

        var result = await runtime.SubmitAsync(
            new SubmitToolRequestDto(userId, agentId, surfaceId, operation, arguments),
            caller,
            cancellationToken);

        // Deny and RequireApproval both come back NOW, as an error carrying the
        // reason — they do not hold the call open while somebody decides.
        //
        // That was measured. Holding the call open was the first design here, and
        // OpenClaw cancels one at ~30s, lets the side effect land anyway, and then
        // tells the owner "Done". Answering immediately instead produced an honest
        // reply that said nothing had happened and asked for approval. So the
        // refusal IS the question: it ends the engine's turn, nothing is held open,
        // and the owner answers in the panel where the approval already lives.
        return result.Decision switch
        {
            PolicyDecision.Allow => Envelope(id, new
            {
                content = new[] { new { type = "text", text = result.Execution?.Message ?? "Done." } },
                isError = result.Execution?.Executed == false
            }),

            PolicyDecision.RequireApproval => Envelope(id, Failed(
                $"Not done — this needs approval from {OwnerName(caller, store)} first. {result.Reason} "
                + "Nothing has changed. Say what you want to do and why, and ask them to approve it"
                + (result.Approval is { } approval ? $" (request {approval.Id})." : "."))),

            _ => Envelope(id, Failed($"Refused, and nothing was changed. {result.Reason}"))
        };
    }

    private static string NotATool(string called) =>
        $"'{called}' is not a tool on this machine, and nothing was done.";

    // The phrasing matters more than it looks. "Nothing has changed" is the part a
    // model acts on: told only that a call failed, it retries or reports success
    // anyway; told plainly that the action did not happen, it says so and asks.
    private static object Failed(string text) => new
    {
        content = new[] { new { type = "text", text } },
        isError = true
    };

    private static string OwnerName(RuntimePrincipal caller, IRuntimeStore store)
    {
        var slug = Ownership.RootUserSlug(caller.Slug, store);
        return store.Users.FirstOrDefault(user => user.Slug == slug)?.DisplayName ?? slug;
    }

    // An agent acts as itself; a person acts through the agent they own. The same
    // rule as every other way into the bus, and the bus re-checks it regardless.
    private static (Guid userId, Guid agentId) Acting(RuntimePrincipal principal, IRuntimeStore store)
    {
        if (principal.Kind == PrincipalKind.Agent)
        {
            var self = store.GetAgent(principal.Subject);
            return (self.OwnerUserId, self.Id);
        }

        var owned = store.Agents.FirstOrDefault(agent => agent.OwnerUserId == principal.Subject);
        return (principal.Subject, owned?.Id ?? Guid.Empty);
    }

    private static IResult Envelope(JsonNode? id, object? result = null, object? error = null) =>
        Results.Json(error is null
            ? new { jsonrpc = "2.0", id, result }
            : (object)new { jsonrpc = "2.0", id, error });
}
