# ResumePoint

> **Pick up exactly where you left off.**
>
> A small companion app for Windows 11 Media Player. It quietly remembers where you
> stopped each video and jumps straight back to that moment the next time you open it —
> no plugin, no changes to the player itself.

让**每一次打开视频都从上次断点继续**。
不修改媒体播放器本体，通过 Windows 官方 **SMTC** 接口读取 / 跳转播放位置。

---

## 这是什么

微软的新版媒体播放器（`Microsoft.ZuneMusic_8wekyb3d8bbwe`）**不能装插件**（官方明文答复不支持），但它**已经把自己的播放位置落盘**了，同时 Windows 提供了官方的外部接口可以读写它。

本工具就是那个"外部接口"：

```
┌─ 媒体播放器（微软的，不改）───────────────┐
│   播放视频 → SMTC 会话暴露当前位置          │
└───────────────┬──────────────────────────┘
                │  官方 API，双向
   ┌────────────┴────────────┐
   │   ResumePoint（本工具）   │
   ├─────────────────────────┤
   │ ①记录端：轮询当前位置      │
   │   → 写进自己的 per-file 表  │
   │                         │
   │ ②播放端：打开文件         │
   │   → 等 SMTC 会话就绪       │
   │   → seek 回断点           │
   └─────────────────────────┘
```

## 为什么需要它（全部为本机实测结论）

| 问题 | 实测结果 |
|---|---|
| 能写插件吗？ | **不能**。微软明确答复不支持，UWP/MSIX 无加载项宿主 |
| 原生自己会续播吗？ | **不会**。文件激活打开时从 `00:00:00` 开始，尽管 `AppState.json` 里存着 `08:03:47` |
| 原生存的位置可靠吗？ | **不可靠**。实测播放 2 分钟 + 2 次 seek + 正常退出，`AppState.json` **一个字节都没重写**；且它**只存一条**（跟着当前队列项走），不是 per-file |
| 外部能驱动它跳转吗？ | **能**。`TryChangePlaybackPositionAsync` 返回 **True**，且位置**真实跳转**（实测 `0:29 → 10:22`，再精确复原到 `08:03:47.6454568`） |
| 怎么知道"正在播哪个文件"？ | SMTC 的 `Title` 就是**去掉扩展名的文件名**（实测 `let me go pv`）。`AppState.json` 的队列**会滞后**，只能当辅助线索，必须靠标题交叉验证 |

## 实测环境

Windows 11 专业版 10.0.26300 ｜ Media Player 11.2609.11.0 ｜ .NET 8 SDK 8.0.428

## 功能

### 记录端（后台常驻）

- 每 2 秒读一次 SMTC 会话的当前位置
- **边播边写**：位置变化就落盘（默认最多攒 5 秒），断电最多丢 5 秒
- **识别不到文件就不写** —— 宁可漏记，也不能把位置记到错的文件上
- SQLite（`WAL` + `synchronous=NORMAL`）→ **断电不会损坏数据库**

### 播放端

- 打开文件 → 等 SMTC 会话就绪并**确认播放的就是这个文件**（标题匹配 / 时长匹配双保险）→ `TryChangePlaybackPositionAsync` 跳回断点
- 默认**跳完先暂停**（避免"一打开就炸出声音"），可在设置里改成自动播放

### 记录库

| 项 | 说明 |
|---|---|
| per-file | 每个文件一条断点，**不是**原生那种"只记最后一个" |
| 指纹兜底 | `size + 首尾各 64KB 的 SHA-256` → 文件**改名 / 移动**后仍能认出 |
| 额外统计 | 时长、标题、最后播放时间、累计观看时长、播放次数、是否看完 |
| 容量 | 每条约 200 字节 → 1 万个视频 ≈ 2 MB，**实际无上限** |

### 托盘 + 主面板

- 托盘常驻，显示实时状态（● 记录中 / ● 等待识别文件 / ● 空闲）
- 主面板：记录列表（可删除、定位、从头播放、续播打开）+ 设置页
- 关闭主窗口隐藏到托盘；单实例互斥，不会重复写库

### 设置项

