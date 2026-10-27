using System.ComponentModel;
using System.Diagnostics;
using MemoryKeeper.Contracts;
using MemoryKeeper.Core.Platform;

namespace MemoryKeeper.Core.Services;

/// <summary>
/// 会话级防重启实现：用户显式把某应用加入阻止名单后，
/// 低频扫描发现同名进程再次启动时将其结束。
/// 仅在客户端运行期间生效（不注册持久守护），系统关键进程与受保护应用豁免。
/// </summary>
public sealed class RespawnBlocker : IRespawnBlocker, IDisposable
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(4);

    private readonly IWhitelistStore _whitelist;
    private readonly IHistoryStore _history;
    private readonly IEventBus _bus;
    private readonly Timer _timer;

    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>在阻止名单中、但因权限不足（服务/SYSTEM 进程）反复结束失败的进程名。</summary>
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private int _tickBusy;

    public event EventHandler? Changed;

    public RespawnBlocker(IWhitelistStore whitelist, IHistoryStore history, IEventBus bus)
    {
        _whitelist = whitelist;
        _history = history;
        _bus = bus;
        _timer = new Timer(OnTick, null, WatchInterval, WatchInterval);
    }

    public IReadOnlyCollection<string> BlockedProcessNames => _blocked;

    public bool IsGroupBlocked(AppGroup group)
        => group.Processes.Any(p => _blocked.Contains(Normalize(p.Name)));

    public bool IsGroupDenied(AppGroup group)
        => group.Processes.Any(p => _denied.Contains(Normalize(p.Name)));

    public void Block(AppGroup group)
    {
        var names = group.Processes.Select(p => Normalize(p.Name)).Where(n => n.Length > 0).ToList();
        if (names.Count == 0) return;
        lock (_blocked)
        {
            foreach (var n in names) _blocked.Add(n);
            _displayNames[group.Key] = group.DisplayName;
        }
        _history.Add(new HistoryRecord
        {
            Time = DateTime.Now,
            AppKey = group.Key,
            DisplayName = group.DisplayName,
            Action = "respawn_block",
            Detail = $"已加入「禁止自动重启」名单：本会话内若 {string.Join("、", names)} 再次启动将被结束",
            Restorable = false, // 解除入口在清理页详情/右键菜单
        });
        _bus.Publish(new AppsChangedMessage());
        Changed?.Invoke(this, EventArgs.Empty);
        // 立即执行一次拦截：把当前仍在运行的无窗口同名进程一并结束，
        // 否则"已阻止重启"状态下应用仍显示在运行，逻辑上不一致
        _ = Task.Run(() => OnTick(null));
    }

    public void Unblock(AppGroup group)
    {
        var names = group.Processes.Select(p => Normalize(p.Name)).ToList();
        lock (_blocked)
        {
            foreach (var n in names) { _blocked.Remove(n); _denied.Remove(n); }
            _displayNames.Remove(group.Key);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _tickBusy, 1) == 1) return;
        try
        {
            string[] blocked;
            lock (_blocked) blocked = [.. _blocked];
            if (blocked.Length == 0) return;

            var blockedSet = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);
            var killedAny = false;
            var deniedChanged = false;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    if (!blockedSet.Contains(name)) continue;
                    if (p.Id == Environment.ProcessId) continue;
                    if (SystemProcessNames.Contains(name)) continue;
                    if (_whitelist.IsProtected(name.ToLowerInvariant())) continue;
                    if (p.MainWindowHandle != IntPtr.Zero) continue; // 用户主动重新打开的应用不强行结束

                    p.Kill(entireProcessTree: false);
                    killedAny = true;
                    if (_denied.Remove(name)) deniedChanged = true;
                    _history.Add(new HistoryRecord
                    {
                        Time = DateTime.Now,
                        AppKey = name.ToLowerInvariant(),
                        DisplayName = name,
                        Action = "respawn_blocked",
                        Detail = $"「{name}」尝试重新启动，已按禁止重启名单结束",
                        Restorable = false,
                    });
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    // 权限不足：如实标记，界面提示需以管理员身份运行，不再假装在拦
                    if (_denied.Add(p.ProcessName)) deniedChanged = true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (killedAny) _bus.Publish(new AppsChangedMessage());
            if (deniedChanged) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _tickBusy, 0);
        }
    }

    private static string Normalize(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public void Dispose() => _timer.Dispose();
}
