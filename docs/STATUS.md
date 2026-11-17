# ResumePoint —— 项目现状文档

> **最后更新**：2026-11-17 22:59
> **面向**：接手本项目的后续会话 / 本地 Agent
> **读完这一份就能接上**——不需要重新调研，也不需要重踩下面记录的坑。

---

## 0. 三十秒速览

| 项 | 值 |
|---|---|
| 是什么 | Windows 11 新版媒体播放器的「断点续播」伴生工具 |
| 不是什么 | **不是插件**（微软官方明确不支持，无加载项宿主） |
| 仓库 | https://github.com/COH2357/ResumePoint |
| Release | v0.1.0（安装包 6.68 MB，按用户安装，免管理员） |
| 技术栈 | .NET 8 + WinForms(托盘) + Microsoft.Data.Sqlite + WinRT 投影 |
| 核心机制 | 通过官方 **SMTC** 接口读写播放器的播放位置 |
| 状态 | v0.1 可用，端到端实测通过；**双击续播的入口还需手动设一次默认关联** |
| 硬约束 | **不能做成 Windows 服务**（GSMTC 必须跑在交互式桌面会话） |

---

## 1. 项目定位

原始需求：「写一个 win11 新版媒体播放器的插件，让每一次打开视频都从上次断点打开，先验证可行性」。

可行性已验证完毕，结论是**插件路线关闭、伴生工具路线成立**：

- 微软社区支持专员（署名）明确答复：*"Media Player does not have the ability to add plug-ins directly, due to product design."*
  <https://learn.microsoft.com/en-us/answers/questions/3937430/windows-media-player>
- 但功能本身不需要插件：播放器**已经落盘**了播放位置，Windows 也提供了官方外部 API 可以读写它
- 于是产品形态 = 一个**独立于播放器**的托盘程序，通过 SMTC 与播放器对话

```
┌─ 媒体播放器（微软的，不改）───────────────┐
│   播放视频 → SMTC 会话暴露当前位置          │
└───────────────┬──────────────────────────┘
                │  官方 API，双向
   ┌────────────┴────────────┐
   │   ResumePoint（本项目）    │
   ├─────────────────────────┤
   │ ①记录端：轮询当前位置      │
   │   → 写进自己的 per-file 表  │
   │                         │
   │ ②播放端：打开文件         │
   │   → 等 SMTC 会话就绪       │
   │   → seek 回断点 → 可选暂停  │
   └─────────────────────────┘
```

---

## 2. 已实测确认的事实

> 以下全部是**本机实测**结论，不是文档推测。**不要重新推导，直接用。**

### 2.1 目标应用

```
PackageFamilyName : Microsoft.ZuneMusic_8wekyb3d8bbwe
AUMID             : Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic
Store ProductID   : 9WZDNCRFJ3PT
实测版本          : 11.2609.11.0（Win11 26300）
```

### 2.2 播放位置存在哪（关键）

```
%LOCALAPPDATA%\Packages\Microsoft.ZuneMusic_8wekyb3d8bbwe\LocalState\AppState.json
```

**真实结构是 `ComponentStates` 包裹**（早期版本的探针按根节点读，恒为 null）：

```json
{ "ComponentStates": {
    "NavigationService": {...},
    "PlayQueueModel": { "Items": [ { "Source": "D:\\a.mp4", "Kind": 1 } ], "CurrentItemIndex": 0 },
    "CurrentItemStateProvider": {...},
    "PlayerModel": { "IsRepeatOneEnabled": false, "PlaybackRate": 1.0, "CurrentPosition": "08:03:47.6454568" },
    "AppChromeViewModel": {...} } }
```

| 事实 | 状态 |
|---|---|
| `ComponentStates.PlayerModel.CurrentPosition` | .NET TimeSpan 字符串，断点位置 |
| `ComponentStates.PlayQueueModel.Items[CurrentItemIndex].Source` | 当前队列项的完整路径 |
| **只存一条断点** | 跟着当前队列项走，播别的就覆盖 → **不是 per-file 表** |
| **落盘是周期性/惰性的** | 实测：播放 2 分钟 + 2 次 seek + 正常退出，`AppState.json` **一个字节都没重写** |
| **队列项会滞后** | 实测：播放器换成别的文件后，队列可能还指着上一个文件 |
| `MediaPlayer.db` | **无位置列**（已用 .NET SQLite 全库扫描确认）。只有 `RecentlyPlayed.PlayedTime`（播放时刻）和 `Album/Track/Video.Duration`（总时长） |
| `Kind` | 实测 .mp4 = 1；研究样本 .MTS = 2 → 疑为「音频=1 / 视频=2」，未最终确认 |

