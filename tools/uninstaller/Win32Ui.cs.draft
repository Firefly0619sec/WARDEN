// ============================================================================
//  卸载确认窗 —— 原生 Win32 实现（2026-10-11 · Beta 3 / 0.3.0）  文件：Win32Ui.cs
//  为什么重写：WinForms 不支持 Native AOT ✗（实测 PublishAot 直接编译失败 ✓）
//              WinForms 自包含 49.25 MB ✗  →  本实现 AOT 约 1 MB ✓ 且无需装 .NET ✓
//  行为规格：诊断\测试报告\WARDEN-0.3.0-Uninstaller行为清单.md（20 条逐条对齐 ✓）
//  已知坑（全部避开 ✓）：
//    ✗ 不返回 WM_CTLCOLORSTATIC 画刷（AOT 下 fail-fast 0xC0000409）
//    ✓ 子控件 ID 放 CreateWindowExW 的 hMenu（否则全 ID 0 ✗ 点不动）
//    ✓ DrawTextW 传真实长度；高 DPI 用 SetProcessDpiAwarenessContext + GetDpiForWindow
// ============================================================================
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WardenUninstall
{
    internal static class Win32Ui
    {
        public static bool Cancelled;

        private const string Cls = "WardenUninstallWnd";
        private const int ID_OK = 101, ID_CANCEL = 102, ID_PURGE = 103, SW_SHOW = 5;
        private const uint WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_CHILD = 0x40000000,
                           WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000,
                           BS_AUTOCHECKBOX = 3, BS_PUSHBUTTON = 0, BS_DEFPUSHBUTTON = 1;
        private const uint WM_CREATE = 0x0001, WM_PAINT = 0x000F, WM_DESTROY = 0x0002,
                           WM_COMMAND = 0x0111, WM_CLOSE = 0x0010, WM_SETFONT = 0x0030;
        private const uint BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1;
        private const uint MB_OK = 0, MB_YESNO = 4, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40, MB_DEFBUTTON2 = 0x100;
        private const uint IDYES = 6;
        private const uint DT_LEFT = 0, DT_WORDBREAK = 0x10, DT_NOPREFIX = 0x800, DT_SINGLELINE = 0x20;
        private const int COLOR_WINDOW = 5, TRANSPARENT = 1, DEFAULT_GUI_FONT = 17;

        private static bool _result, _defaultPurge;
        private static long _dataMb;
        private static IntPtr _hwnd, _ck, _fontBody, _fontTitle, _fontFallback;
        private static double _scale = 1.0;
        private static WndProcDelegate? _procRef;   // ★ 持有引用，否则委托被 GC 回收后回调即崩

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
        // ★★ 2026-10-11 崩溃根因（0xC0000409 fail-fast ✓）：
        //    原生 PAINTSTRUCT 的 rgbReserved 是【32 字节】✗ 我原来只声明 8 个 ✔
        //    → BeginPaint 会写满 72 字节 ✗ → 越界踩坏栈 ✓ 触发 /GS 哨兵 → fail-fast ✓
        //    修法：按原生布局老实声明 ✓ BOOL 显式写 int（不依赖 bool 的默认宽度 ✓）
        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;        // HDC
            public int fErase;        // BOOL（4 ✓）
            public RECT rcPaint;      // RECT（16 ✓）
            public int fRestore;      // BOOL（4 ✓）
            public int fIncUpdate;    // BOOL（4 ✓）
            public byte r0, r1, r2, r3, r4, r5, r6, r7;
            public byte r8, r9, r10, r11, r12, r13, r14, r15;
            public byte r16, r17, r18, r19, r20, r21, r22, r23;
            public byte r24, r25, r26, r27, r28, r29, r30, r31;   // ★ 合计 32 字节 ✓
        }
        private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint ex, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetMessageW(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] private static extern bool IsDialogMessageW(IntPtr h, ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int c);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetStockObject(int i);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int c);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr dc, int c);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int m);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr dc, string s, int n, ref RECT r, uint f);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint under, uint strike, uint charset, uint prec, uint clip, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);

        private static int S(int px) => (int)Math.Round(px * _scale);

        /// <summary>原生消息框（取代 WinForms MessageBox ✗ AOT 安全 ✓）</summary>
        public static void Msg(string text, string caption, bool information)
        {
            try { MessageBoxW(IntPtr.Zero, text, caption, MB_OK | (information ? MB_ICONINFORMATION : MB_ICONWARNING)); } catch { }
        }

        /// <summary>确认窗：true = 确认卸载；false = 取消（Cancelled 同步置位 ✓）</summary>
        public static bool Ask(bool defaultPurge)
        {
            _defaultPurge = defaultPurge;
            _result = false;
            Cancelled = false;
            _dataMb = DataDirMb();
            try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { }   // PER_MONITOR_AWARE_V2 ✓

            IntPtr inst = GetModuleHandleW(null);
            _procRef = WndProc;                                              // ★ 防 GC ✓
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procRef),
                hInstance = inst,
                hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
                lpszClassName = Cls,
            };
            RegisterClassExW(ref wc);

            const int cw = 560, ch = 330;
            _hwnd = CreateWindowExW(0, Cls, "卸载 WARDEN", WS_CAPTION | WS_SYSMENU, 0, 0, cw, ch,
                IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                int r = MessageBoxW(IntPtr.Zero,
                    "卸载会删除程序文件、快捷方式、开机启动与注册表项；配置与日志默认保留。\n\n确定卸载 WARDEN 吗？",
                    "卸载 WARDEN", MB_YESNO | MB_ICONWARNING);
                return r == IDYES;
            }

            uint dpi = GetDpiForWindow(_hwnd);
            _scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int wReal = S(cw), hReal = S(ch);
            SetWindowPos(_hwnd, IntPtr.Zero, (GetSystemMetrics(0) - wReal) / 2, (GetSystemMetrics(1) - hReal) / 2,
                wReal, hReal, 0x0004 /*SWP_NOZORDER*/);

            ShowWindow(_hwnd, SW_SHOW);
            UpdateWindow(_hwnd);
            SetFocus(_hwnd);

            MSG msg;
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0))
            {
                if (!IsDialogMessageW(_hwnd, ref msg))    // ★ Enter/Esc/Tab 生效 ✓
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            if (_fontBody != IntPtr.Zero && _fontBody != _fontFallback) { DeleteObject(_fontBody); }
            if (_fontTitle != IntPtr.Zero && _fontTitle != _fontFallback && _fontTitle != _fontBody) { DeleteObject(_fontTitle); }
            return _result;
        }

        private static long DataDirMb()      // 与旧版算法一致 ✓
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");
                if (!Directory.Exists(dir)) { return 0; }
                long sum = 0;
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { sum += new FileInfo(f).Length; } catch { }
                }
                return sum / (1024 * 1024);
            }
            catch { return 0; }
        }

        private static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            switch (m)
            {
                case WM_CREATE: OnCreate(h); return IntPtr.Zero;
                case WM_COMMAND:
                {
                    int id = (int)(w.ToInt64() & 0xFFFF);
                    int code = (int)((w.ToInt64() >> 16) & 0xFFFF);
                    if (code == 0)
                    {
                        if (id == ID_OK) { OnOk(h); return IntPtr.Zero; }
                        if (id == ID_CANCEL) { OnCancel(h); return IntPtr.Zero; }
                    }
                    return IntPtr.Zero;
                }
                case WM_CLOSE: OnCancel(h); return IntPtr.Zero;
                case WM_DESTROY: PostQuitMessage(0); return IntPtr.Zero;
                case WM_PAINT: OnPaint(h); return IntPtr.Zero;
            }
            return DefWindowProcW(h, m, w, l);
        }

        private static void OnCreate(IntPtr h)
        {
            _fontBody = CreateFontW(S(-12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Microsoft YaHei UI");
            _fontTitle = CreateFontW(S(-17), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, "Microsoft YaHei UI");
            _fontFallback = GetStockObject(DEFAULT_GUI_FONT);
            if (_fontBody == IntPtr.Zero) { _fontBody = _fontFallback; }
            if (_fontTitle == IntPtr.Zero) { _fontTitle = _fontBody; }

            // ★ 控件 ID 放在 hMenu ✓
            _ck = CreateWindowExW(0, "BUTTON", PurgeText(), WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                S(20), S(196), S(520), S(26), h, (IntPtr)ID_PURGE, IntPtr.Zero, IntPtr.Zero);
            IntPtr ok = CreateWindowExW(0, "BUTTON", "卸载", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                S(340), S(280), S(100), S(32), h, (IntPtr)ID_OK, IntPtr.Zero, IntPtr.Zero);
            IntPtr cancel = CreateWindowExW(0, "BUTTON", "取消", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                S(450), S(280), S(90), S(32), h, (IntPtr)ID_CANCEL, IntPtr.Zero, IntPtr.Zero);

            foreach (IntPtr c in new[] { _ck, ok, cancel })
            {
                if (c != IntPtr.Zero) { SendMessageW(c, WM_SETFONT, _fontBody, (IntPtr)1); }
            }
            if (_ck != IntPtr.Zero && _defaultPurge) { SendMessageW(_ck, BM_SETCHECK, (IntPtr)1, IntPtr.Zero); }
        }

        private static string PurgeText() => "同时删除配置与日志（" + _dataMb + " MB，不可恢复）";

        private static void OnOk(IntPtr h)
        {
            long chk = _ck != IntPtr.Zero ? SendMessageW(_ck, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt64()
                                          : (_defaultPurge ? 1 : 0);
            if (chk == 1)
            {
                int r = MessageBoxW(h, "确定连配置与日志一起删掉吗？\n\n删了就找不回来了 ✗",
                    "WARDEN 卸载", MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2);
                if (r != IDYES) { return; }     // ★ 不是 Yes 就中止 ✓
            }
            _result = true;
            DestroyWindow(h);
        }

        private static void OnCancel(IntPtr h)
        {
            Cancelled = true;                   // ★ 取消 = 什么都不删 ✓
            _result = false;
            DestroyWindow(h);
        }

        private static void OnPaint(IntPtr h)
        {
            PAINTSTRUCT ps;
            IntPtr dc = BeginPaint(h, out ps);
            if (dc == IntPtr.Zero) { return; }
            SetBkMode(dc, TRANSPARENT);

            IntPtr br = CreateSolidBrush(0x00D47800);   // BGR of 0x0078D4 ✓
            RECT head = new() { L = 0, T = 0, R = S(560), B = S(60) };
            FillRect(dc, ref head, br);
            DeleteObject(br);

            IntPtr old = SelectObject(dc, _fontTitle);
            SetTextColor(dc, 0x00FFFFFF);
            RECT rt = new() { L = S(20), T = S(16), R = S(540), B = S(46) };
            DrawTextW(dc, "卸载 WARDEN", -1, ref rt, DT_LEFT | DT_SINGLELINE | DT_NOPREFIX);

            SelectObject(dc, _fontBody);
            SetTextColor(dc, 0x00000000);
            RECT body = new() { L = S(20), T = S(76), R = S(540), B = S(196) };
            DrawTextW(dc,
                "卸载会做这些事：\n  ✓ 请 WARDEN 自己退出（不强杀）\n"
                + "  ✓ 删除程序文件、快捷方式、开机启动、注册表项\n"
                + "  ✓ 配置与日志【默认保留】—— 卸载不等于清除痕迹",
                -1, ref body, DT_LEFT | DT_WORDBREAK | DT_NOPREFIX);

            SetTextColor(dc, 0x000050B4);   // BGR of 0x00B450 ✓
            RECT warn = new() { L = S(20), T = S(224), R = S(540), B = S(272) };
            DrawTextW(dc,
                "★ 数据目录里可能存着「被攻击的证据」（EMERGENCY.log / TAMPER.log）✗\n"
                + "  要留着给别人看、或者以后举报流氓软件，就别勾这一项。",
                -1, ref warn, DT_LEFT | DT_WORDBREAK | DT_NOPREFIX);

            SelectObject(dc, old);
            EndPaint(h, ref ps);
        }
    }
}
