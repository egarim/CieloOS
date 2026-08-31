using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

// The real backing for #18: snapshots the owner's home podman volume and can put
// it back. Mirrors PodmanHomeBrowser's host-side access — `podman volume inspect`
// for the mountpoint and `podman unshare` to read the rootless-written files in
// the volume. The snapshot is a tar of the volume under a content-addressed file
// per owner, with a small JSON sidecar so the history reads as actions.
public sealed class PodmanVersionStore : IVersionStore
{
    private readonly SessionBackendOptions options;
    private readonly string snapshotRoot;

    public PodmanVersionStore(SessionBackendOptions options, string snapshotRoot)
    {
        this.options = options;
        this.snapshotRoot = snapshotRoot;
    }

    public async Task<VersionSnapshot> RecordBeforeAsync(string ownerSlug, Guid correlationId, string action, CancellationToken cancellationToken)
    {
        var snapshotId = Guid.NewGuid();
        var volume = options.HomeVolumePrefix + ownerSlug;
        var mount = await MountpointAsync(volume, cancellationToken);
        if (mount is null)
        {
            // No home volume yet (nothing to snapshot); record the intent anyway so
            // the ledger is complete and the action's correlationId stays attributed.
            return new VersionSnapshot(snapshotId, ownerSlug, correlationId, action, $"before {action}", DateTimeOffset.UtcNow);
        }

        var ownerRoot = Path.Combine(snapshotRoot, ownerSlug);
        Directory.CreateDirectory(ownerRoot);
        var archive = Path.Combine(ownerRoot, $"{snapshotId}.tar.gz").Replace("\\", "/");
        var mountPath = Normalize(mount);

        var result = await RunPodmanAsync(new[] { "unshare", "tar", "-czf", archive, "-C", mountPath, "." }, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Home snapshot failed for '{ownerSlug}': {result.Stderr}");
        }

        var createdAt = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(ownerRoot, $"{snapshotId}.json"),
            JsonSerializer.Serialize(new SnapshotMeta(snapshotId, ownerSlug, correlationId, action, createdAt, archive)));
        return new VersionSnapshot(snapshotId, ownerSlug, correlationId, action, $"before {action}", createdAt);
    }

    public Task<IReadOnlyList<VersionSnapshot>> ListAsync(string ownerSlug, CancellationToken cancellationToken)
    {
        var ownerRoot = Path.Combine(snapshotRoot, ownerSlug);
        var list = new List<VersionSnapshot>();
        if (Directory.Exists(ownerRoot))
        {
            foreach (var metaFile in Directory.EnumerateFiles(ownerRoot, "*.json"))
            {
                if (ReadMetadata(metaFile) is { } snapshot)
                {
                    list.Add(snapshot);
                }
            }
        }
        return Task.FromResult<IReadOnlyList<VersionSnapshot>>(list.OrderByDescending(s => s.CreatedAt).ToList());
    }

    public async Task<bool> RestoreAsync(string ownerSlug, Guid snapshotId, CancellationToken cancellationToken)
    {
        var volume = options.HomeVolumePrefix + ownerSlug;
        var mount = await MountpointAsync(volume, cancellationToken);
        if (mount is null)
        {
            return false;
        }

        var ownerRoot = Path.Combine(snapshotRoot, ownerSlug);
        var archive = Path.Combine(ownerRoot, $"{snapshotId}.tar.gz");
        if (!File.Exists(archive))
        {
            return false;
        }

        var result = await RunPodmanAsync(new[] { "unshare", "tar", "-xzf", archive.Replace("\\", "/"), "-C", Normalize(mount) }, cancellationToken);
        return result.ExitCode == 0;
    }

    private async Task<string?> MountpointAsync(string volume, CancellationToken cancellationToken)
    {
        var result = await RunPodmanAsync(new[] { "volume", "inspect", "--format", "{{.Mountpoint}}", volume }, cancellationToken);
        return result.ExitCode == 0 ? result.Stdout.Trim() : null;
    }

    private static string Normalize(string path) => path.Replace("\\", "/");

    private sealed record SnapshotMeta(Guid Id, string OwnerSlug, Guid CorrelationId, string Action, DateTimeOffset CreatedAt, string Archive);

    private static VersionSnapshot? ReadMetadata(string path)
    {
        try
        {
            var meta = JsonSerializer.Deserialize<SnapshotMeta>(File.ReadAllText(path));
            return meta is null ? null : new VersionSnapshot(meta.Id, meta.OwnerSlug, meta.CorrelationId, meta.Action, $"before {meta.Action}", meta.CreatedAt);
        }
        catch
        {
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
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
