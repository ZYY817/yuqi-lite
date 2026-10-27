using MemoryKeeper.Contracts;

namespace MemoryKeeper.Modules.Whitelist;

public sealed class WhitelistModule : IModule
{
    private readonly IWhitelistStore _store;
    private readonly IAppCatalog _catalog;
    private readonly ISettingsStore _settings;
    private readonly IHistoryStore _history;
    private readonly IEventBus _bus;
    private readonly ISystemCriticalProvider _systemCritical;

    public WhitelistModule(
        IWhitelistStore store,
        IAppCatalog catalog,
        ISettingsStore settings,
        IHistoryStore history,
        IEventBus bus,
        ISystemCriticalProvider systemCritical)
    {
        _store = store;
        _catalog = catalog;
        _settings = settings;
        _history = history;
        _bus = bus;
        _systemCritical = systemCritical;
    }

    public string Id => "whitelist";
    public string Title => "保护";
    public int Order => 2;

    public object CreateView() => new WhitelistView(_store, _catalog, _settings, _history, _bus, _systemCritical);
}
