using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Tests;

public class RecordingStepsTests
{
    private static AuditEvent At(string action, DateTimeOffset time, AuditOutcome outcome = AuditOutcome.Success) =>
        new(Guid.NewGuid(), time, Guid.NewGuid(), null, action, outcome, "detail");

    [Fact]
    public void Build_collects_the_success_steps_in_the_recording_window_in_order()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var events = new[]
        {
            At("desktop.type", start.AddSeconds(-10)),      // before the window
            At("desktop.type", start.AddSeconds(5)),        // in the window
            At("browser.click", start.AddSeconds(20)),      // in the window
            At("spreadsheet.clear", start.AddSeconds(100)), // after the window
            At("console.type", start.AddSeconds(15), AuditOutcome.Blocked), // blocked: not a step
        };

        var steps = RecordingSteps.Build(events, 1_700_000_000, 60);

        Assert.Equal(2, steps.Count);
        Assert.Equal("desktop.type", steps[0].Action);
        Assert.Equal("browser.click", steps[1].Action);
    }
}
