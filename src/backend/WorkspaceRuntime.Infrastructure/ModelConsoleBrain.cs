using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

public sealed class ModelBrainOptions
{
    // OpenAI-compatible endpoint. DeepSeek serves this at https://api.deepseek.com.
    public string BaseUrl { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-chat";
    public required string ApiKey { get; init; }

    // Which provider this brain speaks to, stamped on every request it makes so
    // the metering handler knows what was actually called. A run can use two
    // providers — an on-box brain for grounding and a cloud one for vision — and
    // deciding locality per RUN would let the cloud call inherit "on-box" and
    // skip every ceiling (issue #14).
    public string ProviderId { get; init; } = "";
    public string Locality { get; init; } = "";
}

// A model-backed brain for the console loop: it shows the model the goal and the
// current screen and asks for the next action as strict JSON. Any provider that
// speaks the OpenAI chat format (DeepSeek here) drops in via ModelBrainOptions.
// The API key is read from configuration/environment and never logged.
public sealed class ModelConsoleBrain : IConsoleAgentBrain
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string System =
        "You operate a Linux console to accomplish a GOAL. You are given the current SCREEN " +
        "(terminal contents) and the HISTORY of what you have already typed. Decide the SINGLE " +
        "next action. Reply with ONLY a JSON object of the form " +
        "{\"done\": boolean, \"text\": string, \"submit\": boolean, \"note\": string, \"question\": string}. " +
        "If the SCREEN already shows the goal is achieved, set done=true and leave text empty — do " +
        "this immediately, do not re-verify. NEVER repeat a command you have already run; if you " +
        "just confirmed a result, you are done. Otherwise put the " +
        "exact shell command to type in \"text\", with submit=true to press Enter. Keep commands " +
        "safe and scoped to the home directory; never run destructive or networked commands unless " +
        "the goal explicitly requires it. Prefer ONE single-line command per step (use python3 -c " +
        "'...' for short scripts); avoid multi-line heredocs. " +
        "If the GOAL is a question, a greeting, or anything you can answer from what you already " +
        "know, do NOT touch the console: set done=true on the very first step and answer. Only run " +
        "commands when the goal genuinely needs the machine. " +
        "If the GOAL is missing something only the person can decide — which of several reasonable "
        + "readings they meant, a value you cannot know, or permission for something consequential "
        + "— do NOT guess and do NOT spend steps hunting for it: put the question in \"question\" "
        + "and stop. Say what you already found, ask ONE specific thing, and offer the concrete "
        + "options where there are some. Asking is a legitimate way to finish a run: guessing wrong "
        + "costs the person far more time than asking does. Do not ask when you can reasonably "
        + "proceed, and never ask twice for the same thing. "
        // "Do not spend steps hunting" was read, correctly, as "do not check your own
        // hypothesis". A run asked for a product whose name looked wrong, searched six
        // times, concluded the name was wrong, NAMED the likely correction in its
        // question — and stopped with two steps unspent. One search on that name would
        // have found the company and its microcontroller line. The owner then has to
        // answer a question the agent could have answered itself.
        + "One exception, and only one: if you can NAME the specific thing you think they meant, "
        + "spend a single step checking it, and put what you found in your question. That is not "
        + "hunting — hunting is searching for something only they can tell you; this is confirming "
        + "something you already suspect, and it turns a question they have to think about into one "
        + "they can answer in a word. Do not build the deliverable on the guess either way. "
        + "Ask as well when you are BLOCKED: if two or three attempts at the same thing have failed "
        + "and you have no genuinely different idea, say what you learned and what stopped you rather "
        + "than trying another variation of what already did not work. " +
        "\"note\" is a one-line explanation of your reasoning EXCEPT when done=true, where \"note\" " +
        "is your COMPLETE reply to the person: written to them, conversational, and as long as it " +
        "needs to be. It may be multi-line and use markdown, including fenced code blocks. Put the " +
        "reply there directly. Never echo the reply into a file to be read back, and never describe " +
        "the answer instead of giving it.";

    private readonly HttpClient http;
    private readonly ModelBrainOptions options;

    public ModelConsoleBrain(HttpClient http, ModelBrainOptions options)
    {
        this.http = http;
        this.options = options;
        this.http.BaseAddress ??= new Uri(options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<ConsoleAgentAction> DecideAsync(string goal, string screen, IReadOnlyList<string> history, int step, CancellationToken cancellationToken)
    {
        var user =
            $"GOAL:\n{goal}\n\nSCREEN:\n{screen}\n\nSTEP: {step}\n" +
            $"HISTORY (most recent last):\n{(history.Count == 0 ? "(nothing yet)" : string.Join("\n", history))}";

        var payload = new
        {
            model = options.Model,
            messages = new object[]
            {
                new { role = "system", content = System },
                new { role = "user", content = user }
            },
            temperature = 0.1,
            max_tokens = 2000,
            response_format = new { type = "json_object" }
        };

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(payload)
            };
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
            // Which provider this call actually goes to, for metering.
            httpRequest.Options.Set(TokenAccountingRequest.Key,
                new ModelIdentity(options.ProviderId, options.Model, options.Locality));

            using var response = await http.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Stop($"model returned {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<ChatCompletion>(JsonOptions, cancellationToken);
            var content = body?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return Stop("model returned no content");
            }

            var decision = JsonSerializer.Deserialize<BrainDecision>(content, JsonOptions);
            if (decision is null)
            {
                return Stop("could not parse the model's action");
            }

            return new ConsoleAgentAction(decision.Done, decision.Text, decision.Submit, decision.Note, decision.Question);
        }
        catch (Exception exception)
        {
            // Fail safe: stop the loop rather than spin on a broken provider.
            return Stop($"model error: {exception.Message}");
        }
    }

    private static ConsoleAgentAction Stop(string why) => new(true, null, false, why);

    private sealed record ChatCompletion([property: JsonPropertyName("choices")] List<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] Message? Message);
    private sealed record Message([property: JsonPropertyName("content")] string? Content);
    private sealed record BrainDecision(bool Done, string? Text, bool Submit, string? Note, string? Question);
}
