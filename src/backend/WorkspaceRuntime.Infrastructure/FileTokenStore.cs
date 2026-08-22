using System.Security.Cryptography;
using System.Text;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// Two file-backed principals: a human session token and an agent service token.
// Tokens are created once with owner-only permissions; the agent principal can
// never present the human token because it cannot read the file.
public sealed class FileTokenStore : ITokenAuthenticator
{
    private readonly byte[] humanToken;
    private readonly byte[] agentToken;

    public FileTokenStore(string secretsDirectory)
    {
        Directory.CreateDirectory(secretsDirectory);
        humanToken = LoadOrCreate(Path.Combine(secretsDirectory, "human.token"));
        agentToken = LoadOrCreate(Path.Combine(secretsDirectory, "agent.token"));
    }

    public string? Authenticate(string bearerToken)
    {
        var presented = Encoding.UTF8.GetBytes(bearerToken.Trim());
        if (presented.Length == 0)
        {
            return null;
        }

        if (FixedEquals(presented, humanToken))
        {
            return RuntimePrincipals.Human;
        }

        if (FixedEquals(presented, agentToken))
        {
            return RuntimePrincipals.Agent;
        }

        return null;
    }

    private static bool FixedEquals(byte[] presented, byte[] stored) =>
        presented.Length == stored.Length && CryptographicOperations.FixedTimeEquals(presented, stored);

    private static byte[] LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length == 0)
            {
                throw new InvalidOperationException($"Token file {path} is empty. Delete it to mint a fresh token.");
            }

            return Encoding.UTF8.GetBytes(existing);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write
        };
        if (!OperatingSystem.IsWindows())
        {
            // Created owner-only from the first byte; never world-readable.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(Encoding.UTF8.GetBytes(token + Environment.NewLine));
        }

        return Encoding.UTF8.GetBytes(token);
    }
}
