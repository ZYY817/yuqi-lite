namespace MemoryKeeper.Contracts;

/// <summary>应用的安全分级。</summary>
public enum SafetyLevel
{
    /// <summary>系统关键进程，永不展示、永不可操作。</summary>
    System = 0,
    /// <summary>白名单/游戏/驱动等受保护项。</summary>
    Protected = 1,
    /// <summary>词典确认可安全清理。</summary>
    SafeToClose = 2,
    /// <summary>未识别的第三方程序，由用户自行判断。</summary>
    Unknown = 3,
}

/// <summary>应用类别，用于图标展示与规则匹配。</summary>
public enum AppCategory
{
    Unknown,
    App,
    Updater,
    Residual,
    Game,
    Launcher,
    Driver,
    Service,
}

/// <summary>治理动作类型（V1 仅实现 Terminate，其余为预留扩展点）。</summary>
public enum ActionKind
{
    Terminate,
    Suspend,
    LowerPriority,
    DisableStartup,
    BlockLaunch,
}

/// <summary>动作生效范围。</summary>
public enum ActionScope
{
    /// <summary>仅本次执行。</summary>
    Once,
    /// <summary>会话内生效（如游戏模式）。</summary>
    Session,
    /// <summary>持久生效（如禁用自启）。</summary>
    Persistent,
}

/// <summary>单个进程的原始信息。</summary>
public sealed record AppProcessInfo
{
    public required int Pid { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public string? Arguments { get; init; }
    public long MemoryBytes { get; init; }
    public bool HasVisibleWindow { get; init; }
    public int ParentPid { get; init; }
    /// <summary>进程所在会话 ID（0 = 服务/系统会话；取不到为 -1）。</summary>
    public int SessionId { get; init; } = -1;
    /// <summary>进程启动时间（取不到为 null）。</summary>
    public DateTime? StartedAt { get; init; }
}

/// <summary>归组后的"应用"——界面展示与操作的基本单位。</summary>
public sealed class AppGroup
{
    /// <summary>稳定标识（主 exe 名小写），用于白名单与规则匹配。</summary>
    public required string Key { get; set; }
    public required IReadOnlyList<AppProcessInfo> Processes { get; set; }
    public string? InstallDir { get; set; }

    // ---- 以下为分类管线逐步填充的字段 ----
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public string? Publisher { get; set; }
    public SafetyLevel Level { get; set; } = SafetyLevel.Unknown;
    public AppCategory Category { get; set; } = AppCategory.Unknown;
    public string? MatchedRuleId { get; set; }
    public bool IsProtected { get; set; }
    /// <summary>检测到的自启动入口（由 StartupProvider 填充）。</summary>
    public IReadOnlyList<StartupEntry> StartupEntries { get; set; } = [];

    public long TotalMemoryBytes => Processes.Sum(p => p.MemoryBytes);
    public bool HasVisibleWindow => Processes.Any(p => p.HasVisibleWindow);
    /// <summary>组内最早启动进程的启动时间（用于"已运行"展示）。</summary>
    public DateTime? StartedAt => Processes.Where(p => p.StartedAt is not null)
        .Select(p => p.StartedAt!.Value).DefaultIfEmpty().Min() is { } min && min != default
        ? min : null;

