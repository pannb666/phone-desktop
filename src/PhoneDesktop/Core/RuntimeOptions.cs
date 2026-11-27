using System.IO;

namespace PhoneDesktop.Core;

/// <summary>
/// 运行时开关。目前只有一个测试用钩子：
/// 任务管理器命令可以通过环境变量替换，便于自动化验收（避免 UAC 弹窗阻塞）。
/// </summary>
public static class RuntimeOptions
{
    public const string TaskManagerOverrideVariable = "PHONEDESKTOP_TASKMGR";

    public static string TaskManagerCommand { get; } = ResolveTaskManagerCommand();

    private static string ResolveTaskManagerCommand()
    {
        var overridden = Environment.GetEnvironmentVariable(TaskManagerOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            AppLog.Info($"任务管理器命令被环境变量覆盖：{overridden}");
            return overridden.Trim();
        }

        return Path.Combine(Environment.SystemDirectory, "taskmgr.exe");
    }
}
