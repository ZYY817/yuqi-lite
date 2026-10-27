using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using MemoryKeeper.Contracts;
using UserControl = System.Windows.Controls.UserControl;

namespace MemoryKeeper.Modules.Whitelist;

public sealed class WhitelistItemVm
{
    public required string Key { get; init; }
    public string? RuleId { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public string? IconPath { get; init; }
    public string Memory { get; init; } = "";
    /// <summary>system | auto | manual | candidate</summary>
    public string Kind { get; init; } = "manual";

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public bool HasIcon => Shared.IconExtractor.HasEmbeddedIcon(IconPath);
    public bool HasPath => !string.IsNullOrEmpty(IconPath) && File.Exists(IconPath);
    /// <summary>系统关键进程行：只读，不可移除。</summary>
    public bool IsSystemRow => Kind == "system";
    /// <summary>已保护项（自动/手动）：可移除。</summary>
    public bool ShowRemove => Kind is "auto" or "manual";
    /// <summary>候选项：可加入保护。</summary>
    public bool ShowAdd => Kind == "candidate";
}

public sealed class WhitelistVm : INotifyPropertyChanged
{
    private readonly IWhitelistStore _store;
    private readonly IAppCatalog _catalog;
    private readonly ISettingsStore _settings;
    private readonly IHistoryStore _history;
    private readonly IEventBus _bus;
    private readonly ISystemCriticalProvider _systemCritical;

    public ObservableCollection<WhitelistItemVm> SystemItems { get; } = [];
    public ObservableCollection<WhitelistItemVm> AutoItems { get; } = [];
    public ObservableCollection<WhitelistItemVm> ManualItems { get; } = [];
    public ObservableCollection<WhitelistItemVm> CandidateItems { get; } = [];

