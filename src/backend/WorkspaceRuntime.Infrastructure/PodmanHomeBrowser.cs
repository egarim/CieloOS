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
        var target = relative.Length == 0 ? mount : $"{mount}/{relative}";

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

        var target = $"{mount}/{relative}";

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
        return new HomeFile(owner, relative, content, truncated || size > MaxReadBytes, size);
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
