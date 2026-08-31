using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Tests;

public class HyperFrameCompositionTests
{
    [Fact]
    public void Composition_uses_the_recording_as_a_roll_and_each_step_as_an_overlay()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var steps = new[]
        {
            new AuditStep(start.AddSeconds(2), "desktop.type", "type: hello"),
            new AuditStep(start.AddSeconds(5), "browser.click", "Clicked a link."),
        };

        var html = HyperFrameComposition.Render("/config/video.mp4", 1_700_000_000, 60, 1280, 720, steps);

        Assert.Contains("id=\"a-roll\"", html);
        Assert.Contains("\"/config/video.mp4\"", html);
        Assert.Contains("width=\"1280\"", html);
        Assert.Contains("data-start=\"2.00\"", html);
        Assert.Contains("data-start=\"5.00\"", html);
        Assert.Contains("desktop.type — type: hello", html);
        Assert.Contains("browser.click — Clicked a link.", html);
        // Step content is escaped so a page's own text cannot break out of the HTML.
        Assert.DoesNotContain("<script>", html);
    }
}
