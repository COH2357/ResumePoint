# 反射方式取 MediaPlaybackProperties 的真实类型，绕开 PS5.1 的 WinRT 类型名解析限制
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

$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
$sessions = @($mgr.GetSessions())
Write-Output ("SESSION COUNT = " + $sessions.Count)

foreach ($s in $sessions) {
    Write-Output ("--- " + $s.SourceAppUserModelId)
    try {
        $op = $s.TryGetMediaPropertiesAsync()
        $iface = $op.GetType().GetInterfaces() | Where-Object { $_.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
        if (-not $iface) { Write-Output "    (no IAsyncOperation interface found)"; continue }
        $realType = $iface.GetGenericArguments()[0]
        Write-Output ("    real type = " + $realType.FullName)
        $mp = Await $op $realType
        Write-Output ("    Title      = '" + $mp.Title + "'")
        Write-Output ("    Artist     = '" + $mp.Artist + "'")
        Write-Output ("    AlbumTitle = '" + $mp.AlbumTitle + "'")
        Write-Output ("    TrackNumber= '" + $mp.TrackNumber + "'")
        Write-Output ("    ThumbnailUri = " + $mp.ThumbnailUri)
    } catch {
        Write-Output ("    MediaProps ERR: " + $_.Exception.Message)
    }
}
