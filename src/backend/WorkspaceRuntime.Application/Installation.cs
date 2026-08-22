using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkspaceRuntime.Application;

public sealed record InstallationDefaults(
    string ProfileId,
    string Hostname,
    string AdminUser,
    string Locale,
    string KeyboardLayout,
    string TimeZone,
    string DiskLayout,
    bool EnableDiskEncryption,
    bool EnableSsh,
    bool InstallDotnet,
    bool InstallPowerShell,
    string LocalInferenceProvider,
    bool InstallModelWeights,
    bool AllowCloudInferenceFallback);

public sealed record InstallationRequest(
    string? TargetDisk = null,
    string? ProfileId = null,
    string? Hostname = null,
    string? AdminUser = null,
    string? Locale = null,
    string? KeyboardLayout = null,
    string? TimeZone = null,
    bool? EnableDiskEncryption = null,
    bool? EnableSsh = null,
    bool? InstallDotnet = null,
    bool? InstallPowerShell = null,
    string? LocalInferenceProvider = null,
    bool? InstallModelWeights = null,
    bool? AllowCloudInferenceFallback = null);

public sealed record InstallationDisk(
    string Path,
    long SizeBytes,
    bool IsReadOnly,
    bool IsRemovable,
    string Model);

public sealed record InstallationProbe(
    string Architecture,
    long MemoryMiB,
    bool IsUefi,
    IReadOnlyList<InstallationDisk> Disks);

public sealed record InstallationPlan(
    string PlanId,
    string ProfileId,
    string TargetDisk,
    string Hostname,
    string AdminUser,
    string Locale,
    string KeyboardLayout,
    string TimeZone,
    string DiskLayout,
    bool EnableDiskEncryption,
    bool EnableSsh,
    bool InstallDotnet,
    bool InstallPowerShell,
    string LocalInferenceProvider,
    bool InstallModelWeights,
    bool AllowCloudInferenceFallback,
    bool IsDestructive,
    bool RequiresHumanApproval,
    IReadOnlyList<string> Actions);

public sealed record InstallationValidationIssue(string Severity, string Code, string Message);

public sealed record InstallationValidation(
    bool IsValid,
    string PlanId,
    IReadOnlyList<InstallationValidationIssue> Issues);

public sealed record InstallationApproval(
    string PlanId,
    string ApprovedBy,
    DateTimeOffset ApprovedAt,
    bool AuthorizesDestructiveInstallation);

