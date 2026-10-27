namespace MemoryKeeper.Contracts;

// ==================== 平台抽象层 ====================
// 所有 Win32/系统 API 调用都收敛在这组接口后面，
// 便于测试替换，也便于将来拆成独立常驻引擎。

/// <summary>枚举当前运行的进程。</summary>
public interface IProcessProvider
{
    IReadOnlyList<AppProcessInfo> GetProcesses();
    IReadOnlyList<AppProcessInfo> GetProcessesByIds(IReadOnlyCollection<int> pids);
}

/// <summary>验证 exe 的 Authenticode 签名与发布者。</summary>
public interface ISignatureVerifier
{
    /// <returns>发布者名；未签名或验证失败返回 null。</returns>
    string? GetVerifiedPublisher(string path);
    bool IsMicrosoftSigned(string path);
}

/// <summary>自启动来源枚举：注册表 Run 键、启动文件夹、计划任务、自启服务。</summary>
public interface IStartupProvider
{
    /// <summary>枚举全部自启项（含已禁用的备份记录）。</summary>
    IReadOnlyList<StartupEntry> GetAll();
}

// ==================== 分类管线 ====================

/// <summary>把原始进程归并成"应用"。</summary>
public interface IAppGrouper
{
    IReadOnlyList<AppGroup> Group(IReadOnlyList<AppProcessInfo> processes);
}

/// <summary>分类管线中的一环：读取并补充 AppGroup 的展示/评级字段。</summary>
public interface IAppClassifier
{
    /// <summary>该分类器是否能处理此应用（返回 false 则跳到下一环）。</summary>
    bool CanHandle(AppGroup group);
    void Classify(AppGroup group);
}

// ==================== 核心服务 ====================

/// <summary>应用目录服务：扫描 + 归组 + 分类的门面。</summary>
public interface IAppCatalog
{
    IReadOnlyList<AppGroup> Scan();
}

/// <summary>治理动作执行器。</summary>
public interface IActionExecutor
{
    ActionResult Execute(ActionRequest request);
}

/// <summary>白名单存储（键 → 显示名，含用户手动解除自动保护的排除项）。</summary>
public interface IWhitelistStore
{
    bool IsProtected(string appKey);
    /// <summary>用户是否显式解除了该应用的自动保护。</summary>
    bool IsExcluded(string appKey);
    void Protect(string appKey, string displayName);
    void Unprotect(string appKey);
    void ResetExclusions();
    IReadOnlyDictionary<string, string> GetAll();
}

/// <summary>清理快照：记录被结束程序的启动信息以便恢复。</summary>
public interface ISnapshotStore
{
    void Save(IReadOnlyList<SnapshotEntry> entries);
    IReadOnlyList<SnapshotEntry> LoadLast();
    void Clear();
}

public sealed record SnapshotEntry(string AppKey, string DisplayName, string Path, string? Arguments);

/// <summary>模块级键值设置存储。</summary>
public interface ISettingsStore
{
    string? Get(string module, string key);
    void Set(string module, string key, string value);
}

/// <summary>模块间通信总线（模块不直接互相引用）。</summary>
public interface IEventBus
{
    void Publish<T>(T message);
    IDisposable Subscribe<T>(Action<T> handler);
}

/// <summary>游戏状态检测（V1 手动实现，预留自动检测实现）。</summary>
public interface IGameDetector
{
    bool IsGameRunning { get; }
    event EventHandler<bool>? GameStateChanged;
}

/// <summary>专注模式守卫：开启期间拦截新出现的非保护进程。</summary>
public interface IGameModeGuard : IDisposable
{
    bool IsRunning { get; }
    void Start();
    void Stop();
    /// <summary>阻止"已禁用"应用重新拉起（本软件运行期间生效）。</summary>
    void SetBlockRelaunch(bool on);
    /// <summary>拦截了一个进程时触发（参数为进程名）。</summary>
    event EventHandler<string>? ProcessBlocked;
}

/// <summary>禁用存储：持久化已禁用应用及其自启项备份。</summary>
public interface IDisabledStore
{
    bool IsDisabled(string appKey);
    DisableScope? GetScope(string appKey);
    void Add(DisabledAppEntry entry);
    void Remove(string appKey);
    IReadOnlyList<DisabledAppEntry> GetAll();
}

