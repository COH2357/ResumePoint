<#
.SYNOPSIS
    Windows 11 新版媒体播放器（Microsoft.ZuneMusic_8wekyb3d8bbwe）"断点续播"可行性探针。

.DESCRIPTION
    四步只读探测 + 一步可选写测试，用来在 10 分钟内判定整条技术路线是否成立：

      [1] 包身份 / AUMID / 版本
      [2] 断点数据落盘位置：LocalState\AppState.json（PlayerModel.CurrentPosition）
      [3] SMTC 全局媒体会话：Media Player 是否注册会话、当前位置、是否允许 seek
      [4] 尝试外部 seek（仅当显式传 -SeekToSeconds 才执行）

    [3]/[4] 是整条路线的生死判定点：
      - 若 [3] 里出现 SourceAppUserModelId = Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic
        且 IsPlaybackPositionEnabled = True，则外部驱动跳转可行；
      - 若 [3] 里根本没有该会话，或 IsPlaybackPositionEnabled = False，
        则"外部让系统播放器跳到指定秒数"这条路直接判死，改走 mpv/VLC/MPC-HC。

.PARAMETER SeekToSeconds
    可选。对当前活动中的 Media Player 会话发起一次 seek 到指定秒数。
    只影响正在播放的媒体，不写任何文件。

.PARAMETER PackageFamilyName
    可选。默认 Microsoft.ZuneMusic_8wekyb3d8bbwe。

.NOTES
    必须用 Windows PowerShell 5.1（powershell.exe）运行，PS7 不支持这里的 WinRT 投影方式：
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Probe-MediaPlayerResume.ps1
    整个脚本默认只读；唯一写操作是被 -SeekToSeconds 显式触发的。

    【本文件相对导出包原版的两处修正】
    1) 编码：原文件为 UTF-8（无 BOM），Windows PowerShell 5.1 按 ANSI/GBK 解码，
       中文注释与字符串会变成乱码并直接导致解析器报 "Unexpected token"。
       本文件已保存为 UTF-8 with BOM，PS 5.1 可正确解析。
    2) AppState.json 取值路径：真实结构是
           { "ComponentStates": { "PlayQueueModel": {...}, "PlayerModel": {...} } }
       原版按根节点读取 $j.PlayQueueModel / $j.PlayerModel，在真实文件上恒为 null，
       会误报"队列记录为空 / PlayerModel 节点缺失"。本版同时兼容
       "ComponentStates 包裹" 与 "根节点直挂" 两种结构，并打印实际命中的路径。
#>

[CmdletBinding()]
param(
    [int]$SeekToSeconds = -1,
    [string]$PackageFamilyName = 'Microsoft.ZuneMusic_8wekyb3d8bbwe'
)

$ErrorActionPreference = 'Continue'
$script:FAILED = 0

function Write-Section { param([string]$Title)
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkGray
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor DarkGray
}
function Write-Ok   { param($m) Write-Host "  [OK]   $m" -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  [WARN] $m" -ForegroundColor Yellow }
function Write-Bad  { param($m) Write-Host "  [FAIL] $m" -ForegroundColor Red }
function Write-Info { param($m) Write-Host "  [INFO] $m" -ForegroundColor Gray }

# ---------------------------------------------------------------- WinRT 异步桥
# Windows PowerShell 5.1 无法直接 await IAsyncOperation<T>，用 AsTask 反射桥接。
function Initialize-WinRtAwait {
    try {
        Add-Type -AssemblyName System.Runtime.WindowsRuntime -ErrorAction Stop
    } catch {
        Write-Bad "无法加载 System.Runtime.WindowsRuntime。请用 Windows PowerShell 5.1 (powershell.exe) 运行。"
        $script:FAILED++
        return $null
    }
    $methods = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    }
    if (-not $methods) { Write-Bad '找不到 AsTask(IAsyncOperation<T>) 重载。'; $script:FAILED++; return $null }
    $methods[0]
}

function Await-WinRt {
    param($AsyncOp, [Type]$ResultType, $script:AwaitMethod)
    if (-not $AsyncOp) { throw '空异步操作' }
    $m = $AwaitMethod.MakeGenericMethod($ResultType)
    $task = $m.Invoke($null, @($AsyncOp))
    if (-not $task.Wait(10000)) { throw 'await 超时（10s）' }
    $task.Result
}

# ================================================================ [1] 包身份
Write-Section '[1] 包身份 / AUMID'
try {
    $pkg = Get-AppxPackage -Name 'Microsoft.ZuneMusic' -ErrorAction SilentlyContinue
    if (-not $pkg) {
        $pkg = Get-AppxPackage | Where-Object { $_.PackageFamilyName -like 'Microsoft.ZuneMusic_*' } | Select-Object -First 1
    }
    if ($pkg) {
        Write-Ok   "PackageFamilyName : $($pkg.PackageFamilyName)"
        Write-Ok   "Version           : $($pkg.Version)"
        Write-Ok   "InstallLocation   : $($pkg.InstallLocation)"
        $aumid = "$($pkg.PackageFamilyName)!Microsoft.ZuneMusic"
        Write-Ok   "AUMID             : $aumid"
        $script:TargetAumid = $aumid
    } else {
        Write-Warn '未找到 Microsoft.ZuneMusic 包（新版媒体播放器可能未安装/已卸载）。'
        $script:TargetAumid = "$PackageFamilyName!Microsoft.ZuneMusic"
    }
} catch {
    Write-Bad "包查询失败: $($_.Exception.Message)"
    $script:TargetAumid = "$PackageFamilyName!Microsoft.ZuneMusic"
}

