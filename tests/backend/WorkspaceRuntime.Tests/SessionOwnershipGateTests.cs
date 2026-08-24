using System.Text.Json;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The bus gates session-targeting commands on the SESSION's owner. That gate used
// to be a hard-coded list — `console or desktop or session-input` — and when the
// `browser` surface was added nobody extended it. The result was reachable: a
// second user was refused the gated READ endpoint ("may not observe a session
// owned by 'joche'") and then drove that same browser through the command bus,
// clicking inside it, `executed: true`.
//
// The list is now a manifest declaration, so a new surface that forgets it fails
// here instead of shipping a hole. `recorder` is next and would have walked into
// exactly the same one.
public class SessionOwnershipGateTests
{
    [Fact]
    public void Every_surface_that_names_a_session_declares_that_it_does()
    {
        var offenders = new List<string>();

        foreach (var surface in TestRepository.Surfaces().Surfaces)
        {
            // `session` itself is the deliberate exception: create/destroy/inhabit
            // are gated by their own block in AgentRuntime, which resolves the
            // target differently per operation and says so in its refusals.
            if (surface.Id == "session" || surface.TargetsSession)
            {
                continue;
            }

            foreach (var (name, command) in surface.Commands)
            {
                if (!command.Input.TryGetProperty("properties", out var properties)
                    || !properties.TryGetProperty("id", out var id))
                {
                    continue;
                }

                // A session id, as every session-targeting manifest spells it.
                var pattern = id.TryGetProperty("pattern", out var p) ? p.GetString() : null;
                if (pattern == "^[a-z0-9-]{1,64}$")
                {
                    offenders.Add($"{surface.Id}.{name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These commands take a session id but their surface does not declare \"targetsSession\": true, "
            + "so the bus will not check who owns that session:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("console")]
    [InlineData("desktop")]
    [InlineData("session-input")]
    [InlineData("browser")]
    public void The_surfaces_that_drive_a_session_are_declared(string id)
    {
        // Named explicitly as well as derived above, so deleting the flag from a
        // manifest fails twice and reads unambiguously.
        Assert.True(TestRepository.Surfaces().Find(id)!.TargetsSession);
    }

    [Fact]
    public async Task A_user_cannot_drive_a_session_owned_by_someone_else()
    {
        // The regression test for the breach itself, at the choke point every entry
        // path shares. Yulia owns no part of Joche's session and may not act on it,
        // whichever surface she reaches for.
        var store = new InMemoryRuntimeStore();
        var joche = store.Users.First(user => user.Slug == "joche");
        var yulia = store.Users.First(user => user.Slug == "yulia");
        var yuliasAgent = store.Agents.First(agent => agent.OwnerUserId == yulia.Id);

        var runtime = new AgentRuntime(
            store,
            TestRepository.PolicyEngine(),
            new NullExecutor(),
            TestRepository.Surfaces(),
            new OneSession("joche-abc123", joche.Slug));

        foreach (var (surface, operation, arguments) in new[]
        {
            ("browser", "back", new Dictionary<string, string> { ["id"] = "joche-abc123" }),
            ("browser", "click", new Dictionary<string, string> { ["id"] = "joche-abc123", ["element"] = "e13-303199" }),
            ("desktop", "click", new Dictionary<string, string> { ["id"] = "joche-abc123", ["x"] = "10", ["y"] = "10" }),
            ("console", "type", new Dictionary<string, string> { ["id"] = "joche-abc123", ["text"] = "whoami" }),
        })
        {
            var result = await runtime.SubmitAsync(
                new SubmitToolRequestDto(yulia.Id, yuliasAgent.Id, surface, operation, arguments),
                new RuntimePrincipal(PrincipalKind.Human, yulia.Id, yulia.Slug, yulia.DisplayName),
                default);

            Assert.Equal(PolicyDecision.Deny, result.Decision);
            Assert.Contains("joche", result.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_owner_is_still_allowed_through()
    {
        // The gate has to refuse the stranger without refusing the owner, or the
        // fix is just an outage.
        var store = new InMemoryRuntimeStore();
        var joche = store.Users.First(user => user.Slug == "joche");
        var jochesAgent = store.Agents.First(agent => agent.OwnerUserId == joche.Id);

        var runtime = new AgentRuntime(
            store,
            TestRepository.PolicyEngine(),
            new NullExecutor(),
            TestRepository.Surfaces(),
            new OneSession("joche-abc123", joche.Slug));

        var result = await runtime.SubmitAsync(
            new SubmitToolRequestDto(joche.Id, jochesAgent.Id, "browser", "back",
                new Dictionary<string, string> { ["id"] = "joche-abc123" }),
            new RuntimePrincipal(PrincipalKind.Human, joche.Id, joche.Slug, joche.DisplayName),
            default);

        Assert.NotEqual(PolicyDecision.Deny, result.Decision);
    }

    private sealed class OneSession : ISessionBackend
    {
        private readonly DesktopSession session;

        public OneSession(string id, string owner) =>
            session = new DesktopSession(id, owner, "human-desktop", "running", 3000, "desktop");

        public Task<IReadOnlyList<DesktopSession>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DesktopSession>>(new[] { session });

        public Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class NullExecutor : ISandboxedToolExecutor
    {
        public Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolExecutionResult(true, "executed", null));
    }
}
