# Windows 11「每次打开视频都从上次断点继续」——备选方案对比与工程可行性判断

> 调研方式：web_search + web_fetch/直连抓取官方原文（mpv 官方手册、VLC 官方帮助站与官方论坛、MPC-HC 官方 GitHub、foobar2000 Hydrogenaudio Wiki、Microsoft Learn、Microsoft Store 官方产品页、Media Player 官方发行说明、MS 支持库 KB5072911）。
> 凡未找到官方文档的结论，一律标 **[未证实·需实测]**，并给出 5–15 分钟可完成的验证脚本。

---

## 0. 结论先行

- **最省事、最稳**：把默认播放器换成 **mpv**，在 `%APPDATA%\mpv\mpv.conf` 写一行 `save-position-on-quit=yes`（`--resume-playback` 默认就是 `yes`），再把文件关联指过去。**工作量 ≈ 0.5–2 人时，配置是纯文本文件，不会被版本更新打断。**
- **次选**：VLC 把「Continue playback?」设为 `Always`（同时必须勾上「Save recently played items」）；或 MPC-HC / MPC-BE（Options > Player > History）；或 PotPlayer（F5 > 播放 > 记忆播放位置）。都是 0.5–1 人时。
- **内置方案（Microsoft.ZuneMusic_8wekyb3d8bbwe）**：**没有官方开关、没有命令行参数、没有文档化的续播接口**，官方发行说明全篇没有出现 resume/continue/playback position 字样。它最多"从 Home 的『最近播放』点进去可能续上"（社区一致反馈，双击同一文件通常从头开始）→ **[未证实·需实测]**。作为"每次打开都续播"的唯一机制，**不成立**。
- **从外部主动控制一个 UWP 媒体播放器跳到指定秒数**：**技术上"可能"，但没有任何一条是可靠契约**。最接近可用的是 SMTC 的 `TryChangePlaybackPositionAsync`，但它只是向目标 App *发请求*，目标 App 是否实现 `PlaybackPositionChangeRequested` 决定了成败；Microsoft 的 ZuneMusic 播放在这一点上**没有任何公开文档**。

---

## 1. 路线一：Windows 11 内置媒体播放器（Microsoft.ZuneMusic_8wekyb3d8bbwe）

### 1.1 官方证据

| 事实 | 证据 |
|---|---|
| 官方 Store 产品页的功能清单：媒体库、视频收藏、mini player、拖放、缺失编解码器引导、播放速度、9 段均衡器、主题/强调色——**通篇没有「续播 / 记住播放位置」** | https://www.microsoft.com/en-us/p/muzica-groove/9wzdncrfj3pt |
| 官方《Media Player release notes》全页（2026 年 4 月～11 月各版本）**grep 不到** `resume` / `continue` / `playback position` / `where you left` 任一关键字 | https://learn.microsoft.com/en-us/windows-insider/release-notes/apps/media-player |
| 唯一可确认的持久化能力是「最近播放」列表：Settings > Privacy > **Remember recent media**（默认开，最多 20 条），Home 页显示最近播放的音视频 | https://geekrewind.com/manage-recent-media-settings-in-windows-11-media-player/ |
| 体积：Store 页标注 **Approximate size 50.3 MB**（已随系统预装） | https://www.microsoft.com/en-us/p/muzica-groove/9wzdncrfj3pt |
| 微软自 22H2 起把**旧版** Windows Media Player 降级为「可选功能」，并推荐 4K 等现代视频特性改用 Media Player app（即 UWP/WinUI 版） | https://support.microsoft.com/zh-cn/help/17948/ |
| N 版（欧盟版）不含媒体栈，需装 Media Feature Pack | https://support.microsoft.com/nb-no/windows/experience/platform-variants/media-feature-pack-for-windows-n |

### 1.2 能力判定（音频 vs 视频）

