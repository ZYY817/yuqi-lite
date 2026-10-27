using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Rules;

/// <summary>从内嵌资源 apps.json 加载的词典。后续可换成在线规则源。</summary>
public sealed class JsonRuleStore : IRuleStore
{
    private readonly List<RuleEntry> _entries;
    private readonly Dictionary<string, List<RuleEntry>> _byName;

    public JsonRuleStore()
    {
        _entries = LoadEntries();
        _byName = _entries
            .SelectMany(e => e.ProcessNames.Select(n => (name: n.ToLowerInvariant(), entry: e)))
            .GroupBy(t => t.name)
            .ToDictionary(g => g.Key, g => g.Select(t => t.entry).ToList());
    }

    public IReadOnlyList<RuleEntry> GetAll() => _entries;

    public RuleEntry? Match(string processName, string? installDir)
    {
        if (!_byName.TryGetValue(processName.ToLowerInvariant(), out var candidates))
            return null;

        // 带 PathHint 的规则要求目录也匹配；无 PathHint 的直接命中
        var hinted = candidates.FirstOrDefault(e =>
            e.PathHint is not null
            && installDir?.Contains(e.PathHint, StringComparison.OrdinalIgnoreCase) == true);
        return hinted ?? candidates.FirstOrDefault(e => e.PathHint is null);
    }

    private static List<RuleEntry> LoadEntries()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("apps.json", StringComparison.OrdinalIgnoreCase));
        if (resource is null) return [];

        using var stream = asm.GetManifestResourceStream(resource)!;
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        return JsonSerializer.Deserialize<List<RuleEntry>>(stream, opts) ?? [];
    }
}
