namespace WorkspaceRuntime.Tests;

public class RenameSafetyTests
{
    [Fact]
    public void Stable_backend_identifiers_do_not_embed_brand_terms()
    {
        var root = FindRepositoryRoot();
        var stableFiles = Directory.EnumerateFiles(Path.Combine(root, "src", "backend"), "*.*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

        var forbidden = new[] { "namespace Lun", "LunOS", "Lun.Os", "<AssemblyName>Lun", "<RootNamespace>Lun" };
        var violations = stableFiles
            .SelectMany(path => forbidden
                .Where(term => File.ReadAllText(path).Contains(term, StringComparison.Ordinal))
                .Select(term => $"{Path.GetRelativePath(root, path)} contains '{term}'"))
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "WorkspaceRuntime.sln")) &&
               !File.Exists(Path.Combine(directory.FullName, "WorkspaceRuntime.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
