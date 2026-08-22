using WorkspaceRuntime.Application;
using System.Text.Json;

namespace WorkspaceRuntime.Tests;

public class InstallationPlannerTests
{
    private static readonly InstallationProbe SuitableProbe = new(
        "arm64",
        8192,
        IsUefi: true,
        [new InstallationDisk("/dev/vda", 64L * 1024 * 1024 * 1024, IsReadOnly: false, IsRemovable: false, "QEMU disk")]);

    [Fact]
    public void Defaults_are_local_first_and_require_human_approval()
    {
        var planner = new InstallationPlanner();

        var defaults = planner.GetDefaults();
        var plan = planner.CreatePlan(new InstallationRequest(), SuitableProbe);

        Assert.True(defaults.EnableDiskEncryption);
        Assert.False(defaults.EnableSsh);
        Assert.False(defaults.AllowCloudInferenceFallback);
        Assert.Equal("prism-bonsai-4b", defaults.LocalInferenceProvider);
        Assert.Equal("/dev/vda", plan.TargetDisk);
        Assert.True(plan.IsDestructive);
        Assert.True(plan.RequiresHumanApproval);
    }

    [Fact]
    public void Identical_input_produces_identical_plan_identifier()
    {
        var planner = new InstallationPlanner();
        var request = new InstallationRequest(Hostname: "office-system", AdminUser: "owner");

        var first = planner.CreatePlan(request, SuitableProbe);
        var second = planner.CreatePlan(request, SuitableProbe);

        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Equal(64, first.PlanId.Length);
    }

    [Fact]
    public void Multiple_disks_require_an_explicit_target()
    {
        var planner = new InstallationPlanner();
        var probe = SuitableProbe with
        {
            Disks =
            [
                SuitableProbe.Disks[0],
                new InstallationDisk("/dev/vdb", 128L * 1024 * 1024 * 1024, false, false, "Second disk")
            ]
        };

        var plan = planner.CreatePlan(new InstallationRequest(), probe);
        var validation = planner.Validate(plan, probe);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "storage.target-required");
    }

    [Fact]
    public void Modified_plan_is_rejected_before_approval()
    {
        var planner = new InstallationPlanner();
        var plan = planner.CreatePlan(new InstallationRequest(), SuitableProbe);
        var modified = plan with { TargetDisk = "/dev/vdb" };

        var validation = planner.Validate(modified, SuitableProbe);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "plan.hash-mismatch");
    }

    [Fact]
    public void Approval_token_must_equal_the_validated_plan_identifier()
    {
        var planner = new InstallationPlanner();
        var plan = planner.CreatePlan(new InstallationRequest(), SuitableProbe);

        Assert.Throws<UnauthorizedAccessException>(() =>
            planner.Approve(plan, SuitableProbe, new string('0', 64), "owner", DateTimeOffset.UnixEpoch));

        var approval = planner.Approve(plan, SuitableProbe, plan.PlanId, "owner", DateTimeOffset.UnixEpoch);
        Assert.True(approval.AuthorizesDestructiveInstallation);
        Assert.Equal(plan.PlanId, approval.PlanId);
    }

    [Fact]
    public void Tool_contract_does_not_expose_approval_to_the_agent()
    {
        var root = FindRepositoryRoot();
        var contractPath = Path.Combine(root, "distro", "installer", "tool-contract.json");
        using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));
        var commands = contract.RootElement.GetProperty("commands").EnumerateArray();
        var approve = commands.Single(command => command.GetProperty("name").GetString() == "approve");

        Assert.False(approve.GetProperty("exposedToAgent").GetBoolean());
        Assert.True(approve.GetProperty("requiresTrustedCaller").GetBoolean());
        Assert.False(contract.RootElement.GetProperty("constraints").GetProperty("freeFormShell").GetBoolean());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "WorkspaceRuntime.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
