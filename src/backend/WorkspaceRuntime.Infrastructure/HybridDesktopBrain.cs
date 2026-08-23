using Microsoft.Extensions.Logging;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// AT-SPI-FIRST desktop perception: ground on the accessibility element list with a
// TEXT model (no screenshot — nothing leaves the box); fall back to a VISION model
// (screenshot) only when the tree is empty or the text model reports the target is
// not in the list. The vision fallback is optional (null when no vision provider
// is configured), so the default path needs no VLM and no image egress.
public sealed class HybridDesktopBrain : IDesktopAgentBrain
{
    private readonly IDesktopAgentBrain text;
    private readonly IDesktopAgentBrain? vision;
    private readonly ILogger logger;

    public HybridDesktopBrain(IDesktopAgentBrain text, IDesktopAgentBrain? vision, ILogger logger)
    {
        this.text = text;
        this.vision = vision;
        this.logger = logger;
    }

    public async Task<DesktopAgentAction> DecideAsync(
        string goal, IReadOnlyList<DesktopElement> elements, byte[]? screenshotPng,
        int screenWidth, int screenHeight, IReadOnlyList<string> history, int step,
        CancellationToken cancellationToken)
    {
        // Primary: AT-SPI text grounding whenever the tree has elements. No image.
        if (elements.Count > 0)
        {
            var action = await text.DecideAsync(goal, elements, null, screenWidth, screenHeight, history, step, cancellationToken);
            if (!string.Equals(action.Kind, "not_found", StringComparison.OrdinalIgnoreCase))
            {
                return action;
            }
            logger.LogInformation("Desktop brain: target not in the accessibility tree; {Fallback}.",
                vision is null ? "no vision fallback configured" : "falling back to vision");
        }

        // Fallback: vision (screenshot) — only if a vision provider is configured.
        if (vision is not null)
        {
            return await vision.DecideAsync(goal, elements, screenshotPng, screenWidth, screenHeight, history, step, cancellationToken);
        }

        // No vision, and the target is not in the tree: stop cleanly (NOT success).
        return new DesktopAgentAction(true, "done", null, null, null, null, null,
            "Target not found in the accessibility tree and no vision provider is configured.", Aborted: true);
    }
}
