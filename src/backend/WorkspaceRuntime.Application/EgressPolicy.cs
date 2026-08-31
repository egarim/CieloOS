namespace WorkspaceRuntime.Application;

// Resolves a desk profile to its egress allowlist from configuration. This is the
// "which hosts may this desk's browser reach" source for the #17 proxy and the
// navigation check. Absent or empty means NO restriction, so a desk that has
// never been given a list keeps browsing exactly as it does today; the instant a
// list is present, the egress decision is enforced.
public static class EgressPolicy
{
    public static IReadOnlySet<string> AllowedHosts(DeskProfile? deskProfile, Func<string, string?> config)
    {
        var key = deskProfile?.Id ?? DeskProfiles.DefaultId;
        var value = config($"Egress:Allowlists:{key}");
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }
}
