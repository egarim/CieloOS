namespace WorkspaceRuntime.Application;

// Recording a session's desktop, so the agent can produce footage of the work it
// just did and edit it into a tutorial afterwards.
//
// The recording is a FILE IN THE OWNER'S HOME, nothing more: it is never uploaded,
// never sent to a model, and it lands in the same per-owner volume the download
// endpoint already serves. That matters because a recording is thousands of
// screenshots, and this system has a deliberate gate (ISessionVisionConsent) whose
// whole purpose is that screen content does not leave the machine by default.
// Producing a file on the box does not cross that line; anything that later sends
// one somewhere does, and must ask.
public sealed record Recording(
    string Id,
    string Path,
    string StartedAt,
    double StartedAtUnix,
    int RequestedSeconds,
    int Width,
    int Height,
    int Fps,
    // Whether the on-screen recording marker was actually drawn. A person may sit
    // down at a session that is already recording — that is what session.inhabit
    // is for — so the indicator is part of the feature, and its absence is
    // reported rather than swallowed.
    bool Indicator,
    long Bytes = 0,
    double Seconds = 0,
    double ExpectedSeconds = 0,
    bool Truncated = false,
    double ElapsedSeconds = 0);

public sealed record RecorderStatus(string SessionId, bool Running, Recording? Current, bool Ok, string? Error = null);

public sealed record RecorderResult(bool Ok, string Detail, Recording? Recording = null);

public interface IRecorderBackend
{
    Task<RecorderStatus> RecordingStatusAsync(string sessionId, CancellationToken cancellationToken);
    Task<RecorderResult> StartRecordingAsync(string sessionId, int seconds, string name, CancellationToken cancellationToken);
    Task<RecorderResult> StopRecordingAsync(string sessionId, CancellationToken cancellationToken);
}

public static class RecordingLimits
{
    // A hard ceiling enforced on both sides: the helper passes it to ffmpeg's -t,
    // so a runtime that dies without ever calling stop cannot leave a process
    // quietly filling the owner's home volume.
    public const int MaxSeconds = 1800;
    public const int DefaultSeconds = 300;

    public static bool IsUsableName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 64
        && char.IsLetterOrDigit(name[0])
        && name.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
}
