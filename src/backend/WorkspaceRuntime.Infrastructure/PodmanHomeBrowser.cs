using System.Diagnostics;
using System.Globalization;
using System.Text;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// Reads a per-owner home volume from the host side. It resolves the volume's
// mountpoint with `podman volume inspect`, then lists and reads inside it with
// `podman unshare`, which enters the rootless user namespace so idmapped files
// (written by a non-root uid inside the container) are readable. No container
// is spawned and no code runs inside the session — this is a pure host-side
// read path.
public sealed class PodmanHomeBrowser : IHomeBrowser
{
    private const long MaxReadBytes = 256 * 1024;

    // Every read goes through this. It opens the requested path ONCE and then asks
    // the kernel what it actually opened, via /proc/self/fd/3; if that is not
    // inside the volume, nothing is read and nothing is listed.
    //
    // Sanitize() already strips "..", but that is only half the problem: a session
    // owns its home and can drop a symlink to /etc in it, which open() follows.
    // Checking the name first and then opening it by name again would be a race —
    // the session can swap a path component in between — so the check is on the
    // open descriptor itself, which cannot be swapped out from under us.
    //
    // The type check before the open is a separate hazard: opening a FIFO for
    // reading blocks until a writer appears, and a session can leave one in its own
    // home. That check is by name and so racy on its own, which is why every caller
    // also bounds how long it will wait for the open to complete.
    //
    // $1 is the (podman-controlled) mountpoint, $2 the sanitized relative path.
    // Neither is interpolated into the script; only our own fixed operations are.
    private const string Guard = """
        set -eu
        mount="$1"
        relative="$2"
        target="$mount"
        if [ -n "$relative" ]; then
          target="$mount/$relative"
        fi
        kind="$(stat -L -c '%F' -- "$target")"
        case "$kind" in
          'regular file'|'regular empty file'|'directory') ;;
          *) exit 9 ;;
        esac
        exec 3< "$target"
        opened="$(readlink /proc/self/fd/3)"
        case "$opened" in
          "$mount"|"$mount"/*) ;;
          *) exit 8 ;;
        esac
        """;

    private readonly SessionBackendOptions options;

    public PodmanHomeBrowser(SessionBackendOptions options)
    {
        this.options = options;
    }

    public Task<HomeListing?> ListAsync(string owner, string path, CancellationToken cancellationToken) =>
        ListInVolumeAsync(options.HomeVolumePrefix + owner, owner, path, cancellationToken);

    public Task<HomeListing?> ListSharedAsync(string owner, string path, CancellationToken cancellationToken) =>
        ListInVolumeAsync(options.SharedVolumePrefix + owner, owner, path, cancellationToken);

    private async Task<HomeListing?> ListInVolumeAsync(string volume, string owner, string path, CancellationToken cancellationToken)
    {
        var mount = await MountpointAsync(volume, cancellationToken);
        if (mount is null)
        {
            return null;
        }

        var relative = HomePath.Sanitize(path);

        // Listing the opened directory through its own descriptor, so a directory
        // swapped for a symlink after the check cannot redirect the listing. Only
        // %f (the bare name) is used, so the /proc path never reaches the caller.
        var find = await RunGuardedAsync(
            mount,
            relative,
            "find /proc/self/fd/3/. -maxdepth 1 -mindepth 1 -printf '%y\\t%s\\t%T@\\t%f\\n'",
            cancellationToken);

        if (find.ExitCode != 0)
        {
            return new HomeListing(owner, relative, Array.Empty<HomeEntry>());
        }

        var entries = new List<HomeEntry>();
        foreach (var line in find.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split('\t');
            if (columns.Length < 4)
            {
                continue;
            }

            // Hide dot-entries. A home is full of session plumbing (.ICEauthority,
            // .Xresources, .dbus, .cache, .config) and credential stores (.ssh, .gnupg)
            // that an office user should never be shown, let alone be able to open or
            // delete by accident. The shared workspace and Desktop are what matter.
            if (columns[3].StartsWith('.'))
            {
                continue;
            }

            var kind = columns[0] switch { "d" => "directory", "l" => "link", "f" => "file", _ => "special" };
            _ = long.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            var epoch = columns[2].Split('.', 2)[0];
            _ = long.TryParse(epoch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var modified);
            entries.Add(new HomeEntry(columns[3], kind, size, modified));
        }

        entries.Sort((left, right) =>
        {
            if (left.Kind != right.Kind)
            {
                return left.Kind == "directory" ? -1 : 1;
            }
            return string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });

        return new HomeListing(owner, relative, entries);
    }

