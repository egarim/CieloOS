using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

public sealed record SurfaceManifest(
    int Schema,
    string Kind,
    string Id,
    string DisplayName,
    string Executor,
    SurfaceStateSpec State,
    IReadOnlyDictionary<string, SurfaceCommandSpec> Commands);

public sealed record SurfaceStateSpec(
    string Document,
    bool Revisioned,
    JsonElement Shape);

public sealed record SurfaceCommandSpec(
    string DisplayName,
    bool ExposedToAgent,
    bool RequiresHuman,
    bool MutatesState,
    bool Reversible,
    bool DryRun,
    string ValidWhen,
    JsonElement Input,
    SurfacePolicySpec Policy);

public sealed record SurfacePolicySpec(string DefaultDecision, string Reason);

public interface ISurfaceRegistry
{
    IReadOnlyList<SurfaceManifest> Surfaces { get; }
    SurfaceManifest? Find(string id);
}

// The single policy source of truth: decisions come from surface manifests,
// with deny-by-default as the backstop for anything unregistered.
public sealed class ManifestPolicyEngine : IPolicyEngine
{
    private readonly ISurfaceRegistry surfaces;

    public ManifestPolicyEngine(ISurfaceRegistry surfaces)
    {
        this.surfaces = surfaces;
    }

    public PolicyEvaluation Evaluate(PlatformUser user, AgentProfile agent, ToolRequest request)
    {
        if (agent.OwnerUserId != user.Id || agent.Id != request.AgentId)
        {
            return Deny("The agent does not belong to the requesting user.", "ownership");
        }

        if (!agent.GrantedTools.Contains(request.ToolName))
        {
            return Deny($"Agent '{agent.Name}' has not been granted '{request.ToolName}'.", "tool-grant");
        }

        var manifest = surfaces.Find(request.ToolName);
        if (manifest is null || !manifest.Commands.TryGetValue(request.Operation, out var command))
        {
            return Deny($"Operation '{request.ToolName}.{request.Operation}' is not registered.", "deny-by-default");
        }

        var evidence = new Dictionary<string, string>
        {
            ["rule"] = "surface-manifest",
            ["surface"] = manifest.Id,
            ["command"] = request.Operation,
            ["reversible"] = command.Reversible ? "true" : "false"
        };

        return command.Policy.DefaultDecision switch
        {
            "Allow" => new PolicyEvaluation(PolicyDecision.Allow, command.Policy.Reason, evidence),
            "RequireApproval" => new PolicyEvaluation(PolicyDecision.RequireApproval, command.Policy.Reason, evidence),
            _ => new PolicyEvaluation(PolicyDecision.Deny, command.Policy.Reason, evidence)
        };
    }

    private static PolicyEvaluation Deny(string reason, string rule) =>
        new(PolicyDecision.Deny, reason, new Dictionary<string, string> { ["rule"] = rule });
}

public static class RequestHasher
{
    // Canonical form: tool, operation, and arguments in ordinal key order.
    // Approvals bind to this hash; any change to the pending request invalidates consent.
    public static string Compute(ToolRequest request) =>
        Compute(request.ToolName, request.Operation, request.Arguments);

    public static string Compute(string toolName, string operation, IReadOnlyDictionary<string, string> arguments)
    {
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in arguments)
        {
            canonical[pair.Key] = pair.Value;
        }

        var payload = JsonSerializer.Serialize(new
        {
            toolName,
            operation,
            arguments = canonical
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

public interface IDryRunToolExecutor
{
    Task<EffectPreview> PreviewAsync(ToolRequest request, CancellationToken cancellationToken);
}

// Structural validation of command input against the manifest schema:
// required keys, unknown keys, per-property pattern and maxLength. This runs
// at the AgentRuntime choke point so every entry path enforces the same
// contract the UI and agents read from the manifest.
public static class SurfaceInputValidator
{
    public static string? Validate(JsonElement schema, IReadOnlyDictionary<string, string> arguments)
    {
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray())
            {
                var key = name.GetString() ?? "";
                if (!arguments.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    return $"Missing required input '{key}'.";
                }
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties);
        var additionalAllowed = !schema.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind != JsonValueKind.False;

        if (!additionalAllowed && hasProperties)
        {
            var known = properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var key in arguments.Keys)
            {
                if (!known.Contains(key))
                {
                    return $"Unknown input '{key}'.";
                }
            }
        }

        if (hasProperties)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (!arguments.TryGetValue(property.Name, out var value))
                {
                    continue;
                }

                if (property.Value.TryGetProperty("maxLength", out var maxLength) && value.Length > maxLength.GetInt32())
                {
                    return $"Input '{property.Name}' exceeds the maximum length of {maxLength.GetInt32()}.";
                }

                if (property.Value.TryGetProperty("pattern", out var pattern))
                {
                    var expression = pattern.GetString();
                    if (!string.IsNullOrEmpty(expression) &&
                        !System.Text.RegularExpressions.Regex.IsMatch(value, expression, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200)))
                    {
                        return $"Input '{property.Name}' does not match the required pattern.";
                    }
                }
            }
        }

        return null;
    }
}

public static class SurfaceConditions
{
    public static bool IsValidNow(string validWhen, IRuntimeStore store) => validWhen switch
    {
        "has-cells" => store.Spreadsheet.Cells.Count > 0,
        _ => true
    };
}

public sealed record RuntimeEvent(string Type, long Revision, DateTimeOffset OccurredAt);

public interface IRuntimeEventStream
{
    void Publish(RuntimeEvent runtimeEvent);
    IAsyncEnumerable<RuntimeEvent> SubscribeAsync(CancellationToken cancellationToken);
}

public sealed class ChannelRuntimeEventStream : IRuntimeEventStream
{
    private readonly object gate = new();
    private readonly List<Channel<RuntimeEvent>> subscribers = new();

    public void Publish(RuntimeEvent runtimeEvent)
    {
        lock (gate)
        {
            foreach (var subscriber in subscribers)
            {
                subscriber.Writer.TryWrite(runtimeEvent);
            }
        }
    }

    public async IAsyncEnumerable<RuntimeEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<RuntimeEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        lock (gate)
        {
            subscribers.Add(channel);
        }

        try
        {
            await foreach (var runtimeEvent in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return runtimeEvent;
            }
        }
        finally
        {
            lock (gate)
            {
                subscribers.Remove(channel);
            }
        }
    }
}
