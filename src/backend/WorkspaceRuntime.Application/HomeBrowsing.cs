namespace WorkspaceRuntime.Application;

public sealed record HomeEntry(string Name, string Kind, long Size, long ModifiedEpoch);

public sealed record HomeListing(string Owner, string Path, IReadOnlyList<HomeEntry> Entries);

public sealed record HomeFile(string Owner, string Path, string Content, bool Truncated, long Size, bool Binary = false);

// A file leaving the runtime as bytes. Content is an open stream the caller owns
// and must dispose; nothing is buffered, so a spreadsheet or a PDF costs the
// runtime a pipe rather than its size in memory.
public sealed record HomeDownload(string Owner, string Path, string Name, string ContentType, long Size, Stream Content);

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

    // Reading is for looking; downloading is for keeping. A preview decodes text
    // and stops at a cap, so it can never hand back the spreadsheet an agent just
    // wrote — these do, byte for byte.
    Task<HomeDownload?> DownloadAsync(string owner, string path, CancellationToken cancellationToken);
    Task<HomeDownload?> DownloadSharedAsync(string owner, string path, CancellationToken cancellationToken);
}

// Content types for the handful of things an office desk actually produces. The
// default is deliberately application/octet-stream: an unrecognised file must
// download rather than render, so bytes from a session are never interpreted as
// markup in the panel's own origin.
public static class HomeContentType
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".zip"] = "application/zip",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",
        [".mp3"] = "audio/mpeg",
        [".mp4"] = "video/mp4",
        [".wav"] = "audio/wav"
    };

    // Note the absentees: .svg and .html stay octet-stream. Both are active
    // documents, and a session is exactly where an untrusted one comes from.
    public static string ForPath(string path)
    {
        var name = path.Split('/').LastOrDefault() ?? path;
        var dot = name.LastIndexOf('.');
        return dot >= 0 && ByExtension.TryGetValue(name[dot..], out var type)
            ? type
            : "application/octet-stream";
    }
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
