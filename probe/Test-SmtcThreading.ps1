# 验证 SMTC manager 线程归属修复：
#   1) 先关播放器
#   2) 启动 ResumePoint（此时没有任何媒体会话 -> manager 在 UI 线程上创建但列表为空）
#   3) 再启动播放器播放一个"数据库里没有记录"的新文件
#   4) 记录端应当能找到会话并写入记录 —— 这正是修复前失败的场景
$ErrorActionPreference = 'Continue'

$exe = "E:\co-work\WindowsMediaPlayerPlugin\publish\ResumePoint.exe"
$launcher = "E:\co-work\WindowsMediaPlayerPlugin\probe\Launch-MediaPlayer.ps1"
$reader = "E:\co-work\WindowsMediaPlayerPlugin\probe\Read-SmtcAndQueue.ps1"
$file = "E:\Videos\诀别书超长纯.mp4"

function Get-Position {
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $reader 2>&1
    $idx = -1
    for ($i = 0; $i -lt $out.Count; $i++) {
        if ($out[$i] -match 'Microsoft\.ZuneMusic') { $idx = $i; break }
    }
    if ($idx -lt 0) { return $null }
    for ($j = $idx; $j -lt [Math]::Min($idx + 8, $out.Count); $j++) {
        if ($out[$j] -match 'Position = ([0-9:.]+)') { return $Matches[1] }
    }
    return $null
}

Write-Host '=== 步骤 1：关掉播放器 ==='
Get-Process -Name "Microsoft.Media.Player" -EA SilentlyContinue | Stop-Process -Force
Get-Process -Name "ResumePoint" -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
Write-Host ("  播放器进程数 = " + @(Get-Process -Name 'Microsoft.Media.Player' -EA SilentlyContinue).Count)

Write-Host ''
Write-Host '=== 步骤 2：启动 ResumePoint（此刻没有媒体会话） ==='
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
Start-Sleep -Seconds 8
Write-Host ("  ResumePoint PID = " + (Get-Process -Name 'ResumePoint' -EA SilentlyContinue).Id)
Write-Host ("  当前会话位置 = " + (Get-Position) + "   <- 应为空（播放器还没开）")

Write-Host ''
Write-Host '=== 步骤 3：用 --open 打开（等同资源管理器双击的真实流程） ==='
# 注意：必须走 ResumePoint.exe --open（shell 实际执行的就是它），
# 才能验证"跨进程交接"这条链路；用 PowerShell 启动器会绕过它。
Start-Process -FilePath $exe -ArgumentList "--open `"$file`"" -Wait -WorkingDirectory (Split-Path $exe)
Get-Content "$env:LOCALAPPDATA\ResumePoint\last-command.txt" -Encoding UTF8 | Select-Object -Last 4 | ForEach-Object { Write-Host ("  " + $_) }
Write-Host ("  交接文件: " + $(if (Test-Path "$env:LOCALAPPDATA\ResumePoint\last-opened.json") { Get-Content "$env:LOCALAPPDATA\ResumePoint\last-opened.json" -Raw } else { "(无)" }))

Write-Host ''
Write-Host '=== 步骤 4：等记录端发现会话并写入 ==='
for ($i = 1; $i -le 8; $i++) {
    Start-Sleep -Seconds 5
    Write-Host ("  t=" + ($i * 5) + "s  位置 = " + (Get-Position))
}

Write-Host ''
Write-Host '=== 步骤 5：查数据库（应出现新文件记录） ==='
Start-Process -FilePath $exe -ArgumentList "--list" -Wait -WorkingDirectory (Split-Path $exe)
Get-Content "$env:LOCALAPPDATA\ResumePoint\records.txt" -Encoding UTF8
