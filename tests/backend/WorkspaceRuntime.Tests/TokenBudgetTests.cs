using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Tests;

public class TokenBudgetTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Agent = Guid.NewGuid();

    [Fact]
    public void Spend_below_the_ceiling_is_allowed()
    {
        var ledger = new FakeLedger(new TokenSpend(User: 900, Agent: 900, Machine: 900))
        {
            Limits = { new TokenLimit(TokenLimit.UserScope, User.ToString(), 1000) }
        };

        Assert.Null(TokenBudget.Exceeded(ledger, User, Agent, "cloud"));
    }

    [Fact]
    public void Reaching_the_ceiling_stops_the_run_and_says_why()
    {
        var ledger = new FakeLedger(new TokenSpend(User: 1000, Agent: 1000, Machine: 1000))
        {
            Limits = { new TokenLimit(TokenLimit.UserScope, User.ToString(), 1000) }
        };

        var reason = TokenBudget.Exceeded(ledger, User, Agent, "cloud");

        Assert.NotNull(reason);
        // The message is read by whoever asked the agent to do something, so it
        // has to name the numbers and the way out — not just "denied".
        Assert.Contains("1,000", reason);
        Assert.Contains("Models tab", reason);
    }

    [Fact]
    public void A_machine_wide_ceiling_applies_to_a_desk_that_has_none_of_its_own()
    {
        var ledger = new FakeLedger(new TokenSpend(User: 10, Agent: 10, Machine: 5000))
        {
            Limits = { new TokenLimit(TokenLimit.OsScope, "", 4000) }
        };

        Assert.NotNull(TokenBudget.Exceeded(ledger, User, Agent, "cloud"));
    }

    [Fact]
    public void A_limit_on_someone_else_does_not_stop_this_desk()
    {
        var ledger = new FakeLedger(new TokenSpend(User: 9999, Agent: 9999, Machine: 9999))
        {
            Limits = { new TokenLimit(TokenLimit.UserScope, Guid.NewGuid().ToString(), 10) }
        };

        Assert.Null(TokenBudget.Exceeded(ledger, User, Agent, "cloud"));
    }

    [Fact]
    public void On_box_inference_is_never_capped()
    {
        // A local model costs machine time, not money. Stopping a fully-local
        // machine because of a ceiling meant for a metered API would be perverse —
        // the spend is still recorded, it just is not what the ceiling is about.
        var ledger = new FakeLedger(new TokenSpend(User: 10_000, Agent: 10_000, Machine: 10_000))
        {
            Limits = { new TokenLimit(TokenLimit.OsScope, "", 1) }
        };

        Assert.Null(TokenBudget.Exceeded(ledger, User, Agent, TokenBudget.OnBoxLocality));
    }

    [Fact]
    public void An_accounting_scope_is_ambient_and_restores_what_it_replaced()
    {
        Assert.Null(TokenAccountingScope.Current);

        using (TokenAccountingScope.Begin(User, Agent, "deepseek", "deepseek-chat", "cloud"))
        {
            Assert.Equal("deepseek", TokenAccountingScope.Current!.ProviderId);

            using (TokenAccountingScope.Begin(User, Agent, "azure", "gpt-4.1-mini", "cloud"))
            {
                Assert.Equal("azure", TokenAccountingScope.Current!.ProviderId);
            }

            // Nested scopes happen when one run resolves a second provider; the
            // outer one has to survive its inner one.
            Assert.Equal("deepseek", TokenAccountingScope.Current!.ProviderId);
        }

        Assert.Null(TokenAccountingScope.Current);
    }

    private sealed class FakeLedger : ITokenLedger
    {
        private readonly TokenSpend spend;

        public FakeLedger(TokenSpend spend) => this.spend = spend;

        public List<TokenLimit> Recorded { get; } = new();
        public List<TokenLimit> Limits { get; } = new();

        IReadOnlyList<TokenLimit> ITokenLedger.Limits => Limits;

        public void Record(TokenUsage usage)
        {
        }

        public TokenSpend SpentThisMonth(Guid userId, Guid agentId) => spend;

        public IReadOnlyList<TokenUsage> Recent(int limit) => Array.Empty<TokenUsage>();

        public void SetLimit(TokenLimit limit) => Recorded.Add(limit);
    }
}
