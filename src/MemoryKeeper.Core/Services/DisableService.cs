using System.Diagnostics;
using MemoryKeeper.Contracts;
using Microsoft.Win32;

namespace MemoryKeeper.Core.Services;

/// <summary>
/// 自启禁用服务：把应用的自启入口逐项禁用并保留备份，可一键还原。
/// 没有自启项的应用仍可记录为"按场景禁用"（由专注模式守卫执行拦截）。
/// </summary>
public sealed class DisableService : IDisableService
{
    private static readonly string BackupDir = Path.Combine(StorePaths.Root, "disabled-startup");
    private readonly IDisabledStore _store;

    public DisableService(IDisabledStore store)
    {
        _store = store;
        Directory.CreateDirectory(BackupDir);
    }

    public Task DisableAsync(AppGroup group, DisableScope scope)
    {
        return Task.Run(() =>
        {
            var disabledEntries = new List<StartupEntry>();
            if (scope == DisableScope.Permanent)
            {
                foreach (var e in group.StartupEntries.Where(e => !e.IsDisabled))
                {
                    if (DisableEntry(e))
                        disabledEntries.Add(e with { IsDisabled = true });
                }
            }
            _store.Add(new DisabledAppEntry
            {
                AppKey = group.Key,
                DisplayName = group.DisplayName,
                Path = group.Processes.FirstOrDefault(p => p.Path is not null)?.Path,
                Scope = scope,
                Entries = disabledEntries,
            });
        });
    }

    public Task EnableAsync(string appKey)
    {
        return Task.Run(() =>
        {
            var entry = _store.GetAll().FirstOrDefault(e =>
                e.AppKey.Equals(appKey, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return;
            foreach (var e in entry.Entries)
                EnableEntry(e);
            _store.Remove(appKey);
        });
    }

    // ---- 禁用 ----
    private static bool DisableEntry(StartupEntry e)
    {
        try
        {
            return e.Kind switch
            {
                StartupKind.RegistryRun => DisableRunKey(e),
                StartupKind.StartupFolder => DisableFolderLink(e),
                StartupKind.ScheduledTask => RunSilent("schtasks", $"/change /tn \"{e.Location}\" /disable"),
                StartupKind.Service => RunSilent("sc", $"config \"{e.Location}\" start= disabled"),
                _ => false,
            };
        }
        catch { return false; }
    }

    private static bool DisableRunKey(StartupEntry e)
    {
        var (hive, keyPath) = ParseLocation(e.Location);
        var name = e.Id.Split('|').Last();
        using var key = hive.OpenSubKey(keyPath, writable: true);
        if (key is null) return false;
        key.DeleteValue(name, throwOnMissingValue: false);
        return true;
    }

    private static bool DisableFolderLink(StartupEntry e)
    {
        if (!File.Exists(e.Location)) return false;
        var dest = Path.Combine(BackupDir, Path.GetFileName(e.Location));
        File.Move(e.Location, dest, overwrite: true);
        return true;
    }

    // ---- 还原 ----
    private static void EnableEntry(StartupEntry e)
    {
        try
        {
            switch (e.Kind)
            {
                case StartupKind.RegistryRun:
                {
                    var (hive, keyPath) = ParseLocation(e.Location);
                    var name = e.Id.Split('|').Last();
                    using var key = hive.CreateSubKey(keyPath);
                    if (e.RawData is not null)
                        key.SetValue(name, e.RawData);
                    break;
                }
                case StartupKind.StartupFolder:
                    var backup = Path.Combine(BackupDir, Path.GetFileName(e.Location));
                    if (File.Exists(backup))
                        File.Move(backup, e.Location, overwrite: true);
                    break;
                case StartupKind.ScheduledTask:
                    RunSilent("schtasks", $"/change /tn \"{e.Location}\" /enable");
                    break;
                case StartupKind.Service:
                    RunSilent("sc", $"config \"{e.Location}\" start= auto");
                    break;
            }
        }
        catch { }
    }

    private static (RegistryKey Hive, string KeyPath) ParseLocation(string location)
    {
        var sep = location.IndexOf('\\');
        var hiveName = location[..sep];
        var keyPath = location[(sep + 1)..];
        return (hiveName == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser, keyPath);
    }

    private static bool RunSilent(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit(10000);
        return p.ExitCode == 0;
    }
}
