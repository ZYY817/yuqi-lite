using MemoryKeeper.Contracts;

namespace MemoryKeeper.Modules.Disabled;

/// <summary>一级导航「模式」模块：多模式列表、模式创建/编辑、目标程序绑定与应用规则选择器。</summary>
public sealed class DisabledModule : IModule
{
    private readonly IModeStore _modeStore;
    private readonly IModeEngine _modeEngine;
    private readonly IAppCatalog _catalog;
    private readonly IWhitelistStore _whitelist;
    private readonly IEventBus _bus;

    public DisabledModule(
        IModeStore modeStore,
        IModeEngine modeEngine,
        IAppCatalog catalog,
        IWhitelistStore whitelist,
        IEventBus bus)
    {
        _modeStore = modeStore;
        _modeEngine = modeEngine;
        _catalog = catalog;
        _whitelist = whitelist;
        _bus = bus;
    }

    public string Id => "modes";
    public string Title => "模式";
    public int Order => 1;

    public object CreateView() => new DisabledView(_modeStore, _modeEngine, _catalog, _whitelist, _bus);
}
