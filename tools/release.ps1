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
#     pwsh -File tools\release.ps1 -Version 0.3.3 # 指定版本（会写回 csproj）
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
if ([string]::IsNullOrWhiteSpace($cur)) { $cur = '0.3.4' }
if ($Version -ne '' -and $Version -ne $cur) {
    Write-Host ('[发布] 版本 ' + $cur + ' → ' + $Version) -ForegroundColor Yellow
    $projText = [regex]::Replace($projText, '<Version>[^<]*</Version>', '<Version>' + $Version + '</Version>')
    Set-Content -Path $csproj -Value $projText -Encoding utf8 -NoNewline
    $cur = $Version
}
$ver = $cur
$outDir = Join-Path $repo ('dist\release\' + $ver)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ---------------------------------------------------------------- ③.5 语言包随包（2026-10-11 补）
# ★ 缺口：Lang.cs 从 <AppContext.BaseDirectory>\lang\ 读 .txt ✓ 但载荷里一直【没有 lang\ 目录】✗
#   → 六语言能力实际上从未随包发布 ✓（设置界面的语言切换等于只有内置 zh-Hans ✓）
# 本段：把 WARDEN\lang\*.txt 复制进 dist\app\lang\ ✓ 并先跑一致性校验当闸门 ✓
$langSrc = Join-Path $repo 'lang'
# ★★ 2026-10-11 修（真跑一次才发现的硬伤 ✗）：
#   这一段原来直接用 $appDir ✗ 而 $appDir 要到第 88 行（打安装包那段）才定义 ✗
#   → pwsh 的 Join-Path 收到 $null 直接抛「无法绑定到参数 Path」✗ 发布链在第 47 行就断 ✓
#   → 说明这段"补语言包"的代码从加进来那天起【从来没有真跑通过】✗（当时的"干跑"是假的 ✓）
#   修法：$appDir 提前定义 ✓
$appDir = Join-Path $repo 'dist\app'
$langDst = Join-Path $appDir 'lang'
if (-not (Test-Path $langSrc)) { throw ('[发布] 找不到语言包目录 ' + $langSrc + ' ✗ 中止（不发没有语言的包 ✓）') }

# 校验器在 DSH 工作区（与主仓解耦 ✓）；找不到就跳过校验但明确提示 ✓
$parity = Join-Path $env:USERPROFILE 'Documents\deepseek-harness\default-workspace\诊断\tools\lang-parity.ps1'
if (Test-Path $parity) {
    Write-Host '[发布] 语言包一致性校验…' -ForegroundColor Cyan
    & pwsh -NoProfile -File $parity -Dir $langSrc | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '[发布] 语言包校验未通过 ✗ 中止（改包只允许逐条改 ✓）' }
} else {
    Write-Host ('[发布] · 没找到校验器（' + $parity + '）→ 跳过校验，但请人工确认键集合一致 ✓') -ForegroundColor Yellow
}

