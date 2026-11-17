# MEMORY —— win11-mediaplayer-resume

> 项目级持久化摘要。目的：任何后续会话 / 本地 Agent 读完这一份，就能立刻接上，不用重新调研。

## 项目状态

- **2026-11-17**：可行性验证**已完成**。结论、证据链、备选路线对比、探针脚本全部产出。
- **当前卡点**：需要一台真实 Windows 11 机器跑探针（调研沙箱是 Linux，跑不了 WinRT/SMTC）。
- **下一步（拿到探针输出后）**：按决策树分流 → 路线 A（伴生工具）或路线 B（mpv）。

## 一句话结论

> 「给新版媒体播放器写插件」官方层面关闭（MSFT 明文答复不支持）。
> 但功能可实现：断点已在 `AppState.json`，外部有官方 SMTC API 可读可写位置。
> 生死判定点 = `TryChangePlaybackPositionAsync` 对 Media Player 是否返回 True。

## 已核实事实速查

```
PackageFamilyName : Microsoft.ZuneMusic_8wekyb3d8bbwe
AUMID             : Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic
断点文件          : %LOCALAPPDATA%\Packages\Microsoft.ZuneMusic_8wekyb3d8bbwe\LocalState\AppState.json
断点字段          : PlayerModel.CurrentPosition        （TimeSpan 字符串 "00:00:10.3495708"）
路径字段          : PlayQueueModel.Items[].Source       （按路径匹配，改名即失效）
致命限制          : 只存一条断点，跟当前队列项走，不是 per-file 表
MediaPlayer.db    : 只有最近播放历史，无位置列（别搞混）
```

官方外部 API：
- 读位置 `GetTimelineProperties()` / 写位置 `TryChangePlaybackPositionAsync(ticks)` / 打开文件 `ActivateForFile(AUMID, …)`
- 不存在 `ms-media-player:` 协议；`shell:AppsFolder` 忽略命令行参数；无 startTime 语义

## 决策树

```
探针 [3] 出现 Microsoft.ZuneMusic 会话？
├─ 是 + IsPlaybackPositionEnabled=True  → 路线 A：伴生工具（3–6h 原型 / 8–20h 稳定）
│    └─ 再跑 --seek 600：True → 路线 A 完全成立
├─ 是 + 不可 seek                        → 路线 B：mpv
└─ 否（确认播放器在播）                 → 路线 B：mpv

路线 B 配方：%APPDATA%\mpv\mpv.conf → save-position-on-quit=yes
```

## 待办

- [ ] 跑人工三组对照（资源管理器双击 / Home 最近播放 / 完全退出后重开），确认原生行为
- [ ] 跑探针，判定 SMTC seek 可行性
- [ ] 若走路线 A：写最小可跑版本（读 AppState.json → ActivateForFile → SMTC seek → 打印结果）
- [ ] 若走路线 A：建 per-file SQLite 表（因为原生只存一条）
- [ ] 若走路线 A：加 IExplorerCommand 右键扩展（做成可选项，别替换默认关联）
- [ ] （可选）复核 MSIX manifest，确认无 `windows.protocol` / `windows.appExtension`

## 未证实的缺口

- MSIX manifest 未直接读到（rg-adguard 被 Cloudflare 拦）→「无自定义协议」是三重否定推定
- Media Player 是否实现 `PlaybackPositionChangeRequested`（探针直接判这个）
- UIA 进度条形态（零证据，不建议依赖）
- 22H2~25H2 行为差异

## 相关文件

- `HANDOFF.md` —— 完整交接文档（**先读这个**）
- `docs/feasibility-report.md` —— 主报告
- `docs/options-deep-dive.md` —— 路线展开版
- `probe/` —— 探针脚本 + mpv 配置
- `src/MediaResumeProbe/` —— C# 备用探针
- `memory/win11-mediaplayer-resume.md` —— 沉淀的技术结论
