using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Pipeline;

/// <summary>
/// 把进程归并成"应用"。
/// V1 策略：同安装目录归为一组；无路径进程按名归组。
/// 后续可扩展：签名发布者聚合、父子进程树合并。
/// </summary>
public sealed class AppGrouper : IAppGrouper
{
    public IReadOnlyList<AppGroup> Group(IReadOnlyList<AppProcessInfo> processes)
    {
        var groups = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in processes)
        {
            var dir = GetDir(p.Path);
            var key = dir ?? p.Name;

            if (!groups.TryGetValue(key, out var g))
            {
                g = new AppGroup
                {
                    Key = p.Name.ToLowerInvariant(),
                    InstallDir = dir,
                    Processes = new List<AppProcessInfo>(),
                    DisplayName = p.Name,
                };
                groups[key] = g;
            }
            ((List<AppProcessInfo>)g.Processes).Add(p);
        }

        foreach (var g in groups.Values)
            g.Key = PickPrimaryName(g).ToLowerInvariant();

        return groups.Values.ToList();
    }

    private static string? GetDir(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }

    /// <summary>组内"代表性"进程名：优先与目录末段同名的，否则取内存最大者。</summary>
    private static string PickPrimaryName(AppGroup g)
    {
        var dirName = g.InstallDir?.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (dirName is not null)
        {
            var match = g.Processes.FirstOrDefault(p =>
                p.Name.Equals(dirName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Name;
        }
        return g.Processes.MaxBy(p => p.MemoryBytes)?.Name ?? g.Processes[0].Name;
    }
}
