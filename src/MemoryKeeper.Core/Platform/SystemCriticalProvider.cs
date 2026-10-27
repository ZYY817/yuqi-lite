using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Platform;

/// <summary>
/// 枚举当前正在运行的系统关键进程：只取真实在跑、且命中系统关键名单的进程，
/// 同名多实例（如 svchost）合并为一行展示合计内存。
/// </summary>
public sealed class SystemCriticalProvider : ISystemCriticalProvider
{
    private readonly IProcessProvider _procs;

    public SystemCriticalProvider(IProcessProvider procs) => _procs = procs;

    public IReadOnlyList<AppProcessInfo> GetRunningCritical()
        => _procs.GetProcesses()
            .Where(p => SystemProcessNames.Contains(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var items = g.ToList();
                var first = items.OrderByDescending(p => p.MemoryBytes).First();
                return first with
                {
                    // 同名系统进程多实例时合并显示合计内存
                    MemoryBytes = items.Sum(p => p.MemoryBytes),
                };
            })
            .OrderByDescending(p => p.MemoryBytes)
            .ToList();
}