/// <summary>自启禁用服务：执行实际的系统修改与还原。</summary>
public interface IDisableService
{
    /// <summary>禁用某应用的全部自启项并记录备份。</summary>
    Task DisableAsync(AppGroup group, DisableScope scope);
    /// <summary>还原一个已禁用应用的自启项。</summary>
    Task EnableAsync(string appKey);
}

/// <summary>操作历史存储：恢复页的数据源。</summary>
public interface IHistoryStore
{
    void Add(HistoryRecord record);
    void MarkRestored(string appKey, string action);
    IReadOnlyList<HistoryRecord> GetAll();
    void Clear();
}

// ---- 系统内存、验证式清理引擎与多模式服务 ----

/// <summary>读取真实系统物理内存状态。</summary>
public interface ISystemMemoryService
{
    SystemMemorySnapshot GetSnapshot();
}

/// <summary>枚举当前正在运行的系统关键进程（保护页只读展示用，不按名称猜测归类）。</summary>
public interface ISystemCriticalProvider
{
    IReadOnlyList<AppProcessInfo> GetRunningCritical();
}

/// <summary>分步、可取消、带真实进程退出验证的清理执行服务。</summary>
public interface ICleanupExecutionService
{
    Task<CleanupBatchReport> ExecuteBatchAsync(
        IReadOnlyList<AppGroup> targets,
        bool allowForceKillWindowless,
        IProgress<(int CompletedCount, int TotalCount, CleanupItemReport Latest)>? progress,
        CancellationToken ct = default);
}

/// <summary>多模式持久化存储。</summary>
public interface IModeStore
{
    IReadOnlyList<ModeProfile> GetAll();
    ModeProfile? GetById(string id);
    void Save(ModeProfile profile);
    void Delete(string id);
}

/// <summary>
/// 会话级"禁止自动重启"名单：用户显式要求后，若对应进程再次启动会被持续结束。
/// 仅本软件运行期间生效，不做持久化后台守护；系统关键进程与受保护应用始终豁免。
/// </summary>
public interface IRespawnBlocker
{
    /// <summary>当前被阻止的进程名集合（小写）。</summary>
    IReadOnlyCollection<string> BlockedProcessNames { get; }
    /// <summary>该应用的任一进程名是否在阻止名单中。</summary>
    bool IsGroupBlocked(AppGroup group);
    /// <summary>该应用已加入阻止名单、但因权限不足（服务/SYSTEM 进程）实际无法结束。</summary>
    bool IsGroupDenied(AppGroup group);
    /// <summary>把应用的全部关联进程名加入阻止名单。</summary>
    void Block(AppGroup group);
    /// <summary>把应用的全部关联进程名移出阻止名单。</summary>
    void Unblock(AppGroup group);
    /// <summary>阻止名单变化时触发。</summary>
    event EventHandler? Changed;
}

/// <summary>多模式运行与自动触发引擎。</summary>
public interface IModeEngine : IDisposable
{
    ModeProfile? ActiveMode { get; }
    CleanupBatchReport? LastModeReport { get; }
    event EventHandler<ModeProfile?>? ActiveModeChanged;
    Task<CleanupBatchReport?> EnableModeAsync(string modeId, bool autoTriggered = false);
    void StopActiveMode(string reason = "手动停止模式");
}

// ---- 常用消息 ----
public sealed record AppsChangedMessage();
public sealed record WhitelistChangedMessage(string AppKey, bool IsProtected);
public sealed record AppsTerminatedMessage(IReadOnlyList<SnapshotEntry> Terminated);
/// <summary>请求主窗口切换到指定一级导航 Tab（可选附带子动作，如打开新建模式或查看最近结果）。</summary>
public sealed record NavigateToTabMessage(string ModuleId, string? SubAction = null);
/// <summary>请求以管理员身份重启本应用（当前实例退出，UAC 确认后的新实例接管；单实例互斥会短暂等待让位）。</summary>
public sealed record RestartElevatedMessage();
public sealed record ModeChangedMessage(ModeProfile? ActiveMode);
