using System.Diagnostics;
using Windows.Storage;
using Windows.System;

namespace MediaResume.Core;

public enum OpenMode
{
    /// <summary>打开并续播到上次断点。</summary>
    Resume,
    /// <summary>从头打开，不做跳转。</summary>
    FromStart,
}

public sealed class OpenOutcome
{
    public bool Launched { get; init; }
    public bool Seeked { get; init; }
    public double Position { get; init; }
    public string? Message { get; init; }
    public bool Ok => Launched;
}

/// <summary>
/// 播放端：打开指定文件 -> 等播放器会话就绪 -> seek 回断点。
///
/// 已实测（Win11 26300 / Media Player 11.2609.11.0）：
///   SMTC 会话存在、IsPlaybackPositionEnabled=True、TryChangePlaybackPositionAsync 返回 True 且位置真实跳转。
/// </summary>
public sealed class PlayerLauncher
{
    private readonly Settings _settings;
    private readonly ResumeDb _db;
    private readonly Smtc _smtc;
    private readonly LaunchHintStore _hints;

    public PlayerLauncher(Settings settings, ResumeDb db, Smtc smtc, LaunchHintStore hints)
    {
        _settings = settings;
        _db = db;
        _smtc = smtc;
        _hints = hints;
    }

    public async Task<OpenOutcome> OpenAsync(string path, OpenMode mode, CancellationToken ct)
    {
        // 记录我们启动了哪个文件 —— 供记录端做高置信度路径识别
        _hints.Set(path);

        bool launched;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
            var opts = new LauncherOptions
            {
                TargetApplicationPackageFamilyName = Settings.TargetPackageFamilyName,
            };
            launched = await Launcher.LaunchFileAsync(file, opts).AsTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new OpenOutcome { Launched = false, Message = $"打开失败：{ex.Message}" };
        }

        if (!launched)
            return new OpenOutcome { Launched = false, Message = "系统拒绝打开该文件" };

        if (mode == OpenMode.FromStart)
            return new OpenOutcome { Launched = true, Message = "已从头打开" };

        if (!_settings.AutoResumeEnabled)
            return new OpenOutcome { Launched = true, Message = "已打开（自动续播已关闭）" };

        // 查断点
        var record = _db.FindByPath(path) ?? FindByFingerprint(path);
        if (record is null)
            return new OpenOutcome { Launched = true, Message = "已打开（无历史记录）" };

        if (record.IsComplete)
            return new OpenOutcome { Launched = true, Message = "已打开（上次已看完）" };

        if (record.Position < _settings.MinResumeSeconds)
            return new OpenOutcome { Launched = true, Message = "已打开（上次位置太靠前）" };

        // 等播放器就绪并确认"现在播的就是这个文件"
        var session = await WaitForSessionAsync(path, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (session is null)
            return new OpenOutcome { Launched = true, Message = "已打开（未能确认播放器就绪，未跳转）" };

        var current = session.Position.TotalSeconds;
        var target = record.Position;

        if (Math.Abs(current - target) < 5)
            return new OpenOutcome { Launched = true, Position = current, Message = "已打开（位置已吻合）" };

        bool seeked;
        try
        {
            seeked = await _smtc.TrySeekAsync(TimeSpan.FromSeconds(target)).ConfigureAwait(false);
        }
        catch
        {
            seeked = false;
        }

        if (!seeked)
            return new OpenOutcome { Launched = true, Message = "已打开（播放器拒绝了跳转）" };

        // 默认跳完先暂停，避免"一打开就炸出声音"
        if (!_settings.AutoPlayAfterSeek)
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            await _smtc.TryPauseAsync().ConfigureAwait(false);
        }

        return new OpenOutcome
        {
            Launched = true,
            Seeked = true,
            Position = target,
            Message = $"已续播到 {FormatTime(target)}",
        };
    }

    private MediaRecord? FindByFingerprint(string path)
    {
        try
        {
            var fp = FileFingerprint.Compute(path);
            return string.IsNullOrEmpty(fp) ? null : _db.FindByFingerprint(fp);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 等目标会话出现，并且确认它播的就是我们打开的这个文件。
    /// 判定依据（按优先级）：标题匹配 -> 时长匹配 -> 队列匹配。
    /// </summary>
    private async Task<SmtcSessionInfo?> WaitForSessionAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var expectedDuration = await MediaInfo.TryGetDurationAsync(path).ConfigureAwait(false);

        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            var session = await _smtc.GetTargetSessionAsync().ConfigureAwait(false);
            if (session is not null)
            {
                if (PathMatcher.TitleMatchesPath(session.Title, path))
                    return session;

                // 时长对得上也算 —— 标题缺失时的兜底
                if (expectedDuration > 0 && session.EndTime.TotalSeconds > 0)
                {
                    var diff = Math.Abs(session.EndTime.TotalSeconds - expectedDuration);
                    if (diff <= Math.Max(2.0, expectedDuration * 0.01)) return session;
                }

                // 队列里出现我们的文件
                var state = PlayerState.Read();
                if (state.QueueSources.Any(s => PathMatcher.TitleMatchesPath(session.Title, s)))
                    return session;
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return null;
    }

    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) return "--:--";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}

public static class MediaInfo
{
    /// <summary>用 WinRT 读视频时长（秒）。读不到返回 0。</summary>
    public static async Task<double> TryGetDurationAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
            var props = await file.Properties.GetVideoPropertiesAsync().AsTask().ConfigureAwait(false);
            return props.Duration.TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }
}