### 2.3 SMTC（生死判定的结论）

| 检查项 | 实测结果 |
|---|---|
| Media Player 是否注册 SMTC 会话 | ✅ 是 |
| `IsPlaybackPositionEnabled` | ✅ **True** |
| `TryChangePlaybackPositionAsync(600s)` | ✅ 返回 **True** |
| 位置是否**真实跳转** | ✅ `0:29 → 10:22`（= 目标 + 播放漂移），并能精确复原到 `08:03:47.6454568` |
| `MinSeekTime` / `MaxSeekTime` | `00:00:00` / 完整时长 → 任意位置可 seek |
| Title 格式 | **去掉扩展名的文件名**（实测 `let me go pv`、`诀别书超长纯`） |
| `GetVideoPropertiesAsync` 取时长 | ✅ 精确（实测 127.15s 与播放器 EndTime 一致） |

### 2.4 原生续播行为（实测否定）

| 路径 | 实际起播位置 |
|---|---|
| 文件激活打开（≡ 资源管理器双击） | **00:00:00** |
| 完全退出播放器后重开 | **00:00:00** |
| 同期 AppState.json 里存的值 | `08:03:47.6454568` |

→ **原生存了位置却不读**。伴生工具是**必需品**，不是可选项。

> Home → 最近播放 → 点入 这条路径**未测**（需点 UI，SMTC 观测不到）。它不改变工程结论——伴生工具覆盖全部入口。

### 2.5 不存在的接口（别再试）

- ❌ `ms-media-player:` 协议（不存在）
- ❌ `shell:AppsFolder\...` 带命令行参数（参数被忽略）
- ❌ 直接跑 `Microsoft.Media.Player.exe` 带参数（UWP 不接受）
- ❌ 媒体键做绝对 seek（只有 play/pause/next/prev）
- ❌ UIA 拖进度条（零一手证据，XAML 控件树随版本重写即废）
- ❌ 修改 `MediaPlayer.db`（没有位置列）
- ❌ 把微软播放器注册成自定义协议处理程序（必须它自己的 manifest 声明，第三方改不了）

### 2.6 Windows 侧约束

- `UserChoice`（默认打开方式）**有哈希保护，程序无法静默修改** → 必须用户手动在 设置→应用→默认应用 指定一次
- **exe 路径一变**（重装/移动/改名），已有 UserChoice 失效，Windows 会重置回别的程序 → 这是设计使然，不是 bug
- GSMTC **只能在交互式桌面会话**用 → 自启必须是「登录时启动」，**绝不能**注册成 Windows 服务
- 本机 `HiberbootEnabled=1`（快速启动开启）→ 关机是混合关机，**重启**才是完整冷启动
- `.mp4` 的默认关联当前是**夸克网盘**（改名后 Windows 重置了），需重新指一次

---

## 3. 当前架构

### 3.1 进程与角色

```
┌─ 托盘常驻进程（ResumePoint.exe，无参数启动）──────────────┐
│  TrayHost      托盘图标 + 菜单 + 状态显示                    │
│  Recorder      后台轮询（默认 2s）→ 识别当前文件 → 写库      │
│  Smtc          SMTC 封装（manager 固定在 UI 线程创建）        │
│  ResumeDb      SQLite（WAL + synchronous=NORMAL）            │
└───────────────────────┬───────────────────────────────────┘
                        │ 跨进程交接（last-opened.json）
┌───────────────────────┴───────────────────────────────────┐
│  短命进程（ResumePoint.exe --open "<路径>"）                 │
│  由 shell 在双击时拉起 → 打开播放器 → 等会话就绪 → seek      │
│  → 写交接文件 → 退出                                       │
└───────────────────────────────────────────────────────────┘
```

### 3.2 数据流

| 方向 | 机制 | 落点 |
|---|---|---|
| 记录 | SMTC `GetTimelineProperties().Position` | `resumes.db` 的 `media` 表 |
| 识别文件 | ① 跨进程交接（`last-opened.json`）② SMTC Title 匹配 AppState 队列 ③ 队列路径匹配 ④ 队列当前项（低置信度，拒写） | — |
| 读断点 | 按路径查表，路径没命中则按**指纹**（size + 首尾各 64KB 的 SHA-256）兜底 | — |
| 续播 | `Launcher.LaunchFileAsync` → 等会话 `status != Changing && EndTime > 0` → 标题/时长双保险确认 → `TryChangePlaybackPositionAsync` | — |

