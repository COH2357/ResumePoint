namespace MediaResume.Core;

/// <summary>极简文件日志（单文件、按天轮转上限，避免无限增长）。</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string LogPath => Path.Combine(Settings.DefaultDataDirectory(), "logs", "mediaresume.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var path = LogPath;
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);

            lock (Gate)
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = path + ".1";
                    try { File.Move(path, old, overwrite: true); } catch { }
                }

                var sb = new System.Text.StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.Append(" [").Append(level).Append("] ");
                sb.AppendLine(message);
                if (ex is not null)
                    sb.AppendLine("    " + ex.Message + "\n" + ex.StackTrace);

                File.AppendAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败无所谓
        }
    }
}
