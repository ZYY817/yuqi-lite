using System.Windows;
using System.Windows.Input;
using MemoryKeeper.Contracts;

namespace MemoryKeeper.Modules.Cleaner;

public partial class CleanerView : System.Windows.Controls.UserControl
{
    private readonly CleanerViewModel _vm;

    public CleanerView(
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
        InitializeComponent();
        _vm = new CleanerViewModel(
            catalog, cleanup, sysMem, whitelist, modeEngine, modeStore, disableService, history, bus, respawnBlocker);
        DataContext = _vm;
        // 子 Tab 切换时给列表一个短淡入，避免整页"瞬间替换"的生硬感
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CleanerViewModel.HomeTabIndex))
            {
                HomeListScroll.BeginAnimation(
                    System.Windows.UIElement.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0.35, 1,
                        TimeSpan.FromMilliseconds(170),
                        System.Windows.Media.Animation.FillBehavior.Stop)
                    { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
            }
        };
    }

    public CleanerViewModel ViewModel => _vm;

    private async void Clean_Click(object sender, RoutedEventArgs e) => await _vm.CleanSelectedAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.RescanToHomeAsync();
    private async void QuickMode_Click(object sender, RoutedEventArgs e) => await _vm.QuickModeActionAsync();
    private void SelectHome_Click(object sender, RoutedEventArgs e) => _vm.ToggleSelectVisible(_vm.HomeItems);

    private void OpenAllApps_Click(object sender, RoutedEventArgs e) => _vm.ShowAllApps();
    private void BackToHome_Click(object sender, RoutedEventArgs e) => _vm.Screen = CleanerScreen.Home;

    private void ToggleSort_Click(object sender, RoutedEventArgs e) => _vm.SortByMemory = !_vm.SortByMemory;

    private void CancelCleanup_Click(object sender, RoutedEventArgs e) => _vm.CancelCleanup();

    private void ResultRowAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ResultRowVm row)
            row.HandleAction();
    }

    private static AppItemViewModel? ItemOf(object sender)
        => (sender as FrameworkElement)?.Tag as AppItemViewModel
           ?? ((sender as FrameworkElement)?.DataContext as AppItemViewModel);

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        // 勾选框/行内按钮自行处理点击（事件冒泡到行容器）
        for (var d = e.OriginalSource as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.CheckBox or System.Windows.Controls.Button) return;
        if ((sender as FrameworkElement)?.DataContext is not AppItemViewModel item) return;
        // 可选行：点击行主体=勾选/取消勾选；已保护行不可选，点击=进入详情
        if (item.IsProtected) _vm.OpenDetail(item);
        else item.IsSelected = !item.IsSelected;
    }

    private void Menu_Detail(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _vm.OpenDetail(item);
    }

    private void Menu_Protect(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _vm.ToggleProtect(item);
    }

    private void Menu_BlockRespawn(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _vm.ToggleRespawnBlock(item);
    }

    private void Menu_AddToMode(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            var modes = _vm.AllModes;
            if (modes.Count == 0) { _vm.GoToModesTab(); return; }
            if (modes.Count == 1)
            {
                _vm.AddToGameModeCloseList(item.Group);
                MemoryKeeper.Modules.Shared.MkDialog.Alert(System.Windows.Application.Current?.MainWindow,
                    "已加入模式", $"「{item.DisplayName}」已加入「{modes[0].Name}」的启动时关闭规则。");
                return;
            }
            if (sender is not FrameworkElement fe) return;
            var menu = new System.Windows.Controls.ContextMenu { Style = (System.Windows.Style)FindResource("RowMenu") };
            foreach (var m in modes)
            {
                var mi = new System.Windows.Controls.MenuItem
                {
                    Header = $"加入「{m.Name}」",
                    Style = (System.Windows.Style)FindResource("RowMenuItem"),
                    Tag = m,
                };
                mi.Click += (_, _) =>
                {
                    _vm.AddToModeCloseList(item.Group, m);
                    MemoryKeeper.Modules.Shared.MkDialog.Alert(System.Windows.Application.Current?.MainWindow,
                        "已加入模式", $"「{item.DisplayName}」已加入「{m.Name}」的启动时关闭规则。");
                };
                menu.Items.Add(mi);
            }
            menu.PlacementTarget = fe;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void Menu_ManageStartup(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            await _vm.DisableStartupWithConfirmAsync(item.Group);
    }

    private void Menu_OpenLocation(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            _vm.OpenLocation(item.IconPath, item.Group.InstallDir);
    }

    // ---- 详情页事件 ----
    private void Detail_Back(object sender, RoutedEventArgs e) => _vm.CloseDetail();
    private void Detail_OpenFolder(object sender, RoutedEventArgs e) => _vm.Detail?.OpenFolder();

    private async void Detail_Clean(object sender, RoutedEventArgs e)
    {
        if (_vm.Detail is { } d) await d.CleanThisAsync();
    }

    private void Detail_Protect(object sender, RoutedEventArgs e) => _vm.Detail?.ProtectThis();

    private void Detail_BlockRespawn(object sender, RoutedEventArgs e) => _vm.Detail?.ToggleRespawnBlock();

    private void Detail_AddToMode(object sender, RoutedEventArgs e)
    {
        if (_vm.Detail is not { } d) return;
        var modes = _vm.AllModes;
        if (modes.Count == 0) { _vm.GoToModesTab(); return; }
        if (modes.Count == 1) { d.AddToModeClose(modes[0]); return; }

        // 多个模式时弹出选择菜单，由用户指定加入哪个模式的处理规则
        if (sender is not FrameworkElement fe) return;
        var menu = new System.Windows.Controls.ContextMenu { Style = (System.Windows.Style)FindResource("RowMenu") };
        foreach (var m in modes)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = $"加入「{m.Name}」",
                Style = (System.Windows.Style)FindResource("RowMenuItem"),
                Tag = m,
            };
            item.Click += (_, _) => d.AddToModeClose(m);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = fe;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void Detail_ManageStartup(object sender, RoutedEventArgs e)
    {
        if (_vm.Detail is { } d) await d.ManageStartupAsync();
    }
}
