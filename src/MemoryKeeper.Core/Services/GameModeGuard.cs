using System.Diagnostics;
using MemoryKeeper.Contracts;
using MemoryKeeper.Core.Platform;

namespace MemoryKeeper.Core.Services;

/// <summary>
/// 进程守卫，两种模式共用一个轮询定时器：
/// 1. 专注模式（Start/Stop）：拦截一切新启动的非保护进程；
/// 2. 重新拉起拦截（SetBlockRelaunch）：仅拦截"已禁用"名单里的进程，
///    在本软件运行期间持续生效。
/// </summary>
public sealed class GameModeGuard : IGameModeGuard
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);

    private readonly IWhitelistStore _whitelist;
    private readonly IRuleStore _rules;
    private readonly IDisabledStore _disabled;
    private readonly string _windowsDir;

    private readonly HashSet<int> _knownPids = [];
    private readonly int _selfPid = Environment.ProcessId;
    private Timer? _timer;
    private bool _blockRelaunch;

    /// <summary>专注模式是否在运行。</summary>
    public bool IsRunning { get; private set; }
    public event EventHandler<string>? ProcessBlocked;

    public GameModeGuard(IWhitelistStore whitelist, IRuleStore rules, IDisabledStore disabled)
    {
        _whitelist = whitelist;
        _rules = rules;
        _disabled = disabled;
        _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    /// <summary>专注模式开启：记录基线，之后新进程按"非保护即拦截"处理。</summary>
    public void Start()
    {
        if (IsRunning) return;
        Rebaseline();
        IsRunning = true;
        EnsureTimer();
    }

    public void Stop()
    {
        IsRunning = false;
        StopTimerIfIdle();
    }

    /// <summary>阻止已禁用应用重新拉起（本软件运行期间有效）。</summary>
    public void SetBlockRelaunch(bool on)
    {
        if (on == _blockRelaunch) return;
        _blockRelaunch = on;
        if (on)
        {
            Rebaseline();
            EnsureTimer();
        }
        else StopTimerIfIdle();
    }

    private void Rebaseline()
    {
        lock (_knownPids)
        {
            _knownPids.Clear();
            foreach (var p in Process.GetProcesses())
            {
                _knownPids.Add(p.Id);
                p.Dispose();
            }
        }
    }

    private void EnsureTimer() => _timer ??= new Timer(Tick, null, Interval, Interval);

    private void StopTimerIfIdle()
    {
        if (IsRunning || _blockRelaunch) return;
        _timer?.Dispose();
        _timer = null;
    }

    private void Tick(object? state)
    {
        List<int> current;
        try { current = Process.GetProcesses().Select(p => p.Id).ToList(); }
        catch { return; }

        List<int> fresh;
        lock (_knownPids)
        {
            fresh = current.Where(id => !_knownPids.Contains(id)).ToList();
            _knownPids.Clear();
            _knownPids.UnionWith(current);
        }

        foreach (var pid in fresh)
            EvaluateAndMaybeKill(pid);
    }

    private void EvaluateAndMaybeKill(int pid)
    {
        if (pid == _selfPid) return;

        string name, path;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            try { path = p.MainModule?.FileName ?? ""; }
            catch { path = ""; }
            if (string.IsNullOrEmpty(path)) return;
            if (path.StartsWith(_windowsDir, StringComparison.OrdinalIgnoreCase)) return;
            if (SystemProcessNames.Contains(name)) return;

            var key = name.ToLowerInvariant();
            var scope = _disabled.GetScope(key);

            // 已禁用 + 阻止重新拉起 → 无条件拦截
            var blockedByRelaunch = _blockRelaunch && scope is not null;
            // 专注模式 → 拦截非保护进程；"按场景禁用"的即使在白名单内也要拦
            var blockedByFocus = IsRunning
                && (scope == DisableScope.FocusOnly
                    || (!_whitelist.IsProtected(key)
                        && _rules.Match(name, Path.GetDirectoryName(path))?.DefaultProtect != true));

            if (!blockedByRelaunch && !blockedByFocus) return;

            p.Kill(entireProcessTree: false);
            ProcessBlocked?.Invoke(this, name);
        }
        catch { /* 进程可能已退出或拒绝访问 */ }
    }

    public void Dispose() => Stop();
}
