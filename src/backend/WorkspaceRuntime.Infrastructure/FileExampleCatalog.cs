using System.Text.Json;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// The example catalogue, read from the same files the desktop image ships into
// ~/Desktop/Examples. One source: what the person reads in the folder and what the
// panel runs are the same thing, so a demo cannot describe a step it does not take.
public sealed class FileExampleCatalog : IExampleCatalog
{
    private readonly List<Example> examples = new();

    public FileExampleCatalog(string repositoryRoot)
    {
        // Two layouts, because there are two: a git checkout keeps these under
        // distro/, and the shipped bundle flattens that away — the same reason
        // Sessions:ProfileImagesPath points at <bundle>/images/profiles. Looking in
        // both is what stops the panel showing an empty list on a real install.
        var root = new[]
            {
                Path.Combine(repositoryRoot, "distro", "images", "desktop", "examples"),
                Path.Combine(repositoryRoot, "images", "desktop", "examples"),
            }
            .FirstOrDefault(Directory.Exists);

        if (root is null)
        {
            // No examples is not a broken installation — an older bundle simply
            // does not carry them. The panel shows an empty list and says so.
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(name => name, StringComparer.Ordinal))
        {
            var manifest = Path.Combine(directory, "example.json");
            if (!File.Exists(manifest))
            {
                continue;
            }
            examples.Add(Load(manifest));
        }
    }

    public IReadOnlyList<Example> Examples => examples;

    public Example? Find(string id) =>
        examples.FirstOrDefault(example => string.Equals(example.Id, id, StringComparison.Ordinal));

    private static Example Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var steps = new List<ExampleStep>();
        if (root.TryGetProperty("steps", out var stepList) && stepList.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in stepList.EnumerateArray())
            {
                var input = new Dictionary<string, string>(StringComparer.Ordinal);
                if (step.TryGetProperty("input", out var inputElement) && inputElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in inputElement.EnumerateObject())
                    {
                        input[property.Name] = property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString() ?? ""
                            : property.Value.ToString();
                    }
                }

                steps.Add(new ExampleStep(
                    Text(step, "surface"),
                    Text(step, "operation"),
                    input,
                    Text(step, "note"),
                    Text(step, "kind") is { Length: > 0 } kind ? kind : "command"));
            }
        }

        return new Example(
            Text(root, "id"),
            Text(root, "title"),
            Text(root, "summary"),
            root.TryGetProperty("needsSession", out var needs) && needs.ValueKind == JsonValueKind.True,
            steps);
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
