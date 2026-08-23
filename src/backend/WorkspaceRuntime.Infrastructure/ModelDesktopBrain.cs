using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// A vision-backed brain for the desktop loop. It shows the model the goal, the
// AT-SPI ELEMENT LIST (exact boxes) and a SCREENSHOT, and asks for the next action
// as strict JSON. The model is told to PREFER an element id (grounded, exact) and
// use raw x,y only for targets the element list does not contain. Any provider
// that speaks the OpenAI chat format WITH image input (Azure OpenAI gpt-4.1-mini)
// drops in via ModelBrainOptions.
public sealed class ModelDesktopBrain : IDesktopAgentBrain
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string System =
        "You operate an XFCE Linux desktop to accomplish a GOAL. Each turn you receive a numbered " +
        "ELEMENT LIST from the accessibility tree — each line is `[id] role \"name\" (x,y wXh)` with " +
        "EXACT on-screen boxes — and a SCREENSHOT. Decide the SINGLE next action and reply with ONLY a " +
        "JSON object: {\"done\":boolean,\"kind\":\"click|double_click|type|key|done\",\"elementId\":int|null," +
        "\"x\":int|null,\"y\":int|null,\"text\":string|null,\"keysym\":string|null,\"note\":string}. " +
        "PREFER clicking by elementId (exact and reliable). Use x,y pixels ONLY when the target is visible " +
        "in the screenshot but NOT in the element list (e.g. a desktop icon on the canvas). For \"type\", " +
        "put the text in \"text\" (the target must already be focused — click it first). For \"key\", put an " +
        "X keysym in \"keysym\" (Return, Escape, Tab, ctrl+c). If the GOAL is visibly achieved, set done=true " +
        "immediately. Never repeat an action that did not change the screen. \"note\" is one line of reasoning.";

    private readonly HttpClient http;
    private readonly ModelBrainOptions options;

    public ModelDesktopBrain(HttpClient http, ModelBrainOptions options)
    {
        this.http = http;
        this.options = options;
        this.http.BaseAddress ??= new Uri(options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<DesktopAgentAction> DecideAsync(
        string goal, IReadOnlyList<DesktopElement> elements, byte[]? screenshotPng,
        int screenWidth, int screenHeight, IReadOnlyList<string> history, int step,
        CancellationToken cancellationToken)
    {
        var elementList = elements.Count == 0
            ? "(the accessibility tree is empty — use the screenshot with x,y pixels)"
            : string.Join("\n", elements.Select(e => $"[{e.Id}] {e.Role} \"{e.Name}\" ({e.X},{e.Y} {e.W}x{e.H})"));

        var text =
            $"GOAL:\n{goal}\n\nSCREEN: {screenWidth}x{screenHeight}px, origin top-left\n\n" +
            $"ELEMENT LIST:\n{elementList}\n\nSTEP: {step}\n" +
            $"HISTORY (most recent last):\n{(history.Count == 0 ? "(nothing yet)" : string.Join("\n", history))}";

        var userContent = new List<object> { new { type = "text", text } };
        if (screenshotPng is { Length: > 0 })
        {
            var dataUri = "data:image/png;base64," + Convert.ToBase64String(screenshotPng);
            userContent.Add(new { type = "image_url", image_url = new { url = dataUri } });
        }

        var payload = new
        {
            model = options.Model,
            messages = new object[]
            {
                new { role = "system", content = System },
                new { role = "user", content = userContent }
            },
            temperature = 0.0,
            max_tokens = 400,
            response_format = new { type = "json_object" }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Stop($"vision model returned {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<ChatCompletion>(JsonOptions, cancellationToken);
            var content = body?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return Stop("vision model returned no content");
            }

            var decision = JsonSerializer.Deserialize<BrainDecision>(content, JsonOptions);
            if (decision is null)
            {
                return Stop("could not parse the model's action");
            }

            var kind = string.IsNullOrWhiteSpace(decision.Kind) ? (decision.Done ? "done" : "click") : decision.Kind;
            return new DesktopAgentAction(decision.Done || kind == "done", kind, decision.ElementId, decision.X, decision.Y, decision.Text, decision.Keysym, decision.Note);
        }
        catch (Exception exception)
        {
            return Stop($"vision model error: {exception.Message}");
        }
    }

    private static DesktopAgentAction Stop(string why) =>
        new(true, "done", null, null, null, null, null, why);

    private sealed record ChatCompletion([property: JsonPropertyName("choices")] List<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] Message? Message);
    private sealed record Message([property: JsonPropertyName("content")] string? Content);
    private sealed record BrainDecision(bool Done, string? Kind, int? ElementId, int? X, int? Y, string? Text, string? Keysym, string? Note);
}
