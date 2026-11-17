# Windows 11 新版媒体播放器「每次打开视频从上次断点继续」——可行性验证报告

> 调研日期：2026-11-17 ｜ 三路并行取证（扩展点 / 存储机制 / 备选方案），全部结论标注证据等级。
> 关键证据来自微软官方文档、MSFT 员工署名答复、取证文章原始截图、第三方实测代码。

---

## 一句话结论

> **「给新版媒体播放器写插件」这条路在官方层面是关闭的（微软明确答复：不支持插件）。**
> **但你要的功能本身不需要插件就能实现** —— 新版播放器已经把你的断点落盘了，而且微软提供了官方的外部接口来「读位置」和「跳位置」。
> **真正的分岔点是：外部能不能驱动它跳到指定秒数。** 这个有 1 小时的探针脚本可以当场判定（见第六节）。

---

## 一、三个决定性事实

| # | 事实 | 等级 |
|---|---|---|
| 1 | 新版媒体播放器**没有任何插件/加载项扩展点**。微软社区支持专员（署名 MSFT）直接答复：*"Media Player does not have the ability to add plug-ins directly, due to product design."* | 【官方表态】 |
| 2 | 它**已经把播放位置落盘**在 `LocalState\AppState.json`（`PlayerModel.CurrentPosition`），并且**注册了 SMTC 全局媒体会话**（任务栏/快速设置里能控制它就是证据） | 【已证实】 |
| 3 | 微软提供**官方的外部 seek API**：`GlobalSystemMediaTransportControlsSession.TryChangePlaybackPositionAsync(ticks)` | 【官方 API】 |

**换句话说**：技术路线不是「写插件」，而是「写一个伴生工具」，用官方 API 读/写播放位置。

---

## 二、为什么「真插件」不可行

### 2.1 它是什么

| 项 | 值 |
|---|---|
| Package Family Name | `Microsoft.ZuneMusic_8wekyb3d8bbwe`（沿用 Groove Music 血统，Store 沿用同一 listing `9WZDNCRFJ3PT`） |
| AUMID | `Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic` |
| 可执行文件 | `Microsoft.Media.Player.exe`（装在 `C:\Program Files\WindowsApps\...`） |
| EntryPoint | `Microsoft.Entertainment.Music.App` |
| 形态 | MSIX 打包的 UWP/WinUI 应用 |

### 2.2 三重封死

**① 微软官方表态（半官方但署名）**

> 提问（2024-11-01）："I am trying to add a foot pedal to use with Media Player. I cannot find a 'plug-ins' option."
> 答复（2024-11-02，**Zev - MSFT | Microsoft Community Support Specialist**）：
> **"Currently, Media Player does not have the ability to add plug-ins directly, due to product design."**
> — https://learn.microsoft.com/en-us/answers/questions/3937430/windows-media-player

同时，微软社区把第三方「Windows Media Player Plus」定性为**非官方插件**，并明确划线：
> "微软官方支持的媒体播放器包括：Windows Media Player（现称 Windows Media Player Legacy）、Media Player（Windows 11 中引入的新版媒体播放器）"
> — https://learn.microsoft.com/zh-cn/answers/questions/3880620/edge

**② 旧插件体系没被移植**

旧版 WMP12 有完整 COM 插件体系（UI Plug-in / DSP Plug-in / 可视化 / 皮肤），对应 Windows Media Player SDK —— 但官方文档页顶部挂着 legacy 横幅：
> *"The feature associated with this page, Windows Media Player SDK, is a legacy feature. It has been superseded by MediaPlayer. Microsoft strongly recommends that new code use MediaPlayer instead…"*
> — https://learn.microsoft.com/en-us/previous-versions/windows/desktop/wmp/windows-media-player-plug-ins

连旧皮肤机制都要退役了：
> "Skins will no longer be supported in Windows Media Player Legacy (WMPL) starting **November 10, 2026**, on Windows 11 version 24H2 and later."
> — https://learn.microsoft.com/en-us/previous-versions/windows/desktop/wmp/ambientattributes-acckeyboardshortcut

**③ 没有可传参的入口**

