using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MemoryKeeper.Contracts;
using Microsoft.Win32;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using UserControl = System.Windows.Controls.UserControl;

namespace MemoryKeeper.Modules.Disabled;

public sealed class ModeCardVm : INotifyPropertyChanged
{
    public ModeProfile Profile { get; }
    private readonly IModeEngine _engine;

    public ModeCardVm(ModeProfile profile, IModeEngine engine)
    {
        Profile = profile;
        _engine = engine;
    }

    public string Name => Profile.Name;
    public string Description => Profile.Description;
    public string CloseSummary => $"关闭  {Profile.CloseOnStartAppKeys.Count} 项";
    public string PreserveSummary => $"保护  {Profile.PreserveAppKeys.Count} 项";
    public string TriggerBadge => Profile.AutoTrigger ? "自动" : "手动";
    public bool IsActive => _engine.ActiveMode?.Id == Profile.Id;
    public string EnableBtnText => IsActive ? "停止" : "启用";

    /// <summary>状态如实展示：运行中 / 等待目标启动 / 未启用。</summary>
    public string StatusText => IsActive ? "运行中"
        : Profile.AutoTrigger && !string.IsNullOrWhiteSpace(Profile.TargetProcessName) ? "等待目标启动"
        : "未启用";
    public Brush StatusBg => IsActive
        ? new SolidColorBrush(Color.FromRgb(0xE6, 0xF4, 0xED))
        : new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF5));
    public Brush StatusFg => IsActive
        ? new SolidColorBrush(Color.FromRgb(0x18, 0x79, 0x4E))
        : Profile.AutoTrigger && !string.IsNullOrWhiteSpace(Profile.TargetProcessName)
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0xE6))
            : new SolidColorBrush(Color.FromRgb(0x73, 0x79, 0x85));

    // 24x24 描边矢量图标（path mini-language，Stroke 着色用 IconFg）
    public string IconGeometry => Profile.IconKind switch
    {
        "game" => "M17,8.5 H7 A4.5,4.5 0 0,0 2.5,13 L2.5,16.5 A3.5,3.5 0 0,0 9.2,15.9 L10.5,14.5 L13.5,14.5 L14.8,15.9 A3.5,3.5 0 0,0 21.5,16.5 L21.5,13 A4.5,4.5 0 0,0 17,8.5 Z M7.5,11 L7.5,15 M5.5,13 L9.5,13 M16.8,11.5 L16.8,11.6 M15.2,14 L15.2,14.1",
        "code" => "M9,6 L4,12 L9,18 M15,6 L20,12 L15,18 M13.2,4.8 L10.8,19.2",
        "design" => "M4,20 L6.3,14.2 L15.5,5 A2.83,2.83 0 0,1 19,8.5 L9.8,17.7 Z M13,7.5 L16.5,11",
        _ => "M13,2.5 L6,13 L11.2,13 L10.2,21.5 L18,10.5 L12.8,10.5 Z",
    };

    public Brush IconBg => Profile.IconKind switch
    {
        "game" => new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE)),
        "code" => new SolidColorBrush(Color.FromRgb(0xED, 0xEC, 0xFA)),
        "design" => new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xE2)),
        _ => new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF5)),
    };

    public Brush IconFg => Profile.IconKind switch
    {
        "game" => new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)),
        "code" => new SolidColorBrush(Color.FromRgb(0x43, 0x38, 0xCA)),
        "design" => new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
        _ => new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x1D)),
    };

    public void NotifyChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PickerAppVm : INotifyPropertyChanged
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public string? IconPath { get; init; }
    public string MemoryText { get; init; } = "";
    public bool ShowConflictHint { get; init; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ModesViewModel : INotifyPropertyChanged
{
    private readonly IModeStore _store;
    private readonly IModeEngine _engine;
    private readonly IAppCatalog _catalog;
    private readonly IWhitelistStore _whitelist;
    private readonly IEventBus _bus;

    public ObservableCollection<ModeCardVm> Modes { get; } = [];
    public ObservableCollection<PickerAppVm> PickerItems { get; } = [];

    private string _screen = "list"; // list | editor | picker
    public string Screen
    {
        get => _screen;
        set
        {
            _screen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListScreen));
            OnPropertyChanged(nameof(IsEditorScreen));
            OnPropertyChanged(nameof(IsPickerScreen));
        }
    }
    public bool IsListScreen => Screen == "list";
    public bool IsEditorScreen => Screen == "editor";
    public bool IsPickerScreen => Screen == "picker";

    // ---- 正在编辑的模式 ----
    private ModeProfile? _editing;
    private string _pickerTargetList = "close"; // preserve | close | respawn

    public string EditorHeadline => _editing is null ? "编辑模式" : $"编辑模式 · {_editing.Name}";
    public string EditorModeName
    {
        get => _editing?.Name ?? "";
        set
        {
            if (_editing is null) return;
            _editing.Name = value;
            _store.Save(_editing);
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditorHeadline));
            RefreshList();
        }
    }

    public bool EditorIsActive
    {
        get => _editing is not null && _engine.ActiveMode?.Id == _editing.Id;
        set
        {
            if (_editing is null) return;
            // 走与列表「启用」一致的确认流程；用户取消时 ActiveModeChanged 事件会把开关弹回真实状态
            _ = ToggleModeAsync(_editing);
            OnPropertyChanged();
        }
    }

    public bool EditorManualTrigger
    {
        get => _editing is { AutoTrigger: false };
        set
        {
            if (_editing is null || !value) return;
            _editing.AutoTrigger = false;
            _store.Save(_editing);
            NotifyEditorChanged();
        }
    }

    public bool EditorAutoTrigger
    {
        get => _editing is { AutoTrigger: true };
        set
        {
            if (_editing is null || !value) return;
            _editing.AutoTrigger = true;
            _store.Save(_editing);
            NotifyEditorChanged();
        }
    }

    public string EditorTargetDisplay => _editing?.TargetProcessName is { Length: > 0 } n
        ? (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : $"{n}.exe")
        : "未绑定程序";

    public string EditorTargetSub => _editing?.TargetExePath is { Length: > 0 } p
        ? p : "点击「选择」绑定要监测的程序，启动它时自动开启本模式";

    public string? EditorTargetPath => _editing?.TargetExePath;
    public bool EditorHasTargetIcon => Shared.IconExtractor.HasEmbeddedIcon(EditorTargetPath);
    public bool EditorHasTarget => _editing?.TargetProcessName is { Length: > 0 };
    public string EditorTargetBtnText => EditorHasTarget ? "更换" : "选择";

    public string EditorPreserveTitle => $"保留应用 ({_editing?.PreserveAppKeys.Count ?? 0} 项)";
    public string EditorCloseTitle => $"启动时关闭 ({_editing?.CloseOnStartAppKeys.Count ?? 0} 项)";
    public string EditorRespawnTitle => $"运行期间阻止重新启动 ({_editing?.BlockRespawnAppKeys.Count ?? 0} 项)";

    /// <summary>一句话规则摘要，代替长篇说明文字。</summary>
    public string EditorSummary
    {
        get
        {
            if (_editing is null) return "";
            var parts = new List<string>();
            if (_editing.CloseOnStartAppKeys.Count > 0) parts.Add($"关闭 {_editing.CloseOnStartAppKeys.Count} 项");
            if (_editing.PreserveAppKeys.Count > 0) parts.Add($"保留 {_editing.PreserveAppKeys.Count} 项");
            if (_editing.BlockRespawnAppKeys.Count > 0) parts.Add($"阻止 {_editing.BlockRespawnAppKeys.Count} 项自动重启");
            return parts.Count == 0
                ? "尚未配置规则，可在下方选择要关闭或保留的应用。"
                : $"启用后将尝试{string.Join("、", parts)}；系统关键进程与保护名单始终不受影响。";
        }
    }

    public bool EditorMonitorRespawn
    {
        get => _editing?.MonitorRespawnEnabled ?? false;
        set
        {
            if (_editing is null) return;
            _editing.MonitorRespawnEnabled = value;
            _store.Save(_editing);
            OnPropertyChanged();
        }
    }

    private bool _showAdvanced;
    public bool ShowAdvanced { get => _showAdvanced; set { _showAdvanced = value; OnPropertyChanged(); } }

    // ---- Picker 状态 ----
    private string _pickerTitle = "";
    public string PickerTitle { get => _pickerTitle; set { _pickerTitle = value; OnPropertyChanged(); } }
    private string _pickerSubtitle = "";
    public string PickerSubtitle { get => _pickerSubtitle; set { _pickerSubtitle = value; OnPropertyChanged(); } }

    public ModesViewModel(
        IModeStore store,
        IModeEngine engine,
        IAppCatalog catalog,
        IWhitelistStore whitelist,
        IEventBus bus)
    {
        _store = store;
        _engine = engine;
        _catalog = catalog;
        _whitelist = whitelist;
        _bus = bus;

        _engine.ActiveModeChanged += (_, _) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var m in Modes) m.NotifyChanged();
                OnPropertyChanged(nameof(EditorIsActive));
            });
        };

        RefreshList();
    }

    public void RefreshList()
    {
        Modes.Clear();
        foreach (var p in _store.GetAll())
            Modes.Add(new ModeCardVm(p, _engine));
    }

    public void OpenEditor(ModeProfile profile)
    {
        _editing = profile;
        ShowAdvanced = false;
        NotifyEditorChanged();
        Screen = "editor";
    }

    public void CreateAndEditNewMode()
    {
        var count = _store.GetAll().Count + 1;
        var profile = new ModeProfile
        {
            Id = $"custom-{DateTime.Now:HHmmss}",
            Name = $"自定义模式 {count}",
            Description = "针对特定软件或游戏场景的自定义后台规则",
            IconKind = "custom",
            AutoTrigger = false,
            PreserveAppKeys = ["steam", "nvidia", "realtek", "devin"],
            CloseOnStartAppKeys = ["wpscloudsvr", "lghub_updater", "vbcscompiler"],
            MonitorRespawnEnabled = false,
        };
        _store.Save(profile);
        RefreshList();
        OpenEditor(profile);
    }

    public void DuplicateMode(ModeProfile src)
    {
        var copy = new ModeProfile
        {
            Id = $"copy-{DateTime.Now:HHmmss}",
            Name = $"{src.Name}（副本）",
            Description = src.Description,
            IconKind = src.IconKind,
            AutoTrigger = src.AutoTrigger,
            TargetProcessName = src.TargetProcessName,
            TargetExePath = src.TargetExePath,
            PreserveAppKeys = [.. src.PreserveAppKeys],
            CloseOnStartAppKeys = [.. src.CloseOnStartAppKeys],
            MonitorRespawnEnabled = src.MonitorRespawnEnabled,
            BlockRespawnAppKeys = [.. src.BlockRespawnAppKeys],
        };
        _store.Save(copy);
        RefreshList();
    }

    public void DeleteMode(ModeProfile profile)
    {
        var res = MemoryKeeper.Modules.Shared.MkDialog.Confirm(
            System.Windows.Application.Current?.MainWindow,
            $"删除模式「{profile.Name}」？",
            "删除后不可恢复，不影响已安装应用和其他模式。",
            okText: "删除");
        if (!res) return;

        if (_engine.ActiveMode?.Id == profile.Id)
            _engine.StopActiveMode("模式已被删除");
        _store.Delete(profile.Id);
        RefreshList();
    }

    public async Task ToggleModeAsync(ModeProfile profile)
    {
        if (_engine.ActiveMode?.Id == profile.Id)
        {
            _engine.StopActiveMode("用户在模式页手动停止");
            RefreshList();
            return;
        }

        if (_engine.ActiveMode is { } current && current.Id != profile.Id)
        {
            var confirm = MemoryKeeper.Modules.Shared.MkDialog.Confirm(
                System.Windows.Application.Current?.MainWindow,
                $"切换到「{profile.Name}」？",
                $"当前运行的「{current.Name}」将停止，新规则立即生效。",
                okText: "切换");
            if (!confirm) return;
        }

        // 启用前明确告知将要执行的操作与保留项目，避免未经确认就结束后台进程
        var respawn = profile.MonitorRespawnEnabled && profile.BlockRespawnAppKeys.Count > 0
            ? $"，阻止 {profile.BlockRespawnAppKeys.Count} 项自动重启"
            : "";
        var target = profile.AutoTrigger && profile.TargetProcessName is not null
            ? $"\n也会在「{profile.TargetProcessName}」启动时自动启用，目标退出后自动停止。"
            : "";
        var preview = MemoryKeeper.Modules.Shared.MkDialog.Confirm(
            System.Windows.Application.Current?.MainWindow,
            $"启用「{profile.Name}」？",
            $"启用后立即生效：尝试关闭 {profile.CloseOnStartAppKeys.Count} 项、保留 {profile.PreserveAppKeys.Count} 项{respawn}；" +
            $"系统关键进程与保护名单不受影响。已关闭的应用不会自动恢复。{target}",
            okText: "启用");
        if (!preview) return;

        await _engine.EnableModeAsync(profile.Id, autoTriggered: false);
        RefreshList();
    }

    public void PickTargetExe()
    {
        if (_editing is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "选择触发此模式的目标游戏或软件 (.exe)",
            Filter = "可执行程序 (*.exe)|*.exe",
        };
        if (dlg.ShowDialog() == true)
        {
            _editing.TargetExePath = dlg.FileName;
            _editing.TargetProcessName = Path.GetFileNameWithoutExtension(dlg.FileName);
            _editing.AutoTrigger = true;
            _store.Save(_editing);
            NotifyEditorChanged();
            RefreshList();
        }
    }

    public void ClearTargetExe()
    {
        if (_editing is null) return;
        _editing.TargetExePath = null;
        _editing.TargetProcessName = null;
        _editing.AutoTrigger = false;
        _store.Save(_editing);
        NotifyEditorChanged();
        RefreshList();
    }

    public async Task OpenPickerAsync(string targetList)
    {
        if (_editing is null) return;
        _pickerTargetList = targetList;

        (PickerTitle, PickerSubtitle, var currentSet) = targetList switch
        {
            "preserve" => ("管理保留应用", "勾选在本模式运行期间绝不关闭的应用", _editing.PreserveAppKeys),
            "respawn" => ("管理防重新启动名单", "勾选在模式运行期间若被后台重新拉起需再次拦截的应用", _editing.BlockRespawnAppKeys),
            _ => ("管理启动时关闭名单", "勾选在启用本模式时需要自动关闭的后台应用（全局保护优先）", _editing.CloseOnStartAppKeys),
        };

        var selectedSet = new HashSet<string>(currentSet, StringComparer.OrdinalIgnoreCase);
        var groups = await Task.Run(() => _catalog.Scan());

        PickerItems.Clear();
        foreach (var g in groups)
        {
            var isGlobalProtected = g.IsProtected || _whitelist.IsProtected(g.Key);
            PickerItems.Add(new PickerAppVm
            {
                Key = g.Key,
                DisplayName = g.DisplayName,
                Description = g.Description ?? g.Publisher ?? "第三方后台应用",
                IconPath = g.Processes.FirstOrDefault(p => p.Path is not null)?.Path,
                MemoryText = $"{Math.Max(1, g.TotalMemoryBytes / 1048576.0):F0} MB",
                IsChecked = selectedSet.Contains(g.Key) || (g.MatchedRuleId is not null && selectedSet.Contains(g.MatchedRuleId)),
                ShowConflictHint = targetList != "preserve" && isGlobalProtected,
            });
        }
        Screen = "picker";
    }

    public void SavePicker()
    {
        if (_editing is null) return;
        var checkedKeys = PickerItems.Where(i => i.IsChecked).Select(i => i.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        switch (_pickerTargetList)
        {
            case "preserve":
                _editing.PreserveAppKeys = checkedKeys;
                break;
            case "respawn":
                _editing.BlockRespawnAppKeys = checkedKeys;
                if (checkedKeys.Count > 0) _editing.MonitorRespawnEnabled = true;
                break;
            default:
                _editing.CloseOnStartAppKeys = checkedKeys;
                break;
        }
        _store.Save(_editing);
        RefreshList();
        NotifyEditorChanged();
        Screen = "editor";
    }

    private void NotifyEditorChanged()
    {
        OnPropertyChanged(nameof(EditorHeadline));
        OnPropertyChanged(nameof(EditorModeName));
        OnPropertyChanged(nameof(EditorIsActive));
        OnPropertyChanged(nameof(EditorManualTrigger));
        OnPropertyChanged(nameof(EditorAutoTrigger));
        OnPropertyChanged(nameof(EditorTargetDisplay));
        OnPropertyChanged(nameof(EditorTargetSub));
        OnPropertyChanged(nameof(EditorTargetPath));
        OnPropertyChanged(nameof(EditorHasTargetIcon));
        OnPropertyChanged(nameof(EditorHasTarget));
        OnPropertyChanged(nameof(EditorTargetBtnText));
        OnPropertyChanged(nameof(EditorPreserveTitle));
        OnPropertyChanged(nameof(EditorCloseTitle));
        OnPropertyChanged(nameof(EditorRespawnTitle));
        OnPropertyChanged(nameof(EditorMonitorRespawn));
        OnPropertyChanged(nameof(EditorSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class DisabledView : UserControl
{
    private readonly ModesViewModel _vm;

    public DisabledView(
        IModeStore modeStore,
        IModeEngine modeEngine,
        IAppCatalog catalog,
        IWhitelistStore whitelist,
        IEventBus bus)
    {
        InitializeComponent();
        _vm = new ModesViewModel(modeStore, modeEngine, catalog, whitelist, bus);
        DataContext = _vm;
    }

    public ModesViewModel ViewModel => _vm;

    private static ModeCardVm? CardOf(object sender)
        => (sender as FrameworkElement)?.Tag as ModeCardVm
           ?? (sender as FrameworkElement)?.DataContext as ModeCardVm;

    private async void ToggleMode_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card)
            await _vm.ToggleModeAsync(card.Profile);
    }

    private void EditMode_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card)
            _vm.OpenEditor(card.Profile);
    }

    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || CardOf(sender) is null) return;
        var menu = new ContextMenu { Style = (Style)FindResource("RowMenu") };
        (string header, Action act)[] items =
        [
            ("编辑模式规则", () => _vm.OpenEditor(CardOf(fe)!.Profile)),
            ("复制此模式", () => _vm.DuplicateMode(CardOf(fe)!.Profile)),
            ("删除此模式", () => _vm.DeleteMode(CardOf(fe)!.Profile)),
        ];
        foreach (var (header, act) in items)
        {
            var mi = new MenuItem { Header = header, Style = (Style)FindResource("RowMenuItem") };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = fe;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void DuplicateMode_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card)
            _vm.DuplicateMode(card.Profile);
    }

    private void DeleteMode_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card)
            _vm.DeleteMode(card.Profile);
    }

    private void CreateNewMode_Click(object sender, RoutedEventArgs e) => _vm.CreateAndEditNewMode();
    private void BackToList_Click(object sender, RoutedEventArgs e) => _vm.Screen = "list";

    private void PickTargetExe_Click(object sender, RoutedEventArgs e) => _vm.PickTargetExe();
    private void ClearTargetExe_Click(object sender, RoutedEventArgs e) => _vm.ClearTargetExe();

    private async void ManagePreserve_Click(object sender, RoutedEventArgs e) => await _vm.OpenPickerAsync("preserve");
    private async void ManageClose_Click(object sender, RoutedEventArgs e) => await _vm.OpenPickerAsync("close");
    private async void ManageRespawn_Click(object sender, RoutedEventArgs e) => await _vm.OpenPickerAsync("respawn");

    private void ToggleAdvanced_Click(object sender, MouseButtonEventArgs e) => _vm.ShowAdvanced = !_vm.ShowAdvanced;
    private void ClosePicker_Click(object sender, RoutedEventArgs e) => _vm.Screen = "editor";
    private void SavePicker_Click(object sender, RoutedEventArgs e) => _vm.SavePicker();
}
