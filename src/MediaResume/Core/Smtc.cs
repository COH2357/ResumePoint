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

    /// <summary>
    /// 在**有消息泵的线程**（UI 线程）上预热 manager。
    ///
    /// 这一点很关键：GlobalSystemMediaTransportControlsSessionManager 的会话列表是靠
    /// SessionsChanged 事件异步填充的。如果 manager 是在"会话还不存在"的时刻创建的，
    /// 而它所在的线程又没有消息循环来派发事件，GetSessions() 会永远返回空 ——
    /// 实测踩过这个坑：冷启动播放器后，manager 早于会话创建，之后 30 秒都看不到会话。
    ///
    /// 所以：manager 一律在 UI 线程（跑着 Application.Run 的那个）上创建。
    /// </summary>
    public async Task WarmUpAsync()
    {
        try { await GetManagerAsync().ConfigureAwait(false); }
        catch { }
    }

    /// <summary>丢弃缓存的 manager，下次查询会重新向系统请求一个。</summary>
    public void InvalidateManager()
    {
        Interlocked.Exchange(ref _manager, null);
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync()
    {
        var cached = Volatile.Read(ref _manager);
        if (cached is not null) return cached;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _manager);
            if (cached is null)
            {
                var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                mgr.SessionsChanged += (_, _) => SessionsChanged?.Invoke();
                Volatile.Write(ref _manager, mgr);
                cached = mgr;
            }
            return cached;
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
