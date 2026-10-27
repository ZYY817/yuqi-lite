using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Services;

/// <summary>扫描门面：枚举 → 归组 → 分类管线 → 应用白名单 → 排序输出。</summary>
public sealed class AppCatalog : IAppCatalog
{
    private readonly IProcessProvider _processes;
    private readonly IAppGrouper _grouper;
    private readonly IReadOnlyList<IAppClassifier> _pipeline;
    private readonly IWhitelistStore _whitelist;
    private readonly IStartupProvider _startup;
    private readonly IDisabledStore _disabled;
    private readonly ISettingsStore _settings;

    public AppCatalog(
        IProcessProvider processes,
        IAppGrouper grouper,
        IEnumerable<IAppClassifier> pipeline,
        IWhitelistStore whitelist,
        IStartupProvider startup,
        IDisabledStore disabled,
        ISettingsStore settings)
    {
        _processes = processes;
        _grouper = grouper;
        _pipeline = pipeline.ToList();
        _whitelist = whitelist;
        _startup = startup;
        _disabled = disabled;
        _settings = settings;
    }

    public IReadOnlyList<AppGroup> Scan()
    {
        var selfPid = Environment.ProcessId;
        var groups = _grouper.Group(_processes.GetProcesses())
            .Where(g => g.Processes.All(p => p.Pid != selfPid))
            .ToList();

        foreach (var g in groups)
        foreach (var classifier in _pipeline)
            if (classifier.CanHandle(g))
                classifier.Classify(g);

        groups = MergeByIdentity(groups);

        // 自动保护开关关闭时，或某应用被用户显式移除了保护，撤销默认保护
        var autoProtect = _settings.Get("whitelist", "autoProtect") != "0";
        foreach (var g in groups.Where(g => !_whitelist.IsProtected(g.Key) && g.IsProtected))
        {
            if (!autoProtect || _whitelist.IsExcluded(g.Key) || (g.MatchedRuleId is not null && _whitelist.IsExcluded(g.MatchedRuleId)))
            {
                g.IsProtected = false;
                if (g.Level == SafetyLevel.Protected)
                    g.Level = SafetyLevel.Unknown;
            }
        }

        // 白名单优先于词典建议
        foreach (var g in groups.Where(g => _whitelist.IsProtected(g.Key)))
        {
            g.IsProtected = true;
            if (g.Level is SafetyLevel.SafeToClose or SafetyLevel.Unknown)
                g.Level = SafetyLevel.Protected;
        }

        // 填充自启动入口（按 exe 路径匹配到应用组）
        var startupEntries = _startup.GetAll();
        foreach (var g in groups)
        {
            var paths = g.Processes.Select(p => p.Path)
                .Where(p => p is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            g.StartupEntries = startupEntries
                .Where(e => e.ExePath is not null &&
                    (paths.Contains(e.ExePath) ||
                     (g.InstallDir is not null && e.ExePath.StartsWith(g.InstallDir, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }

        return groups
            .Where(g => g.Level != SafetyLevel.System)
            .OrderBy(g => g.Level switch
            {
                SafetyLevel.SafeToClose => 0,
                SafetyLevel.Unknown => 1,
                _ => 2,
            })
            .ThenByDescending(g => g.TotalMemoryBytes)
            .ToList();
    }

    /// <summary>合并命中同一词典规则或同名同级的应用组，避免同一软件因多子目录重复出现。</summary>
    private static List<AppGroup> MergeByIdentity(List<AppGroup> groups)
    {
        var merged = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            if (g.Level == SafetyLevel.System)
                continue;
            var id = g.MatchedRuleId is not null
                ? $"rule:{g.MatchedRuleId}"
                : $"name:{g.DisplayName}";

            if (!merged.TryGetValue(id, out var existing))
            {
                merged[id] = g;
            }
            else
            {
                var combined = existing.Processes.Concat(g.Processes).ToList();
                existing.Processes = combined;
                if (g.IsProtected) existing.IsProtected = true;
                if (g.Level == SafetyLevel.SafeToClose && !existing.IsProtected)
                    existing.Level = SafetyLevel.SafeToClose;
                existing.InstallDir ??= g.InstallDir;
                existing.Publisher ??= g.Publisher;
            }
        }
        return merged.Values.ToList();
    }
}
