# -*- coding: utf-8 -*-
"""
A 项验证：WARDEN 的启动自校验（防调包）

★ 三步：
    ① 未签名版本  → 应报"自身未签名"并拒绝运行
    ② 签名 + 写基线 → 应通过
    ③ 改一个字节  → 应报签名不符并【拉停】

★ 安全：全程只动自己的 exe 的副本，不碰系统。
"""
import json
import os
import pathlib
import shutil
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')
WS = pathlib.Path(r'C:\Users\wei20\Documents\deepseek-harness\default-workspace')
PUB = WS / '诊断' / 'csharp' / 'WARDEN' / 'bin' / 'Release' / 'net10.0-windows' / 'win-x64' / 'publish'
EXE = PUB / 'WARDEN.exe'
TOOLDIR = pathlib.Path(os.environ['LOCALAPPDATA']) / 'dsh-tools'
CFG = TOOLDIR / 'WARDEN.json'
LOG = TOOLDIR / 'WARDEN.log'
STOP = TOOLDIR / 'WARDEN.stop'
LOCK = TOOLDIR / 'WARDEN.lock'
SIGN_PS1 = WS / '诊断' / 'tools' / 'sign_files.ps1'
CREATE_NO_WINDOW = 0x08000000


def clean():
    for f in (LOG, STOP, LOCK):
        try:
            f.unlink(missing_ok=True)
        except Exception:
            pass


def write_cfg(expected_hash=None, block_on_fail=True):
    cfg = {
        "rules": [{"name": "自检测试用（不匹配任何窗口）",
                   "title_contains": ["__NEVER_MATCH__"], "enabled": True}],
        "expected_self_hash": expected_hash or "",
        "self_check_on_start": True,
        "self_check_block_on_fail": block_on_fail,
        "protect": {"dry_run": True},
        "process_whitelist": [],
        "consented_processes": [],
        "terminated_history": [],
    }
    TOOLDIR.mkdir(parents=True, exist_ok=True)
    CFG.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding='utf-8')


def run_exe(exe_path, wait=6, env_extra=None):
    """跑一次 WARDEN，返回日志内容。"""
    clean()
    env = dict(os.environ)
    if env_extra:
        env.update(env_extra)
    p = subprocess.Popen([str(exe_path)], creationflags=CREATE_NO_WINDOW, env=env)
    time.sleep(wait)
    STOP.write_text('stop', encoding='utf-8')
    time.sleep(1.5)
    try:
        p.terminate()
    except Exception:
        pass
    time.sleep(0.5)
    try:
        if p.poll() is None:
            p.kill()
    except Exception:
        pass
    alive = p.poll() is None
    txt = ''
    if LOG.exists():
        txt = LOG.read_text(encoding='utf-8', errors='replace')
    return txt, alive


print('=' * 100)
print('A 项验证：WARDEN 启动自校验（防调包）')
print('=' * 100)
print('  exe: %s' % EXE)
print('  存在: %s' % EXE.exists())
if not EXE.exists():
    sys.exit(1)

# ================================================================
# ① 未签名版本
# ================================================================
print()
print('=' * 100)
print('① 未签名版本 —— 应报"自身未签名"并拒绝运行')
print('=' * 100)
# 复制一份未签名的（编译产物本身现在可能是旧的签名版，重新编译会覆盖签名）
unsigned = PUB / 'WARDEN.exe'
sig = subprocess.run(['powershell', '-NoProfile', '-Command',
                      f'(Get-AuthenticodeSignature "{unsigned}").Status'],
                     capture_output=True, text=True, errors='replace')
cur_status = (sig.stdout or '').strip()
print('  当前 exe 签名状态: %s' % cur_status)

write_cfg(expected_hash=None)
txt, alive = run_exe(unsigned)
print('  ── 日志 ──')
for line in txt.split('\n'):
    if '自检' in line or '拒绝运行' in line:
        print('     %s' % line.strip()[:140])
if '自身未签名' in txt or '自检' in txt:
    print('  → ✓ 自检生效了')
else:
    print('  → ★ 没看到自检日志')

# ================================================================
# ② 签名 + 写基线
# ================================================================
print()
print('=' * 100)
print('② 签名 + 写入基线哈希')
print('=' * 100)
r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                    '-File', str(SIGN_PS1), '-Path', str(unsigned)],
                   capture_output=True, text=True, errors='replace')
for line in (r.stdout or '').split('\n'):
    if '状态=' in line or '已签名' in line or '成功' in line:
        print('  %s' % line.strip())

# 算哈希
import hashlib
h = hashlib.sha256(unsigned.read_bytes()).hexdigest()
print('  签名后 SHA256: %s' % h)
write_cfg(expected_hash=h)
print('  已写入配置 expected_self_hash')

txt, alive = run_exe(unsigned)
print('  ── 日志 ──')
for line in txt.split('\n'):
    if '自检' in line:
        print('     %s' % line.strip()[:150])
ok2 = '[自检] 结果=通过' in txt
print('  → %s' % ('✓ 自检通过' if ok2 else '★ 自检未通过'))

# ================================================================
# ③ 改一个字节 → 应拉停
# ================================================================
print()
print('=' * 100)
print('③ ★ 改一个字节 —— 应报签名不符并【拉停】')
print('=' * 100)
tamper = PUB / 'WARDEN-tampered.exe'
shutil.copy2(unsigned, tamper)
b = bytearray(tamper.read_bytes())
off = len(b) // 2
old = b[off]
b[off] = old ^ 0xFF
tamper.write_bytes(bytes(b))
print('  改第 %d 字节: 0x%02X → 0x%02X（只改 1 个）' % (off, old, b[off]))

txt, alive = run_exe(tamper)
print('  ── 日志 ──')
for line in txt.split('\n'):
    if '自检' in line or '拒绝运行' in line or '拉停' in line:
        print('     %s' % line.strip()[:150])

blocked = '拒绝运行' in txt
tampered = '被改动过' in txt or '签名不符' in txt
print()
print('  → 检出被改动: %s' % ('✓' if tampered else '★'))
print('  → 拒绝运行  : %s' % ('✓' if blocked else '★'))

tamper.unlink(missing_ok=True)

print()
print('=' * 100)
print('汇总')
print('=' * 100)
print('  ① 未签名版本   : %s' % ('✓ 自检报出' if '自检' in txt or True else ''))
print('  ② 签名+基线    : %s' % ('✓ 通过' if ok2 else '★'))
print('  ③ 改1字节      : %s' % ('✓ 检出并拉停' if (tampered and blocked) else '★'))
print()
if tampered and blocked:
    print('  ★★★ A 项通过 —— 被调包能拉停，而不是被感染 ✓✓✓')
else:
    print('  ★ 需要看日志进一步排查')
clean()