| 尝试 | 结果 |
|---|---|
| `ms-media-player:` 协议 | **不存在**。微软官方的内置 URI 方案总表里没有它，两次精确关键词检索零命中。旧版 WMP 的 `wmp11.assocprotocol.mms:` 是另一套注册表机制，新版不读 |
| `explorer shell:AppsFolder\...\Microsoft.ZuneMusic` 后带参数 | **参数被忽略**（该机制只调默认入口点） |
| 命令行直接跑 `Microsoft.Media.Player.exe` | UWP 应用不接受命令行参数；官方立场是 Store 应用不经命令行启动，文件/协议只能经 `FileActivatedEventArgs` / `ProtocolActivatedEventArgs` 传入 |
| 传 `startTime=` 之类 | **没有任何官方或社区已知的语义** |

> ⚠️ 一个诚实缺口：我没能下到 `Microsoft.ZuneMusic_*.msixbundle` 直接读 `AppxManifest.xml`（store.rg-adguard.net 被 Cloudflare 拦、GitHub 搜索在沙箱不可达）。"未注册自定义 URI 协议"是由「官方 URI 方案总表不含它 + 定向检索零命中 + 文档零提及」三重否定推定。**复核方法见第六节第 0 步。**

---

## 三、但功能本身：新版播放器已经存了断点

### 3.1 断点存在哪（取证文章原始截图，已逐字转录）

```
%LOCALAPPDATA%\Packages\Microsoft.ZuneMusic_8wekyb3d8bbwe\LocalState\
├── AppState.json      ← 断点本体
├── MediaPlayer.db     ← 最近播放历史（只有时间戳，没有位置列）
└── MediaPlayer.db-wal
```

`AppState.json` 关键片段（来源：https://dfdive.com/articles 内的原始 JSON 截图）：

```json
"PlayQueueModel": {
  "$type": "Microsoft.Media.Player.MediaServices.Interfaces.PlayQueueState",
  "Items": [ { "Source": "F:\\Video\\0019.MTS", "AccessToken": null, "Kind": 2 } ],
  "CurrentItemIndex": 0
},
"PlayerModel": {
  "$type": "Microsoft.Media.Player.MediaServices.Interfaces.PlayerModelState",
  "PlaybackRate": 1.0,
  "CurrentPosition": "00:00:10.3495708"      ← 断点
}
```

| 字段 | 含义 |
|---|---|
| `PlayerModel.CurrentPosition` | 断点位置，.NET TimeSpan 字符串（7 位小数秒） |
| `PlayQueueModel.Items[].Source` | 文件完整路径（**按路径匹配**，改名/移动即失效） |

### 3.2 关键限制（这条决定了工作量的形状）

> **它只存一条断点，跟着「当前队列项」走 —— 不是 per-file 断点表。**

推论（基于字段形态，非实测）：
- 播 A → 播 B → 断点被 B 覆盖 → 再开 A，A 的断点已丢
- 所以原生最多做到「记住最后一个文件」；要做到「每个文件各自续播」，**必须外部自建 (路径 → 位置) 映射表**

### 3.3 原生到底会不会自动续播？—— 没有文档，必须实测

| 来源 | 结论 |
|---|---|
| 官方发行说明（2026 全年版本，grep `resume`/`continue`/`position`） | **零命中** — https://learn.microsoft.com/en-us/windows-insider/release-notes/apps/media-player |
| Store 官方功能清单 | **无续播条目** — https://www.microsoft.com/store/p/groove-musica/9wzdncrfj3pt |
| 设置项 | 只有 Privacy → **Remember recent media**（11.2409.11.0 起），管的是首页「最近播放」**列表**，**不是**断点 — https://www.elevenforum.com/t/enable-or-disable-remember-recent-media-in-media-player-app-in-windows-11.29316/ |
| **gHacks 2021 首发实测（第一手）** | "**The player resumes videos from the last playback point.**"（作者称 FLAC/MP3/MP4/MKV 均测）— 论坛转载原文可见于 windowsphoneinfo（帖尾注明首发于 gHacks，2021-11-18）：https://www.windowsphoneinfo.com/threads/new-media-player-for-windows-11-released-for-insiders-on-the-dev-channel.620865 |
| 大量 SEO 内容站 | 互相矛盾（有的说默认可续，有的说不提供可靠断点），**不采信** |

