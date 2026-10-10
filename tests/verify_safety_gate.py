# -*- coding: utf-8 -*-
"""
★ 强杀安全闸验证 —— 这是最重要的一项测试

目的：确认那道"防把系统搞崩"的闸真的拦得住。
     ★★ 全程【不真的杀任何进程】——
        只做静态判定：把危险进程名/路径喂给安全闸，看它答不答应。
        这样即使判定逻辑写错了，也不会造成任何破坏 ✓

验证三类：
    A 系统关键进程名        → 必须全部拒绝
    B 系统目录下的路径      → 必须拒绝
    C 正常第三方程序        → 应该放行
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import pathlib
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8')
WS = pathlib.Path(r'C:\Users\wei20\Documents\deepseek-harness\default-workspace')

# 从 Core.cs 里把硬黑名单抠出来（保证测的就是真实代码里的那份）
core = (WS / '诊断' / 'csharp' / 'WARDEN' / 'Core.cs').read_text(encoding='utf-8')
import re
m = re.search(r'HardBlock = new\(StringComparer\.OrdinalIgnoreCase\)\s*\{(.*?)\};',
              core, re.S)
block = re.findall(r'"([^"]+)"', m.group(1)) if m else []
m2 = re.search(r'SystemDirMarkers\s*=\s*\{(.*?)\};', core, re.S)
markers = re.findall(r'@"([^"]+)"', m2.group(1)) if m2 else []

print('=' * 96)
print('① 从真实源码里读出来的安全闸定义')
print('=' * 96)
print('  硬黑名单条目数 : %d' % len(block))
print('  系统目录标记数 : %d' % len(markers))
print('  名单前 12 个    : %s' % ', '.join(block[:12]))
print('  目录标记        : %s' % ', '.join(markers))

# 用 Python 复刻同一套判定逻辑（逐字对照 Core.cs 的 MayTerminate）
def may_terminate(proc, image_path):
    if not proc:
        return False, '进程名为空'
    if proc.lower() in [b.lower() for b in block]:
        return False, '在硬黑名单里'
    if image_path:
        low = image_path.replace('/', '\\').lower()
        for mk in markers:
            if mk.lower() in low:
                return False, '映像在系统目录(%s)' % mk.strip('\\')
    return True, '允许'

print()
print('=' * 96)
print('② A 组：系统关键进程 —— 必须【全部拒绝】')
print('=' * 96)
DANGEROUS = [
    ('csrss.exe',      r'C:\Windows\System32\csrss.exe'),
    ('wininit.exe',    r'C:\Windows\System32\wininit.exe'),
    ('winlogon.exe',   r'C:\Windows\System32\winlogon.exe'),
    ('services.exe',   r'C:\Windows\System32\services.exe'),
    ('lsass.exe',      r'C:\Windows\System32\lsass.exe'),
    ('smss.exe',       r'C:\Windows\System32\smss.exe'),
    ('svchost.exe',    r'C:\Windows\System32\svchost.exe'),
    ('dwm.exe',        r'C:\Windows\System32\dwm.exe'),
    ('explorer.exe',   r'C:\Windows\explorer.exe'),
    ('System',         ''),
    ('Registry',       ''),
    ('MemCompression', ''),
    ('fontdrvhost.exe', r'C:\Windows\System32\fontdrvhost.exe'),
    ('ctfmon.exe',     r'C:\Windows\System32\ctfmon.exe'),
    ('sihost.exe',     r'C:\Windows\System32\sihost.exe'),
    ('taskhostw.exe',  r'C:\Windows\System32\taskhostw.exe'),
    ('audiodg.exe',    r'C:\Windows\System32\audiodg.exe'),
    ('spoolsv.exe',    r'C:\Windows\System32\spoolsv.exe'),
    ('MsMpEng.exe',    r'C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe'),
    ('SearchIndexer.exe', r'C:\Windows\System32\SearchIndexer.exe'),
]
a_pass = 0
a_fail = []
for proc, path in DANGEROUS:
    ok, why = may_terminate(proc, path)
    mark = '✓ 拒绝' if not ok else '★★ 竟然放行 ✗✗✗'
    if not ok:
        a_pass += 1
    else:
        a_fail.append(proc)
    print('  %-22s %-58s %s' % (proc, why[:56], mark))
print()
print('  A 组结果: %d/%d 被正确拒绝  %s'
      % (a_pass, len(DANGEROUS), '✓ 全部通过' if not a_fail else '★ 漏网: ' + ', '.join(a_fail)))

print()
print('=' * 96)
print('③ B 组：系统目录路径 —— 即使进程名伪装成正常，也必须拒绝')
print('=' * 96)
B_CASES = [
    ('svchost.exe',      r'C:\Windows\System32\svchost.exe'),
    ('unknown.exe',      r'C:\Windows\System32\some.dll.exe'),
    ('helper.exe',       r'C:\Windows\SysWOW64\helper.exe'),
    ('app.exe',          r'C:\Windows\WinSxS\app.exe'),
    ('svc.exe',          r'C:\Windows\Servicing\svc.exe'),
    ('x.exe',            r'C:\Windows\SystemApps\x.exe'),
    ('nvidia.exe',       r'C:\Windows\system32\nvidia.exe'),
]
b_pass = 0
for proc, path in B_CASES:
    ok, why = may_terminate(proc, path)
    if not ok:
        b_pass += 1
    print('  %-18s %-52s %s' % (proc, path[12:60], '✓ 拒绝' if not ok else '★ 放行 ✗'))
print()
print('  B 组结果: %d/%d 被拒绝  %s'
      % (b_pass, len(B_CASES), '✓ 全部通过' if b_pass == len(B_CASES) else '★ 有漏网'))

print()
print('=' * 96)
print('④ C 组：正常第三方程序 —— 应该【放行】（否则工具没法干活）')
print('=' * 96)
C_CASES = [
    ('toutiao.exe',    r'C:\Users\wei20\AppData\Local\SomeAd\toutiao.exe'),
    ('adpopup.exe',    r'C:\Program Files (x86)\Adware\adpopup.exe'),
    ('news.exe',       r'C:\Program Files\NewsPop\news.exe'),
    ('helper.exe',     r'D:\Apps\Tool\helper.exe'),
    ('python.exe',     r'C:\Users\wei20\.dsh\dsh-runtimes\python\python.exe'),
]
c_pass = 0
for proc, path in C_CASES:
    ok, why = may_terminate(proc, path)
    if ok:
        c_pass += 1
    print('  %-18s %-56s %s' % (proc, path[:54], '✓ 放行' if ok else '★ 被拒: ' + why))
print()
print('  C 组结果: %d/%d 被放行（这些才是该杀的）' % (c_pass, len(C_CASES)))

print()
print('=' * 96)
print('⑤ 汇总')
print('=' * 96)
print('  A 系统进程  : %d/%d 拒绝  %s' % (a_pass, len(DANGEROUS),
                                        '✓' if a_pass == len(DANGEROUS) else '★'))
print('  B 系统目录  : %d/%d 拒绝  %s' % (b_pass, len(B_CASES),
                                        '✓' if b_pass == len(B_CASES) else '★'))
print('  C 正常程序  : %d/%d 放行  %s' % (c_pass, len(C_CASES),
                                        '✓' if c_pass == len(C_CASES) else '★'))
ok_all = (a_pass == len(DANGEROUS) and b_pass == len(B_CASES)
          and c_pass == len(C_CASES))
print()
print('  ★ 结论: %s' % ('安全闸判定正确 —— 系统进程拦得住，流氓软件放得过 ✓✓✓'
                        if ok_all else '★ 有需要修正的地方 ✗'))
print()
print('  ★★ 说明：本测试【全程没有真的杀任何进程】——')
print('      只用源码里那份名单做纯静态判定，所以即使逻辑写错也不会造成破坏。')
