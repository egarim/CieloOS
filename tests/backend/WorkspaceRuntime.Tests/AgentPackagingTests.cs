using System.Text.Json;

namespace WorkspaceRuntime.Tests;

public class AgentPackagingTests
{
    [Fact]
    public void Local_inference_service_runs_the_structured_model_manager()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "distro", "services", "local-inference.service"));

        Assert.Contains("ExecStart=/usr/local/sbin/workspace-model run", service);
        Assert.DoesNotContain("placeholder", service, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DynamicUser=yes", service);
        Assert.Contains("ProtectSystem=strict", service);
    }

    [Fact]
    public void Agent_service_checks_the_real_provider_without_a_placeholder_loop()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "distro", "services", "agent-runtime.service"));

        Assert.Contains("ExecStart=/usr/local/bin/workspace-agent status --json", service);
        Assert.DoesNotContain("sleep infinity", service);
    }

    [Fact]
    public void Bonsai_profile_pins_runtime_and_model_supply_chain_inputs()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "distro", "models", "providers", "prism-bonsai-4b.json");
        using var profile = JsonDocument.Parse(File.ReadAllText(path));
        var model = profile.RootElement.GetProperty("model");
        var runtime = profile.RootElement.GetProperty("runtime");

        Assert.Matches("^[0-9a-f]{64}$", model.GetProperty("artifact").GetProperty("sha256").GetString()!);
        Assert.Matches("^[0-9a-f]{40}$", runtime.GetProperty("source").GetProperty("revision").GetString()!);
        Assert.StartsWith("https://", model.GetProperty("artifact").GetProperty("url").GetString());
        Assert.StartsWith("https://", runtime.GetProperty("source").GetProperty("repository").GetString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkspaceRuntime.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
