# HANDOFF —— Windows 11 新版媒体播放器「视频断点续播」

> 这份文档是给**接手这个任务的人或本地 Agent** 看的。读完这一份，你不需要重读对话历史就能继续推进。
> 最后更新：2026-11-17

---

## 0. 原始需求（用户原话）

> 「可以编写一个 win11 新版媒体播放器的插件吗，使得每一次打开视频都从上次播放的断点打开，先验证可行性」

拆成三件事：
1. **能不能**给新版媒体播放器写插件？
2. 如果能/不能，**怎么实现**「每次打开视频都从上次断点打开」？
3. 这一轮**只做可行性验证**，不急着写实现。

**当前状态**：第 3 步已完成（见 `docs/feasibility-report.md`），第 1、2 步的结论也已给出。
**下一步卡在**：需要一台真实的 Windows 11 机器跑探针（当前沙箱是 Linux，跑不了）。

---

## 1. 结论摘要（30 秒版）

| 问题 | 答案 |
|---|---|
| 能写插件吗？ | **不能**。微软官方明确表态不支持；UWP/MSIX 应用没有加载项宿主机制 |
| 那功能能做吗？ | **能**。不需要插件 —— 走「外部伴生工具 + 官方 SMTC API」 |
| 已经原生满足了吗？ | **部分**。原生只存**一条**断点（跟着当前队列项走），且微软零文档；一手实测称会续播 |
| 最稳的兜底方案 | **换 mpv**，`mpv.conf` 一行 `save-position-on-quit=yes`，0.5–2 人时 |
| 生死判定点 | SMTC 的 `TryChangePlaybackPositionAsync` 对 Media Player 是否返回 True |

---

## 2. 已核实的技术事实（别重新推导，直接用）

### 2.1 目标应用身份

```
PackageFamilyName : Microsoft.ZuneMusic_8wekyb3d8bbwe
AUMID             : Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic
Executable        : Microsoft.Media.Player.exe
EntryPoint        : Microsoft.Entertainment.Music.App
Store ProductID   : 9WZDNCRFJ3PT   （沿用旧 Groove Music 的同一 listing）
```

### 2.2 断点数据落盘位置

```
%LOCALAPPDATA%\Packages\Microsoft.ZuneMusic_8wekyb3d8bbwe\LocalState\
├── AppState.json      ← 断点本体
├── MediaPlayer.db     ← 最近播放历史（只有时间戳，没有位置列！）
└── MediaPlayer.db-wal
```

`AppState.json` 结构（来自取证文章内嵌的原始 JSON 截图，已逐字转录）：

```json
"PlayQueueModel": {
  "$type": "Microsoft.Media.Player.MediaServices.Interfaces.PlayQueueState",
  "Items": [ { "Source": "F:\\Video\\0019.MTS", "AccessToken": null, "Kind": 2 } ],
  "Permutation": [0], "CurrentItemIndex": 0,
  "IsShuffleEnabled": false, "IsRepeatAllEnabled": false
},
"CurrentItemStateProvider": {
  "$type": "Microsoft.Media.Player.MediaServices.Interfaces.CurrentItemState",
  "ActiveAudioIndex": 0, "ActiveSubtitleId": null, "ExternalSubtitleSources": []
},
"PlayerModel": {
  "$type": "Microsoft.Media.Player.MediaServices.Interfaces.PlayerModelState",
  "IsRepeatOnceEnabled": false, "IsPlayAs360Enabled": false,
  "PlaybackRate": 1.0,
  "CurrentPosition": "00:00:10.3495708"     ← 断点位置
}
```

| 字段 | 含义 / 注意 |
|---|---|
| `PlayerModel.CurrentPosition` | .NET TimeSpan 字符串，7 位小数秒（`"00:00:10.3495708"`） |
| `PlayQueueModel.Items[].Source` | 完整本地路径；**按路径匹配** → 改名/移动后失效 |
| `Kind` | `2` = 视频（`1` 大概是音频，未 100% 确认） |

### 2.3 三个必须记住的限制

1. **只存一条断点**，跟着 `PlayQueueModel` 的当前项走 —— **不是 per-file 表**。播 A → 播 B → A 的断点丢失。
2. **按路径匹配**，没有内容哈希兜底 → 改名/移动后必然失效。
3. **`MediaPlayer.db` 里没有位置列**。网上大量教程说"删 MediaPlayer.db-wal 清历史"，那跟断点无关，别搞混。

### 2.4 原生续播行为：未知，必须实测

