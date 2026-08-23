using System.Security.Cryptography;
using System.Text;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// Bearer tokens are "<slug>:<hmac>", where the HMAC binds the slug to a single
// server-held signing key. Authentication verifies the HMAC (so a token cannot
// be forged for a slug) and then resolves the slug to a known user or agent —
// an unknown slug is rejected even with a valid signature. One key mints a
// token for every identity, so adding a user needs no new key material.
public sealed class IdentityTokenAuthenticator : ITokenAuthenticator
{
    private readonly byte[] signingKey;
    private readonly IRuntimeStore store;
    private readonly string secretsDirectory;

    public IdentityTokenAuthenticator(string secretsDirectory, IRuntimeStore store)
    {
        this.store = store;
        this.secretsDirectory = secretsDirectory;
        Directory.CreateDirectory(secretsDirectory);
        signingKey = LoadOrCreateKey(Path.Combine(secretsDirectory, "signing.key"));

        // Write each identity's token to an owner-only file for convenience
        // (dev login, VM setup). The tokens are derivable from the key, so this
        // is a convenience, not the source of truth.
        foreach (var slug in store.Users.Select(user => user.Slug).Concat(store.Agents.Select(agent => agent.Slug)))
        {
            WriteTokenFile(Path.Combine(secretsDirectory, $"{slug}.token"), Mint(slug));
        }
    }

    public RuntimePrincipal? Authenticate(string bearerToken)
    {
        var trimmed = bearerToken.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            return null;
        }

        var slug = trimmed[..separator];
        var presented = trimmed[(separator + 1)..];
        if (!FixedEquals(presented, Sign(slug)))
        {
            return null;
        }

        // A valid signature is necessary but not sufficient: the slug must name
        // a real, current identity.
        return store.FindPrincipalBySlug(slug);
    }

    public string Mint(string slug) => $"{slug}:{Sign(slug)}";

    public string IssueToken(string slug)
    {
        var token = Mint(slug);
        WriteTokenFile(Path.Combine(secretsDirectory, $"{slug}.token"), token);
        return token;
    }

    private string Sign(string slug) =>
        Convert.ToHexString(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(slug))).ToLowerInvariant();

    private static bool FixedEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length == 0)
            {
                throw new InvalidOperationException($"Signing key {path} is empty. Delete it to mint a fresh key.");
            }
            return Convert.FromHexString(existing);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        WriteOwnerOnly(path, Convert.ToHexString(key).ToLowerInvariant() + Environment.NewLine);
        return key;
    }

    private static void WriteTokenFile(string path, string token) => WriteOwnerOnly(path, token + Environment.NewLine);

    private static void WriteOwnerOnly(string path, string content)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