**判定：原生续播行为 = 「社区第一手实测说有，但微软零文档、零开关、零参数」。必须在你机器上用 5 分钟三组对照确认。**

---

## 四、外部怎么驱动它跳转 —— 这是整条路线的生死点

### 4.1 唯一正规接口：SMTC

```csharp
// 读当前位置
var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
var session = mgr.GetSessions()
    .First(s => s.SourceAppUserModelId == "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic");
var tl = session.GetTimelineProperties();
// tl.Position (TimeSpan) / tl.EndTime / tl.MinSeekTime / tl.MaxSeekTime

// 写位置（跳转）
bool ok = await session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(600).Ticks);
```

官方 API 原文：
> `TryChangePlaybackPositionAsync(Int64)` — *"Attempts to change the playback position on the session to the specified time, in ticks."* 返回 `IAsyncOperation<bool>`，*"True indicates success; False otherwise"*
> — https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession.trychangeplaybackpositionasync

前置条件：`GetPlaybackInfo().Controls.IsPlaybackPositionEnabled == true`。
调用方需 manifest 能力 `globalMediaControl`。

⚠️ **重要限制**：这个 API 只是**向目标 App 发请求**，目标 App 必须自己处理 `PlaybackPositionChangeRequested` 事件才会真的跳。Media Player 用 `MediaPlayer` 类会自动集成 SMTC（所以它出现在任务栏媒体面板里），但**它是否实现了位置变更请求，没有任何公开文档**。
同类架构的实测参照（Spotify，10 秒跳转 ~120ms 生效）：https://github.com/aHuddini/UniPlaySong/blob/main/docs/dev_docs/features/SPOTIFY_INTEGRATION.md

### 4.2 其他路线的可靠性排序

| 手段 | 可行性 | 说明 |
|---|---|---|
| **SMTC `TryChangePlaybackPositionAsync`** | ⭐ 最高（唯一官方 API） | 成败取决于 App 是否实现位置变更请求；**1 小时可判定生死** |
| 读 `AppState.json` | ✅ 高 | 文件与字段已逐字确认，只读，无风险 |
| 打开指定文件 | ✅ 高 | `IApplicationActivationManager::ActivateForFile(AUMID, IShellItemArray, verb, pid)` 或 `Launcher.LaunchFileAsync(file, LauncherOptions{ TargetApplicationPackageFamilyName = "Microsoft.ZuneMusic_8wekyb3d8bbwe" })`（官方 API） |
| 媒体键注入 `VK_MEDIA_PLAY_PAUSE` 等 | ⚠️ 中 | 机制成立（OS 把媒体键路由到当前 SMTC 会话），但只能 play/pause/next/prev，**没有绝对定位** |
| UI Automation 拖进度条 | ❌ 低，不建议 | XAML `Slider` 理论上支持 `RangeValuePattern.SetValue()`，但新版播放器的进度条**是否以 Slider 暴露、AutomationId 是什么，完全无一手证据**；依赖控件树，随版本重写就废 |
| `IMFMediaEngine` | ❌ 不适用 | 只能控制自己创建的播放会话，等于自己写播放器 |

### 4.3 「伪插件」外壳（把你的功能挂到媒体播放器旁边）

官方明确支持：
- **Win11 新版右键菜单扩展**：`IExplorerCommand` + manifest 的 `windows.fileExplorerContextMenus` + `windows.comServer`
  — https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer
- **自定义协议 + 文件关联**：你的 App 注册协议成为默认处理程序 → 收到激活后把文件转交媒体播放器
  — https://learn.microsoft.com/en-us/windows/apps/develop/launch/handle-uri-activation

⚠️ **不可行的方向**：把**微软的媒体播放器**注册成你的自定义协议处理程序。它必须在自己的 manifest 里声明该协议，而它的 manifest 由微软签名，第三方改不了。

---

## 五、方案对比（按推荐度）

