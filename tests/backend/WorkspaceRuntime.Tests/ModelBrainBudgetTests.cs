using System.Net;
using System.Text;
using System.Text.Json;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// Does the model actually receive its budget?
//
// A run was asked to research a product and then produce a spreadsheet. It spent
// all eight steps searching — it found real listings at real prices — and had
// nothing left to write the file with. Every turn it was told "STEP: 3" and never
// that there were eight, so "stop looking and start delivering" was not a thought
// available to it.
//
// The fix is a number in a prompt, which is exactly the kind of change that looks
// done and is not: the parameter can be threaded all the way through and still not
// reach the text. HttpClient is injected here, so the request the model would
// actually receive can be read rather than assumed.
public class ModelBrainBudgetTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            var completion = "{\"choices\":[{\"message\":{\"content\":\"{\\\"done\\\":true,\\\"note\\\":\\\"ok\\\"}\"}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(completion, Encoding.UTF8, "application/json")
            };
        }
    }

    private static string UserMessage(string body)
    {
        using var document = JsonDocument.Parse(body);
        foreach (var message in document.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("role").GetString() == "user")
            {
                return message.GetProperty("content").GetString() ?? "";
            }
        }

        return "";
    }

    private static async Task<string> AskAsync(int step, int maxSteps)
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var brain = new ModelConsoleBrain(http, new ModelBrainOptions
        {
            BaseUrl = "https://example.invalid",
            Model = "test-model",
            ApiKey = "unused"
        });

        await brain.DecideAsync("find me a thing", "screen", Array.Empty<string>(), step, maxSteps, CancellationToken.None);
        return UserMessage(handler.Body);
    }

    [Fact]
    public async Task The_model_is_told_how_many_steps_it_has()
    {
        var user = await AskAsync(step: 3, maxSteps: 8);

        Assert.Contains("STEP: 3 of 8", user);
    }

    // The old format. If this ever comes back the agent is budget-blind again, and
    // nothing else in the product would notice.
    [Fact]
    public async Task The_budget_is_not_silently_dropped()
    {
        var user = await AskAsync(step: 1, maxSteps: 6);

        Assert.DoesNotContain("STEP: 1\n", user);
        Assert.Contains("of 6", user);
    }

    [Fact]
    public async Task The_goal_and_screen_still_reach_the_model()
    {
        var user = await AskAsync(step: 2, maxSteps: 5);

        Assert.Contains("find me a thing", user);
        Assert.Contains("screen", user);
    }
}
