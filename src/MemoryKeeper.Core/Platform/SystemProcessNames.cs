namespace MemoryKeeper.Core.Platform;

/// <summary>系统关键进程名单，分类器与游戏模式守卫共用。</summary>
public static class SystemProcessNames
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "Secure System", "Idle",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm",
        "svchost", "dwm", "fontdrvhost", "sihost", "taskhostw", "RuntimeBroker",
        "dllhost", "conhost", "explorer", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "ctfmon", "spoolsv", "WmiPrvSE", "audiodg", "dasHost",
        "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray",
        "AggregatorHost", "SystemSettings", "ApplicationFrameHost", "TextInputHost",
        "Widgets", "WidgetService", "SearchIndexer", "SearchProtocolHost", "smartscreen",
        "MoUsoCoreWorker", "UsoClient", "TiWorker", "TrustedInstaller", "WUDFHost",
        "ImeWatchdog", "ChsIME", "CrossDeviceService", "PhoneExperienceHost", "GameBar",
        "GameBarFTServer", "XboxPcAppFT", "backgroundTaskHost", "UserOOBEBroker",
    };

    public static bool Contains(string name) => Names.Contains(name);
}
