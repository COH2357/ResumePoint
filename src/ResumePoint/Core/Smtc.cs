using System.Windows.Forms;
using Windows.Media.Control;

namespace ResumePoint.Core;

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
/// 关键点 1：GlobalSystemMediaTransportControlsSessionManager.RequestAsync() 首次调用可能要好几秒，
///          所以这里缓存 manager 实例，之后只调 GetSessions()（便宜）。
///
/// 关键点 2（架构性）：manager 的会话列表靠 SessionsChanged 事件**异步填充**。
///          若 manager 是在"会话还不存在"的时刻创建、且所在线程没有消息循环，
///          GetSessions() 会永远返回空。
///          —— 这个坑实测踩了两次：
///            ① manager 在后台线程创建 → 看不到后来出现的会话
///            ② 修好后加了 InvalidateManager()，它又在后台线程重建 manager → 同样的坑
///          所以这里做强制约束：**manager 永远由 UI 线程（跑着 Application.Run 的那个）创建**，
///          任何线程请求时都把创建动作调度回 UI 线程。
/// </summary>
public sealed class Smtc
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private int _ownerThreadId;
    private Control? _dispatcher;

    public event Action? SessionsChanged;

    /// <summary>
    /// 绑定一个用于跨线程调度回 UI 线程的控件（托盘宿主里的隐藏 Control）。
    /// 必须在 UI 线程上调用。
    /// </summary>
    public void AttachDispatcher(Control dispatcher)
    {
        _dispatcher = dispatcher;
        Volatile.Write(ref _ownerThreadId, Environment.CurrentManagedThreadId);
    }

    /// <summary>
    /// 在**有消息泵的 UI 线程**上预热 manager。必须在 UI 线程上调用。
    /// </summary>
    public async Task WarmUpAsync()
    {
        Volatile.Write(ref _ownerThreadId, Environment.CurrentManagedThreadId);
        try { await GetManagerAsync().ConfigureAwait(false); }
        catch { }
    }

    /// <summary>丢弃缓存的 manager，下次查询会重新向系统请求一个（仍会回到 UI 线程创建）。</summary>
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
                cached = await CreateManagerOnOwnerThreadAsync().ConfigureAwait(false);
                Volatile.Write(ref _manager, cached);
            }
            return cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 在归属线程（UI 线程）上创建 manager。
    /// 当前不在归属线程时，通过 dispatcher 把动作调度回去再等结果。
    /// </summary>
    private async Task<GlobalSystemMediaTransportControlsSessionManager> CreateManagerOnOwnerThreadAsync()
    {
        var ownerId = Volatile.Read(ref _ownerThreadId);
        var dispatcher = _dispatcher;

        if (ownerId != 0 && Environment.CurrentManagedThreadId != ownerId && dispatcher is not null)
        {
            var tcs = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { tcs.TrySetResult(await CreateManagerCoreAsync().ConfigureAwait(false)); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法把 SMTC manager 的创建调度到 UI 线程", ex);
            }

            return await tcs.Task.ConfigureAwait(false);
        }

        return await CreateManagerCoreAsync().ConfigureAwait(false);
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager> CreateManagerCoreAsync()
    {
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        mgr.SessionsChanged += (_, _) => SessionsChanged?.Invoke();
        return mgr;
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
