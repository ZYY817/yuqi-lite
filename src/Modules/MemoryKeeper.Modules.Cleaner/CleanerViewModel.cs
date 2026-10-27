using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using MemoryKeeper.Contracts;

namespace MemoryKeeper.Modules.Cleaner;

public enum CleanerScreen
{
    Home,
    Detail,
    Progress,
    Result,
}

public sealed class CleanerViewModel : INotifyPropertyChanged
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

    private List<AppItemViewModel> _allApps = [];
    private CancellationTokenSource? _cleanupCts;
    private CleanerScreen _previousScreen = CleanerScreen.Home;

    /// <summary>首页当前 Tab 过滤后的应用列表。</summary>
    public ObservableCollection<AppItemViewModel> HomeItems { get; } = [];
    /// <summary>清理进行中实时进度列表。</summary>
    public ObservableCollection<ProgressRowVm> ProgressItems { get; } = [];
    /// <summary>清理结果：已成功关闭项。</summary>
    public ObservableCollection<ResultRowVm> ResultClosedItems { get; } = [];
    /// <summary>清理结果：仍需处理项。</summary>
    public ObservableCollection<ResultRowVm> ResultAttentionItems { get; } = [];

    private CleanerScreen _screen = CleanerScreen.Home;
    public CleanerScreen Screen
    {
        get => _screen;
        set
        {
            if (_screen == value) return;
            _screen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHomeScreen));
            OnPropertyChanged(nameof(IsDetailScreen));
            OnPropertyChanged(nameof(IsProgressScreen));
            OnPropertyChanged(nameof(IsResultScreen));
        }
    }

    public bool IsHomeScreen => Screen == CleanerScreen.Home;
    public bool IsDetailScreen => Screen == CleanerScreen.Detail;
    public bool IsProgressScreen => Screen == CleanerScreen.Progress;
    public bool IsResultScreen => Screen == CleanerScreen.Result;

    private string _headline = "正在扫描后台应用…";
    public string Headline { get => _headline; private set => Set(ref _headline, value); }

    private string _summaryLine = "正在读取进程与系统物理内存数据…";
    public string SummaryLine { get => _summaryLine; private set => Set(ref _summaryLine, value); }

    private string _sectionHeader = "建议处理 (0)";
    public string SectionHeader { get => _sectionHeader; private set => Set(ref _sectionHeader, value); }

    private string _cleanText = "请勾选要处理的后台应用";
    public string CleanText { get => _cleanText; private set => Set(ref _cleanText, value); }

    private string _estimateCaption = "";
    public string EstimateCaption { get => _estimateCaption; private set => Set(ref _estimateCaption, value); }

    // ---- 当前模式卡片状态（如实展示，未启用不伪装已开启） ----
    public string CurrentModeName => _modeEngine.ActiveMode?.Name
        ?? _modeStore.GetAll().FirstOrDefault(m => m.AutoTrigger)?.Name
        ?? "未启用模式";
    public string CurrentModeSubtitle => _modeEngine.ActiveMode is not null ? "运行中"
        : _modeStore.GetAll().Any(m => m.AutoTrigger && m.TargetProcessName is not null) ? "等待目标启动"
        : _modeStore.GetAll().Count > 0 ? "可在模式页开启" : "尚未创建";
    /// <summary>首页模式开关：有活动模式时关闭它；未启用时跳模式页选择（开关随后弹回真实状态）。</summary>
    public bool ModeSwitchOn
    {
        get => _modeEngine.ActiveMode is not null;
        set
        {
            if (value) _ = QuickModeActionAsync();
            else _modeEngine.StopActiveMode("用户在首页关闭模式");
            // 未配置模式时开关会被弹回：用真实状态回写
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                () => OnPropertyChanged(nameof(ModeSwitchOn)));
        }
    }

    // ---- 首页分段 Tab：suggested | others（已保护项归「保护」页统一管理） ----
    private string _homeTab = "suggested";
    public string HomeTab
    {
        get => _homeTab;
        set
        {
            if (Set(ref _homeTab, value))
            {
                OnPropertyChanged(nameof(IsSuggestedTab));
                OnPropertyChanged(nameof(IsOthersTab));
                OnPropertyChanged(nameof(HomeTabIndex));
                RebuildHomeItems();
            }
        }
    }
    public bool IsSuggestedTab { get => HomeTab == "suggested"; set { if (value) HomeTab = "suggested"; } }
    public bool IsOthersTab { get => HomeTab == "others"; set { if (value) HomeTab = "others"; } }

    /// <summary>滑动 Tab 选中索引：0 建议 / 1 其他（已保护应用统一由「保护」页管理，不在清理页出现）。</summary>
    public int HomeTabIndex
    {
        get => HomeTab == "others" ? 1 : 0;
        set => HomeTab = value == 1 ? "others" : "suggested";
    }

    private List<string> _homeTabLabels = ["建议处理", "其他后台应用", "已保护"];
    public List<string> HomeTabLabels { get => _homeTabLabels; private set => Set(ref _homeTabLabels, value); }

    // ---- 「其他后台应用」Tab 的搜索与排序 ----
    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set { if (Set(ref _searchQuery, value)) RebuildHomeItems(); }
    }

    private bool _sortByMemory = true;
    public bool SortByMemory
    {
        get => _sortByMemory;
        set { if (Set(ref _sortByMemory, value)) { OnPropertyChanged(nameof(SortButtonText)); RebuildHomeItems(); } }
    }
    public string SortButtonText => SortByMemory ? "排序：按内存占用 ↓" : "排序：按应用名称 A-Z";

    private bool _homeEmpty;
    public bool HomeEmpty { get => _homeEmpty; private set => Set(ref _homeEmpty, value); }

    // ---- 清理进度状态 ----
    private int _progressPercent;
    public int ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
    private string _progressSubtitle = "正在清理...";
    public string ProgressSubtitle { get => _progressSubtitle; private set => Set(ref _progressSubtitle, value); }
    private string _progressEta = "";
    public string ProgressEta { get => _progressEta; private set => Set(ref _progressEta, value); }

    // ---- 清理结果状态 ----
    private string _resultHeadline = "清理完成";
    public string ResultHeadline { get => _resultHeadline; private set => Set(ref _resultHeadline, value); }
    private string _resultSubtitle = "";
    public string ResultSubtitle { get => _resultSubtitle; private set => Set(ref _resultSubtitle, value); }
    private int _resultClosedCount;
    public int ResultClosedCount { get => _resultClosedCount; private set => Set(ref _resultClosedCount, value); }
    private int _resultAttentionCount;
    public int ResultAttentionCount { get => _resultAttentionCount; private set => Set(ref _resultAttentionCount, value); }
    private string _resultMemoryText = "0 MB";
    public string ResultMemoryText { get => _resultMemoryText; private set => Set(ref _resultMemoryText, value); }
    private string _resultMemoryLabel = "进程工作集释放";
    public string ResultMemoryLabel { get => _resultMemoryLabel; private set => Set(ref _resultMemoryLabel, value); }
    private string _resultObservedText = "--";
    public string ResultObservedText { get => _resultObservedText; private set => Set(ref _resultObservedText, value); }
    private string _resultObservedLabel = "实测可用内存变化";
    public string ResultObservedLabel { get => _resultObservedLabel; private set => Set(ref _resultObservedLabel, value); }
    private string _resultStatsLine = "";
    public string ResultStatsLine { get => _resultStatsLine; private set => Set(ref _resultStatsLine, value); }

    private bool _busy;
    public bool Busy { get => _busy; private set { _busy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanClean)); } }
    public bool CanClean => !Busy && _allApps.Any(i => i.IsSelected);

    private AppDetailViewModel? _detail;
    public AppDetailViewModel? Detail { get => _detail; private set => Set(ref _detail, value); }

    private CleanupBatchReport? _lastReport;
    public bool HasLastReport => _lastReport is not null;

    public CleanerViewModel(
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

        _respawnBlocker.Changed += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshAsync);

        _modeEngine.ActiveModeChanged += (_, _) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(CurrentModeName));
                OnPropertyChanged(nameof(CurrentModeSubtitle));
                OnPropertyChanged(nameof(ModeSwitchOn));
            });
        };

        _bus.Subscribe<WhitelistChangedMessage>(_ => System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshAsync));
        _bus.Subscribe<AppsChangedMessage>(_ =>
        {
            if (Screen is CleanerScreen.Home)
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshAsync);
        });

        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        Busy = true;
        var groupsTask = Task.Run(() => _catalog.Scan());
        var memSnap = _sysMem.GetSnapshot();
        var groups = await groupsTask;

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _allApps = groups.Select(g =>
            {
                var preselect = g.Level == SafetyLevel.SafeToClose && !g.IsProtected;
                var vm = new AppItemViewModel(g, preselect, _respawnBlocker);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AppItemViewModel.IsSelected))
                    {
                        UpdateCleanText();
                        OnPropertyChanged(nameof(CanClean));
                    }
                };
                return vm;
            }).ToList();

            var suggested = _allApps.Where(a => a.Group.Level == SafetyLevel.SafeToClose && !a.IsProtected).ToList();

            RebuildHomeItems();

            var procCount = groups.Sum(g => g.Processes.Count);
            var usedGb = memSnap.UsedPhysicalBytes / 1073741824.0;
            var totalGb = memSnap.TotalPhysicalBytes / 1073741824.0;
            var othersCount = _allApps.Count - suggested.Count - _allApps.Count(a => a.IsProtected);

            SummaryLine = $"{procCount} 个进程 · {suggested.Count} 项建议处理 · 系统内存 {usedGb:F1} / {totalGb:F1} GB（{memSnap.MemoryLoadPercent}%）";
            Headline = groups.Count == 0 ? "没有发现需要处理的后台应用" : $"发现 {groups.Count} 个后台应用";
            HomeTabLabels = [$"建议处理 ({suggested.Count})", $"其他后台应用 ({othersCount})"];

            UpdateCleanText();
            Busy = false;
        });
    }

    private void RebuildHomeItems()
    {
        IEnumerable<AppItemViewModel> query = HomeTab switch
        {
            "others" => _allApps.Where(a => !a.IsProtected && a.Group.Level != SafetyLevel.SafeToClose),
            _ => _allApps.Where(a => a.Group.Level == SafetyLevel.SafeToClose && !a.IsProtected),
        };

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            query = query.Where(a =>
                a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || a.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)
                || a.Group.Key.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        query = SortByMemory
            ? query.OrderByDescending(a => a.Group.TotalMemoryBytes)
            : query.OrderBy(a => a.DisplayName);

        HomeItems.Clear();
        foreach (var item in query)
            HomeItems.Add(item);

        SectionHeader = HomeTab == "others" ? "其他后台应用（暂无明确依据建议处理）" : "建议处理";
        HomeEmpty = HomeItems.Count == 0;
    }

    private void UpdateCleanText()
    {
        var selected = _allApps.Where(i => i.IsSelected).ToList();
        var n = selected.Count;
        var wsSum = selected.Sum(i => i.Group.TotalMemoryBytes);
        CleanText = n == 0 ? "请勾选要处理的后台应用" : $"尝试关闭选中的 {n} 项";
        EstimateCaption = n == 0
            ? ""
            : $"预计可回收约 {FormatSize(wsSum)}，实际结果以处理后检测为准。";
    }

    public void ToggleSelectVisible(IEnumerable<AppItemViewModel> items)
    {
        // 受保护应用默认不参与批量处理
        var list = items.Where(i => !i.IsProtected).ToList();
        var allChecked = list.Count > 0 && list.All(i => i.IsSelected);
        foreach (var item in list)
            item.IsSelected = !allChecked;
    }

    // ==================== 模式快捷操作 ====================

    public async Task QuickModeActionAsync()
    {
        if (_modeEngine.ActiveMode is not null)
        {
            _modeEngine.StopActiveMode("用户在首页手动停止模式");
            await RefreshAsync();
            return;
        }
        // 未启用模式时跳转模式页由用户选择，避免在首页直接按默认规则结束后台进程
        GoToModesTab();
    }

    public void GoToModesTab() => _bus.Publish(new NavigateToTabMessage("modes"));
    public void ShowAllApps() => HomeTab = "others";

    // ==================== 真实清理与结果验证闭环 ====================

    public async Task CleanSelectedAsync()
    {
        var selected = _allApps.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;
        await RunCleanupWithProgressAsync(selected.Select(s => s.Group).ToList(), allowForceKill: false);
    }

    public async Task RunCleanupWithProgressAsync(IReadOnlyList<AppGroup> targets, bool allowForceKill)
    {
        if (targets.Count == 0) return;

        // 对用户主动勾选清理的受保护项，先解除其保护覆盖
        foreach (var g in targets.Where(g => g.IsProtected))
        {
            _whitelist.Unprotect(g.Key);
            if (g.MatchedRuleId is not null) _whitelist.Unprotect(g.MatchedRuleId);
            g.IsProtected = false;
        }

        Busy = true;
        ProgressItems.Clear();
        foreach (var g in targets)
        {
            ProgressItems.Add(new ProgressRowVm
            {
                AppKey = g.Key,
                DisplayName = g.DisplayName,
                IconPath = g.Processes.FirstOrDefault(p => p.Path is not null)?.Path,
            });
        }

        ProgressPercent = 5;
        ProgressSubtitle = $"0 / {targets.Count} 项";
        ProgressEta = "";
        var startedAt = DateTime.Now;
        Screen = CleanerScreen.Progress;

        // 记录各应用的进程名，清理后做延迟复核，避免"已关闭"其实被守护进程重新拉起
        _lastCleanupProcNames = targets.ToDictionary(
            g => g.Key,
            g => g.Processes.Select(p => NormalizeProcName(p.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        _cleanupCts = new CancellationTokenSource();
        var progress = new Progress<(int CompletedCount, int TotalCount, CleanupItemReport Latest)>(update =>
        {
            var (done, total, latest) = update;
            ProgressPercent = total == 0 ? 100 : Math.Clamp((int)Math.Round(done * 100.0 / total), 8, 100);
            ProgressSubtitle = $"{done} / {total} 项";

            if (done > 0 && done < total)
            {
                var avg = (DateTime.Now - startedAt).TotalSeconds / done;
                ProgressEta = $"预计剩余约 {Math.Max(1, (int)Math.Round(avg * (total - done)))} 秒";
            }
            else
            {
                ProgressEta = done >= total ? "正在核验结果..." : "";
            }

            var row = ProgressItems.FirstOrDefault(r => r.AppKey.Equals(latest.AppKey, StringComparison.OrdinalIgnoreCase));
            row?.ApplyStatus(latest.Status, latest.StatusText);
        });

        // 给界面一个短暂过渡以便用户看清实时进度状态
        await Task.Delay(250);
        var report = await _cleanup.ExecuteBatchAsync(targets, allowForceKill, progress, _cleanupCts.Token);
        _cleanupCts.Dispose();
        _cleanupCts = null;

        ApplyBatchReport(report);
        Screen = CleanerScreen.Result;
        Busy = false;
        _ = DetectLateRespawnsAsync();
        _ = RefreshAsync();
    }

    private Dictionary<string, string[]> _lastCleanupProcNames = new(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeProcName(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// 延迟二次核验：杀完 350ms 内的即时核验可能漏掉几秒后才被守护进程重新拉起的应用。
    /// 发现复活的"已关闭"项如实地改判为"已重新启动"并计入仍需处理。
    /// </summary>
    private async Task DetectLateRespawnsAsync()
    {
        var watch = _lastCleanupProcNames;
        var closedRows = ResultClosedItems.Where(r => r.Report.Status is CleanupItemStatus.Closed or CleanupItemStatus.AlreadyExited).ToList();
        if (watch.Count == 0 || closedRows.Count == 0) return;

        await Task.Delay(4200);

        var respawnedKeys = await Task.Run(() =>
        {
            var keys = new List<string>();
            foreach (var row in closedRows)
            {
                if (!watch.TryGetValue(row.Report.AppKey, out var names)) continue;
                foreach (var name in names)
                {
                    try
                    {
                        if (Process.GetProcessesByName(name).Length > 0) { keys.Add(row.Report.AppKey); break; }
                    }
                    catch { }
                }
            }
            return keys;
        });
        if (respawnedKeys.Count == 0) return;

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var key in respawnedKeys)
            {
                var row = ResultClosedItems.FirstOrDefault(r => r.Report.AppKey.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (row is null) continue;
                ResultClosedItems.Remove(row);
                row.MarkRespawned();
                ResultAttentionItems.Add(row);
                _history.Add(new HistoryRecord
                {
                    Time = DateTime.Now,
                    AppKey = row.Report.AppKey,
                    DisplayName = row.Report.DisplayName,
                    Action = "respawned",
                    Detail = "进程结束后不久被守护组件重新拉起，当前仍在运行",
                    MemoryBytes = row.Report.WorkingSetBytes,
                    NeedsAttention = true,
                    Restorable = false,
                });
            }
            ResultClosedCount = ResultClosedItems.Count;
            ResultAttentionCount = ResultAttentionItems.Count;
            ResultSubtitle = $"已成功关闭 {ResultClosedCount} 项 · 仍需处理 {ResultAttentionCount} 项";

            // 主动引导：结束后又被拉起的应用，提供一键「禁止自动重启」
            var names = string.Join("、", ResultAttentionItems
                .Where(r => respawnedKeys.Contains(r.Report.AppKey))
                .Select(r => $"「{r.DisplayName}」"));
            var res = MemoryKeeper.Modules.Shared.MkDialog.Confirm(
                System.Windows.Application.Current?.MainWindow,
                "检测到应用反复重启",
                $"{names} 结束后不久被守护组件重新拉起，目前仍在运行。\n\n" +
                "这类应用由自身服务/计划任务守护，单次清理无法阻止它复活。要禁止它在本软件运行期间自动重启吗？（拦截期间若你手动重新打开该应用窗口，不会被误结束）",
                okText: "禁止重启",
                cancelText: "暂不处理");
            if (res)
            {
                foreach (var key in respawnedKeys)
                {
                    var item = _allApps.FirstOrDefault(a => a.Group.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (item is null || _respawnBlocker.IsGroupBlocked(item.Group)) continue;
                    // 服务/SYSTEM 级进程拦不动：引导提权重启而非假装设置成功
                    if (item.Group.NeedsElevation && !MemoryKeeper.Modules.Shared.Elevation.IsAdmin)
                        RestartElevatedWithConfirm(item.DisplayName);
                    else
                        _respawnBlocker.Block(item.Group);
                }
            }
        });
    }

    public void CancelCleanup() => _cleanupCts?.Cancel();

    private void UpdateResultStatsLine()
        => ResultStatsLine = $"关闭工作集 {ResultMemoryText} · 实测可用内存 {ResultObservedText}";

    private void ApplyBatchReport(CleanupBatchReport report)
    {
        _lastReport = report;
        OnPropertyChanged(nameof(HasLastReport));

        ResultClosedCount = report.ClosedCount;
        ResultAttentionCount = report.NeedsAttentionCount;
        ResultHeadline = report.WasCancelled ? "清理已中止" : "清理完成";
        ResultSubtitle = $"已成功关闭 {report.ClosedCount} 项 · 仍需处理 {report.NeedsAttentionCount} 项"
            + (report.SkippedCount > 0 ? $" · 已跳过 {report.SkippedCount} 项" : "");

        // 两个指标分开：已关闭进程工作集（预估口径） vs 系统可用物理内存实测变化
        ResultMemoryText = FormatSize(report.ClosedWorkingSetBytes);
        ResultMemoryLabel = "已关闭进程工作集";
        ResultObservedText = report.ObservedSystemFreedBytes >= 8 * 1048576
            ? $"+{FormatSize(report.ObservedSystemFreedBytes)}"
            : "无显著变化";
        ResultObservedLabel = "实测可用内存变化";
        UpdateResultStatsLine();

        ResultClosedItems.Clear();
        foreach (var item in report.Items.Where(i => i.Status is CleanupItemStatus.Closed or CleanupItemStatus.AlreadyExited))
            ResultClosedItems.Add(new ResultRowVm(item, this));

        ResultAttentionItems.Clear();
        foreach (var item in report.Items.Where(i => i.Status is not (CleanupItemStatus.Closed or CleanupItemStatus.AlreadyExited)))
            ResultAttentionItems.Add(new ResultRowVm(item, this));
    }

    public void ShowLastResult()
    {
        if (_lastReport is not null)
            Screen = CleanerScreen.Result;
    }

    /// <summary>重新扫描并回到首页（用于结果页底部「重新扫描」）。</summary>
    public async Task RescanToHomeAsync()
    {
        Screen = CleanerScreen.Home;
        await RefreshAsync();
    }

    // ==================== 单项与详情操作 ====================

    public void ToggleProtect(AppItemViewModel item)
    {
        var g = item.Group;
        if (g.IsProtected)
        {
            _whitelist.Unprotect(g.Key);
            if (g.MatchedRuleId is not null) _whitelist.Unprotect(g.MatchedRuleId);
            g.IsProtected = false;
            if (g.Level == SafetyLevel.Protected) g.Level = SafetyLevel.Unknown;
            _history.MarkRestored(g.Key, "protected");
        }
        else
        {
            _whitelist.Protect(g.Key, g.DisplayName);
            g.IsProtected = true;
            g.Level = SafetyLevel.Protected;
            item.IsSelected = false;
            _history.Add(new HistoryRecord
            {
                Time = DateTime.Now, AppKey = g.Key, DisplayName = g.DisplayName,
                Action = "protected", Detail = "已加入保护名单", Restorable = true,
            });
        }
        _bus.Publish(new WhitelistChangedMessage(g.Key, g.IsProtected));
        _ = RefreshAsync();
    }

    public void AddToGameModeCloseList(AppGroup g)
    {
        var gameMode = _modeStore.GetById("game") ?? _modeStore.GetAll().FirstOrDefault();
        if (gameMode is null) return;
        AddToModeCloseList(g, gameMode);
    }

    /// <summary>将应用加入指定模式的「启动时关闭」规则（不入保护、不禁启动项）。</summary>
    public void AddToModeCloseList(AppGroup g, ModeProfile mode)
    {
        if (!mode.CloseOnStartAppKeys.Contains(g.Key, StringComparer.OrdinalIgnoreCase))
        {
            mode.CloseOnStartAppKeys.Add(g.Key);
            _modeStore.Save(mode);
        }
    }

    public void SaveMode(ModeProfile profile) => _modeStore.Save(profile);
    public IReadOnlyList<ModeProfile> AllModes => _modeStore.GetAll();

    public async Task DisableStartupWithConfirmAsync(AppGroup g)
    {
        var entries = g.StartupEntries;
        var sourceDesc = entries.Count > 0
            ? string.Join("、", entries.Select(e => $"{e.Kind}: {e.Name}"))
            : "关联后台启动项";

        var confirm = MemoryKeeper.Modules.Shared.MkDialog.Confirm(
            System.Windows.Application.Current?.MainWindow,
            $"禁用「{g.DisplayName}」开机自启？",
            $"将禁用这些启动来源：{sourceDesc}。原始配置已备份，可随时恢复；不会卸载应用，也不会结束正在运行的进程。",
            okText: "禁用");
        if (!confirm) return;

        await _disableService.DisableAsync(g, DisableScope.Permanent);
        _history.Add(new HistoryRecord
        {
            Time = DateTime.Now,
            AppKey = g.Key,
            DisplayName = g.DisplayName,
            Action = "disabled",
            Detail = entries.Count > 0 ? $"已禁用 {entries.Count} 个自动启动项（{sourceDesc}）" : "已加入启动项禁用名单",
            Restorable = true,
            RestorePath = g.Processes.FirstOrDefault(p => p.Path is not null)?.Path,
        });
        _bus.Publish(new AppsChangedMessage());
    }

    /// <summary>该应用是否在本会话的「禁止自动重启」名单中。</summary>
    public bool IsRespawnBlocked(AppGroup g) => _respawnBlocker.IsGroupBlocked(g);

    /// <summary>禁止/解除禁止该应用在本软件运行期间自动重启（独立于结束进程与开机启动管理）。</summary>
    public void ToggleRespawnBlock(AppItemViewModel item) => ToggleRespawnBlock(item.Group);

    public void ToggleRespawnBlock(AppGroup g)
    {
        if (_respawnBlocker.IsGroupBlocked(g))
        {
            _respawnBlocker.Unblock(g);
            _history.Add(new HistoryRecord
            {
                Time = DateTime.Now,
                AppKey = g.Key,
                DisplayName = g.DisplayName,
                Action = "respawn_unblock",
                Detail = $"已解除「{g.DisplayName}」的禁止自动重启",
                Restorable = false,
            });
            return;
        }

        // 服务/SYSTEM 级进程：当前权限根本杀不动，先如实告知并给出提权入口，
        // 否则"已阻止重启"徽标会变成谎言
        if (g.NeedsElevation && !MemoryKeeper.Modules.Shared.Elevation.IsAdmin)
        {
            RestartElevatedWithConfirm(g.DisplayName);
            return;
        }

        var procNames = string.Join("、", g.Processes.Select(p => p.Name));
        var confirm = MemoryKeeper.Modules.Shared.MkDialog.Confirm(
            System.Windows.Application.Current?.MainWindow,
            $"禁止「{g.DisplayName}」自动重启？",
            $"仅在本软件运行期间生效：{procNames} 再次启动会被持续结束；你手动打开窗口不受影响。退出本软件后不再拦截。",
            okText: "禁止重启");
        if (!confirm) return;

        _respawnBlocker.Block(g);
    }

    /// <summary>权限不足时的真实出路：提权重启本软件（UAC 确认），重启后清理、拦截与禁用服务均生效。</summary>
    public void RestartElevatedWithConfirm(string displayName)
    {
        var ok = MemoryKeeper.Modules.Shared.MkDialog.Confirm(
            System.Windows.Application.Current?.MainWindow,
            "需要管理员权限",
            $"「{displayName}」的进程运行在高权限会话（Windows 服务/系统级），当前权限无法结束或拦截。\n\n" +
            "以管理员身份重启本软件后即可处理；届时「禁止自动重启」和「禁用开机启动项（含守护服务）」对该应用均会真正生效。",
            okText: "以管理员重启",
            cancelText: "取消");
        if (ok) _bus.Publish(new RestartElevatedMessage());
    }

    /// <summary>按应用 Key 切换阻止重启状态（供结果页入口使用）。</summary>
    public void ToggleRespawnBlockByKey(string appKey)
    {
        var item = _allApps.FirstOrDefault(a => a.Group.Key.Equals(appKey, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            MemoryKeeper.Modules.Shared.MkDialog.Alert(
                System.Windows.Application.Current?.MainWindow,
                "暂时无法操作",
                "当前未检测到该应用的运行进程。等它重新出现后，可在列表或详情页中设置禁止重启。");
            return;
        }
        ToggleRespawnBlock(item.Group);
    }

    /// <summary>重建详情页 VM 以刷新操作文案（详情 VM 为只读快照）。</summary>
    public void ReopenDetail(AppGroup g) => Detail = new AppDetailViewModel(g, this);

    public void OpenLocation(string? exePath, string? installDir = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                Process.Start("explorer.exe", $"/select,\"{exePath}\"");
            else if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                Process.Start("explorer.exe", $"\"{installDir}\"");
        }
        catch { }
    }

    public void OpenDetail(AppItemViewModel item)
    {
        _previousScreen = CleanerScreen.Home;
        Detail = new AppDetailViewModel(item.Group, this);
        Screen = CleanerScreen.Detail;
    }

    public void OpenDetailByKey(string appKey)
    {
        var item = _allApps.FirstOrDefault(a => a.Group.Key.Equals(appKey, StringComparison.OrdinalIgnoreCase));
        if (item is not null) OpenDetail(item);
    }

    public void CloseDetail() => Screen = _previousScreen;

    public static string FormatMb(long bytes) => $"{Math.Max(1, bytes / 1024d / 1024d):F0} MB";
    public static string FormatSize(long bytes)
    {
        var mb = bytes / 1024d / 1024d;
        return mb >= 1024 ? $"{mb / 1024d:F1} GB" : $"{Math.Max(1, mb):F0} MB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class AppItemViewModel : INotifyPropertyChanged
{
    public AppGroup Group { get; }
    private readonly IRespawnBlocker? _respawnBlocker;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value && !IsProtected; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public string DisplayName => Group.DisplayName;
    public string Detail => Group.Description ?? Group.Publisher ?? "未知第三方程序 · 需确认所属软件";
    public string MemoryText => CleanerViewModel.FormatSize(Group.TotalMemoryBytes);
    public bool IsProtected => Group.IsProtected;
    public string StatusBadge => Group.IsProtected ? "已保护"
        : Group.Level == SafetyLevel.SafeToClose ? "建议清理" : "用途待确认";
    /// <summary>该应用是否在本会话的「禁止自动重启」名单中。</summary>
    public bool IsRespawnBlocked => _respawnBlocker?.IsGroupBlocked(Group) ?? false;
    /// <summary>已入拦截名单但权限不足实际拦不住（服务/SYSTEM 进程）。</summary>
    public bool IsRespawnDenied => _respawnBlocker?.IsGroupDenied(Group) ?? false;
    /// <summary>拦截徽标文案：权限不足时如实显示，不假装"已阻止"。</summary>
    public string RespawnBadgeText => IsRespawnDenied ? "需管理员权限" : "已阻止重启";
    /// <summary>组内进程运行在其他会话（服务/系统级）：当前权限无法结束，需提权。</summary>
    public bool NeedsAdmin => Group.NeedsElevation && !Shared.Elevation.IsAdmin;
    /// <summary>未入拦截名单时的"需管理员"提示徽章（已入名单时徽标文案已覆盖该信息）。</summary>
    public bool NeedsAdminOnly => NeedsAdmin && !IsRespawnBlocked;
    public string? IconPath => Group.Processes.FirstOrDefault(p => p.Path is not null)?.Path;
    public bool HasPath => !string.IsNullOrEmpty(IconPath) && File.Exists(IconPath);
    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();
    public bool HasIcon => Shared.IconExtractor.HasEmbeddedIcon(IconPath);

    public AppItemViewModel(AppGroup group, bool selected, IRespawnBlocker? respawnBlocker = null)
    {
        Group = group;
        _isSelected = selected;
        _respawnBlocker = respawnBlocker;
        if (respawnBlocker is not null)
            respawnBlocker.Changed += (_, _) =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRespawnBlocked)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRespawnDenied)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RespawnBadgeText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsAdminOnly)));
            };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ProgressRowVm : INotifyPropertyChanged
{
    public required string AppKey { get; init; }
    public required string DisplayName { get; init; }
    public string? IconPath { get; init; }
    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();
    public bool HasIcon => Shared.IconExtractor.HasEmbeddedIcon(IconPath);

    private string _statusText = "等待处理";
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText))); }
    }

    private bool _isDone;
    public bool IsDone
    {
        get => _isDone;
        private set { _isDone = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDone))); }
    }

    private System.Windows.Media.Brush _badgeBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF2, 0xF5));
    public System.Windows.Media.Brush BadgeBg
    {
        get => _badgeBg;
        private set { _badgeBg = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeBg))); }
    }

    private System.Windows.Media.Brush _badgeFg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x73, 0x79, 0x85));
    public System.Windows.Media.Brush BadgeFg
    {
        get => _badgeFg;
        private set { _badgeFg = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeFg))); }
    }

    /// <summary>按清理验证状态更新徽标文案与配色。</summary>
    public void ApplyStatus(CleanupItemStatus status, string? rawText)
    {
        var (text, bg, fg) = status switch
        {
            CleanupItemStatus.RequestingClose or CleanupItemStatus.Verifying
                => ("正在关闭…", 0xFEF0E8u, 0xD95D1Eu),
            CleanupItemStatus.Closed or CleanupItemStatus.AlreadyExited
                => ("已关闭", 0xE6F4EDu, 0x18794Eu),
            CleanupItemStatus.SkippedProtected
                => ("跳过保护", 0xE8F0FEu, 0x2E6BE6u),
            CleanupItemStatus.Cancelled
                => ("已取消", 0xF0F2F5u, 0x737985u),
            CleanupItemStatus.FailedAccessDenied
                => ("需要管理员权限", 0xFEF0E8u, 0xD95D1Eu),
            CleanupItemStatus.FailedRespawned
                => ("已重新启动", 0xFEF0E8u, 0xD95D1Eu),
            CleanupItemStatus.FailedAssociatedRunning
                => ("仍在运行", 0xFEF0E8u, 0xD95D1Eu),
            CleanupItemStatus.FailedTimeout
                => ("请求超时", 0xFEF0E8u, 0xD95D1Eu),
            _ => (rawText ?? "等待处理", 0xF0F2F5u, 0x737985u),
        };
        StatusText = text;
        BadgeBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(
            (byte)(bg >> 16), (byte)(bg >> 8), (byte)bg));
        BadgeFg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(
            (byte)(fg >> 16), (byte)(fg >> 8), (byte)fg));
        IsDone = status is CleanupItemStatus.Closed or CleanupItemStatus.AlreadyExited;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ResultRowVm : INotifyPropertyChanged
{
    private readonly CleanerViewModel _owner;
    public CleanupItemReport Report { get; }
    private bool _respawned;

    public ResultRowVm(CleanupItemReport report, CleanerViewModel owner)
    {
        Report = report;
        _owner = owner;
    }

    /// <summary>延迟复核发现该应用已被重新拉起，如实改判（不能再算"已关闭"）。</summary>
    public void MarkRespawned()
    {
        _respawned = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReasonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionBtnText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowTint)));
    }

    public bool TreatAsRespawned => _respawned || Report.Status == CleanupItemStatus.FailedRespawned;

    public string DisplayName => Report.DisplayName;
    public string? IconPath => Report.IconPath;
    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();
    public bool HasIcon => Shared.IconExtractor.HasEmbeddedIcon(IconPath);
    public string MemoryText => CleanerViewModel.FormatSize(Report.WorkingSetBytes);
    public string ReasonText => _respawned
        ? "结束后不久被守护进程重新拉起，仍可看到该应用在运行"
        : Report.ReasonDetail ?? Report.StatusText;
    public string ActionBtnText => TreatAsRespawned ? "禁止重启"
        : Report.Status == CleanupItemStatus.FailedAccessDenied ? "以管理员处理"
        : "查看原因";
    /// <summary>仍需处理分组行的左侧状态色：正常灰 / 重启橙。</summary>
    public string RowTint => TreatAsRespawned ? "#FEF0E8" : "#F0F2F5";

    public void HandleAction()
    {
        if (TreatAsRespawned)
        {
            _owner.ToggleRespawnBlockByKey(Report.AppKey);
            return;
        }
        if (Report.Status == CleanupItemStatus.FailedAccessDenied)
        {
            _owner.RestartElevatedWithConfirm(Report.DisplayName);
            return;
        }
        MemoryKeeper.Modules.Shared.MkDialog.Alert(
            System.Windows.Application.Current?.MainWindow,
            $"{Report.DisplayName} · {Report.StatusText}",
            $"{Report.ReasonDetail}（已退出 {Report.ClosedProcessCount} 个进程 · 仍运行 {Report.RemainingProcessCount} 个）\n\n" +
            "可以：在详情页加入保护名单，或选择「禁止其自动重启」、管理其开机启动项。");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AppDetailViewModel
{
    private readonly AppGroup _g;
    private readonly CleanerViewModel _owner;

    public AppDetailViewModel(AppGroup g, CleanerViewModel owner)
    {
        _g = g;
        _owner = owner;

        ProcessCountText = $"{g.Processes.Count} 个关联进程";
        MemoryText = CleanerViewModel.FormatSize(g.TotalMemoryBytes);
        RuntimeText = g.HasVisibleWindow ? "主窗口运行中" : "后台驻留（主窗口已关闭）";
        PublisherText = !string.IsNullOrWhiteSpace(g.Publisher) ? g.Publisher : "未知或未数字签名";
        StartupText = g.StartupEntries.Count == 0
            ? "未检测到开机自动启动项"
            : $"检测到 {g.StartupEntries.Count} 项开机启动配置（{string.Join(" / ", g.StartupEntries.Select(e => e.Kind))}）";
        InstallDirText = g.InstallDir ?? Path.GetDirectoryName(IconPath) ?? "未知路径（受系统权限限制）";
        AssociatedHeader = $"关联进程明细（{g.Processes.Count} 个）";

        LevelTag = g.IsProtected ? "已保护" : g.Level switch
        {
            SafetyLevel.SafeToClose => "建议清理",
            SafetyLevel.Protected => "已保护",
            _ => "用途待确认",
        };

        var evidence = new List<string>
        {
            g.HasVisibleWindow ? "检测到可见主窗口正在运行，清理前将优先请求正常关闭" : "主窗口已关闭，当前仅后台进程驻留",
            g.Category is AppCategory.Updater or AppCategory.Residual
                ? "属于独立更新或后台辅助组件"
                : g.StartupEntries.Count > 0
                    ? $"配置了 {g.StartupEntries.Count} 个开机自启动入口"
                    : !string.IsNullOrWhiteSpace(g.Publisher)
                        ? $"已验证发布者：{g.Publisher}"
                        : "未知第三方程序，需确认用途后再处理",
            "非 Windows 系统核心关键进程"
        };
        Evidence = evidence;

        AssociatedProcesses = g.Processes
            .OrderByDescending(p => p.MemoryBytes)
            .Select(p => $"{p.Name}.exe (PID {p.Pid}) · {CleanerViewModel.FormatMb(p.MemoryBytes)}")
            .ToList();
    }

    public string DisplayName => _g.DisplayName;
    public string Description => _g.Description ?? _g.Publisher ?? "用途待确认";
    public string? IconPath => _g.Processes.FirstOrDefault(p => p.Path is not null)?.Path;
    public bool HasPath => !string.IsNullOrEmpty(IconPath) && File.Exists(IconPath);
    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();
    public bool HasIcon => Shared.IconExtractor.HasEmbeddedIcon(IconPath);
    public string ProtectActionTitle => _g.IsProtected ? "从保护名单移除" : "加入保护名单";
    public string ProtectActionDesc => _g.IsProtected ? "解除保护后将回到普通候选列表" : "普通清理与自动模式均不会关闭此应用";
    public string RespawnActionTitle => _owner.IsRespawnBlocked(_g) ? "取消禁止自动重启" : "禁止其自动重启";
    public string RespawnActionDesc => _owner.IsRespawnBlocked(_g)
        ? "解除后该应用重新启动时将不再被自动结束"
        : "仅本软件运行期间生效：结束后若再次启动会被持续结束";

    public string LevelTag { get; }
    public string ProcessCountText { get; }
    public string MemoryText { get; }
    public string RuntimeText { get; }
    public string PublisherText { get; }
    public string StartupText { get; }
    public string InstallDirText { get; }
    public string AssociatedHeader { get; }
    public IReadOnlyList<string> Evidence { get; }
    public IReadOnlyList<string> AssociatedProcesses { get; }

    public void OpenFolder() => _owner.OpenLocation(IconPath, _g.InstallDir);

    public async Task CleanThisAsync() => await _owner.RunCleanupWithProgressAsync([_g], allowForceKill: false);

    public void ProtectThis()
    {
        _owner.ToggleProtect(new AppItemViewModel(_g, false));
        _owner.CloseDetail();
    }

    /// <summary>将本应用加入指定模式的「启动时关闭」规则。</summary>
    public void AddToModeClose(ModeProfile mode)
    {
        if (!mode.CloseOnStartAppKeys.Contains(_g.Key, StringComparer.OrdinalIgnoreCase))
        {
            mode.CloseOnStartAppKeys.Add(_g.Key);
            _owner.SaveMode(mode);
        }
        MemoryKeeper.Modules.Shared.MkDialog.Alert(
            System.Windows.Application.Current?.MainWindow,
            "已加入模式",
            $"「{_g.DisplayName}」已加入「{mode.Name}」的启动时关闭规则，可在「模式」页随时编辑。");
    }

    public void ToggleRespawnBlock()
    {
        _owner.ToggleRespawnBlock(_g);
        _owner.ReopenDetail(_g); // 刷新操作文案
    }

    public async Task ManageStartupAsync() => await _owner.DisableStartupWithConfirmAsync(_g);
}