# ================================================ [2] 断点数据落盘位置
Write-Section '[2] 断点数据落盘位置 (LocalState)'
$stateDir = Join-Path $env:LOCALAPPDATA "Packages\$PackageFamilyName\LocalState"

$appStatePath = Join-Path $stateDir 'AppState.json'
if (Test-Path $appStatePath) {
    Write-Ok "AppState.json 存在: $appStatePath"
    try {
        $raw = Get-Content -LiteralPath $appStatePath -Raw -Encoding UTF8
        $j = $raw | ConvertFrom-Json

        # --- 容错：真实结构是 ComponentStates 包裹，但保留根节点直挂的兼容 ---
        # 优先用 ComponentStates；只有它不存在时才回退到根节点。
        $scope = $j
        $scopePath = '(根节点)'
        if ($j.PSObject.Properties['ComponentStates'] -and $j.ComponentStates) {
            $scope = $j.ComponentStates
            $scopePath = 'ComponentStates'
        }
        Write-Info "取值节点路径: $scopePath"

        $q = $scope.PSObject.Properties['PlayQueueModel']
        $pm = $scope.PSObject.Properties['PlayerModel']

        if ($q -and $q.Value) {
            $queue = $q.Value
            $srcs = @()
            if ($queue.Items) { $srcs = @($queue.Items | ForEach-Object { $_.Source }) }
            if ($srcs.Count -gt 0) {
                foreach ($s in $srcs) { Write-Info "队列项 Source      : $s" }
                foreach ($it in @($queue.Items)) {
                    if ($null -ne $it.Kind) { Write-Info "队列项 Kind        : $($it.Kind)" }
                    if ($null -ne $it.AccessToken) {
                        Write-Info "队列项 AccessToken : $(if ($it.AccessToken) { 'present（存在，需原样保留）' } else { 'null' })"
                    }
                }
            } else {
                Write-Info 'PlayQueueModel.Items 为空（播放器当前没有队列记录）'
            }
            if ($null -ne $queue.CurrentItemIndex) {
                Write-Info "CurrentItemIndex   : $($queue.CurrentItemIndex)"
            }
        } else {
            Write-Warn 'PlayQueueModel 节点缺失（该版本可能改了结构）'
        }

        if ($pm -and $pm.Value) {
            $player = $pm.Value
            $pos = $player.CurrentPosition
            if ($null -ne $pos) {
                Write-Ok "PlayerModel.CurrentPosition = $pos"
                try {
                    $ts = [TimeSpan]::Parse($pos)
                    Write-Info ("  -> 换算 = {0:hh\:mm\:ss\.fff}" -f $ts)
                } catch { Write-Warn "  -> CurrentPosition 不是合法 TimeSpan 字符串: $pos" }
            } else {
                Write-Warn 'PlayerModel.CurrentPosition 缺失（该版本可能改了结构）'
            }
        } else {
            Write-Warn 'PlayerModel 节点缺失（该版本可能改了结构）'
        }
        Write-Info '注意：该字段只保存"当前队列项"的单一断点，不是 per-file 断点表。'
    } catch {
        Write-Bad "解析 AppState.json 失败: $($_.Exception.Message)"
    }
} else {
    Write-Warn "未找到 $appStatePath"
    Write-Info '（播放器从未运行过、或该版本换了结构。目录清单如下）'
    if (Test-Path $stateDir) {
        Get-ChildItem -LiteralPath $stateDir -Force | Select-Object Name, Length, LastWriteTime |
            Format-Table -AutoSize | Out-String | Write-Host
    }
}

$dbPath = Join-Path $stateDir 'MediaPlayer.db'
if (Test-Path $dbPath) {
    Write-Ok "MediaPlayer.db 存在: $dbPath （注意：这张表只有'最近播放'时间戳，没有位置列）"
    $sqlite = Get-Command sqlite3 -ErrorAction SilentlyContinue
    if ($sqlite) {
        Write-Info 'sqlite3 可用，查询最近播放：'
        & $sqlite.Source $dbPath "SELECT f.Uri, r.ItemType, datetime((r.PlayedTime/10000000.0)-62135596800,'unixepoch') AS PlayedUTC FROM File f JOIN RecentlyPlayed r ON f.Id=r.Id ORDER BY r.PlayedTime DESC LIMIT 10;" 2>&1 | Write-Host
    } else {
        Write-Info '（未安装 sqlite3，跳过查询；不影响判定）'
    }
} else {
    Write-Warn '未找到 MediaPlayer.db'
}

