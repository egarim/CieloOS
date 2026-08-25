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
    public void The_startup_probe_never_outwaits_the_recording()
    {
        // The surface accepts one second, and a one-second capture FINISHES inside
        // a one-second health probe — so "it already exited" was read as "it failed
        // to start", making the shortest supported length the least reliable one.
        // The probe is now bounded by the recording, and an exit during it is
        // judged by the file rather than assumed to be a failure.
        var helper = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "distro", "images", "desktop", "lunos-recorder"));

        Assert.Contains("time.sleep(min(1.0, max(0.2, seconds / 2)))", helper);
        Assert.DoesNotContain("time.sleep(1.0)", helper);
    }

    [Fact]
    public void A_pid_alone_is_not_an_identity()
    {
        // The state file OUTLIVES its ffmpeg: the process stops itself at -t while
        // the file waits for `stop`. Linux reuses pids, so a later stop would have
        // sent SIGINT and then SIGKILL to whatever innocent process inherited the
        // number. Proven on the live box with a planted state file: before this,
        // an unrelated `sleep` would have been killed; now it survives.
        var helper = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "distro", "images", "desktop", "lunos-recorder"));

        Assert.Contains("/proc/{pid}/stat", helper);
        Assert.Contains("pidStart", helper);
        Assert.Contains("pidComm", helper);
        // The bare liveness check is gone — os.kill(pid, 0) answers "does a process
        // exist", which is not the question.
        Assert.DoesNotContain("os.kill(pid, 0)", helper);
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

public class ExampleRunnerTests
{
    [Fact]
    public void Two_runs_cannot_both_claim_the_same_person()
    {
        // Read-then-write is not atomic just because the dictionary is. Two
        // requests a millisecond apart could both see "nothing running" and both
        // start driving the same desktop.
        var runner = new ExampleRunner();
        var claims = 0;

        Parallel.For(0, 64, index =>
        {
            if (runner.TryClaim("joche", Run($"run-{index}", ExampleRunState.Running)))
            {
                Interlocked.Increment(ref claims);
            }
        });

        Assert.Equal(1, claims);
    }

    [Fact]
    public void A_finished_run_releases_the_claim()
    {
        // The gate has to open again, or the person gets one demo per restart.
        var runner = new ExampleRunner();
        Assert.True(runner.TryClaim("joche", Run("first", ExampleRunState.Running)));
        Assert.False(runner.TryClaim("joche", Run("second", ExampleRunState.Running)));

        runner.Update("joche", current => current with { State = ExampleRunState.Finished });

        Assert.True(runner.TryClaim("joche", Run("third", ExampleRunState.Running)));
    }

    [Fact]
    public void A_run_awaiting_a_person_still_holds_the_claim()
    {
        var runner = new ExampleRunner();
        Assert.True(runner.TryClaim("joche", Run("first", ExampleRunState.AwaitingApproval)));
        Assert.False(runner.TryClaim("joche", Run("second", ExampleRunState.Running)));
    }

    [Fact]
    public void One_persons_run_does_not_block_anothers()
    {
        var runner = new ExampleRunner();
        Assert.True(runner.TryClaim("joche", Run("hers", ExampleRunState.Running)));
        Assert.True(runner.TryClaim("yulia", Run("his", ExampleRunState.Running)));
    }

    [Fact]
    public void The_session_is_bound_into_every_step()
    {
        var bound = ExampleSubstitution.Bind(
            new Dictionary<string, string> { ["id"] = "{session}", ["url"] = "https://example.com" },
            "joche-abc123");

        Assert.Equal("joche-abc123", bound["id"]);
        Assert.Equal("https://example.com", bound["url"]);
    }

    [Fact]
    public void Every_shipped_example_names_surfaces_that_exist()
    {
        // The failure this prevents is the one that makes demos worthless: a
        // renamed surface leaves an example that still reads convincingly and
        // refuses at step one. The examples are also the installation's acceptance
        // test, so a stale one is worse than none.
        var catalog = new FileExampleCatalog(TestRepository.Root());
        var surfaces = TestRepository.Surfaces();

        Assert.NotEmpty(catalog.Examples);
        foreach (var example in catalog.Examples)
        {
            Assert.NotEmpty(example.Steps);
            foreach (var step in example.Steps)
            {
                var surface = surfaces.Find(step.Surface);
                Assert.True(surface is not null, $"{example.Id}: no surface '{step.Surface}'");
                if (step.Kind == "command")
                {
                    Assert.True(surface!.Commands.ContainsKey(step.Operation),
                        $"{example.Id}: '{step.Surface}' has no command '{step.Operation}'");
                }
                Assert.False(string.IsNullOrWhiteSpace(step.Note), $"{example.Id}: a step with nothing to say");
            }
        }
    }

