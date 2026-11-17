using System.Diagnostics;
using Windows.Media.Control;
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
        var session = await WaitForSessionAsync(path, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (session is null)
            return new OpenOutcome { Launched = true, Message = "已打开（未能确认播放器就绪，未跳转）" };

        var current = session.Position.TotalSeconds;
        var target = record.Position;

        if (Math.Abs(current - target) < 5)
            return new OpenOutcome { Launched = true, Position = current, Message = "已打开（位置已吻合）" };

        bool seeked = false;
        try
        {
            // seek 偶尔会被播放器在"刚加载完"的状态下拒掉 -> 重试几次
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                var posNow = (await _smtc.GetTargetSessionAsync().ConfigureAwait(false))?.Position.TotalSeconds ?? 0;
                AppLog.Info($"发起 seek(第{attempt}次): 当前={posNow:0.00}s 目标={target:0.00}s");
                if (await _smtc.TrySeekAsync(TimeSpan.FromSeconds(target)).ConfigureAwait(false))
                {
                    seeked = true;
                    AppLog.Info("seek 返回: True");
                    break;
                }
                AppLog.Warn("seek 返回: False，稍后重试");
                await Task.Delay(600, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("seek 异常", ex);
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
    ///
    /// 关键：会话刚建立时是 status=Changing / title='' / end=0 —— 播放器还在加载。
    /// 此时不能匹配、更不能 seek（实测会被拒，返回 False）。
    /// 必须等到 status 就绪且 EndTime>0 才参与判定。
    /// </summary>
    private async Task<SmtcSessionInfo?> WaitForSessionAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var expectedDuration = await MediaInfo.TryGetDurationAsync(path).ConfigureAwait(false);
        AppLog.Info($"等待会话: 文件={path} 期望时长={expectedDuration:0.00}s 超时={timeout.TotalSeconds:0}s");

        SmtcSessionInfo? last = null;
        var lastLogged = "";
        var invalidated = false;

        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            var session = await _smtc.GetTargetSessionAsync().ConfigureAwait(false);

            // 会话迟迟不出现：丢弃缓存的 manager，重新向系统请求一个
            // （manager 若早于会话创建，就可能永远看不到后来出现的会话）
            if (session is null && !invalidated && sw.Elapsed.TotalSeconds > 4)
            {
                invalidated = true;
                _smtc.InvalidateManager();
                AppLog.Warn("未见目标会话，丢弃缓存的 manager 后重试");
            }

            if (session is not null)
            {
                last = session;

                var logLine = $"status={session.Status} title='{session.Title}' " +
                              $"pos={session.Position.TotalSeconds:0.00} end={session.EndTime.TotalSeconds:0.00}";
                if (logLine != lastLogged)
                {
                    AppLog.Info("  候选会话: " + logLine);
                    lastLogged = logLine;
                }

                // 会话就绪判据：不在 Changing 状态，且已经有时长（= 媒体已加载）
                var ready = session.Status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing
                            && session.EndTime.TotalSeconds > 0;

                if (ready)
                {
                    // 1) 标题匹配（最可靠）
                    if (PathMatcher.TitleMatchesPath(session.Title, path))
                        return session;

                    // 2) 时长匹配
                    if (expectedDuration > 0)
                    {
                        var diff = Math.Abs(session.EndTime.TotalSeconds - expectedDuration);
                        if (diff <= Math.Max(2.0, expectedDuration * 0.01)) return session;
                    }

                    // 3) 播放队列里出现我们的文件
                    var state = PlayerState.Read();
                    if (state.QueueSources.Any(q =>
                            string.Equals(q, path, StringComparison.OrdinalIgnoreCase) ||
                            PathMatcher.TitleMatchesPath(session.Title, q)))
                        return session;

                    // 4) 兜底：标题为空但时长对得上（某些版本不报 Title）
                    if (string.IsNullOrWhiteSpace(session.Title) && expectedDuration > 0)
                    {
                        var diff = Math.Abs(session.EndTime.TotalSeconds - expectedDuration);
                        if (diff <= Math.Max(3.0, expectedDuration * 0.02)) return session;
                    }
                }
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        AppLog.Warn(last is null
            ? "等待会话超时：没有出现任何目标会话"
            : $"等待会话超时：最后候选 status={last.Status} title='{last.Title}' " +
              $"pos={last.Position} end={last.EndTime}");
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
