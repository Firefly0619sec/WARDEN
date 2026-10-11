# ============================================================================
#  B10 性能基准（WARDEN）
# ============================================================================
#  量三个数（用户会关心"这东西占我多少资源"）：
#    ① 空闲时：CPU 占用 / 内存 / 扫描轮次（每秒多少轮）
#    ② 被"弹窗轰炸"时：还能不能跟上（轮次是否掉下来）
#    ③ 应急压制期间：CPU 峰值
#
#  ★ 只动自己拉起的进程：WARDEN 测试实例 + 红队自测工具 ✓
#  ★ 测完把红队工具停掉、WARDEN 恢复原状（用户自己的实例不动 ✗）
#
#  用法：pwsh -File tools\bench.ps1 [-Seconds 20]
# ============================================================================
param(
    [int]$Seconds = 20
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
$inst = Join-Path $env:LOCALAPPDATA 'Programs\WARDEN'
$data = Join-Path $env:LOCALAPPDATA 'WARDEN'
$log = Join-Path $data 'WARDEN.log'

function Sample([string]$label, [int]$sec) {
    Write-Host ('  · ' + $label + '（采样 ' + $sec + ' 秒）') -ForegroundColor DarkGray
    $first = -1.0; $mem = 0L; $n = 0; $peak = 0.0
    $end = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $end) {
        $p = Get-Process WARDEN -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) {
            if ($first -lt 0) { $first = $p.TotalProcessorTime.TotalMilliseconds }
            $mem = [Math]::Max($mem, $p.WorkingSet64)
            $peak = [Math]::Max($peak, $p.TotalProcessorTime.TotalMilliseconds)
        }
        Start-Sleep -Milliseconds 500
        $n++
    }
    $p2 = Get-Process WARDEN -ErrorAction SilentlyContinue | Select-Object -First 1
    $cpuMs = if ($p2 -and $first -ge 0) { $p2.TotalProcessorTime.TotalMilliseconds - $first } else { 0 }
    $cpuPct = if ($sec -gt 0) { [Math]::Round($cpuMs / ($sec * 1000) * 100 /
                              [Environment]::ProcessorCount, 1) } else { 0 }
    return [pscustomobject]@{
        Case = $label
        CpuPct = $cpuPct
        MemMb = [int]($mem / 1MB)
        Points = $n
    }
}

Write-Host '=== WARDEN 性能基准 ===' -ForegroundColor Cyan
if (-not (Test-Path (Join-Path $inst 'WARDEN.exe'))) {
    Write-Host ('★ 找不到安装好的 WARDEN: ' + $inst) -ForegroundColor Yellow
    exit 1
}

# 先停掉用户自己的实例（测完会重新拉起 ✓）
$wasRunning = (Get-Process WARDEN -ErrorAction SilentlyContinue) -ne $null
Get-Process WARDEN -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
Remove-Item (Join-Path $data 'WARDEN.lock') -Force -ErrorAction SilentlyContinue

$results = @()
Start-Process (Join-Path $inst 'WARDEN.exe') -WorkingDirectory $inst | Out-Null
# ★ 采样修复：先确认它真的起来了 ✗ 否则样本全是 0（上次就踩了这个坑 ✓）
$ready = $false
for ($w = 0; $w -lt 20; $w++) {
    Start-Sleep -Milliseconds 500
    if (Get-Process WARDEN -ErrorAction SilentlyContinue) { $ready = $true; break }
}
if (-not $ready) {
    Write-Host '  ★ WARDEN 没起来 → 检查自校验/锁文件（日志见 %LOCALAPPDATA%\WARDEN\WARDEN.log）' -ForegroundColor Red
    exit 2
}
Write-Host '  · 等待 WARDEN 起来 ✓' -ForegroundColor DarkGray
Start-Sleep -Seconds 3
$results += Sample '空闲（无弹窗）' $Seconds

# 弹窗轰炸
$rt = Join-Path $inst 'RedTeam.exe'
if (Test-Path $rt) {
    $rtp = Start-Process $rt -ArgumentList ([string]($Seconds + 6)) -WorkingDirectory $inst -PassThru
    Start-Sleep -Seconds 3
    $results += Sample '被弹窗轰炸' $Seconds
    try { if (-not $rtp.HasExited) { $rtp.Kill() } } catch { }
    Start-Sleep -Seconds 2
} else {
    Write-Host ('  · 没有红队工具（' + $rt + '），跳过轰炸场景') -ForegroundColor DarkGray
}

# 每轮扫描耗时（从日志的 [状态] 行间隔估算）
$marks = @()
if (Test-Path $log) {
    $marks = Select-String -Path $log -Pattern '\[状态\]' | Select-Object -Last 20 |
             ForEach-Object { ($_.Line -split '\s+')[1] }
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String -Width 120 | Write-Host

Write-Host '=== 结论 ===' -ForegroundColor Cyan
$idle = $results | Where-Object { $_.Case -like '空闲*' }
$load = $results | Where-Object { $_.Case -like '*轰炸*' }
if ($idle) {
    $msg = '  空闲时：CPU ' + $idle.CpuPct + '%（单核口径，' + [Environment]::ProcessorCount + ' 核机器）   内存 ' + $idle.MemMb + ' MB'
    Write-Host $msg -ForegroundColor Green
}
if ($load) {
    Write-Host ('  轰炸时：CPU ' + $load.CpuPct + '%   内存 ' + $load.MemMb + ' MB') -ForegroundColor Yellow
}
Write-Host ('  日志里的状态行数：' + $marks.Count + '（越多说明扫描越勤）') -ForegroundColor DarkGray
Write-Host ''
Write-Host '★ 只动了自测用的实例与红队工具 ✓ 你的 WARDEN 实例请自己重新启动（如果之前开着）' -ForegroundColor DarkGray
if ($wasRunning) {
    Start-Process (Join-Path $inst 'WARDEN.exe') -WorkingDirectory $inst | Out-Null
    Write-Host '  （已重新拉起 WARDEN ✓）' -ForegroundColor DarkGray
}
