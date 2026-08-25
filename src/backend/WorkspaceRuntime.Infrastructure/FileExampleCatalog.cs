using System.Text.Json;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// The example catalogue, read from the same files the desktop image ships into
// ~/Desktop/Examples. One source: what the person reads in the folder and what the
// panel runs are the same thing, so a demo cannot describe a step it does not take.
public sealed class FileExampleCatalog : IExampleCatalog
{
    private readonly List<Example> examples = new();

    // THREE layouts, because there are three, and getting this wrong shows up as an
    // empty Examples list rather than as an error:
    //
    //   git checkout   <root>/distro/images/desktop/examples
    //   run.sh bundle  <root>/images/desktop/examples
    //   install.sh     /var/lib/cielo/images/desktop/examples
    //
    // The installed case is the one that bites: install.sh puts the image tree under
    // /var/lib/cielo/images while the runtime root is /opt/cielo, so nothing under
    // the runtime root contains them. It is passed in rather than guessed, derived
    // from the same setting that locates the desk-profile build contexts, so the two
    // cannot drift apart.
    public FileExampleCatalog(string repositoryRoot, string? installedImagesRoot = null)
    {
        var candidates = new List<string>
        {
            Path.Combine(repositoryRoot, "distro", "images", "desktop", "examples"),
            Path.Combine(repositoryRoot, "images", "desktop", "examples"),
        };
        if (!string.IsNullOrWhiteSpace(installedImagesRoot))
        {
            candidates.Add(Path.Combine(installedImagesRoot, "desktop", "examples"));
        }

        var root = candidates.FirstOrDefault(Directory.Exists);

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
