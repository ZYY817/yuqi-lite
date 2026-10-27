using System.Diagnostics;
using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Services;

/// <summary>
/// 结束进程执行器：有窗口的先请求正常关闭，超时或失败再强杀。
/// 自身进程与受保护进程拒绝执行。
/// </summary>
public sealed class TerminateExecutor : IActionExecutor
{
    private static readonly TimeSpan GraceTimeout = TimeSpan.FromSeconds(3);

    public ActionResult Execute(ActionRequest request)
    {
        if (request.Kind != ActionKind.Terminate)
            return new ActionResult(false, [], $"动作 {request.Kind} 暂未实现");

        if (request.Target.IsProtected)
            return new ActionResult(false, [], "受保护的应用不会被结束");

        var details = new List<ProcessActionResult>();
        var selfPid = Environment.ProcessId;

        foreach (var info in request.Target.Processes)
        {
            if (info.Pid == selfPid)
            {
                details.Add(new(info.Pid, info.Name, false, "不能结束自身"));
                continue;
            }
            details.Add(TerminateOne(info));
        }

        var ok = details.Any(d => d.Success);
        var failed = details.Count(d => !d.Success);
        var message = failed == 0 ? null : $"{failed} 个进程未能结束（可能需要管理员权限）";
        return new ActionResult(ok, details, message);
    }

    private static ProcessActionResult TerminateOne(AppProcessInfo info)
    {
        try
        {
            using var p = Process.GetProcessById(info.Pid);

            if (info.HasVisibleWindow)
            {
                p.CloseMainWindow();
                if (p.WaitForExit((int)GraceTimeout.TotalMilliseconds))
                    return new(info.Pid, info.Name, true, null);
            }

            p.Kill(entireProcessTree: true);
            // 之前忽略了 WaitForExit 返回值：进程没死也会误报成功
            var exited = p.WaitForExit((int)GraceTimeout.TotalMilliseconds);
            return exited
                ? new(info.Pid, info.Name, true, null)
                : new(info.Pid, info.Name, false, "结束请求超时，进程仍在运行");
        }
        catch (ArgumentException)
        {
            return new(info.Pid, info.Name, true, "进程已自行退出");
        }
        catch (Exception ex)
        {
            return new(info.Pid, info.Name, false, ex.Message);
        }
    }
}
