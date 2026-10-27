using MemoryKeeper.Contracts;
using MemoryKeeper.Core.Platform;

namespace MemoryKeeper.Core.Pipeline;

/// <summary>
/// 第一环：判定系统进程（终判），并为第三方应用填充发布者。
/// 系统规则：伪进程 → 核心名单 → Windows 目录 + Microsoft 签名。
/// </summary>
public sealed class SignatureClassifier : IAppClassifier
{
    private readonly ISignatureVerifier _verifier;
    private readonly string _windowsDir;

    public SignatureClassifier(ISignatureVerifier verifier)
    {
        _verifier = verifier;
        _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    public bool CanHandle(AppGroup group) => group.Level == SafetyLevel.Unknown;

    public void Classify(AppGroup group)
    {
        // 无路径的伪进程 + 核心名单：直接判定系统级
        if (group.Processes.Any(p => p.Path is null && SystemProcessNames.Contains(p.Name))
            || group.Processes.Any(p => SystemProcessNames.Contains(p.Name)))
        {
            MarkSystem(group);
            return;
        }

        var paths = group.Processes.Select(p => p.Path).Where(p => p is not null).Cast<string>().ToList();

        // 全部位于 Windows 目录或 WindowsApps\Microsoft 系统应用目录 → 判定系统组件
        if (paths.Count > 0 && paths.All(p =>
            p.StartsWith(_windowsDir, StringComparison.OrdinalIgnoreCase)
            || p.Contains(@"\WindowsApps\Microsoft", StringComparison.OrdinalIgnoreCase)
            || p.Contains(@"\Microsoft\EdgeWebView\", StringComparison.OrdinalIgnoreCase)))
        {
            MarkSystem(group);
            return;
        }

        // 第三方应用：填发布者（供后续分类器与界面兜底使用）
        group.Publisher ??= paths.Select(_verifier.GetVerifiedPublisher).FirstOrDefault(p => p is not null);

        // Microsoft 签名的非系统目录组件也按系统处理（如部分运行库宿主）
        if (group.Publisher is not null
            && group.Publisher.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            && paths.Count > 0)
        {
            MarkSystem(group);
            return;
        }

        // 针对目录签名（MSIX/系统组件）未内嵌 PE 签名的情况，检查版本信息中的 Microsoft Operating System 标识
        if (paths.Count > 0)
        {
            try
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(paths[0]);
                if (vi.ProductName?.Contains("Windows® Operating System", StringComparison.OrdinalIgnoreCase) == true
                    || (vi.CompanyName?.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) == true
                        && !paths[0].Contains("OneDrive", StringComparison.OrdinalIgnoreCase)
                        && !paths[0].Contains("Office", StringComparison.OrdinalIgnoreCase)))
                {
                    MarkSystem(group);
                }
            }
            catch { }
        }
    }

    private static void MarkSystem(AppGroup g)
    {
        g.Level = SafetyLevel.System;
        g.IsProtected = true;
    }
}
