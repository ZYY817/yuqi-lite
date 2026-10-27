using MemoryKeeper.Contracts;

namespace MemoryKeeper.Modules.Cleaner;

public sealed class CleanerModule : IModule
{
    private readonly IAppCatalog _catalog;
    private readonly ICleanupExecutionService _cleanup;
    private readonly ISystemMemoryService _sysMem;
    private readonly IWhitelistStore _whitelist;
    private readonly IModeEngine _modeEngine;
    private readonly IModeStore _modeStore;
    private readonly IDisableService _disableService;
    private readonly IHistoryStore _history;
    private readonly IEventBus _bus;
    private readonly IRespawnBlocker _respawnBlocker;

    public CleanerModule(
        IAppCatalog catalog,
        ICleanupExecutionService cleanup,
        ISystemMemoryService sysMem,
        IWhitelistStore whitelist,
        IModeEngine modeEngine,
        IModeStore modeStore,
        IDisableService disableService,
        IHistoryStore history,
        IEventBus bus,
        IRespawnBlocker respawnBlocker)
    {
        _catalog = catalog;
        _cleanup = cleanup;
        _sysMem = sysMem;
        _whitelist = whitelist;
        _modeEngine = modeEngine;
        _modeStore = modeStore;
        _disableService = disableService;
        _history = history;
        _bus = bus;
        _respawnBlocker = respawnBlocker;
    }

    public string Id => "cleaner";
    public string Title => "清理";
    public int Order => 0;

    public object CreateView() => new CleanerView(
        _catalog, _cleanup, _sysMem, _whitelist, _modeEngine, _modeStore, _disableService, _history, _bus, _respawnBlocker);
}
