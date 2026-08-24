using Microsoft.EntityFrameworkCore;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// The bill, kept next to the audit trail and for the same reason: an action
// nobody can account for afterwards may as well not have been governed.
public sealed class EfTokenLedger : ITokenLedger
{
    private readonly IDbContextFactory<RuntimeDbContext> contextFactory;

    public EfTokenLedger(IDbContextFactory<RuntimeDbContext> contextFactory)
    {
        this.contextFactory = contextFactory;
    }

    public void Record(TokenUsage usage)
    {
        using var context = contextFactory.CreateDbContext();
        context.TokenUsage.Add(new TokenUsageRow
        {
            Id = Guid.NewGuid(),
            OccurredAt = usage.OccurredAt,
            MonthKey = MonthKeyOf(usage.OccurredAt),
            UserId = usage.UserId,
            AgentId = usage.AgentId,
            ProviderId = usage.ProviderId,
            Model = usage.Model,
            Locality = usage.Locality,
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens
        });
        context.SaveChanges();
    }

    public TokenSpend SpentThisMonth(Guid userId, Guid agentId, bool billableOnly = true)
    {
        var key = MonthKeyOf(DateTimeOffset.UtcNow);
        using var context = contextFactory.CreateDbContext();
        var month = context.TokenUsage.AsNoTracking().Where(row => row.MonthKey == key);
        if (billableOnly)
        {
            // On-box spend is recorded but never billed, so counting it toward a
            // ceiling would mean a month of local use could exhaust a cloud budget
            // the moment someone switched provider.
            month = month.Where(row => row.Locality != TokenBudget.OnBoxLocality);
        }

        // Three sums rather than one grouped query: the numbers answer three
        // different questions ("this desk", "this agent", "this machine") and the
        // machine total is not the sum of the desks a caller can see.
        return new TokenSpend(
            month.Where(row => row.UserId == userId).Sum(row => (long?)(row.PromptTokens + row.CompletionTokens)) ?? 0,
            month.Where(row => row.AgentId == agentId).Sum(row => (long?)(row.PromptTokens + row.CompletionTokens)) ?? 0,
            month.Sum(row => (long?)(row.PromptTokens + row.CompletionTokens)) ?? 0);
    }

    public IReadOnlyList<TokenUsage> Recent(int limit)
    {
        using var context = contextFactory.CreateDbContext();
        // Ordered by Id-free insertion order via the timestamp, in memory: SQLite
        // stores a DateTimeOffset as text it cannot sort meaningfully, and this
        // list is the last handful of calls, not a report.
        return context.TokenUsage.AsNoTracking()
            .AsEnumerable()
            .OrderByDescending(row => row.OccurredAt)
            .Take(limit)
            .Select(row => new TokenUsage(
                row.UserId, row.AgentId, row.ProviderId, row.Model, row.Locality,
                row.PromptTokens, row.CompletionTokens, row.OccurredAt))
            .ToList();
    }

    public IReadOnlyList<TokenLimit> Limits
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.TokenLimits.AsNoTracking()
                .Select(row => new TokenLimit(row.Scope, row.Subject, row.MonthlyTokens))
                .ToList();
        }
    }

    public void SetLimit(TokenLimit limit)
    {
        using var context = contextFactory.CreateDbContext();
        var existing = context.TokenLimits
            .FirstOrDefault(row => row.Scope == limit.Scope && row.Subject == limit.Subject);

        if (limit.MonthlyTokens <= 0)
        {
            if (existing is not null)
            {
                context.TokenLimits.Remove(existing);
                context.SaveChanges();
            }
            return;
        }

        if (existing is null)
        {
            context.TokenLimits.Add(new TokenLimitRow
            {
                Scope = limit.Scope,
                Subject = limit.Subject,
                MonthlyTokens = limit.MonthlyTokens
            });
        }
        else
        {
            existing.MonthlyTokens = limit.MonthlyTokens;
        }

        context.SaveChanges();
    }

    // A calendar month in UTC. The machine's local month would drift with the
    // timezone and make two people on the same box disagree about when the budget
    // resets.
    private static string MonthKeyOf(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyy-MM");
}