| 来源 | 说了什么 |
|---|---|
| 官方发行说明（2026 全年） | grep `resume`/`continue`/`position` **零命中** |
| Store 官方功能清单 | **无**续播条目 |
| 设置项 | 只有 Privacy → **Remember recent media**（11.2409.11.0 起）→ 管首页「最近播放」**列表**，不是断点 |
| gHacks 2021 一手实测 | "**The player resumes videos from the last playback point.**" |
| 大量 SEO 站 | 互相矛盾，**不采信** |

### 2.5 外部接口（全部是官方 API）

```csharp
// 读
var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
var s = mgr.GetSessions()
    .First(x => x.SourceAppUserModelId == "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic");
var tl = s.GetTimelineProperties();
// tl.Position (TimeSpan) / tl.EndTime / tl.MinSeekTime / tl.MaxSeekTime / tl.LastUpdatedTime

// 写（跳转）
bool ok = await s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(600).Ticks);
```

| 事实 | 值 |
|---|---|
| 位置单位 | ticks = 100ns = `TimeSpan.Ticks` |
| 前置条件 | `s.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled == true` |
| 调用方要求 | manifest 能力 `globalMediaControl`；需交互式桌面会话（服务/计划任务里不可用） |
| 打开指定文件 | `IApplicationActivationManager::ActivateForFile(AUMID, IShellItemArray, verb, pid)`<br>或 `Launcher.LaunchFileAsync(file, LauncherOptions{ TargetApplicationPackageFamilyName = "Microsoft.ZuneMusic_8wekyb3d8bbwe" })` |

### 2.6 已排除的路子（别再试）

| 路子 | 为什么不行 |
|---|---|
| 写 DLL 插件进播放器进程 | UWP/MSIX 无加载项宿主；旧 WMP12 SDK 已 legacy 且未移植 |
| `ms-media-player:` 协议 | 不存在（微软内置 URI 方案总表里没有，定向检索零命中） |
| `explorer shell:AppsFolder\...` 带参数 | 参数被忽略，该机制只调默认入口点 |
| 直接跑 `Microsoft.Media.Player.exe` 带命令行 | UWP 应用不接受命令行参数 |
| 媒体键注入做绝对定位 | 媒体键只有 play/pause/next/prev，**没有绝对 seek** |
| `IMFMediaEngine` 控制别的播放器 | 只能控制自己创建的播放会话 |
| 把微软媒体播放器注册成自定义协议处理程序 | 必须由它自己的 manifest 声明协议，第三方改不了 |
| UIA 拖进度条 | 零一手证据；XAML 控件树随版本重写即废（**不建议依赖**） |
| 改 `MediaPlayer.db` 的位置 | 那张表根本没有位置列 |

---

## 3. 下一步：30 分钟判定流程

### 第 0 步 · 人工三组对照（5 分钟）

同一个视频，每组记下**实际起播位置**：

| 组 | 操作 |
|---|---|
| A | 资源管理器里双击打开 |
| B | 媒体播放器 Home → 最近播放 → 点入 |
| C | 完全退出播放器后，重新双击同一个文件 |

> gHacks 一手实测指向 B/C 会续、A 不一定。**若 A 也能续 → 直接收工，零开发。**

### 第 1 步 · 跑探针（10 分钟）

**先让媒体播放器播着一个视频**（SMTC 会话只在播放时存在），然后：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\probe\Probe-MediaPlayerResume.ps1
```

或直接双击 `probe\run-probe.cmd`。

### 第 2 步 · 分流决策

```
探针输出 [3] 里出现 Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic ?
│
├─ 是，且 IsPlaybackPositionEnabled = True
│    → 路线 A「伴生工具」成立，继续第 3 步
│
├─ 是，但 IsPlaybackPositionEnabled = False
│    → 半死。外部 seek 大概率被拒 → 走路线 B（mpv）
│
└─ 否（根本没有这个会话）
     → 先确认播放器是否在播放（重跑）；仍在播放还是没有
        → 走路线 B（mpv）

