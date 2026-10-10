# WARDEN 卸载脚本
#
# 【它做什么】
#     把 WARDEN 在你的电脑上留下的所有东西清干净 ✗
#
# 【为什么需要它】
#     WARDEN 会创建这些文件：
#       · 配置与日志（%LOCALAPPDATA%\WARDEN\）
#       · 可能还有开发期的旧目录（%LOCALAPPDATA%\dsh-tools\）
#       · 开机自启项（如果你手动加过）
#     自己手动找很容易漏 ✗
#     而"卸载不干净"正是流氓软件的通病 —— 我们不该犯同样的毛病 ✓
#
# 【用法】
#     powershell -ExecutionPolicy Bypass -File uninstall.ps1
#     powershell -ExecutionPolicy Bypass -File uninstall.ps1 -KeepConfig   保留配置与日志
#     powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Yes          不询问，直接删

param(
    [switch]$KeepConfig,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$toolDir = Join-Path $env:LOCALAPPDATA 'WARDEN'
$legacyDir = Join-Path $env:LOCALAPPDATA 'dsh-tools'

function Head($t) {
    Write-Host ''
    Write-Host ('=' * 64) -ForegroundColor Cyan
    Write-Host " $t" -ForegroundColor Cyan
    Write-Host ('=' * 64) -ForegroundColor Cyan
}

Head 'WARDEN 卸载'

# ---------------------------------------------------------------- 1. 停掉进程
Write-Host ''
Write-Host '[1] 停止正在运行的 WARDEN'
$procs = Get-Process -Name 'WARDEN', 'WARDEN-Settings' -ErrorAction SilentlyContinue
if ($procs) {
    # ★ 先让它优雅退出（写 .stop 文件），退不掉再强制 ✗
    if (Test-Path $toolDir) {
        Set-Content -Path (Join-Path $toolDir 'WARDEN.stop') -Value 'stop' -Encoding UTF8
        Start-Sleep -Seconds 2
    }
    foreach ($p in $procs) {
        try {
            if (-not $p.HasExited) { $p.Kill() }
            Write-Host "  ✓ 已停止 $($p.ProcessName) (PID $($p.Id))"
        }
        catch {
            Write-Host "  · $($p.ProcessName) 已经退出"
        }
    }
}
else {
    Write-Host '  · 没有在运行'
}

# ---------------------------------------------------------------- 2. 要删什么
Write-Host ''
Write-Host '[2] 将删除以下内容'
$targets = @()
if (-not $KeepConfig) {
    $targets += $toolDir
    $targets += $legacyDir
}
else {
    Write-Host '  · 配置与日志：保留（-KeepConfig）' -ForegroundColor Yellow
}
$targets += Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\WARDEN.lnk'

$existing = @()
foreach ($t in $targets) {
    if (Test-Path $t) {
        $existing += $t
        if (Test-Path $t -PathType Container) {
            $size = (Get-ChildItem $t -Recurse -File -ErrorAction SilentlyContinue |
                     Measure-Object -Property Length -Sum).Sum
            Write-Host ("  · {0}  ({1:N1} KB)" -f $t, ($size / 1KB))
        }
        else {
            Write-Host "  · $t"
        }
    }
}
if ($existing.Count -eq 0) {
    Write-Host '  · 没有找到需要删除的东西'
}

# ---------------------------------------------------------------- 3. 确认
if ($existing.Count -gt 0 -and -not $Yes) {
    Write-Host ''
    $ans = Read-Host '确认删除？输入 y 回车继续，其它任意键取消'
    if ($ans -ne 'y' -and $ans -ne 'Y') {
        Write-Host ''
        Write-Host '已取消，什么都没删 ✓'
        exit 0
    }
}

# ---------------------------------------------------------------- 4. 执行删除
Write-Host ''
Write-Host '[3] 删除'
foreach ($t in $existing) {
    try {
        # ★ 删之前把绝对路径打出来复核 ✗
        #   这是防止"计算路径算错了把别的东西删掉"✗
        $full = [System.IO.Path]::GetFullPath($t)
        if ($full -notlike "*$env:LOCALAPPDATA*" -and $full -notlike "*Startup*") {
            Write-Host "  ★ 跳过可疑路径: $full" -ForegroundColor Red
            continue
        }
        Remove-Item $full -Recurse -Force -ErrorAction Stop
        Write-Host "  ✓ 已删除 $full"
    }
    catch {
        Write-Host "  ★ 删除失败: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# ---------------------------------------------------------------- 5. 检查签名证书
Head '完成'
Write-Host ''
Write-Host '  说明：本脚本【不会】删除数字签名证书 ✓'
Write-Host '        它在 %LOCALAPPDATA%\dsh-secrets\ 下，可能被别的项目共用。'
Write-Host '        如果你确定不再需要它，可以手动删除：'
Write-Host '          · codesign.pfx   私钥（★ 删了就再也签不出同样的签名了）'
Write-Host '          · codesign.cer   公钥'
Write-Host ''
Write-Host '  证书还在"受信任的根证书颁发机构"里。若要一并移除：'
Write-Host '    1. Win+R 运行 certlm.msc'
Write-Host '    2. 受信任的根证书颁发机构 → 证书'
Write-Host '    3. 找到 CN=Firefly0619sec Code Signing，删除'
Write-Host ''
Write-Host '  ★ 程序本体（WARDEN.exe）需要你自己手动删除 ✓'
Write-Host ''
