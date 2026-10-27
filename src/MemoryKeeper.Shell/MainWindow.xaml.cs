using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemoryKeeper.Contracts;
using WinForms = System.Windows.Forms;

namespace MemoryKeeper.Shell;

public partial class MainWindow : Window
{
    private readonly List<IModule> _modules;
    private List<IModule> _navModules = [];
    private readonly Dictionary<string, object> _viewCache = new();
    private readonly ISettingsStore _settings;
    private readonly IModeEngine _modeEngine;
    private readonly WinForms.NotifyIcon _tray;
    private bool _realClose;

    public MainWindow(
        IEnumerable<IModule> modules,
        IEventBus bus,
        ISettingsStore settings,
        IModeEngine modeEngine)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => EnableRoundedCorners();
        _modules = modules.ToList();
        _settings = settings;
        _modeEngine = modeEngine;

        _navModules = _modules.Where(m => !m.NavHidden).ToList();
        NavTabs.ItemsSource = _navModules.Select(m => m.Title).ToList();
        NavTabs.SelectionChanged += (_, idx) =>
        {
            WelcomeOverlay.Visibility = Visibility.Collapsed;
            if (idx >= 0 && idx < _navModules.Count) ShowModule(_navModules[idx]);
        };

        bus.Subscribe<NavigateToTabMessage>(msg => Dispatcher.Invoke(() => SwitchToTab(msg.ModuleId)));
        bus.Subscribe<RestartElevatedMessage>(_ => Dispatcher.Invoke(RestartElevated));

        var appIcoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        Icon? trayIcon = null;
        try { if (File.Exists(appIcoPath)) trayIcon = new Icon(appIcoPath); } catch { }

        _tray = new WinForms.NotifyIcon
        {
            Icon = trayIcon ?? SystemIcons.Shield,
            Text = "宇奇轻管 — 正在运行",
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开宇奇轻管", null, (_, _) => ShowFromTray());
        var gameModeItem = new WinForms.ToolStripMenuItem("启用游戏模式");
        gameModeItem.Click += async (_, _) =>
        {
            if (_modeEngine.ActiveMode is not null) _modeEngine.StopActiveMode("从系统托盘停止模式");
            else await _modeEngine.EnableModeAsync("game", autoTriggered: false);
        };
        menu.Items.Add(gameModeItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => RealClose());
        menu.Opening += (_, _) =>
        {
            gameModeItem.Checked = _modeEngine.ActiveMode is not null;
            gameModeItem.Text = _modeEngine.ActiveMode is { } m ? $"当前模式：{m.Name}（点击停止）" : "启用游戏模式";
        };
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        if (_modules.Count > 0)
        {
            var firstModule = _navModules.FirstOrDefault() ?? _modules[0];
            ShowModule(firstModule);
            Dispatcher.InvokeAsync(() => NavTabs.SelectedIndex = _navModules.IndexOf(firstModule));
        }

        // 首次启动展示欢迎引导（完成或跳过后不再自动弹出，可随时点顶栏 ? 查看）
        if (_settings.Get("onboarding", "done") != "1")
        {
            WelcomeOverlay.Visibility = Visibility.Visible;
        }
    }

    private void EnableRoundedCorners()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public void SwitchToTab(string moduleId)
    {
        WelcomeOverlay.Visibility = Visibility.Collapsed;

        var target = _modules.FirstOrDefault(m => m.Id.Equals(moduleId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        ShowModule(target);
        var idx = _navModules.IndexOf(target);
        // 隐藏导航的模块（如记录页）：清空选中，滑块随之隐藏，返回主 Tab 时恢复
        NavTabs.SelectedIndex = idx;
    }

    private void ShowModule(IModule module)
    {
        if (!_viewCache.TryGetValue(module.Id, out var view))
        {
            view = module.CreateView();
            _viewCache[module.Id] = view;
        }
        ModuleContent.Content = view;
    }

    // ---- 首次欢迎引导交互 ----
    private void ShowGuide_Click(object sender, RoutedEventArgs e)
    {
        WelcomeOverlay.Visibility = WelcomeOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CompleteOnboarding()
    {
        _settings.Set("onboarding", "done", "1");
        WelcomeOverlay.Visibility = Visibility.Collapsed;
    }

    private void WelcomeCleanBtn_Click(object sender, RoutedEventArgs e)
    {
        CompleteOnboarding();
        SwitchToTab("cleaner");
    }

    private void WelcomeModeBtn_Click(object sender, RoutedEventArgs e)
    {
        CompleteOnboarding();
        SwitchToTab("modes");
        if (_viewCache.TryGetValue("modes", out var v)
            && v is MemoryKeeper.Modules.Disabled.DisabledView dv
            && dv.ViewModel.Modes.FirstOrDefault() is { } firstMode)
        {
            dv.ViewModel.OpenEditor(firstMode.Profile);
        }
    }

    private void WelcomeSkip_Click(object sender, RoutedEventArgs e) => CompleteOnboarding();

    private void Chrome_Drag(object sender, MouseButtonEventArgs e)
    {
        // 点击落在 Tab/按钮等交互控件上时不触发窗口拖动，否则吃掉点击导致无法切换
        for (var d = e.OriginalSource as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is MemoryKeeper.Modules.Shared.SlidingTabs
                or System.Windows.Controls.Button
                or System.Windows.Controls.TextBox) return;
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_realClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RealClose()
    {
        _realClose = true;
        _tray.Visible = false;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>以管理员身份重启本应用：拉起 UAC 提权新实例后真正退出，由新实例接管单实例互斥锁。</summary>
    private void RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            RealClose();
        }
        catch (Win32Exception) { /* 用户取消了 UAC */ }
        catch { }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        => FindVisualChildren<T>(parent).FirstOrDefault();

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var sub in FindVisualChildren<T>(child))
                yield return sub;
        }
    }
}
