using System.ComponentModel;
using System.Diagnostics;
using MemoryKeeper.Contracts;
using MemoryKeeper.Core.Platform;

namespace MemoryKeeper.Core.Services;

/// <summary>
/// 真实分步清理与验证引擎：
/// 检查运行状态 → 核实保护规则 → 请求正常关闭/结束后台进程 → 等待并重新核查系统进程 → 区分已关闭/重新拉起/关联残留/权限不足。
/// </summary>
public sealed class CleanupExecutionService : ICleanupExecutionService
{
    private readonly ISystemMemoryService _memory;
    private readonly IWhitelistStore _whitelist;
    private readonly ISnapshotStore _snapshots;
    private readonly IHistoryStore _history;
    private readonly IEventBus _bus;

    public CleanupExecutionService(
        ISystemMemoryService memory,
        IWhitelistStore whitelist,
        ISnapshotStore snapshots,
        IHistoryStore history,
        IEventBus bus)
    {
        _memory = memory;
        _whitelist = whitelist;
        _snapshots = snapshots;
        _history = history;
        _bus = bus;
    }

    public async Task<CleanupBatchReport> ExecuteBatchAsync(
        IReadOnlyList<AppGroup> targets,
        bool allowForceKillWindowless,
        IProgress<(int CompletedCount, int TotalCount, CleanupItemReport Latest)>? progress,
        CancellationToken ct = default)
    {
        var beforeMem = _memory.GetSnapshot();
        var reports = new List<CleanupItemReport>(targets.Count);
        var snapshotEntries = new List<SnapshotEntry>();
        var total = targets.Count;
        var completed = 0;
        var wasCancelled = false;

        foreach (var group in targets)
        {
            var iconPath = group.Processes.FirstOrDefault(p => p.Path is not null)?.Path;
            var wsBytes = group.TotalMemoryBytes;

            if (ct.IsCancellationRequested)
            {
                wasCancelled = true;
                var cancelledReport = new CleanupItemReport(
                    group.Key, group.DisplayName, iconPath, wsBytes,
                    CleanupItemStatus.Cancelled, "已取消", "用户取消了后续清理任务", 0, group.Processes.Count);
                reports.Add(cancelledReport);
                completed++;
                progress?.Report((completed, total, cancelledReport));
                continue;
            }

            // 1. 重新核实安全与保护规则
            if (group.Level == SafetyLevel.System
                || group.Processes.Any(p => SystemProcessNames.Contains(p.Name))
                || _whitelist.IsProtected(group.Key))
            {
                var skipped = new CleanupItemReport(
                    group.Key, group.DisplayName, iconPath, wsBytes,
                    CleanupItemStatus.SkippedProtected, "已跳过", "应用受到保护规则限制，已跳过", 0, group.Processes.Count);
                reports.Add(skipped);
                completed++;
                progress?.Report((completed, total, skipped));
                continue;
            }

            // 报告"正在请求关闭..."
            progress?.Report((completed, total, new CleanupItemReport(
                group.Key, group.DisplayName, iconPath, wsBytes,
                CleanupItemStatus.RequestingClose, "正在结束...", null, 0, group.Processes.Count)));

            var itemReport = await Task.Run(() => ProcessSingleApp(group, iconPath, wsBytes, allowForceKillWindowless, snapshotEntries), ct)
                .ConfigureAwait(false);

            reports.Add(itemReport);
            completed++;
            progress?.Report((completed, total, itemReport));
        }

        if (snapshotEntries.Count > 0)
        {
            _snapshots.Save(snapshotEntries);
            _bus.Publish(new AppsTerminatedMessage(snapshotEntries));
        }

        var afterMem = _memory.GetSnapshot();
        var now = DateTime.Now;

        foreach (var r in reports)
        {
            if (r.Status is CleanupItemStatus.Cancelled or CleanupItemStatus.SkippedProtected)
                continue;

            var ok = r.Status is CleanupItemStatus.Closed or CleanupItemStatus.AlreadyExited;
            _history.Add(new HistoryRecord
            {
                Time = now,
                AppKey = r.AppKey,
                DisplayName = r.DisplayName,
                Action = ok ? "terminated" : "failed",
                Detail = ok ? $"已关闭后台进程（进程工作集 {FormatMb(r.WorkingSetBytes)}）" : (r.ReasonDetail ?? r.StatusText),
                FailureReason = ok ? null : r.ReasonDetail,
                MemoryBytes = r.WorkingSetBytes,
                NeedsAttention = !ok,
                Restorable = ok && !string.IsNullOrEmpty(r.IconPath),
                RestorePath = r.IconPath,
            });
        }

        _bus.Publish(new AppsChangedMessage());
        return new CleanupBatchReport(now, beforeMem, afterMem, reports, wasCancelled);
    }

