# ============================================================================
#  B12 发布自动化（WARDEN）
# ============================================================================
#  一条命令把"能发出去的东西"全部做出来：
#    ① 版本号校验/写入（可选 -Version）
#    ② 全量构建（build.ps1：主程序 + 各工具 + dist\app）
#    ③ 打安装包（payload.zip + WARDEN-Setup.exe）
#    ④ 计算 SHA256（主程序 + 安装包）→ 写入 SHA256SUMS.txt ✓
#    ⑤ 生成发布说明草稿（RELEASE-<版本>.md，含校验值和文件清单）
#    ⑥ 全部产物归到 dist\release\<版本>\
#
#  ★ 安全：全程只写 dist\ 与快照目录；不删源码、不删 .git ✓
#  ★ 不联网：不推送、不调 GitHub（推送是 F3，等仓库建好再做 ✓）
#
#  用法：
#     pwsh -File tools\release.ps1                       # 用现有版本号
#     pwsh -File tools\release.ps1 -Version 0.2.1 # 指定版本（会写回 csproj）
# ============================================================================
param(
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Write-Host ('[发布] 仓库: ' + $repo) -ForegroundColor Cyan

# ---------------------------------------------------------------- ① 版本号
$csproj = Join-Path $repo 'src\WARDEN.csproj'
$projText = Get-Content $csproj -Raw
$cur = [regex]::Match($projText, '<Version>([^<]+)</Version>').Groups[1].Value
if ([string]::IsNullOrWhiteSpace($cur)) { $cur = '0.2.1' }
if ($Version -ne '' -and $Version -ne $cur) {
    Write-Host ('[发布] 版本 ' + $cur + ' → ' + $Version) -ForegroundColor Yellow
    $projText = [regex]::Replace($projText, '<Version>[^<]*</Version>', '<Version>' + $Version + '</Version>')
    Set-Content -Path $csproj -Value $projText -Encoding utf8 -NoNewline
    $cur = $Version
}
$ver = $cur
$outDir = Join-Path $repo ('dist\release\' + $ver)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Write-Host ('[发布] 版本: ' + $ver + '   产物目录: ' + $outDir) -ForegroundColor Cyan

# ---------------------------------------------------------------- ② 全量构建
Write-Host '[发布] 全量构建（build.ps1）…' -ForegroundColor Cyan
& pwsh -NoProfile -File (Join-Path $repo 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw '[发布] 构建失败，中止' }

# ---------------------------------------------------------------- ③ 打安装包
Write-Host '[发布] 打安装包…' -ForegroundColor Cyan
$setupDir = Join-Path $repo 'tools\setup'
$appDir = Join-Path $repo 'dist\app'
$payload = Join-Path $setupDir 'payload.zip'
Remove-Item $payload -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $payload -CompressionLevel Optimal -Force
Push-Location $setupDir
& dotnet publish -c Release -r win-x64 --nologo -v q | Out-Null
Pop-Location
$setupExe = Join-Path $setupDir 'bin\Release\net10.0-windows\win-x64\publish\WARDEN-Setup.exe'
if (-not (Test-Path $setupExe)) { throw '[发布] 安装包没编出来' }

# ---------------------------------------------------------------- ④ 收集产物 + 校验值
Write-Host '[发布] 收集产物并计算校验值…' -ForegroundColor Cyan
$items = @()
$mainExe = Join-Path $appDir 'WARDEN.exe'
if (Test-Path $mainExe) { $items += @{ name = 'WARDEN.exe'; path = $mainExe } }
$items += @{ name = 'WARDEN-Setup.exe'; path = $setupExe }
$cli = Join-Path $repo 'tools\cli\bin\Release\net10.0-windows\win-x64\native\warden-cli.exe'
if (Test-Path $cli) { $items += @{ name = 'warden-cli.exe'; path = $cli } }
$rt = Join-Path $repo 'tools\redteam\bin\Release\net10.0-windows\win-x64\native\WARDEN-RedTeam.exe'
if (Test-Path $rt) { $items += @{ name = 'WARDEN-RedTeam.exe'; path = $rt } }

$sums = @()
foreach ($it in $items) {
    $h = (Get-FileHash $it.path -Algorithm SHA256).Hash.ToLower()
    Copy-Item $it.path (Join-Path $outDir $it.name) -Force
    $sums += ('{0}  {1}' -f $h, $it.name)
    Write-Host ('    ✓ ' + $it.name.PadRight(22) + '{0:N1} MB  {1}' -f ((Get-Item $it.path).Length / 1MB), $h.Substring(0, 16)) -ForegroundColor Green
}
Set-Content -Path (Join-Path $outDir 'SHA256SUMS.txt') -Value ($sums -join "`n") -Encoding utf8

# ---------------------------------------------------------------- ⑤ 发布说明草稿
$notes = @(
    '# WARDEN ' + $ver,
    '',
    '> 这份草稿由 tools\release.ps1 自动生成 ✓ 发之前自己读一遍、补上"这次改了什么" ✓',
    '',
    '## 文件与校验值（SHA256）',
    '',
    '```',
    ($sums -join "`n"),
    '```',
    '',
    '## 怎么装',
    '',
    '1. 把 `WARDEN-Setup.exe` 拷到目标机器（离线也能装 ✓）',
    '2. 双击 → 按向导走（默认装在 `%LOCALAPPDATA%\Programs\WARDEN`，不需要管理员权限 ✓）',
    '3. 想让人自己验证来源：对照上面的 SHA256 ✓',
    '',
    '## 许可证',
    '',
    'AGPL-3.0-or-later —— 注意：**把 WARDEN 做成在线服务也要开源** ✓',
    '',
    '## 这次改了什么',
    '',
    '（待填）'
)
Set-Content -Path (Join-Path $outDir ('RELEASE-' + $ver + '.md')) -Value ($notes -join "`n") -Encoding utf8

Write-Host ''
Write-Host ('[发布] 完成 ✓  产物在: ' + $outDir) -ForegroundColor Green
Get-ChildItem $outDir | ForEach-Object { '    ' + $_.Name.PadRight(30) + ('{0:N1} MB' -f ($_.Length / 1MB)) }
Write-Host ''
Write-Host '★ 本脚本不联网、不推送 ✓ 推送与发版是 F3（等仓库建好）' -ForegroundColor DarkGray
