using System.Text.Json;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Tests;

// The wizard's backing data. What matters here is the refusals: an engine that
// cannot state its confinement must not be installable, because a wizard that
// installs something ungoverned is worse than no wizard — it looks supervised.
public class EngineApiTests
{
    [Fact]
    public void Choosing_what_runs_on_this_machine_is_a_human_decision()
    {
        // Including the read. The list is a menu of other agent harnesses; an agent
        // has no use for one, and an agent that can enumerate them can pick.
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/engines", "GET"));
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/API/Engines/", "GET"));
    }

    // Not about engines, but found while mapping the ground for organizations and
    // it belongs next to the other AccessPolicy assertions.
    [Fact]
    public void An_agent_cannot_enumerate_the_people_on_this_machine()
    {
        // GET /api/users was AnyPrincipal, takes no HttpContext and projects
        // nothing, so any agent token could list every person here with their
        // email. Security.cs already claims the opposite rule for the message
        // directory — "an agent that cannot enumerate people cannot pick a new
        // target for anything" — which was true through one door and false
        // through another.
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/users", "GET"));
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required("/api/users", "POST"));
    }

    [Fact]
    public void An_unpinned_or_unverifiable_engine_cannot_be_installed()
    {
        foreach (var (engine, expected) in new (EngineManifest, string)[]
        {
            (Manifest(package: "openclaw"), "not pinned"),
            (Manifest(digest: null), "digest"),
            (Manifest(network: "bridge"), "network"),
            (Manifest(configure: new[] { "openclaw config set tools.profile minimal" }), "own tools"),
        })
        {
            var described = Json(EngineApi.Describe(engine));
            Assert.False(described.GetProperty("installable").GetBoolean());
            var blockers = string.Join(" ", described.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()));
            Assert.Contains(expected, blockers, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_fully_declared_engine_is_installable()
    {
        var described = Json(EngineApi.Describe(Manifest()));

        Assert.True(described.GetProperty("installable").GetBoolean());
        Assert.Empty(described.GetProperty("blockers").EnumerateArray());

        var confinement = described.GetProperty("confinement");
        Assert.True(confinement.GetProperty("sealedNetwork").GetBoolean());
        Assert.True(confinement.GetProperty("ownToolsDisabled").GetBoolean());
        Assert.True(confinement.GetProperty("versionPinned").GetBoolean());
        Assert.True(confinement.GetProperty("digestRecorded").GetBoolean());
    }

    [Fact]
    public void The_oversight_line_does_not_claim_to_see_what_it_cannot()
    {
        var oversight = Json(EngineApi.Describe(Manifest())).GetProperty("oversight").GetString()!;

        // The plan called for "bus coverage" — the share of an engine's actions that
        // were policy-checked. That number cannot be computed: the denominator is
        // exactly the part we cannot see, namely whatever the engine does with the
        // shell it still has inside its own container. A confident percentage built
        // on a missing denominator is worse than no percentage, so the wizard says
        // what is true instead.
        Assert.Contains("audit trail", oversight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not visible", oversight, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%", oversight, StringComparison.Ordinal);
        Assert.DoesNotContain("coverage", oversight, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_shipped_openclaw_engine_is_held_back_by_its_missing_digest()
    {
        var engine = new FileEngineCatalog(TestRepository.Root()).Find("openclaw")!;
        var described = Json(EngineApi.Describe(engine));

        // It is deliberately not installable yet, and for the right reason: nobody
        // has recorded what 2026.9.4 should hash to. The manifest says so out loud.
        Assert.False(described.GetProperty("installable").GetBoolean());
        Assert.Contains(
            described.GetProperty("blockers").EnumerateArray().Select(blocker => blocker.GetString()),
            blocker => blocker!.Contains("digest", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.Clone();

    private static EngineManifest Manifest(
        string package = "openclaw@2026.9.4",
        string? digest = "sha512-abc",
        string network = "none",
        string[]? configure = null) =>
        new(1, "engine", "test", "Test Engine", "1.0",
            new EngineInstall("npm", package, digest, configure ?? new[] { "x config set tools.allow '[\"cielo__*\"]'" }),
            new EngineInvoke("x", new[] { "-m", "{goal}" }, null),
            network);

    // Sibling of ShippedToNewInstallationsTests in RecorderSurfaceTests.cs: a fix
    // that only exists on the machine it was made on is not a fix.
    //
    // FileEngineCatalog reads <bundle>/engines. The release script staged surfaces
    // and not engines, so every installed machine would have shown an empty engine
    // list and looked broken rather than failing — the #49 shape exactly, where the
    // code was right and had never once run anywhere real.
    [Fact]
    public void Engine_manifests_reach_a_new_installation()
    {
        var release = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "distro", "scripts", "build-release.sh"));

        Assert.Contains("/engines/", release, StringComparison.Ordinal);
        Assert.Contains("*.engine.json", release, StringComparison.Ordinal);

        // And there is something to ship.
        Assert.NotEmpty(new FileEngineCatalog(TestRepository.Root()).Engines);
    }
}
