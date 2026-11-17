# ASCII-only: SMTC 会话 + 媒体属性 + AppState.json 队列 交叉校验
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
[Windows.Media.MediaProperties.MediaPlaybackProperties, Windows.Media.MediaProperties, ContentType = WindowsRuntime] | Out-Null

$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
$sessions = @($mgr.GetSessions())
Write-Output ("SESSION COUNT = " + $sessions.Count)

foreach ($s in $sessions) {
    $id = $s.SourceAppUserModelId
    Write-Output ("--- " + $id)
    try {
        $info = $s.GetPlaybackInfo()
        Write-Output ("    PlaybackStatus = " + $info.PlaybackStatus)
        Write-Output ("    IsPlaybackPositionEnabled = " + $info.Controls.IsPlaybackPositionEnabled)
    } catch { Write-Output ("    PlaybackInfo ERR: " + $_.Exception.Message) }
    try {
        $tl = $s.GetTimelineProperties()
        Write-Output ("    Position = " + $tl.Position + "  EndTime = " + $tl.EndTime)
    } catch { Write-Output ("    Timeline ERR: " + $_.Exception.Message) }
    try {
        $mp = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.MediaProperties.MediaPlaybackProperties])
        Write-Output ("    Title  = '" + $mp.Title + "'")
        Write-Output ("    Artist = '" + $mp.Artist + "'")
        Write-Output ("    Album  = '" + $mp.AlbumTitle + "'")
    } catch { Write-Output ("    MediaProps ERR: " + $_.Exception.Message) }
}

Write-Output ''
Write-Output '--- AppState.json queue ---'
$sp = "$env:LOCALAPPDATA\Packages\Microsoft.ZuneMusic_8wekyb3d8bbwe\LocalState\AppState.json"
if (Test-Path $sp) {
    $j = (Get-Content -LiteralPath $sp -Raw -Encoding UTF8) | ConvertFrom-Json
    $cs = $j.ComponentStates
    $idx = 0
    foreach ($it in @($cs.PlayQueueModel.Items)) {
        Write-Output ("    [$idx] " + $it.Source)
        $idx++
    }
    Write-Output ("    CurrentItemIndex = " + $cs.PlayQueueModel.CurrentItemIndex)
    Write-Output ("    CurrentPosition  = " + $cs.PlayerModel.CurrentPosition)
    Write-Output ("    LastWrite        = " + (Get-Item $sp).LastWriteTime)
}
