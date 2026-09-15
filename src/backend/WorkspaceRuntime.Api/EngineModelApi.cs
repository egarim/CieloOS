using System.Text;
using System.Text.Json;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

// The only way an engine reaches a model.
//
// A foreign harness normally holds its own provider credentials and decides its
// own model. Both of those belong to the host here, and not for tidiness: an
// engine that can reach a provider directly can bill a budget nobody is watching,
// and "the benchmark pinned the model" stops being a fact about the run and
// becomes a claim about a config file somebody could edit.
//
// So an engine is given this URL and no key of its own, inside a container with
// --network=none where this is one of the only two things reachable. It cannot
// call api.deepseek.com because there is no route to it.
//
// The `model` field in the request is deliberately IGNORED. The response says
// which model actually served the call, so an engine that asked for something else
// is told plainly rather than left believing it got what it asked for.
public static class EngineModelApi
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/engine/v1/chat/completions", async (
            JsonElement body,
            HttpContext context,
            IModelRegistry models,
            IRuntimeStore store,
            ITokenLedger? ledger,
            EngineModelClient clients,
            CancellationToken cancellationToken) =>
        {
            var caller = (RuntimePrincipal)context.Items["principal"]!;
            var agent = AgentOf(caller, store);
            if (agent is null)
            {
                return Results.Json(new { error = new { message = "This account has no agent to act as." } },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (models.Resolve("chat", agent) is not { } resolved)
            {
                return Results.Json(new { error = new { message = "No chat provider is configured on this machine." } },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var provider = resolved.Profile;

            // Budget is checked here rather than only after the fact, because this
            // is the point where an engine can spend: a foreign harness may make
            // twenty calls for one goal and never tell us it intends to.
            if (ledger is not null
                && TokenBudget.Exceeded(ledger, agent.OwnerUserId, agent.Id, provider.Locality) is { } overspent)
            {
                return Results.Json(new { error = new { message = overspent } }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            var forwarded = Rewrite(body, provider.Model);
            using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(forwarded, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
            }

            // The metering handler lives on this client, so the call is counted
            // whether or not the engine cooperates.
            using var scope = ledger is null
                ? null
                : TokenAccountingScope.Begin(agent.OwnerUserId, agent.Id, provider.Id, provider.Model, provider.Locality);

            var http = clients.Http;
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                return Results.Content(payload, "application/json", Encoding.UTF8, (int)response.StatusCode);
            }
            catch (Exception exception)
            {
                // Never surface the provider URL or key in an error an engine reads.
                return Results.Json(new { error = new { message = $"The model provider could not be reached: {exception.GetType().Name}." } },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    // Replace whatever model the caller asked for with the one the host chose, and
    // leave the rest of the body alone — messages, tools, temperature, streaming
    // are the engine's business and none of them are ours to reinterpret.
    private static string Rewrite(JsonElement body, string model)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (body.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in body.EnumerateObject())
                {
                    if (property.NameEquals("model"))
                    {
                        continue;
                    }
                    property.WriteTo(writer);
                }
            }
            writer.WriteString("model", model);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static AgentProfile? AgentOf(RuntimePrincipal principal, IRuntimeStore store) =>
        principal.Kind == PrincipalKind.Agent
            ? store.GetAgent(principal.Subject)
            : store.Agents.FirstOrDefault(agent => agent.OwnerUserId == principal.Subject);
}

// One client, with the metering handler wired in front of it the way every other
// metered client in this runtime is built. Not IHttpClientFactory: its pipeline
// assigns InnerHandler itself, and TokenMeteringHandler takes its inner handler in
// the constructor, so the two conventions quietly fight over the same field.
public sealed class EngineModelClient
{
    public EngineModelClient(HttpClient http) => Http = http;
    public HttpClient Http { get; }
}