### 3.3 数据库结构

```sql
media(id, path UNIQUE, display_path, title, size, mtime, fingerprint,
      duration, position, updated_at, last_played_at,
      play_count, total_watched, is_complete)
```

- **per-file**（不是原生那种"只记最后一个"）
- `is_complete` 每次写入按当前位置**重新计算**（不粘滞——用户看完后从头重看时必须清掉）
- `total_watched` 按**墙钟时间限幅**累加（快进/跳段不会被算成观看）

---

## 4. 已知问题

> 编号便于后续引用。P0 = 影响核心体验或正确性。

### P0-1　双击到续播要等 1.6–3.8 秒

每次双击都启动一个**独立的 .NET 进程**，冷启动要重新做一遍 `RequestAsync()`（建立与系统的 SMTC 连接）。
实测 3 次：3.82s / 1.69s / 1.61s。**这是当前最明显的体验短板。**

### P0-2　改「记录写入位置」后数据库不跟着走

`Program.cs:58` 在启动时 `new ResumeDb(settings.DatabasePath)` 打开库；`MainForm.SaveSettings()` 只改了设置对象。
→ **改完设置后，数据仍写在旧目录**，下次启动才切过去，而旧目录里的库**不会被搬过去** → 用户以为数据丢了。
（其余设置项——轮询间隔、落盘间隔、续播开关、跳完是否播放、最小续播秒数、距片尾多少秒算看完——都是**每次读取、立即生效**的。）

### P0-3　日志路径不跟随自定义数据目录

`AppLog.LogPath` 写死用 `Settings.DefaultDataDirectory()`，而不是配置里的 `DataDirectory`。
→ 设置页写"数据库与日志存放处"，但改了之后日志还在老地方。

### P1-4　默认文件关联需手动设置，且易被重置

见 §2.6。exe 路径一变就断。目前没有"一键重新注册并引导"的流程。

### P1-5　没有右键菜单扩展（IExplorerCommand）

计划中未做。注意：unpackaged 的 COM 扩展**只会出现在传统右键菜单**（需 Shift+F10 / "显示更多选项"）；
要进 Win11 现代右键菜单必须 MSIX + 签名。

### P1-6　不经本工具入口播放时，识别延迟不可控

用户直接在 Media Player 里点「Home → 最近播放」时没有交接信息，只能等 `AppState.json` 队列慢慢更新（实测滞后不可控，可能几十秒到几分钟）。这期间**不记录**（宁可漏记也不记错文件）。

### P1-7　未装 .NET 8 Desktop Runtime 的机器跑不了

当前是 framework-dependent 发布（安装包 6.68 MB）。要分发到别的机器需 self-contained（约 +70 MB）。

### P1-8　EXE 未签名 → SmartScreen 警告

### P2-9　没有自动化测试

`probe/*.ps1` 是**手工验证脚本**，没有断言、不能进 CI。目前所有正确性都靠人工跑。

### P2-10　没有 CI

`.github/workflows/` 不存在。Windows 11 + Media Player 的实机验证也没法自动化。

### P2-11　没有 CONTRIBUTING / SECURITY.md

`LICENSE` 已由用户补上（**MIT**，Copyright (c) 2026 Zhsj）。还缺 `CONTRIBUTING.md` 与 `SECURITY.md`。

### P2-12　卸载后不清理数据目录

