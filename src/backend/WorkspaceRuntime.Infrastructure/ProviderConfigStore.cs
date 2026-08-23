using System.Text;
using System.Text.Json;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

// The mutable provider layer, persisted to <secrets>/providers.json (0600 — it
// holds API keys). Runtime-added providers and OS-default overrides live here; the
// ModelRegistry reads it live, so a provider added from the panel is usable with
// no restart. Startup/config providers are NOT stored here — they stay immutable.
public sealed class ProviderConfigStore : IProviderConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string path;
    private readonly object gate = new();
    private FileShape state;

    public ProviderConfigStore(string secretsDirectory)
    {
        Directory.CreateDirectory(secretsDirectory);
        path = Path.Combine(secretsDirectory, "providers.json");
        state = Load(path);
    }

    public IReadOnlyList<ProviderProfile> All()
    {
        lock (gate)
        {
            return state.Providers.Select(ToProfile).ToArray();
        }
    }

    public ProviderProfile Add(ProviderDraft draft)
    {
        lock (gate)
        {
            var id = UniqueId(draft.DisplayName);
            var record = new ProviderRecord
            {
                Id = id,
                DisplayName = draft.DisplayName,
                Kind = draft.Kind,
                BaseUrl = draft.BaseUrl,
                Model = draft.Model,
                Capabilities = draft.Capabilities.ToList(),
                Locality = draft.Locality,
                ApiKey = draft.ApiKey
            };
            state.Providers.Add(record);
            Persist();
            return ToProfile(record);
        }
    }

    public bool Remove(string id)
    {
        lock (gate)
        {
            var removed = state.Providers.RemoveAll(record => string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed)
            {
                return false;
            }

            // Drop any OS default that pointed at the removed provider, so the
            // registry falls back cleanly instead of resolving a ghost id.
            foreach (var capability in state.OsDefaults.Where(pair => string.Equals(pair.Value, id, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToList())
            {
                state.OsDefaults.Remove(capability);
            }

            Persist();
            return true;
        }
    }

    public IReadOnlyDictionary<string, string> OsDefaults()
    {
        lock (gate)
        {
            return new Dictionary<string, string>(state.OsDefaults, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetOsDefault(string capability, string providerId)
    {
        lock (gate)
        {
            state.OsDefaults[capability] = providerId;
            Persist();
        }
    }

    private string UniqueId(string displayName)
    {
        var baseId = Slug.Of(displayName);
        if (baseId.Length == 0)
        {
            baseId = "provider";
        }

        var candidate = baseId;
        var suffix = 2;
        while (state.Providers.Any(record => string.Equals(record.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseId}-{suffix++}";
        }
        return candidate;
    }

    private void Persist()
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, JsonOptions)));
    }

    private static FileShape Load(string path)
    {
        if (!File.Exists(path))
        {
            return new FileShape();
        }

        try
        {
            return JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), JsonOptions) ?? new FileShape();
        }
        catch (JsonException)
        {
            // A corrupt file must not take the runtime down; start from empty
            // (the operator can re-add providers). The bad file is left in place.
            return new FileShape();
        }
    }

    private static ProviderProfile ToProfile(ProviderRecord record) => new(
        record.Id,
        record.DisplayName,
        record.Kind,
        record.BaseUrl,
        record.Model,
        new HashSet<string>(record.Capabilities, StringComparer.OrdinalIgnoreCase),
        record.Locality,
        record.ApiKey);

    private sealed class FileShape
    {
        public List<ProviderRecord> Providers { get; set; } = new();
        public Dictionary<string, string> OsDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProviderRecord
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Kind { get; set; } = "openai-compatible";
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public List<string> Capabilities { get; set; } = new();
        public string Locality { get; set; } = "cloud";
        public string? ApiKey { get; set; }
    }
}
