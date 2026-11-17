# ASCII-only SMTC probe (avoids PS 5.1 UTF-8-no-BOM encoding trap)
$ErrorActionPreference = 'Continue'

Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null
$methods = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
}
$am = $methods[0]

function Await($op, [Type]$t) {
    $task = $am.MakeGenericMethod($t).Invoke($null, @($op))
    if (-not $task.Wait(15000)) { throw 'timeout' }
    $task.Result
}

[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Control.GlobalSystemMediaTransportControlsSession, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null

Write-Output ("SessionManager type: " + [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager].FullName)

$op = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()
$mgr = Await $op ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
$sessions = @($mgr.GetSessions())

Write-Output ("SESSION COUNT = " + $sessions.Count)
foreach ($s in $sessions) {
    $id = $s.SourceAppUserModelId
    Write-Output ("--- " + $id)
    try {
        $info = $s.GetPlaybackInfo()
        Write-Output ("    PlaybackStatus = " + $info.PlaybackStatus)
        Write-Output ("    IsPlaybackPositionEnabled = " + $info.Controls.IsPlaybackPositionEnabled)
        Write-Output ("    IsPlayEnabled = " + $info.Controls.IsPlayEnabled)
    } catch { Write-Output ("    PlaybackInfo ERR: " + $_.Exception.Message) }
    try {
        $tl = $s.GetTimelineProperties()
        Write-Output ("    Position = " + $tl.Position + "  EndTime = " + $tl.EndTime)
        Write-Output ("    MinSeek = " + $tl.MinSeekTime + "  MaxSeek = " + $tl.MaxSeekTime)
    } catch { Write-Output ("    Timeline ERR: " + $_.Exception.Message) }
    try {
        $mp = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.MediaPlaybackProperties])
        Write-Output ("    Title = '" + $mp.Title + "'  Artist = '" + $mp.Artist + "'")
    } catch { Write-Output ("    MediaProps ERR: " + $_.Exception.Message) }
}
