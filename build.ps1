# ============================================================================
#  ★★★ 安全闸（事故后新增，2026-10-10）：
#    之前发生过"仓库被整空"的事故 ✗ 从此构建脚本必须：
#      ① 先打一份快照（只增不删）
#      ② 只允许删除 dist\ / bin\ / obj\ 三处，绝不碰源码与 .git
# ---- 安全闸：构建前把【源码】备一份（2026-10-11 重做 ✓）
#   原来：把【整个仓库】压成一个 zip ✗ 含 bin/obj/dist/publish ✓
#        实测每份 1.5 GB ✗ 一天构建 6 次 = 白吃 9 GB 磁盘 ✓
#   现在：只备源码（排构建产物）+ 只留最近 5 份 + 5 分钟内不重复备 ✓
$RepoRoot = $PSScriptRoot
$snap = Join-Path $env:USERPROFILE 'Documents\WARDEN-快照'
New-Item -ItemType Directory -Path $snap -Force | Out-Null
$last = Get-ChildItem $snap -File -Filter 'WARDEN-*-构建前.zip' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($last -and ((Get-Date) - $last.LastWriteTime).TotalMinutes -lt 5) {
    Write-Host '[安全闸] 5 分钟内已备过，跳过（省磁盘 ✓）' -ForegroundColor DarkGray
} else {
    $z = Join-Path $snap ('WARDEN-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-构建前.zip')
    $stage = Join-Path $env:TEMP ('warden_src_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $exD = @('bin', 'obj', 'publish', 'dist', 'release', '.vs', 'TestResults', '__pycache__')
    $exF = @('*.exe', '*.dll', '*.pdb', '*.zip', '*.7z', '*.nupkg', '*.lib', '*.exp')
    $rc = @($RepoRoot, $stage, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1')
    foreach ($x in $exD) { $rc += '/XD'; $rc += $x }
    foreach ($x in $exF) { $rc += '/XF'; $rc += $x }
    & robocopy @rc | Out-Null
    Compress-Archive -Path "$stage\*" -DestinationPath $z -CompressionLevel Optimal -Force
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    $mb = [math]::Round((Get-Item $z).Length / 1MB, 1)
    Write-Host ('[安全闸] 源码快照: ' + (Split-Path $z -Leaf) + '  ' + $mb + ' MB ✓') -ForegroundColor DarkGray
    Get-ChildItem $snap -File -Filter 'WARDEN-*-构建前.zip' | Sort-Object LastWriteTime -Descending |
        Select-Object -Skip 5 | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
}
function Assert-SafeDelete([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $allowed = @('dist', 'bin', 'obj')
    $leaf = Split-Path $full -Leaf
    if ($allowed -notcontains $leaf) {
        throw ('[安全闸] 拒绝对「' + $full + '」执行删除 —— 只允许删 dist/bin/obj ✗')
    }
    if (-not $full.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ('[安全闸] 拒绝删除仓库以外的路径：' + $full)
    }
}
# WARDEN 构建脚本
#
# 【一条命令完成】编译 → 签名 → 更新自校验基线
#
# 【为什么需要它】
#     手动做这些很容易漏步骤 ✗
#     尤其是【签名之后必须更新基线哈希】——
#       忘了更新，程序下次启动就会因为"哈希与基线不符"拒绝运行 ✗
#     而这恰恰是自校验在正常工作，不是 bug ✓
#
# 【用法】
#     powershell -ExecutionPolicy Bypass -File build.ps1
#     powershell -ExecutionPolicy Bypass -File build.ps1 -SkipSign     只编译不签名
#     powershell -ExecutionPolicy Bypass -File build.ps1 -NoBuild      只签名不编译


# ---------------------------------------------------------------------------
# ★ 安全网（事故后新增 2026-10-10）：
#   有些工具的源码在"仓库被清空"事故里丢了 ✗（settings/logview/updater/uninstaller）
#   → 这里先查一遍，缺源码的就跳过并明确提示，绝不让整个构建崩掉 ✓
# ---------------------------------------------------------------------------
$toolDirs = @('settings','logview','updater','uninstaller','help','setup')
foreach ($td in $toolDirs) {
    $tp = Join-Path $PSScriptRoot ("tools\" + $td)
    if (-not (Test-Path $tp)) { Write-Host ("  · tools\" + $td + " 不存在，跳过") -ForegroundColor DarkGray; continue }
    $proj = Get-ChildItem $tp -Filter '*.csproj' -ErrorAction SilentlyContinue | Select-Object -First 1
    $cs   = Get-ChildItem $tp -Filter '*.cs' -ErrorAction SilentlyContinue
    if (-not $proj -or $cs.Count -eq 0) {
        Write-Host ("  ★ tools\" + $td + " 源码缺失，跳过（事故遗留，见 README 的恢复说明）") -ForegroundColor Yellow
        continue
    }
    Write-Host ("  ✓ tools\" + $td + " 源码在（" + $cs.Count + " 个 .cs）") -ForegroundColor DarkGray
}
param(
    [switch]$SkipSign,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$setDir = Join-Path $root 'tools\settings'
$outDir = Join-Path $srcDir 'bin\Release\net10.0-windows\win-x64\publish'
$mainExe = Join-Path $outDir 'WARDEN.exe'
$signPs1 = Join-Path (Split-Path (Split-Path $root -Parent) -Parent) 'tools\sign_files.ps1'
$toolDir = Join-Path $env:LOCALAPPDATA 'WARDEN'
$cfgPath = Join-Path $toolDir 'WARDEN.json'

function Head($t) {
    Write-Host ''
    Write-Host ('=' * 70) -ForegroundColor Cyan
    Write-Host " $t" -ForegroundColor Cyan
    Write-Host ('=' * 70) -ForegroundColor Cyan
}

function Step($n, $t) {
    Write-Host ''
    Write-Host "[$n] $t" -ForegroundColor Yellow
}

$t0 = Get-Date

# ---------------------------------------------------------------- 1. 编译主程序
if (-not $NoBuild) {
    Head 'WARDEN 构建'
    Step 1 '编译主程序（Native AOT）'
    Push-Location $srcDir
    $buildOut = & dotnet publish -c Release -r win-x64 --nologo 2>&1
    $code = $LASTEXITCODE
    Pop-Location
    $errs = $buildOut | Select-String -Pattern 'error CS|error IL| error '
    if ($errs) {
        Write-Host '  ★ 编译报错：' -ForegroundColor Red
        $errs | Select-Object -First 10 | ForEach-Object { Write-Host "    $_" }
        exit 1
    }
    if ($code -ne 0) {
        Write-Host "  ★ 编译失败，退出码 $code" -ForegroundColor Red
        exit 1
    }
    if (-not (Test-Path $mainExe)) {
        Write-Host "  ★ 找不到产物 $mainExe" -ForegroundColor Red
        exit 1
    }
    $kb = (Get-Item $mainExe).Length / 1KB
    Write-Host ("  ✓ 编译成功  WARDEN.exe  {0:N1} KB" -f $kb) -ForegroundColor Green

    # ------------------------------------------------------------ 2. 编译设置界面
    Step 2 '编译设置界面'
    Push-Location $setDir
    $setOut = & dotnet publish -c Release --nologo 2>&1
    $c2 = $LASTEXITCODE
    Pop-Location
    $e2 = $setOut | Select-String -Pattern 'error CS'
    if ($e2 -or $c2 -ne 0) {
        Write-Host '  ★ 设置界面编译失败：' -ForegroundColor Red
        $e2 | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" }
        exit 1
    }
    Write-Host '  ✓ 设置界面编译成功' -ForegroundColor Green
}
else {
    Head 'WARDEN 构建（跳过编译）'
}

# ---------------------------------------------------------------- 3. 签名
if (-not $SkipSign) {
    Step 3 '给主程序签名'
    if (-not (Test-Path $signPs1)) {
        Write-Host "  · 找不到签名脚本 $signPs1 —— 跳过" -ForegroundColor DarkYellow
        Write-Host '    （如果你只想本地测试，可以加 -SkipSign）'
    }
    else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signPs1 -Path $mainExe 2>&1 |
            Select-String -Pattern '已签名|状态=|成功|失败' | ForEach-Object {
                Write-Host "    $($_.Line.Trim())"
            }

        # ------------------------------------------------------------ 4. 更新基线
        Step 4 '更新自校验基线哈希'
        $hash = (Get-FileHash $mainExe -Algorithm SHA256).Hash.ToLower()
        Write-Host "    SHA256: $hash"

        if (Test-Path $cfgPath) {
            $json = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $json | Add-Member -NotePropertyName expected_self_hash -NotePropertyValue $hash -Force
            if ($null -eq $json.self_check_on_start) {
                $json | Add-Member -NotePropertyName self_check_on_start -NotePropertyValue $true -Force
            }
            if ($null -eq $json.self_check_block_on_fail) {
                $json | Add-Member -NotePropertyName self_check_block_on_fail -NotePropertyValue $true -Force
            }
            ($json | ConvertTo-Json -Depth 10) | Set-Content $cfgPath -Encoding UTF8
            Write-Host '    ✓ 基线已写入配置' -ForegroundColor Green
        }
        else {
            Write-Host "    · 配置还不存在（首次运行 WARDEN 时会生成）" -ForegroundColor DarkYellow
            Write-Host "      下次运行后请再执行一次本脚本，或用设置界面点「用当前哈希填入」"
        }
    }
}
else {
    Step 3 '跳过签名（-SkipSign）'
}

# ---------------------------------------------------------------- 5. 打包到 dist
#
# ★★★ 为什么必须打包（用户 2026-10-10 反馈）
#
#     编译产物在两个不同的地方：
#       src\bin\...\publish\WARDEN.exe             ← 主程序
#       tools\settings\bin\...\Settings.exe ← 设置界面
#
#     而主程序的托盘菜单里"打开设置界面"是去【自己所在目录】找 ✗
#     → 找不到 → 只能退而打开文件资源管理器 ✗
#     用户看到的就是"点设置却弹出了资源管理器"✗
#
#     解法：把发布需要的东西全部收拢到 dist\ ✓
#       用户在 dist\ 里双击 WARDEN.exe，
#       点托盘菜单就能正确拉起设置界面 ✓
Step 5 '打包到 dist\'

$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist -Force | Out-Null

# ① 主程序 + 图标
if (Test-Path $mainExe) { Copy-Item $mainExe $dist -Force }
$icoSrc = Join-Path $srcDir 'icon.ico'
if (Test-Path $icoSrc) { Copy-Item $icoSrc $dist -Force }
  # ★ 暂停/紧急两种状态的图标是运行时按【文件路径】加载的 ✗ 必须一起进 dist ✓
  foreach ($extra in @('icon.ico', 'icon-paused.ico', 'icon-emergency.ico')) {
      $p2 = Join-Path $PSScriptRoot $extra
      if (Test-Path $p2) { Copy-Item $p2 $dist -Force }
  }

# ② 设置界面
#
# ★★★ 这里踩过一个大坑（2026-10-10）：
#
#     bin\Release 下【有两个】Settings.exe ✗
#       ...\win-x64\Settings.exe            158.5 KB ← 框架依赖 ✗
#       ...\win-x64\publish\Settings.exe     47 MB    ← 自包含单文件 ✓
#
#     原来用 Sort-Object -Descending 挑，挑到了【框架依赖】那个 ✗
#     单把它复制到 dist\ → 找不到依赖 → 启动即崩 ✗
#     表面现象是"点设置没反应"，实际是进程启动就失败了 ✗
#     （排查过程：日志显示"[托盘] 已启动设置界面: ...dist\Settings.exe"
#       路径完全正确，但进程不存在 → 才想到是启动失败 ✗）
#
#     → 改成【明确优先 publish 目录】✓ 并按大小兜底 ✓
$setExe = $null
$setPublish = Join-Path $setDir 'bin\Release\net10.0-windows\win-x64\publish\Settings.exe'
if (Test-Path $setPublish) {
    $setExe = Get-Item $setPublish
    Write-Host '    （取自 publish 目录）' -ForegroundColor DarkGray
}
else {
    # 兜底：全局找，但【按文件大小取最大】
    # 自包含的那个一定大得多 ✓ 这个判据比按名字排序可靠 ✗
    $setExe = Get-ChildItem (Join-Path $setDir 'bin\Release') -Recurse `
                -Filter 'Settings.exe' -ErrorAction SilentlyContinue |
              Sort-Object Length -Descending | Select-Object -First 1
    if ($setExe) {
        Write-Host '    （按大小兜底找到的）' -ForegroundColor DarkGray
    }
}

if ($setExe) {
    Copy-Item $setExe.FullName $dist -Force
    Write-Host ("    ✓ Settings.exe  {0:N1} MB" -f ($setExe.Length / 1MB)) -ForegroundColor Green
    if ($setExe.Length -lt 5MB) {
        Write-Host '    ★★ 警告：这个设置界面只有几百 KB，很可能是【框架依赖】版 ✗' -ForegroundColor Yellow
        Write-Host '        复制到 dist\ 后会因为找不到依赖而启动失败 ✗' -ForegroundColor Yellow
    }
}
else {
    Write-Host '    ★ 找不到 Settings.exe —— 设置界面将无法启动' -ForegroundColor Yellow
}

# ③ 说明文件
foreach ($doc in @('README.md', 'README.en.md', 'LICENSE', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md')) {
    $p = Join-Path $root $doc
    if (Test-Path $p) { Copy-Item $p $dist -Force }
}

# ④ 公钥证书（供用户验证发布物）
$cert = Join-Path $root 'certs\Firefly0619sec-codesign.cer'
if (Test-Path $cert) {
    # ★ 第三方声明目录也要随包（.NET 是 MIT，要求随分发保留声明）
if (Test-Path (Join-Path $PSScriptRoot 'docs\第三方声明')) {
    $td = Join-Path $dist '第三方声明'
    New-Item -ItemType Directory -Path $td -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'docs\第三方声明\*') $td -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path (Join-Path $dist 'certs') -Force | Out-Null
    Copy-Item $cert (Join-Path $dist 'certs') -Force
}

Write-Host ''
Write-Host '    dist\ 内容：' -ForegroundColor Cyan
Get-ChildItem $dist -File | ForEach-Object {
    Write-Host ("      {0,-26} {1,9:N1} KB" -f $_.Name, ($_.Length / 1KB))
}

# ---------------------------------------------------------------- 6. 摘要
Head '完成'
$elapsed = ((Get-Date) - $t0).TotalSeconds
Write-Host ("  用时: {0:N1} 秒" -f $elapsed)
if (Test-Path $mainExe) {
    $f = Get-Item $mainExe
    Write-Host ("  产物: {0}" -f $f.FullName)
    Write-Host ("  大小: {0:N1} KB   修改于 {1}" -f ($f.Length / 1KB), $f.LastWriteTime.ToString('HH:mm:ss'))
    $sig = Get-AuthenticodeSignature $mainExe
    $color = if ($sig.Status -eq 'Valid') { 'Green' } else { 'Yellow' }
    Write-Host ("  签名: {0}" -f $sig.Status) -ForegroundColor $color
    if ($sig.SignerCertificate) {
        Write-Host ("        签名者 {0}" -f $sig.SignerCertificate.Subject)
    }
}
Write-Host ''
Write-Host '  ★ 下一步：启动设置界面，点「启动 WARDEN」' -ForegroundColor Yellow
Write-Host ''
