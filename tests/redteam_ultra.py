# -*- coding: utf-8 -*-
"""
红队 Ultra —— 极端的对抗手段

★ 立此文件的理由（用户 2026-10-09）：
    "敌人不会睡觉，他们会用各种方式绕过我们的限制"

★ 和第一版（redteam_simulator.py）的区别：
    第一版模拟的是【普通流氓软件的恶劣行为】✗
    这一版模拟的是【专门针对 WARDEN 的绕过手段】✗✗✗

★ 场景清单（只列针对性的）：
    U1  隐藏对抗        ★★★ 被隐藏后立刻 Show 回来 —— 直接打废"隐藏"策略
    U2  显示振荡        高频 Show/Hide，让可见性统计失真
    U3  多进程守护      A 被杀，B 立刻重启 A（真正的守护进程）
    U4  伪装系统进程名   进程名报成 svchost.exe 的样子
    U5  伪装系统窗口类   类名注册成 #32770（系统对话框类）
    U6  空标题          ★ 有窗口、有类名，但标题为空 → 绕过标题匹配
    U7  模仿正常程序     尺寸/位置/子控件都像正经软件
    U8  屏幕外弹窗       位置放到屏幕外，看得见枚举不到"位置"
    U9  全部规避行为分   不抢焦点、不置顶、有子控件、尺寸正常 → 分数压到最低
    U10 秒级重建        比 R2 更狠：每秒重建 20+ 个窗口

★ 安全：纯 Win32 窗口，不写注册表、不写启动项、不落盘、不联网。
      自清理，进程退出即结束。
"""
import argparse
import ctypes
import random
import string
import sys
import time
from ctypes import wintypes

sys.stdout.reconfigure(encoding='utf-8')
u = ctypes.windll.user32
k = ctypes.windll.kernel32

WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_longlong, wintypes.HWND, wintypes.UINT,
                             wintypes.WPARAM, wintypes.LPARAM)


class WNDCLASS(ctypes.Structure):
    _fields_ = [("style", wintypes.UINT), ("lpfnWndProc", WNDPROC),
                ("cbClsExtra", ctypes.c_int), ("cbWndExtra", ctypes.c_int),
                ("hInstance", wintypes.HINSTANCE), ("hIcon", wintypes.HICON),
                ("hCursor", wintypes.HANDLE), ("hbrBackground", wintypes.HBRUSH),
                ("lpszMenuName", wintypes.LPCWSTR), ("lpszClassName", wintypes.LPCWSTR)]


u.CreateWindowExW.restype = ctypes.c_void_p
u.CreateWindowExW.argtypes = [
    wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
    ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
    wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID]
u.DefWindowProcW.restype = ctypes.c_longlong
u.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
u.RegisterClassW.restype = wintypes.ATOM
u.RegisterClassW.argtypes = [ctypes.POINTER(WNDCLASS)]
u.IsWindow.argtypes = [wintypes.HWND]
u.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
u.DestroyWindow.argtypes = [wintypes.HWND]
u.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
u.SetForegroundWindow.argtypes = [wintypes.HWND]
u.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                           ctypes.c_int, ctypes.c_int, wintypes.UINT]
k.GetModuleHandleW.restype = ctypes.c_void_p

WS_OVERLAPPEDWINDOW = 0x00CF0000
WS_POPUP = 0x80000000
WS_CHILD = 0x40000000
WS_VISIBLE = 0x10000000
WS_EX_TOPMOST = 0x00000008
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_NOACTIVATE = 0x08000000
HWND_TOPMOST = -1
SWP_NOMOVE = 0x0002
SWP_NOSIZE = 0x0001
SWP_SHOWWINDOW = 0x0040
SWP_NOACTIVATE = 0x0010
WM_CLOSE = 0x0010
WM_QUIT = 0x0012
SW_HIDE = 0
SW_SHOW = 5
SW_SHOWNA = 8

# ★ U1/U2 用：是否启用"隐藏对抗"
FIGHT_HIDE = [False]


def _wndproc(hwnd, msg, wp, lp):
    if msg == WM_CLOSE:
        # ★ 有些场景连 WM_CLOSE 也不理（配合 U1）
        if FIGHT_HIDE[0]:
            return 0
        u.DestroyWindow(hwnd)
        return 0
    if msg == 120:   # WM_DESTROY
        return 0
    return u.DefWindowProcW(hwnd, msg, wp, lp)


_wndproc_ref = WNDPROC(_wndproc)


def rnd(n=8):
    return ''.join(random.choice(string.ascii_uppercase + string.digits) for _ in range(n))


class Ultra:
    def __init__(self):
        self.hwnds = []
        self.cls_n = 0

    def make_class(self, name=None):
        self.cls_n += 1
        cls = name or ('Ultra_%s_%d' % (rnd(6), self.cls_n))
        wc = WNDCLASS()
        wc.lpfnWndProc = _wndproc_ref
        wc.hInstance = k.GetModuleHandleW(None)
        wc.lpszClassName = cls
        if not u.RegisterClassW(ctypes.byref(wc)) and not name:
            return None
        return cls

    def create(self, title, w=320, h=140, x=None, y=None, ex=0, children=0):
        cls = self.make_class()
        if not cls:
            return None
        hinst = k.GetModuleHandleW(None)
        x = x if x is not None else random.randint(60, 800)
        y = y if y is not None else random.randint(60, 500)
        hwnd = u.CreateWindowExW(ex, cls, title, WS_OVERLAPPEDWINDOW,
                                 x, y, w, h, None, None, hinst, None)
        if not hwnd:
            return None
        for i in range(children):
            kind = "EDIT" if i == 0 else "STATIC"
            txt = "输入" if i == 0 else ("控件 %d" % i)
            u.CreateWindowExW(0, kind, txt, WS_CHILD | WS_VISIBLE,
                              8, 8 + i * 22, 200, 20, hwnd, None, hinst, None)
        u.ShowWindow(hwnd, SW_SHOW)
        self.hwnds.append(hwnd)
        return hwnd

    def prune(self):
        self.hwnds = [h for h in self.hwnds if u.IsWindow(h)]

    def count(self):
        self.prune()
        return len(self.hwnds)

    def close_all(self):
        FIGHT_HIDE[0] = False
        for h in list(self.hwnds):
            try:
                if u.IsWindow(h):
                    u.PostMessageW(h, WM_CLOSE, 0, 0)
            except Exception:
                pass
        self.hwnds = []