IsPlaybackPositionEnabled = True 时，再做一次真写验证：
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\probe\Probe-MediaPlayerResume.ps1 -SeekToSeconds 600
返回 True = 外部跳转真的生效（跳到 10 分钟）→ 路线 A 完全成立
返回 False = 播放器拒绝 → 走路线 B
```

### 第 3 步 · 按路线实施

#### 路线 A · 系统播放器 + 外部伴生工具（3–6 人时原型 / 8–20 人时稳定）

架构（.NET 8，C#）：

```
┌─ MediaResume.exe（伴生工具）
│
├─ [记录端] 后台轮询 SMTC
│    每 2s 取 session.GetTimelineProperties().Position
│    + 媒体标识（取 SMTC 媒体属性里的标题/艺术家，或自己映射）
│    → 写入本地 per-file 表  SQLite: (path, position, duration, updated_at)
│    ★ 关键：必须自己建 per-file 表，因为 AppState.json 只存一条
│
├─ [播放端] 接管打开动作
│    a) 文件关联 / 自定义协议 → 收到文件路径
│    b) 查本地表命中？→ 无 → 直接 ActivateForFile 打开
│    c) 命中 → ActivateForFile 打开 → 等 SMTC 会话出现
│            → TryChangePlaybackPositionAsync(命中位置)
│            → （可选）TryPauseAsync，先暂停等用户点播放
│
└─ [入口] IExplorerCommand 右键扩展
     "用媒体播放器续播打开"（推荐做成可选右键项，不改默认关联）
     manifest: windows.fileExplorerContextMenus + windows.comServer
```

实施注意：
- **别替换默认文件关联**。做成右键项更安全 —— 播放器更新导致 SMTC 失效时，用户双击行为不受影响。
- 定位「正在播放的是哪个文件」是难点：SMTC 的媒体属性（标题/艺术家）不一定是完整路径。可以配合读 `AppState.json` 的 `PlayQueueModel.Items[].Source` 做交叉验证。
- 先写最小可跑版本：读 AppState.json → 打印断点 → ActivateForFile → SMTC seek → 打印结果。跑通再加外壳。

#### 路线 B · 换 mpv（0.5–2 人时）

`%APPDATA%\mpv\mpv.conf`（样例见 `probe/mpv.conf`）：

```ini
save-position-on-quit=yes
resume-playback-check-mtime=yes
```

然后把 `.mp4/.mkv/.avi/.mov` 等关联指向 mpv。
**注意**：mpv.exe 是无控制台程序，做文件关联时建议指向 `mpv.com` 或写个 `.bat` 包装，否则黑框一闪而过会很难受。

---

## 4. 资源索引

| 路径 | 内容 |
|---|---|
| `docs/feasibility-report.md` | **主报告**：结论 + 完整证据链 + 8 条路线对比表 + 判定步骤 |
| `docs/options-deep-dive.md` | 8 条路线逐条展开（含全部 URL 与各播放器的官方配置文档引用） |
| `probe/Probe-MediaPlayerResume.ps1` | 探针：包身份 / 断点落盘 / SMTC 会话 / 可选 seek 写测试 |
| `probe/run-probe.cmd` | 双击即跑的包装 |
| `probe/mpv.conf` | 路线 B 的现成配置 |
| `src/MediaResumeProbe/` | C# 备用探针（.NET 8 + WinRT 投影），当 PowerShell 的 WinRT await 桥在你的环境里不稳时用 |
| `memory/win11-mediaplayer-resume.md` | 已沉淀的技术结论（等价于 Agent memory topic `win11-mediaplayer-resume`） |
| `MEMORY.md` | 本项目的持久化结论摘要 |

---

## 5. 尚未证实的缺口（**别当结论用**）

1. **未能直接读 `AppxManifest.xml`** —— `store.rg-adguard.net` 被 Cloudflare 拦、GitHub 代码搜索在沙箱不可达、Store 官方 API 需授权票据。
   → 「未注册自定义 URI 协议」是由「官方 URI 方案总表不含它 + 定向检索零命中 + 文档零提及」三重**否定推定**。
   → 复核方法：https://store.rg-adguard.net/ → PackageFamilyName 填 `Microsoft.ZuneMusic_8wekyb3d8bbwe` → Ring 选 Fast → 下 `Microsoft.ZuneMusic_*.msixbundle` → 7-Zip 解压 → 看 `App_*_x64\AppxManifest.xml` 的 `<Extensions>`。预期：只有 `windows.fileTypeAssociation`（媒体扩展名）+ 可能 `windows.shareTarget`/`windows.appService`；**没有** `windows.protocol`，**没有** `windows.appExtension`。
2. **Media Player 是否实现 `PlaybackPositionChangeRequested`** —— 这决定 SMTC seek 能不能成。**探针第 2 步直接判这个。**
3. **UIA 里进度条是否以 Slider/RangeValue 暴露** —— 零一手证据，建议不依赖。
4. **22H2 / 23H2 / 24H2 / 25H2 之间续播行为差异** —— 查不到一手实测。
5. **Reddit / 知乎的一手用户实测正文** —— 本轮网络受限，未取到。

---

## 6. 风险与已知坑

| 风险 | 说明 |
|---|---|
| **XAML 应用运行时脆弱** | KB5072911：Windows 11 上 XAML 依赖应用（开始菜单、资源管理器、设置）可能无法启动。深度绑定媒体播放器的长期风险 > 换个播放器 |
| **播放器更新会改结构** | `AppState.json` 的字段名可能变；`MediaPlayer.db` 表结构也可能变。读的时候要做**容错**（字段缺失降级，不要抛异常崩掉） |
| **SMTC 会话只在播放时存在** | 探针和工具都要处理「会话不存在」的情况 |
| **GSMTC 需交互式桌面** | 不能在服务/计划任务/无头会话里用（微软 Q&A 已确认） |
| **`RequestAsync()` 昂贵** | 首次可能卡数秒；实际工具里要缓存 + 单飞合并 |
| **PowerShell 探针的 WinRT await 桥** | 需要 Windows PowerShell 5.1（`powershell.exe`），**PS7 不支持**该投影方式。若报错就换 `src/MediaResumeProbe/` 的 C# 版本 |
| **C# 探针已编译验证** | 已在 .NET 9 SDK 下以 `net8.0-windows10.0.19041.0` 编译通过（0 error / 0 warning），验证了 `Windows.Media.Control` 那套 API 签名正确。**但未在真实 Windows 上运行过**（沙箱是 Linux） |

---

## 7. 主要证据来源（原始 URL）

**微软官方文档**
- SMTC seek：`https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession.trychangeplaybackpositionasync`
- SMTC 会话类：`https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession`
- 时间轴属性：`https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessiontimelineproperties`
- 打开文件：`https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateforfile`
- 文件关联启动：`https://learn.microsoft.com/en-us/uwp/api/windows.system.launcheroptions.targetapplicationpackagefamilyname`
- 右键菜单扩展：`https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer`
- 旧插件体系（legacy）：`https://learn.microsoft.com/en-us/previous-versions/windows/desktop/wmp/windows-media-player-plug-ins`
- 内置 URI 方案总表：`https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-default-app`
- 发行说明：`https://learn.microsoft.com/en-us/windows-insider/release-notes/apps/media-player`
- KB5072911：`https://support.microsoft.com/en-us/topic/kb5072911-explorer-the-start-menu-and-other-xaml-dependent-apps-might-not-start-or-close-unexpectedly-on-some-enterprise-devices-d2d30684-4e2b-47f5-9899-a00a8e0acb09`

