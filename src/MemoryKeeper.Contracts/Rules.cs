namespace MemoryKeeper.Contracts;

/// <summary>词典中的一条规则：识别一个应用并给出人话说明与默认建议。</summary>
public sealed record RuleEntry
{
    /// <summary>规则 ID，如 "qq-music"。</summary>
    public required string Id { get; init; }
    /// <summary>匹配的进程名（不含 .exe，不区分大小写）。</summary>
    public required IReadOnlyList<string> ProcessNames { get; init; }
    /// <summary>匹配的安装目录片段（可选，如 "Tencent"）。</summary>
    public string? PathHint { get; init; }
    /// <summary>展示的友好名称。</summary>
    public required string DisplayName { get; init; }
    /// <summary>一句人话说明，如"自动更新组件"。</summary>
    public required string Description { get; init; }
    public AppCategory Category { get; init; } = AppCategory.App;
    /// <summary>建议的评级。</summary>
    public SafetyLevel SuggestedLevel { get; init; } = SafetyLevel.Unknown;
    /// <summary>命中后是否默认保护（游戏、反作弊等）。</summary>
    public bool DefaultProtect { get; init; }
}

/// <summary>内置/在线词典的读取接口。</summary>
public interface IRuleStore
{
    IReadOnlyList<RuleEntry> GetAll();
    /// <summary>按进程名查找规则。</summary>
    RuleEntry? Match(string processName, string? installDir);
}
