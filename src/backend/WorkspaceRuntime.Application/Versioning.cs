using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Application;

// One recoverable point in an owner's home: the state immediately before a
// non-reversible agent action, tagged with that action's correlationId so a
// person can see "before <action> <time>" rather than a timestamped snapshot.
public sealed record VersionSnapshot(
    Guid Id,
    string OwnerSlug,
    Guid CorrelationId,
    string Action,
    string Label,
    DateTimeOffset CreatedAt);

// The version boundary of the OS is the agent action, not a commit. Recording a
// snapshot before a non-reversible action is what makes the action's
// consequences recoverable; the history reads as actions. The default is a
// no-op (see NullVersionStore) so a runtime without versioning still works.
public interface IVersionStore
{
    // Snapshot the owner's home NOW, tagged with the action that is about to run.
    Task<VersionSnapshot> RecordBeforeAsync(string ownerSlug, Guid correlationId, string action, CancellationToken cancellationToken);

    Task<IReadOnlyList<VersionSnapshot>> ListAsync(string ownerSlug, CancellationToken cancellationToken);

    // Restore the home to the captured state. False if the snapshot is unknown.
    Task<bool> RestoreAsync(string ownerSlug, Guid snapshotId, CancellationToken cancellationToken);
}

// A version store that records the ledger but captures nothing. For unit tests
// and for a box where the home is not a podman volume the runtime can inspect.
public sealed class InMemoryVersionStore : IVersionStore
{
    private readonly Dictionary<string, List<VersionSnapshot>> snapshots = new(StringComparer.Ordinal);

    public Task<VersionSnapshot> RecordBeforeAsync(string ownerSlug, Guid correlationId, string action, CancellationToken cancellationToken)
    {
        var snapshot = new VersionSnapshot(Guid.NewGuid(), ownerSlug, correlationId, action, $"before {action}", DateTimeOffset.UtcNow);
        if (!snapshots.TryGetValue(ownerSlug, out var list))
        {
            list = new List<VersionSnapshot>();
            snapshots[ownerSlug] = list;
        }
        list.Add(snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<VersionSnapshot>> ListAsync(string ownerSlug, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VersionSnapshot>>(
            snapshots.TryGetValue(ownerSlug, out var list)
                ? list.OrderByDescending(s => s.CreatedAt).ToList()
                : Array.Empty<VersionSnapshot>());

    public Task<bool> RestoreAsync(string ownerSlug, Guid snapshotId, CancellationToken cancellationToken)
    {
        // Nothing is captured, so there is nothing to restore; but the id is
        // recorded, so answer true to mirror what a real store would do.
        if (snapshots.TryGetValue(ownerSlug, out var list))
        {
            return Task.FromResult(list.Any(s => s.Id == snapshotId));
        }
        return Task.FromResult(false);
    }
}

// The no-op default used when versioning is not configured, so an absent
// IVersionStore never changes how a command runs.
public sealed class NullVersionStore : IVersionStore
{
    public Task<VersionSnapshot> RecordBeforeAsync(string ownerSlug, Guid correlationId, string action, CancellationToken cancellationToken) =>
        Task.FromResult(new VersionSnapshot(Guid.NewGuid(), ownerSlug, correlationId, action, $"before {action}", DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<VersionSnapshot>> ListAsync(string ownerSlug, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VersionSnapshot>>(Array.Empty<VersionSnapshot>());

    public Task<bool> RestoreAsync(string ownerSlug, Guid snapshotId, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

// The undo boundary is the agent action, so the snapshot is taken immediately
// before a NON-reversible action — the moment after which the action could not
// be undone by itself. A reversible action (a spreadsheet cell write) needs no
// checkpoint.
public static class UndoPolicy
{
    public static bool ShouldSnapshot(SurfaceCommandSpec? command) =>
        command is not null && !command.Reversible;
}
