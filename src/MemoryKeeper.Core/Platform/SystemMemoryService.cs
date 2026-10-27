using System.Runtime.InteropServices;
using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Platform;

/// <summary>调用 Win32 GlobalMemoryStatusEx 获取真实系统物理内存数据。</summary>
public sealed class SystemMemoryService : ISystemMemoryService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public SystemMemorySnapshot GetSnapshot()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            return new SystemMemorySnapshot(
                (long)mem.ullTotalPhys,
                (long)mem.ullAvailPhys,
                (int)mem.dwMemoryLoad);
        }
        return new SystemMemorySnapshot(16L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024, 50);
    }
}