断点库会留在 `%LOCALAPPDATA%\ResumePoint\`。

### P2-13　改名前的旧目录残留

`%LOCALAPPDATA%\MediaResume`（36.4 KB）+ `%APPDATA%\MediaResume`（0.3 KB）——迁移完成后没清理。
（顺带确认：删除工具在本机对工作区外路径会拒绝，`rm --` 必须作为**单独一条命令**执行。）

### P2-14　`--diag` / `--list` 输出到文件而非 stdout

WinExe 没有控制台，所以结果写进 `diag.txt` / `records.txt`。命令行用户拿不到 stdout，体验别扭。

### P2-15　`LaunchHandoff.FilePath` 每次读都 `Settings.Load()`

记录端每 2 秒读一次 `settings.json`。功能无碍，但是无谓的磁盘 IO。

### P2-16　`total_watched` 首条记录从 0 起算

第一次观看的时长不计入累计（首条无法算增量）。

### P2-17　指纹匹配的边界：同内容副本会互相覆盖

内容完全相同的两个文件（备份副本）指纹相同 → 记录会串。

### P2-18　路径规范化较浅

只做了 `GetFullPath` + 分隔符统一。8.3 短名、UNC 长路径（`\\?\`）、盘符映射变化（本地盘 ↔ 网络盘）都可能导致同一文件识别不出。

---

## 5. 优化方向

按投入产出排序。

### 5.1 用命名管道把「续播」交给托盘常驻进程（解 P0-1）

托盘进程**已经**持有热的 SMTC manager。把 `--open` 改成"只发一条 IPC 请求，托盘进程去做打开 + seek"：

- 双击到续播从 1.6–3.8s 降到 **~100ms 级**
- 顺带省掉每次一个 .NET 进程的启动开销
- 代价：需要定义 IPC 协议、处理托盘进程未运行时的降级（回退到当前的独立进程路径）

建议协议：请求 = `{ "path": "...", "mode": "resume|fromStart" }`，响应 = `{ "ok": true, "message": "..." }`。
用 `NamedPipeServerStream`（名字如 `ResumePoint.1`）+ JSON，Windows 命名管道天然支持 ACL。

### 5.2 修 P0-2 / P0-3（数据目录与日志路径）

- 改数据目录时：关闭当前库 → 迁移/复制 db 到新位置 → 重开库 → 更新设置
- 或者更简单诚实的做法：**改设置后提示"重启后生效"**，并提供"立即迁移"按钮
- 日志路径改用 `Settings.Load().DataDirectory`（注意别在 LogPath 里递归调 Load）

### 5.3 补自动化测试（P2-9）

把 `probe/` 里的手工脚本改造成可断言、可 CI 运行的版本：

- **纯逻辑单测**（不需要 Windows 播放器）：`PathMatcher`、`PathResolver`、`FileFingerprint`、`ResumeDb`、设置迁移
- **需要真实环境的集成测试**：标记为 `[Trait("Category","Integration")]`，CI 上跳过、在本机手动跑
- 建议新建 `tests/ResumePoint.Tests`（xUnit）

### 5.4 右键入口（P1-5）

- 短期：注册进"打开方式"列表 + 一键跳转到默认应用设置（已有 `FileAssociation` 类可扩展）
- 中期：unpackaged COM `IExplorerCommand`（传统右键菜单）
- 长期：MSIX 打包（有签名成本，且沙箱会影响自定义数据目录）

### 5.5 减少对 `AppState.json` 的依赖（P1-6）

已发现 `MediaPlayer.db` 里有 `Video` 表（`Uri` / `Title` / `Duration`）——播放器的**媒体库**知道有哪些视频。
虽然它没有位置，但可以作为**第三条路径识别线索**（按 Title + Duration 匹配库里的 Uri），补上 P1-6 的缺口。

### 5.6 健壮性

- 记录端在**识别不到文件**时，可以尝试用 SMTC 的 Title + 时长在已知库/队列里做模糊匹配（降级为中置信度，标注"推测"）
- `total_watched` 首条：用 SMTC 的 EndTime 与首次观察位置估算，或干脆在 UI 上说明"累计观看从第二次起算"
- 指纹：加入"最近修改时间 + 精确大小"作为辅助，降低 P2-17 的串档风险

### 5.7 分发

- self-contained 发布（免 .NET 运行时依赖）
- 代码签名（消除 SmartScreen 警告）
- ~~加 `LICENSE`~~ —— 已完成（MIT）
- 补 `CONTRIBUTING.md` / `SECURITY.md`
- GitHub Actions：至少跑 build + 单测；集成测试需要一台装了 Media Player 的 Windows 机器（可用自托管 runner）

---

## 6. 未证实的缺口

> **别当结论用。**

1. **Media Player 更新后 SMTC 行为是否变化** —— 若变化，外部 seek 会失效，而**记录库不受影响**（可随时切 mpv 兜底：`save-position-on-quit=yes`）。目前**没有自动降级路径**。
2. **Home → 最近播放 → 点入** 是否续播（需点 UI，未测）。
3. **22H2 ~ 25H2 的行为差异** —— 查不到一手实测。
4. **快速启动（HiberbootEnabled=1）下**关机/重启后 SMTC 会话与 AppState.json 的状态 —— 未验证。
5. **UIA 里进度条是否以 Slider/RangeValue 暴露** —— 依然零证据，**不建议依赖**。
6. **`Kind` 字段语义**（1=音频? 2=视频?）—— 未最终确认。

---

## 7. 关键文件索引

```
src/ResumePoint/
├── Program.cs              入口：单实例互斥 / --diag / --list / --open / --open-start
├── app.manifest            asInvoker（DPI 由 csproj 的 ApplicationHighDpiMode 管）
├── ResumePoint.csproj      TFM net8.0-windows10.0.19041.0（WinRT 投影靠这个）
├── ResumePoint.ico/.png    应用图标（由 tools\Generate-Icon.ps1 生成）
├── Core/
│   ├── Settings.cs         设置持久化 + 旧目录迁移 + 旧数据接手
│   ├── ResumeDb.cs         SQLite 断点表（per-file + 指纹兜底 + WAL）
│   ├── FileFingerprint.cs  size + 首尾 64KB 的 SHA-256
│   ├── Smtc.cs             SMTC 封装（manager 固定在 UI 线程创建，见 P0-1 相关注释）
│   ├── PlayerState.cs      读 AppState.json（容错 ComponentStates 包裹）
│   ├── PathResolver.cs     标题↔文件名匹配 + 路径猜测置信度
│   ├── Recorder.cs         记录端：边播边写，识别不可信就不写
│   ├── PlayerLauncher.cs   播放端：LaunchFileAsync → 等就绪 → seek → 可选暂停
│   ├── LaunchHandoff.cs    跨进程交接（last-opened.json）
│   ├── FileAssociation.cs  注册进系统"打开方式"列表
│   ├── AutoStart.cs        开机自启（启动文件夹快捷方式，非服务）
│   └── AppLog.cs           文件日志（2MB 轮转）
└── UI/
    ├── TrayHost.cs         托盘常驻 + 菜单 + 状态（注意 _dispatch.Handle 那行）
    ├── MainForm.cs         主面板：记录表 / 设置页
    └── IconFactory.cs      托盘图标三级降级：ico 文件 → exe 资源 → 运行时绘制

