using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Pipeline;

/// <summary>第二环：命中内置词典 → 填中文名、人话说明与建议评级。</summary>
public sealed class DictionaryClassifier : IAppClassifier
{
    private readonly IRuleStore _rules;

    public DictionaryClassifier(IRuleStore rules) => _rules = rules;

    public bool CanHandle(AppGroup group)
        => group.Level is SafetyLevel.Unknown && FindRule(group) is not null;

    public void Classify(AppGroup group)
    {
        var rule = FindRule(group);
        if (rule is null) return;

        group.DisplayName = rule.DisplayName;
        group.Description = rule.Description;
        group.Category = rule.Category;
        group.Level = rule.SuggestedLevel;
        group.MatchedRuleId = rule.Id;
        if (rule.DefaultProtect)
            group.IsProtected = true;
    }

    private RuleEntry? FindRule(AppGroup group)
    {
        foreach (var p in group.Processes)
        {
            var rule = _rules.Match(p.Name, group.InstallDir);
            if (rule is not null) return rule;
        }
        return null;
    }
}