- **记录写入位置**可自定义（默认 `%LOCALAPPDATA%\ResumePoint`）
- **开机自动启动**（登录时启动，⚠️ **不能**做成 Windows 服务——SMTC 必须在交互式桌面会话里用）
- 轮询间隔 / 落盘间隔 / 最小续播秒数 / 距片尾多少秒算看完

## 用法

### 日常

双击视频 → 自动续播（前提：视频文件的默认打开方式是走本工具，见下）。

### 命令行

```powershell
# 打开并续播
ResumePoint.exe --open "D:\videos\a.mkv"

# 从头打开（忽略断点）
ResumePoint.exe --open-start "D:\videos\a.mkv"

# 诊断：环境 / SMTC 会话 / 播放器落盘状态
ResumePoint.exe --diag

# 列出全部断点记录
ResumePoint.exe --list
```

### 让"双击"走本工具

Windows 10+ 的 `UserChoice` 有哈希保护，**程序无法静默改默认关联**。安装器会把自己注册进系统的"打开方式"列表，然后：

> **设置 → 应用 → 默认应用** → 选 ResumePoint → 把 `.mp4` / `.mkv` 等指过去

只此一次，之后双击即续播。

## 构建

```powershell
# 开发构建
dotnet build src\ResumePoint

# 发布
dotnet publish src\ResumePoint -c Release -r win-x64 --self-contained false -o publish

# 打安装包（需要 Inno Setup 6）
iscc installer\ResumePoint.iss
```

## 项目结构

```
src/ResumePoint/
├── Program.cs            入口：单实例互斥 / --diag / --list / --open
├── app.manifest          asInvoker（GSMTC 需要交互式桌面会话）
├── Core/
│   ├── Settings.cs        设置持久化（%APPDATA%），数据目录可自定义 + 旧目录迁移
│   ├── ResumeDb.cs        SQLite 断点表：per-file + 指纹兜底 + WAL
│   ├── FileFingerprint.cs size+首尾64KB 的 SHA-256
│   ├── Smtc.cs            SMTC 封装（manager 固定在 UI 线程创建）
│   ├── PlayerState.cs     读 AppState.json（容错 ComponentStates 包裹）
│   ├── PathResolver.cs    标题↔文件名匹配 + 路径猜测置信度
│   ├── Recorder.cs        记录端：边播边写
│   ├── PlayerLauncher.cs  播放端：打开→等就绪→seek
│   ├── LaunchHandoff.cs   跨进程交接（--open 与托盘之间）
│   ├── FileAssociation.cs 注册进系统"打开方式"列表
│   ├── AutoStart.cs       开机自启（启动文件夹快捷方式）
│   └── AppLog.cs          文件日志
├── UI/
│   ├── TrayHost.cs        托盘常驻 + 菜单 + 状态
│   ├── MainForm.cs        主面板：记录表 / 设置页
│   └── IconFactory.cs     托盘图标（多尺寸 ico → exe 资源 → 运行时绘制）
└── ResumePoint.ico/.png   应用图标

docs/STATUS.md            项目现状文档（给 Agent 读）
docs/research/            原始可行性调研（从 zip 解出）
installer/ResumePoint.iss Inno Setup 安装脚本
tools/Generate-Icon.ps1   图标生成脚本
probe/                    研究期 PowerShell 实测探针 + 手工验证脚本
win11-mediaplayer-resume-export.zip  原始可行性调研包（原件）
```

## 已知限制

- **不能**做成 Windows 服务（GSMTC 需交互式桌面会话）
- **不能**自动改系统的默认文件关联（Windows 的 `UserChoice` 限制），需用户手动设一次
- 播放器更新若改变 SMTC 行为，外部 seek 可能失效——但**记录库不受影响**，随时可切到 mpv 兜底
- `AppState.json` 的队列会滞后，所以路径识别**以 SMTC 标题为主**，队列只作兜底

## 相关文档

- **`docs/STATUS.md`** —— **项目现状文档（给 Agent 读）**：已验证事实 / 已知问题分级 / 优化方向 / 踩过的坑。**接手先读这份。**
- `docs/research/feasibility-report.md` —— 可行性主报告
- `docs/research/options-deep-dive.md` —— 8 条路线逐条对比
- `docs/research/HANDOFF.md` —— 研究交接文档