installer/ResumePoint.iss  Inno Setup 6 安装脚本（注意其中三个"坑"的注释）
tools/Generate-Icon.ps1    图标生成脚本（可复现）
probe/                     研究期 PowerShell 实测探针 + 手工验证脚本

docs/
├── STATUS.md              ← 本文件
└── research/              原始可行性调研（从 zip 里解出来的，此前只有 zip）
    ├── feasibility-report.md    可行性主报告：结论 + 证据链 + 8 条路线对比
    ├── options-deep-dive.md     8 条路线逐条展开（含全部 URL）
    ├── HANDOFF.md               调研期交接文档
    ├── MEMORY.md                调研期结论摘要
    ├── win11-mediaplayer-resume.md  已沉淀的技术结论
    └── overview.md              调研包原始 README

win11-mediaplayer-resume-export.zip  原始调研包（保留原件，docs/research 是它的解出版）
```

---

## 8. 常用命令

```powershell
# 开发
dotnet build src\ResumePoint
dotnet publish src\ResumePoint -c Release -r win-x64 --self-contained false -o publish

# 运行
publish\ResumePoint.exe                    # 托盘常驻
publish\ResumePoint.exe --open "D:\a.mkv"  # 打开并续播（等同双击）
publish\ResumePoint.exe --open-start "D:\a.mkv"
publish\ResumePoint.exe --list             # 列出断点记录
publish\ResumePoint.exe --diag             # 环境 / SMTC / 播放器落盘状态

# 安装包
iscc installer\ResumePoint.iss             # 产物在 installer\output\

