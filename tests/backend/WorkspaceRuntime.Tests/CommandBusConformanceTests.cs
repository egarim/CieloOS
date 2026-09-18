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
        "/api/users",                  // add a teammate: control-plane identity creation, and now OWNER-only
        "/api/projects",               // start a project, and the four below: records ABOUT work, written
        "/api/projects/*/members",     // and read by people. Nothing in any workspace changes — a project
        "/api/projects/*/tasks",       // holds no file, no volume and no path — and an agent cannot reach
        "/api/projects/tasks/*/report",// any of these at all: the whole /api/projects prefix is human-only
                                       // except one read, /api/projects/mine, which returns the agent's own
                                       // owner's rows.
                                       //
                                       // The report route is the interesting one and it is deliberately NOT
                                       // on the bus: only the ASSIGNEE may write it, not even the lead, so
                                       // an agent writing it would be an agent speaking as its owner about
                                       // work it may not have done. The day that should be possible it
                                       // becomes a surface with a RequireApproval policy and an author
                                       // derived from the caller — so the lead can see "reported by maria's
                                       // agent" and never mistake it for Maria's word. That is a feature,
                                       // not a wider allowlist.
        "/api/organizations",          // create an organization: control-plane, owner-only, and it touches
                                       // no workspace at all — an organization owns no files, no volume and
                                       // no session, it is a name and a membership key. Not on the bus
                                       // because it is not an agent's to do in any form: an agent that could
                                       // create an organization could create a place to put people.
        "/api/users/*/organization",   // move a person between organizations: owner-only, and it changes
                                       // exactly one column. Their slug, home volume, token file, audit
                                       // history and spreadsheet are all keyed on the slug and none of them
                                       // move — which is why this is a control-plane edit and not a
                                       // workspace mutation. It is audited (user.organization) because it
                                       // changes who can see them and the slug it is keyed on does not
                                       // change, so nothing else would record that anything happened.
        "/api/messages/*",             // message another person: human-to-human, HumanOnly on every verb
                                       // including reads, and nothing in a workspace changes. It is not on
                                       // the bus because nothing here is an agent's to do — the agent
                                       // cannot read these or send them. The day it should be able to send
                                       // on its owner's behalf, this becomes a surface with a
                                       // RequireApproval policy ("may I send this?"), not a wider allowlist.
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
        "/api/invites/preview",        // public onboarding control plane: preview only records that
        "/api/invites/redeem",         // a link was seen; redeem sets the first password and session.
                                       // Neither changes anything in a user's workspace, and neither can
                                       // be emitted by an agent because both are Public entry points.
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
