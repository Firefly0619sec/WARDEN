# ============================================================================
#  发布前闸门（2026-10-11 信息安全体检新增 ✓）
#
#  【为什么要它】体检抓到两个「复制粘贴同源 bug」✗：
#    ① 同一段「写自检基线」的代码在 setup 与 updater 里各一份 ✗
#       我修了 setup 漏了 updater ✗ → 用户用更新器升级就会把配置写坏 ✗✗
#    ② 版本号散在 10 个文件里 ✗ 漏改一处就会出现"安装包写着 0.3.4、
#       程序自己报 0.3.3"这种让人没法核对来源的情况 ✓
#  → 所以把这两类"漏一处就出事"的检查【脚本化】✓ 发布前必须跑 ✓
#
#  【用法】pwsh -File tools\pre-release-check.ps1
#          退出码 0 = 可以发 ✓   非 0 = 不许发 ✗
# ============================================================================
$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
$fail = 0
function Bad($msg) { Write-Host ('  ✗ ' + $msg) -ForegroundColor Red; $script:fail++ }
function Ok($msg) { Write-Host ('  ✓ ' + $msg) -ForegroundColor Green }

Write-Host '══ 发布前闸门 ══' -ForegroundColor Cyan

# ---------------------------------------------------------------- ① 正则组引用
Write-Host ''
Write-Host '① 危险的正则替换写法（$1 后面直接接十六进制会变成不存在的组号 ✗）' -ForegroundColor Cyan
$hits = git grep -n -I -E 'Replace\([^)]*"\$[0-9]"\s*\+' -- '*.cs' 2>$null
if ($hits) {
    Bad '发现 "$N" + 变量 形式的替换串（哈希以数字开头就会拼成 $1<数字> ✗ 写坏配置）'
    $hits | ForEach-Object { Write-Host ('      ' + $_) -ForegroundColor DarkGray }
    Write-Host '      → 一律改成 ${1} / ${3} 加花括号 ✓ 并在替换后自检 ✓' -ForegroundColor Yellow
} else { Ok '没有这种写法 ✓（全部用 ${1} 花括号 ✓）' }

# ---------------------------------------------------------------- ② 自检兜底
Write-Host ''
Write-Host '② 写配置的地方有没有「替换后自检」兜底' -ForegroundColor Cyan
$writers = @(git grep -l -I 'expected_self_hash' -- '*.cs' 2>$null)
foreach ($w in $writers) {
    $t = Get-Content (Join-Path $repo $w) -Raw
    if ($t -match 'Regex\.IsMatch\(\s*$|Regex.IsMatch\(' -and $t -match 'expected_self_hash') { Ok ($w + ' 里有自检 ✓') }
    elseif ($t -match 'RefreshSelfBaseline') { Ok ($w + ' 只有刷新逻辑 ✓') }
}

# ---------------------------------------------------------------- ③ 版本一致
Write-Host ''
Write-Host '③ 产品版本号是否处处一致' -ForegroundColor Cyan
$want = ([regex]::Match((Get-Content 'src\WARDEN.csproj' -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value
$spots = @(
    @{ f = 'src\WARDEN.csproj'; p = '<Version>([^<]+)</Version>' },
    @{ f = 'src\Program.cs'; p = 'const string Version = "([^"]+)"' },
    @{ f = 'tools\cli\warden-cli.csproj'; p = '<Version>([^<]+)</Version>' },
    @{ f = 'tools\cli\Program.cs'; p = 'WARDEN ([0-9]+\.[0-9]+\.[0-9]+) \(AGPL' },
    @{ f = 'tools\help\HelpCore.cs'; p = 'Version = "([^"]+)"' },
    @{ f = 'tools\help\WARDEN-Help.csproj'; p = '<Version>([^<]+)</Version>' },
    @{ f = 'tools\online-setup\OnlineSetupWizard2.cs'; p = 'const string Version = "([^"]+)"' },
    @{ f = 'tools\online-setup\WARDEN-Online-Setup.csproj'; p = '<Version>([^<]+)</Version>' },
    @{ f = 'tools\redteam\WARDEN-RedTeam.csproj'; p = '<Version>([^<]+)</Version>' },
    @{ f = 'tools\setup\WARDEN-Setup.csproj'; p = '<Version>([^<]+)</Version>' }
)
foreach ($s in $spots) {
    if (-not (Test-Path $s.f)) { Bad ('缺文件 ' + $s.f); continue }
    $v = ([regex]::Match((Get-Content $s.f -Raw), $s.p)).Groups[1].Value
    if ($v -eq $want) { Ok ($s.f.PadRight(46) + $v) } else { Bad ($s.f.PadRight(46) + $v + '  ≠ ' + $want) }
}
Write-Host ('  产品版本 = ' + $want + '（四个 AOT 工具保留各自历史编号 ✓ 不在本检查范围 ✓）') -ForegroundColor DarkGray

# ---------------------------------------------------------------- ④ 敏感信息
Write-Host ''
Write-Host '④ 公开仓里有没有密钥/口令（会被全世界看到 ✗）' -ForegroundColor Cyan
$sec = git grep -n -I -E 'ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{20,}|BEGIN PGP PRIVATE KEY|AKIA[0-9A-Z]{16}|xox[baprs]-' -- . ':(exclude)tools/pre-release-check.ps1' 2>$null
if ($sec) { Bad '发现疑似密钥 ✗ 必须先在本地清掉并轮换 ✓'; $sec | Select-Object -First 5 | ForEach-Object { Write-Host ('      ' + $_) } }
else { Ok '没有高信号密钥 ✓' }
$paths = git grep -n -I -E 'C:\\Users\\[A-Za-z0-9_]+' -- '*.cs' '*.ps1' '*.py' 2>$null | Where-Object { $_ -notmatch 'Users\\%|Users\\Public' }
if ($paths) { Write-Host ('  · 提示：代码里有具体用户路径 ' + $paths.Count + ' 处（不算密钥 ✓ 但公开仓最好换成占位符 ✓）') -ForegroundColor Yellow }

# ---------------------------------------------------------------- ⑤ 载荷语言包
Write-Host ''
Write-Host '⑤ 语言包是否六份齐 + 键集合一致' -ForegroundColor Cyan
$langCount = (Get-ChildItem 'lang\*.txt' -ErrorAction SilentlyContinue | Measure-Object).Count
if ($langCount -ge 6) { Ok ('语言包 ' + $langCount + ' 个 ✓') } else { Bad ('语言包只有 ' + $langCount + ' 个 ✗') }

Pop-Location
Write-Host ''
if ($fail -gt 0) { Write-Host ('══ 闸门未通过：' + $fail + ' 处 ✗ 不许发布 ══') -ForegroundColor Red; exit 1 }
Write-Host '══ 闸门全过 ✓ 可以发布 ══' -ForegroundColor Green
exit 0
