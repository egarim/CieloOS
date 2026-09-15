using System.Text.Json;

namespace WorkspaceRuntime.Application;

// What it takes to run somebody else's agent harness here.
//
// Shaped like surfaces/*.surface.json on purpose: a file on disk, read at
// startup, describing something the runtime will do rather than code that does
// it. An engine that needs a code change to add is an engine nobody adds.
//
// Everything in Install is pinned. "Install the latest OpenClaw" from a panel is
// a supply-chain decision made by whoever happened to click it, and the person
// clicking is usually not the person who would notice.
public sealed record EngineManifest(
    int Schema,
    string Kind,
    string Id,
    string DisplayName,
    string Version,
    EngineInstall Install,
    EngineInvoke Invoke,
    // "none" is the only value that means anything today, and it is the point: the
    // engine's container gets no network, and the two endpoints below reach it
    // through a mounted socket. Recorded here so a manifest asking for something
    // laxer has to say so in writing.
    string Network,
    string? Notes = null);

public sealed record EngineInstall(
    string Kind,              // "npm"
    string Package,           // pinned, e.g. "openclaw@2026.9.4"
    string? Digest,           // recorded now; verified when the wizard installs
    IReadOnlyList<string>? Configure);

public sealed record EngineInvoke(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string>? Env);

public interface IEngineCatalog
{
    IReadOnlyList<EngineManifest> Engines { get; }
    EngineManifest? Find(string id);
}

public sealed class FileEngineCatalog : IEngineCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FileEngineCatalog(string root)
    {
        var directory = Path.Combine(root, "engines");
        Engines = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.engine.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => JsonSerializer.Deserialize<EngineManifest>(File.ReadAllText(path), Json))
                .Where(manifest => manifest is not null)
                .Select(manifest => manifest!)
                .ToList()
            : new List<EngineManifest>();
    }

    public IReadOnlyList<EngineManifest> Engines { get; }

    public EngineManifest? Find(string id) =>
        Engines.FirstOrDefault(engine => string.Equals(engine.Id, id, StringComparison.OrdinalIgnoreCase));
}

// Where an engine's two endpoints live, from inside its container.
//
// Both are loopback, because the container has no network: a forwarder inside it
// carries these to a bind-mounted unix socket. Measured — see
// docs/agent-engines.md — with --network=none there is no DNS and no route, but
// lo still works, so an engine sees ordinary HTTP and needs no special support.
public sealed record EngineEndpoints(string ModelUrl, string McpUrl, string Token);

public sealed record EngineInvocation(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env);

public sealed record EngineProcessResult(int ExitCode, string Stdout, string Stderr);

// Runs an engine's command inside a session's container. Abstracted so the engine
// can be tested without podman, and so the day a session is an Incus container
// instead is one implementation rather than a rewrite.
public interface IEngineProcess
{
    Task<EngineProcessResult> RunAsync(string sessionId, EngineInvocation invocation, CancellationToken cancellationToken);
}
