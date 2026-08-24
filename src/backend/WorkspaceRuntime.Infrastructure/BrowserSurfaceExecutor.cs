using System.Globalization;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// Routes the `browser` surface's commands to the browser backend, so an agent's
// navigation and clicks ride the same manifest-checked, ownership-gated, audited
// bus as `console.type` and `desktop.click`. Observation (elements, page text) is
// a gated read on the /browser/* endpoints; this executor owns the half that
// changes something.
public sealed class BrowserSurfaceExecutor : ISurfaceExecutor
{
    private readonly IBrowserBackend browser;

    public BrowserSurfaceExecutor(IBrowserBackend browser)
    {
        this.browser = browser;
    }

    public string SurfaceId => "browser";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var id = Required(request, "id");
        switch (request.Operation)
        {
            case "navigate":
            {
                var url = request.Arguments.GetValueOrDefault("url", "");
                // Defense in depth, independent of the manifest's maxLength and of
                // the backend's own check: a javascript: URL is arbitrary script
                // execution, which is the one capability this surface refuses to
                // expose as a command. Letting it in through navigate would void
                // every other row in the manifest.
                if (!BrowserUrl.IsAllowed(url, out var refusal))
                {
                    return new ToolExecutionResult(false, refusal, null);
                }
                var result = await browser.NavigateAsync(id, url, cancellationToken);
                return new ToolExecutionResult(result.Ok, Describe(result), null);
            }

            case "click":
            {
                var element = Required(request, "element");
                if (!BrowserRef.IsWellFormed(element))
                {
                    return new ToolExecutionResult(false, $"'{element}' is not an element reference from this page.", null);
                }
                var result = await browser.ClickAsync(id, element, cancellationToken);
                return new ToolExecutionResult(result.Ok, Describe(result), null);
            }

            case "back":
            {
                var result = await browser.BackAsync(id, cancellationToken);
                return new ToolExecutionResult(result.Ok, Describe(result), null);
            }

            default:
                return new ToolExecutionResult(false, $"Browser executor rejected unknown operation '{request.Operation}'.", null);
        }
    }

    public Task<EffectPreview> PreviewAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var id = request.Arguments.GetValueOrDefault("id");
        var summary = request.Operation switch
        {
            "navigate" => $"Would open '{request.Arguments.GetValueOrDefault("url")}' in session '{id}'.",
            "click" => $"Would click element {request.Arguments.GetValueOrDefault("element")} on the page open in session '{id}'.",
            "back" => $"Would go back to the previous page in session '{id}'.",
            _ => $"Unknown browser operation '{request.Operation}'."
        };
        return Task.FromResult(new EffectPreview(true, summary, Array.Empty<CellChange>()));
    }

    // Where the browser ended up is the useful part of the audit record: "clicked
    // link 'Sign in'" says little, "…and the page is now accounts.example.com"
    // says what actually happened.
    private static string Describe(BrowserActionResult result) =>
        string.IsNullOrEmpty(result.Url) || result.Detail.Contains(result.Url, StringComparison.Ordinal)
            ? result.Detail
            : $"{result.Detail} Now at {result.Url}.";

    private static string Required(ToolRequest request, string key) =>
        request.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument '{key}'.");
}