# 图标
pwsh tools\Generate-Icon.ps1
```

**数据位置**：`%LOCALAPPDATA%\ResumePoint\resumes.db`（可改）、`%APPDATA%\ResumePoint\settings.json`（固定）。
**日志**：`%LOCALAPPDATA%\ResumePoint\logs\resumepoint.log`。

---

## 9. 踩过的坑（不要重蹈）

全部是本项目实测踩中并已修复的，写在这里是为了让后续会话**别再走一遍**。

### 9.1 SMTC 的三个坑（最难）

1. **manager 必须在有消息泵的线程上创建。** 会话列表靠 `SessionsChanged` 事件异步填充；manager 若在"会话还不存在"的时刻创建、所在线程又没有消息循环，`GetSessions()` 会**永远返回空**。
2. **"加个 invalidate 让它自愈"会把修复拆掉。** 在后台线程重建 manager → 又没有消息泵 → 回到坑 1。正确做法是**把创建动作调度回 UI 线程**，而不是在调用线程重建。
3. **会话"就绪"有判据。** 刚建立时是 `status=Changing / title='' / end=0`（播放器还在加载）。此时匹配和 seek 都会失败（seek 返回 `False`）。必须等 `status != Changing && EndTime > 0`。

### 9.2 `AppState.json` 的两个坑

4. **结构是 `ComponentStates` 包裹**，不是根节点。按根节点读 → 恒为 null → 误报"播放器没存断点"。
5. **队列项会滞后**，不能当权威来源。必须用 SMTC 的 Title 交叉验证。

### 9.3 跨进程盲区

6. **`--open` 是独立进程，托盘是另一个进程**，两者互不相识。记录端因此认不出"刚打开的文件" → 需要 `last-opened.json` 交接。

### 9.4 UI 线程陷阱

7. **`Control.BeginInvoke` 在没有窗口句柄时直接抛异常**，且极易被 `catch { }` 吞掉 → 托盘状态永远停在初始值，而**记录端其实一直在正常写库**。症状极具误导性。

### 9.5 编码 / 脚本

8. **Windows PowerShell 5.1 按 ANSI 读 UTF-8 无 BOM 的 .ps1** → 中文变乱码 → 解析器直接报错。必须存成 **UTF-8 with BOM**。
9. **PowerShell 里 `New-Object` 不接受多参数换行** → 用 `::new()` 构造器语法。
10. **PowerShell 里 `@()` 传给 `AddPolygon` 会被当成 `Object[]`** → 显式声明 `[System.Drawing.PointF[]]`。
11. **PS7 不支持 `ContentType = WindowsRuntime` 类型解析** → 要用 WinRT 就得调 `powershell.exe`（PS 5.1）。
12. **PS7 里用 `<` 做输入重定向是解析错误** → 整段脚本都不会执行。用 `Process` + `StandardInput.WriteLine()`。
13. **批量替换是大小写敏感的** → `logs\mediaresume.log`（小写）漏改。替换后要**大小写不敏感**复查一遍。

### 9.6 ICO 组装

14. **`BITMAPINFOHEADER` 字段偏移写错 → GDI+ 报"操作成功完成"**（完全误导的错误）。正确：0 biSize / 4 biWidth / 8 biHeight / 12 biPlanes / **14 biBitCount** / 16 biCompression / 20 biSizeImage。
15. **ICO 的 BMP 是自下而上的**，要翻转行序。
16. **`LinearGradientBrush` 的渐变线长度 = |w·sinθ|+|h·cosθ|**，宽扁矩形会溢出边界 → 高光出现硬接缝。让渐变覆盖整个矩形即可。

### 9.7 Inno Setup

17. **[Tasks] 的 Flags 里没有 `checked`** —— 任务默认就是勾选，要默认不勾选才写 `unchecked`。
18. **[CustomMessages] 的值里不能用 ASCII 双引号** —— `{cm:}` 展开后会破坏 `Description` 的引号配对，报错还指向后面的 `Flags`（极具误导性）。
19. **[Files] 的相对路径以 .iss 所在目录为基准** —— `installer\` 的上一级就是项目根。

### 9.8 环境

20. **`rm --` 在本机必须作为单独一条命令执行**，且路径要在工作区内；工作区外的路径会被 trash 启动器拒绝。
21. **`Get-AppxPackage` 拿到的 `InstallLocation` 在 `C:\Program Files\WindowsApps`**，直接访问可能被 ACL 拦。
22. **本机桌面被重定向到 `E:\coh23\Desktop`**，不是 `%USERPROFILE%\Desktop`。
23. **GitHub 凭据在 GCM 里**（`git:https://github.com`），`gh` 未安装。用 `git credential fill` 取 token 调 API 即可（注意坑 12）。

---

## 10. 环境现状

| 项 | 值 |
|---|---|
| OS | Windows 11 专业版 10.0.26300（快速启动 **开启**） |
| .NET SDK | 8.0.428（`C:\Program Files\dotnet\dotnet.exe`，注意不在 PATH 里） |
| Inno Setup | 6.7.3（`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`） |
| Media Player | 11.2609.11.0 |
| 安装位置 | `%LOCALAPPDATA%\Programs\ResumePoint`（按用户安装，免管理员） |
| git | 2.56.0，remote = `https://github.com/COH2357/ResumePoint.git` |
| gh CLI | 未安装（用 GCM 里的 token 调 API） |

**当前遗留**：`.mp4` 默认关联是夸克网盘，需在 设置→应用→默认应用→ResumePoint 断点续播 里重新指定一次（P1-4）。
