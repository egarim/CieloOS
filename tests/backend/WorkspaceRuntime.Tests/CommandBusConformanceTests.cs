using System.Text.RegularExpressions;

namespace WorkspaceRuntime.Tests;

// Design law 1 (docs/ai-native-ui.md): every mutation goes through the command
// bus. This test fails if any frontend source posts to an endpoint outside the
// allowlist, the same drift-prevention pattern as RenameSafetyTests.
public class CommandBusConformanceTests
{
    private static readonly string[] AllowedPostPrefixes =
    {
        "/api/surfaces/",        // surface command dispatch (the bus)
        "/api/approvals/",       // human consent verbs (approve/reject, human principal only)
        "/api/inference/chat"    // local model conversation; advisory, mutates nothing
    };

    // Exact endpoints (normalized, with ${...} -> *) that are bus-respecting even
    // though they aren't literal /api/surfaces calls.
    private static readonly string[] AllowedPostPaths =
    {
        "/api/sessions/*/agent-run",   // runs the console loop; every keystroke it makes is a policy-checked console.type
        "/api/sessions/*/desktop-run"  // runs the desktop loop; every click/keystroke it makes is a policy-checked desktop.*
    };

    [Fact]
    public void Frontend_mutations_go_through_the_command_bus()
    {
        var frontendSource = Path.Combine(TestRepository.Root(), "src", "frontend", "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(frontendSource, "*.ts*", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);

            // Match api("<path>", { ... method: "POST" ... }) and fetch("<path>", { ... method: "POST" ... }),
            // including template literals with interpolation.
            foreach (Match match in Regex.Matches(content, "(?:api|fetch)\\s*(?:<[^>]*>)?\\s*\\(\\s*[`\"']([^`\"']+)[`\"']\\s*,((?:[^()]|\\([^()]*\\))*)\\)", RegexOptions.Singleline))
            {
                var path = match.Groups[1].Value;
                var options = match.Groups[2].Value;
                if (!options.Contains("POST", StringComparison.Ordinal))
                {
                    continue;
                }

                var normalized = Regex.Replace(path, "\\$\\{[^}]*\\}", "*");
                if (!AllowedPostPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal))
                    && !AllowedPostPaths.Contains(normalized, StringComparer.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: POST {path}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Frontend mutations must go through the surface command bus. Offenders:\n" + string.Join("\n", offenders));
    }
}
