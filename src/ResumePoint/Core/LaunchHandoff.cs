using System.Text.Json;

namespace ResumePoint.Core;

/// <summary>
/// 跨进程交接：把"刚打开了哪个文件"告诉托盘里的记录端。
///
/// 背景：双击文件时，shell 启动的是一个**独立的** `ResumePoint.exe --open "&lt;路径&gt;"` 进程，
/// 而记录端常驻在**另一个**托盘进程里。两者互不相识 —— 记录端只能去猜"现在播的是哪个文件"：
///   * SMTC 的 Title 只有文件名，没有完整路径
///   * 播放器的 AppState.json 队列是**滞后写入**的（实测播放器换成别的文件后，
///     队列可能还指着上一个文件），靠它匹配会漏记
/// 所以让打开方主动写一个极简的交接文件，记录端每次轮询读一下即可。
/// </summary>
public sealed class LaunchHandoff
{
    /// <summary>被打开的媒体文件完整路径。</summary>
    public string MediaPath { get; init; } = "";
    public long AtUnix { get; init; }

    public DateTime At => DateTimeOffset.FromUnixTimeSeconds(AtUnix).LocalDateTime;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static string FilePath
    {
        get
        {
            var dir = Settings.Load().DataDirectory;
            return System.IO.Path.Combine(dir, "last-opened.json");
        }
    }

    /// <summary>记录"刚刚打开了 path"。由打开方（--open 命令行 / 托盘菜单）调用。</summary>
    public static void Write(string path)
    {
        try
        {
            var file = FilePath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);

            var json = JsonSerializer.Serialize(
                new LaunchHandoff { MediaPath = path, AtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, Opts);

            // 原子写：先写临时文件再改名，避免记录端读到半截 JSON
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("写交接文件失败", ex);
        }
    }

    /// <summary>读取交接信息，超过 maxAge 视为无效。</summary>
    public static LaunchHandoff? Read(TimeSpan maxAge)
    {
        try
        {
            var file = FilePath;
            if (!File.Exists(file)) return null;

            var h = JsonSerializer.Deserialize<LaunchHandoff>(File.ReadAllText(file), Opts);
            if (h is null || string.IsNullOrWhiteSpace(h.MediaPath)) return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - h.AtUnix > maxAge.TotalSeconds) return null;
            return h;
        }
        catch
        {
            return null;
        }
    }
}