    public Task<HomeFile?> ReadAsync(string owner, string path, CancellationToken cancellationToken) =>
        ReadInVolumeAsync(options.HomeVolumePrefix + owner, owner, path, cancellationToken);

    public Task<HomeFile?> ReadSharedAsync(string owner, string path, CancellationToken cancellationToken) =>
        ReadInVolumeAsync(options.SharedVolumePrefix + owner, owner, path, cancellationToken);

    private async Task<HomeFile?> ReadInVolumeAsync(string volume, string owner, string path, CancellationToken cancellationToken)
    {
        var mount = await MountpointAsync(volume, cancellationToken);
        if (mount is null)
        {
            return null;
        }

        var relative = HomePath.Sanitize(path);
        if (relative.Length == 0)
        {
            return null;
        }

        // %F/%s of the descriptor, not of the name: same file we would go on to read.
        var stat = await RunGuardedAsync(mount, relative, "stat -L --printf '%F\\t%s' /proc/self/fd/3", cancellationToken);
        if (stat.ExitCode != 0)
        {
            return null;
        }

        var statColumns = stat.Stdout.Trim().Split('\t');
        if (statColumns.Length < 2 || statColumns[0].Contains("directory", StringComparison.Ordinal))
        {
            return null;
        }

        _ = long.TryParse(statColumns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);

        var read = await RunGuardedAsync(
            mount,
            relative,
            $"head -c {(MaxReadBytes + 1).ToString(CultureInfo.InvariantCulture)} <&3",
            cancellationToken);

        if (read.ExitCode != 0)
        {
            return null;
        }

        var truncated = read.Stdout.Length > MaxReadBytes;
        var content = truncated ? read.Stdout[..(int)MaxReadBytes] : read.Stdout;

        // A spreadsheet or a PDF rendered as decoded text is noise at best, and at
        // worst it looks like the file is corrupt. Say it is binary and let the
        // caller offer the download instead of a preview of mojibake. NUL means
        // binary outright; U+FFFD is what bytes that are not UTF-8 decode to.
        // `head -c` cuts at a byte, so a large text file can be severed mid-character
        // and the decoder renders that tail as U+FFFD. That is truncation, not a
        // binary file, so ignore replacement characters at the very end of a read
        // that was cut short.
        var scan = truncated || size > MaxReadBytes
            ? content.AsSpan().TrimEnd('�')
            : content.AsSpan();

        if (scan.Contains('\0') || scan.Contains('�'))
        {
            return new HomeFile(owner, relative, "", false, size, Binary: true);
        }

        return new HomeFile(owner, relative, content, truncated || size > MaxReadBytes, size);
    }

    public Task<HomeDownload?> DownloadAsync(string owner, string path, CancellationToken cancellationToken) =>
        DownloadInVolumeAsync(options.HomeVolumePrefix + owner, owner, path, cancellationToken);

    public Task<HomeDownload?> DownloadSharedAsync(string owner, string path, CancellationToken cancellationToken) =>
        DownloadInVolumeAsync(options.SharedVolumePrefix + owner, owner, path, cancellationToken);

