# 原生续播对照实验：完全退出 -> 重新用文件激活打开 -> 读起播位置
param(
    [Parameter(Mandatory=$true)][string]$FilePath
)

$ErrorActionPreference = 'Continue'
$statePath = "$env:LOCALAPPDATA\Packages\Microsoft.ZuneMusic_8wekyb3d8bbwe\LocalState\AppState.json"

function Read-State {
    try {
        $j = (Get-Content -LiteralPath $statePath -Raw -Encoding UTF8) | ConvertFrom-Json
        return $j.ComponentStates.PlayerModel.CurrentPosition
    } catch { return '<unreadable>' }
}

Write-Host '=== 1. 优雅关闭媒体播放器 ==='
$p = Get-Process -Name 'Microsoft.Media.Player' -ErrorAction SilentlyContinue
if ($p) {
    [void]$p.CloseMainWindow()
    Write-Host "  已发送 WM_CLOSE，等待退出..."
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 800
        if (-not (Get-Process -Name 'Microsoft.Media.Player' -ErrorAction SilentlyContinue)) { break }
    }
    if (Get-Process -Name 'Microsoft.Media.Player' -ErrorAction SilentlyContinue) {
        Write-Host '  仍未退出，强制结束'
        Get-Process -Name 'Microsoft.Media.Player' -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 2
    } else {
        Write-Host '  已正常退出 ✅'
    }
} else {
    Write-Host '  播放器本来就未运行'
}

Start-Sleep -Seconds 2
Write-Host ("  AppState.json 落盘值 = " + (Read-State))
Write-Host ("  LastWrite             = " + (Get-Item $statePath).LastWriteTime)

Write-Host ''
Write-Host '=== 2. 重新用文件激活方式打开（等同资源管理器双击） ==='
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\Launch-MediaPlayer.ps1" -FilePath $FilePath

Write-Host ''
Write-Host '=== 3. 等待起播并读位置 ==='
$reader = "$PSScriptRoot\Read-SmtcSessions.ps1"
for ($i = 1; $i -le 15; $i++) {
    Start-Sleep -Seconds 4
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reader 2>&1
    $line = $out | Select-String -Pattern 'Microsoft.ZuneMusic' | Select-Object -First 1
    if ($line) {
        $idx = [array]::IndexOf($out, $line)
        for ($j = $idx; $j -lt [Math]::Min($idx + 6, $out.Count); $j++) {
            Write-Host ("  " + $out[$j].ToString().Trim())
        }
        break
    }
    Write-Host "  (t=$($i*4)s) 尚未检测到目标会话..."
}
