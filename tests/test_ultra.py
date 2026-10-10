# -*- coding: utf-8 -*-
"""
红队 Ultra 全流程测试 —— 极端对抗 + 完整决策流程

★ 和之前的区别：
    之前测的是"能不能拦住" ✗
    这次测的是：
      · 极端绕过手段下，工具还能不能拦住
      · ★★ 复活场景：强杀后它又回来，弹窗推荐是否变成「永久允许强杀」
      · ★ 全流程：第一段 → 拒绝隐藏 → 第二段 → 授权 → 强杀 → 复活 → 再弹

★ 场景（Ultra）：
    U1  隐藏对抗     ★★★ 被隐藏后立刻 Show 回来
    U2  显示振荡
    U4  伪装系统进程名
    U5  伪装系统窗口类
    U6  空标题
    U7  模仿正常程序
    U8  屏幕外弹窗
    U9  全力规避行为分
    U10 秒级重建

★ 安全：DSH_SANDBOX_PIDS 限制强杀范围；只操作带标记的窗口。
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import pathlib
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')
WS = pathlib.Path(r'C:\Users\wei20\Documents\deepseek-harness\default-workspace')
PY = sys.executable
LAB = WS / '诊断' / 'sandbox' / 'popup-lab'
ULTRA = LAB / 'redteam_ultra.py'
GUARD = (WS / '诊断' / 'csharp' / 'WARDEN' / 'bin' / 'Release'
         / 'net10.0-windows' / 'win-x64' / 'publish' / 'WARDEN.exe')
TOOLDIR = pathlib.Path(os.environ['LOCALAPPDATA']) / 'dsh-tools'
CFG = TOOLDIR / 'WARDEN.json'
LOG = TOOLDIR / 'WARDEN.log'
STOP = TOOLDIR / 'WARDEN.stop'
LOCK = TOOLDIR / 'WARDEN.lock'
CREATE_NO_WINDOW = 0x08000000

u = ctypes.windll.user32
u.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
u.IsWindow.argtypes = [wt.HWND]
u.IsWindowVisible.argtypes = [wt.HWND]


def tagged(tag, visible_only=True, include_empty_title=False):
    out = []
    P = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, _):
        if visible_only and not u.IsWindowVisible(h):
            return True
        b = ctypes.create_unicode_buffer(512)
        u.GetWindowTextW(h, b, 512)
        t = b.value
        if tag in t or (include_empty_title and t == ''):
            out.append(h)
        return True

    u.EnumWindows(P(cb), 0)
    return out


def count(tag):
    return len(tagged(tag))


def kill_tagged(tag):
    for h in tagged(tag, visible_only=False):
        try:
            u.PostMessageW(h, 0x0010, 0, 0)
        except Exception:
            pass


def clean_state():
    for f in (STOP, LOCK, LOG):
        try:
            f.unlink(missing_ok=True)
        except Exception:
            pass


def write_cfg(tag='ULTRAPOP', consent_after=2, revived=False):
    cfg = {
        "rules": [{"name": "Ultra测试", "title_contains": [tag], "enabled": True}],
        "behavior": {
            "rule_hit": 40, "steals_focus": 12, "topmost": 10, "no_edit": 8,
            "no_children": 6, "respawn": 15, "small_floating": 8,
            "title_churn": 10, "apply_threshold": 20,
            "terminate_threshold": 40,
        },
        "action": {
            "allow_terminate": True,
            "l1_wait_ms": 200, "l2_wait_ms": 300,
            "failures_before_kill": 1, "deadline_ms": 2000,
            "honor_signatures": True, "honor_whitelist_for_kill": True,
            "allow_consent_prompt": True,
            "consent_after_failures": consent_after,
            "second_stage_min_hides": 3,
            "second_stage_max_wait_ms": 4000,
            "second_stage_delay_ms": 0,
        },
        "protect": {
            "skip_if_foreground": False, "skip_if_has_edit": False,
            "skip_if_has_child_window": False, "dry_run": False,
        },
        "process_whitelist": [],
        "consented_processes": [],
        # ★★ 关键：预置"强杀历史"，用来触发【复活】判定 ✓
        "terminated_history": ["python.exe"] if revived else [],
    }
    TOOLDIR.mkdir(parents=True, exist_ok=True)
    CFG.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding='utf-8')


def run_scenario(scn, duration=18, observe=14):
    """跑一个 Ultra 场景，观察拦截效果。"""
    print()
    print('=' * 100)
    print('■ %s' % scn)
    print('=' * 100)
    clean_state()
    write_cfg(revived=False)

    rt = subprocess.Popen([PY, '-X', 'utf8', str(ULTRA), '--scenario', scn,
                           '--duration', str(duration * 4), '--tag', 'ULTRAPOP'],
                          creationflags=CREATE_NO_WINDOW,
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(3)
    base = count('ULTRAPOP')
    print('  红队已起，可见窗口 = %d' % base)

    env = dict(os.environ)
    sandbox = {rt.pid}
    try:
        r = subprocess.run(['powershell', '-NoProfile', '-Command',
                            f'(Get-CimInstance Win32_Process -Filter "ParentProcessId={rt.pid}").ProcessId'],
                           capture_output=True, text=True, errors='replace', timeout=20)
        for line in (r.stdout or '').split('\n'):
            if line.strip().isdigit():
                sandbox.add(int(line.strip()))
    except Exception:
        pass
    env['DSH_SANDBOX_PIDS'] = ','.join(str(x) for x in sorted(sandbox))

    guard = subprocess.Popen([str(GUARD)], creationflags=CREATE_NO_WINDOW, env=env)

    samples = []
    t0 = time.time()
    while time.time() - t0 < observe:
        samples.append(count('ULTRAPOP'))
        time.sleep(0.5)
    end = count('ULTRAPOP')

    STOP.write_text('stop', encoding='utf-8')
    time.sleep(1.0)
    for p in (guard, rt):
        try:
            p.terminate()
        except Exception:
            pass
    time.sleep(0.4)
    for p in (guard, rt):
        try:
            if p.poll() is None:
                p.kill()
        except Exception:
            pass
    kill_tagged('ULTRAPOP')

    hid = l3 = refused = 0
    if LOG.exists():
        t = LOG.read_text(encoding='utf-8', errors='replace')
        hid = t.count('[L2 隐藏+关闭]')
        l3 = t.count('[L3 强杀]')
        refused = t.count('拒绝强杀')

    ok = end <= max(1, base // 3)
    print('  可见窗口: 起始=%d 结束=%d' % (base, end))
    print('  处置: 隐藏=%d  ★L3强杀=%d  被拒=%d' % (hid, l3, refused))
    print('  → %s' % ('✓ 压制住' if ok else '★ 未压住'))
    return ok


def test_revival():
    """
    ★★★ 复活场景全流程测试（用户 2026-10-09 的核心设计）
        预置 terminated_history=["python.exe"]
        → 工具应当判定为"复活"
        → 弹窗推荐应变成「永久允许强杀」
        → 标题应变成"这个程序被强杀后又回来了"
    """
    print()
    print('=' * 100)
    print('★★★ 复活场景全流程（重点）')
    print('=' * 100)
    clean_state()
    write_cfg(revived=True)
    print('  已预置 terminated_history=["python.exe"]（模拟"之前强杀过它"）')

    rt = subprocess.Popen([PY, '-X', 'utf8', str(ULTRA), '--scenario', 'U1',
                           '--duration', '100', '--tag', 'ULTRAPOP'],
                          creationflags=CREATE_NO_WINDOW,
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(3)
    env = dict(os.environ)
    env['DSH_SANDBOX_PIDS'] = str(rt.pid)
    guard = subprocess.Popen([str(GUARD)], creationflags=CREATE_NO_WINDOW, env=env)

    print('  等待弹窗（观察标题是否变成"强杀后又回来了"）…')
    seen_title = None
    for i in range(40):
        time.sleep(1)
        wins = tagged('ULTRAPOP')
        # 找所有标题含 WARDEN 的
        P = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
        titles = []

        def cb(h, _):
            b = ctypes.create_unicode_buffer(512)
            u.GetWindowTextW(h, b, 512)
            if 'WARDEN' in b.value:
                titles.append(b.value)
            return True
        u.EnumWindows(P(cb), 0)
        if titles:
            seen_title = titles[0]
            print('  +%2ds  ★ 弹窗出现: 「%s」' % (i + 1, seen_title[:60]))
            break

    # 让弹窗超时（无人点）→ 走"继续隐藏"
    print('  （不点它，等它超时，验证超时路径）')
    time.sleep(20)

    STOP.write_text('stop', encoding='utf-8')
    time.sleep(1.0)
    for p in (guard, rt):
        try:
            p.terminate()
        except Exception:
            pass
    time.sleep(0.4)
    for p in (guard, rt):
        try:
            if p.poll() is None:
                p.kill()
        except Exception:
            pass
    kill_tagged('ULTRAPOP')

    print()
    print('  ── 日志关键行 ──')
    revived_detected = False
    if LOG.exists():
        for line in LOG.read_text(encoding='utf-8', errors='replace').split('\n'):
            if any(k in line for k in ('复活', '弹窗]', '授权', '继续隐藏', '强杀')):
                print('     %s' % line.strip()[:140])
                if '复活' in line:
                    revived_detected = True
    ok = revived_detected or (seen_title is not None and '回来' in (seen_title or ''))
    print()
    print('  → 复活判定: %s' % ('✓ 触发了（推荐应已改为永久强杀）' if ok else '★ 未触发'))
    return ok


if __name__ == '__main__':
    print('防御方: %s' % GUARD.name)
    print('红队  : %s' % ULTRA.name)
    if not GUARD.exists() or not ULTRA.exists():
        print('★ 缺少文件')
        sys.exit(1)

    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument('--only', default='', help='只跑某一部分: scenarios / revival')
    a = ap.parse_args()

    results = {}
    if a.only != 'revival':
        for scn in ['U1', 'U2', 'U4', 'U5', 'U6', 'U7', 'U8', 'U9', 'U10']:
            try:
                results[scn] = run_scenario(scn)
            except Exception as e:
                print('  ★ 场景异常: %s' % str(e)[:120])
    if a.only != 'scenarios':
        results['REVIVAL'] = test_revival()

    print()
    print('=' * 100)
    print('汇总')
    print('=' * 100)
    for k, v in results.items():
        print('  %-10s %s' % (k, '✓ 通过' if v else '★ 未通过'))
    clean_state()