New-Item -ItemType Directory -Path $langDst -Force | Out-Null
Copy-Item (Join-Path $langSrc '*.txt') $langDst -Force
$langCount = (Get-ChildItem $langDst -File -Filter '*.txt' | Measure-Object).Count
Write-Host ('[发布] 语言包（构建前先放一份）：' + $langCount + ' 个 → dist\app\lang\') -ForegroundColor DarkGray
if ($langCount -lt 6) { throw ('[发布] 语言包只有 ' + $langCount + ' 个（期望 6 个）✗ 中止') }
Write-Host ('[发布] 版本: ' + $ver + '   产物目录: ' + $outDir) -ForegroundColor Cyan

# ---------------------------------------------------------------- ② 全量构建
Write-Host '[发布] 全量构建（build.ps1）…' -ForegroundColor Cyan
& pwsh -NoProfile -File (Join-Path $repo 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw '[发布] 构建失败，中止' }

# ★ 2026-10-11 修：build.ps1 的 Step 5「打包到 dist\」会【清空 dist\】✗
#   于是它在 4.5 步组装好的 dist\app\ 会被一起删掉 ✓
#   → release.ps1 到这里打载荷时就找不到 dist\app\* ✗（0.3.3 卡在这里 ✓）
#   所以打包前【重新组装一次载荷】✓（脚本是幂等的 ✓ 只是再拷一遍 ✓）
Write-Host '[发布] 组装载荷（重新生成 dist\app）…' -ForegroundColor Cyan
& pwsh -NoProfile -File (Join-Path $repo 'tools\tools-build.ps1') | Out-Host
if ($LASTEXITCODE -ne 0) { throw '[发布] 载荷组装失败，中止（不发缺东西的包 ✓）' }

# ★ 2026-10-11 修：build.ps1 的 Step 5 清空 dist\ 时，把上面 ① 建的
#   dist\release\<版本>\ 目录也一起删了 ✗ → 后面 Copy-Item 报"找不到路径"✓
#   → 这里重建一次产物目录 ✓
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ★★ 2026-10-11 修（第二个硬伤 ✗）：语言包必须在【载荷重新组装之后】再复制一次 ✓
#   因为 build.ps1 的 Step 5 会清空 dist\ ✗ 前面放的那份 lang\ 会被一起抹掉 ✗
#   → 等于语言包从来没随包发布过 ✓（只修 $appDir 还是不对 ✓ 两个都得修 ✓）
$langDst = Join-Path $appDir 'lang'
New-Item -ItemType Directory -Path $langDst -Force | Out-Null
Copy-Item (Join-Path $langSrc '*.txt') $langDst -Force
$langCount2 = (Get-ChildItem $langDst -File -Filter '*.txt' | Measure-Object).Count
Write-Host ('[发布] 语言包已随包（组装后确认）：' + $langCount2 + ' 个 → dist\app\lang\') -ForegroundColor Green
if ($langCount2 -lt 6) { throw ('[发布] 语言包只有 ' + $langCount2 + ' 个（期望 6 个）✗ 中止') }
# ---------------------------------------------------------------- ③ 打安装包
Write-Host '[发布] 打安装包…' -ForegroundColor Cyan
$setupDir = Join-Path $repo 'tools\setup'
$payload = Join-Path $setupDir 'payload.zip'
Remove-Item $payload -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $payload -CompressionLevel Optimal -Force

# ---------------------------------------------------------------- ③.9 载荷闸门（2026-10-11 补）
# ★ 为什么要有：安装包里的东西【永远要拆开看一眼】✗
#   本次就抓到「语言包其实从没进过包」✗（见上面两处修复 ✓）
#   闸门三条：八个必需 exe 齐全 ✓ 语言包 ≥ 6 个 ✓ 每个 exe ≤ 8MB ✓ 不合格一律中止 ✓
Write-Host '[发布] 载荷闸门：拆开 payload.zip 逐条核对…' -ForegroundColor Cyan
$zip = [System.IO.Compression.ZipFile]::OpenRead($payload)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName })
    $needExe = @('WARDEN.exe', 'Settings.exe', 'Updater.exe', 'LogView.exe',
                 'Uninstaller.exe', 'Help.exe', 'warden-cli.exe', 'RedTeam.exe')
    $miss = @($needExe | Where-Object { $names -notcontains $_ })
    if ($miss.Count -gt 0) { throw ('[发布] 载荷里缺少必需 exe：' + ($miss -join ', ') + ' ✗ 中止') }
    $langs = @($names | Where-Object { $_ -like 'lang/*.txt' })
    if ($langs.Count -lt 6) { throw ('[发布] 载荷里语言包只有 ' + $langs.Count + ' 个 ✗ 中止（六语言等于没发 ✓）') }
    $big = @($zip.Entries | Where-Object { $_.FullName -like '*.exe' -and $_.Length -gt 8MB })
    if ($big.Count -gt 0) {
        throw ('[发布] 载荷里这些 exe 超过 8MB ✗ 中止：' + (($big | ForEach-Object { $_.FullName + '=' + [math]::Round($_.Length / 1MB, 2) + 'MB' }) -join ', '))
    }
    Write-Host ('    ✓ 必需 exe 8 个齐全 ✓ 语言包 ' + $langs.Count + ' 个 ✓ 每个 exe ≤ 8MB ✓（共 ' + $names.Count + ' 个条目）') -ForegroundColor Green
}
finally { $zip.Dispose() }

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
$rt = Join-Path $repo 'tools\redteam\bin\Release\net10.0-windows\win-x64\native\RedTeam.exe'
if (Test-Path $rt) { $items += @{ name = 'RedTeam.exe'; path = $rt } }

