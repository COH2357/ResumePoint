using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResumePoint.Core;

/// <summary>
/// 用户设置。持久化在 %APPDATA%\ResumePoint\settings.json（位置固定），
/// 而记录数据库放在可自定义的 data 目录里 —— 移动数据目录不会丢设置。
/// </summary>
public sealed class Settings
{
    public const string TargetAumid = "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic";
    public const string TargetPackageFamilyName = "Microsoft.ZuneMusic_8wekyb3d8bbwe";

    /// <summary>记录写入位置（数据库 + 日志）。</summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory();

    /// <summary>轮询 SMTC 的间隔（秒）。</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>位置落盘的最长间隔（秒）—— 断电最多丢这么久。</summary>
    public int CommitIntervalSeconds { get; set; } = 5;

    /// <summary>是否启用自动续播。</summary>
    public bool AutoResumeEnabled { get; set; } = true;

    /// <summary>跳转到断点后是否自动播放。默认 false：跳完先暂停，避免"一打开就炸出声音"。</summary>
    public bool AutoPlayAfterSeek { get; set; } = false;

    /// <summary>关闭主窗口时隐藏到托盘而不是退出。</summary>
    public bool MinimizeOnClose { get; set; } = true;

    /// <summary>开机自启。</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>断点小于该秒数视为"从头看"，不续播。</summary>
    public double MinResumeSeconds { get; set; } = 10;

    /// <summary>距片尾在该秒数以内视为"已看完"，下次不再续播。</summary>
    public double CompleteWithinSeconds { get; set; } = 15;

    /// <summary>识别当前播放文件时，允许标题匹配的最小长度，避免误匹配。</summary>
    public int MinTitleMatchLength { get; set; } = 3;

    [JsonIgnore]
    public string DatabasePath => Path.Combine(DataDirectory, "resumes.db");

    [JsonIgnore]
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string DefaultDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResumePoint");

    /// <summary>设置文件的固定位置 —— 不随 DataDirectory 变化。</summary>
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ResumePoint", "settings.json");

    /// <summary>改名前的应用目录名 —— 用于把旧数据迁移过来。</summary>
    public const string LegacyAppFolder = "MediaResume";

    public static string LegacySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyAppFolder, "settings.json");

    public static string LegacyDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyAppFolder);

    /// <summary>设置是从旧目录读出来的（改名前遗留）。</summary>
    public bool LoadedFromLegacy { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Settings Load()
    {
        // 先读新位置，再回退到改名前的旧位置（保证升级后设置不丢）
        var s = TryRead(SettingsPath);
        if (s is null)
        {
            s = TryRead(LegacySettingsPath);
            if (s is not null) s.LoadedFromLegacy = true;
        }

        if (s is not null)
        {
            s.Normalize();
            return s;
        }

        var d = new Settings();
        d.Normalize();
        return d;
    }

    private static Settings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            // 设置损坏时回退到默认值，不让程序起不来
            return null;
        }
    }

    /// <summary>
    /// 把改名前的数据目录（断点库）迁移到新位置。已有同名文件不覆盖。
    /// </summary>
    public static void MigrateLegacyData(Settings settings)
    {
        try
        {
            var oldDir = LegacyDataDirectory;
            var newDir = settings.DataDirectory;

            if (!Directory.Exists(oldDir)) return;
            if (string.Equals(oldDir.TrimEnd('\\'), newDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(newDir);
            foreach (var name in new[] { "resumes.db", "resumes.db-wal", "resumes.db-shm" })
            {
                var src = Path.Combine(oldDir, name);
                var dst = Path.Combine(newDir, name);
                if (!File.Exists(src) || File.Exists(dst)) continue;
                try { File.Copy(src, dst, overwrite: false); } catch { }
            }
            AppLog.Info($"已从旧数据目录迁移断点库: {oldDir} -> {newDir}");
        }
        catch (Exception ex)
        {
            AppLog.Error("迁移旧数据失败", ex);
        }
    }

    public void Save()
    {
        try
        {
            Normalize();
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch
        {
            // 设置写失败不应影响主流程
        }
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory)) DataDirectory = DefaultDataDirectory();
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, 1, 30);
        CommitIntervalSeconds = Math.Clamp(CommitIntervalSeconds, 1, 120);
        MinResumeSeconds = Math.Max(0, MinResumeSeconds);
        CompleteWithinSeconds = Math.Max(0, CompleteWithinSeconds);
        MinTitleMatchLength = Math.Clamp(MinTitleMatchLength, 2, 20);
    }
}