public sealed class InstallationPlanner
{
    private static readonly JsonSerializerOptions HashJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex HostnamePattern = new("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);
    private static readonly Regex UserPattern = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    public InstallationDefaults GetDefaults() => new(
        "personal-agent-workstation",
        "workspace-runtime",
        "workspace",
        "en_US.UTF-8",
        "us",
        "Etc/UTC",
        "use-entire-disk",
        EnableDiskEncryption: true,
        EnableSsh: false,
        InstallDotnet: true,
        InstallPowerShell: true,
        LocalInferenceProvider: "prism-bonsai-4b",
        InstallModelWeights: true,
        AllowCloudInferenceFallback: false);

    public InstallationPlan CreatePlan(InstallationRequest request, InstallationProbe probe)
    {
        var defaults = GetDefaults();
        var eligibleDisks = probe.Disks.Where(disk => !disk.IsReadOnly && !disk.IsRemovable).ToList();
        var targetDisk = request.TargetDisk ?? (eligibleDisks.Count == 1 ? eligibleDisks[0].Path : string.Empty);
        var installDotnet = request.InstallDotnet ?? defaults.InstallDotnet;
        var installPowerShell = request.InstallPowerShell ?? defaults.InstallPowerShell;
        var installWeights = request.InstallModelWeights ?? defaults.InstallModelWeights;
        var encryption = request.EnableDiskEncryption ?? defaults.EnableDiskEncryption;
        var actions = new List<string>
        {
            "storage.partition-gpt",
            "storage.format-system",
            "base.install",
            "identity.create-administrator",
            "runtime.install-agent-services"
        };

        if (encryption)
        {
            actions.Insert(1, "storage.encrypt-luks");
        }

        if (installDotnet)
        {
            actions.Add("runtime.install-dotnet");
        }

        if (installPowerShell)
        {
            actions.Add("runtime.install-powershell");
        }

        if (installWeights)
        {
            actions.Add("inference.install-model-weights");
        }

        var unsigned = new UnsignedInstallationPlan(
            request.ProfileId ?? defaults.ProfileId,
            targetDisk,
            request.Hostname ?? defaults.Hostname,
            request.AdminUser ?? defaults.AdminUser,
            request.Locale ?? defaults.Locale,
            request.KeyboardLayout ?? defaults.KeyboardLayout,
            request.TimeZone ?? defaults.TimeZone,
            defaults.DiskLayout,
            encryption,
            request.EnableSsh ?? defaults.EnableSsh,
            installDotnet,
            installPowerShell,
            request.LocalInferenceProvider ?? defaults.LocalInferenceProvider,
            installWeights,
            request.AllowCloudInferenceFallback ?? defaults.AllowCloudInferenceFallback,
            IsDestructive: true,
            RequiresHumanApproval: true,
            actions);

        return ToPlan(unsigned, ComputePlanId(unsigned));
    }

    public InstallationValidation Validate(InstallationPlan plan, InstallationProbe probe)
    {
        var issues = new List<InstallationValidationIssue>();
        var unsigned = ToUnsigned(plan);
        var expectedPlanId = ComputePlanId(unsigned);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedPlanId),
                Encoding.ASCII.GetBytes(plan.PlanId)))
        {
            issues.Add(Error("plan.hash-mismatch", "The plan content no longer matches its identifier."));
        }

        if (string.IsNullOrWhiteSpace(plan.TargetDisk))
        {
            issues.Add(Error("storage.target-required", "Select a target disk before installation."));
        }
        else
        {
            var target = probe.Disks.FirstOrDefault(disk => disk.Path == plan.TargetDisk);
            if (target is null)
            {
                issues.Add(Error("storage.target-not-found", $"Target disk '{plan.TargetDisk}' was not found."));
            }
            else if (target.IsReadOnly || target.IsRemovable)
            {
                issues.Add(Error("storage.target-ineligible", "The target disk is read-only or removable."));
            }
        }

        if (!HostnamePattern.IsMatch(plan.Hostname))
        {
            issues.Add(Error("identity.invalid-hostname", "Hostname must be a valid lowercase DNS label."));
        }

        if (!UserPattern.IsMatch(plan.AdminUser))
        {
            issues.Add(Error("identity.invalid-user", "Administrator name is not a valid Linux user name."));
        }

        if (probe.MemoryMiB < 4096)
        {
            issues.Add(Error("hardware.memory-minimum", "At least 4 GiB of memory is required."));
        }
        else if (plan.InstallModelWeights && probe.MemoryMiB < 8192)
        {
            issues.Add(Warning("inference.memory-recommended", "Local inference is more comfortable with at least 8 GiB of memory."));
        }

        if (plan.AllowCloudInferenceFallback)
        {
            issues.Add(Warning("inference.cloud-fallback", "Cloud inference may send prompts outside this machine and requires a separate runtime approval policy."));
        }

        return new InstallationValidation(
            issues.All(issue => issue.Severity != "error"),
            plan.PlanId,
            issues);
    }

    public InstallationApproval Approve(
        InstallationPlan plan,
        InstallationProbe probe,
        string approvalToken,
        string approvedBy,
        DateTimeOffset approvedAt)
    {
        var validation = Validate(plan, probe);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("An invalid installation plan cannot be approved.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(plan.PlanId),
                Encoding.ASCII.GetBytes(approvalToken)))
        {
            throw new UnauthorizedAccessException("Approval token does not match the installation plan.");
        }

        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new ArgumentException("The approving user is required.", nameof(approvedBy));
        }

        return new InstallationApproval(plan.PlanId, approvedBy, approvedAt, true);
    }

    private static InstallationValidationIssue Error(string code, string message) => new("error", code, message);
    private static InstallationValidationIssue Warning(string code, string message) => new("warning", code, message);

    private static string ComputePlanId(UnsignedInstallationPlan plan)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, HashJsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static InstallationPlan ToPlan(UnsignedInstallationPlan plan, string planId) => new(
        planId,
        plan.ProfileId,
        plan.TargetDisk,
        plan.Hostname,
        plan.AdminUser,
        plan.Locale,
        plan.KeyboardLayout,
        plan.TimeZone,
        plan.DiskLayout,
        plan.EnableDiskEncryption,
        plan.EnableSsh,
        plan.InstallDotnet,
        plan.InstallPowerShell,
        plan.LocalInferenceProvider,
        plan.InstallModelWeights,
        plan.AllowCloudInferenceFallback,
        plan.IsDestructive,
        plan.RequiresHumanApproval,
        plan.Actions);

    private static UnsignedInstallationPlan ToUnsigned(InstallationPlan plan) => new(
        plan.ProfileId,
        plan.TargetDisk,
        plan.Hostname,
        plan.AdminUser,
        plan.Locale,
        plan.KeyboardLayout,
        plan.TimeZone,
        plan.DiskLayout,
        plan.EnableDiskEncryption,
        plan.EnableSsh,
        plan.InstallDotnet,
        plan.InstallPowerShell,
        plan.LocalInferenceProvider,
        plan.InstallModelWeights,
        plan.AllowCloudInferenceFallback,
        plan.IsDestructive,
        plan.RequiresHumanApproval,
        plan.Actions);

    private sealed record UnsignedInstallationPlan(
        string ProfileId,
        string TargetDisk,
        string Hostname,
        string AdminUser,
        string Locale,
        string KeyboardLayout,
        string TimeZone,
        string DiskLayout,
        bool EnableDiskEncryption,
        bool EnableSsh,
        bool InstallDotnet,
        bool InstallPowerShell,
        string LocalInferenceProvider,
        bool InstallModelWeights,
        bool AllowCloudInferenceFallback,
        bool IsDestructive,
        bool RequiresHumanApproval,
        IReadOnlyList<string> Actions);
}
