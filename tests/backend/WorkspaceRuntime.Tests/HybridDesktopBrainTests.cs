using Microsoft.Extensions.Logging.Abstractions;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// AT-SPI-first desktop perception: ground on the element list with a TEXT model
// (no screenshot); use the VISION model only when the tree is empty or the text
// model says the target is not in the list.
public class HybridDesktopBrainTests
{
    private static readonly IReadOnlyList<DesktopElement> OneElement =
        new[] { new DesktopElement(0, "push button", "OK", 0, 0, 10, 10) };
    private static readonly IReadOnlyList<DesktopElement> NoElements = Array.Empty<DesktopElement>();

    private static Task<DesktopAgentAction> Run(HybridDesktopBrain brain, IReadOnlyList<DesktopElement> els, byte[]? shot) =>
        brain.DecideAsync("goal", els, shot, 100, 100, Array.Empty<string>(), 1, CancellationToken.None);

    [Fact]
    public async Task Uses_the_text_brain_when_elements_are_present()
    {
        var text = new FakeBrain(new DesktopAgentAction(false, "click", 0, null, null, null, null, "click"));
        var vision = new FakeBrain(new DesktopAgentAction(false, "click", null, 5, 5, null, null, "vis"));
        var brain = new HybridDesktopBrain(text, vision, NullLogger.Instance);

        var action = await Run(brain, OneElement, new byte[] { 1 });

        Assert.Equal(0, action.ElementId);   // the text brain's grounded click
        Assert.Equal(1, text.Calls);
        Assert.Equal(0, vision.Calls);        // vision not consulted
        Assert.Null(text.LastScreenshot);     // text brain got NO screenshot (nothing leaves the box)
    }

    [Fact]
    public async Task Falls_back_to_vision_when_the_text_brain_reports_not_found()
    {
        var text = new FakeBrain(new DesktopAgentAction(false, "not_found", null, null, null, null, null, "not in list"));
        var vision = new FakeBrain(new DesktopAgentAction(false, "click", null, 5, 5, null, null, "vis"));
        var brain = new HybridDesktopBrain(text, vision, NullLogger.Instance);

        var action = await Run(brain, OneElement, new byte[] { 1 });

        Assert.Equal(5, action.X);            // vision's pixel click
        Assert.Equal(1, vision.Calls);
        Assert.NotNull(vision.LastScreenshot); // vision DID receive the screenshot
    }

    [Fact]
    public async Task Uses_vision_directly_when_the_tree_is_empty()
    {
        var text = new FakeBrain(new DesktopAgentAction(false, "click", 0, null, null, null, null, "x"));
        var vision = new FakeBrain(new DesktopAgentAction(false, "click", null, 5, 5, null, null, "vis"));
        var brain = new HybridDesktopBrain(text, vision, NullLogger.Instance);

        var action = await Run(brain, NoElements, new byte[] { 1 });

        Assert.Equal(0, text.Calls);          // text brain skipped
        Assert.Equal(1, vision.Calls);
        Assert.Equal(5, action.X);
    }

    [Fact]
    public async Task Aborts_when_target_not_found_and_no_vision_is_configured()
    {
        var text = new FakeBrain(new DesktopAgentAction(false, "not_found", null, null, null, null, null, "not in list"));
        var brain = new HybridDesktopBrain(text, vision: null, NullLogger.Instance);

        var action = await Run(brain, OneElement, null);

        Assert.True(action.Aborted);
    }

    private sealed class FakeBrain : IDesktopAgentBrain
    {
        private readonly DesktopAgentAction action;
        public int Calls { get; private set; }
        public byte[]? LastScreenshot { get; private set; }
        public FakeBrain(DesktopAgentAction action) => this.action = action;

        public Task<DesktopAgentAction> DecideAsync(
            string goal, IReadOnlyList<DesktopElement> elements, byte[]? screenshotPng,
            int screenWidth, int screenHeight, IReadOnlyList<string> history, int step, CancellationToken cancellationToken)
        {
            Calls++;
            LastScreenshot = screenshotPng;
            return Task.FromResult(action);
        }
    }
}
