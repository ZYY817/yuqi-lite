using MemoryKeeper.Contracts;
using MemoryKeeper.Core.Pipeline;
using MemoryKeeper.Core.Platform;
using MemoryKeeper.Core.Rules;
using MemoryKeeper.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryKeeper.Core;

/// <summary>Core 服务的统一注册入口，Shell 只调一次。</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddMemoryKeeperCore(this IServiceCollection services)
    {
        // 平台层
        services.AddSingleton<IProcessProvider, ProcessProvider>();
        services.AddSingleton<ISignatureVerifier, SignatureVerifier>();
        services.AddSingleton<IStartupProvider, StartupProvider>();
        services.AddSingleton<ISystemMemoryService, SystemMemoryService>();
        services.AddSingleton<ISystemCriticalProvider, SystemCriticalProvider>();

        // 存储与模式引擎
        services.AddSingleton<IRuleStore, JsonRuleStore>();
        services.AddSingleton<IWhitelistStore, WhitelistStore>();
        services.AddSingleton<ISnapshotStore, SnapshotStore>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IGameDetector, ManualGameDetector>();
        services.AddSingleton<IGameModeGuard, GameModeGuard>();
        services.AddSingleton<IDisabledStore, DisabledStore>();
        services.AddSingleton<IDisableService, DisableService>();
        services.AddSingleton<IHistoryStore, HistoryStore>();
        services.AddSingleton<ICleanupExecutionService, CleanupExecutionService>();
        services.AddSingleton<IModeStore, ModeStore>();
        services.AddSingleton<IModeEngine, ModeEngine>();
        services.AddSingleton<IRespawnBlocker, RespawnBlocker>();

        // 分类管线（注册顺序即执行顺序）
        services.AddSingleton<IAppGrouper, AppGrouper>();
        services.AddSingleton<IAppClassifier, SignatureClassifier>();
        services.AddSingleton<IAppClassifier, DictionaryClassifier>();
        services.AddSingleton<IAppClassifier, HeuristicClassifier>();

        // 门面与执行器
        services.AddSingleton<IAppCatalog, AppCatalog>();
        services.AddSingleton<IActionExecutor, TerminateExecutor>();

        return services;
    }
}
