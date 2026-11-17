# 已沉淀技术结论 —— Windows 11 新版媒体播放器「视频断点续播」

> 这是从会话记忆中导出的持久化结论，等价于 Agent memory topic `win11-mediaplayer-resume`。
> 目的：换机器 / 换会话 / 换 Agent 后，不用重新调研一遍。

调研日 2026-11-17。三路并行取证（扩展点 / 存储机制 / 备选方案）。

---

## 核心判定

1. **真插件不可行**。MSFT 社区支持专员（署名）明确答复：
   > "Media Player does not have the ability to add plug-ins directly, due to product design."
   > https://learn.microsoft.com/en-us/answers/questions/3937430/windows-media-player

   旧 WMP12 的 COM 插件体系（Windows Media Player SDK）已标 legacy，未移植到新版。
2. **需求本身不需要插件**：新版播放器已落盘断点，且外部有官方 API 可读可写位置。
3. **生死判定点 = SMTC `TryChangePlaybackPositionAsync` 是否被 Media Player 接受**（1 小时可判）。

---

## 已核实的技术事实（复用价值高）

| 项 | 值 |
|---|---|
| PackageFamilyName | `Microsoft.ZuneMusic_8wekyb3d8bbwe`（沿用 Groove Music 血统，Store listing `9WZDNCRFJ3PT`） |
| AUMID | `Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic` |
| exe / EntryPoint | `Microsoft.Media.Player.exe` / `Microsoft.Entertainment.Music.App` |
| 断点文件 | `%LOCALAPPDATA%\Packages\Microsoft.ZuneMusic_8wekyb3d8bbwe\LocalState\AppState.json` |
| 断点字段 | `PlayerModel.CurrentPosition`（.NET TimeSpan 字符串，如 `"00:00:10.3495708"`） |
| 路径字段 | `PlayQueueModel.Items[].Source`（完整本地路径，**按路径匹配**，改名即失效） |
| 关键限制 | **只存一条断点、跟着当前队列项走**，不是 per-file 表 → 播别的文件即覆盖 |
| `MediaPlayer.db` | 只有最近播放历史（`File` / `RecentlyPlayed` / `Album` / `Video` 表），**无位置列** |
| 最近播放开关 | Settings → Privacy → Remember recent media（11.2409.11.0 起），管列表不管断点 |

---

## 外部接口（官方）

- **读位置**：`GlobalSystemMediaTransportControlsSessionManager.RequestAsync()` → `GetSessions()` →
  `session.GetTimelineProperties()`（Position / EndTime / MinSeekTime / MaxSeekTime）
- **写位置**：`session.TryChangePlaybackPositionAsync(ticks)` → `IAsyncOperation<bool>`；单位 100ns（= TimeSpan.Ticks）
- **前置**：`GetPlaybackInfo().Controls.IsPlaybackPositionEnabled == true`；调用方需 manifest 能力 `globalMediaControl`
- **打开指定文件**：`IApplicationActivationManager::ActivateForFile(AUMID, IShellItemArray, verb, pid)`
  或 `Launcher.LaunchFileAsync(file, LauncherOptions{ TargetApplicationPackageFamilyName = ... })`
- **不存在** `ms-media-player:` 协议；`shell:AppsFolder` 启动方式会忽略命令行参数；没有 startTime 之类语义

---

## 探针脚本用法

```powershell
# 先让媒体播放器播着一个视频（SMTC 会话只在播放时存在）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\probe\Probe-MediaPlayerResume.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\probe\Probe-MediaPlayerResume.ps1 -SeekToSeconds 600
```

C# 备选版（PowerShell WinRT 桥不稳时用）：

```bash
dotnet run --project src/MediaResumeProbe
dotnet run --project src/MediaResumeProbe -- --seek 600
```

必须用 Windows PowerShell 5.1（PS7 不支持该 WinRT 投影方式）。

---

## 结论与推荐

- **原生是否自动续播**：微软零文档、零开关；有一手实测（gHacks 2021，"resumes videos from the last playback point"）指向会续。
  → 必须人工三组对照（资源管理器双击 / Home 最近播放 / 完全退出后重开）实测。
- **若 SMTC 可 seek** → 做「伴生工具」：自建 (路径→位置) 表 + ActivateForFile 打开 +
  TryChangePlaybackPositionAsync 跳转 + IExplorerCommand 右键项。
- **若 SMTC 不可 seek** → **直接上 mpv**：`%APPDATA%\mpv\mpv.conf` 写 `save-position-on-quit=yes`，
  0.5–2 人时，真正 per-file、跨版本稳定。
- **不建议**走 UIA 拖进度条：零一手证据、依赖控件树、版本重写即废。

---

## 未证实的缺口（别当结论）

- 未下到 MSIX 直接读 AppxManifest 证实「无自定义协议」（rg-adguard 被 Cloudflare 拦）→ 推定结论，复核方法见 HANDOFF 第 5 节
- Media Player 是否实现 `PlaybackPositionChangeRequested`（决定 SMTC seek 成败）
- UIA 里进度条是否以 Slider/RangeValue 暴露
- 22H2~25H2 续播行为差异
