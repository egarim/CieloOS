using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// A session id must be unique per session. It was unique per session only while
// the owner's slug stayed short.
//
// The id was built as $"{owner}-{Guid:N}" truncated to Math.Min(owner.Length + 9,
// 40) — which spends the budget on the owner first and gives the remainder to the
// random part. 8 hex digits at 31 characters, 4 at 35, NONE at 39: every session
// that owner opened got the same id, and the second `podman --name` collided.
//
// Latent until now. Organizations compose the org into the slug, which is what
// turns a thirty-character owner from improbable into ordinary.
public class SessionIdTests
{
    [Theory]
    [InlineData("joche")]
    [InlineData("acme-maria")]
    [InlineData("international-systems-alexandra")]          // 31
    [InlineData("international-systems-alexandra-b")]        // 33
    [InlineData("a-very-long-organization-name-for-someone")] // 41, longer than the whole budget
    public void Two_sessions_for_one_owner_never_share_an_id(string owner)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Assert.True(ids.Add(SessionNaming.NewId(owner)),
                $"Two sessions for '{owner}' ({owner.Length} chars) got the same id — podman --name would collide.");
        }
    }

    [Theory]
    [InlineData("joche")]
    [InlineData("international-systems-alexandra-b")]
    [InlineData("a-very-long-organization-name-for-someone")]
    public void An_id_still_fits_the_budget_and_still_starts_with_who_it_belongs_to(string owner)
    {
        var id = SessionNaming.NewId(owner);

        Assert.True(id.Length <= 40, $"'{id}' is {id.Length} characters.");

        // The readable prefix is a convenience — nothing parses the owner back out
        // of an id, it comes from the lunos.owner label — so it may be truncated.
        // It must still be recognisable.
        Assert.StartsWith(owner[..Math.Min(owner.Length, 8)], id, StringComparison.Ordinal);
    }

    [Fact]
    public void Owners_sharing_a_long_prefix_still_get_different_ids()
    {
        // Truncating the owner means two long slugs can share a stem. The random
        // half is what keeps them apart, which is the whole point of not spending
        // it on the name.
        var a = SessionNaming.NewId("international-systems-alexandra-one");
        var b = SessionNaming.NewId("international-systems-alexandra-two");
        Assert.NotEqual(a, b);
    }
}
