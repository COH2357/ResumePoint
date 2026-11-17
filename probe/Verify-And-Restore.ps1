# 验证 seek 是否真正生效，并把播放位置复原到原始断点
param(
    [long]$RestoreTicks = 290276454568,   # 08:03:47.6454568
    [int]$VerifySeconds = 600
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null
$am = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]
function Await($op, [Type]$t) {
    $task = $am.MakeGenericMethod($t).Invoke($null, @($op))
    if (-not $task.Wait(15000)) { throw 'timeout' }
    $task.Result
}

[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Control.GlobalSystemMediaTransportControlsSession, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null

function Get-Target {
    $mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
    foreach ($s in @($mgr.GetSessions())) {
        if ($s.SourceAppUserModelId -like 'Microsoft.ZuneMusic*') { return $s }
    }
    return $null
}

function Show-Position([string]$tag) {
    $s = Get-Target
    if (-not $s) { Write-Host "[$tag] 无目标会话"; return $null }
    $tl = $s.GetTimelineProperties()
    Write-Host ("[{0}] Position = {1}  (总时长 {2})" -f $tag, $tl.Position, $tl.EndTime)
    return $tl.Position
}

Write-Host '=== A. 确认上一次 seek(600s) 是否真的生效 ==='
$p = Show-Position 'seek后'
if ($p) {
    $sec = $p.TotalSeconds
    Write-Host ("  -> 位置秒数 = {0:N1}（目标 600，允许播放漂移）" -f $sec)
    if ([Math]::Abs($sec - 600) -lt 30) { Write-Host '  -> 判定: 跳转真实生效 ✅' }
    else { Write-Host ("  -> 判定: 偏离 600s 较远（当前 {0:N1}s），需人工确认画面" -f $sec) }
}

Write-Host ''
Write-Host '=== B. 复原到原始断点 08:03:47.6454568 ==='
$s = Get-Target
if ($s) {
    $resOp = $s.TryChangePlaybackPositionAsync($RestoreTicks)
    $ok = Await $resOp ([bool])
    Write-Host "  TryChangePlaybackPositionAsync(restore) = $ok"
    Start-Sleep -Seconds 2
    Show-Position '复原后' | Out-Null
}

Write-Host ''
Write-Host '=== C. 当前 AppState.json 落盘值 ==='
$statePath = "$env:LOCALAPPDATA\Packages\Microsoft.ZuneMusic_8wekyb3d8bbwe\LocalState\AppState.json"
$j = (Get-Content -LiteralPath $statePath -Raw -Encoding UTF8) | ConvertFrom-Json
Write-Host ("  PlayerModel.CurrentPosition = " + $j.ComponentStates.PlayerModel.CurrentPosition)