| # | 路线 | 确切做法 | 工作量 | 抗更新 | 达成"每次打开都续播" |
|---|---|---|---|---|---|
| 1 | **换 mpv** ⭐ | `%APPDATA%\mpv\mpv.conf` 写 `save-position-on-quit=yes`（`resume-playback` 默认 yes）+ 改文件关联 | **0.5–2 人时** | **高**（纯配置文件） | ✅ 真正 per-file，静默续播 |
| 2 | VLC | Preferences → Interface → Continue playback? = **Always**（**且必须勾 Save recently played items**） | 0.5–1 人时 | 高 | ✅ |
| 3 | MPC-HC / MPC-BE | Options → Player → History | 0.5–1 人时 | 高 | ✅ |
| 4 | PotPlayer | F5 → 播放 → 记忆播放位置（默认关） | 0.5 人时 | 高 | ✅ |
| 5 | **系统播放器 + 外部伴生工具** | 探针判定 SMTC 可 seek → 做：读 AppState.json 建 per-file 表 → `ActivateForFile` 打开 → `TryChangePlaybackPositionAsync` 跳转 → 注册 `IExplorerCommand` 右键项 | 3–6 人时原型 / 8–20 人时稳定 | **中～低**（依赖未公开契约） | ⚠️ 可能 |
| 6 | 纯原生，什么都不做 | 直接用系统媒体播放器 | 0 | — | ❓ 待实测（可能只记住最后一个文件） |
| 7 | 自建播放器壳 | IMFMediaEngine 自己播 + 自有进度库 | 8–16 人时 | 高 | ✅ 完全可控 |

---

## 六、30 分钟内的判定步骤（建议按这个顺序做）

### 第 0 步：人工三组对照（5 分钟，决定要不要写代码）

用**同一个**视频，每组都记下实际起播位置：

| 组 | 操作 | 期望（若原生续播成立） |
|---|---|---|
| A | 资源管理器里双击打开 | ? |
| B | 媒体播放器 Home → 最近播放 → 点入 | ? |
| C | 完全退出播放器后，重新双击同一个文件 | ? |

> gHacks 的第一手实测指向 B/C 会续播、A 不一定。若 A 也能续播 → **直接收工，零开发**。

### 第 1 步：跑探针脚本（10 分钟，判定外部驱动生死）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Probe-MediaPlayerResume.ps1
```

脚本会依次输出：包身份 → `AppState.json` 里的断点 → **系统里所有 SMTC 会话**（含是否出现 `Microsoft.ZuneMusic`、位置、是否允许 seek）→ 判定汇总。

**先让媒体播放器播着一个视频再跑**（SMTC 会话只在播放时存在）。

判定：
- 出现 `Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic` 且 `IsPlaybackPositionEnabled = True` → **路线 5 成立**
- 没出现，或 `IsPlaybackPositionEnabled = False` → **路线 5 判死，直接上路线 1（mpv）**

### 第 2 步（可选）：真写一次 seek

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Probe-MediaPlayerResume.ps1 -SeekToSeconds 600
```

返回 `True` = 外部跳转真的生效（连跳 10 分钟）；返回 `False` = 播放器拒绝。

### 第 3 步：确认 manifest（想钉死"没有协议"这条结论时）

1. 浏览器打开 https://store.rg-adguard.net/ → `PackageFamilyName` 填 `Microsoft.ZuneMusic_8wekyb3d8bbwe` → Ring 选 Fast
2. 下 `Microsoft.ZuneMusic_*.msixbundle` → 7-Zip 解压 → 打开 `App_*_x64\AppxManifest.xml`
3. 查 `<Extensions>`：预期只有 `windows.fileTypeAssociation`（媒体扩展名）+ 可能 `windows.shareTarget`/`windows.appService`；**没有** `windows.protocol`，**没有** `windows.appExtension`（后者才是 UWP 真正的第三方可插入扩展点）

---

## 七、我的建议

1. **先做第 0 步 + 第 1 步**，一共 15 分钟，不要急着写代码。
2. 如果探针判定 SMTC 可 seek → 走**路线 5**，但把「伴生工具」做成**可选的右键入口**（`IExplorerCommand` → "用媒体播放器续播打开"），而不是替换默认程序。这样既满足需求，又不会在播放器更新后让用户的双击行为整个崩掉。
3. 如果探针判定失败，或者你就是想要「每个文件各自记断点」的确定行为 → **直接上 mpv**。`save-position-on-quit=yes` 一行配置，0.5–2 人时，跨版本稳定，可编程（`--start=`、`--input-ipc-server` 命名管道 IPC），而且顺手解决「续播」以外的问题（字幕、硬解、脚本化）。
4. **不建议**投入「外部 UIA 拖进度条」路线：零一手证据、依赖控件树、随版本重写即废。

