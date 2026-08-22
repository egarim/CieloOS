using System.Text.Json;

namespace WorkspaceRuntime.Tests;

public class AutomationToolManifestTests
{
    [Fact]
    public void Dotnet_automation_profile_references_existing_structured_tool_manifests()
    {
        var root = FindRepositoryRoot();
        var profilePath = Path.Combine(root, "distro", "profiles", "dotnet-automation.json");
        using var profileJson = JsonDocument.Parse(File.ReadAllText(profilePath));

        var manifests = profileJson.RootElement.GetProperty("toolManifests")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToList();

        Assert.Contains("tools/dotnet.build.json", manifests);
        Assert.Contains("tools/dotnet.test.json", manifests);
        Assert.Contains("tools/dotnet.script.json", manifests);
        Assert.Contains("tools/powershell.run.json", manifests);

        foreach (var manifest in manifests)
        {
            Assert.False(string.IsNullOrWhiteSpace(manifest));
            var manifestPath = Path.Combine(root, "distro", manifest!);
            Assert.True(File.Exists(manifestPath));

            using var toolJson = JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert.Equal(1, toolJson.RootElement.GetProperty("schema").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(toolJson.RootElement.GetProperty("toolName").GetString()));
            Assert.Equal("sandboxed-process", toolJson.RootElement.GetProperty("executor").GetString());
            Assert.True(toolJson.RootElement.GetProperty("command").TryGetProperty("argsTemplate", out _));
        }
    }

    [Fact]
    public void Dotnet_script_uses_native_dotnet_10_file_apps()
    {
        var root = FindRepositoryRoot();
        var manifestPath = Path.Combine(root, "distro", "tools", "dotnet.script.json");
        using var manifestJson = JsonDocument.Parse(File.ReadAllText(manifestPath));

        var command = manifestJson.RootElement.GetProperty("command");
        Assert.Equal("dotnet", command.GetProperty("executable").GetString());
        Assert.Equal(
            ["run", "--file", "{script}"],
            command.GetProperty("argsTemplate").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public void PowerShell_arm64_archive_is_versioned_and_checksum_pinned()
    {
        var root = FindRepositoryRoot();
        var profilePath = Path.Combine(root, "distro", "profiles", "dotnet-automation.json");
        using var profileJson = JsonDocument.Parse(File.ReadAllText(profilePath));

        var powershell = profileJson.RootElement.GetProperty("packages").GetProperty("powershell");
        Assert.Equal("arm64", powershell.GetProperty("architecture").GetString());
        Assert.Matches(@"^7\.\d+\.\d+$", powershell.GetProperty("version").GetString()!);
        Assert.Equal(
            "d4ef2382fa452f2ccbdb48a01adbbce9ed64954872123970c16be6d086d1224b",
            powershell.GetProperty("sha256").GetString());
        Assert.StartsWith("https://github.com/PowerShell/PowerShell/releases/download/", powershell.GetProperty("source").GetString());
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
