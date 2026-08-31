using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Tests;

public class AuditQueryTests
{
    private static AuditEvent At(string action, DateTimeOffset time) =>
        new(Guid.NewGuid(), time, Guid.NewGuid(), null, action, AuditOutcome.Success, "detail");

    [Fact]
    public void Filter_narrows_by_time_range_and_action()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            At("desktop.type", t0),
            At("desktop.type", t0.AddMinutes(5)),
            At("browser.click", t0.AddMinutes(10)),
        };

        var after = AuditQuery.Filter(events, since: t0.AddMinutes(8));
        Assert.Single(after);
        Assert.Equal("browser.click", after[0].Action);

        var allTyped = AuditQuery.Filter(events, action: "desktop.type");
        Assert.Equal(2, allTyped.Count);

        var window = AuditQuery.Filter(events, since: t0.AddMinutes(3), until: t0.AddMinutes(12));
        Assert.Equal(2, window.Count);
    }

    [Fact]
    public void Filter_with_no_args_returns_everything()
    {
        var events = new[] { At("a", DateTimeOffset.UtcNow), At("b", DateTimeOffset.UtcNow) };
        Assert.Equal(2, AuditQuery.Filter(events).Count);
    }
}
