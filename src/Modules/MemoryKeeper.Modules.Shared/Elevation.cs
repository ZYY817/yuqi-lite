using System.Security.Principal;

namespace MemoryKeeper.Modules.Shared;

/// <summary>当前进程权限检测。提权重启通过事件总线 RestartElevatedMessage 由主窗口执行。</summary>
public static class Elevation
{
    private static readonly Lazy<bool> _isAdmin = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    });

    /// <summary>本软件当前是否以管理员权限运行。</summary>
    public static bool IsAdmin => _isAdmin.Value;
}
