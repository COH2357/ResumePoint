using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ResumePoint.Core;

public sealed class MediaRecord
{
    public long Id { get; init; }
    public string Path { get; init; } = "";
    public string DisplayPath { get; init; } = "";
    public string? Title { get; init; }
    public long Size { get; init; }
    public long Mtime { get; init; }
    public string? Fingerprint { get; init; }
    public double Duration { get; init; }
    public double Position { get; init; }
    public long UpdatedAt { get; init; }
    public long LastPlayedAt { get; init; }
    public int PlayCount { get; init; }
    public double TotalWatched { get; init; }
    public bool IsComplete { get; init; }

    /// <summary>已播放进度（0~1）。</summary>
    public double Progress => Duration > 1 ? Math.Clamp(Position / Duration, 0, 1) : 0;
}

/// <summary>
/// 断点记录库（SQLite）。
///
/// 设计要点：
///  * WAL + synchronous=NORMAL —— 断电不会损坏库，最坏丢最后一次提交
///  * 每条记录一个文件（per-file），解决原生"只存一条、跟着队列项走"的硬伤
///  * 路径匹配为主，指纹匹配兜底 —— 文件改名/移动后仍能认出
/// </summary>
public sealed class ResumeDb : IDisposable
{
    private readonly object _gate = new();
    private SqliteConnection? _conn;

    public string DatabasePath { get; }