    [Fact]
    public void Deleting_the_examples_folder_makes_it_stay_deleted()
    {
        // Bumping the version would otherwise resurrect a folder somebody removed
        // on purpose: the new version's marker is absent, so the seed reads like a
        // first run. Deleting something twice to make it stay gone is the computer
        // arguing with its owner.
        var seed = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "distro", "images", "desktop", "cielo-examples-seed"));

        Assert.Contains(".cielo-examples-seeded", seed);
        Assert.Contains("! -d \"$TARGET\"", seed);
    }

    private static ExampleRun Run(string id, ExampleRunState state) =>
        new(id, "example", "Example", null, state, 0, 3, "", Array.Empty<ExampleStepReport>());
}

public class ApprovalHonestyTests
{
    [Fact]
    public void An_approved_command_that_did_not_work_is_not_recorded_as_success()
    {
        // The audit stamped Success on every approval, whatever the executor
        // returned. So a navigation the person consented to, and which was then
        // refused downstream, read as if it had worked — in the one place people
        // go to find out what actually happened.
        var source = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Application", "RuntimeServices.cs"));

        Assert.Contains("result.Executed ? AuditOutcome.Success : AuditOutcome.Blocked", source);
        Assert.Contains("did not take effect", source);
    }
}

public class DesktopGeometryTests
{
    [Fact]
    public void A_desktop_session_is_pinned_to_a_fixed_size()
    {
        // Selkies re-modes the display through RandR when a viewer connects, and
        // that one behaviour caused three separate failures: recordings truncating
        // silently (x11grab dies on the reconfiguration and still exits 0), element
        // boxes going stale, and any coordinate an agent was handed pointing
        // somewhere else a moment later.
        var source = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Infrastructure", "SessionOrchestrator.cs"));

        Assert.Contains("SELKIES_MANUAL_WIDTH=", source);
        Assert.Contains("SELKIES_MANUAL_HEIGHT=", source);
        Assert.Contains("SELKIES_IS_MANUAL_RESOLUTION_MODE=true", source);
    }

    [Fact]
    public void A_console_session_is_not_given_a_screen_size()
    {
        // A console has no X display; setting a resolution on it would be cargo.
        var source = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Infrastructure", "SessionOrchestrator.cs"));

        Assert.Contains("if (!isConsole)", source);
    }

    [Fact]
    public void The_size_is_configurable_but_defaults_to_something_footage_is_cut_for()
    {
        var options = new SessionBackendOptions();
        Assert.Equal(1920, options.DesktopWidth);
        Assert.Equal(1080, options.DesktopHeight);
    }

    [Fact]
    public void The_recorder_still_reads_the_geometry_rather_than_assuming_it()
    {
        // Belt and braces: pinning the size is the fix, but a recorder that hard-codes
        // 1920x1080 would produce a broken file the day someone changes the setting,
        // or on a session that predates it.
        var helper = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "distro", "images", "desktop", "lunos-recorder"));

        Assert.Contains("xdpyinfo", helper);
        Assert.DoesNotContain("1920x1080", helper);
    }
}

public class LanguageTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("es")]
    public void The_three_languages_the_product_is_built_for_are_offered(string code)
    {
        Assert.True(Languages.IsKnown(code));
    }

    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("es-419", "es")]
    [InlineData("es_MX", "es")]
    [InlineData("EN-gb", "en")]
    public void A_regional_variant_lands_where_it_means_to(string requested, string expected)
    {
        // "es-419" is Latin American Spanish and must not fall through to English
        // just because the exact tag is not on the list.
        Assert.Equal(expected, Languages.Resolve(requested).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("klingon")]
    public void An_unknown_language_opens_an_English_desk_rather_than_no_desk(string? code)
    {
        // A language removed in a later release, or a code from a newer panel, must
        // still open a session. English is inconvenient; a machine that will not
        // start is not.
        Assert.Equal("en", Languages.Resolve(code).Code);
    }

    [Fact]
    public void Every_language_keeps_a_us_layout()
    {
        // ASCII paths, shell commands and the agent's own xdotool keysyms have to
        // keep working whatever the person writes in. A desk that can only type
        // Cyrillic cannot type a file path.
        foreach (var language in Languages.All)
        {
            Assert.Contains("us", language.Layouts.Split(','));
        }
    }

    [Fact]
    public void Every_language_names_a_real_locale()
    {
        // C.UTF-8 silently mangles anything outside ASCII — the failure people
        // describe as "it looked fine until I typed my own name".
        foreach (var language in Languages.All)
        {
            Assert.EndsWith(".UTF-8", language.Locale);
            Assert.NotEqual("C.UTF-8", language.Locale);
        }
    }

    [Fact]
    public void A_session_is_given_its_owners_locale_and_layouts()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Infrastructure", "SessionOrchestrator.cs"));

        Assert.Contains("LANG={language.Locale}", source);
        Assert.Contains("CIELO_LAYOUTS={language.Layouts}", source);
    }

    [Fact]
    public void The_desktop_image_can_actually_render_and_type_these_languages()
    {
        // Installing locales is not enough — they have to be GENERATED, or the
        // container falls back to C.UTF-8 and mangles non-ASCII. And Cyrillic
        // renders as boxes without fonts, which looks like a bug rather than a
        // missing package.
        var containerfile = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "distro", "images", "desktop", "Containerfile"));

        Assert.Contains("locale-gen", containerfile);
        Assert.Contains("ru_RU.UTF-8", containerfile);
        Assert.Contains("es_ES.UTF-8", containerfile);
        Assert.Contains("fonts-dejavu-core", containerfile);
    }

    [Fact]
    public void Users_who_predate_languages_are_recorded_as_English()
    {
        var migration = Directory
            .EnumerateFiles(Path.Combine(TestRepository.Root(), "src", "backend",
                "WorkspaceRuntime.Infrastructure", "Migrations"), "*UserLanguage.cs")
            .Single();

        Assert.Contains("defaultValue: \"en\"", File.ReadAllText(migration));
    }
}

