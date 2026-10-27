using System.Diagnostics;
using System.Text.Json;
using MemoryKeeper.Contracts;
using MemoryKeeper.Core.Platform;

namespace MemoryKeeper.Core.Services;

/// <summary>多模式持久化存储（modes.json），首次启动自动预置三个可编辑示例模式。</summary>
public sealed class ModeStore : IModeStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string ModesPath => Path.Combine(StorePaths.Root, "modes.json");
    private readonly List<ModeProfile> _modes;

    public ModeStore()
    {
        _modes = LoadOrSeed();
    }

    public IReadOnlyList<ModeProfile> GetAll() => _modes;

    public ModeProfile? GetById(string id)
        => _modes.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public void Save(ModeProfile profile)
    {
        profile.UpdatedAt = DateTime.Now;
        var idx = _modes.FindIndex(m => m.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _modes[idx] = profile;
        else _modes.Add(profile);
        Persist();
    }

    public void Delete(string id)
    {
        if (_modes.RemoveAll(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0)
            Persist();
    }

    private void Persist()
        => File.WriteAllText(ModesPath, JsonSerializer.Serialize(_modes, JsonOpts));

    private static List<ModeProfile> LoadOrSeed()
    {
        if (File.Exists(ModesPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<ModeProfile>>(File.ReadAllText(ModesPath));
                if (loaded is { Count: > 0 }) return loaded;
            }
            catch { }
        }

        var seeded = new List<ModeProfile>
        {
            new()
            {
                Id = "game",
                Name = "游戏模式",
                Description = "启动游戏时自动关闭无关后台应用，保留游戏平台与驱动组件",
                IconKind = "game",
                AutoTrigger = false,
                PreserveAppKeys = ["steam", "nvidia", "realtek", "discord", "wegame", "epic", "qqpcmgr", "ioa", "devin"],
                CloseOnStartAppKeys = ["wpscloudsvr", "quarkclouddrive", "lghub_updater", "vbcscompiler", "qtwebengineprocess", "smartvpn", "baidunetdisk", "thunder", "qqmusic", "cloudmusic"],
                MonitorRespawnEnabled = true,
                BlockRespawnAppKeys = ["wpscloudsvr", "lghub_updater", "vbcscompiler"],
            },
            new()
            {
                Id = "dev",
                Name = "开发模式",
                Description = "保留开发工具、编译器与终端，关闭娱乐与网盘更新组件",
                IconKind = "code",
                AutoTrigger = false,
                PreserveAppKeys = ["devin", "code", "cursor", "node", "dotnet", "vbcscompiler", "chrome"],
                CloseOnStartAppKeys = ["quarkclouddrive", "wpscloudsvr", "lghub_updater", "qqmusic", "cloudmusic", "baidunetdisk"],
                MonitorRespawnEnabled = false,
                BlockRespawnAppKeys = ["wpscloudsvr", "lghub_updater"],
            },
            new()
            {
                Id = "design",
                Name = "设计模式",
                Description = "保留设计软件、同步工具与素材应用，清理多余驻留进程",
                IconKind = "design",
                AutoTrigger = false,
                PreserveAppKeys = ["onedrive", "chrome", "nvidia", "devin"],
                CloseOnStartAppKeys = ["lghub_updater", "vbcscompiler", "smartvpn", "thunder"],
                MonitorRespawnEnabled = false,
                BlockRespawnAppKeys = ["lghub_updater"],
            },
        };
        try { File.WriteAllText(ModesPath, JsonSerializer.Serialize(seeded, JsonOpts)); } catch { }
        return seeded;
    }
}

/// <summary>
/// 多模式运行与自动触发引擎：
/// 1. 低频监测绑定的目标游戏/应用启动与退出；
/// 2. 启用模式时核实全局保护与本模式保留列表，执行启动时关闭规则；
/// 3. 运行期间仅对用户勾选的防重启应用进行受频次上限（最多 3 次）限制的温和拦截。
/// </summary>
public sealed class ModeEngine : IModeEngine
{
    private const int MaxRespawnKillsPerApp = 3;
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(4);

    private readonly IModeStore _store;
    private readonly IAppCatalog _catalog;
    private readonly ICleanupExecutionService _cleanup;
    private readonly IWhitelistStore _whitelist;
    private readonly IHistoryStore _history;
    private readonly IEventBus _bus;

    private readonly Timer _timer;
    private readonly Dictionary<string, int> _respawnKillCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _seenPids = [];
    private int _tickBusy;
    private bool _activeByAutoTrigger;

    public ModeProfile? ActiveMode { get; private set; }
    public CleanupBatchReport? LastModeReport { get; private set; }
    public event EventHandler<ModeProfile?>? ActiveModeChanged;

    public ModeEngine(
        IModeStore store,
        IAppCatalog catalog,
        ICleanupExecutionService cleanup,
        IWhitelistStore whitelist,
        IHistoryStore history,
        IEventBus bus)
    {
        _store = store;
        _catalog = catalog;
        _cleanup = cleanup;
        _whitelist = whitelist;
        _history = history;
        _bus = bus;
        _timer = new Timer(OnTick, null, WatchInterval, WatchInterval);
    }

    public async Task<CleanupBatchReport?> EnableModeAsync(string modeId, bool autoTriggered = false)
    {
        var mode = _store.GetById(modeId);
        if (mode is null) return null;

        ActiveMode = mode;
        _activeByAutoTrigger = autoTriggered;
        _respawnKillCounts.Clear();
        SnapshotCurrentPids();

        ActiveModeChanged?.Invoke(this, ActiveMode);
        _bus.Publish(new ModeChangedMessage(ActiveMode));

        // 扫描当前后台并选出符合本模式关闭规则且不受保护的应用
        var groups = await Task.Run(() => _catalog.Scan()).ConfigureAwait(false);
        var preserveSet = new HashSet<string>(mode.PreserveAppKeys, StringComparer.OrdinalIgnoreCase);
        var closeSet = new HashSet<string>(mode.CloseOnStartAppKeys, StringComparer.OrdinalIgnoreCase);

        var targets = groups.Where(g =>
        {
            if (g.Level == SafetyLevel.System || g.IsProtected || _whitelist.IsProtected(g.Key))
                return false;
            if (preserveSet.Contains(g.Key) || (g.MatchedRuleId is not null && preserveSet.Contains(g.MatchedRuleId)))
                return false;
            if (mode.TargetProcessName is not null && g.Key.Equals(mode.TargetProcessName, StringComparison.OrdinalIgnoreCase))
                return false;
            // 命中模式关闭列表，或属于建议清理项
            return closeSet.Contains(g.Key)
                || (g.MatchedRuleId is not null && closeSet.Contains(g.MatchedRuleId))
                || g.Level == SafetyLevel.SafeToClose;
        }).ToList();

        var report = await _cleanup.ExecuteBatchAsync(targets, allowForceKillWindowless: false, progress: null).ConfigureAwait(false);
        LastModeReport = report;

        _history.Add(new HistoryRecord
        {
            Time = DateTime.Now,
            AppKey = $"mode:{mode.Id}",
            DisplayName = $"{mode.Name}（{(autoTriggered ? "自动触发" : "手动启用")}）",
            Action = "mode_start",
            Detail = $"已启用「{mode.Name}」：成功关闭 {report.ClosedCount} 项，跳过/保留 {groups.Count - targets.Count} 项",
            FailureReason = report.NeedsAttentionCount > 0 ? $"有 {report.NeedsAttentionCount} 项未能关闭" : null,
            NeedsAttention = report.NeedsAttentionCount > 0,
            Restorable = false,
        });

        return report;
    }

    public void StopActiveMode(string reason = "手动停止模式")
    {
        if (ActiveMode is null) return;
        var prev = ActiveMode;
        ActiveMode = null;
        _activeByAutoTrigger = false;
        _respawnKillCounts.Clear();

        _history.Add(new HistoryRecord
        {
            Time = DateTime.Now,
            AppKey = $"mode:{prev.Id}",
            DisplayName = $"{prev.Name} · 已停止",
            Action = "mode_stop",
            Detail = $"{reason}，已结束本模式的临时后台监测",
            Restorable = false,
        });

        ActiveModeChanged?.Invoke(this, null);
        _bus.Publish(new ModeChangedMessage(null));
    }

    private void SnapshotCurrentPids()
    {
        lock (_seenPids)
        {
            _seenPids.Clear();
            foreach (var p in Process.GetProcesses())
            {
                _seenPids.Add(p.Id);
                p.Dispose();
            }
        }
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _tickBusy, 1) == 1) return;
        try
        {
            CheckAutoTriggerAndRespawn();
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _tickBusy, 0);
        }
    }

    private void CheckAutoTriggerAndRespawn()
    {
        var procs = Process.GetProcesses();
        var currentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentPids = new List<int>(procs.Length);

        foreach (var p in procs)
        {
            try
            {
                currentPids.Add(p.Id);
                currentNames.Add(p.ProcessName);
            }
            catch { }
            finally { p.Dispose(); }
        }

        // 1. 若当前未启用模式，检查是否有配置了自动触发的模式其目标程序已启动
        if (ActiveMode is null)
        {
            var autoMode = _store.GetAll().FirstOrDefault(m =>
                m.AutoTrigger && !string.IsNullOrWhiteSpace(m.TargetProcessName)
                && currentNames.Contains(Path.GetFileNameWithoutExtension(m.TargetProcessName)));

            if (autoMode is not null)
            {
                _ = EnableModeAsync(autoMode.Id, autoTriggered: true);
                return;
            }
        }
        else
        {
            // 2. 若当前模式由目标程序自动触发，当目标程序退出后自动停止该模式
            if (_activeByAutoTrigger && !string.IsNullOrWhiteSpace(ActiveMode.TargetProcessName))
            {
                var targetName = Path.GetFileNameWithoutExtension(ActiveMode.TargetProcessName);
                if (!currentNames.Contains(targetName))
                {
                    StopActiveMode($"检测到目标应用「{ActiveMode.TargetProcessName}」已退出");
                    return;
                }
            }

            // 3. 运行期间防重新拉起（仅当开关启用，且严格限制每个应用最多尝试 3 次）
            if (ActiveMode.MonitorRespawnEnabled && ActiveMode.BlockRespawnAppKeys.Count > 0)
            {
                List<int> freshPids;
                lock (_seenPids)
                {
                    freshPids = currentPids.Where(id => !_seenPids.Contains(id)).ToList();
                    _seenPids.Clear();
                    _seenPids.UnionWith(currentPids);
                }

                var blockSet = new HashSet<string>(ActiveMode.BlockRespawnAppKeys, StringComparer.OrdinalIgnoreCase);
                foreach (var pid in freshPids)
                {
                    TryHandleRespawnedProcess(pid, blockSet);
                }
            }
        }
    }

    private void TryHandleRespawnedProcess(int pid, HashSet<string> blockSet)
    {
        if (pid == Environment.ProcessId) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            var key = p.ProcessName.ToLowerInvariant();
            if (!blockSet.Contains(key)) return;
            if (SystemProcessNames.Contains(p.ProcessName) || _whitelist.IsProtected(key)) return;
            if (p.MainWindowHandle != IntPtr.Zero) return;

            var count = _respawnKillCounts.GetValueOrDefault(key, 0);
            if (count >= MaxRespawnKillsPerApp) return; // 超过重试上限，停止继续杀进程

            p.Kill(entireProcessTree: false);
            _respawnKillCounts[key] = count + 1;
        }
        catch { }
    }

    public void Dispose() => _timer.Dispose();
}
