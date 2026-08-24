using System.Text.Json;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

public sealed class FileSurfaceRegistry : ISurfaceRegistry
{
    private static readonly HashSet<string> Decisions = new(StringComparer.Ordinal) { "Allow", "Deny", "RequireApproval" };
    private static readonly HashSet<string> ValidWhenVocabulary = new(StringComparer.Ordinal) { "always", "has-cells" };

    private readonly List<SurfaceManifest> surfaces = new();

    public FileSurfaceRegistry(string repositoryRoot)
    {
        var surfacesDirectory = Path.Combine(repositoryRoot, "surfaces");
        if (!Directory.Exists(surfacesDirectory))
        {
            throw new InvalidOperationException($"Surface manifest directory was not found: {surfacesDirectory}");
        }

        foreach (var path in Directory.EnumerateFiles(surfacesDirectory, "*.surface.json").OrderBy(name => name, StringComparer.Ordinal))
        {
            // Skip dotfiles such as macOS AppleDouble "._name.surface.json"
            // sidecars, which match the glob but are not manifests.
            if (Path.GetFileName(path).StartsWith('.'))
            {
                continue;
            }

            surfaces.Add(Load(path));
        }

        if (surfaces.Count == 0)
        {
            throw new InvalidOperationException($"No surface manifests were found in {surfacesDirectory}.");
        }
    }

    public IReadOnlyList<SurfaceManifest> Surfaces => surfaces;

    public SurfaceManifest? Find(string id) =>
        surfaces.FirstOrDefault(surface => string.Equals(surface.Id, id, StringComparison.Ordinal));

    // A malformed manifest fails startup rather than degrading silently: the
    // manifest is the contract the UI, policy engine, and agents all trust.
    private static SurfaceManifest Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var schema = RequireInt(root, "schema", path);
        if (schema != 2)
        {
            throw new InvalidOperationException($"{path}: unsupported schema {schema}; expected 2.");
        }

        var kind = RequireString(root, "kind", path);
        if (kind != "surface")
        {
            throw new InvalidOperationException($"{path}: unsupported kind '{kind}'.");
        }

        var stateElement = Require(root, "state", path);
        var state = new SurfaceStateSpec(
            RequireString(stateElement, "document", path),
            RequireBool(stateElement, "revisioned", path),
            Require(stateElement, "shape", path).Clone());

        var commands = new Dictionary<string, SurfaceCommandSpec>(StringComparer.Ordinal);
        foreach (var property in Require(root, "commands", path).EnumerateObject())
        {
            var command = property.Value;
            var policyElement = Require(command, "policy", path);
            var decision = RequireString(policyElement, "defaultDecision", path);
            if (!Decisions.Contains(decision))
            {
                throw new InvalidOperationException($"{path}: command '{property.Name}' has unknown defaultDecision '{decision}'.");
            }

            var validWhen = RequireString(command, "validWhen", path);
            if (!ValidWhenVocabulary.Contains(validWhen))
            {
                throw new InvalidOperationException($"{path}: command '{property.Name}' has unknown validWhen '{validWhen}'.");
            }

            commands[property.Name] = new SurfaceCommandSpec(
                RequireString(command, "displayName", path),
                RequireBool(command, "exposedToAgent", path),
                RequireBool(command, "requiresHuman", path),
                RequireBool(command, "mutatesState", path),
                RequireBool(command, "reversible", path),
                RequireBool(command, "dryRun", path),
                validWhen,
                Require(command, "input", path).Clone(),
                new SurfacePolicySpec(decision, RequireString(policyElement, "reason", path)));
        }

        if (commands.Count == 0)
        {
            throw new InvalidOperationException($"{path}: a surface must declare at least one command.");
        }

        return new SurfaceManifest(
            schema,
            kind,
            RequireString(root, "id", path),
            RequireString(root, "displayName", path),
            RequireString(root, "executor", path),
            state,
            commands,
            root.TryGetProperty("targetsSession", out var targets)
                && targets.ValueKind == JsonValueKind.True);
    }

    private static JsonElement Require(JsonElement element, string name, string path) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidOperationException($"{path}: missing required property '{name}'.");

    private static string RequireString(JsonElement element, string name, string path) =>
        Require(element, name, path).GetString()
            ?? throw new InvalidOperationException($"{path}: property '{name}' must be a string.");

    private static int RequireInt(JsonElement element, string name, string path) =>
        Require(element, name, path).GetInt32();

    private static bool RequireBool(JsonElement element, string name, string path) =>
        Require(element, name, path).GetBoolean();
}
