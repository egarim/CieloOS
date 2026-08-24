using System.Text;
using System.Text.Json;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// Records what every model call cost, from inside the HTTP pipeline.
//
// This is the only place all of them meet. The brains (console, desktop, the
// hybrid one) each build their own request, and a per-brain hook would be three
// places to forget. Providers already return `usage` on the response; until now
// it was read and thrown away.
//
// Attribution comes from the ambient TokenAccountingScope the loop opened, not
// from anything in the request — an HTTP handler has no idea who a user is, and
// guessing from a URL would be worse than not counting.
public sealed class TokenMeteringHandler : DelegatingHandler
{
    private readonly ITokenLedger ledger;

    public TokenMeteringHandler(ITokenLedger ledger, HttpMessageHandler inner)
        : base(inner)
    {
        this.ledger = ledger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        var scope = TokenAccountingScope.Current;
        if (scope is null || !response.IsSuccessStatusCode)
        {
            return response;
        }

        try
        {
            // The body has to be read here and put back, because the caller has
            // not read it yet. Buffering a chat completion is cheap; getting the
            // accounting wrong is not.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var headers = response.Content.Headers;
            var replacement = new StringContent(body, Encoding.UTF8);
            foreach (var header in headers)
            {
                replacement.Headers.Remove(header.Key);
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            response.Content = replacement;

            if (TryReadUsage(body, out var prompt, out var completion))
            {
                ledger.Record(new TokenUsage(
                    scope.UserId, scope.AgentId, scope.ProviderId, scope.Model, scope.Locality,
                    prompt, completion, DateTimeOffset.UtcNow));
            }
        }
        catch
        {
            // Accounting must never break the call it is measuring: a provider
            // that answers in an unexpected shape costs us a record, not a reply.
        }

        return response;
    }

    private static bool TryReadUsage(string body, out int promptTokens, out int completionTokens)
    {
        promptTokens = 0;
        completionTokens = 0;

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var promptValue))
        {
            promptTokens = promptValue;
        }

        if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var completionValue))
        {
            completionTokens = completionValue;
        }

        return promptTokens > 0 || completionTokens > 0;
    }
}