- **音频**：**没有**续播。Media Player 的音乐库只记录"最近播放的曲目"，不记录曲目内进度；连续播放天然从 0 开始。
- **视频**：**[未证实·需实测]**。多个二手来源称应用"remembers where you left off"、"quick resume"，且 Home 的最近播放条目会显示进度并续播：
  - https://techbloat.com/windows-11s-new-media-player-brings-big-improvements-to-audio-and-video.html
  - https://rottenwifi.com/how-to-use-the-media-player-app-in-windows-11/
  但这些都不是微软文档；**没有开关、没有参数、没有"从头开始"入口**（对比第三方商店播放器普遍提供"从头播放"），也没有任何对外可读的位置 API。

### 1.3 5 分钟实测脚本（建议先跑这个再决定要不要走外部路线）

1. 把一个 30 分钟本地 MP4 拖到 `Videos` 文件夹（进库），双击打开，播放到 10:00，按 **Esc/关闭按钮**（不要用任务栏停止）。
2. **A 路径**：回到 Media Player → Home → 点该视频条目。记录是否从 10:00 附近继续。
3. **B 路径**：资源管理器里**双击同一文件**。记录是否从头开始。
4. **C 路径**：把 Media Player 完全退出（含托盘）后再双击打开。
   → 若 A 续播、B/C 不续播 ⇒ 内置能力只覆盖"应用内最近播放入口"，**不满足"每次打开都续播"**。

### 1.4 结论

- 内置方案在**"每次打开都自动续播"**这个口径下：**不达标**（无开关、无参数、无契约）。
- 内置方案在**"从最近播放继续"**这个口径下：可能可用，但属于未文档化行为，随时可能被 Store 更新改掉（可靠性评级：**低**）。

---

## 2. 路线二：换播放器（推荐路线）

### 2.1 mpv —— 本次调研中证据最完整、最"可编程"的方案

官方手册 <https://mpv.io/manual/stable/> 原文要点：

> **RESUMING PLAYBACK**
> mpv is capable of storing the playback position of the currently playing file and resume from there the next time that file is played. This is done with the commands `quit-watch-later` (bound to **Shift+Q** by default) and `write-watch-later-config`, and with the `--save-position-on-quit` option. […] However if mpv is terminated abruptly and doesn't have the time to save, then the position will not be saved.

- `--save-position-on-quit` — Always save the current playback position on quit […] **This does not happen if playback of a file is stopped in other ways**（例如切下一集不保存）。
- `--resume-playback=<yes|no>` — Restore playback position from the watch_later configuration subdirectory （**default: yes**）。
- `--resume-playback-check-mtime=<yes|no>` — 仅当文件 mtime 未变才恢复（防同名不同内容误跳，默认 no）。
- `--watch-later-options=option1,...` — 除位置外还能存音量、音轨、字幕轨等；`--watch-later-options=start` 则只存位置。
- `--watch-later-dir=<path>` / `--write-filename-in-watch-later-config` / `--ignore-path-in-watch-later-config` — 控制 watch_later 文件位置与内容。
- `--save-watch-history` / `--watch-history-path` — 播放历史（隐私敏感，默认关闭）。
- 命令行跳转：`--start=<relative time>` — "Seek to given time position. The general format for times is [+|-][[hh:]mm:]ss[.ms]" → `mpv --start=600 file.mkv`。
- 外部进程控制：`--input-ipc-server` — Windows 上是**命名管道**，可走 JSON IPC 读写 `playback-time` 属性。
- Windows 路径（手册 **FILES ON WINDOWS**）：`mpv.conf` = `%APPDATA%/mpv/mpv.conf`；`watch_later` 目录 = `%LOCALAPPDATA%/mpv/watch_later`；`input.conf` 同在 `%APPDATA%/mpv/`；exe 同目录存在 `portable_config` 时全部改走该目录（**便携模式**）。

**确切用法（Windows）**：

- 永久生效：新建 `%APPDATA%\mpv\mpv.conf`，内容仅需一行
  ```
  save-position-on-quit=yes
  ```
  （`resume-playback` 默认为 yes，不必再写；若希望文件被替换后不误跳，加 `resume-playback-check-mtime=yes`。）
