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
            // Buffer FIRST, then read. Buffered content can be read again, so the
            // caller still gets its body and nothing has to be rebuilt.
            //
            // This used to read the stream and substitute a fresh StringContent.
            // That works when the read succeeds and is a trap when it does not: a
            // slow provider whose read is cancelled leaves the stream consumed and
            // the replacement never assigned, and the catch below swallows it. The
            // caller then reads a corpse and the agent reports
            //
            //     model error: The stream was already consumed. It cannot be read again.
            //
            // which is a failure the ACCOUNTING caused, in the handler whose comment
            // promises it will never break the call it is measuring.
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (TryReadUsage(body, out var prompt, out var completion))
            {
                // The run's scope says WHO; the request says WHAT was called. A
                // desktop run can use an on-box brain for grounding and a cloud one
                // for vision, so taking locality from the run would let the cloud
                // call inherit "on-box" and escape every ceiling.
                var called = request.Options.TryGetValue(TokenAccountingRequest.Key, out var stamped)
                    ? stamped
                    : new ModelIdentity(scope.ProviderId, scope.Model, scope.Locality);

                ledger.Record(new TokenUsage(
                    scope.UserId, scope.AgentId, called.ProviderId, called.Model, called.Locality,
                    prompt, completion, DateTimeOffset.UtcNow));
            }
        }
        catch
        {
            // Accounting must never break the call it is measuring: a provider that
            // answers in an unexpected shape costs us a record, not a reply. That is
            // only true because the body is buffered before it is read — swallowing
            // an exception here after consuming the stream would hand the caller a
            // response it cannot read, which is breaking the call, quietly.
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
