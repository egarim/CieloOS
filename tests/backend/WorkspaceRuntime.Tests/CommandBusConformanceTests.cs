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
        "/api/inference/chat",   // local model conversation; advisory, mutates nothing
        "/api/setup/",           // first-run claim: control-plane, loopback-gated, not agent-emittable
        "/api/models"            // models surface: provider config, human-only, not a surface mutation
    };

    // Exact endpoints (normalized, with ${...} -> *) that are bus-respecting even
    // though they aren't literal /api/surfaces calls.
    private static readonly string[] AllowedPostPaths =
    {
        "/api/sessions/*/agent-run",   // runs the console loop; every keystroke it makes is a policy-checked console.type
        "/api/sessions/*/desktop-run", // runs the desktop loop; every click/keystroke it makes is a policy-checked desktop.*
        "/api/examples/*/run",         // runs a scripted example; every step is an ordinary policy-checked command,
                                       // which is the whole point — a demo that took a private path would prove nothing
        "/v1/agent/chat/completions",  // the same console loop as /api/sessions/*/agent-run, entered from a
                                       // chat message instead of a goal field. Every action it takes on the
                                       // machine is an ordinary policy-checked console.type, so the bus sees
                                       // all of it; this endpoint only decides what to ask the agent for.
        "/api/users",                  // add a teammate: control-plane identity creation, human-only
        "/api/threads",                // start a conversation: the person's own record of what they
                                       // delegated. Nothing in a workspace changes, and authorship is
                                       // taken from the caller rather than the body, so this cannot be
                                       // used to forge a message from someone else.
        "/api/usage/limits",           // set a model budget: an owner's ceiling on spend, human-only
        "/api/auth/login",             // sign in: the control plane's own front door
        "/api/auth/logout",
        "/api/auth/logout-all",
        "/api/auth/password",
        "/api/auth/language",          // the language YOU read in: your own preference on the
                                       // control plane, HumanOnly like the rest of /api/auth/*,
                                       // and it mutates nothing in any workspace. It reached this
                                       // list late only because nothing shipped had ever called
                                       // it — main.tsx has no i18n at all — so the portal is the
                                       // first code to exercise an endpoint that has existed for
                                       // a while. The guard was right to stop and ask.
        "/api/keys",                   // mint a revocable credential, human-only
        "/api/desk-profiles/*/build"   // build a desk image: provisioning this machine, human-only,
                                       // not a surface mutation (nothing in the workspace changes)
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
