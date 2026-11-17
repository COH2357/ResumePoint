# ResumePoint 端到端验证
# 1) 启动托盘程序（记录端）
# 2) 让播放器播一段，记录端应写入断点
# 3) 关闭播放器（模拟"完全退出"）
# 4) 用 --open 重新打开 -> 应自动跳回断点
$ErrorActionPreference = 'Continue'

$exe = "E:\co-work\WindowsMediaPlayerPlugin\src\ResumePoint\bin\Debug\net8.0-windows10.0.19041.0\ResumePoint.exe"
$launcher = "E:\co-work\WindowsMediaPlayerPlugin\probe\Launch-MediaPlayer.ps1"
$reader = "E:\co-work\WindowsMediaPlayerPlugin\probe\Read-SmtcAndQueue.ps1"
$file = "E:\Videos\let me go pv.mp4"

function Get-Position {
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reader 2>&1
    $idx = -1
    for ($i = 0; $i -lt $out.Count; $i++) {
        if ($out[$i] -match 'Microsoft\.ZuneMusic') { $idx = $i; break }
    }
    if ($idx -lt 0) { return $null }
    for ($j = $idx; $j -lt [Math]::Min($idx + 8, $out.Count); $j++) {
        if ($out[$j] -match 'Position = ') {
            if ($out[$j] -match 'Position = ([0-9:.]+)') { return $Matches[1] }
        }
    }
    return $null
}

Write-Host '=== 步骤 1/6：启动 ResumePoint 托盘程序 ==='
Get-Process -Name "ResumePoint" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
Start-Sleep -Seconds 4
$proc = Get-Process -Name "ResumePoint" -ErrorAction SilentlyContinue
Write-Host ("  托盘进程: " + $(if ($proc) { "已运行 (PID $($proc.Id))" } else { "未运行!" }))

Write-Host ''
Write-Host '=== 步骤 2/6：启动播放器播放测试视频 ==='
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $launcher -FilePath $file 2>&1 | Select-String 'RESULT' | ForEach-Object { Write-Host ("  " + $_.ToString().Trim()) }

Write-Host ''
Write-Host '=== 步骤 3/6：等待记录端写入（约 45 秒） ==='
for ($i = 1; $i -le 9; $i++) {
    Start-Sleep -Seconds 5
    Write-Host ("  t=" + ($i * 5) + "s  播放器位置 = " + (Get-Position))
}

Write-Host ''
Write-Host '=== 步骤 4/6：查数据库 ==='
$p = Start-Process -FilePath $exe -ArgumentList "--list" -Wait -PassThru -WorkingDirectory (Split-Path $exe)
Get-Content "$env:LOCALAPPDATA\ResumePoint\records.txt" -Encoding UTF8

Write-Host '=== 步骤 5/6：关闭播放器（模拟完全退出） ==='
$mp = Get-Process -Name "Microsoft.Media.Player" -ErrorAction SilentlyContinue
if ($mp) {
    [void]$mp.CloseMainWindow()
    Start-Sleep -Seconds 6
    if (Get-Process -Name "Microsoft.Media.Player" -ErrorAction SilentlyContinue) {
        Write-Host '  未响应 WM_CLOSE，强制结束'
        Get-Process -Name "Microsoft.Media.Player" -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 2
    } else { Write-Host '  已关闭' }
} else { Write-Host '  播放器本来就没运行' }
Write-Host ("  当前会话位置: " + (Get-Position))

Write-Host ''
Write-Host '=== 步骤 6/6：用 --open 重新打开（应自动跳回断点） ==='
# 注意：路径含空格，必须显式加引号传给 Start-Process
$p2 = Start-Process -FilePath $exe -ArgumentList "--open `"$file`"" -Wait -PassThru -WorkingDirectory (Split-Path $exe)
Write-Host ("  退出码: " + $p2.ExitCode)
Start-Sleep -Seconds 3
Write-Host ("  续播后位置 = " + (Get-Position))
Write-Host "  --- last-command.txt ---"
Get-Content "$env:LOCALAPPDATA\ResumePoint\last-command.txt" -Encoding UTF8 | Select-Object -Last 4 | ForEach-Object { Write-Host ("    " + $_) }

Write-Host ''
Write-Host '=== 最终数据库 ==='
Start-Process -FilePath $exe -ArgumentList "--list" -Wait -WorkingDirectory (Split-Path $exe)
Get-Content "$env:LOCALAPPDATA\ResumePoint\records.txt" -Encoding UTF8