    private static CleanupItemReport ProcessSingleApp(
        AppGroup group,
        string? iconPath,
        long wsBytes,
        bool allowForceKillWindowless,
        List<SnapshotEntry> snapshotEntries)
    {
        var targetPids = group.Processes.Select(p => p.Pid).ToHashSet();
        var procNames = group.Processes.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 检查目标进程是否仍在运行
        var aliveProcs = new List<Process>();
        foreach (var pid in targetPids)
        {
            Process? p = null;
            try
            {
                p = Process.GetProcessById(pid);
                if (p.HasExited) { p.Dispose(); continue; }
                aliveProcs.Add(p);
            }
            catch (ArgumentException) { /* 进程确实已退出 */ }
            catch
            {
                // 查询被拒（权限不足）：进程存在但状态未知，仍按"存活"处理并尝试结束，
                // 由后续 Kill 的结果如实上报权限不足，而不是误报"已自行退出"
                if (p is not null) aliveProcs.Add(p);
            }
        }

        if (aliveProcs.Count == 0)
        {
            return new CleanupItemReport(
                group.Key, group.DisplayName, iconPath, wsBytes,
                CleanupItemStatus.AlreadyExited, "已退出", "目标进程在清理前已自行退出", group.Processes.Count, 0);
        }

        // 记录快照供后续需要时手动启动
        foreach (var pInfo in group.Processes.Where(x => x.Path is not null))
        {
            lock (snapshotEntries)
            {
                snapshotEntries.Add(new SnapshotEntry(group.Key, group.DisplayName, pInfo.Path!, pInfo.Arguments));
            }
        }

        bool accessDenied = false;
        bool windowCloseTimeout = false;

        foreach (var proc in aliveProcs)
        {
            try
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    // 有可见窗口的应用：优先发送正常关闭消息，避免强制杀死导致未保存数据丢失
                    if (proc.CloseMainWindow())
                    {
                        if (!proc.WaitForExit(800))
                        {
                            if (allowForceKillWindowless)
                                proc.Kill(entireProcessTree: false);
                            else
                                windowCloseTimeout = true;
                        }
                    }
                    else if (allowForceKillWindowless)
                    {
                        proc.Kill(entireProcessTree: false);
                    }
                    else
                    {
                        windowCloseTimeout = true;
                    }
                }
                else
                {
                    // 无窗口的后台进程：结束进程，给足退出时间避免误报"仍在运行"
                    proc.Kill(entireProcessTree: false);
                    proc.WaitForExit(1200);
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                accessDenied = true;
            }
            catch (InvalidOperationException) { /* 已退出 */ }
            catch { accessDenied = true; }
            finally
            {
                proc.Dispose();
            }
        }

        // 等待短暂时间后重新核验系统真实状态
        Thread.Sleep(450);

        int stillRunningOriginal = 0;
        foreach (var pid in targetPids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited) stillRunningOriginal++;
            }
            catch (ArgumentException)
            {
                // 进程确实已不存在（GetProcessById 只在进程消失时抛此异常）
            }
            catch
            {
                // 查询被拒（服务/SYSTEM 进程权限不足）≠ 已退出：
                // 失败一律按"仍在运行"处理，绝不能误报为已关闭
                stillRunningOriginal++;
                accessDenied = true;
            }
        }

        // 检查是否被守护服务以新 PID 重新拉起
        int respawnedCount = 0;
        if (stillRunningOriginal == 0)
        {
            foreach (var name in procNames)
            {
                try
                {
                    var currentByName = Process.GetProcessesByName(name);
                    foreach (var p in currentByName)
                    {
                        if (!targetPids.Contains(p.Id))
                            respawnedCount++;
                        p.Dispose();
                    }
                }
                catch { }
            }
        }

        int closedCount = Math.Max(0, aliveProcs.Count - stillRunningOriginal);

        if (stillRunningOriginal == 0 && respawnedCount == 0)
        {
            return new CleanupItemReport(
                group.Key, group.DisplayName, iconPath, wsBytes,
                CleanupItemStatus.Closed, "已关闭", "关联后台进程已全部退出", closedCount, 0);
        }

        if (respawnedCount > 0)
        {
            return new CleanupItemReport(
                group.Key, group.DisplayName, iconPath, wsBytes,
                CleanupItemStatus.FailedRespawned, "被重新拉起",
                "结束后被系统服务或守护组件重新拉起，可加入模式拦截或管理启动项", closedCount, respawnedCount);
        }

        if (accessDenied)
        {
            return new CleanupItemReport(
                group.Key, group.DisplayName, iconPath, wsBytes,
                CleanupItemStatus.FailedAccessDenied, "权限不足",
                "进程以更高权限运行（Windows 服务/管理员），需以管理员身份重启本软件后才能结束", closedCount, stillRunningOriginal);
        }

        if (windowCloseTimeout)
        {
            return new CleanupItemReport(
                group.Key, group.DisplayName, iconPath, wsBytes,
                CleanupItemStatus.FailedTimeout, "等待窗口确认",
                "关闭请求超时，应用可能存在未保存内容，未执行强制终止", closedCount, stillRunningOriginal);
        }

        return new CleanupItemReport(
            group.Key, group.DisplayName, iconPath, wsBytes,
            CleanupItemStatus.FailedAssociatedRunning, "未完全关闭",
            $"存在 {stillRunningOriginal} 个关联进程仍在运行，当前未完全关闭", closedCount, stillRunningOriginal);
    }

    private static string FormatMb(long bytes) => $"{Math.Max(1, bytes / 1048576.0):F0} MB";
}
