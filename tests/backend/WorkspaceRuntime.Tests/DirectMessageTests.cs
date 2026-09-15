using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// Messages between people. The interesting properties are all about the boundary,
// because that is the one a bug here would cross: a conversation has exactly two
// readers and the store is the last place that can still say so.
public sealed class DirectMessageTests : IDisposable
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(), $"workspace-runtime-dm-{Guid.NewGuid():N}.db");

    private EfRuntimeStore CreateStore()
    {
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new EfRuntimeStore(new PooledDbContextFactory<RuntimeDbContext>(options), ensureCreated: true);
    }

    [Fact]
    public void A_reply_lands_in_the_same_conversation_as_the_message_it_answers()
    {
        var store = CreateStore();
        var a = store.Users[0].Slug;
        var b = store.Users[1].Slug;

        store.SendDirectMessage(a, b, "Are you free at four?");
        store.SendDirectMessage(b, a, "Yes.");

        // If the pair key were direction-sensitive these would be two separate
        // conversations and each person would see half the exchange.
        Assert.Equal(2, store.ReadConversation(a, b).Count);
        Assert.Equal(2, store.ReadConversation(b, a).Count);
        Assert.Single(store.ListConversations(a));
        Assert.Single(store.ListConversations(b));
    }

    [Fact]
    public void Order_survives_messages_sent_in_the_same_tick()
    {
        var store = CreateStore();
        var a = store.Users[0].Slug;
        var b = store.Users[1].Slug;

        for (var index = 1; index <= 20; index++)
        {
            store.SendDirectMessage(index % 2 == 0 ? a : b, index % 2 == 0 ? b : a, $"line {index}");
        }

        var conversation = store.ReadConversation(a, b);
        Assert.Equal(20, conversation.Count);
        // Timestamps are not the order. Two messages in the same tick have no
        // recoverable order from CreatedAt alone, which is the bug the thread
        // messages had to be migrated to fix; this reads by Sequence.
        Assert.Equal(
            Enumerable.Range(1, 20).Select(index => $"line {index}").ToList(),
            conversation.Select(message => message.Text).ToList());
    }

    [Fact]
    public void A_third_person_sees_none_of_it()
    {
        var store = CreateStore();
        var a = store.Users[0].Slug;
        var b = store.Users[1].Slug;
        var outsider = "outsider";
        store.AddUser(
            new PlatformUser(Guid.NewGuid(), "Outsider", "outsider@example.com", outsider, "office", "en"),
            new Workspace(Guid.NewGuid(), Guid.NewGuid(), "Outsider"),
            new AgentProfile(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Outsider agent", "", new HashSet<string>(), $"{outsider}-agent"));

        store.SendDirectMessage(a, b, "Something private.");

        // Knowing both slugs is not access. The pair key is derived from them, so a
        // check on the key alone would let anyone who can guess two names read the
        // conversation; the caller must actually be one of the two ends.
        Assert.Empty(store.ReadConversation(outsider, a));
        Assert.Empty(store.ReadConversation(outsider, b));
        Assert.Empty(store.ListConversations(outsider));
    }

    [Fact]
    public void Unread_counts_only_what_was_sent_to_you()
    {
        var store = CreateStore();
        var a = store.Users[0].Slug;
        var b = store.Users[1].Slug;

        store.SendDirectMessage(a, b, "one");
        store.SendDirectMessage(a, b, "two");
        store.SendDirectMessage(b, a, "three");

        Assert.Equal(2, store.ListConversations(b).Single().Unread);
        Assert.Equal(1, store.ListConversations(a).Single().Unread);

        // Opening it clears only the caller's side. Marking your own outgoing
        // messages read would make the other person's badge depend on you opening
        // a tab, which is not something they can observe or rely on.
        Assert.Equal(2, store.MarkConversationRead(b, a));
        Assert.Equal(0, store.ListConversations(b).Single().Unread);
        Assert.Equal(1, store.ListConversations(a).Single().Unread);
    }

    [Fact]
    public void The_conversation_list_shows_the_newest_message_and_who_sent_it()
    {
        var store = CreateStore();
        var a = store.Users[0].Slug;
        var b = store.Users[1].Slug;

        store.SendDirectMessage(a, b, "first");
        store.SendDirectMessage(b, a, "last");

        var summary = store.ListConversations(a).Single();
        Assert.Equal(b, summary.WithSlug);
        Assert.Equal("last", summary.LastText);
        Assert.Equal(b, summary.LastFromSlug);
        // The display name, not the slug: a list of usernames is not a messenger.
        Assert.Equal(store.Users.Single(user => user.Slug == b).DisplayName, summary.WithDisplay);
    }

    [Fact]
    public void Messages_survive_a_restart()
    {
        var store = CreateStore();
        var a = store.Users[0].Slug;
        var b = store.Users[1].Slug;
        store.SendDirectMessage(a, b, "still here?");

        var reopened = CreateStore();
        var conversation = reopened.ReadConversation(b, a);

        Assert.Single(conversation);
        Assert.Equal("still here?", conversation[0].Text);
        Assert.Equal(a, conversation[0].FromSlug);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in Directory.EnumerateFiles(
            Path.GetDirectoryName(databasePath)!,
            Path.GetFileNameWithoutExtension(databasePath) + "*"))
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}

// The access rule lives in AccessPolicy, and a route that is not listed there
// falls through to AnyPrincipal — which is how #43 happened. Assert the listing
// rather than trusting that someone remembered.
public class MessageAccessPolicyTests
{
    [Theory]
    [InlineData("/api/messages", "GET")]
    [InlineData("/api/messages", "POST")]
    [InlineData("/api/messages/yulia", "GET")]
    [InlineData("/api/messages/yulia", "POST")]
    public void Every_message_route_is_human_only(string path, string method)
    {
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required(path, method));
    }

    [Theory]
    [InlineData("/API/MESSAGES", "GET")]
    [InlineData("/api/Messages/Yulia", "POST")]
    [InlineData("/api/messages/", "GET")]
    public void Case_and_a_trailing_slash_do_not_get_you_past_it(string path, string method)
    {
        // #43 was exactly this: the lookup compared against lowercase literals
        // without normalising, so /API/BRANDING missed every entry and fell through
        // to AnyPrincipal. A new route family gets the same test on day one.
        Assert.Equal(AccessLevel.HumanOnly, AccessPolicy.Required(path, method));
    }
}
