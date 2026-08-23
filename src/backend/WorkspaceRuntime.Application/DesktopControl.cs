namespace WorkspaceRuntime.Application;

// The desktop counterpart to IConsoleBackend: observe a desktop session as a
// screenshot, and act on it with pointer/keyboard input. Observation is a gated
// READ (the /screenshot endpoint, like /console); the state-changing input rides
// the `desktop` surface, so every click and keystroke is manifest-checked,
// ownership-scoped and audited on the one bus — the governed form of the
// screenshot->model->xdotool spike.
public sealed record DesktopShot(string SessionId, byte[] Png, int Width, int Height, bool Ok, string? Error = null);

public sealed record DesktopActionResult(bool Ok, string Detail);

// One actionable UI element from the accessibility tree, with its EXACT on-screen
// box. The center is what you click — deterministic grounding, no pixel guessing.
public sealed record DesktopElement(int Id, string Role, string Name, int X, int Y, int W, int H)
{
    public int CenterX => X + (W / 2);
    public int CenterY => Y + (H / 2);
}

public sealed record DesktopElements(string SessionId, IReadOnlyList<DesktopElement> Elements, bool Ok, string? Error = null);

// The next thing the agent wants to do on the desktop. Grounded on an element id
// (preferred — exact center, no guessing) or a raw pixel point (vision fallback
// for what the tree does not expose), or a keystroke, or done.
public sealed record DesktopAgentAction(
    bool Done,
    string Kind,        // "click" | "double_click" | "type" | "key" | "done"
    int? ElementId,     // grounded target: an id from the observed element list
    int? X, int? Y,     // vision fallback: raw pixel target
    string? Text,       // for "type"
    string? Keysym,     // for "key"
    string? Note,
    bool Aborted = false); // the brain could not proceed (no vision, model error) — NOT goal-complete

public sealed record DesktopLoopStep(
    int Step, int ElementCount, string Kind, int? ElementId, int? X, int? Y,
    string? Text, string? Keysym, bool Done, string? Note, string Decision, string Reason);

public sealed record DesktopLoopResult(
    string SessionId, string Goal, bool Completed, string StopReason, IReadOnlyList<DesktopLoopStep> Steps);

// The pluggable desktop brain: it sees the goal, the AT-SPI element list, and a
// screenshot, and decides the single next action. A vision-capable model backs it
// (gpt-4.1-mini); the loop, policy bus, and audit trail do not change behind it.
public interface IDesktopAgentBrain
{
    Task<DesktopAgentAction> DecideAsync(
        string goal, IReadOnlyList<DesktopElement> elements, byte[]? screenshotPng,
        int screenWidth, int screenHeight, IReadOnlyList<string> history, int step,
        CancellationToken cancellationToken);
}

public interface IDesktopBackend
{
    Task<DesktopShot> ScreenshotAsync(string sessionId, CancellationToken cancellationToken);
    // AT-SPI-first perception: the actionable elements with exact boxes. Primary
    // grounding path; the screenshot + a vision model is the fallback for surfaces
    // the accessibility tree does not expose (canvases, some icons).
    Task<DesktopElements> ElementsAsync(string sessionId, CancellationToken cancellationToken);
    Task<DesktopActionResult> ClickAsync(string sessionId, int x, int y, int button, int repeat, CancellationToken cancellationToken);
    Task<DesktopActionResult> TypeTextAsync(string sessionId, string text, CancellationToken cancellationToken);
    Task<DesktopActionResult> KeyAsync(string sessionId, string keysym, CancellationToken cancellationToken);
}
