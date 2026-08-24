using System.Security.Cryptography;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// PBKDF2-SHA256, from the framework. Argon2id would be the better choice on
// paper, but it means a third-party package in a runtime that currently ships
// with none, and a well-parameterised PBKDF2 is a long way ahead of what this
// replaces (a password nobody had at all). The format carries its own
// parameters, so the cost can be raised later without invalidating old hashes.
//
//   pbkdf2-sha256$<iterations>$<salt-b64>$<hash-b64>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // OWASP's 2023 floor for PBKDF2-SHA256. Measured on this hardware at roughly
    // 100 ms, which is the point: slow enough to matter offline, fast enough that
    // a login does not feel broken.
    private const int Iterations = 600_000;

    // Computed once: it costs the same 100 ms as any other hash, and paying that
    // on every failed login would be a denial-of-service lever.
    public string DummyHash { get; } = "";

    public Pbkdf2PasswordHasher()
    {
        DummyHash = Hash("cielo-constant-time-failure-placeholder");
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var parts = stored.Split('$');
        if (parts.Length != 4
            || !string.Equals(parts[0], Scheme, StringComparison.Ordinal)
            || !int.TryParse(parts[1], out var iterations)
            || iterations <= 0)
        {
            // An unreadable hash denies. The alternative — treating it as "no
            // password set" — would turn a corrupt row into an open door.
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