- 临时/命令行：`mpv --save-position-on-quit "D:\videos\a.mkv"`；退出时按 **Shift+Q** 才会写位置（普通 `q` 不写）。
- 强制从指定秒数起播（给包装器用）：`mpv --start=600 "D:\videos\a.mkv"`。

> ⚠️ **纠正两个常见误解**
> 1. mpv 的 input.conf 里**没有** `save-position` 这个命令名；官方命令是 `write-watch-later-config`（写位置但继续播）与 `quit-watch-later`（写位置并退出）。默认绑定：`Shift+Q`。
> 2. mpv 的 `Ctrl+Left/Right` 是**上一/下一字幕**，不是跳 60 秒；跳 60 秒是 `↑/↓`（`←/→` 是 5 秒）。

**mpv vs Windows 11 自带 Media Player 取舍**

| 维度 | Media Player（内置） | mpv |
|---|---|---|
| 安装 | 已预装，0 下载；Store 页标 ~50.3 MB | 需下载单个可移植 exe（几十 MB 量级，未精确核验）；`portable_config` 支持免安装 |
| 调用方式 | 只能由 shell/文件关联激活，**无 CLI 参数**、无位置参数 | 完整 CLI：`--start=`、`--save-position-on-quit`、`--input-ipc-server` |
| 配置持久性 | 设置存于 app 容器，UI 更新可能改布局 | 纯文本 `mpv.conf`/`input.conf`，选项名多年稳定 |
| 续播可控性 | 无开关、无参数、未文档化 | 三个开关 + 两个命令 + IPC，**完全可控** |
| 抗更新 | 中～低（Store 更新随时改行为，无契约） | 高（配置向后兼容性好） |

### 2.2 VLC —— 有开关，但默认是「Ask」

- 官方帮助站：Tools > Preferences > **Interface** 页 > **Continue playback?** 有 `Never / Ask / Always` 三档，**默认 Ask**，表现为顶部弹出 "Continue playback?" 提示条 + Continue 按钮。
  https://www.vlchelp.com?p=1285/
- 官方论坛给出的进阶路径：Preferences → Show settings = **All** → **Interface > Main interfaces > Qt** → "Continue playback?" 改 **Always**。
  https://forum.videolan.org/viewtopic.php?p=462350
- **两个前置条件（很容易踩坑）**：
  1. 必须同时启用 **Save recently played items**（Interface > Privacy / Network Interaction），否则续播不生效；
  2. 关闭方式要"正常关闭"才会落盘；路径被改名/移动/盘符变化后会视为新文件。
  参考：https://www.ghacks.net/2014/06/05/remember-position-using-vlc-media-player/ 、https://lawsoung1947.blogspot.com/2022/10/vlc-do-you-want-to-continue-where-you-left-off.html
- 命令行跳转：VLC 支持 `--start-time`（以 `vlc --longhelp` 输出为准；本次未抓到官方文档页，列为**待确认项**）。

### 2.3 MPC-HC / MPC-BE

- MPC-HC 官方 release notes / README 原文：
  > "MPC-HC can **remember recently played files and also their playback position, so you can resume playback from when you left** — **Options > Player > History**"
  https://github.com/clsid2/mpc-hc/releases/tag/2.8.0 、https://github.com/clsid2/mpc-hc
