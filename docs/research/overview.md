# win11-mediaplayer-resume

> Windows 11 新版媒体播放器「每次打开视频从上次断点继续」——可行性调研项目包
> 导出时间：2026-11-17 ｜ 状态：**可行性已验证，等实机探针数据后进入实现**

---

## 快速开始

```powershell
# 1) 先在 Windows 11 媒体播放器里播放一个视频（SMTC 会话只在播放时存在）
# 2) 跑探针
probe\run-probe.cmd
#    或
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\probe\Probe-MediaPlayerResume.ps1

# 3) 可选：真写一次 seek（跳到 10 分钟）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\probe\Probe-MediaPlayerResume.ps1 -SeekToSeconds 600
```

**先读 `HANDOFF.md`** —— 那里有完整结论、已核实事实清单、决策树和实施架构设计。

---

## 结论速览

| 问题 | 答案 |
|---|---|
| 能给新版媒体播放器写插件吗？ | **不能**。微软官方明确答复不支持；UWP/MSIX 无加载项宿主机制 |
| 那功能能实现吗？ | **能**，走「外部伴生工具 + 官方 SMTC API」，不需要插件 |
| 原生是不是已经满足了？ | **部分**。原生只存**一条**断点（跟当前队列项走），且微软零文档；一手实测称会续播 → 需实测确认 |
| 兜底方案 | 换 mpv，`mpv.conf` 一行 `save-position-on-quit=yes`，0.5–2 人时 |
| 生死判定点 | `TryChangePlaybackPositionAsync` 对 Media Player 是否返回 True |

---

## 目录结构

```
win11-mediaplayer-resume-export/
├── README.md                        ← 本文件
├── HANDOFF.md                       ← ★ 交接文档：结论 / 已核实事实 / 决策树 / 实施架构
├── MEMORY.md                        ← 项目级持久化摘要（给后续会话快速恢复上下文）
├── docs/
│   ├── feasibility-report.md        ← 主报告：结论 + 证据链 + 8 条路线对比 + 判定步骤
│   └── options-deep-dive.md         ← 8 条路线逐条展开（含全部 URL）
├── probe/
│   ├── Probe-MediaPlayerResume.ps1  ← PowerShell 探针（主）
│   ├── run-probe.cmd                ← 双击即跑
│   └── mpv.conf                     ← 路线 B 的现成配置
├── src/
│   └── MediaResumeProbe/            ← C# 备用探针（.NET 8 + WinRT 投影）
│       ├── MediaResumeProbe.csproj
│       └── Program.cs
└── memory/
    └── win11-mediaplayer-resume.md  ← 已沉淀的技术结论（= Agent memory topic）
```

---

## 环境要求

| 项 | 要求 |
|---|---|
| OS | Windows 11（22H2 及以上），**交互式桌面会话** |
| PowerShell | Windows PowerShell **5.1**（`powershell.exe`）。PS7 不支持本探针的 WinRT 投影方式 |
| .NET（用 C# 探针时） | .NET 8 SDK |
| 权限 | 普通用户即可；但需**播放中的媒体播放器**才有 SMTC 会话可探测 |

**已验证状态**
- C# 探针：已在 .NET 9 SDK 下以 `net8.0-windows10.0.19041.0` **编译通过（0 error / 0 warning）**
  —— 等于验证了 `Windows.Media.Control` 那套 API（`TryChangePlaybackPositionAsync` /
  `GetTimelineProperties` / `IsPlaybackPositionEnabled` / `RequestAsync`）确实存在且签名一致。
- PowerShell 探针：**未在真实 Windows 上运行过**（调研沙箱是 Linux，无 PowerShell）。
  逻辑与 C# 版等价，若 WinRT await 桥报错请直接改用 C# 版。

---

## 已知坑

- SMTC 会话**只在播放时存在** —— 探针前务必先让播放器播起来。
- GSMTC **不能在服务 / SYSTEM / 无头会话里用**（微软 Q&A 已确认）。
- PowerShell 探针里 `RequestAsync()` 首次调用可能卡几秒，属正常。
- `AppState.json` 的字段结构可能随播放器版本变化 —— 正式工具里要做**容错降级**，别让字段缺失直接崩掉。
- Windows 11 存在 XAML 依赖应用集体打不开的已知 bug（KB5072911）—— 深度绑定媒体播放器的长期风险高于换播放器。
