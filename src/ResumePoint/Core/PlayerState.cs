using System.Text.Json;

namespace ResumePoint.Core;

/// <summary>
/// 新版媒体播放器的落盘状态（%LOCALAPPDATA%\Packages\Microsoft.ZuneMusic_8wekyb3d8bbwe\LocalState\AppState.json）。
///
/// 实测结构是：
///   { "ComponentStates": { "PlayQueueModel": {...}, "PlayerModel": {...} } }
/// 注意：PlayQueueModel / PlayerModel 在 ComponentStates 里面，不是根节点。
/// 而且实测发现它的落盘是"周期性"的，队列项会滞后 —— 所以它只能当辅助线索，不能当权威来源。
/// </summary>
public sealed class PlayerStateSnapshot
{
    public bool Available { get; init; }
    public DateTime LastWrite { get; init; }
    public IReadOnlyList<string> QueueSources { get; init; } = Array.Empty<string>();
    public int CurrentItemIndex { get; init; }
    public string? CurrentSource { get; init; }
    public TimeSpan? Position { get; init; }
}

public static class PlayerState
{
    public static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages", Settings.TargetPackageFamilyName, "LocalState", "AppState.json");

    private static readonly JsonDocumentOptions DocOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static PlayerStateSnapshot Read()
    {
        var path = StatePath;
        try
        {
            if (!File.Exists(path)) return new PlayerStateSnapshot();

            var fi = new FileInfo(path);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs, DocOpts);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new PlayerStateSnapshot();

            // 容错：真实结构是 ComponentStates 包裹，同时兼容根节点直挂
            var scope = doc.RootElement;
            if (scope.TryGetProperty("ComponentStates", out var cs) && cs.ValueKind == JsonValueKind.Object)
                scope = cs;

            var sources = new List<string>();
            var index = 0;
            string? current = null;
            TimeSpan? pos = null;

            if (scope.TryGetProperty("PlayQueueModel", out var q) && q.ValueKind == JsonValueKind.Object)
            {
                if (q.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in items.EnumerateArray())
                    {
                        if (it.TryGetProperty("Source", out var src) && src.ValueKind == JsonValueKind.String)
                            sources.Add(src.GetString()!);
                    }
                }
                if (q.TryGetProperty("CurrentItemIndex", out var ix) && ix.TryGetInt32(out var v))
                    index = v;
            }

            if (index >= 0 && index < sources.Count) current = sources[index];

            if (scope.TryGetProperty("PlayerModel", out var pm) && pm.ValueKind == JsonValueKind.Object)
            {
                if (pm.TryGetProperty("CurrentPosition", out var cp) && cp.ValueKind == JsonValueKind.String)
                {
                    var s = cp.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && TimeSpan.TryParse(s, out var ts)) pos = ts;
                }
            }

            return new PlayerStateSnapshot
            {
                Available = true,
                LastWrite = fi.LastWriteTime,
                QueueSources = sources,
                CurrentItemIndex = index,
                CurrentSource = current,
                Position = pos,
            };
        }
        catch
        {
            return new PlayerStateSnapshot();
        }
    }
}
