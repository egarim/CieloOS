namespace WorkspaceRuntime.Application;

// The desktop counterpart to IConsoleBackend: observe a desktop session as a
// screenshot, and act on it with pointer/keyboard input. Observation is a gated
// READ (the /screenshot endpoint, like /console); the state-changing input rides
// the `desktop` surface, so every click and keystroke is manifest-checked,
// ownership-scoped and audited on the one bus — the governed form of the
// screenshot->model->xdotool spike.
public sealed record DesktopShot(string SessionId, byte[] Png, int Width, int Height, bool Ok, string? Error = null);

public sealed record DesktopActionResult(bool Ok, string Detail);

public interface IDesktopBackend
{
    Task<DesktopShot> ScreenshotAsync(string sessionId, CancellationToken cancellationToken);
    Task<DesktopActionResult> ClickAsync(string sessionId, int x, int y, int button, int repeat, CancellationToken cancellationToken);
    Task<DesktopActionResult> TypeTextAsync(string sessionId, string text, CancellationToken cancellationToken);
    Task<DesktopActionResult> KeyAsync(string sessionId, string keysym, CancellationToken cancellationToken);
}
