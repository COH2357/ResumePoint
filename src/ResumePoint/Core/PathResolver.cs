using System.Globalization;

namespace ResumePoint.Core;

/// <summary>标题与文件名的匹配规则。</summary>
public static class PathMatcher
{
    /// <summary>
    /// SMTC 的 Title 通常是文件名（可能带扩展名），有时是完整路径。
    /// 这里做"任一方包含另一方"的宽松匹配，并要求短的那边至少 minLen 个字符，避免"Media"之类的误命中。
    /// </summary>
    public static bool TitleMatchesPath(string? title, string path, int minLen = 3)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) return false;

        var t = Normalize(title);
        var n = Normalize(name);

        if (t.Length < minLen || n.Length < minLen) return false;
        if (t.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;

        var shorter = t.Length <= n.Length ? t : n;
        var longer = t.Length <= n.Length ? n : t;
        if (shorter.Length < minLen) return false;

        return longer.Contains(shorter, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string s)
    {
        s = s.Trim();
        s = s.Replace('　', ' ');           // 全角空格
        s = s.Replace('_', ' ');            // 下划线当空格
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>解析出来的路径猜测，带置信度。</summary>
public sealed record PathGuess(string Path, double Confidence, string Source)
{
    public const double High = 1.0;
    public const double Medium = 0.6;
    public const double Low = 0.3;
}

/// <summary>
/// "当前在播的是哪个文件"的解析器。
///
/// 实测背景：AppState.json 的队列会滞后（播放器换成别的文件后，它可能还指着旧文件），
/// 所以绝不能只看队列，必须用 SMTC 的标题做交叉验证才敢采信。
/// </summary>
public static class PathResolver
{
    /// <summary>
    /// 解析当前播放文件。
    /// </summary>
    /// <param name="session">当前目标会话</param>
    /// <param name="state">播放器落盘状态（可能滞后）</param>
    /// <param name="hint">最近由我们启动的文件（我们自己最清楚）</param>
    public static PathGuess? Resolve(SmtcSessionInfo session, PlayerStateSnapshot state, LaunchHint? hint)
    {
        var title = session.Title;

        // 1) 我们自己刚启动的文件 + 标题对得上 -> 最高置信度
        if (hint is not null && PathMatcher.TitleMatchesPath(title, hint.Path))
            return new PathGuess(hint.Path, PathGuess.High, "launch-hint+title");

        // 2) 播放队列里有标题对得上的项 -> 高置信度
        if (state.QueueSources.Count > 0)
        {
            foreach (var src in state.QueueSources)
            {
                if (PathMatcher.TitleMatchesPath(title, src))
                    return new PathGuess(src, PathGuess.High, "queue+title");
            }
        }

        // 3) 只有队列当前项，标题对不上 -> 可能是滞后，置信度低
        if (state.CurrentSource is not null)
            return new PathGuess(state.CurrentSource, PathGuess.Low, "queue-current");

        // 4) 只知道"我们启动过它"
        if (hint is not null)
            return new PathGuess(hint.Path, PathGuess.Low, "launch-hint");

        return null;
    }
}

/// <summary>最近一次由本工具启动的文件。</summary>
public sealed class LaunchHint
{
    public string Path { get; init; } = "";
    public DateTime At { get; init; } = DateTime.UtcNow;

    /// <summary>提示在多久之内仍然可信。</summary>
    public bool IsFresh(TimeSpan window) => DateTime.UtcNow - At <= window;
}
