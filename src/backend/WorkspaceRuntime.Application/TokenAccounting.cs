namespace WorkspaceRuntime.Application;

// What one model call cost, attributed the way the audit trail attributes
// actions: to a human, acting through an agent. A bill that cannot name both is
// not much of a bill.
public sealed record TokenUsage(
    Guid UserId,
    Guid AgentId,
    string ProviderId,
    string Model,
    string Locality,
    int PromptTokens,
    int CompletionTokens,
    DateTimeOffset OccurredAt)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}

// A ceiling for one subject. Scope is "user", "agent" or "os"; Subject is the id
// it applies to (empty for "os", which is the whole machine).
public sealed record TokenLimit(string Scope, string Subject, long MonthlyTokens)
{
    public const string UserScope = "user";
    public const string AgentScope = "agent";
    public const string OsScope = "os";
}

public sealed record TokenSpend(long User, long Agent, long Machine);

public interface ITokenLedger
{
    void Record(TokenUsage usage);

    // Spend in the current calendar month. A rolling window would be defensible
    // too; a calendar month is what a provider's invoice uses, so a number here
    // can be compared with a number there.
    // billableOnly excludes on-box spend, which is what a ceiling acts on. The
    // full total is still available for display: a machine that has run a local
    // model hard should be able to see that, it just should not be billed for it.
    TokenSpend SpentThisMonth(Guid userId, Guid agentId, bool billableOnly = true);

    // A caller sees its own calls, not the machine's: on a multi-user box the
    // recent list would otherwise show other desks' providers, models and volumes.
    IReadOnlyList<TokenUsage> Recent(int limit, Guid? userId = null);

    IReadOnlyList<TokenLimit> Limits { get; }

    // A limit of 0 removes it: there is no such thing as a zero-token desk, and
    // "delete" and "set to nothing" should not be two different verbs.
    void SetLimit(TokenLimit limit);
}

// The rule the loops ask about before spending anything.
public static class TokenBudget
{
    // On-box inference costs no money, and a machine running a local model should
    // not be stopped by a cap meant for a metered API. Local spend is still
    // recorded — it is real machine time, and you cannot manage what you cannot
    // see — but it is not what the ceiling is about.
    public const string OnBoxLocality = "on-box";

    // Returns null when the call may proceed, or the reason it may not, written
    // for the person who will read it in a chat reply.
    public static string? Exceeded(ITokenLedger ledger, Guid userId, Guid agentId, string locality)
    {
        if (string.Equals(locality, OnBoxLocality, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var spent = ledger.SpentThisMonth(userId, agentId, billableOnly: true);
        foreach (var limit in ledger.Limits)
        {
            var (used, subject) = limit.Scope switch
            {
                TokenLimit.UserScope when limit.Subject == userId.ToString() => (spent.User, "your desk"),
                TokenLimit.AgentScope when limit.Subject == agentId.ToString() => (spent.Agent, "this agent"),
                TokenLimit.OsScope => (spent.Machine, "this machine"),
                _ => (-1L, "")
            };

            if (used >= 0 && limit.MonthlyTokens > 0 && used >= limit.MonthlyTokens)
            {
                return $"The model budget for {subject} is used up for this month " +
                       $"({used:N0} of {limit.MonthlyTokens:N0} tokens). An owner can raise it in the panel's Models tab.";
            }
        }

        return null;
    }
}

// Who a model call is being made for. The call itself happens deep inside an
// HTTP client that knows nothing about identities, so the acting pair travels
// alongside it rather than through every brain's signature.
public sealed class TokenAccountingScope : IDisposable
{
    private static readonly AsyncLocal<TokenAccountingScope?> Ambient = new();

    private readonly TokenAccountingScope? previous;

    private TokenAccountingScope(Guid userId, Guid agentId, string providerId, string model, string locality)
    {
        UserId = userId;
        AgentId = agentId;
        ProviderId = providerId;
        Model = model;
        Locality = locality;
        previous = Ambient.Value;
        Ambient.Value = this;
    }

    public Guid UserId { get; }
    public Guid AgentId { get; }
    public string ProviderId { get; }
    public string Model { get; }
    public string Locality { get; }

    public static TokenAccountingScope? Current => Ambient.Value;

    public static TokenAccountingScope Begin(Guid userId, Guid agentId, string providerId, string model, string locality) =>
        new(userId, agentId, providerId, model, locality);

    public void Dispose() => Ambient.Value = previous;
}

// Which provider a run is about to bill. Carried separately from the brain
// because a brain is cached per provider and shared by every desk that resolves
// to it — the identity of the call belongs to the run, not to the brain.
public sealed record ModelIdentity(string ProviderId, string Model, string Locality);

// The per-request stamp a brain puts on its HTTP call, so metering reflects the
// provider that was actually used rather than whichever one the run started with.
public static class TokenAccountingRequest
{
    public static readonly HttpRequestOptionsKey<ModelIdentity> Key = new("cielo.model");
}
