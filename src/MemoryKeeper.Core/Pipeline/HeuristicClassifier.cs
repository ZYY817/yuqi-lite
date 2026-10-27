using System.Diagnostics;
using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Pipeline;

/// <summary>
/// 第三环（兜底）：为仍未知的应用补全展示信息。
/// 用 exe 版本信息取产品名，给出保守的"未知"评级——不做激进猜测。
/// </summary>
public sealed class HeuristicClassifier : IAppClassifier
{
    // 只在词典也未命中时兜底；词典命中后 Level 可能仍是 Unknown，但说明已填好
    public bool CanHandle(AppGroup group) => group.Level == SafetyLevel.Unknown && group.MatchedRuleId is null;

    public void Classify(AppGroup group)
    {
        var info = group.Processes
            .Where(p => p.Path is not null)
            .Select(p => p.Path!)
            .Select(ReadVersionInfo)
            .FirstOrDefault(v => v is not null);

        if (info?.ProductName is { Length: > 0 } product)
            group.DisplayName = product;
        else if (info?.FileDescription is { Length: > 0 } desc)
            group.DisplayName = desc;

        group.Publisher ??= info?.CompanyName;
        if (group.Publisher is not null)
            group.Publisher = group.Publisher.Trim('"', '\'', ' ');

        // 大内存有可见窗口的程序（如大型游戏）默认保护，防止误杀
        if (group.HasVisibleWindow && group.TotalMemoryBytes >= 1500L * 1024 * 1024)
        {
            group.Category = AppCategory.Game;
            group.IsProtected = true;
            group.Level = SafetyLevel.Protected;
            group.Description = "正在运行的大型应用或游戏，默认保留";
            return;
        }

        // 仅针对明确的无窗口崩溃收集进程归入建议清理；其余未知进程一律标记为"用途待确认"
        var keyLower = group.Key.ToLowerInvariant();
        if (!group.HasVisibleWindow && (keyLower.Contains("crashpad") || keyLower.Contains("crashreporter") || keyLower.Contains("crash_handler")))
        {
            group.Category = AppCategory.Residual;
            group.Level = SafetyLevel.SafeToClose;
            group.Description = "后台崩溃收集辅助进程，主窗口关闭后可清理";
            return;
        }

        group.Description = group.Publisher is { Length: > 0 } pub
            ? $"{ShortenPublisher(pub)} 相关组件 · 需确认用途"
            : group.HasVisibleWindow ? "运行中的第三方程序 · 确认用途后再处理" : "未知第三方程序 · 需确认所属软件";

        group.Category = group.HasVisibleWindow ? AppCategory.App : AppCategory.Unknown;
        // Level 保持 Unknown：未知应用不给"建议清理"，交给用户判断
    }

    /// <summary>"Tencent Technology (Shenzhen) Company Limited" → "Tencent Technology"。</summary>
    private static string ShortenPublisher(string publisher)
    {
        var s = publisher.Trim('"', '\'', ' ');
        var paren = s.IndexOf('(');
        if (paren > 0) s = s[..paren];
        foreach (var suffix in new[] { " Company Limited", " Corporation", " Co., Ltd.", " Co.,Ltd", " Inc.", " LLC", " Ltd." })
        {
            var i = s.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (i > 0) s = s[..i];
        }
        return s.Trim();
    }

    private static FileVersionInfo? ReadVersionInfo(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path); }
        catch { return null; }
    }
}
