using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Platform;

/// <summary>基于 WMI 一次查询取进程全量信息，再用 Process 补充窗口标记。</summary>
public sealed class ProcessProvider : IProcessProvider
{
    public IReadOnlyList<AppProcessInfo> GetProcesses()
    {
        var windows = GetWindowedPids();
        var list = new List<AppProcessInfo>();

        const string query = "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, WorkingSetSize, CreationDate, SessionId FROM Win32_Process";
        using var searcher = new ManagementObjectSearcher(query);
        foreach (var mo in searcher.Get())
        {
            var pid = Convert.ToInt32(mo["ProcessId"]);
            var rawName = mo["Name"] as string ?? "";
            list.Add(new AppProcessInfo
            {
                Pid = pid,
                Name = rawName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? rawName[..^4] : rawName,
                // WMI 对提权/服务进程返回 null → 用 NtQuerySystemInformation 兜底取镜像路径
                Path = mo["ExecutablePath"] as string ?? QueryImagePathByPid(pid),
                Arguments = mo["CommandLine"] as string,
                MemoryBytes = Convert.ToInt64(mo["WorkingSetSize"]),
                ParentPid = Convert.ToInt32(mo["ParentProcessId"] ?? 0),
                SessionId = mo["SessionId"] is null ? -1 : Convert.ToInt32(mo["SessionId"]),
                HasVisibleWindow = windows.Contains(pid),
                StartedAt = ParseWmiDate(mo["CreationDate"] as string),
            });
        }
        return list;
    }

    public IReadOnlyList<AppProcessInfo> GetProcessesByIds(IReadOnlyCollection<int> pids)
        => GetProcesses().Where(p => pids.Contains(p.Pid)).ToList();

    /// <summary>解析 WMI CIM 时间格式 "yyyyMMddHHmmss.ffffff±zzz"。</summary>
    private static DateTime? ParseWmiDate(string? raw)
    {
        if (raw is null || raw.Length < 14) return null;
        try
        {
            var local = new DateTime(
                int.Parse(raw[..4]), int.Parse(raw[4..6]), int.Parse(raw[6..8]),
                int.Parse(raw[8..10]), int.Parse(raw[10..12]), int.Parse(raw[12..14]),
                DateTimeKind.Unspecified);
            // "…+480" 的分钟偏移 → UTC → 本地时间
            var signIdx = raw.LastIndexOfAny(['+', '-']);
            if (signIdx > 14 && int.TryParse(raw[(signIdx + 1)..], out var offsetMin))
            {
                var utc = raw[signIdx] == '+' ? local.AddMinutes(-offsetMin) : local.AddMinutes(offsetMin);
                return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            }
            return local;
        }
        catch { return null; }
    }

    // ---- NtQuerySystemInformation 兜底取进程镜像路径（无需打开进程句柄，对提权/服务进程同样有效） ----

    private const int SystemProcessIdInformation = 88;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_ID_INFORMATION
    {
        public IntPtr ProcessId;
        public UNICODE_STRING ImageName;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass,
        ref SYSTEM_PROCESS_ID_INFORMATION systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

    /// <summary>WMI 拿不到 ExecutablePath 时（服务/提权进程），用此 API 取镜像路径。</summary>
    public static string? QueryImagePathByPid(int pid)
    {
        const int nameChars = 0x400;
        var buffer = Marshal.AllocHGlobal(nameChars * 2);
        try
        {
            var info = new SYSTEM_PROCESS_ID_INFORMATION
            {
                ProcessId = (IntPtr)pid,
                ImageName = new UNICODE_STRING
                {
                    Length = 0,
                    MaximumLength = (ushort)(nameChars * 2),
                    Buffer = buffer,
                },
            };
            // 注意：传入的是结构体本身大小（名称缓冲单独分配、由 Buffer 指针引用），
            // 传结构体+缓冲总长会返回 STATUS_INFO_LENGTH_MISMATCH
            var status = NtQuerySystemInformation(
                SystemProcessIdInformation, ref info,
                Marshal.SizeOf<SYSTEM_PROCESS_ID_INFORMATION>(), out _);
            if (status != 0 || info.ImageName.Length == 0) return null;

            var devicePath = Marshal.PtrToStringUni(buffer, info.ImageName.Length / 2);
            return DevicePathToDosPath(devicePath);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>"\Device\HarddiskVolume3\foo\bar.exe" → "C:\foo\bar.exe"。</summary>
    private static string? DevicePathToDosPath(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            var letter = drive.Name.TrimEnd('\\'); // "C:"
            var sb = new StringBuilder(260);
            if (QueryDosDevice(letter, sb, sb.Capacity) == 0) continue;
            var device = sb.ToString();
            if (devicePath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                return letter + devicePath[device.Length..];
        }
        return devicePath;
    }

    private static HashSet<int> GetWindowedPids()
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                    set.Add(p.Id);
            }
            catch { /* 进程可能已退出 */ }
            finally { p.Dispose(); }
        }
        return set;
    }
}