    // ---- 分段 Tab：system | auto | manual | add ----
    private string _tab = "auto";
    public string Tab
    {
        get => _tab;
        set
        {
            _tab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSystemTab));
            OnPropertyChanged(nameof(IsAutoTab));
            OnPropertyChanged(nameof(IsManualTab));
            OnPropertyChanged(nameof(IsAddTab));
            OnPropertyChanged(nameof(TabIndex));
        }
    }
    public bool IsSystemTab { get => Tab == "system"; set { if (value) Tab = "system"; } }
    public bool IsAutoTab { get => Tab == "auto"; set { if (value) Tab = "auto"; } }
    public bool IsManualTab { get => Tab == "manual"; set { if (value) Tab = "manual"; } }
    public bool IsAddTab { get => Tab == "add"; set { if (value) Tab = "add"; } }

    /// <summary>滑动 Tab 选中索引：0 系统 / 1 自动 / 2 手动；"add" 面板激活时返回 -1 隐藏滑块。</summary>
    public int TabIndex
    {
        get => Tab switch { "auto" => 1, "manual" => 2, "system" => 0, _ => -1 };
        set { if (value >= 0) Tab = value switch { 1 => "auto", 2 => "manual", _ => "system" }; OnPropertyChanged(); }
    }

    private List<string> _tabLabels = ["系统关键进程", "自动保护", "手动保护"];
    public List<string> TabLabels { get => _tabLabels; private set { _tabLabels = value; OnPropertyChanged(); } }
    public bool ManualEmpty => ManualItems.Count == 0;

    private bool _autoProtect = true;
    public bool AutoProtect
    {
        get => _autoProtect;
        set
        {
            _autoProtect = value;
            _settings.Set("whitelist", "autoProtect", value ? "1" : "0");
            OnPropertyChanged();
            _bus.Publish(new AppsChangedMessage());
            _ = RefreshAsync();
        }
    }

    public WhitelistVm(
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
        _autoProtect = _settings.Get("whitelist", "autoProtect") != "0";
        _bus.Subscribe<WhitelistChangedMessage>(_ => System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshAsync));
        _bus.Subscribe<AppsChangedMessage>(_ => System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshAsync));
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var groups = await Task.Run(() => _catalog.Scan());
        var critical = await Task.Run(() => _systemCritical.GetRunningCritical());
        var manualKeys = _store.GetAll();

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SystemItems.Clear();
            foreach (var p in critical)
            {
                SystemItems.Add(new WhitelistItemVm
                {
                    Key = $"sys:{p.Name.ToLowerInvariant()}",
                    Name = $"{p.Name}.exe",
                    Detail = "Windows 系统关键进程 · 安全策略强制保护",
                    IconPath = p.Path,
                    Memory = $"{Math.Max(1, p.MemoryBytes / 1048576.0):F0} MB",
                    Kind = "system",
                });
            }

            AutoItems.Clear();
            foreach (var g in groups.Where(g => g.IsProtected && !manualKeys.ContainsKey(g.Key)))
            {
                AutoItems.Add(new WhitelistItemVm
                {
                    Key = g.Key,
                    RuleId = g.MatchedRuleId,
                    Name = g.DisplayName,
                    Detail = g.Description ?? "系统推荐的关键组件，默认保留",
                    IconPath = g.Processes.FirstOrDefault(p => p.Path is not null)?.Path,
                    Memory = $"{Math.Max(1, g.TotalMemoryBytes / 1048576.0):F0} MB",
                    Kind = "auto",
                });
            }

            ManualItems.Clear();
            foreach (var kv in manualKeys)
            {
                var g = groups.FirstOrDefault(x => x.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                ManualItems.Add(new WhitelistItemVm
                {
                    Key = kv.Key,
                    RuleId = g?.MatchedRuleId,
                    Name = g?.DisplayName ?? kv.Value,
                    Detail = g?.Description ?? "手动添加的应用，不会被清理或关闭",
                    IconPath = g?.Processes.FirstOrDefault(p => p.Path is not null)?.Path,
                    Memory = g is null ? "未运行" : $"{Math.Max(1, g.TotalMemoryBytes / 1048576.0):F0} MB",
                    Kind = "manual",
                });
            }

            CandidateItems.Clear();
            var candidates = groups
                .Where(g => !g.IsProtected)
                .OrderByDescending(g => g.HasVisibleWindow)
                .ThenByDescending(g => g.TotalMemoryBytes)
                .Take(10);
            foreach (var g in candidates)
            {
                CandidateItems.Add(new WhitelistItemVm
                {
                    Key = g.Key,
                    RuleId = g.MatchedRuleId,
                    Name = g.DisplayName,
                    Detail = g.Description ?? "建议在使用时保持运行，避免误关闭",
                    IconPath = g.Processes.FirstOrDefault(p => p.Path is not null)?.Path,
                    Memory = $"{Math.Max(1, g.TotalMemoryBytes / 1048576.0):F0} MB",
                    Kind = "candidate",
                });
            }

            TabLabels = [$"系统关键进程 ({SystemItems.Count})", $"自动保护 ({AutoItems.Count})", $"手动保护 ({ManualItems.Count})"];
            OnPropertyChanged(nameof(ManualEmpty));
        });
    }

    public void AddProtect(WhitelistItemVm item)
    {
        _store.Protect(item.Key, item.Name);
        _history.Add(new HistoryRecord
        {
            Time = DateTime.Now,
            AppKey = item.Key,
            DisplayName = item.Name,
            Action = "protected",
            Detail = "已手动加入保护名单",
            Restorable = true,
        });
        _bus.Publish(new WhitelistChangedMessage(item.Key, true));
        _bus.Publish(new AppsChangedMessage());
        _ = RefreshAsync();
    }

    public void RemoveItemWithConfirm(WhitelistItemVm item)
    {
        var confirm = MemoryKeeper.Modules.Shared.MkDialog.Confirm(
            System.Windows.Application.Current?.MainWindow,
            $"移除「{item.Name}」的保护？",
            "移除后它会重新进入清理候选列表（当前不会被结束）。",
            okText: "移除保护");
        if (!confirm) return;

        _store.Unprotect(item.Key);
        if (item.RuleId is not null)
            _store.Unprotect(item.RuleId);
        _history.MarkRestored(item.Key, "protected");
        _bus.Publish(new WhitelistChangedMessage(item.Key, false));
        _bus.Publish(new AppsChangedMessage());
        _ = RefreshAsync();
    }

    /// <summary>恢复默认保护：带确认提示；仅还原自动保护排除项与开关，不删除用户手动保护。</summary>
    public void ResetDefaultProtectionWithConfirm()
    {
        var res = MemoryKeeper.Modules.Shared.MkDialog.Confirm(
            System.Windows.Application.Current?.MainWindow,
            "恢复默认保护？",
            "重新启用自动保护，并撤销此前手动解除的项；你手动添加的保护不受影响，也不会结束任何进程。",
            okText: "恢复默认");
        if (!res) return;

        _store.ResetExclusions();
        _autoProtect = true;
        _settings.Set("whitelist", "autoProtect", "1");
        OnPropertyChanged(nameof(AutoProtect));
        _history.Add(new HistoryRecord
        {
            Time = DateTime.Now,
            AppKey = "whitelist:default",
            DisplayName = "保护名单",
            Action = "protected",
            Detail = "已恢复默认保护策略",
            Restorable = false,
        });
        _bus.Publish(new AppsChangedMessage());
        _ = RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class WhitelistView : UserControl
{
    private readonly WhitelistVm _vm;

    public WhitelistView(
        IWhitelistStore store,
        IAppCatalog catalog,
        ISettingsStore settings,
        IHistoryStore history,
        IEventBus bus,
        ISystemCriticalProvider systemCritical)
    {
        InitializeComponent();
        _vm = new WhitelistVm(store, catalog, settings, history, bus, systemCritical);
        DataContext = _vm;
    }

    private void GoAddTab_Click(object sender, RoutedEventArgs e) => _vm.Tab = "add";

    private void SingleRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is WhitelistItemVm item)
            _vm.RemoveItemWithConfirm(item);
    }

    private void AddProtect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is WhitelistItemVm item)
            _vm.AddProtect(item);
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is WhitelistItemVm item && item.HasPath)
        {
            try { Process.Start("explorer.exe", $"/select,\"{item.IconPath}\""); } catch { }
        }
    }

    private void RuleHint_Click(object sender, RoutedEventArgs e) => _vm.ResetDefaultProtectionWithConfirm();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();
}
