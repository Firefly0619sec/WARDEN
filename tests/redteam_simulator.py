# -*- coding: utf-8 -*-
"""
红队弹窗模拟器 —— 站在"流氓软件作者"的角度攻击 WARDEN

★ 设计原则：不模拟"普通弹窗"，只模拟【专门对抗清理工具】的弹窗。
  每一个 R 场景都对应一条针对性的绕过思路，目的是把防御打穿。

R 场景清单：
    R1  标题/类名高频轮换          打规则匹配
    R2  疯狂重建（关一个开两个）     打处置速度
    R3  吃掉 WM_CLOSE              打 L1/L2 关闭请求
    R4  ★ 伪装带输入框              打 skip_if_has_edit 保护
    R5  ★ 伪装带子控件              打 skip_if_has_child_window 保护
    R6  ★ 同进程开"正常窗口"         打强杀闸⑤（阻止强杀）
    R7  ★ WS_EX_NOACTIVATE + 不抢焦点 打行为分（降分规避门槛）
    R8  抢占前台 + 强制置顶          打前台保护
    R9  守护进程（被杀后重启）        打单进程强杀
    R10 瞬间开大量窗口               打性能
    R11 挂起消息循环（无响应）        打 SendMessageTimeout
    R12 进程名伪装成 svchost.exe      打名单匹配
    R13 极端尺寸（1x1 / 全屏）        打尺寸判定
    R14 不可见但有标题的窗口          打可见性判定

★ 安全：全部是纯 Win32 窗口，不写注册表、不写启动项、不落盘、不联网。
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

# ---- Win32 声明：restype 和 argtypes 都要 ----
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
u.IsWindow.restype = wintypes.BOOL
u.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
u.DestroyWindow.argtypes = [wintypes.HWND]
u.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
u.SetForegroundWindow.argtypes = [wintypes.HWND]
u.SetForegroundWindow.restype = wintypes.BOOL
u.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                           ctypes.c_int, ctypes.c_int, wintypes.UINT]
u.SetWindowPos.restype = wintypes.BOOL
k.GetModuleHandleW.restype = ctypes.c_void_p
k.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]

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
WM_CLOSE = 0x0010
WM_DESTROY = 0x0002
WM_QUIT = 0x0012

# ★ 全局开关：R3 场景下窗口过程会【吃掉 WM_CLOSE】
SWALLOW_CLOSE = [False]
# ★ 全局开关：R11 场景下窗口过程装死
GO_HUNG = [False]
_wndproc_ref = None


def _wndproc(hwnd, msg, wp, lp):
    if msg == WM_CLOSE:
        if SWALLOW_CLOSE[0]:
            # ★★ 红队核心手法之一：假装没听见，什么都不做
            #    这样 PostMessage/SendMessage 全都无效，只能靠强杀
            return 0
        u.DestroyWindow(hwnd)
        return 0
    if msg == WM_DESTROY:
        return 0
    return u.DefWindowProcW(hwnd, msg, wp, lp)


_wndproc_ref = WNDPROC(_wndproc)


def rnd(n=8):
    return ''.join(random.choice(string.ascii_uppercase + string.digits) for _ in range(n))


class RedTeam:
    def __init__(self):
        self.hwnds = []
        self.cls_n = 0

    def make_class(self):
        self.cls_n += 1
        name = 'RT_%s_%d' % (rnd(6), self.cls_n)
        wc = WNDCLASS()
        wc.lpfnWndProc = _wndproc_ref
        wc.hInstance = k.GetModuleHandleW(None)
        wc.lpszClassName = name
        if not u.RegisterClassW(ctypes.byref(wc)):
            return None
        return name

    def create(self, title, ex=0, style=WS_OVERLAPPEDWINDOW,
               w=320, h=140, x=None, y=None, children=0, visible=True,
               topmost=False):
        cls = self.make_class()
        if not cls:
            return None
        if topmost:
            ex |= WS_EX_TOPMOST
        hinst = k.GetModuleHandleW(None)
        x = x if x is not None else random.randint(60, 800)
        y = y if y is not None else random.randint(60, 500)
        hwnd = u.CreateWindowExW(ex, cls, title, style, x, y, w, h,
                                 None, None, hinst, None)
        if not hwnd:
            return None

        # ★ R4：塞一个输入框 → 让清理工具的 skip_if_has_edit 保护生效
        # ★ R5：塞多个子控件 → 让 skip_if_has_child_window 生效
        # ★ R6：塞足够多的"正常"控件 → 让"看起来像正常窗口"成立
        for i in range(children):
            if i == 0:
                u.CreateWindowExW(0, "EDIT", "伪装输入框",
                                  WS_CHILD | WS_VISIBLE, 8, 8, 200, 22,
                                  hwnd, None, hinst, None)
            else:
                u.CreateWindowExW(0, "STATIC", "正常控件 %d" % i,
                                  WS_CHILD | WS_VISIBLE, 8, 8 + i * 20, 180, 18,
                                  hwnd, None, hinst, None)

        if visible:
            u.ShowWindow(hwnd, 5)
        self.hwnds.append(hwnd)
        return hwnd

    def prune(self):
        self.hwnds = [h for h in self.hwnds if u.IsWindow(h)]

    def count(self):
        self.prune()
        return len(self.hwnds)

    def close_all(self, force=True):
        SWALLOW_CLOSE[0] = False
        for h in list(self.hwnds):
            try:
                if u.IsWindow(h):
                    u.PostMessageW(h, WM_CLOSE, 0, 0)
            except Exception:
                pass
        self.hwnds = []


def pump(msgs=200):
    msg = wintypes.MSG()
    n = 0
    while n < msgs and u.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
        if msg.message == WM_QUIT:
            break
        u.TranslateMessage(ctypes.byref(msg))
        u.DispatchMessageW(ctypes.byref(msg))
        n += 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--scenario', required=True,
                    help='R1..R14，或 all')
    ap.add_argument('--duration', type=int, default=30)
    ap.add_argument('--tag', default='RTPOP')
    args = ap.parse_args()

    scn = args.scenario.upper()
    print('[红队] 场景=%s 时长=%ds 标记=%s' % (scn, args.duration, args.tag), flush=True)
    rt = RedTeam()
    start = time.time()
    n = 0

    # ---------------------------------------------------------- 各场景初始化
    if scn in ('R3', 'ALL'):
        SWALLOW_CLOSE[0] = True     # 吃掉 WM_CLOSE
    if scn == 'R11':
        GO_HUNG[0] = True

    if scn == 'R10':
        for i in range(50):
            rt.create('%s-mass-%d' % (args.tag, i), x=10 + i * 3, y=10 + i * 3,
                      w=160, h=90)
    elif scn == 'R13':
        rt.create('%s-tiny' % args.tag, w=1, h=1)
        rt.create('%s-huge' % args.tag, w=1900, h=1000, x=0, y=0)
    elif scn == 'R12':
        rt.create('%s-svchost-lookalike' % args.tag)
    else:
        rt.create('%s-%s' % (args.tag, scn))

    while time.time() - start < args.duration:
        pump(120)
        n += 1
        rt.prune()

        try:
            if scn == 'R1':
                # ★ 每秒换一批标题和类名 —— 纯标题匹配必失效
                for h in rt.hwnds[:1]:
                    pass
                if rt.count() < 2:
                    rt.create('%s-%s' % (args.tag, rnd(10)))
                    if n % 3 == 0:
                        for h in list(rt.hwnds):
                            if u.IsWindow(h):
                                u.PostMessageW(h, WM_CLOSE, 0, 0)

            elif scn in ('R2', 'ALL'):
                # ★ 关一个开两个 —— 打处置速度
                if rt.count() < 6:
                    rt.create('%s-%s' % (args.tag, rnd(6)))
                    rt.create('%s-%s' % (args.tag, rnd(6)))

            elif scn == 'R3':
                # 窗口吃掉 WM_CLOSE，我们只需保证它们一直活着
                if rt.count() < 2:
                    rt.create('%s-stubborn-%d' % (args.tag, n))

            elif scn == 'R4':
                # ★★ 致命绕过：带输入框的弹窗
                if rt.count() < 2:
                    rt.create('%s-with-edit-%d' % (args.tag, n), children=1)

            elif scn == 'R5':
                if rt.count() < 2:
                    rt.create('%s-with-children-%d' % (args.tag, n), children=4)

            elif scn == 'R6':
                # ★★ 致命绕过：一个"正常窗口" + 一个纯广告窗
                real = [h for h in rt.hwnds if u.IsWindow(h)]
                if len(real) < 2:
                    rt.create('%s-real-window' % args.tag, children=6,
                              w=560, h=380)
                    rt.create('%s-ad-popup' % args.tag, w=280, h=120)
                # 保证那个"正常窗口"一直在，用来阻止强杀
            elif scn == 'R7':
                # ★ 不抢焦点：WS_EX_NOACTIVATE，降低行为分
                if rt.count() < 2:
                    rt.create('%s-stealth-%d' % (args.tag, n),
                              ex=WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)

            elif scn == 'R8':
                for h in rt.hwnds:
                    if u.IsWindow(h):
                        u.SetForegroundWindow(h)
                        u.SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
                                       SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW)

            elif scn == 'R9':
                # 守护：被杀后由外层脚本重启（见测试台），这里只保证存在
                if rt.count() < 1:
                    rt.create('%s-daemon' % args.tag)

            elif scn == 'R11':
                # ★ 装死：不抽消息队列 → 窗口无响应
                time.sleep(0.5)
                continue

            elif scn == 'R14':
                if rt.count() < 2:
                    rt.create('%s-invisible' % args.tag, visible=False)

        except Exception as e:
            print('[红队] 异常: %s' % e, flush=True)

        time.sleep(0.02)

    print('[红队] 结束，剩余窗口=%d' % rt.count(), flush=True)
    rt.close_all()
    time.sleep(0.3)
    print('[红队] 退出', flush=True)


if __name__ == '__main__':
    main()
