namespace WorkspaceRuntime.Application;

public sealed record HomeEntry(string Name, string Kind, long Size, long ModifiedEpoch);

public sealed record HomeListing(string Owner, string Path, IReadOnlyList<HomeEntry> Entries);

public sealed record HomeFile(string Owner, string Path, string Content, bool Truncated, long Size);

// The observation half of "the agent has a home you can see": a read-only,
// host-side view of a principal's persistent home volume. Reads are policed
// like any other observation (design law 2); the browser never runs code
// inside the session container (design law 4).
public interface IHomeBrowser
{
    Task<HomeListing?> ListAsync(string owner, string path, CancellationToken cancellationToken);
    Task<HomeFile?> ReadAsync(string owner, string path, CancellationToken cancellationToken);

    // The per-owner shared workspace (lunos-shared-<user>), mounted into the
    // user's and their agents' sessions at ~/shared — the collaboration space
    // distinct from the private home.
    Task<HomeListing?> ListSharedAsync(string owner, string path, CancellationToken cancellationToken);
    Task<HomeFile?> ReadSharedAsync(string owner, string path, CancellationToken cancellationToken);
}

public static class HomePath
{
    // Reduces a caller-supplied path to a safe relative path: no absolute
    // escapes, no "..", no empty or "." components. Returns "" for the root.
    public static string Sanitize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>();
        foreach (var part in parts)
        {
            if (part == "." || part == "..")
            {
                continue;
            }
            safe.Add(part);
        }

        return string.Join('/', safe);
    }
}
