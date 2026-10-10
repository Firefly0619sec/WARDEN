// ============================================================================
//  A47 全屏红色警告（应急模式的"叫醒用户"那一半）
//  —— 2026-10-10 第二轮：按钮必须能按、必须有反应，并且提供几个
//     "比常规更强硬、但只用来拖延时间"的选项（用户要求）
// ============================================================================
//  【用户的要求】
//      "红色警告还不能只有一个按钮，按钮必须可以按下去并且有反应。
//       而且按钮的话，建议再加几个选项，都是比常规更强制的选项，
//       比如说高频强杀之类的，可以用来帮助拖延时间，
//       但不要碰内核，那不是我们的工作。"
//
//  【★ 五个按钮（点下去立刻生效 + 当场回显结果）】
//      ① 加倍压制      间隔减半、每轮多压几个、门槛放宽 → 把桌面抢回来
//      ② 延长压制      再顶 5 分钟，给用户时间去卸载
//      ③ 冻结全部      把所有"已确认流氓"冻住（★ 只冻不杀，保留证据）
//      ④ 导出证据      把日志复制到桌面，方便交给专业工具/人员
//      ⑤ 关闭警告      明确关闭（不自动关、不强迫停留）
//
//  【★ 三条纪律（踩坑换来的）】
//      ① 绝不在调用线程建窗口（A54：模态告警会卡死主扫描循环）
//         → 后台线程建窗口 + 自己的消息循环 ✓
//      ② 纳入自保护：外部 WM_CLOSE / SC_CLOSE 关不掉它 ✓（实测过）
//      ③ 不堵门：给明确的按钮，点了就响应 ✓
//
//  【★★ 我们【不】做的事】
//      不装驱动、不碰内核、不挂钩子 —— 那不是我们的活 ✗
//      所有按钮都只用"用户态 + 系统本来就允许的权限"：
//      结束进程（要有权限）、冻结用 NtSuspendProcess、导出只是复制文件 ✓
//
//  【★ 为什么按钮要自己画】
//      主程序是 Native AOT → WinForms 用不了 ✗
//      → 自绘 + 自己命中测试 + 悬停/按下三态（否则用户按了没感觉 ✗）
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WARDEN
{
    internal static class EmergencyWarning
    {
        // ---- 按钮动作编号 ----
        private const int ActEscalate = 1;
        private const int ActExtend = 2;
        private const int ActFreezeAll = 3;
        private const int ActExport = 4;
        private const int ActClose = 5;

        private sealed class Btn
        {
            public RECT Rect;
            public string Label = string.Empty;
            public int Action;
            public bool Hover;
            public bool Pressed;
        }

        // ---- 状态 ----
        private static readonly object Gate = new object();
        private static IntPtr _hwnd = IntPtr.Zero;
        private static long _lastShownTicks;
        private static Action<string>? _log;
        private static string _className = string.Empty;
        private static Btn[] _buttons = new Btn[0];
        private static string _status = WARDEN.L.T("★ 下面四个按钮都会【立刻生效】，且只作用于已经确认的流氓程序");
        private static int _statusY;
        private static int _bodyBottom;

        /// <summary>★ 窗口过程必须长期持有引用，否则被 GC 回收后消息一来就崩 ✗</summary>
        private static WndProcDelegate? _procRef;

        public static void Init(Action<string> log)
        {
            _log = log;
        }

        /// <summary>★★★ 弹出全屏红色警告（不阻塞调用方）。</summary>
        public static void Show(string kind)
        {
            long now = DateTime.Now.Ticks;
            long last = Interlocked.Read(ref _lastShownTicks);
            if (last != 0 && now - last < TimeSpan.TicksPerSecond * 30)
            {
                _log?.Invoke("[EMERGENCY-UI] warning suppressed (rate limit)");
                BringToFront();
                return;
            }
            Interlocked.Exchange(ref _lastShownTicks, now);

            var t = new Thread(() => UiMain(kind))
            {
                IsBackground = true,
                Name = "WARDEN-EmergencyUI",
            };
            t.Start();
        }

        private static void BringToFront()
        {
            IntPtr h;
            lock (Gate)
            {
                h = _hwnd;
            }
            if (h != IntPtr.Zero)
            {
                SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
                             SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetForegroundWindow(h);
            }
        }

        // ================================================================
        //  UI 线程
        // ================================================================
        private static void UiMain(string kind)
        {
            try
            {
                if (!RegisterClassOnce())
                {
                    _log?.Invoke("[EMERGENCY-UI] RegisterClass failed");
                    return;
                }

                int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
                int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
                int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                if (w <= 0 || h <= 0)
                {
                    w = 1024;
                    h = 768;
                    x = 0;
                    y = 0;
                }

                IntPtr hwnd = CreateWindowExW(
                    WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
                    _className, WARDEN.L.T("WARDEN 应急警告"),
                    WS_POPUP | WS_VISIBLE,
                    x, y, w, h,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

                if (hwnd == IntPtr.Zero)
                {
                    _log?.Invoke("[EMERGENCY-UI] CreateWindowEx failed err="
                        + Marshal.GetLastWin32Error());
                    return;
                }

                lock (Gate)
                {
                    _hwnd = hwnd;
                }

                SelfProtect.ProtectHandle(hwnd);   // ★ 纪律②
                BuildLayout(hwnd);

                SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_SHOWWINDOW);
                SetForegroundWindow(hwnd);
                SetFocus(hwnd);
                MessageBeep(MB_ICONHAND);

                _log?.Invoke("[EMERGENCY-UI] full-screen warning shown  kind=" + kind);

                // ★★★ 默认动作：自动导出证据（用户要求），并在界面上【明说】✓
                //   放后台线程：导出是磁盘 IO（以后再加上报网络），别卡住界面 ✗
                _status = WARDEN.L.T("★ 正在自动导出证据到桌面…");
                var exporter = new Thread(() =>
                {
                    try
                    {
                        string note = Emergency.AutoExport();
                        _status = "✓ " + note
                            + "   ｜   （把证据上报给作者的功能：等 WARDEN 仓库建好后启用）";
                    }
                    catch (Exception ex)
                    {
                        _status = WARDEN.L.T("证据导出失败：") + ex.Message;
                    }
                    IntPtr h2 = _hwnd;
                    if (h2 != IntPtr.Zero)
                    {
                        InvalidateRect(h2, IntPtr.Zero, false);
                    }
                })
                {
                    IsBackground = true,
                    Name = "WARDEN-EmergencyExport",
                };
                exporter.Start();

                while (Native.GetMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }

                _log?.Invoke("[EMERGENCY-UI] warning closed by user");
            }
            catch (Exception e)
            {
                _log?.Invoke("[EMERGENCY-UI] failed: " + e.Message);
            }
            finally
            {
                lock (Gate)
                {
                    _hwnd = IntPtr.Zero;
                }
            }
        }

        private static bool RegisterClassOnce()
        {
            if (_className.Length > 0)
            {
                return true;
            }
            _className = "WARDEN_EmergencyWnd_" + Environment.ProcessId;
            _procRef = WndProc;

            var wc = new WNDCLASS
            {
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procRef),
                hInstance = GetModuleHandleW(null),
                lpszClassName = _className,
            };

            ushort atom = RegisterClassW(ref wc);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1410)
                {
                    return false;
                }
            }
            return true;
        }

        // ================================================================
        //  布局（按屏幕尺寸自适应，小屏也不会挤爆）
        // ================================================================
        private static void BuildLayout(IntPtr hwnd)
        {
            RECT rc;
            GetClientRect(hwnd, out rc);
            int w = rc.right - rc.left;
            int h = rc.bottom - rc.top;

            int gap = 22;
            int bh = Math.Max(48, h / 14);
            int bw = Math.Min(440, (w - margin(w) * 2 - gap) / 2);
            int left = (w - (bw * 2 + gap)) / 2;

            // ★ 行距别太挤：截图里 2x2 按钮快贴成一整块了 ✓
            int vgap = 20;
            int closeY = h - 20 - bh;
            int row2Y = closeY - bh - vgap;
            int row1Y = row2Y - bh - vgap;
            int statusY = row1Y - 48;

            var list = new Btn[5];
            list[0] = new Btn { Action = ActEscalate, Label = WARDEN.L.T("加倍压制（更狠）"),
                                Rect = R(left, row1Y, bw, bh) };
            list[1] = new Btn { Action = ActExtend, Label = WARDEN.L.T("延长压制 5 分钟"),
                                Rect = R(left + bw + gap, row1Y, bw, bh) };
            list[2] = new Btn { Action = ActFreezeAll, Label = WARDEN.L.T("冻结全部可疑进程"),
                                Rect = R(left, row2Y, bw, bh) };
            list[3] = new Btn { Action = ActExport, Label = WARDEN.L.T("重新导出证据"),
                                Rect = R(left + bw + gap, row2Y, bw, bh) };
            int cw = bw * 2 + gap;
            list[4] = new Btn { Action = ActClose, Label = WARDEN.L.T("我知道了，关闭这个警告"),
                                Rect = R((w - cw) / 2, closeY, cw, bh) };
            _buttons = list;

            _statusY = statusY;
            _bodyBottom = statusY - 14;
        }

        private static int margin(int w) => Math.Max(60, w / 12);

        private static RECT R(int x, int y, int w, int h)
        {
            return new RECT { left = x, top = y, right = x + w, bottom = y + h };
        }

        private static bool Inside(RECT r, int x, int y)
            => x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;

        private static int HitIndex(int x, int y)
        {
            Btn[] bs = _buttons;
            for (int i = 0; i < bs.Length; i++)
            {
                if (Inside(bs[i].Rect, x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        // ================================================================
        //  窗口过程
        // ================================================================
        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_PAINT:
                        Paint(hwnd);
                        return IntPtr.Zero;

                    case WM_ERASEBKGND:
                        return new IntPtr(1);

                    case WM_SIZE:
                        BuildLayout(hwnd);
                        return IntPtr.Zero;

                    // ---- 鼠标移动：只为了"按下去有感觉" ✓ ----
                    case WM_MOUSEMOVE:
                        {
                            int mx = LoWord(lParam), my = HiWord(lParam);
                            int idx = HitIndex(mx, my);
                            bool changed = false;
                            for (int i = 0; i < _buttons.Length; i++)
                            {
                                bool hov = (i == idx);
                                if (_buttons[i].Hover != hov)
                                {
                                    _buttons[i].Hover = hov;
                                    changed = true;
                                }
                            }
                            if (changed)
                            {
                                InvalidateRect(hwnd, IntPtr.Zero, false);
                            }
                            var tme = new TRACKMOUSEEVENT
                            {
                                cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
                                dwFlags = TME_LEAVE,
                                hwndTrack = hwnd,
                            };
                            TrackMouseEvent(ref tme);
                        }
                        return IntPtr.Zero;

                    case WM_MOUSELEAVE:
                        for (int i = 0; i < _buttons.Length; i++)
                        {
                            _buttons[i].Hover = false;
                        }
                        InvalidateRect(hwnd, IntPtr.Zero, false);
                        return IntPtr.Zero;

                    // ---- 按下就立刻做事（响应要快，不能等抬起）✓ ----
                    case WM_LBUTTONDOWN:
                        {
                            int mx = LoWord(lParam), my = HiWord(lParam);
                            int idx = HitIndex(mx, my);
                            if (idx < 0)
                            {
                                return IntPtr.Zero;
                            }
                            _buttons[idx].Pressed = true;
                            InvalidateRect(hwnd, IntPtr.Zero, false);
                            RunAction(hwnd, _buttons[idx]);
                        }
                        return IntPtr.Zero;

                    case WM_LBUTTONUP:
                        {
                            int mx = LoWord(lParam), my = HiWord(lParam);
                            int idx = HitIndex(mx, my);
                            for (int i = 0; i < _buttons.Length; i++)
                            {
                                _buttons[i].Pressed = (i == idx) && _buttons[i].Pressed;
                                if (i != idx)
                                {
                                    _buttons[i].Pressed = false;
                                }
                            }
                            if (idx >= 0 && IsWindow(hwnd))
                            {
                                _buttons[idx].Pressed = false;
                                InvalidateRect(hwnd, IntPtr.Zero, false);
                            }
                        }
                        return IntPtr.Zero;

                    case WM_CLOSE:
                        _log?.Invoke("[EMERGENCY-UI] WM_CLOSE ignored (use the button)");
                        return IntPtr.Zero;

                    case WM_DESTROY:
                        PostQuitMessage(0);
                        return IntPtr.Zero;
                }
            }
            catch (Exception e)
            {
                _log?.Invoke("[EMERGENCY-UI] wndproc error: " + e.Message);
            }

            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        /// <summary>★ 按下一个按钮 → 做事 + 当场回显结果（用户要的"有反应"）✓</summary>
        private static void RunAction(IntPtr hwnd, Btn b)
        {
            string result;
            try
            {
                switch (b.Action)
                {
                    case ActEscalate:
                        result = Emergency.Escalate();
                        b.Label = WARDEN.L.T("加倍压制（已升级）");
                        break;

                    case ActExtend:
                        result = Emergency.Extend(300);
                        b.Label = WARDEN.L.T("已延长 5 分钟");
                        break;

                    case ActFreezeAll:
                        result = Emergency.RequestFreezeAll();
                        b.Label = WARDEN.L.T("冻结全部（持续生效）");
                        break;

                    case ActExport:
                        result = Emergency.AutoExport();
                        b.Label = WARDEN.L.T("已重新导出");
                        break;

                    case ActClose:
                        _log?.Invoke("[EMERGENCY-UI] user acknowledged the warning");
                        DestroyWindow(hwnd);
                        return;

                    default:
                        return;
                }
            }
            catch (Exception e)
            {
                result = WARDEN.L.T("操作失败：") + e.Message;
                _log?.Invoke("[EMERGENCY-UI] action failed: " + e.Message);
            }

            _status = "✓ " + result;
            _log?.Invoke("[EMERGENCY-UI] action=" + b.Action + "  " + result);
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        private static int LoWord(IntPtr v) => (short)((long)v & 0xFFFF);
        private static int HiWord(IntPtr v) => (short)(((long)v >> 16) & 0xFFFF);

        // ================================================================
        //  绘制
        // ================================================================
        private static void Paint(IntPtr hwnd)
        {
            int psSize = Marshal.SizeOf<PAINTSTRUCT>();
            IntPtr psBuf = Marshal.AllocHGlobal(psSize);
            IntPtr hdc = BeginPaint(hwnd, psBuf);
            IntPtr fTitle = IntPtr.Zero;
            IntPtr fSub = IntPtr.Zero;
            IntPtr fBody = IntPtr.Zero;
            IntPtr fBtn = IntPtr.Zero;
            IntPtr fStatus = IntPtr.Zero;
            IntPtr oldFont = IntPtr.Zero;

            try
            {
                RECT rc;
                GetClientRect(hwnd, out rc);
                int w = rc.right - rc.left;
                int h = rc.bottom - rc.top;

                // ---- 背景 ----
                IntPtr bg = CreateSolidBrush(ColorRef(0xB4, 0x0A, 0x0A));
                FillRect(hdc, ref rc, bg);
                DeleteObject(bg);
                SetBkMode(hdc, TRANSPARENT);

                // ---- 字体（按屏幕高度自适应，小屏也不挤）----
                // ★★ 字号必须【有界】—— 第一版写成 34 + scale*10，
                //    900 高屏幕上算出 -134px 的巨字：标题被截断、
                //    正文溢出、按钮文字互相压住 ✗（2026-10-10 截图实测）
                //    → 以 900p 为基准等比缩放，并夹在 0.85x~1.6x ✓
                double k = h / 900.0;
                if (k < 0.85)
                {
                    k = 0.85;
                }
                if (k > 1.6)
                {
                    k = 1.6;
                }
                fTitle = MakeFont(-Px(50, k), 800);
                fSub = MakeFont(-Px(26, k), 600);
                fBody = MakeFont(-Px(18, k), 400);
                fStatus = MakeFont(-Px(21, k), 600);
                fBtn = MakeFont(-Px(22, k), 700);

                // ---- 黑标题带 ----
                var band = new RECT { left = 0, top = 0, right = w, bottom = 132 };
                IntPtr black = CreateSolidBrush(0x00000000);
                FillRect(hdc, ref band, black);
                DeleteObject(black);

                oldFont = SelectObject(hdc, fTitle);
                SetTextColor(hdc, 0x00FFFFFF);
                var rTitle = new RECT { left = 0, top = 26, right = w, bottom = 118 };
                DrawTextW(hdc, WARDEN.L.T("⚠  WARDEN 正在被攻击"), -1, ref rTitle,
                          DT_CENTER | DT_VCENTER | DT_SINGLELINE);

                SelectObject(hdc, fSub);
                SetTextColor(hdc, 0x00FFD9D9);
                var rSub = new RECT { left = 0, top = 148, right = w, bottom = 192 };
                DrawTextW(hdc, WARDEN.L.T("我已经进入【应急压制模式】，正在按住那个程序"),
                          -1, ref rSub, DT_CENTER | DT_SINGLELINE);

                // ---- 正文 ----
                SelectObject(hdc, fBody);
                SetTextColor(hdc, 0x00FFFFFF);
                int m = margin(w);
                var rBody = new RECT { left = m, top = 206, right = w - m, bottom = _bodyBottom };
                DrawTextW(hdc, BodyText(), -1, ref rBody,
                          DT_LEFT | DT_TOP | DT_WORDBREAK | DT_NOPREFIX);

                // ---- 状态行：按钮到底干了什么，必须当场看见 ✓ ----
                SelectObject(hdc, fStatus);
                SetTextColor(hdc, 0x00FFF0A0);
                var rStatus = new RECT { left = m, top = _statusY, right = w - m, bottom = _statusY + 40 };
                DrawTextW(hdc, _status, -1, ref rStatus,
                          DT_CENTER | DT_VCENTER | DT_SINGLELINE);

                // ---- 五个按钮（三态：常态 / 悬停 / 按下）----
                SelectObject(hdc, fBtn);
                foreach (Btn b in _buttons)
                {
                    int fill = 0x00FFFFFF;                                   // 常态：白
                    if (b.Pressed)
                    {
                        fill = 0x0090B0D0;                                   // 按下：偏暗蓝灰
                    }
                    else if (b.Hover)
                    {
                        fill = 0x00D9F2FF;                                   // 悬停：淡蓝
                    }

                    IntPtr br = CreateSolidBrush(fill);
                    RECT rr = b.Rect;
                    FillRect(hdc, ref rr, br);
                    DeleteObject(br);

                    SetTextColor(hdc, ColorRef(0xB4, 0x0A, 0x0A));
                    RECT rt = b.Rect;
                    // ★ 按下时文字往右下挪 1px —— "按下去"的手感 ✓
                    if (b.Pressed)
                    {
                        rt.left += 1;
                        rt.top += 1;
                        rt.right += 1;
                        rt.bottom += 1;
                    }
                    DrawTextW(hdc, b.Label, -1, ref rt,
                              DT_CENTER | DT_VCENTER | DT_SINGLELINE);
                }
            }
            catch (Exception e)
            {
                _log?.Invoke("[EMERGENCY-UI] paint failed: " + e.Message);
                try
                {
                    PaintFallback(hwnd, hdc);
                }
                catch
                {
                    // 连兜底都挂 → 日志里已有记录 ✓
                }
            }
            finally
            {
                if (oldFont != IntPtr.Zero)
                {
                    SelectObject(hdc, oldFont);
                }
                DeleteFont(fTitle);
                DeleteFont(fSub);
                DeleteFont(fBody);
                DeleteFont(fBtn);
                DeleteFont(fStatus);
                EndPaint(hwnd, psBuf);
                Marshal.FreeHGlobal(psBuf);
            }
        }

        /// <summary>
        /// ★★ 兜底画面：只用"绝对不会失败"的 API 组合。
        ///   哪怕主绘制挂了，用户也必须看到【出了什么事 + 该干什么】✗
        /// </summary>
        private static void PaintFallback(IntPtr hwnd, IntPtr hdc)
        {
            RECT rc;
            GetClientRect(hwnd, out rc);
            int w = rc.right - rc.left;

            IntPtr bg = CreateSolidBrush(ColorRef(0xB4, 0x0A, 0x0A));
            FillRect(hdc, ref rc, bg);
            DeleteObject(bg);

            SetTextColor(hdc, 0x00FFFFFF);
            var r = new RECT { left = 40, top = 40, right = Math.Max(60, w - 40), bottom = rc.bottom - 40 };
            DrawTextW(hdc,
                WARDEN.L.T("WARDEN 正在被攻击 —— 已经进入应急压制模式。")
                + Environment.NewLine + Environment.NewLine
                + WARDEN.L.T("请按顺序做：") + Environment.NewLine
                + WARDEN.L.T("1) 用 Geek Uninstaller 卸掉你最近装的那个软件；") + Environment.NewLine
                + WARDEN.L.T("2) 用 Windows 安全中心（或卡巴斯基）做一次扫描；") + Environment.NewLine
                + WARDEN.L.T("3) 卸不掉就重启进安全模式再卸。") + Environment.NewLine
                + Environment.NewLine
                + WARDEN.L.T("现在先别重启电脑。"),
                -1, ref r, DT_LEFT | DT_TOP | DT_WORDBREAK | DT_NOPREFIX);
        }

        /// <summary>★ 正文：说人话、给顺序，并说清四个按钮各自干什么</summary>
        private static string BodyText()
        {
            // ★ 文案要压在一屏内（加过一版太长的，被状态行裁掉了 ✗）
            //   → 不留空行、句子尽量短 ✓
            var sb = new StringBuilder();
            sb.AppendLine(WARDEN.L.T("刚才有程序试图关掉 WARDEN（或伪造它的停止请求），已进入应急压制模式。"));
            sb.AppendLine(WARDEN.L.T("下面四个按钮【点下去立刻生效】，只作用于已经确认的流氓程序："));
            sb.AppendLine(WARDEN.L.T("① 加倍压制 —— 盯得更密、压得更多，把桌面抢回来。"));
            sb.AppendLine(WARDEN.L.T("② 延长压制 —— 再顶 5 分钟，给你时间去卸载。"));
            sb.AppendLine(WARDEN.L.T("③ 冻结全部 —— 把所有可疑程序冻住（只冻不杀，证据还在）。"));
            sb.AppendLine(WARDEN.L.T("④ 重新导出 —— 手动再导一次（进应急时已经自动导过一遍）。"));
            sb.AppendLine(WARDEN.L.T("你该做的（约 3 分钟）："));
            sb.AppendLine(WARDEN.L.T("· 用「Geek Uninstaller」卸掉最近装的那个软件（通常就是它）。"));
            sb.AppendLine(WARDEN.L.T("· 用「Windows 安全中心」快速扫描；想更彻底用卡巴斯基免费版全盘扫。"));
            sb.AppendLine(WARDEN.L.T("· 卸不掉 / 又回来 → 重启进【安全模式】再卸；现在先别重启电脑。"));
            sb.AppendLine(WARDEN.L.T("★ 证据已【自动导出到桌面】：万一我们被强行杀掉，你手上还有现场记录。"));
            sb.AppendLine("★ 目前【不会外发】：上报给作者的功能要等 WARDEN 仓库建好后启用。");
            sb.AppendLine("★ WARDEN 只做用户态压制：不替你清除、不装驱动、不碰内核。");
            return sb.ToString();
        }

        // ================================================================
        //  Win32
        // ================================================================
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        private const uint WM_PAINT = 0x000F;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_MOUSELEAVE = 0x02A3;
        private const uint WM_SIZE = 0x0005;

        private const uint TME_LEAVE = 0x00000002;

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        private const uint DT_LEFT = 0x0000;
        private const uint DT_CENTER = 0x0001;
        private const uint DT_TOP = 0x0000;
        private const uint DT_VCENTER = 0x0004;
        private const uint DT_SINGLELINE = 0x0020;
        private const uint DT_WORDBREAK = 0x0010;
        private const uint DT_NOPREFIX = 0x0800;

        private const int TRANSPARENT = 1;
        private const uint MB_ICONHAND = 0x00000010;
        private const int DEFAULT_CHARSET = 1;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        /// <summary>★ COLORREF 是 0x00BBGGRR（蓝绿红反着写）—— 写错了颜色就变 ✗</summary>
        private static int ColorRef(int r, int g, int b) => (b << 16) | (g << 8) | r;

        /// <summary>★ 按屏幕比例算字号（有下限，绝不会出"巨字"）✓</summary>
        private static int Px(int basePx, double k)
        {
            int v = (int)Math.Round(basePx * k);
            return v < 12 ? 12 : v;
        }

        private static IntPtr MakeFont(int height, int weight)
        {
            return CreateFontW(height, 0, 0, 0, weight, 0, 0, 0,
                               DEFAULT_CHARSET, 0, 0, 0, 0, "Microsoft YaHei UI");
        }

        private static void DeleteFont(IntPtr f)
        {
            if (f != IntPtr.Zero)
            {
                DeleteObject(f);
            }
        }

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName,
            string lpWindowName, uint dwStyle, int x, int y, int w, int h,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Native.MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref Native.MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, IntPtr lpPaint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, IntPtr lpPaint);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText,
            ref RECT lprc, uint format);

        // ★★ SetBkMode / SetTextColor 属于 gdi32（写成 user32 会 EntryPointNotFound）✗
        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        private static extern uint SetTextColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement,
            int cOrientation, int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut,
            int iCharSet, int iOutPrecision, int iClipPrecision, int iQuality,
            int iPitchAndFamily, string pszFaceName);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    }
}
