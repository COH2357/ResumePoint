using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ResumePoint.Core;

/// <summary>
/// 文件关联注册。
///
/// 重要约束：Windows 10+ 的 UserChoice（默认打开方式）有哈希保护，
/// **程序无法静默修改**。所以这里只做两件事：
///   1) 把自己注册进系统的"打开方式"列表（OpenWith）
///   2) 引导用户去 设置→应用→默认应用 手动选一次
/// 绝不去改 UserChoice —— 改了会被系统重置，还可能触发安全提示。
/// </summary>
public static class FileAssociation
{
    private const string ProgId = "ResumePoint.Video";
    private const string AppExe = "ResumePoint.exe";

    private static readonly string[] Extensions =
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
        ".mp3", ".m4a", ".flac", ".wav", ".aac", ".wma",
    };

    public static bool IsRegistered()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}");
            return k is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var cmd = $"\"{exe}\" --open \"%1\"";

            Write($@"Software\Classes\{ProgId}", "", "ResumePoint 断点续播打开");
            Write($@"Software\Classes\{ProgId}\DefaultIcon", "", $"{exe},0");
            Write($@"Software\Classes\{ProgId}\shell\open\command", "", cmd);
            Write($@"Software\Classes\{ProgId}\SupportedTypes", null, null, Extensions);

            Write($@"Software\Classes\Applications\{AppExe}", "", "ResumePoint 断点续播");
            Write($@"Software\Classes\Applications\{AppExe}\shell\open\command", "", cmd);
            Write($@"Software\Classes\Applications\{AppExe}\SupportedTypes", null, null, Extensions);

            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);  // SHCNE_ASSOCCHANGED
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("注册文件关联失败", ex);
            return false;
        }
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}");
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{AppExe}");
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Error("注销文件关联失败", ex);
        }
    }

    /// <summary>打开 Windows 的"默认应用"设置页，让用户手动把视频指给本工具。</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开默认应用设置失败", ex);
        }
    }

    private static void Write(string subKey, string? name, string? value, string[]? values = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(subKey);
        if (k is null) return;

        if (values is not null)
        {
            foreach (var v in values) k.SetValue(v, "");
        }
        else if (name is not null)
        {
            k.SetValue(name, value ?? "");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
