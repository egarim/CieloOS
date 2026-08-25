using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public class RecorderSurfaceTests
{
    [Fact]
    public void Recording_someone_elses_session_is_impossible_by_declaration()
    {
        // The surface takes a session id, so it MUST declare targetsSession or the
        // bus will not check who owns that session. `browser` shipped without this
        // and one user could drive another's browser; a recorder without it would
        // let one user film another's desktop, which is worse.
        Assert.True(TestRepository.Surfaces().Find("recorder")!.TargetsSession);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1801")]
    [InlineData("99999")]
    public async Task A_recording_has_a_ceiling(string seconds)
    {
        // The ceiling is not politeness. ffmpeg gets it as -t, so a runtime that
        // dies without ever calling stop still cannot leave a process filling the
        // owner's home volume.
        var backend = new RecordingRecorder();
        var executor = new RecorderSurfaceExecutor(backend);

        var result = await executor.ExecuteAsync(
            Request("start", ("id", "desk-1"), ("seconds", seconds)), default);

        Assert.False(result.Executed);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_recording_without_a_length_gets_the_default()
    {
        var backend = new RecordingRecorder();
        var executor = new RecorderSurfaceExecutor(backend);

        var result = await executor.ExecuteAsync(Request("start", ("id", "desk-1")), default);

        Assert.True(result.Executed);
        Assert.Equal(new[] { $"start:{RecordingLimits.DefaultSeconds}:session" }, backend.Calls);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("with space")]
    [InlineData("-leading-dash")]
    [InlineData(".hidden")]
    [InlineData("")]
    public async Task A_recording_name_cannot_wander_out_of_the_home(string name)
    {
        // The name becomes part of a path inside the owner's home.
        var backend = new RecordingRecorder();
        var executor = new RecorderSurfaceExecutor(backend);

        var result = await executor.ExecuteAsync(
            Request("start", ("id", "desk-1"), ("name", name)), default);

        Assert.False(result.Executed);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_missing_recording_marker_is_reported_every_time()
    {
        // A person can sit down at a session that is already recording. If the
        // marker could not be drawn they have no way to know, so the recording
        // still runs — refusing to record because a label failed is the wrong
        // trade — but it says so where someone will read it.
        var executor = new RecorderSurfaceExecutor(new RecordingRecorder { Marker = false });

        var result = await executor.ExecuteAsync(Request("start", ("id", "desk-1")), default);

        Assert.True(result.Executed);
        Assert.Contains("marker could not be drawn", result.Message);
    }

    [Fact]
    public async Task A_short_recording_is_a_failure_not_a_success()
    {
        // ffmpeg exits 0 after the X display is resized out from under it —
        // measured on real hardware: 2.3s written of a requested 6.8s, exit 0. The
        // encoded duration is the only honest witness, and a tutorial built from
        // footage that quietly stopped half way is worse than no tutorial, because
        // nobody checks.
        var executor = new RecorderSurfaceExecutor(new RecordingRecorder { Truncate = true });

        var result = await executor.ExecuteAsync(Request("stop", ("id", "desk-1")), default);

        Assert.False(result.Executed);
        Assert.Contains("stopped early", result.Message);
        // The partial file is still named, because sometimes it is worth having.
        Assert.Contains("/config/recordings/", result.Message);
    }

    [Fact]
    public async Task An_unknown_operation_is_refused()
    {
        var executor = new RecorderSurfaceExecutor(new RecordingRecorder());
        var result = await executor.ExecuteAsync(Request("upload", ("id", "desk-1")), default);
        Assert.False(result.Executed);
    }

    [Fact]
    public void The_manifest_offers_no_way_to_send_a_recording_anywhere()
    {
        // The negative assertion that keeps the privacy story true. A recording is
        // thousands of screenshots; this system has a deliberate gate whose whole
        // purpose is that screen content does not leave the machine by default.
        // Producing a file in the owner's home does not cross that line. A command
        // here called upload/publish/share would, silently.
        var recorder = TestRepository.Surfaces().Find("recorder")!;

        Assert.Equal(new[] { "start", "stop" }, recorder.Commands.Keys.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_new_desk_can_record()
    {
        Assert.Contains("recorder", OwnerDefaults.AgentTools);
    }

    [Fact]
    public void The_helper_never_trusts_ffmpegs_exit_code()
    {
        // A drift lint: the truncation check is the whole reason this surface can
        // be believed, and nothing else in the build would notice its removal.
        var helper = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "distro", "images", "desktop", "lunos-recorder"));

        Assert.Contains("ffprobe", helper);
        Assert.Contains("truncated", helper);
        // SIGINT, not SIGKILL: ffmpeg must write the moov atom or the file is
        // unplayable, which would turn every stop into a lost recording.
        Assert.Contains("SIGINT", helper);
    }

    [Fact]
    public void Two_recordings_cannot_race_each_other()
    {
        // Found in review. Two `start` requests could both read "not recording"
        // before either wrote, and both launch ffmpeg; the second write hid the
        // first PID, so `stop` killed one and the other kept running invisibly
        // until its own time limit. And two recordings named the same in the same
        // second derived the same path, where ffmpeg's -y silently overwrote a
        // finished capture.
        var helper = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "distro", "images", "desktop", "lunos-recorder"));

        Assert.Contains("fcntl.flock", helper);
        Assert.Contains("os.O_EXCL", helper);
    }

    [Fact]
    public void The_recording_marker_reports_what_actually_happened()
    {
        // Also found in review, and squarely my own bug: the check read
        // `os.path.exists(...) or True`, which is always True, so a marker that
        // never reached the display still reported success — defeating the privacy
        // warning that is the entire reason the field exists.
        var helper = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "distro", "images", "desktop", "lunos-recorder"));

        Assert.DoesNotContain("or True", helper);
        Assert.Contains("marker.poll() is None", helper);
    }

    private static ToolRequest Request(string operation, params (string Key, string Value)[] arguments) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "recorder", operation,
            arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            DateTimeOffset.UtcNow);

    private sealed class RecordingRecorder : IRecorderBackend
    {
        public List<string> Calls { get; } = new();
        public bool Marker { get; init; } = true;
        public bool Truncate { get; init; }

        private Recording Sample(bool truncated) => new(
            "session-20260825-120000", "/config/recordings/session-20260825-120000.mp4",
            "2026-08-25T12:00:00Z", 1787000000, 300, 1920, 1080, 30, Marker,
            Bytes: 1024, Seconds: truncated ? 2.3 : 300, ExpectedSeconds: truncated ? 6.8 : 300,
            Truncated: truncated);

        public Task<RecorderStatus> RecordingStatusAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add("status");
            return Task.FromResult(new RecorderStatus(sessionId, true, Sample(false), true));
        }

        public Task<RecorderResult> StartRecordingAsync(string sessionId, int seconds, string name, CancellationToken cancellationToken)
        {
            Calls.Add($"start:{seconds}:{name}");
            return Task.FromResult(new RecorderResult(true, $"Recording for up to {seconds}s.", Sample(false)));
        }

        public Task<RecorderResult> StopRecordingAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            return Truncate
                ? Task.FromResult(new RecorderResult(false,
                    "The recording stopped early: 2.3s of 6.8s. The X display was almost certainly resized while recording.",
                    Sample(true)))
                : Task.FromResult(new RecorderResult(true, "Recorded 300.0s.", Sample(false)));
        }
    }
}