    private static readonly int OwnSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
    /// <summary>
    /// 组内是否有进程运行在其他会话（如 Session 0 的 Windows 服务/SYSTEM 进程）。
    /// 此类进程需要管理员权限才能结束或拦截，界面应如实提示并给出提权入口。
    /// </summary>
    public bool NeedsElevation => Processes.Any(p => p.SessionId >= 0 && p.SessionId != OwnSessionId);
}

/// <summary>自启动来源类型。</summary>
public enum StartupKind
{
    RegistryRun,
    StartupFolder,
    ScheduledTask,
    Service,
}

/// <summary>一条检测到的自启动入口。</summary>
public sealed record StartupEntry
{
    /// <summary>唯一标识（类型 + 位置 + 名称），禁用/还原时用。</summary>
    public required string Id { get; init; }
    public required StartupKind Kind { get; init; }
    /// <summary>展示名（注册表值名 / 快捷方式名 / 任务名 / 服务名）。</summary>
    public required string Name { get; init; }
    /// <summary>指向的 exe 路径（解析后）。</summary>
    public string? ExePath { get; init; }
    /// <summary>原始位置（注册表路径 / 快捷方式路径 / 任务名 / 服务名），还原用。</summary>
    public required string Location { get; init; }
    /// <summary>原始命令行/值数据，还原用。</summary>
    public string? RawData { get; init; }
    /// <summary>当前是否处于禁用状态。</summary>
    public bool IsDisabled { get; init; }
}

/// <summary>一条治理动作请求。</summary>
public sealed record ActionRequest(
    AppGroup Target,
    ActionKind Kind,
    ActionScope Scope = ActionScope.Once);

/// <summary>禁用生效范围。</summary>
public enum DisableScope
{
    /// <summary>长期禁用：拆掉所有自启入口。</summary>
    Permanent,
    /// <summary>按场景禁用：仅专注模式期间拦截。</summary>
    FocusOnly,
}

/// <summary>一个被禁用应用的持久化记录（含还原所需的全部信息）。</summary>
public sealed record DisabledAppEntry
{
    public required string AppKey { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>主 exe 路径（图标展示用）。</summary>
    public string? Path { get; init; }
    public required DisableScope Scope { get; init; }
    public DateTime DisabledAt { get; init; } = DateTime.Now;
    /// <summary>被禁用的自启项备份，还原时逐项恢复。</summary>
    public IReadOnlyList<StartupEntry> Entries { get; init; } = [];
}

/// <summary>一条操作历史记录（清理/模式/禁用/保护），供记录页展示与详情钻取。</summary>
public sealed record HistoryRecord
{
    public required DateTime Time { get; init; }
    public required string AppKey { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>"terminated" | "failed" | "disabled" | "protected" | "mode_start" | "mode_stop"</summary>
    public required string Action { get; init; }
    /// <summary>人话描述，如"已结束后台进程"或失败原因。</summary>
    public required string Detail { get; init; }
    /// <summary>失败或需关注的详细原因（可空）。</summary>
    public string? FailureReason { get; init; }
    /// <summary>当时进程工作集字节数。</summary>
    public long MemoryBytes { get; init; }
    /// <summary>是否需要用户关注（如关闭失败、被重新拉起、权限不足）。</summary>
    public bool NeedsAttention { get; init; }
    /// <summary>是否可恢复。</summary>
    public bool Restorable { get; init; }
    /// <summary>是否已被恢复。</summary>
    public bool Restored { get; set; }
    /// <summary>恢复所需负载：terminated → exe 路径。</summary>
    public string? RestorePath { get; init; }
    public string? RestoreArgs { get; init; }
}

/// <summary>系统物理内存真实快照（来自 GlobalMemoryStatusEx）。</summary>
public sealed record SystemMemorySnapshot(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    int MemoryLoadPercent)
{
    public long UsedPhysicalBytes => Math.Max(0, TotalPhysicalBytes - AvailablePhysicalBytes);
}

/// <summary>单个应用在清理流程中的实时状态。</summary>
public enum CleanupItemStatus
{
    Waiting,
    RequestingClose,
    Verifying,
    Closed,
    AlreadyExited,
    SkippedProtected,
    Cancelled,
    FailedTimeout,
    FailedAccessDenied,
    FailedAssociatedRunning,
    FailedRespawned,
}

/// <summary>单个应用的清理验证结果。</summary>
public sealed record CleanupItemReport(
    string AppKey,
    string DisplayName,
    string? IconPath,
    long WorkingSetBytes,
    CleanupItemStatus Status,
    string StatusText,
    string? ReasonDetail,
    int ClosedProcessCount,
    int RemainingProcessCount);

/// <summary>一次批量清理的完整验证报告。</summary>
public sealed record CleanupBatchReport(
    DateTime ExecutedAt,
    SystemMemorySnapshot BeforeMemory,
    SystemMemorySnapshot AfterMemory,
    IReadOnlyList<CleanupItemReport> Items,
    bool WasCancelled)
{
    public int ClosedCount => Items.Count(i => i.Status is CleanupItemStatus.Closed or CleanupItemStatus.AlreadyExited);
    public int NeedsAttentionCount => Items.Count(i => i.Status is CleanupItemStatus.FailedTimeout
        or CleanupItemStatus.FailedAccessDenied or CleanupItemStatus.FailedAssociatedRunning or CleanupItemStatus.FailedRespawned);
    public int SkippedCount => Items.Count(i => i.Status is CleanupItemStatus.SkippedProtected or CleanupItemStatus.Cancelled);
    /// <summary>实测系统可用物理内存增加量（若系统并发分配导致小于 0 则记为 0）。</summary>
    public long ObservedSystemFreedBytes => Math.Max(0, AfterMemory.AvailablePhysicalBytes - BeforeMemory.AvailablePhysicalBytes);
    /// <summary>已关闭应用的进程工作集统计值。</summary>
    public long ClosedWorkingSetBytes => Items
        .Where(i => i.Status is CleanupItemStatus.Closed or CleanupItemStatus.AlreadyExited)
        .Sum(i => i.WorkingSetBytes);
}

/// <summary>多模式管理中的单个模式配置。</summary>
public sealed class ModeProfile
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    /// <summary>"game" | "code" | "design" | "custom"</summary>
    public string IconKind { get; set; } = "game";
    /// <summary>是否检测到目标应用启动时自动启用。</summary>
    public bool AutoTrigger { get; set; }
    /// <summary>绑定的目标进程名（不含 .exe）。</summary>
    public string? TargetProcessName { get; set; }
    /// <summary>绑定的目标可执行文件完整路径。</summary>
    public string? TargetExePath { get; set; }
    /// <summary>本模式保留的应用 Key 集合。</summary>
    public List<string> PreserveAppKeys { get; set; } = [];
    /// <summary>本模式启动时自动关闭的应用 Key 集合。</summary>
    public List<string> CloseOnStartAppKeys { get; set; } = [];
    /// <summary>是否在模式运行期间监测并阻止指定应用重新启动（默认关闭）。</summary>
    public bool MonitorRespawnEnabled { get; set; }
    /// <summary>模式运行期间阻止重新启动的应用 Key 集合。</summary>
    public List<string> BlockRespawnAppKeys { get; set; } = [];
    /// <summary>高级设置：退出模式后是否在界面提示可恢复项。</summary>
    public bool PromptRestoreOnExit { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>单个进程的执行结果。</summary>
public sealed record ProcessActionResult(int Pid, string Name, bool Success, string? Error);

/// <summary>一次治理动作的结果。</summary>
public sealed record ActionResult(
    bool Succeeded,
    IReadOnlyList<ProcessActionResult> Details,
    string? Message = null);
