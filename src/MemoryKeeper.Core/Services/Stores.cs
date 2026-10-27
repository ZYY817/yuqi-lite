using System.Text.Json;
using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Services;

/// <summary>所有持久化都落到 %LocalAppData%/MemoryKeeper/ 下的 JSON 文件。</summary>
public static class StorePaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MemoryKeeper");

    static StorePaths() => Directory.CreateDirectory(Root);

    public static string Whitelist => Path.Combine(Root, "whitelist.json");
    public static string WhitelistExcluded => Path.Combine(Root, "whitelist-excluded.json");
    public static string Snapshot => Path.Combine(Root, "snapshot.json");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Disabled => Path.Combine(Root, "disabled.json");
    public static string History => Path.Combine(Root, "history.json");
}

public sealed class WhitelistStore : IWhitelistStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly Dictionary<string, string> _entries;
    private readonly HashSet<string> _excluded;

    public WhitelistStore()
    {
        _entries = Load();
        _excluded = LoadExcluded();
    }

    public bool IsProtected(string appKey) => _entries.ContainsKey(appKey);
    public bool IsExcluded(string appKey) => _excluded.Contains(appKey);
    public IReadOnlyDictionary<string, string> GetAll() => _entries;

    public void Protect(string appKey, string displayName)
    {
        _entries[appKey] = displayName;
        if (_excluded.Remove(appKey)) PersistExcluded();
        Persist();
    }

    public void Unprotect(string appKey)
    {
        var removed = _entries.Remove(appKey);
        if (_excluded.Add(appKey)) PersistExcluded();
        if (removed) Persist();
    }

    public void ResetExclusions()
    {
        if (_excluded.Count == 0) return;
        _excluded.Clear();
        PersistExcluded();
    }

    private void Persist()
        => File.WriteAllText(StorePaths.Whitelist, JsonSerializer.Serialize(_entries, JsonOpts));

    private void PersistExcluded()
        => File.WriteAllText(StorePaths.WhitelistExcluded, JsonSerializer.Serialize(_excluded.ToList(), JsonOpts));

    private static HashSet<string> LoadExcluded()
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        if (!File.Exists(StorePaths.WhitelistExcluded))
            return new(comparer);
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePaths.WhitelistExcluded)) ?? [];
            return new HashSet<string>(list, comparer);
        }
        catch { return new(comparer); }
    }

    private static Dictionary<string, string> Load()
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        if (!File.Exists(StorePaths.Whitelist))
            return new(comparer);
        try
        {
            var text = File.ReadAllText(StorePaths.Whitelist);
            // 兼容旧格式：字符串数组
            if (text.TrimStart().StartsWith('['))
            {
                var keys = JsonSerializer.Deserialize<List<string>>(text) ?? [];
                return keys.ToDictionary(k => k, k => k, comparer);
            }
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new(comparer);
        }
        catch { return new(comparer); }
    }
}

public sealed class SnapshotStore : ISnapshotStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public void Save(IReadOnlyList<SnapshotEntry> entries)
        => File.WriteAllText(StorePaths.Snapshot, JsonSerializer.Serialize(entries, JsonOpts));

    public IReadOnlyList<SnapshotEntry> LoadLast()
        => File.Exists(StorePaths.Snapshot)
            ? JsonSerializer.Deserialize<List<SnapshotEntry>>(File.ReadAllText(StorePaths.Snapshot)) ?? []
            : [];

    public void Clear()
    {
        if (File.Exists(StorePaths.Snapshot)) File.Delete(StorePaths.Snapshot);
    }
}

public sealed class DisabledStore : IDisabledStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly List<DisabledAppEntry> _entries;

    public DisabledStore()
    {
        _entries = File.Exists(StorePaths.Disabled)
            ? JsonSerializer.Deserialize<List<DisabledAppEntry>>(File.ReadAllText(StorePaths.Disabled)) ?? []
            : [];
    }

    public bool IsDisabled(string appKey)
        => _entries.Any(e => e.AppKey.Equals(appKey, StringComparison.OrdinalIgnoreCase));

    public DisableScope? GetScope(string appKey)
        => _entries.FirstOrDefault(e => e.AppKey.Equals(appKey, StringComparison.OrdinalIgnoreCase))?.Scope;

    public void Add(DisabledAppEntry entry)
    {
        _entries.RemoveAll(e => e.AppKey.Equals(entry.AppKey, StringComparison.OrdinalIgnoreCase));
        _entries.Add(entry);
        Persist();
    }

    public void Remove(string appKey)
    {
        if (_entries.RemoveAll(e => e.AppKey.Equals(appKey, StringComparison.OrdinalIgnoreCase)) > 0)
            Persist();
    }

    public IReadOnlyList<DisabledAppEntry> GetAll() => _entries;

    private void Persist()
        => File.WriteAllText(StorePaths.Disabled, JsonSerializer.Serialize(_entries, JsonOpts));
}

public sealed class HistoryStore : IHistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly List<HistoryRecord> _records;
    private const int MaxRecords = 200;

    public HistoryStore()
    {
        try
        {
            _records = File.Exists(StorePaths.History)
                ? JsonSerializer.Deserialize<List<HistoryRecord>>(File.ReadAllText(StorePaths.History)) ?? []
                : [];
        }
        catch { _records = []; }
    }

    public void Add(HistoryRecord record)
    {
        _records.Insert(0, record);
        if (_records.Count > MaxRecords)
            _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);
        Persist();
    }

    public void MarkRestored(string appKey, string action)
    {
        foreach (var r in _records.Where(r =>
            r.AppKey.Equals(appKey, StringComparison.OrdinalIgnoreCase) && r.Action == action && !r.Restored))
            r.Restored = true;
        Persist();
    }

    public IReadOnlyList<HistoryRecord> GetAll() => _records;

    public void Clear()
    {
        _records.Clear();
        Persist();
    }

    private void Persist()
        => File.WriteAllText(StorePaths.History, JsonSerializer.Serialize(_records, JsonOpts));
}

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private Dictionary<string, string> _values;

    public SettingsStore()
    {
        _values = File.Exists(StorePaths.Settings)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePaths.Settings)) ?? new()
            : new();
    }

    public string? Get(string module, string key) => _values.GetValueOrDefault($"{module}:{key}");

    public void Set(string module, string key, string value)
    {
        _values[$"{module}:{key}"] = value;
        File.WriteAllText(StorePaths.Settings, JsonSerializer.Serialize(_values, JsonOpts));
    }
}