**MSFT 署名答复（"不支持插件"的关键证据）**
- `https://learn.microsoft.com/en-us/answers/questions/3937430/windows-media-player`
- `https://learn.microsoft.com/zh-cn/answers/questions/3880620/edge`
- `https://learn.microsoft.com/en-us/answers/questions/1263190/winrar`（GSMTC 在服务上下文不可用）

**取证 / 一手实测**
- `AppState.json` 原始截图：`https://dfdive.com/articles`
- gHacks「resumes videos from the last playback point」：`https://www.windowsphoneinfo.com/threads/new-media-player-for-windows-11-released-for-insiders-on-the-dev-channel.620865`
- Remember recent media 开关：`https://www.elevenforum.com/t/enable-or-disable-remember-recent-media-in-media-player-app-in-windows-11.29316/`
- 清最近播放：`https://www.elevenforum.com/t/clear-recent-media-history-in-media-player-app-in-windows-11.17141/latest`
- Spotify 走 SMTC seek 实测：`https://github.com/aHuddini/UniPlaySong/blob/main/docs/dev_docs/features/SPOTIFY_INTEGRATION.md`
- manifest 实际 PowerShell 输出：`https://stackoverflow.com/questions/79825660/how-do-i-send-arguments-to-application-in-windows-launched-with-shellappsfolder`

**备选播放器官方文档**
- mpv 手册（RESUMING PLAYBACK）：`https://mpv.io/manual/stable/`
- VLC Continue playback：`https://www.vlchelp.com?p=1285/`
- MPC-HC History：`https://github.com/clsid2/mpc-hc`
- PotPlayer：`https://potplayer.multimediaplayer.net/tutorials/potplayer-remember-playback-positions`

**不予采信**：techdows / sekin / 101-help / laptopjudge / positioniseverything / techbloat / pcnmobile / techyorker 等批量 SEO 站（措辞互相矛盾、无一手测试）。