    // Streams a file out of the volume verbatim: the same guarded host-side read as
    // the preview — no container is started and nothing runs inside the session
    // (design law 4) — but the bytes are handed over undecoded and uncapped, because
    // a download that truncates or re-encodes is not a download.
    private async Task<HomeDownload?> DownloadInVolumeAsync(string volume, string owner, string path, CancellationToken cancellationToken)
    {
        var mount = await MountpointAsync(volume, cancellationToken);
        if (mount is null)
        {
            return null;
        }

        var relative = HomePath.Sanitize(path);
        if (relative.Length == 0)
        {
            return null;
        }

        // Type and size for the response and the audit line. The authoritative
        // check is the guard on the streaming open below; if the file changes in
        // between, this size is merely stale, and Content-Length is not set from it.
        var stat = await RunGuardedAsync(mount, relative, "stat -L --printf '%F\\t%s' /proc/self/fd/3", cancellationToken);
        if (stat.ExitCode != 0)
        {
            return null;
        }

        var statColumns = stat.Stdout.Trim().Split('\t');
        if (statColumns.Length < 2 || statColumns[0].Contains("directory", StringComparison.Ordinal))
        {
            return null;
        }

        _ = long.TryParse(statColumns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);

        // The guard runs inside the streaming process, so a refusal happens before
        // any bytes exist. It writes one 0x01 byte first: reading that here is how
        // we learn the open passed while the response is still ours to refuse —
        // otherwise a rejected download would arrive as a silent empty file.
        var process = StartPodman(new[] { "unshare", "bash", "-c", Script("printf '\\001'\ncat <&3"), "cielo-download", mount, relative });
        if (process is null)
        {
            return null;
        }

        // Nobody reads stderr, and a full pipe would wedge `cat` mid-file.
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // A download may legitimately stream for a long time, so it cannot run under
        // a `timeout`; what is bounded instead is the wait for that first byte. If
        // the open blocks — a FIFO slipped in behind the type check — the marker
        // never arrives and this becomes a 404 rather than a stuck request.
        var stream = process.StandardOutput.BaseStream;
        var marker = new byte[1];
        int read;
        using var openWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        openWait.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            read = await stream.ReadAsync(marker, openWait.Token);
        }
        catch
        {
            read = 0;
        }

        if (read != 1 || marker[0] != 1)
        {
            using var rejected = new ProcessOutputStream(process);
            return null;
        }

        var name = relative.Split('/').Last();
        return new HomeDownload(owner, relative, name, HomeContentType.ForPath(relative), size, new ProcessOutputStream(process));
    }

    private async Task<string?> MountpointAsync(string volume, CancellationToken cancellationToken)
    {
        var inspect = await RunPodmanAsync(new[] { "volume", "inspect", volume, "--format", "{{.Mountpoint}}" }, cancellationToken);
        if (inspect.ExitCode != 0)
        {
            return null;
        }

        var mount = inspect.Stdout.Trim();
        if (mount.Length == 0)
        {
            return null;
        }

        // The guard compares the opened path against this prefix, so it has to be
        // the canonical one. Podman owns this path; nothing a session can reach.
        var canonical = await RunPodmanAsync(new[] { "unshare", "realpath", "-e", "--", mount }, cancellationToken);
        if (canonical.ExitCode != 0)
        {
            return null;
        }

        var real = canonical.Stdout.Trim();
        return real.Length == 0 ? null : real;
    }

    // A multi-line C# literal keeps the LINE ENDINGS OF ITS SOURCE FILE, so on a
    // checkout that uses CRLF this script reaches bash as `set -eu\r` — a syntax
    // error, and every listing, preview and download silently returns nothing.
    // Nothing in the tests catches it either, because a Linux checkout is LF: it
    // only appears when the binary is built from a Windows working tree. The
    // script is data for another program, so it is normalised where it is built
    // rather than left to depend on how git happened to write the file.
    private static string Script(string operation) =>
        (Guard + "\n" + operation).Replace("\r\n", "\n").Replace("\r", "\n");

    // `timeout` is the backstop for the one thing the guard cannot prevent by
    // itself: if the target turns into a FIFO between the type check and the open,
    // the open blocks forever. Listing and previewing are meant to be instant, so a
    // ceiling costs nothing and turns a hung request into a plain "not readable".
    private Task<(int ExitCode, string Stdout, string Stderr)> RunGuardedAsync(string mount, string relative, string operation, CancellationToken cancellationToken) =>
        RunPodmanAsync(new[] { "unshare", "timeout", "15", "bash", "-c", Script(operation), "cielo-browse", mount, relative }, cancellationToken);

    private Process? StartPodman(string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.PodmanPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = null
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            return process;
        }
        catch
        {
            process.Dispose();
            return null;
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunPodmanAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.PodmanPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return (127, "", exception.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}

// A read-only stream over a child process's stdout that owns the process. ASP.NET
// disposes the stream once the response is written; without this wrapper, a client
// that disconnects mid-download would leave the reader running with a full pipe.
internal sealed class ProcessOutputStream : Stream
{
    private readonly Process process;
    private readonly Stream inner;

    public ProcessOutputStream(Process process)
    {
        this.process = process;
        inner = process.StandardOutput.BaseStream;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Already gone, or gone between the check and the kill.
            }
            process.Dispose();
        }
        base.Dispose(disposing);
    }
}
