using System.Diagnostics;
using System.Management;
using MemoryKeeper.Contracts;
using Microsoft.Win32;

namespace MemoryKeeper.Core.Platform;

/// <summary>
/// 枚举全部自启动入口：注册表 Run/RunOnce、启动文件夹、计划任务、自启服务。
/// 只读枚举；禁用/还原由 DisableService 执行。
/// </summary>
public sealed class StartupProvider : IStartupProvider
{
    private static readonly (RegistryKey Hive, string Path)[] RunLocations =
    [
        (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    private IReadOnlyList<StartupEntry>? _fastCache;
    private IReadOnlyList<StartupEntry>? _taskCache;
    private DateTime _cacheAt;
    private int _warmingTasks;

    public StartupProvider()
    {
        // 后台预热较慢的计划任务查询，避免首次打开界面卡顿
        WarmScheduledTasksAsync();
    }

    public IReadOnlyList<StartupEntry> GetAll()
    {
        if (_fastCache is null || (DateTime.UtcNow - _cacheAt).TotalSeconds >= 30)
        {
            var fast = new List<StartupEntry>();
            fast.AddRange(FromRunKeys());
            fast.AddRange(FromStartupFolders());
            fast.AddRange(FromServices());
            _fastCache = fast;
            _cacheAt = DateTime.UtcNow;
            WarmScheduledTasksAsync();
        }

        return _taskCache is { Count: > 0 }
            ? _fastCache.Concat(_taskCache).ToList()
            : _fastCache;
    }

    private void WarmScheduledTasksAsync()
    {
        if (Interlocked.Exchange(ref _warmingTasks, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try { _taskCache = FromScheduledTasks().ToList(); }
            catch { }
            finally { Interlocked.Exchange(ref _warmingTasks, 0); }
        });
    }

    // ---- 注册表 Run/RunOnce ----
    private static IEnumerable<StartupEntry> FromRunKeys()
    {
        foreach (var (hive, keyPath) in RunLocations)
        {
            RegistryKey? key;
            try { key = hive.OpenSubKey(keyPath); } catch { continue; }
            if (key is null) continue;
            using (key)
            {
                var location = $"{(hive == Registry.CurrentUser ? "HKCU" : "HKLM")}\\{keyPath}";
                foreach (var name in key.GetValueNames())
                {
                    var value = key.GetValue(name) as string;
                    if (value is null) continue;
                    yield return new StartupEntry
                    {
                        Id = $"run|{location}|{name}",
                        Kind = StartupKind.RegistryRun,
                        Name = name,
                        ExePath = ExtractExePath(value),
                        Location = location,
                        RawData = value,
                    };
                }
            }
        }
    }

    // ---- 启动文件夹快捷方式 ----
    private static IEnumerable<StartupEntry> FromStartupFolders()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                string? target = null;
                try { target = ResolveShortcut(lnk); } catch { }
                yield return new StartupEntry
                {
                    Id = $"folder|{lnk}",
                    Kind = StartupKind.StartupFolder,
                    Name = Path.GetFileNameWithoutExtension(lnk),
                    ExePath = target,
                    Location = lnk,
                };
            }
        }
    }

    private static string? ResolveShortcut(string lnkPath)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t is null) return null;
        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(t);
            dynamic shortcut = shell!.CreateShortcut(lnkPath);
            var target = (string)shortcut.TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch { return null; }
    }

    // ---- 计划任务（schtasks CSV 输出）----
    private static IEnumerable<StartupEntry> FromScheduledTasks()
    {
        string output;
        try
        {
            var psi = new ProcessStartInfo("schtasks", "/query /fo csv /v")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) yield break;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
        }
        catch { yield break; }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) yield break;
        var header = ParseCsvLine(lines[0]);
        var nameIdx = Array.FindIndex(header, h => h.Contains("TaskName") || h == "任务名");
        var runIdx = Array.FindIndex(header, h => h.Contains("Task To Run") || h == "要运行的任务");
        var statusIdx = Array.FindIndex(header, h => h.Contains("Scheduled Task State") || h.Contains("计划任务状态"));
        if (nameIdx < 0 || runIdx < 0) yield break;

        foreach (var line in lines.Skip(1))
        {
            var cols = ParseCsvLine(line);
            if (cols.Length <= Math.Max(nameIdx, runIdx)) continue;
            var name = cols[nameIdx];
            var toRun = cols[runIdx];
            if (string.IsNullOrWhiteSpace(toRun) || toRun == "N/A" || toRun.StartsWith("COM handler", StringComparison.OrdinalIgnoreCase))
                continue;
            var disabled = statusIdx >= 0 && statusIdx < cols.Length
                && cols[statusIdx].Contains("Disabled", StringComparison.OrdinalIgnoreCase);
            yield return new StartupEntry
            {
                Id = $"task|{name}",
                Kind = StartupKind.ScheduledTask,
                Name = name.TrimStart('\\'),
                ExePath = ExtractExePath(toRun),
                Location = name,
                IsDisabled = disabled,
            };
        }
    }

    private static string[] ParseCsvLine(string line)
    {
        var cols = new List<string>();
        var cur = new System.Text.StringBuilder();
        var inQ = false;
        foreach (var c in line.TrimEnd('\r'))
        {
            if (c == '"') inQ = !inQ;
            else if (c == ',' && !inQ) { cols.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cols.Add(cur.ToString());
        return cols.ToArray();
    }

    // ---- 自启服务 ----
    private static IEnumerable<StartupEntry> FromServices()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        ManagementObjectCollection results;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, PathName, StartMode FROM Win32_Service WHERE StartMode='Auto'");
            results = searcher.Get();
        }
        catch { yield break; }

        foreach (var mo in results.Cast<ManagementObject>())
        {
            var pathName = mo["PathName"] as string;
            var exe = ExtractExePath(pathName);
            // Windows 目录内的服务视为系统组件，不列为"第三方自启"
            if (exe is null || exe.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                continue;
            yield return new StartupEntry
            {
                Id = $"svc|{mo["Name"]}",
                Kind = StartupKind.Service,
                Name = mo["DisplayName"] as string ?? (string)mo["Name"],
                ExePath = exe,
                Location = (string)mo["Name"],
            };
        }
    }

    /// <summary>从命令行/注册表值中提取 exe 路径。</summary>
    public static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();

        // 引号包裹的路径优先
        var q1 = command.IndexOf('"');
        if (q1 >= 0)
        {
            var q2 = command.IndexOf('"', q1 + 1);
            if (q2 > q1 + 1)
            {
                var inner = command[(q1 + 1)..q2];
                if (inner.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || inner.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
                    return inner;
            }
        }
        // 找 .exe 边界，向前回溯到路径起点
        var exeIdx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx < 0) return null;
        var start = 0;
        for (var i = exeIdx; i >= 0; i--)
        {
            if (command[i] is ' ' or '\t' or '"') { start = i + 1; break; }
        }
        return command[start..(exeIdx + 4)].Trim('"', ' ');
    }
}
