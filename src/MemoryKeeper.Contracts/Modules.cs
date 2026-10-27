namespace MemoryKeeper.Contracts;

/// <summary>
/// 功能模块契约。每个功能（清理/白名单/恢复/未来的游戏模式…）
/// 都是一个 IModule，Shell 只负责承载，不知道功能细节。
/// </summary>
public interface IModule
{
    /// <summary>模块标识。</summary>
    string Id { get; }
    /// <summary>导航中显示的名称。</summary>
    string Title { get; }
    /// <summary>导航排序。</summary>
    int Order { get; }
    /// <summary>为 true 时不出现在顶部导航（二级页/覆盖层用）。</summary>
    bool NavHidden => false;
    /// <summary>模块主视图（WPF UIElement，以 object 返回避免 Contracts 依赖 WPF）。</summary>
    object CreateView();
    /// <summary>托盘右键菜单等扩展贡献（可空）。</summary>
    IEnumerable<ModuleMenuItem> GetMenuItems() => Enumerable.Empty<ModuleMenuItem>();
}

public sealed record ModuleMenuItem(string Text, Action OnClick);