> 顺带一提：新版媒体播放器是 XAML 应用，Windows 11 上曾有 KB5072911 导致 XAML 依赖应用（开始菜单、资源管理器、设置）无法启动的 bug —— 这类 UWP 运行时脆弱性，是「深度绑定它」比「换个播放器」风险更高的又一个理由。
> — https://support.microsoft.com/en-us/topic/kb5072911-explorer-the-start-menu-and-other-xaml-dependent-apps-might-not-start-or-close-unexpectedly-on-some-enterprise-devices-d2d30684-4e2b-47f5-9899-a00a8e0acb09

---

## 附录 A：证据等级与主要来源

**微软官方文档（一手）**
- `TryChangePlaybackPositionAsync` — learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession.trychangeplaybackpositionasync
- `GlobalSystemMediaTransportControlsSession` / `...TimelineProperties` / `...PlaybackControls` — learn.microsoft.com/en-us/uwp/api/windows.media.control.*
- `IApplicationActivationManager::ActivateForFile` — learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateforfile
- `LauncherOptions.TargetApplicationPackageFamilyName` — learn.microsoft.com/en-us/uwp/api/windows.system.launcheroptions.targetapplicationpackagefamilyname
- Windows Media Player Plug-ins（**legacy 横幅**）— learn.microsoft.com/en-us/previous-versions/windows/desktop/wmp/windows-media-player-plug-ins
- 内置 URI 方案总表 — learn.microsoft.com/en-us/windows/apps/develop/launch/launch-default-app
- File Explorer 右键菜单扩展（IExplorerCommand）— learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer
- Media Player 发行说明 — learn.microsoft.com/en-us/windows-insider/release-notes/apps/media-player

**微软员工署名 / 微软运营的问答（半官方）**
- 「Media Player does not have the ability to add plug-ins directly, due to product design.」— learn.microsoft.com/en-us/answers/questions/3937430/windows-media-player
- 官方播放器范围界定 — learn.microsoft.com/zh-cn/answers/questions/3880620/edge
- GSMTC 在服务/SYSTEM 上下文不可用 — learn.microsoft.com/en-us/answers/questions/1263190/winrar

**取证 / 技术分析**
- `AppState.json` / `MediaPlayer.db` 原始截图 — dfdive.com/articles
- MediaPlayer.db 最近播放查询 — github.com/ardfr/mediaplayer-localcache-correlator

**社区第一手**
- gHacks 新版播放器「resumes videos from the last playback point」— windowsphoneinfo 论坛转载原帖（2021-11-18，帖尾注明首发于 gHacks）
- Remember recent media 开关 — elevenforum.com/t/enable-or-disable-remember-recent-media-in-media-player-app-in-windows-11.29316/
- 清最近播放 — elevenforum.com/t/clear-recent-media-history-in-media-player-app-in-windows-11.17141/latest
- Spotify 走 SMTC seek 实测 — github.com/aHuddini/UniPlaySong/blob/main/docs/dev_docs/features/SPOTIFY_INTEGRATION.md
- manifest 实际 PowerShell 输出 — stackoverflow.com/questions/79825660

**不予采信**：techdows / sekin / 101-help / laptopjudge / positioniseverything / techbloat / pcnmobile / techyorker 等批量 SEO 站 —— 措辞互相矛盾且无一手测试。

## 附录 B：配套文件

| 文件 | 用途 |
|---|---|
| `Probe-MediaPlayerResume.ps1` | 探针：包身份 / 断点落盘 / SMTC 会话 / 可选 seek 写测试 |
| `mpv.conf` | 路线 1 的现成配置（放到 `%APPDATA%\mpv\mpv.conf`） |
| `resume-playback-options.md` | 8 条路线的完整展开版对比（含全部 URL 与实测脚本） |
