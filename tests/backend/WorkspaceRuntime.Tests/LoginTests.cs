using WorkspaceRuntime.Application;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// The three things issue #9 says are missing: a credential to prove identity,
// sessions that can end, and keys an integration can hold instead of a person's
// own credential.
public class LoginTests
{
    [Fact]
    public void A_password_verifies_only_against_itself()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var stored = hasher.Hash("correct horse battery staple");

        Assert.True(hasher.Verify("correct horse battery staple", stored));
        Assert.False(hasher.Verify("Correct horse battery staple", stored));
        Assert.False(hasher.Verify("", stored));
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        // Per-password salt: two people who choose the same password must not be
        // visibly identical in the database.
        var hasher = new Pbkdf2PasswordHasher();

        Assert.NotEqual(hasher.Hash("same password"), hasher.Hash("same password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$600000$!!!not-base64!!!$aGFzaA==")]
    public void An_unreadable_hash_denies_rather_than_admits(string stored)
    {
        // A corrupt or truncated row must be a locked door, not an open one.
        Assert.False(new Pbkdf2PasswordHasher().Verify("anything", stored));
    }

    [Fact]
    public void A_session_can_be_ended_and_stops_working()
    {
        var sessions = new InMemorySessionStore();
        var user = Guid.NewGuid();
        var (session, secret) = sessions.Create(user, TimeSpan.FromHours(1));
        var now = DateTimeOffset.UtcNow;

        Assert.NotNull(sessions.Resolve(secret, now));

        sessions.Revoke(session.Id);

        // This is the whole point of server-side sessions: the credential the
        // browser still holds is now worthless, which a permanent token could
        // never be made to be.
        Assert.Null(sessions.Resolve(secret, now));
    }

    [Fact]
    public void Signing_out_everywhere_ends_every_session_for_that_person()
    {
        var sessions = new InMemorySessionStore();
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        var (_, laptop) = sessions.Create(user, TimeSpan.FromHours(1));
        var (_, phone) = sessions.Create(user, TimeSpan.FromHours(1));
        var (_, someoneElse) = sessions.Create(other, TimeSpan.FromHours(1));
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(2, sessions.RevokeAllFor(user));

        Assert.Null(sessions.Resolve(laptop, now));
        Assert.Null(sessions.Resolve(phone, now));
        // And nobody else is signed out by it.
        Assert.NotNull(sessions.Resolve(someoneElse, now));
    }

    [Fact]
    public void An_expired_session_stops_working_without_anyone_revoking_it()
    {
        var sessions = new InMemorySessionStore();
        var (_, secret) = sessions.Create(Guid.NewGuid(), TimeSpan.FromMinutes(30));

        Assert.NotNull(sessions.Resolve(secret, DateTimeOffset.UtcNow));
        Assert.Null(sessions.Resolve(secret, DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Fact]
    public void An_api_key_is_revocable_and_belongs_to_one_person()
    {
        var keys = new InMemoryApiKeyStore();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var (key, secret) = keys.Create(owner, "open-webui", null);
        var now = DateTimeOffset.UtcNow;

        Assert.StartsWith(CredentialFormat.ApiKeyPrefix, secret);
        Assert.NotNull(keys.Resolve(secret, now));

        // Someone else's revoke must not reach it.
        Assert.False(keys.Revoke(key.Id, stranger));
        Assert.NotNull(keys.Resolve(secret, now));

        Assert.True(keys.Revoke(key.Id, owner));
        Assert.Null(keys.Resolve(secret, now));
    }

    [Fact]
    public void Two_credentials_never_collide()
    {
        // These are the identity of a session: a repeat would mean two people
        // sharing one. 256 bits of randomness, checked crudely but honestly.
        var secrets = Enumerable.Range(0, 200).Select(_ => SecretHash.NewSecret()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(200, secrets.Count);
    }
}
