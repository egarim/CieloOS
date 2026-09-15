using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

// What the admin area needs to offer "add an engine", and what it must show
// before anyone clicks.
//
// The wizard is the last piece of this work rather than the first, for one
// reason: a wizard that installs an engine we cannot govern ships something worse
// than no wizard, because it looks supervised. Whoever used it would reasonably
// assume the approvals and the audit trail cover what it just installed.
//
// So every engine here reports its confinement, and an engine that cannot state
// it is not installable.
public static class EngineApi
{
    public static void Map(WebApplication app)
    {
        // Human-only by AccessPolicy: choosing what runs on this machine is not an
        // agent's decision to make about itself.
        app.MapGet("/api/engines", (IEngineCatalog catalog, ISurfaceRegistry surfaces) =>
            Results.Ok(new
            {
                // What ANY engine would be given. Shown next to the list because the
                // question a person actually has is "what will it be able to do",
                // and the answer is the same for all of them.
                tools = surfaces.Surfaces
                    .SelectMany(surface => surface.Commands
                        .Where(command => command.Value.ExposedToAgent && !command.Value.RequiresHuman)
                        .Select(command => new
                        {
                            name = $"{surface.Id}.{command.Key}",
                            displayName = command.Value.DisplayName,
                            reversible = command.Value.Reversible,
                            needsApproval = string.Equals(command.Value.Policy.DefaultDecision, "RequireApproval", StringComparison.OrdinalIgnoreCase)
                        }))
                    .OrderBy(tool => tool.name, StringComparer.Ordinal),

                engines = catalog.Engines.Select(Describe)
            }));
    }

    public static object Describe(EngineManifest engine)
    {
        var blockers = Blockers(engine).ToList();
        return new
        {
            engine.Id,
            engine.DisplayName,
            engine.Version,
            engine.Install.Package,

            // Confinement, as DECLARED. Not as measured — see below.
            confinement = new
            {
                network = engine.Network,
                sealedNetwork = string.Equals(engine.Network, "none", StringComparison.OrdinalIgnoreCase),
                ownToolsDisabled = (engine.Install.Configure ?? Array.Empty<string>())
                    .Any(step => step.Contains("tools.allow", StringComparison.OrdinalIgnoreCase)),
                versionPinned = engine.Install.Package.Contains('@', StringComparison.Ordinal),
                digestRecorded = !string.IsNullOrWhiteSpace(engine.Install.Digest)
            },

            // The honest sentence to put under the confinement panel.
            //
            // The plan for this said "bus coverage": the share of an engine's
            // actions that were policy-checked. That number cannot be computed and
            // saying it could was wrong. Everything an engine does through MCP is
            // audited; anything it does with the shell it still has inside its own
            // container is not, and the denominator is exactly the part we cannot
            // see. A percentage built on a denominator we do not have would be a
            // confident number that means nothing, which is worse than no number.
            //
            // What IS true and worth showing: the container bounds the damage
            // whatever the engine does, and the audit is complete for everything
            // that came through the bus.
            oversight = "Everything this engine does through CieloOS tools is policy-checked and on the audit "
                + "trail. Anything it does with a shell inside its own container is not visible here — the "
                + "container limits what that could reach, it does not record it.",

            installable = blockers.Count == 0,
            blockers,
            engine.Notes
        };
    }

    // Refusing to install is the point of the digest check. "Install the latest
    // OpenClaw" from a panel is a supply-chain decision made by whoever happened to
    // click, and the person clicking is usually not the person who would notice.
    private static IEnumerable<string> Blockers(EngineManifest engine)
    {
        if (!engine.Install.Package.Contains('@', StringComparison.Ordinal))
        {
            yield return "The package is not pinned to a version.";
        }

        if (string.IsNullOrWhiteSpace(engine.Install.Digest))
        {
            yield return "No digest has been recorded for this version, so what would be installed cannot be verified.";
        }

        if (!string.Equals(engine.Network, "none", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"This engine asks for network '{engine.Network}' rather than none, which would let it reach a model provider directly.";
        }

        if (!(engine.Install.Configure ?? Array.Empty<string>())
            .Any(step => step.Contains("tools.allow", StringComparison.OrdinalIgnoreCase)))
        {
            yield return "Nothing in its setup removes the engine's own tools, so it would act outside the policy bus.";
        }
    }
}
