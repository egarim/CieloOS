using System.Globalization;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// Routes the `recorder` surface to the recorder backend, so starting and stopping
// a capture rides the same manifest-checked, ownership-gated, audited bus as every
// other action. Reading the current state is a gated read on
// /api/sessions/{id}/recording; this executor owns the half that changes something.
public sealed class RecorderSurfaceExecutor : ISurfaceExecutor
{
    private readonly IRecorderBackend recorder;

    public RecorderSurfaceExecutor(IRecorderBackend recorder)
    {
        this.recorder = recorder;
    }

    public string SurfaceId => "recorder";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var id = Required(request, "id");
        switch (request.Operation)
        {
            case "start":
            {
                var seconds = RecordingLimits.DefaultSeconds;
                if (request.Arguments.TryGetValue("seconds", out var raw)
                    && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var requested))
                {
                    if (requested < 1 || requested > RecordingLimits.MaxSeconds)
                    {
                        return new ToolExecutionResult(false,
                            $"A recording must be between 1 and {RecordingLimits.MaxSeconds} seconds.", null);
                    }
                    seconds = requested;
                }

                var name = request.Arguments.GetValueOrDefault("name", "session");
                // Checked here as well as by the manifest pattern: the name becomes
                // part of a path inside the owner's home, and a guard only one
                // caller honours is not a guard.
                if (!RecordingLimits.IsUsableName(name))
                {
                    return new ToolExecutionResult(false, $"'{name}' is not a usable recording name.", null);
                }

                var result = await recorder.StartRecordingAsync(id, seconds, name, cancellationToken);
                return new ToolExecutionResult(result.Ok, Describe(result), null);
            }

            case "stop":
            {
                var result = await recorder.StopRecordingAsync(id, cancellationToken);
                return new ToolExecutionResult(result.Ok, Describe(result), null);
            }

            default:
                return new ToolExecutionResult(false, $"Recorder executor rejected unknown operation '{request.Operation}'.", null);
        }
    }

    public Task<EffectPreview> PreviewAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var id = request.Arguments.GetValueOrDefault("id");
        var summary = request.Operation switch
        {
            "start" => $"Would start recording the desktop of session '{id}' to a file in its owner's home, "
                       + $"for up to {request.Arguments.GetValueOrDefault("seconds", RecordingLimits.DefaultSeconds.ToString(CultureInfo.InvariantCulture))} seconds, "
                       + "with an on-screen recording marker.",
            "stop" => $"Would stop the recording running on session '{id}' and close the file.",
            _ => $"Unknown recorder operation '{request.Operation}'."
        };
        return Task.FromResult(new EffectPreview(true, summary, Array.Empty<CellChange>()));
    }

    // What ends up in the audit line. A recording that is missing its on-screen
    // marker still runs — refusing to record because a label failed would be the
    // wrong trade — but it says so, every time, where someone will read it.
    private static string Describe(RecorderResult result)
    {
        var detail = result.Detail;
        if (result.Recording is { } recording)
        {
            if (!recording.Indicator)
            {
                detail += " WARNING: the on-screen recording marker could not be drawn, so anyone taking a seat at this session will not see that it is being captured.";
            }
            if (recording.Truncated)
            {
                detail += $" The file is kept at {recording.Path} in case the partial footage is useful.";
            }
        }
        return detail;
    }

    private static string Required(ToolRequest request, string key) =>
        request.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument '{key}'.");
}
