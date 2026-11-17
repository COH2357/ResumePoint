using Windows.Media.Control;

namespace MediaResume.Core;

/// <summary>
/// 记录端：轮询 SMTC，把"当前播到第几秒"写进数据库。
///
/// 三条铁律：
///  1. 边播边写 —— 不能攒到退出时才写，否则断电整段丢失
///  2. 识别不到文件就不写 —— 宁可漏记，也不能把位置记到错的文件上
///  3. 一次会话最多一个写入者 —— 用单实例锁
/// </summary>
public sealed class Recorder
{
    private readonly Settings _settings;
    private readonly ResumeDb _db;
    private readonly Smtc _smtc;
    private readonly LaunchHintStore _hints;

    private DateTime _lastWriteUtc = DateTime.MinValue;
    private string? _lastWrittenPath;
    private double _lastWrittenPosition;
    private double _lastObservedPosition;
    private DateTime _lastObservedUtc = DateTime.MinValue;
    private bool _hadSession;
    private DateTime _lastSessionSeenUtc = DateTime.MinValue;

    public event Action<RecorderSnapshot>? Changed;

    public Recorder(Settings settings, ResumeDb db, Smtc smtc, LaunchHintStore hints)
    {
        _settings = settings;
        _db = db;
        _smtc = smtc;
        _hints = hints;
    }

    /// <summary>暴露给托盘宿主，用于在 UI 线程上预热 SMTC manager。</summary>
    public Smtc Smtc => _smtc;

    public sealed class RecorderSnapshot
    {
        public SmtcSessionInfo? Session { get; init; }
        public string? Path { get; init; }
        public double Confidence { get; init; }
        public double PositionSeconds { get; init; }
        public double DurationSeconds { get; init; }
        public string? Title { get; init; }
        public DateTime LastWriteUtc { get; init; }
    }

    public async Task TickAsync()
    {
        var session = await _smtc.GetTargetSessionAsync().ConfigureAwait(false);

        if (session is null)
        {
            // 播放器退出了 -> 会话消失时补一次最终写入
            if (_hadSession) { FinalWrite(); _hadSession = false; }

            // 长时间看不到会话时丢弃缓存的 manager：
            // 若 manager 是在"会话还不存在"时创建的，它可能永远看不到后来出现的会话
            if (_lastSessionSeenUtc == DateTime.MinValue ||
                (DateTime.UtcNow - _lastSessionSeenUtc).TotalSeconds > 10)
            {
                _smtc.InvalidateManager();
            }

            Changed?.Invoke(new RecorderSnapshot());
            return;
        }

        _lastSessionSeenUtc = DateTime.UtcNow;

        if (session.Status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                           or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
        {
            Changed?.Invoke(new RecorderSnapshot { Session = session });
            return;
        }

        _hadSession = true;

        var state = PlayerState.Read();
        var guess = PathResolver.Resolve(session, state, _hints.Latest);

        // 识别不到足够可信的文件 -> 不写
        var path = guess is not null && guess.Confidence >= PathGuess.High ? guess.Path : null;

        Changed?.Invoke(new RecorderSnapshot
        {
            Session = session,
            Path = path,
            Confidence = guess?.Confidence ?? 0,
            PositionSeconds = session.Position.TotalSeconds,
            DurationSeconds = session.EndTime.TotalSeconds,
            Title = session.Title,
            LastWriteUtc = _lastWriteUtc,
        });

        if (path is null) return;

        var position = session.Position.TotalSeconds;
        if (position < 3) return;   // 刚开始播放，不记

        var changed = Math.Abs(position - _lastWrittenPosition) > 1.0;
        var due = (DateTime.UtcNow - _lastWriteUtc).TotalSeconds >= _settings.CommitIntervalSeconds;

        if (changed && due) Write(path, position, session);
    }

    private void Write(string path, double position, SmtcSessionInfo session)
    {
        try
        {
            var duration = session.EndTime.TotalSeconds;

            // "已看完"每次重新算，不做粘滞：
            // 用户看完后从头重看时必须清掉，否则以后永远不再续播。
            var isComplete = duration > 0 && duration - position <= _settings.CompleteWithinSeconds;

            // 累计观看：按墙钟时间限幅，避免快进/跳段被算成"看了"
            var watched = 0d;
            var now = DateTime.UtcNow;
            if (_lastObservedUtc != DateTime.MinValue)
            {
                var posDelta = position - _lastObservedPosition;
                var wall = (now - _lastObservedUtc).TotalSeconds;
                if (posDelta > 0 && posDelta < 600)
                    watched = Math.Min(posDelta, wall * 1.5 + 2);
            }
            _lastObservedPosition = position;
            _lastObservedUtc = now;

            _db.UpsertPosition(path, position, duration, session.Title, isComplete, watched);

            _lastWriteUtc = now;
            _lastWrittenPath = path;
            _lastWrittenPosition = position;
        }
        catch
        {
            // 写库失败不应中断轮询
        }
    }

    private void FinalWrite()
    {
        if (_lastWrittenPath is null) return;
        try
        {
            var rec = _db.FindByPath(_lastWrittenPath);
            if (rec is not null)
                _db.UpsertPosition(_lastWrittenPath, _lastWrittenPosition, rec.Duration, rec.Title, rec.IsComplete);
        }
        catch { }
    }
}

/// <summary>记录"最近由本工具启动的文件"，供路径解析做高置信度匹配。</summary>
public sealed class LaunchHintStore
{
    private LaunchHint? _latest;

    public LaunchHint? Latest => _latest;

    public void Set(string path)
    {
        _latest = new LaunchHint { Path = path, At = DateTime.UtcNow };
    }

    public void Clear() => _latest = null;
}