public class ApplicationLanguageTests
{
    [Fact]
    public void The_toolkit_is_translated_too_not_only_the_applications()
    {
        // XFCE's own parts already shipped ru and es. The toolkit did not: without
        // gtk30.mo and glib20.mo every file dialog, Cancel button and GLib error
        // stays English while the menus around it are translated. Menus in one
        // language and dialogs in another reads as a broken build, not a missing
        // package. Ubuntu keeps those in language packs.
        var containerfile = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "distro", "images", "desktop", "Containerfile"));

        Assert.Contains("language-pack-gnome-ru", containerfile);
        Assert.Contains("language-pack-gnome-es", containerfile);
    }

    [Fact]
    public void The_variable_gettext_actually_reads_is_set()
    {
        // gettext consults LANGUAGE ahead of LC_ALL and LANG. Setting only LANG
        // gets number and date formatting right and leaves every interface in
        // English — the confusing half of the job, and the half that looks done.
        var source = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Infrastructure", "SessionOrchestrator.cs"));

        Assert.Contains("LANGUAGE={language.Code}", source);
        Assert.Contains("LANG={language.Locale}", source);
    }
}

// A fix that only exists on the machine it was made on is not a fix. These check
// that what was built today actually reaches a NEW installation — the failure mode
// being an empty list or an English prompt rather than an error anybody notices.
public class ShippedToNewInstallationsTests
{
    private static string Release() => File.ReadAllText(Path.Combine(
        TestRepository.Root(), "distro", "scripts", "build-release.sh"));

    private static string Installer() => File.ReadAllText(Path.Combine(
        TestRepository.Root(), "distro", "install.sh"));

    [Fact]
    public void The_release_bundle_carries_the_translations_not_only_the_manifests()
    {
        // `cp surfaces/*.surface.json` alone ships a bundle whose consent prompts
        // are English everywhere — and it LOOKS fine, because the panel falls back
        // rather than failing. Nobody sees a bug; they see a machine that never
        // speaks their language.
        Assert.Contains("surfaces/i18n", Release());
    }

    [Fact]
    public void The_release_bundle_carries_the_image_tree()
    {
        // The examples, the Containerfiles, the helpers and the seeds all live
        // under distro/images and are built on the target.
        Assert.Contains("distro/images/.", Release());
    }

    [Fact]
    public void The_installer_lays_down_surfaces_wholesale()
    {
        // Wholesale, so a new subdirectory (i18n today, something else tomorrow)
        // does not need the installer edited to travel with it.
        Assert.Contains("$BUNDLE/surfaces", Installer());
    }

    [Fact]
    public void The_catalogue_looks_where_the_installer_actually_puts_things()
    {
        // install.sh puts the image tree in /var/lib/cielo/images while the runtime
        // root is /opt/cielo, so NOTHING under the runtime root holds the examples.
        // Found by auditing rather than by testing: locally it worked, because the
        // directory had been created by hand.
        var catalog = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Infrastructure", "FileExampleCatalog.cs"));
        var program = File.ReadAllText(Path.Combine(
            TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Api", "Program.cs"));

        Assert.Contains("installedImagesRoot", catalog);
        // Derived from the profile-images setting rather than written out again, so
        // moving one moves the other.
        Assert.Contains("Sessions:ProfileImagesPath", program);
        Assert.Contains("new FileExampleCatalog(", program);
    }

    [Fact]
    public void An_installed_layout_finds_its_examples()
    {
        // The behaviour, not just the source: given the installed shape, the
        // catalogue must read the examples.
        var root = Path.Combine(Path.GetTempPath(), "cielo-install-shape-" + Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(root, "opt", "cielo");
        var examples = Path.Combine(root, "var", "lib", "cielo", "images", "desktop", "examples", "01-demo");
        try
        {
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(examples);
            File.WriteAllText(Path.Combine(examples, "example.json"),
                """{"id":"demo","title":"Demo","summary":"s","needsSession":false,"steps":[{"surface":"spreadsheet","operation":"set-cell","input":{"address":"A1","value":"x"},"note":"n"}]}""");

            var catalog = new FileExampleCatalog(
                runtimeRoot,
                Path.Combine(root, "var", "lib", "cielo", "images"));

            Assert.Single(catalog.Examples);
            Assert.Equal("demo", catalog.Examples[0].Id);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