- 2.8.0 新增最近文件菜单里的 **Show History** 对话框，可搜索/清理/**Reset position**（说明"位置"是播放器自己维护的历史数据，非文件元数据）。
- MPC-BE 继承同一套历史/位置机制：Options > **Player > History** > 记住播放位置；配置文件键 `RememberFilePos=1`。
  https://www.9k9k.com/pcsoft/395.html
- 附带好处：MPC-HC 1.9.17 起支持 SMTC（Options > Advanced > UseSMTC），即它会出现在系统媒体会话里（影响"外部读取位置"的方案，见 §3）。

### 2.4 PotPlayer

- F5（或右键 > 选项）> **播放** > 勾选 **"Remember video playback positions" / 记忆播放位置**；右键播放列表可"清除记忆播放位置"。
  https://iwantaholodeck.com/enabling-full-screen-mode-and-resume-for-potplayer/
  https://potplayer.multimediaplayer.net/tutorials/potplayer-remember-playback-positions
- 默认关闭，需手动开启；仅对本地文件有效，网络流基本无效。

### 2.5 foobar2000

- 核心功能只有**会话级**恢复：Preferences > **Playback** > "**Save playback state when closing foobar2000 and resume on next startup**"（记住最后播放的曲目与位置，下次启动继续）。
  https://wiki.hydrogenaud.io/index.php?title=Foobar2000:Preferences:Playback
- **按曲目记住位置**属于第三方组件 `foo_resume`（数据存在 profile 目录下的文本文件）。
  https://www.webkkk.net/reda777/foo_resume
- foobar2000 本质是音频播放器，视频能力弱，不建议作为"视频续播"主方案。

---

## 3. 路线三：外部驱动 / 包装器（不换播放器）

### 3.1 技术前提逐条核实

| 前提 | 核实结果 | 证据 |
|---|---|---|
| SMTC 能不能**读**别的应用的播放位置？ | **能**。`GlobalSystemMediaTransportControlsSessionManager.RequestAsync()` → `GetCurrentSession()/GetSessions()` → `GetTimelineProperties()` 返回 `Position/StartTime/EndTime/MinSeekTime/MaxSeekTime/LastUpdatedTime`；`GetPlaybackInfo()` 返回状态与可用控件。 | https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager?view=winrt-26100 、https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession?view=winrt-26100 、https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessiontimelineproperties?view=winrt-26100 |
| 调用方需要什么权限？ | 需要应用能力 **`globalMediaControl`**（写在 package manifest 里）。 | 同上（"App capabilities: globalMediaControl"） |
| SMTC 能不能**写**位置？ | **有 API，但是"请求式"**：`GSMTCSession.TryChangePlaybackPositionAsync(Int64)` — "Attempts to change the playback position on the session to the specified time, in ticks." 目标 App 必须自己处理 `SystemMediaTransportControls.PlaybackPositionChangeRequested` 事件才会真的跳转；且 App 必须设置 `MinSeekTime/MaxSeekTime`，否则系统不会触发该请求。 | https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/system-media-transport-controls |
| 媒体键能 seek 吗？ | 只能 play/pause/next/prev/快进快退，**没有"跳到任意秒"**语义；SMTC 也没有"绝对定位"的按键。 | 同上 |
| UIA 能否定位进度条？ | UIA 是 WinUI/XAML 应用的通用自动化面（Slider/RangeValuePattern 理论上可用），但元素名、AutomationId、是否暴露 RangeValue 模式**完全由应用决定**，XAML 控件常常没有稳定 AutomationId。 | https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-uiautomationoverview |
| AHK 侧现成工具 | AHK v2 有成熟 UIA 封装 **UIA-v2**（基于 thqby 的 UIAutomation 库），自带 `UIATreeInspector.ahk` 可视化查树。 | https://github.com/Descolada/UIA-v2 |
| 24H2+ 是否"收紧了 UIA"？ | **没有找到任何官方证据**表明 Windows 11 24H2 收紧了 UI Automation。真实风险来自别处：XAML 依赖应用在 24H2/25H2 上出现过**注册竞态导致 Start/Explorer/设置打不开**的官方已知问题（KB5072911），说明 XAML 应用栈本身不稳定；以及微软持续把系统 UI 迁到 WinUI 3（任务栏、文件管理器属性对话框、Print Management 等），元素树会随版本变化。 | https://support.microsoft.com/en-us/topic/kb5072911-explorer-the-start-menu-and-other-xaml-dependent-apps-might-not-start-or-close-unexpectedly-on-some-enterprise-devices-d2d30684-4e2b-47f5-9899-a00a8e0acb09 |
| Media Foundation（IMFMediaEngine）能帮忙 seek 吗？ | **不能用来控制别人的播放器**。IMFMediaEngine 是给你自己的播放栈用的（自己打开文件、自己 seek），与目标 App 的播放会话无关。 | https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nn-mfmediaengine-imfmediaengine |
| 自定义协议 / 右键菜单怎么做？ | 官方路径：注册 `HKCR\<scheme>`（`URL Protocol` + `shell\open\command`）做自定义协议；上下文菜单用 **`IExplorerCommand`**（`GetTitle/Invoke/...`）。 | https://learn.microsoft.com/en-us/windows/win32/shell/launch 、https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-iexplorercommand |

### 3.2 社区已验证的做法（证明"读"完全可行，"写"要碰运气）

- Wox 启动器的媒体插件在 Windows 端就是用 `GlobalSystemMediaTransportControlsSessionManager` 读取各播放器（含 Chrome/Edge/Spotify/VLC）的标题、封面与进度，并能发送 play/pause/next/prev。
  https://blog.gitcode.com/ffcd77ad388f5c56e60145e6aaad2134.html
- C# 最小示例（读会话 + 读 Position + `TryPauseAsync()`）：
  https://hacklines.com/en/editions/321615/click
- **注意其中的关键限制**：这些实现都只做了 play/pause/next/prev——**没有一个实现了"跳到任意秒"**，原因正是 §3.1 的 `PositionChangeRequest` 需要目标 App 配合。

### 3.3 三种外部包装器形态的可行性

**A. AHK v2 监听媒体会话 + 打开文件后 seek**
- 读位置：可用 AHK v2 + GSMTC（需 .NET/WinRT 调用）或干脆由 mpv/VLC 自己的状态文件读取；**seek 注入**：只能用 `SendInput` 模拟按键（媒体键不支持绝对定位；Ctrl+Right 之类的组合键在新版 Media Player 里无文档，且在新版 Media Player 里语义未定义）或 UIA `RangeValuePattern.SetValue()`（需目标 App 暴露可写 Slider）。
- **致命弱点**：焦点抢占（会打断用户输入）、需等待播放器窗口与 XAML 树就绪、元素名随版本变、无法保证绝对精度。
- 工作量：原型 **2–4 人时**，稳定化 **8–20 人时**。可靠性：**低**。

**B. C#/.NET 或 Python 包装器：读位置 → 启动播放器 → 注入跳转**
- 最靠谱的注入通道是 **SMTC `TryChangePlaybackPositionAsync`**（若目标 App 支持）。
- 次优是 **JSON IPC**（mpv `--input-ipc-server`，Windows 命名管道）——完全确定性的读写 `playback-time`，但只对 mpv 有效。
- 再退一步是"**自己用 MF 播**"（IMFMediaEngine 自己开文件自己 seek，自己维护进度库）——等于自建播放器。
- 工作量：SMTC 版本原型 **3–6 人时**（含权限申请 `globalMediaControl`、会话枚举、竞态处理）；**成败取决于 ZuneMusic 是否实现 `PlaybackPositionChangeRequested`——公开资料里查不到，必须实测**。
- 可靠性：**中～低**（依赖未文档化契约）。

**C. 注册自定义协议 + 改文件关联（shell 新建菜单 / IExplorerCommand 上下文菜单）**
- 这条路线本身是**稳的**：注册项是注册表 + COM 壳扩展，Windows 官方路径稳定，`IExplorerCommand` 是 Win11 新上下文菜单的推荐扩展点。
- 但它的作用只是"多一个右键项"，**不解决"怎么精确跳到 N 秒"**——最终仍要回到 A/B 的注入问题。
- 真正有价值的组合是：**右键项 → 调用 mpv 并带 `--start=<秒>`**（位置由包装器自己的 sqlite/json 进度库提供），这样"右键/双击打开视频就续播"完全确定性。
- 工作量：注册协议+关联 **2–4 人时**；加 `IExplorerCommand` 壳扩展（含注册、调试、Win11 新旧菜单兼容）**再 4–8 人时**；带进度库与 `--start` 注入 **再 2–4 人时**。
- 可靠性：菜单项 **高**；进度库 **高**；但它**本质上就是换播放器路线**。

---

## 4. 总对比表

| # | 路线 | 机制 / 确切做法 | 难度 | 工作量（人时） | 抗更新可靠性 | 用户可感知效果 |
|---|---|---|---|---|---|---|
| 1 | **Windows 11 内置 Media Player（原样）** | 无开关、无 CLI；仅"最近播放"条目可能续播 **[未证实·需实测]** | — | 0 | **低**（Store 更新即可能改行为；无文档契约） | 大概率"双击从头开始"，不达标 |
| 2 | **内置 Media Player + 外部包装器注入 seek** | SMTC `TryChangePlaybackPositionAsync` 或 UIA/SendInput 注入 | 高 | 3–6（原型）／8–20（稳） | **低**（依赖目标 App 未公开的 `PositionChangeRequest` 实现） | 需先实测目标 App 是否支持；不稳定、可能跳错位置 |
| 3 | **mpv（推荐）** | `mpv.conf` 写 `save-position-on-quit=yes`（`--resume-playback` 默认 yes）；Shift+Q 或退出即存；位置在 `%LOCALAPPDATA%\mpv\watch_later` | 低 | **0.5–2** | **高**（选项名多年稳定；配置是纯文本） | 每次打开自动从断点续播，静默无提示（可加 OSD） |
| 4 | **VLC** | Preferences > Interface > Continue playback? = **Always**（并启用 Save recently played items） | 低 | 0.5–1 | 高 | 同上；默认是 Ask（弹提示条），需手动改 |
| 5 | **MPC-HC / MPC-BE** | Options > Player > History（记住播放位置）；2.8.0 起有 Show History / Reset position | 低 | 0.5–1 | 高（clsid2 活跃维护） | 同上；轻量、GUI 友好 |
| 6 | **PotPlayer** | F5 > 播放 > 记忆播放位置（Remember video playback positions） | 低 | 0.5 | 高 | 同上；默认关闭，需手动开 |
| 7 | **foobar2000** | Preferences > Playback > "Save playback state … resume on next startup"（仅会话级）；按文件需第三方 `foo_resume` | 低 | 0.5–1（+装组件） | 中（依赖第三方组件） | 视频能力弱，不推荐 |
| 8 | **AHK v2 包装器（监听 SMTC + 注入）** | GSMTC 读位置 → 启动播放器 → `SendInput`/UIA RangeValue 注入 | 中～高 | 2–4（原型） | **低** | 需抢焦点，可能误跳；用户有明显感知延迟 |
| 9 | **C#/.NET 包装器（SMTC 写位置）** | `GlobalSystemMediaTransportControlsSessionManager` → `TryChangePlaybackPositionAsync(ticks)` | 高 | 3–6 | **中～低** | 若目标 App 支持则无感；否则完全无效 |
| 10 | **自定义协议 + IExplorerCommand 右键项** | 注册 `HKCR\<scheme>` + `IExplorerCommand` 壳扩展 → 调 mpv `--start=N` | 中～高 | 8–16（全套） | 菜单 **高** / 注入 **低** | 右键多一个"续播打开"，双击仍需靠 mpv 关联 |
| 11 | **自建播放器壳（IMFMediaEngine + 自有进度库）** | 自己播、自己 seek、自己存进度 | 高 | 8–16 | 高（自己的代码） | 完全可控，但等于换播放器 + 写播放器 |

---

## 5. 关键判断：能否从外部主动控制 UWP 媒体播放器跳到指定秒数？

**结论：能"试"，但没有任何一条是可靠契约；要做产品级功能必须假设它随时失效。**

按可行性/可靠性排序：

1. **SMTC `TryChangePlaybackPositionAsync`**（最正规）
   - 机制：外部进程作为 SMTC 消费者，向目标 App 的会话发"改播放位置"请求；App 侧在 `SystemMediaTransportControls.PlaybackPositionChangeRequested` 里执行 seek。
   - 可靠性：取决于 **目标 App 是否实现该事件**。Media Player（ZuneMusic）使用 `MediaPlayer` 类会自动集成 SMTC，但**是否处理位置变更请求，公开资料完全查不到** → **[未证实·需实测]**。
   - 脆弱点：App 更新可能不实现/改语义；需要 `globalMediaControl` 能力声明；App 未播放时无会话；毫秒→ticks（100ns）换算易错。
   - 实测方法：写一个最小 C#/Python 程序，用 GSMTC 列出会话，对当前会话调 `TryChangePlaybackPositionAsync(600 * 10_000_000)`，观察 Media Player 是否跳到 10:00。**这一步能在 1 小时内决定整条路线生死。**

2. **UIA（AutomationId / RangeValuePattern）**
   - 机制：找到进度条 Slider，SetValue 或用 InvokePattern 点"跳转到 X"。
   - 可靠性：**低**。XAML 控件常无稳定 AutomationId；WinUI3 迁移会改元素树；控件不暴露 RangeValue 模式时直接不可用；多显示器/全屏/触摸模式会改变控件树。
   - 配套工具：AHK v2 的 UIA-v2 + `UIATreeInspector.ahk`（https://github.com/Descolada/UIA-v2）、微软的 Inspect.exe / Accessibility Insights。

3. **键盘注入（SendInput 模拟 Ctrl+Right 等）**
   - 机制：向前台窗口发按键。
   - 可靠性：**低**。Windows 11 新版 Media Player **没有任何官方键盘快捷键文档**，"Ctrl+Right 跳 60 秒"是**不存在**的机制（mpv 里 Ctrl+Right 是切换字幕，60 秒是 ↑/↓；新版 Media Player 官方未公布快捷键表）。媒体键只有 play/pause/next/prev/快进，**没有绝对 seek**。
   - 额外问题：抢焦点、依赖前台窗口、无法精确到秒（只能"按 N 次 5 秒/10 秒"逼近）。

4. **Media Foundation / IMFMediaEngine**
   - 只能控制**自己创建的播放会话**，对第三方 App 无效（https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nn-mfmediaengine-imfmediaengine）。若用它实现需求，等于自建播放器（路线 11）。

5. **读取位置 → 重新启动播放器带位置参数**
   - 这是"外部驱动"里唯一**确定性**的形态：位置由外部自己维护，启动时用 mpv `--start=N` / VLC `--start-time=N` 传进去。
   - **但这本质上就是换播放器路线**——把"续播"这件事从 UWP App 手里拿走。

---

## 6. 明确推荐

> **推荐：换 mpv 做默认播放器，`%APPDATA%\mpv\mpv.conf` 写 `save-position-on-quit=yes`（必要时加 `resume-playback-check-mtime=yes`），并把 `.mp4/.mkv/.avi/...` 文件关联指向 `mpv.exe`。** 这是唯一"零开发、配置级、跨版本稳定、且完全可编程"的方案，0.5–2 人时即可交付，用户体验为"每次打开自动续播"。
> 若不想换命令行风格播放器：**VLC（Continue playback? = Always + Save recently played items）** 或 **MPC-HC（Options > Player > History）** 是等价次选，同样 1 小时内可交付。

**若坚持用系统自带媒体播放器（Microsoft.ZuneMusic_8wekyb3d8bbwe），最现实的做法按优先级：**

1. **先实测**（§1.3 的 A/B/C 三步）。若确认"从 Home 最近播放点进去会续播"，就把使用习惯固定为**只从 Media Player 的 Home/最近播放进入**，并把资源管理器的双击行为改道到 mpv（此时是"混用"，但用户拿到的是"系统播放器 + 断点续播"的体感）。
2. **接受"半自动"**：Media Player 负责播与看，"续播"交给外部记录——即用 §3.3-C 的方案做右键项"用媒体播放器续播打开"，但内部转交 mpv 并带 `--start=N`。**坦白讲：这条路线的价值只在于保留"系统媒体播放器"的入口语义，实际播放已不是系统播放器。**
3. **不建议**投入"外部主动控制 ZuneMusic seek"（SMTC 写位置 / UIA / 键盘注入）：唯一有希望的是 SMTC `TryChangePlaybackPositionAsync`，但需先做 1 小时的可行性验证；即便成功，也会因无公开契约而长期脆弱——**这属于"研究项目"，不属于"需求交付"**。

---

## 7. 证据 URL 清单

**mpv（官方手册，最权威）**
- https://mpv.io/manual/stable/ —— RESUMING PLAYBACK / Watch Later / FILES ON WINDOWS / `--start=` / `--input-ipc-server`

**VLC（官方帮助站 + 官方论坛）**
- https://www.vlchelp.com?p=1285/
- https://forum.videolan.org/viewtopic.php?p=462350
- https://www.ghacks.net/2014/06/05/remember-position-using-vlc-media-player/
- https://lawsoung1947.blogspot.com/2022/10/vlc-do-you-want-to-continue-where-you-left-off.html

**MPC-HC / MPC-BE**
- https://github.com/clsid2/mpc-hc/releases/tag/2.8.0
- https://github.com/clsid2/mpc-hc
- https://www.9k9k.com/pcsoft/395.html

**PotPlayer / foobar2000**
- https://iwantaholodeck.com/enabling-full-screen-mode-and-resume-for-potplayer/
- https://potplayer.multimediaplayer.net/tutorials/potplayer-remember-playback-positions
- https://wiki.hydrogenaud.io/index.php?title=Foobar2000:Preferences:Playback
- https://www.webkkk.net/reda777/foo_resume

**Windows 11 内置媒体播放器**
- https://www.microsoft.com/en-us/p/muzica-groove/9wzdncrfj3pt （Store 官方产品页/功能清单/体积）
- https://learn.microsoft.com/en-us/windows-insider/release-notes/apps/media-player （官方发行说明）
- https://geekrewind.com/manage-recent-media-settings-in-windows-11-media-player/
- https://techbloat.com/windows-11s-new-media-player-brings-big-improvements-to-audio-and-video.html
- https://rottenwifi.com/how-to-use-the-media-player-app-in-windows-11/
- https://support.microsoft.com/zh-cn/help/17948/ （旧版 WMP 降级为可选功能）
- https://support.microsoft.com/nb-no/windows/experience/platform-variants/media-feature-pack-for-windows-n （N 版 Media Feature Pack）

**SMTC / UIA / MF / Shell**
- https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager?view=winrt-26100
- https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession?view=winrt-26100
- https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessiontimelineproperties?view=winrt-26100
- https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/system-media-transport-controls
- https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-uiautomationoverview
- https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nn-mfmediaengine-imfmediaengine
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-iexplorercommand
- https://learn.microsoft.com/en-us/windows/win32/shell/launch
- https://support.microsoft.com/en-us/topic/kb5072911-explorer-the-start-menu-and-other-xaml-dependent-apps-might-not-start-or-close-unexpectedly-on-some-enterprise-devices-d2d30684-4e2b-47f5-9899-a00a8e0acb09

**社区实现证据（读位置/控制其他播放器）**
- https://github.com/Descolada/UIA-v2
- https://blog.gitcode.com/ffcd77ad388f5c56e60145e6aaad2134.html （Wox 媒体插件 / Windows 端 GSMTC）
- https://hacklines.com/en/editions/321615/click （C# GSMTC 读写示例）

---

### 附：本次调研未能证实 / 需实测的点（请勿当作结论）

1. Media Player 从 Home「最近播放」点进视频是否一定续播（A 路径）。
2. Media Player 双击同一文件是否续播（B 路径）——社区反馈普遍为"从头开始"。
3. Media Player（ZuneMusic）是否实现 SMTC `PlaybackPositionChangeRequested`（决定路线 2/9 生死）。
4. Windows 11 24H2 是否收紧了 UI Automation——**未找到官方证据**，不要在方案里写这条假设。
5. VLC 命令行 `--start-time` 的官方文档页（本次未抓到，建议以 `vlc --longhelp` 为准）。
6. mpv 便携版 exe 的精确体积（以实际构建为准）。
