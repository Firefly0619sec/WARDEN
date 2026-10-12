# ============================================================================
#  各工具编译 + 载荷组装（2026-10-11 补写）
#
#  【为什么要写它】build.ps1 里【没有】编译各工具的步骤 ✗ 也不组装 dist\app ✗
#    （它的注释提到过"事故后源码丢失"，看起来连这段构建逻辑也一起丢了 ✓）
#    结果 release.ps1 打载荷时找不到 dist\app\* → 发布链直接断掉 ✗
#  【它做什么】
#    ① 依次编译 7 个工具（updater / logview / uninstaller / help / cli / redteam / online-setup）
#    ② 把产物 + 图标 + 文档组装成 dist\app\（= 安装包载荷的原料 ✓）
#  【安全】只写 dist\ 与各工具的 bin/obj ✓ 绝不删源码与 .git ✓
#  【失败处理】任何一个工具编不过就【明确报错并退出】✗ 不静默跳过 ✓
# ============================================================================

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot | Split-Path -Parent      # tools\ 的上一级 = 仓库根
if (-not (Test-Path (Join-Path $root 'src'))) { $root = Split-Path $PSScriptRoot -Parent }

$distApp = Join-Path $root 'dist\app'
$dist    = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $distApp -Force | Out-Null

Write-Host ''
Write-Host ('=' * 70) -ForegroundColor Cyan
Write-Host ' 各工具编译 + 载荷组装' -ForegroundColor Cyan
Write-Host ('=' * 70) -ForegroundColor Cyan

# 工具清单：目录 → 产物 exe 名
$tools = [ordered]@{
    'updater'      = 'Updater.exe'
    'logview'      = 'LogView.exe'
    'uninstaller'  = 'Uninstaller.exe'
    'help'         = 'Help.exe'
    'cli'          = 'warden-cli.exe'
    'redteam'      = 'RedTeam.exe'
    'online-setup' = 'WARDEN-Online-Setup.exe'
}

