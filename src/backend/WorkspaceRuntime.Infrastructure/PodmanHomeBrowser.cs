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
        var target = await ResolveInsideAsync(mount, relative, cancellationToken);
        if (target is null)
        {
            return new HomeListing(owner, relative, Array.Empty<HomeEntry>());
        }

        var find = await RunPodmanAsync(new[]
        {
            "unshare", "find", target,
            "-maxdepth", "1", "-mindepth", "1",
            "-printf", "%y\t%s\t%T@\t%f\n"
        }, cancellationToken);

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

            var kind = columns[0] switch { "d" => "directory", "l" => "link", _ => "file" };
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

        var target = await ResolveInsideAsync(mount, relative, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var stat = await RunPodmanAsync(new[] { "unshare", "stat", "-c", "%F\t%s", target }, cancellationToken);
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

        var read = await RunPodmanAsync(new[]
        {
            "unshare", "head", "-c", (MaxReadBytes + 1).ToString(CultureInfo.InvariantCulture), target
        }, cancellationToken);

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
        if (content.Contains('\0') || content.Contains('�'))
        {
            return new HomeFile(owner, relative, "", false, size, Binary: true);
        }

        return new HomeFile(owner, relative, content, truncated || size > MaxReadBytes, size);
    }

    public Task<HomeDownload?> DownloadAsync(string owner, string path, CancellationToken cancellationToken) =>
        DownloadInVolumeAsync(options.HomeVolumePrefix + owner, owner, path, cancellationToken);

    public Task<HomeDownload?> DownloadSharedAsync(string owner, string path, CancellationToken cancellationToken) =>
        DownloadInVolumeAsync(options.SharedVolumePrefix + owner, owner, path, cancellationToken);

    // Streams a file out of the volume verbatim. `cat` under `podman unshare` is
    // the same host-side read path as the preview — no container is started and
    // nothing runs inside the session (design law 4) — but the bytes are handed
    // to the caller undecoded and uncapped, because a download that truncates or
    // re-encodes is not a download.
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

        var target = await ResolveInsideAsync(mount, relative, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var stat = await RunPodmanAsync(new[] { "unshare", "stat", "-c", "%F\t%s", target }, cancellationToken);
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

        var startInfo = new ProcessStartInfo
        {
            FileName = options.PodmanPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("unshare");
        startInfo.ArgumentList.Add("cat");
        startInfo.ArgumentList.Add(target);

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            return null;
        }

        // Nobody reads stderr here, and a full pipe would wedge `cat` mid-file.
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var name = relative.Split('/').Last();
        return new HomeDownload(owner, relative, name, HomeContentType.ForPath(relative), size, new ProcessOutputStream(process));
    }

    // Turns a caller-supplied relative path into a real path that is provably
    // inside the volume, or null. Sanitize() stops "..", but that is only half the
    // problem: a session owns its home and can drop a symlink to /etc in it, and
    // both stat and cat follow symlinks — so without resolving first, an entirely
    // authorized read walks straight out of the volume. Resolve both ends and
    // require the target to be the mount or beneath it.
    private async Task<string?> ResolveInsideAsync(string mount, string relative, CancellationToken cancellationToken)
    {
        var mountReal = await RealPathAsync(mount, cancellationToken);
        if (mountReal is null)
        {
            return null;
        }

        var targetReal = relative.Length == 0
            ? mountReal
            : await RealPathAsync($"{mountReal}/{relative}", cancellationToken);

        return targetReal is not null
            && (targetReal == mountReal || targetReal.StartsWith(mountReal + "/", StringComparison.Ordinal))
                ? targetReal
                : null;
    }

    private async Task<string?> RealPathAsync(string path, CancellationToken cancellationToken)
    {
        var resolved = await RunPodmanAsync(new[] { "unshare", "realpath", "-e", "--", path }, cancellationToken);
        if (resolved.ExitCode != 0)
        {
            return null;
        }

        var real = resolved.Stdout.Trim();
        return real.Length == 0 ? null : real;
    }

    private async Task<string?> MountpointAsync(string volume, CancellationToken cancellationToken)
    {
        var inspect = await RunPodmanAsync(new[] { "volume", "inspect", volume, "--format", "{{.Mountpoint}}" }, cancellationToken);
        if (inspect.ExitCode != 0)
        {
            return null;
        }

        var mount = inspect.Stdout.Trim();
        return string.IsNullOrEmpty(mount) ? null : mount;
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
// that disconnects mid-download would leave `podman unshare cat` running with a
// full pipe forever.
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
