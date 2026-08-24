namespace WorkspaceRuntime.Application;

// What a desk is FOR, chosen when the user is created. A .NET developer, an
// office worker and a marketer need different machines, and asking each of them
// to install their own toolchain defeats the point of an OS that arrives ready
// to work (issue #15).
//
// Deliberately not called a "profile" alone: `profile` already names the session
// kind (agent-console, human-desktop, …) in session.surface.json, and two
// different "profiles" in one API would be a trap.
//
// A desk profile decides three things, and they are separable on purpose:
//   Image       — the toolchain the desktop session runs.
//   AgentTools  — what the desk's agent may do at all; a developer's agent that
//                 can run `dotnet` is a different policy subject from an office
//                 agent that cannot, and the policy engine stays the authority.
//   Seeds       — what the home starts with (a Projects folder, templates).
public sealed record DeskProfile(
    string Id,
    string Label,
    string Description,
    string Image,
    IReadOnlySet<string> AgentTools)
{
    // A profile whose image is not the shared desktop has to be built before a
    // session of that kind can start, and building it is not instant.
    public bool NeedsOwnImage => !string.Equals(Image, DeskProfiles.SharedDesktopImage, StringComparison.Ordinal);
}

public static class DeskProfiles
{
    public const string SharedDesktopImage = "localhost/lunos-desktop:latest";

    // The default is the desk everyone has had until now, under a name. Keeping
    // it as `office` rather than inventing a new default means an existing
    // machine's behaviour is unchanged and nothing new has to be built.
    public const string DefaultId = "office";

    private static readonly DeskProfile Office = new(
        "office",
        "Office",
        "Documents and spreadsheets: ONLYOFFICE, a file manager and a browser. The agent writes .xlsx/.docx/.pptx and can drive the desktop.",
        SharedDesktopImage,
        OwnerDefaults.AgentTools);

    private static readonly DeskProfile Dotnet = new(
        "dotnet",
        ".NET developer",
        "The office desk plus a .NET SDK, Uno Platform templates and VS Code with the Uno extension. `dotnet new unoapp` works on a fresh desk.",
        "localhost/cielo-desk-dotnet:latest",
        OwnerDefaults.AgentTools);

    private static readonly DeskProfile Marketing = new(
        "marketing",
        "Marketing",
        "The office desk plus the visual tools: GIMP, Inkscape and a screenshot tool, with the agent's private web search.",
        "localhost/cielo-desk-marketing:latest",
        OwnerDefaults.AgentTools);

    public static IReadOnlyList<DeskProfile> All { get; } = new[] { Office, Dotnet, Marketing };

    public static DeskProfile Default => Office;

    // Unknown ids resolve to the default rather than throwing: a desk created by
    // a newer panel, or a profile later removed, must still open a session.
    public static DeskProfile Resolve(string? id) =>
        All.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public static bool IsKnown(string? id) =>
        All.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));
}