# ==================================================== [3] SMTC 会话探测
Write-Section '[3] SMTC 全局媒体会话（生死判定）'
$awaitMethod = Initialize-WinRtAwait

if ($awaitMethod) {
    try {
        [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null
        [Windows.Media.Control.GlobalSystemMediaTransportControlsSession, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null

        $mgrOp = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()
        $mgr = Await-WinRt $mgrOp ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]) $awaitMethod
        $sessions = @($mgr.GetSessions())

        Write-Ok "当前系统媒体会话数: $($sessions.Count)"

        $found = $null
        foreach ($s in $sessions) {
            $id = $s.SourceAppUserModelId
            $isTarget = ($id -eq $script:TargetAumid) -or ($id -like 'Microsoft.ZuneMusic*')
            $tag = if ($isTarget) { '  <<<< 新版媒体播放器' } else { '' }
            Write-Info "会话: $id$tag"

            try {
                $info = $s.GetPlaybackInfo()
                Write-Info ("   状态={0}  可Seek={1}" -f $info.PlaybackStatus, $info.Controls.IsPlaybackPositionEnabled)
            } catch { Write-Warn "   读 PlaybackInfo 失败: $($_.Exception.Message)" }

            try {
                $tl = $s.GetTimelineProperties()
                Write-Info ("   位置={0}  时长={1}  Min={2} Max={3}" -f $tl.Position, $tl.EndTime, $tl.MinSeekTime, $tl.MaxSeekTime)
            } catch { Write-Warn "   读 TimelineProperties 失败: $($_.Exception.Message)" }

            if ($isTarget -and -not $found) { $found = $s }
        }

        if ($found) {
            Write-Host ''
            Write-Ok '★ 新版媒体播放器确实注册了 SMTC 会话 —— "外部读位置"这条路成立。'
            $seekable = $false
            try { $seekable = $found.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled } catch {}
            if ($seekable) {
                Write-Ok '★ IsPlaybackPositionEnabled = True —— "外部写位置（seek）"这条路也成立。'
            } else {
                Write-Warn 'IsPlaybackPositionEnabled = False —— 外部 seek 大概率会被拒绝，路线风险高。'
            }
            $script:TargetSession = $found
        } else {
            Write-Host ''
            Write-Warn '未发现新版媒体播放器的 SMTC 会话。'
            Write-Info '可能原因：播放器没在播放任何内容（会话只在播放时出现），或它不注册 SMTC。'
            Write-Info '判定：先在媒体播放器里播一个视频，再重跑本脚本看 [3]。'
        }
    } catch {
        Write-Bad "SMTC 探测失败: $($_.Exception.Message)"
        Write-Info '提示：SMTC 需要交互式桌面会话（不能在服务/计划任务里跑）。'
    }
} else {
    Write-Warn 'WinRT 桥不可用，跳过 SMTC 探测。'
}

# ==================================================== [4] 可选：写测试
if ($SeekToSeconds -ge 0) {
    Write-Section "[4] 外部 seek 写测试 -> $SeekToSeconds 秒"
    if ($script:TargetSession) {
        try {
            $ticks = [TimeSpan]::FromSeconds($SeekToSeconds).Ticks
            $resOp = $script:TargetSession.TryChangePlaybackPositionAsync($ticks)
            $ok = Await-WinRt $resOp ([bool]) $awaitMethod
            if ($ok) {
                Write-Ok "TryChangePlaybackPositionAsync 返回 True —— 外部跳转成功，路线成立 ✅"
            } else {
                Write-Warn "TryChangePlaybackPositionAsync 返回 False —— 播放器拒绝了这次跳转。"
                Write-Info '若此时播放器确实在播放，通常说明它没实现 PlaybackPositionChangeRequested。'
            }
        } catch {
            Write-Bad "seek 调用异常: $($_.Exception.Message)"
        }
    } else {
        Write-Warn '没有可用的目标会话，跳过 seek。请先让媒体播放器开始播放，再重跑。'
    }
}

# ==================================================== 结论
Write-Section '判定汇总'
Write-Host @'
  路线成立（可做"系统播放器 + 外部伴生工具"）：
    [3] 出现 Microsoft.ZuneMusic 会话  且  IsPlaybackPositionEnabled = True
    -> 可做：外部读断点 + 外部 seek + 自定义协议/IExplorerCommand 右键入口

  路线判死（改用自带续播的播放器）：
    [3] 无该会话，或 IsPlaybackPositionEnabled = False
    -> 直接上 mpv（save-position-on-quit=yes），0.5–2 人时搞定，零风险

  无论上面哪种，"双击文件是否自动续播"仍需人工确认：
    用同一个视频做三组对照（资源管理器双击 / Home 最近播放点入 / 完全退出后重开），
    每次都记下实际起播位置。
'@ -ForegroundColor Yellow
