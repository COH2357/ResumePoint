using System.IO;

namespace ResumePoint.Core;

/// <summary>
/// 开机自启。
///
/// 关键约束：GSMTC 必须在交互式桌面会话里用，所以自启必须是"登录时启动"，
/// 绝不能注册成 Windows 服务（服务/SYSTEM 上下文取不到媒体会话）。
/// 这里用"启动文件夹"快捷方式 —— 最简单、最透明、用户可随时在任务管理器里禁用。
/// </summary>
public static class AutoStart
{
    private const string ShortcutName = "ResumePoint.lnk";

    private static string StartupDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup));

    private static string ShortcutPath => Path.Combine(StartupDir, ShortcutName);

    public static bool IsEnabled()
    {
        try { return File.Exists(ShortcutPath); }
        catch { return false; }
    }

    public static void Enable()
    {
        try
        {
            Directory.CreateDirectory(StartupDir);
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            // 用 WScript.Shell 生成 .lnk
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return;

            dynamic? link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe)!;
            link.Description = "ResumePoint 断点续播伴生工具";
            link.WindowStyle = 1;   // 最小化（托盘程序没有窗口）
            link.Save();
        }
        catch
        {
            // 自启失败不影响主流程
        }
    }

    public static void Disable()
    {
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); }
        catch { }
    }
}