def pump(n=200):
    msg = wintypes.MSG()
    c = 0
    while c < n and u.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
        if msg.message == WM_QUIT:
            break
        u.TranslateMessage(ctypes.byref(msg))
        u.DispatchMessageW(ctypes.byref(msg))
        c += 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--scenario', default='U1')
    ap.add_argument('--duration', type=int, default=60)
    ap.add_argument('--tag', default='ULTRAPOP')
    args = ap.parse_args()

    scn = args.scenario.upper()
    tag = args.tag
    print('[Ultra] 场景=%s 时长=%ds 标记=%s' % (scn, args.duration, tag), flush=True)

    rt = Ultra()
    start = time.time()

    # ★ U1/U2 打开"隐藏对抗"
    if scn in ('U1', 'U2', 'U9'):
        FIGHT_HIDE[0] = True

    # ---------- 初始化 ----------
    if scn == 'U4':
        # ★ 伪装系统进程名：标题装作 svchost 的样子
        rt.create('%s|svchost.exe' % tag)
    elif scn == 'U5':
        # ★ 类名伪装成系统对话框类 #32770
        cls = rt.make_class('#32770')
        if cls:
            hinst = k.GetModuleHandleW(None)
            h = u.CreateWindowExW(0, cls, tag + '-sysclass', WS_OVERLAPPEDWINDOW,
                                  200, 200, 360, 160, None, None, hinst, None)
            u.ShowWindow(h, SW_SHOW)
            rt.hwnds.append(h)
    elif scn == 'U6':
        # ★ 空标题：有窗口、有类名，标题是空串
        rt.create('')
        rt.create('')
    elif scn == 'U8':
        # ★ 屏幕外：位置放到很远
        rt.create(tag + '-offscreen', x=6000, y=6000)
    elif scn == 'U9':
        # ★ 全力规避行为分：不抢焦点、不置顶、有输入框+多个子控件、尺寸正常
        rt.create(tag + '-stealth', w=560, h=380, children=6,
                  ex=WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)
    elif scn == 'U10':
        for i in range(20):
            rt.create('%s-%d' % (tag, i), x=20 + i * 5, y=20 + i * 5, w=180, h=90)
    else:
        rt.create('%s-%s' % (tag, scn))

    # ---------- 主循环 ----------
    n = 0
    while time.time() - start < args.duration:
        pump(150)
        n += 1
        rt.prune()

        try:
            if scn == 'U1':
                # ★★★ 隐藏对抗：每隔一小段，把所有窗口全部 Show 回来
                #     专打"隐藏窗口"这个核心手段 ✗
                for h in rt.hwnds:
                    if u.IsWindow(h):
                        u.ShowWindow(h, SW_SHOWNA)
                        u.ShowWindow(h, SW_SHOW)
                if rt.count() < 3:
                    rt.create('%s-%s' % (tag, rnd(6)))

            elif scn == 'U2':
                # ★ 显示振荡：50ms 一轮 Show/Hide
                for h in rt.hwnds:
                    if u.IsWindow(h):
                        u.ShowWindow(h, SW_HIDE)
                time.sleep(0.05)
                for h in rt.hwnds:
                    if u.IsWindow(h):
                        u.ShowWindow(h, SW_SHOW)
                time.sleep(0.05)

            elif scn == 'U3':
                # ★ 多进程守护由外层脚本负责（它负责重启本进程）
                if rt.count() < 2:
                    rt.create('%s-daemon-%d' % (tag, n))

            elif scn == 'U6':
                if rt.count() < 3:
                    rt.create('')

            elif scn == 'U7':
                if rt.count() < 2:
                    rt.create('%s-regular-app' % tag, w=640, h=420, children=7)

            elif scn == 'U8':
                if rt.count() < 2:
                    rt.create(tag + '-offscreen', x=7000, y=7000)

            elif scn == 'U9':
                if rt.count() < 2:
                    rt.create('%s-stealth-%d' % (tag, n), w=560, h=380,
                              children=6, ex=WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)

            elif scn == 'U10':
                # ★ 秒级重建：每次补 20 个
                if rt.count() < 40:
                    for i in range(20):
                        rt.create('%s-%s-%d' % (tag, rnd(4), i),
                                  x=random.randint(0, 900),
                                  y=random.randint(0, 600), w=160, h=80)

            else:
                if rt.count() < 1:
                    rt.create('%s-%s' % (tag, scn))

        except Exception as e:
            print('[Ultra] 异常: %s' % e, flush=True)

        time.sleep(0.01)

    print('[Ultra] 结束，剩余窗口=%d' % rt.count(), flush=True)
    rt.close_all()
    time.sleep(0.3)
    print('[Ultra] 退出', flush=True)


if __name__ == '__main__':
    main()