    public ResumeDb(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public void Open()
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _conn = new SqliteConnection($"Data Source={DatabasePath};Cache=Shared");
            _conn.Open();

            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=NORMAL;");
            Execute("PRAGMA foreign_keys=ON;");

            Execute("""
                CREATE TABLE IF NOT EXISTS media (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    path           TEXT    NOT NULL,
                    display_path   TEXT    NOT NULL,
                    title          TEXT,
                    size           INTEGER NOT NULL DEFAULT 0,
                    mtime          INTEGER NOT NULL DEFAULT 0,
                    fingerprint    TEXT,
                    duration       REAL    NOT NULL DEFAULT 0,
                    position       REAL    NOT NULL DEFAULT 0,
                    updated_at     INTEGER NOT NULL DEFAULT 0,
                    last_played_at INTEGER NOT NULL DEFAULT 0,
                    play_count     INTEGER NOT NULL DEFAULT 0,
                    total_watched  REAL    NOT NULL DEFAULT 0,
                    is_complete    INTEGER NOT NULL DEFAULT 0
                );
                """);
            Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_media_path ON media(path COLLATE NOCASE);");
            Execute("CREATE INDEX IF NOT EXISTS idx_media_fp ON media(fingerprint);");
            Execute("CREATE INDEX IF NOT EXISTS idx_media_updated ON media(updated_at DESC);");
        }
    }

    /// <summary>把路径规范化成可稳定比较的形式。</summary>
    public static string NormalizePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // 统一分隔符
            full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return full;
        }
        catch
        {
            return path;
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static MediaRecord ReadRecord(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Path = r.GetString(1),
        DisplayPath = r.GetString(2),
        Title = r.IsDBNull(3) ? null : r.GetString(3),
        Size = r.GetInt64(4),
        Mtime = r.GetInt64(5),
        Fingerprint = r.IsDBNull(6) ? null : r.GetString(6),
        Duration = r.GetDouble(7),
        Position = r.GetDouble(8),
        UpdatedAt = r.GetInt64(9),
        LastPlayedAt = r.GetInt64(10),
        PlayCount = r.GetInt32(11),
        TotalWatched = r.GetDouble(12),
        IsComplete = r.GetInt64(13) != 0,
    };

    private const string SelectCols = "id, path, display_path, title, size, mtime, fingerprint, duration, position, updated_at, last_played_at, play_count, total_watched, is_complete";

    /// <summary>按路径查找（忽略大小写）。</summary>
    public MediaRecord? FindByPath(string path)
    {
        lock (_gate)
        {
            if (_conn is null) return null;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {SelectCols} FROM media WHERE path = @p COLLATE NOCASE LIMIT 1;";
            cmd.Parameters.AddWithValue("@p", NormalizePath(path));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRecord(r) : null;
        }
    }

    /// <summary>按指纹查找 —— 文件改名/移动后的兜底。</summary>
    public MediaRecord? FindByFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return null;
        lock (_gate)
        {
            if (_conn is null) return null;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {SelectCols} FROM media WHERE fingerprint = @f LIMIT 1;";
            cmd.Parameters.AddWithValue("@f", fingerprint);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRecord(r) : null;
        }
    }

    public List<MediaRecord> List(int limit = 5000)
    {
        lock (_gate)
        {
            var list = new List<MediaRecord>();
            if (_conn is null) return list;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {SelectCols} FROM media ORDER BY updated_at DESC LIMIT @n;";
            cmd.Parameters.AddWithValue("@n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadRecord(r));
            return list;
        }
    }

    /// <summary>
    /// 写入一次播放位置。路径优先；路径变了则用指纹找回同一条记录并更新路径。
    /// </summary>
    /// <param name="isComplete">
    /// 是否"已看完"。每次写入都按传入值覆盖 —— 不能是粘滞标志：
    /// 用户看完后从头重看时，必须把"已看完"清掉，否则以后永远不再续播。
    /// </param>
    /// <param name="watchedDelta">本次实际观看的秒数（由调用方按墙钟时间限幅，避免快进被算成观看）。</param>
    public MediaRecord? UpsertPosition(string rawPath, double positionSeconds, double durationSeconds, string? title, bool isComplete, double watchedDelta = 0)
    {
        lock (_gate)
        {
            if (_conn is null) return null;

            var path = NormalizePath(rawPath);
            var now = NowUnix();

            long size = 0, mtime = 0;
            try
            {
                var fi = new FileInfo(rawPath);
                if (fi.Exists)
                {
                    size = fi.Length;
                    mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                }
            }
            catch { /* 文件可能已不存在 */ }

            var existing = FindByPath(path);

            // 路径没命中 -> 用指纹找（改名/移动）
            if (existing is null)
            {
                var fp = FileFingerprint.Compute(rawPath);
                if (!string.IsNullOrEmpty(fp))
                {
                    existing = FindByFingerprint(fp);
                    if (existing is not null)
                    {
                        // 文件被改名/移动了 -> 更新路径
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.CommandText = "UPDATE media SET path = @p, display_path = @dp, size = @sz, mtime = @mt WHERE id = @id;";
                            cmd.Parameters.AddWithValue("@p", path);
                            cmd.Parameters.AddWithValue("@dp", rawPath);
                            cmd.Parameters.AddWithValue("@sz", size);
                            cmd.Parameters.AddWithValue("@mt", mtime);
                            cmd.Parameters.AddWithValue("@id", existing.Id);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
            }

            if (existing is null)
            {
                var fp = FileFingerprint.Compute(rawPath);
                using var ins = _conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO media (path, display_path, title, size, mtime, fingerprint, duration, position,
                                       updated_at, last_played_at, play_count, total_watched, is_complete)
                    VALUES (@p, @dp, @t, @sz, @mt, @fp, @du, @po, @ua, @lp, 1, @tw, @ic);
                    """;
                ins.Parameters.AddWithValue("@p", path);
                ins.Parameters.AddWithValue("@dp", rawPath);
                ins.Parameters.AddWithValue("@t", (object?)title ?? Path.GetFileNameWithoutExtension(rawPath));
                ins.Parameters.AddWithValue("@sz", size);
                ins.Parameters.AddWithValue("@mt", mtime);
                ins.Parameters.AddWithValue("@fp", (object?)fp ?? DBNull.Value);
                ins.Parameters.AddWithValue("@du", durationSeconds);
                ins.Parameters.AddWithValue("@po", positionSeconds);
                ins.Parameters.AddWithValue("@ua", now);
                ins.Parameters.AddWithValue("@lp", now);
                ins.Parameters.AddWithValue("@tw", 0d);   // 首条记录无法算增量，累计观看从第二次写入起算
                ins.Parameters.AddWithValue("@ic", isComplete ? 1 : 0);
                ins.ExecuteNonQuery();
                return FindByPath(path);
            }

            // 已有记录 -> 更新位置
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE media SET
                        position = @po,
                        duration = CASE WHEN @du > 0 THEN @du ELSE duration END,
                        updated_at = @ua,
                        last_played_at = @lp,
                        title = COALESCE(@t, title),
                        size = @sz,
                        mtime = @mt,
                        fingerprint = COALESCE(@fp, fingerprint),
                        total_watched = total_watched + @tw,
                        is_complete = @ic
                    WHERE id = @id;
                    """;
                cmd.Parameters.AddWithValue("@po", positionSeconds);
                cmd.Parameters.AddWithValue("@du", durationSeconds);
                cmd.Parameters.AddWithValue("@ua", now);
                cmd.Parameters.AddWithValue("@lp", now);
                cmd.Parameters.AddWithValue("@t", (object?)title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@sz", size);
                cmd.Parameters.AddWithValue("@mt", mtime);
                cmd.Parameters.AddWithValue("@fp", (object?)FileFingerprint.Compute(rawPath) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tw", Math.Max(0, watchedDelta));
                cmd.Parameters.AddWithValue("@ic", isComplete ? 1 : 0);
                cmd.Parameters.AddWithValue("@id", existing.Id);
                cmd.ExecuteNonQuery();
            }

            return FindByPath(existing.Id == 0 ? path : existing.Path);
        }
    }

    public bool Delete(long id)
    {
        lock (_gate)
        {
            if (_conn is null) return false;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM media WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int Clear()
    {
        lock (_gate)
        {
            if (_conn is null) return 0;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM media;";
            return cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _conn?.Close(); } catch { }
            _conn?.Dispose();
            _conn = null;
        }
    }
}