$failed = @()
foreach ($k in $tools.Keys) {
    $dir = Join-Path $root ('tools\' + $k)
    $exeName = $tools[$k]
    if (-not (Test-Path $dir)) { Write-Host ('  ★ 缺少目录 tools\' + $k) -ForegroundColor Red; $failed += $k; continue }
    Write-Host ('  · 编译 tools\' + $k + ' → ' + $exeName) -ForegroundColor DarkGray
    Push-Location $dir
    $out = & dotnet publish -c Release -r win-x64 --nologo -v q 2>&1
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) {
        Write-Host ('    ★ 编译失败：tools\' + $k) -ForegroundColor Red
        ($out | Select-String 'error' | Select-Object -First 4) | ForEach-Object { Write-Host ('      ' + $_.Line.Trim()) -ForegroundColor Red }
        $failed += $k
        continue
    }
    # ★ 2026-10-11 修（Help 缺陷的根因之一）：不能用"最新时间"随便挑 ✗
    #   开了 PublishAot 的工具，产物在 bin\Release\...\win-x64\native\ ✓
    #   而没开的工具（或历史残留）在 publish\ ✓ 后者可能是"多文件应用的空壳 exe"✗
    #   （Help 就栽在这里：publish\Help.exe 只有 174 KB，真代码在同目录 219 个 dll 里 ✓）
    $isAot = $false
    $cp = Get-ChildItem $dir -File -Filter '*.csproj' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cp) { $isAot = ((Get-Content $cp.FullName -Raw) -match '<PublishAot>true</PublishAot>') }
    $found = $null
    if ($isAot) {
        $found = Get-ChildItem (Join-Path $dir 'bin\Release') -Recurse -File -Filter $exeName -ErrorAction SilentlyContinue |
                 Where-Object { $_.DirectoryName -match '\\native$' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $found) {
        $found = Get-ChildItem (Join-Path $dir 'bin\Release') -Recurse -File -Filter $exeName -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $found) { Write-Host ('    ★ 编译成功但找不到产物 ' + $exeName) -ForegroundColor Red; $failed += $k; continue }
    $sizeMb = [math]::Round($found.Length / 1MB, 2)
    $from = if ($found.DirectoryName -match '\\native$') { 'native(AOT)' } else { 'publish' }
    Copy-Item $found.FullName (Join-Path $distApp $exeName) -Force
    Write-Host ('    ✓ ' + $exeName + '  ' + $sizeMb + ' MB   来源=' + $from) -ForegroundColor Green
    # ★ 硬闸门：AOT 工具的体积必须小；出现几十 MB 说明挑到了自包含版或空壳 ✗
    if ($isAot -and $found.Length -gt 8MB) {
        Write-Host ('    ★ ' + $exeName + ' 声明了 PublishAot 却有 ' + $sizeMb + ' MB ✗ 可能是空壳或多文件应用 → 拒绝打包 ✓') -ForegroundColor Red
        $failed += $k
    }
}

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host ('  ★ 有 ' + $failed.Count + ' 个工具没编译成功：' + ($failed -join ', ')) -ForegroundColor Red
    Write-Host '    载荷不完整 ✗ 不继续组装 ✓（避免发出缺东西的安装包 ✓）' -ForegroundColor Red
    exit 1
}

# 主程序（build.ps1 的 Step 1 产物）也要进载荷 ✓
$mainExe = Join-Path $root 'src\bin\Release\net10.0-windows\win-x64\publish\WARDEN.exe'
if (Test-Path $mainExe) {
    Copy-Item $mainExe (Join-Path $distApp 'WARDEN.exe') -Force
    Write-Host ('  ✓ WARDEN.exe  ' + [math]::Round((Get-Item $mainExe).Length / 1MB, 2) + ' MB') -ForegroundColor Green
    if ((Get-Item $mainExe).Length -gt 8MB) { Write-Host '  ★ WARDEN.exe 应当 < 8MB（AOT ✓）却更大 ✗ 拒绝打包 ✓' -ForegroundColor Red; exit 1 }
} else {
    Write-Host '  ★ 找不到主程序产物 WARDEN.exe ✗（先跑 build.ps1 的 Step 1）' -ForegroundColor Red
    exit 1
}

# ★ 2026-10-11 补：设置界面由 build.ps1 的 Step 2 编译（不在本脚本的工具清单里 ✓）
#   上一次就是因为漏了它 → 自检报"缺少必需文件 Settings.exe" → 构建被正确拦下 ✓
$setExe = Join-Path $root 'tools\settings\bin\Release\net10.0-windows\win-x64\publish\Settings.exe'
if (Test-Path $setExe) {
    Copy-Item $setExe (Join-Path $distApp 'Settings.exe') -Force
    Write-Host ('  ✓ Settings.exe  ' + [math]::Round((Get-Item $setExe).Length / 1MB, 2) + ' MB') -ForegroundColor Green
} else {
    Write-Host '  ★ 找不到 Settings.exe ✗（先跑 build.ps1 的 Step 2）' -ForegroundColor Red
    exit 1
}

# 图标（三个状态各一份 ✓ 名字就是 0.2.5 载荷里的名字 ✓）
foreach ($ico in @('icon.ico', 'icon-paused.ico', 'icon-emergency.ico')) {
    $src = Join-Path $dist $ico
    if (Test-Path $src) { Copy-Item $src (Join-Path $distApp $ico) -Force; Write-Host ('  ✓ ' + $ico) -ForegroundColor Green }
    else { Write-Host ('  · 图标缺失（跳过）：' + $ico) -ForegroundColor Yellow }
}

# 文档
foreach ($doc in @('README.md', 'README.en.md', 'CHANGELOG.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $src = Join-Path $dist $doc
    if (Test-Path $src) { Copy-Item $src (Join-Path $distApp $doc) -Force; Write-Host ('  ✓ ' + $doc) -ForegroundColor Green }
    else { Write-Host ('  · 文档缺失（跳过）：' + $doc) -ForegroundColor Yellow }
}

# 组装报告（★ 一眼能看出载荷齐不齐 ✓）
Write-Host ''
Write-Host '  载荷 dist\app\ 内容：' -ForegroundColor Cyan
$items = Get-ChildItem $distApp -File | Sort-Object Name
$total = 0
foreach ($i in $items) { $total += $i.Length; Write-Host ('    ' + $i.Name.PadRight(30) + ([math]::Round($i.Length / 1MB, 2)).ToString().PadLeft(8) + ' MB') }
Write-Host ('    ' + ('合计'.PadRight(30)) + ([math]::Round($total / 1MB, 1)).ToString().PadLeft(8) + ' MB') -ForegroundColor Cyan

$needExe = @('WARDEN.exe', 'Settings.exe', 'Updater.exe', 'LogView.exe', 'Uninstaller.exe', 'Help.exe', 'warden-cli.exe', 'RedTeam.exe')
$missing = $needExe | Where-Object { -not (Test-Path (Join-Path $distApp $_)) }
if ($missing.Count -gt 0) {
    Write-Host ('  ★ 载荷里缺少必需文件：' + ($missing -join ', ')) -ForegroundColor Red
    exit 1
}
Write-Host '  ✓ 八个必需 exe 齐全 ✓ 载荷可以打包 ✓' -ForegroundColor Green