$sums = @()
foreach ($it in $items) {
    $h = (Get-FileHash $it.path -Algorithm SHA256).Hash.ToLower()
    Copy-Item $it.path (Join-Path $outDir $it.name) -Force
    $sums += ('{0}  {1}' -f $h, $it.name)
    Write-Host ('    ✓ ' + $it.name.PadRight(22) + '{0:N1} MB  {1}' -f ((Get-Item $it.path).Length / 1MB), $h.Substring(0, 16)) -ForegroundColor Green
}
Set-Content -Path (Join-Path $outDir 'SHA256SUMS.txt') -Value ($sums -join "`n") -Encoding utf8

# ---------------------------------------------------------------- ④.5 GPG 签名（来源认证 ✓ 2026-10-11 信息安全体检补）
# ★ 为什么必须有：光有 SHA256SUMS.txt 等于没认证 ✗
#   能改下载页的人（或 GitHub 账号被盗 ✗）可以【同时换掉 exe 和校验和】✗ 用户怎么对都对不上 ✓
#   → 用发布者的 GPG 私钥签一份 SHA256SUMS.txt.asc ✓ 用户拿公钥指纹一验就知道是不是真货 ✓
# ★ 签名失败一律【中止发布】✗ 绝不发一个"说是签了其实没签"的版本 ✓
$fp = 'EBFB6B895D8EEC8F59CE0A26E23876CBBDAE330F'     # 与提交签名同一把钥匙 ✓
$sumFile = Join-Path $outDir 'SHA256SUMS.txt'
$ascFile = Join-Path $outDir 'SHA256SUMS.txt.asc'
Remove-Item $ascFile -Force -ErrorAction SilentlyContinue
Write-Host '[发布] GPG 签名 SHA256SUMS.txt…' -ForegroundColor Cyan
$gpg = (Get-Command gpg -ErrorAction SilentlyContinue)
if (-not $gpg) { throw '[发布] 找不到 gpg ✗ 中止（不发没有来源认证的包 ✓）' }
# 口令走 gpg-agent（保险库预置过 ✓）——不落命令行 ✗ 不进环境变量 ✗
& gpg --batch --yes --armor --local-user $fp --detach-sign --output $ascFile $sumFile 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $ascFile)) {
    throw '[发布] GPG 签名失败 ✗ 中止（先跑 诊断\tools\gpg_passvault.py --preset 把口令预置进 agent ✓）'
}
& gpg --verify $ascFile $sumFile 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw '[发布] 签名自校验没过 ✗ 中止' }
Write-Host ('    ✓ SHA256SUMS.txt.asc 已生成并自校验通过 ✓ 指纹 ' + $fp.Substring(0, 16) + '…') -ForegroundColor Green
Write-Host ''
Write-Host '★ 首次发布前记得跑一次闸门（防同源 bug 再犯 ✓）：pwsh -File tools\pre-release-check.ps1' -ForegroundColor DarkGray

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
    '',
    '## 怎么验证来源（★ 别只看哈希 ✗ 哈希本身也可能被一起换掉 ✓）',
    '',
    '1. 导入发布者公钥（README 里也有同一份指纹 ✓）：',
    '   ```',
    '   gpg --recv-keys ' + $fp,
    '   ```',
    '2. 校验校验和文件【本身】的签名（这一步才是关键 ✓）：',
    '   ```',
    '   gpg --verify SHA256SUMS.txt.asc SHA256SUMS.txt',
    '   ```',
    '3. 再对文件算哈希，和 SHA256SUMS.txt 里的逐条比：',
    '   ```',
    '   sha256sum -c SHA256SUMS.txt        # Linux / Git Bash',
    '   Get-FileHash WARDEN-Setup.exe -Algorithm SHA256   # Windows PowerShell',
    '   ```',
    '4. 签名在这里 → `SHA256SUMS.txt.asc` ✓（发布者指纹：`' + $fp + '`）',
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
