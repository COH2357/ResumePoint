using Windows.Media.Control;

namespace MediaResume.Core;

public sealed class SmtcSessionInfo
{
    public string Aumid { get; init; } = "";
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public GlobalSystemMediaTransportControlsSessionPlaybackStatus Status { get; set; } =
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
    public bool SeekEnabled { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan EndTime { get; set; }
    public TimeSpan MinSeekTime { get; set; }
    public TimeSpan MaxSeekTime { get; set; }

    public bool IsTarget => string.Equals(Aumid, Settings.TargetAumid, StringComparison.OrdinalIgnoreCase)
                            || Aumid.StartsWith("Microsoft.ZuneMusic", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// SMTC（全局媒体控制）封装。
///
/// 关键点：GlobalSystemMediaTransportControlsSessionManager.RequestAsync() 首次调用可能要好几秒，
/// 所以这里缓存 manager 实例，之后只调 GetSessions()（便宜）。
/// </summary>
public sealed class Smtc
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public event Action? SessionsChanged;

    private async Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync()
    {
        if (_manager is not null) return _manager;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_manager is null)
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.SessionsChanged += (_, _) => SessionsChanged?.Invoke();
            }
            return _manager;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<SmtcSessionInfo>> GetSessionsAsync()
    {
        var result = new List<SmtcSessionInfo>();
        try
        {
            var mgr = await GetManagerAsync().ConfigureAwait(false);
            foreach (var s in mgr.GetSessions())
            {
                var info = new SmtcSessionInfo { Aumid = s.SourceAppUserModelId };

                try
                {
                    var pb = s.GetPlaybackInfo();
                    info.Status = pb.PlaybackStatus;
                    info.SeekEnabled = pb.Controls.IsPlaybackPositionEnabled;
                }
                catch { /* 某些会话不提供 PlaybackInfo */ }

                try
                {
                    var tl = s.GetTimelineProperties();
                    info.Position = tl.Position;
                    info.EndTime = tl.EndTime;
                    info.MinSeekTime = tl.MinSeekTime;
                    info.MaxSeekTime = tl.MaxSeekTime;
                }
                catch { /* 会话刚建立时可能拿不到时间轴 */ }

                try
                {
                    var mp = await s.TryGetMediaPropertiesAsync();
                    info.Title = mp.Title;
                    info.Artist = mp.Artist;
                }
                catch { /* 取不到媒体属性不影响主流程 */ }

                result.Add(info);
            }
        }
        catch
        {
            // SMTC 在非交互式会话 / 服务上下文下会直接抛异常，静默降级
        }
        return result;
    }

    public async Task<SmtcSessionInfo?> GetTargetSessionAsync()
    {
        var sessions = await GetSessionsAsync().ConfigureAwait(false);
        return sessions.FirstOrDefault(s => s.IsTarget);
    }

    /// <summary>对目标会话发起跳转。返回 false = 播放器拒绝了这次 seek。</summary>
    public async Task<bool> TrySeekAsync(TimeSpan position)
    {
        try
        {
            var mgr = await GetManagerAsync().ConfigureAwait(false);
            var s = mgr.GetSessions().FirstOrDefault(x =>
                string.Equals(x.SourceAppUserModelId, Settings.TargetAumid, StringComparison.OrdinalIgnoreCase));
            if (s is null) return false;
            return await s.TryChangePlaybackPositionAsync(position.Ticks);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TryPauseAsync()
    {
        try
        {
            var mgr = await GetManagerAsync().ConfigureAwait(false);
            var s = mgr.GetSessions().FirstOrDefault(x =>
                string.Equals(x.SourceAppUserModelId, Settings.TargetAumid, StringComparison.OrdinalIgnoreCase));
            if (s is null) return false;
            return await s.TryPauseAsync();
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TryPlayAsync()
    {
        try
        {
            var mgr = await GetManagerAsync().ConfigureAwait(false);
            var s = mgr.GetSessions().FirstOrDefault(x =>
                string.Equals(x.SourceAppUserModelId, Settings.TargetAumid, StringComparison.OrdinalIgnoreCase));
            if (s is null) return false;
            return await s.TryPlayAsync();
        }
        catch
        {
            return false;
        }
    }
}
