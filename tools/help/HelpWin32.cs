// ============================================================================
//  WARDEN 帮助中心 —— 原生 Win32 实现（2026-10-11 · Beta 3 / 0.3.3）
//  文件：HelpWin32.cs
//
//  【为什么重写】
//    WinForms 不支持 Native AOT ✗（PublishAot 编译直接失败）
//    而 Help 原来是 WinForms + SelfContained：publish 目录 219 个文件 / 108.8 MB ✗
//    打包进安装载荷的却只有 Help.exe（174 KB 的 apphost 空壳）✗
//    → 0.2.5 / 0.2.6 装完之后 Help 双击根本没反应（真代码在 Help.dll 里 ✗）
//    改成原生 AOT 单文件后：一个 exe 约 1 MB，单独放哪都能跑 ✓
//
//  【界面规格来自哪】HelpForm.cs / Lang.cs / Diagnostics.cs
//    原文案、原坐标一个都没动 ✓ 只换掉承载它们的控件实现 ✓
//      主窗：客户区 820x600；顶部条 60（底色 0,120,212）；底部条 58（底色 248,249,251）
//            左侧目录 220 宽 · 1 像素分隔线 · 右侧只读正文
//            按钮 (16,14,120x32) (144,14,130x32) (282,14,130x32) (686,14,118x32)
//      反馈窗：客户区 760x640；顶部条 52；底部条 88
//            标签/输入框坐标逐项照抄 BuildUI ✓
//
//  【血泪教训，这里全部避开】
//    ✗ 绝不用 Marshal.GetFunctionPointerForDelegate（AOT 下 fail-fast 0xC0000409）
//      ✓ 一律 [UnmanagedCallersOnly(CallConvs = CallConvStdcall)] + delegate* unmanaged[Stdcall]
//    ✗ 结构体里绝不放 string / bool / 数组（AOT 禁用内置封送器 ✗ 一封送就 fail-fast）
//      ✓ 结构体只用 IntPtr + 整数（blittable ✓）；字符串用 Marshal.StringToHGlobalUni ✓
//    ✗ P/Invoke 归属写错就 EntryPointNotFoundException → 控件整个建不出来
//      ✓ MessageBoxW / DrawTextW / CreateWindowExW / SetWindowPos 在 user32
//      ✓ GetStockObject / CreateFontW / SelectObject / CreateSolidBrush / DeleteObject 在 gdi32
//      ✓ 文件对话框在 comdlg32
//    ✗ 客户区尺寸要用 AdjustWindowRectEx 换算（否则底部按钮被边框裁掉）
//    ✗ DPI 缩放必须【建窗之前】定好（建窗后再算 → 窗口放大而控件不放大）
//    ✗ 异常绝不能逃出 UnmanagedCallersOnly 回调（包 try/catch + 写日志）
//
//  日志：%TEMP%\help-win32.log（关键步骤各一行 ✓ 不再静默吞异常）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WardenHelp
{
    internal static unsafe class HelpWin32
    {
        // ====================================================================
        //  ★ 窗口回调：必须是静态方法、必须带 CallConvStdcall、绝不能抛异常
        // ====================================================================
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        private static IntPtr MainWndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            try { return WndProc(h, m, w, l, false); }
            catch (Exception ex) { L("★ 主窗回调异常 msg=0x" + m.ToString("X") + " : " + ex); return DefWindowProcW(h, m, w, l); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        private static IntPtr FbWndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            try { return WndProc(h, m, w, l, true); }
            catch (Exception ex) { L("★ 反馈窗回调异常 msg=0x" + m.ToString("X") + " : " + ex); return DefWindowProcW(h, m, w, l); }
        }

        /// <summary>
        /// 面板窗口过程（★ 只为查清面板到底收哪些消息 / 并把底色画上 ✓）
        /// 实测动机：内核 STATIC 类的面板既不收 WM_PAINT 也不收 WM_ERASEBKGND ✗
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        private static IntPtr PanelWndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            try
            {
                int pc = PanelColor(h);
                if (m == WM_PAINT && pc >= 0)
                {
                    PAINTSTRUCT ps;
                    IntPtr dc = BeginPaint(h, out ps);
                    if (dc != IntPtr.Zero)
                    {
                        RECT rc;
                        GetClientRect(h, out rc);
                        FillRect(dc, ref rc, Bg(pc));
                        if (h == _top) { DrawTopBarText(dc); }
                        else if (h == _fbTop) { DrawFeedbackTopText(dc); }
                        EndPaint(h, ref ps);
                    }
                    if (!_paintedLogged)
                    {
                        L("PanelWndProc 首次画到面板 hwnd=" + h + " 颜色=#" + pc.ToString("X6") + " ✓");
                        _paintedLogged = true;
                    }
                    return IntPtr.Zero;
                }
                if (m == WM_ERASEBKGND && pc >= 0)
                {
                    RECT rc;
                    GetClientRect(h, out rc);
                    FillRect(w, ref rc, Bg(pc));
                    if (h == _top) { DrawTopBarText(w); }
                    else if (h == _fbTop) { DrawFeedbackTopText(w); }
                    return (IntPtr)1;
                }
                // 只探测"不常来的消息"，避免刷屏 ✓
                if (m == 0x0001 || m == 0x0018 || m == 0x0005 || m == 0x0007 || m == 0x000F || m == 0x0014)
                {
                    if (_panelMsgProbe < 30) { _panelMsgProbe++; L("PanelWndProc msg=0x" + m.ToString("X") + " hwnd=" + h); }
                }
                return DefWindowProcW(h, m, w, l);
            }
            catch (Exception ex) { L("★ 面板回调异常 msg=0x" + m.ToString("X") + " : " + ex); return DefWindowProcW(h, m, w, l); }
        }

        // ====================================================================
        //  常量
        // ====================================================================
        private const string ClsMain = "WardenHelpWnd";
        private const string ClsFb = "WardenHelpFeedbackWnd";
        // ★★ 2026-10-11 实测逼出来的第三个类：面板必须用自己的类 ✗
        //   实测：内核 STATIC 类的面板【既不收 WM_PAINT 也不收 WM_ERASEBKGND】✗
        //         （三种踢法都试过：InvalidateRect / PostMessage / SendMessage 全无效 ✓）
        //   所以自己注册一个类，hbrBackground 直接给对应底色的刷子 →
        //   系统在擦背景时就用它 ✓ 不依赖任何回调 ✓ 最稳 ✓
        private const string ClsPanel = "WardenHelpPanelWnd";

        // 控件 ID（★ 必须放进 CreateWindowExW 的 hMenu，否则全 ID 0 → WM_COMMAND 点不动）
        private const int ID_TOC = 101;
        private const int ID_BODY = 102;
        private const int ID_BTN_FEEDBACK = 103;
        private const int ID_BTN_OPENPAGE = 104;
        private const int ID_BTN_SETTINGS = 105;
        private const int ID_BTN_CLOSE = 106;

        private const int ID_FB_KIND = 201;
        private const int ID_FB_SUBJECT = 202;
        private const int ID_FB_DETAIL = 203;
        private const int ID_FB_CONTACT = 204;
        private const int ID_FB_DIAG = 205;
        private const int ID_FB_PREVIEW = 206;
        private const int ID_FB_GITHUB = 207;
        private const int ID_FB_COPY = 208;
        private const int ID_FB_SAVE = 209;
        private const int ID_FB_CLOSE = 210;
        private const int ID_FB_STATUS = 211;

        // 窗口类风格 / 窗口风格
        private const uint CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001;
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
        private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000;
        private const uint WS_BORDER = 0x00800000, WS_VSCROLL = 0x00200000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint SS_LEFT = 0x00000000;
        private const uint BS_PUSHBUTTON = 0x00000000, BS_DEFPUSHBUTTON = 0x00000001,
                           BS_AUTOCHECKBOX = 0x00000003;
        private const uint ES_LEFT = 0x0000, ES_MULTILINE = 0x0004, ES_AUTOVSCROLL = 0x0040,
                           ES_READONLY = 0x0800;
        private const uint CBS_DROPDOWNLIST = 0x0003;
        private const uint LBS_NOTIFY = 0x0001, LBS_OWNERDRAWFIXED = 0x0010;

        // 消息
        private const uint WM_CREATE = 0x0001, WM_DESTROY = 0x0002, WM_SIZE = 0x0005,
                           WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_GETMINMAXINFO = 0x0024,
                           WM_DRAWITEM = 0x002B, WM_MEASUREITEM = 0x002C, WM_SETFONT = 0x0030,
                           WM_COMMAND = 0x0111, WM_SETFOCUS = 0x0007, WM_CTLCOLORSTATIC = 0x0138,
                           WM_ERASEBKGND = 0x0014;
        private const uint LB_ADDSTRING = 0x0180, LB_SETCURSEL = 0x0186, LB_GETCURSEL = 0x0188,
                           LB_SETITEMHEIGHT = 0x01A0;
        private const uint CB_ADDSTRING = 0x0143, CB_GETCURSEL = 0x0147, CB_SETCURSEL = 0x014E,
                           CB_SETMINVISIBLE = 0x1701;
        private const uint BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1;
        private const uint EM_SETSEL = 0x00B1, EM_SCROLLCARET = 0x00B7, EM_SETLIMITTEXT = 0x00C5;

        // 通知码
        private const int LBN_SELCHANGE = 1, CBN_SELCHANGE = 1, BN_CLICKED = 0, EN_CHANGE = 0x0300;

        // 绘制
        private const uint DT_LEFT = 0x0000, DT_WORDBREAK = 0x0010, DT_SINGLELINE = 0x0020,
                           DT_VCENTER = 0x0004, DT_NOPREFIX = 0x0800, DT_EXPANDTABS = 0x0040;
        private const int TRANSPARENT = 1, COLOR_WINDOW = 5, DEFAULT_GUI_FONT = 17;
        private const uint ODS_SELECTED = 0x0001;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        // MessageBox
        private const uint MB_OK = 0, MB_YESNO = 4, MB_ICONERROR = 0x10,
                           MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40;
        private const int IDOK = 1, IDYES = 6;
        private const int SW_SHOW = 5;

        // 颜色（GDI 是 0x00BBGGRR 反序 ✓ 全部换算过）
        private const int CLR_BLUE = 0x00D47800;      // RGB(0,120,212)   顶栏底色
        private const int CLR_SUBHEAD = 0x00FCEEDF;   // RGB(225,238,252) 副标题
        private const int CLR_BOTTOM = 0x00FBF9F8;    // RGB(248,249,251) 底栏底色
        private const int CLR_SPLIT = 0x00EAE5E2;     // RGB(226,229,234) 分隔线
        private const int CLR_WHITE = 0x00FFFFFF;
        private const int CLR_TITLE = 0x002A221E;     // RGB(30,34,42)    正文标题
        private const int CLR_HINT = 0x00808080;      // Color.Gray
        private const int CLR_PREVIEW = 0x00FCFBFA;   // RGB(250,251,252) 预览底色
        private const int CLR_TOC_BG = 0x00FFFFFF;
        private const int CLR_SEL_BG = 0x00D47800;    // 目录选中底色（同顶栏蓝）

        private static readonly char NUL = (char)0;

        // ====================================================================
        //  P/Invoke —— ★ 归属不能写错
        // ====================================================================
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public IntPtr lpszMenuName, lpszClassName;   // ★ 原生 LPCWSTR（不放 string ✗）
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        // ★ MINMAXINFO = 5 个 POINT（40 字节 ✓ 少写会踩栈）
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        // ★ 全部整数 → blittable ✓
        [StructLayout(LayoutKind.Sequential)]
        private struct MEASUREITEMSTRUCT
        {
            public uint CtlType, CtlID, itemID, itemWidth, itemHeight;
            public UIntPtr itemData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DRAWITEMSTRUCT
        {
            public uint CtlType, CtlID, itemID, itemAction, itemState;
            public IntPtr hwndItem, hDC;
            public RECT rcItem;
            public UIntPtr itemData;
        }

        // ★★ PAINTSTRUCT 的 rgbReserved 是【32 字节】✗ 少声明会越界踩栈 → fail-fast 0xC0000409 ✓
        //    BOOL 一律显式写 int（不依赖 bool 的默认宽度 ✓）
        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            public byte r0, r1, r2, r3, r4, r5, r6, r7;
            public byte r8, r9, r10, r11, r12, r13, r14, r15;
            public byte r16, r17, r18, r19, r20, r21, r22, r23;
            public byte r24, r25, r26, r27, r28, r29, r30, r31;   // ★ 合计 32 字节 ✓
        }

        // ★ 与 winuser.h 的 OPENFILENAMEW 逐字段对齐（全部指针/整数 → blittable ✓）
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAMEW
        {
            public uint lStructSize;
            public IntPtr hwndOwner, hInstance, lpstrFilter, lpstrCustomFilter;
            public uint nMaxCustFilter, nFilterIndex;
            public IntPtr lpstrFile;
            public uint nMaxFile;
            public IntPtr lpstrFileTitle;
            public uint nMaxFileTitle;
            public IntPtr lpstrInitialDir, lpstrTitle;
            public uint Flags;
            public ushort nFileOffset, nFileExtension;
            public IntPtr lpstrDefExt, lCustData, lpfnHook, lpTemplateName, pvReserved;
            public uint dwReserved, FlagsEx;
        }
        // ★ 2026-10-11 补：子代理交付物缺少这个 P/Invoke 声明（干净编译才现形）
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);


        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetMessageW(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] private static extern bool IsDialogMessageW(IntPtr h, ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, string l);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int c);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();          // ★ 建窗之前用
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool AdjustWindowRectEx(ref RECT r, uint style, bool menu, uint exStyle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, char[] buf, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr h, string s);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr rgn, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
        // ★ DrawTextW / FillRect 在 user32（不是 gdi32 ✗ 写错文字就画不出来）
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DrawTextW(IntPtr dc, string s, int n, ref RECT r, uint f);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        // ★ GDI 全在 gdi32
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int c);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr dc, int c);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int m);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight,
            uint italic, uint under, uint strike, uint charset, uint prec, uint clip, uint quality,
            uint pitch, string face);
        // ★ 剪贴板（取代 WinForms Clipboard ✗ AOT 安全 ✓）
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr h);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint fmt, IntPtr mem);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string n);
        // ★ 文件对话框（comdlg32 ✓ 系统原生「另存为」）
        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OPENFILENAMEW o);

        private const uint OFN_OVERWRITEPROMPT = 0x00000002, OFN_HIDEREADONLY = 0x00000004,
                           OFN_PATHMUSTEXIST = 0x00000800;
        private const uint GMEM_MOVEABLE = 0x0002, CF_UNICODETEXT = 13;

        // ====================================================================
        //  状态
        // ====================================================================
        private static double _scale = 1.0;
        private static IntPtr _fallback;
        private static IntPtr _fontBase, _fontToc, _fontHeading, _fontBody, _fontPreview;
        private static readonly List<IntPtr> _fonts = new();
        private static IntPtr _hollowBrush;

        private static IntPtr _clsMainPtr, _clsFbPtr, _clsPanelPtr;

        // 主窗
        private static IntPtr _main, _top, _bottom, _toc, _split, _body;
        private static bool _wantFeedback;

        // 反馈窗
        private static IntPtr _fb, _fbTop, _fbHost, _fbBottom,
                               _cbKind, _txtSubject, _txtDetail, _txtContact, _ckDiag,
                               _preview, _status;

        // 每个静态标签 → 它所在面板的底色（用于 WM_CTLCOLORSTATIC ✓）
        private static readonly Dictionary<IntPtr, IntPtr> _labelBg = new();

        // ====================================================================
        //  日志（%TEMP%\help-win32.log）
        // ====================================================================
        private static void L(string s)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "help-win32.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>★ 逻辑像素 → 当前 DPI 下的物理像素。</summary>
        private static int S(int px) => (int)Math.Round(px * _scale);

        private static void Msg(string text, uint icon)
        {
            try { MessageBoxW(IntPtr.Zero, text ?? "", Program.AppName, MB_OK | icon); } catch { }
        }

        /// <summary>★ 启动期致命错误（取代 WinForms 的 MessageBox.Show ✗）：写日志 + 弹原生消息框 ✓</summary>
        internal static void Fatal(string text)
        {
            L("★ Fatal: " + text);
            Msg(text, MB_ICONERROR);
        }

        // ====================================================================
        //  入口
        // ====================================================================
        public static void Run(bool startOnFeedback)
        {
            // ★★ 2026-10-11 实测发现并修掉的真 bug（界面显示成 help.title 的根因之一）：
            //   WardenLang.Lang 是【惰性】加载的（第一次碰它才跑静态构造函数读语言包）
            //   而 Program.Sections 只解析一次就缓存 ✗
            //   于是「第一次用到 Lang」发生在解析章节标题的那一刻 →
            //   Titles 全部拿到底表里的 key 名，「标题」与「按钮」两套文案对不上 ✗
            //   修法：进任何界面代码之前先把语言包加载完 ✓ 再解析章节 ✓
            bool langLoaded = WardenLang.Lang.Loaded;   // 这一句就会触发静态构造 → 读语言包 ✓
            L("语言包已加载=" + langLoaded + " code=" + WardenLang.Lang.Code
              + " name=" + WardenLang.Lang.Name
              + (WardenLang.Lang.LoadError.Length > 0 ? " 载入错误=" + WardenLang.Lang.LoadError : ""));

            try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { }   // PER_MONITOR_AWARE_V2 ✓
            try { _scale = GetDpiForSystem() / 96.0; } catch { _scale = 1.0; }  // ★ 建窗之前定好 ✓
            if (_scale <= 0) { _scale = 1.0; }
            try
            {
                string forced = Environment.GetEnvironmentVariable("WARDEN_TEST_DPI");
                if (!string.IsNullOrEmpty(forced) && uint.TryParse(forced, out uint fd) && fd >= 96)
                {
                    _scale = fd / 96.0;
                    L("★ 测试钩子生效：按 DPI " + fd + " 渲染（缩放 " + _scale.ToString("0.00") + "x）");
                }
            }
            catch { }

            L("=== Help 启动 v" + Program.Version + " 缩放=" + _scale.ToString("0.00")
              + "x 语言=" + WardenLang.Lang.Code + " PID=" + Environment.ProcessId);
            L("第一章标题（语言包生效后）=「" + Program.Sections[0].Title + "」");   // ★ 自检：标题不能是 key 名
            _wantFeedback = startOnFeedback;

            BuildFonts();
            _hollowBrush = GetStockObject(5 /*NULL_BRUSH*/);

            IntPtr inst = GetModuleHandleW(null);
            _clsMainPtr = Marshal.StringToHGlobalUni(ClsMain);
            _clsFbPtr = Marshal.StringToHGlobalUni(ClsFb);
            var wcMain = MakeClass(_clsMainPtr);
            ushort a1 = RegisterClassExW(ref wcMain);
            L("RegisterClassExW(main) atom=" + a1 + " err=" + Marshal.GetLastWin32Error());
            var wcFb = MakeClass(_clsFbPtr);
            ushort a2 = RegisterClassExW(ref wcFb);
            L("RegisterClassExW(feedback) atom=" + a2 + " err=" + Marshal.GetLastWin32Error());
            _clsPanelPtr = Marshal.StringToHGlobalUni(ClsPanel);
            var wcPanel = MakePanelClass(_clsPanelPtr);
            ushort a3 = RegisterClassExW(ref wcPanel);
            L("RegisterClassExW(panel) atom=" + a3 + " err=" + Marshal.GetLastWin32Error());

            if (!ShowMain(inst))
            {
                Cleanup();
                return;
            }

            MSG msg;
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0))
            {
                // ★ IsDialogMessageW：Tab / Enter / Esc 生效 ✓
                IntPtr target = _fb != IntPtr.Zero ? _fb : _main;
                if (target != IntPtr.Zero && IsDialogMessageW(target, ref msg)) { continue; }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            L("主消息循环结束（主窗已关闭 ✓）");

            // ★ --feedback：主窗关掉之后再弹反馈窗（取代 WinForms 的 Shown 事件 ✓）
            if (_wantFeedback)
            {
                L("--feedback：主窗已关，接着开反馈窗");
                if (ShowFeedback(inst))
                {
                    while (GetMessageW(out msg, IntPtr.Zero, 0, 0))
                    {
                        if (_fb != IntPtr.Zero && IsDialogMessageW(_fb, ref msg)) { continue; }
                        TranslateMessage(ref msg);
                        DispatchMessageW(ref msg);
                    }
                }
            }

            Cleanup();
            L("=== Help 正常退出 ✓");
        }

        private static void Cleanup()
        {
            foreach (IntPtr f in _fonts)
            {
                if (f != IntPtr.Zero && f != _fallback) { DeleteObject(f); }
            }
            _fonts.Clear();
            if (_clsMainPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_clsMainPtr); _clsMainPtr = IntPtr.Zero; }
            if (_clsFbPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_clsFbPtr); _clsFbPtr = IntPtr.Zero; }
            if (_clsPanelPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_clsPanelPtr); _clsPanelPtr = IntPtr.Zero; }
        }

        /// <summary>★ 面板类：底色由 PanelWndProc 自己画（不依赖内核 STATIC 类的行为 ✓）</summary>
        private static WNDCLASSEXW MakePanelClass(IntPtr name) => new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&PanelWndProc,
            hInstance = GetModuleHandleW(null),
            hbrBackground = IntPtr.Zero,          // ★ 底色不靠类刷子，靠 PanelWndProc 自己填 ✓
            lpszClassName = name,
        };
        /// <summary>★ 两个窗口共用同一个类结构，回调不同（AOT 官方方式 ✓）</summary>
        private static WNDCLASSEXW MakeClass(IntPtr name)
        {
            IntPtr proc = name == _clsMainPtr
                ? (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&MainWndProc
                : (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&FbWndProc;
            return new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = proc,
                hInstance = GetModuleHandleW(null),
                hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
                lpszClassName = name,
            };
        }

        /// <summary>客户区尺寸 → 窗口尺寸（★ 不换算的话底部按钮会被标题栏+边框裁掉）。</summary>
        private static void ClientToWindow(int cw, int ch, uint style, out int ww, out int wh)
        {
            RECT r = new() { L = 0, T = 0, R = S(cw), B = S(ch) };
            AdjustWindowRectEx(ref r, style, false, 0);
            ww = r.R - r.L; wh = r.B - r.T;
        }

        // ====================================================================
        //  主窗
        // ====================================================================
        private static bool ShowMain(IntPtr inst)
        {
            string title = WardenLang.Lang.T("help.title");
            L("主窗标题解析 = 「" + title + "」 Loaded=" + WardenLang.Lang.Loaded
              + " Code=" + WardenLang.Lang.Code);
            ClientToWindow(820, 600, WS_CAPTION | WS_SYSMENU, out int ww, out int wh);
            L("主窗客户区 820x600 → 窗口 " + ww + "x" + wh);

            _main = CreateWindowExW(0, ClsMain, title, WS_CAPTION | WS_SYSMENU,
                0, 0, ww, wh, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (_main == IntPtr.Zero)
            {
                L("★ CreateWindowExW(main) 失败 err=" + Marshal.GetLastWin32Error());
                Msg("WARDEN 帮助中心窗口创建失败。", MB_ICONERROR);
                return false;
            }
            L("主窗 hwnd=" + _main + " 标题=" + title);
            SetWindowPos(_main, IntPtr.Zero,
                (GetSystemMetrics(0) - ww) / 2, (GetSystemMetrics(1) - wh) / 2, 0, 0,
                SWP_NOZORDER | SWP_NOSIZE);
            ShowWindow(_main, SW_SHOW);
            UpdateWindow(_main);
            SetFocus(_main);
            return true;
        }

        private static void BuildMainControls(IntPtr h)
        {
            L("WM_CREATE(主窗) hwnd=" + h);

            // ★★ 建完之后必须把两层面板摆回窗口的上下两端 ✗ 光靠 WM_CREATE 期间建控件根本不够 ✓
            //    实测坑①：STATIC 面板建出来不会自己铺满 → 窗口中间留一大片白 ✗
            //    实测坑②：后建的 STATIC 在 z 序上【盖住】先建的 ✗ 顶栏蓝底会被底栏灰底压掉 ✓
            //      → 所以建完最后用 SetWindowPos 把顶栏提到最前 ✓（WinForms 的 Dock 顺序也是这个道理 ✓）

            _top = CreateWindowExW(0, ClsPanel, "", WS_CHILD | WS_VISIBLE | SS_LEFT,
                0, 0, S(820), S(60), h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Lab(_top, WardenLang.Lang.T("help.heading"), 18, 8, 600, 26, _fontHeading);
            Lab(_top, WardenLang.Lang.T("help.subheading"), 20, 34, 700, 20, _fontBase);
            PaintPanel(_top, CLR_BLUE);

            _bottom = CreateWindowExW(0, ClsPanel, "", WS_CHILD | WS_VISIBLE | SS_LEFT,
                0, 0, S(820), S(58), h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            PaintPanel(_bottom, CLR_BOTTOM);

            // ★ 按钮 ID 全部放进 hMenu ✓
            Btn(h, WardenLang.Lang.T("help.btn.feedback"), 16, 542 + 14, 120, 32, ID_BTN_FEEDBACK, false);   // ★ 父窗口改主窗 ✓ 否则 WM_COMMAND 被 STATIC 吞掉 ✗
            Btn(h, WardenLang.Lang.T("help.btn.openPage"), 144, 542 + 14, 130, 32, ID_BTN_OPENPAGE, false);
            Btn(h, WardenLang.Lang.T("help.btn.settings"), 282, 542 + 14, 130, 32, ID_BTN_SETTINGS, false);
            Btn(h, WardenLang.Lang.T("common.close"), 686, 542 + 14, 118, 32, ID_BTN_CLOSE, true);

            // 左侧目录：自绘（行高 30 ✓ 无边框 ✓）
            _toc = CreateWindowExW(0, "LISTBOX", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | LBS_NOTIFY | LBS_OWNERDRAWFIXED,
                S(0), S(60), S(220), S(482), h, (IntPtr)ID_TOC, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(_toc, LB_SETITEMHEIGHT, IntPtr.Zero, (IntPtr)S(30));

            _split = CreateWindowExW(0, ClsPanel, "", WS_CHILD | WS_VISIBLE | SS_LEFT,
                S(220), S(60), S(1), S(482), h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            PaintPanel(_split, CLR_SPLIT);

            // 右侧正文：只读多行 ✓ 白底 ✓
            _body = CreateWindowExW(0, "EDIT", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | ES_LEFT | ES_MULTILINE | ES_READONLY,
                S(221), S(60), S(599), S(482), h, (IntPtr)ID_BODY, IntPtr.Zero, IntPtr.Zero);

            L("主窗控件: top=" + _top + " bottom=" + _bottom + " toc=" + _toc
              + " split=" + _split + " body=" + _body + " err=" + Marshal.GetLastWin32Error());

            LayoutMain();
            L("主窗布局完成：顶栏(0,0,820,60) 菜单(0,60,220,482) 分隔线(220,60,1,482) 正文(221,60,599,482) 底栏(0,542,820,58)");

            foreach (IntPtr c in new[] { _toc, _body })
            {
                if (c != IntPtr.Zero) { SendMessageW(c, WM_SETFONT, _fontBase, (IntPtr)1); }
            }
            if (_toc != IntPtr.Zero) { SendMessageW(_toc, WM_SETFONT, _fontToc, (IntPtr)1); }

            // 填充目录（原文案前缀两个空格 ✓）
            var secs = Program.Sections;
            for (int i = 0; i < secs.Length; i++)
            {
                SendMessageW(_toc, LB_ADDSTRING, IntPtr.Zero, "  " + secs[i].Title);
            }
            L("目录填充完成 共 " + secs.Length + " 节");
            SendMessageW(_toc, LB_SETCURSEL, IntPtr.Zero, IntPtr.Zero);   // SelectedIndex = 0 ✓
            ShowSection(0);
            InvalidateRect(h, IntPtr.Zero, true);
        }

        /// <summary>
        /// ★★ 主窗布局（2026-10-11 实测发现必须显式做 ✗ 光靠 CreateWindowExW 的初始矩形不够 ✓）
        ///   WinForms 的 Dock 会替我们算这些位置；原生实现里要自己摆 ✓
        ///     顶栏   客户区 (0,   0) 820x60    ← Dock.Top    ✓
        ///     底栏   客户区 (0, 542) 820x58    ← Dock.Bottom ✓
        ///     左目录 客户区 (0,  60) 220x482   ← Dock.Left   ✓
        ///     分隔线 客户区 (220,60)   1x482                 ✓
        ///     正文   客户区 (221,60) 599x482   ← Fill         ✓
        ///   z 序：顶栏必须最后提到最前 ✗ 否则被后建的底栏盖掉 ✓
        /// </summary>
        private static void LayoutMain()
        {
            if (_top != IntPtr.Zero) { SetWindowPos(_top, IntPtr.Zero, 0, 0, S(820), S(60), SWP_NOZORDER | SWP_NOACTIVATE); }
            if (_bottom != IntPtr.Zero) { SetWindowPos(_bottom, IntPtr.Zero, 0, S(542), S(820), S(58), SWP_NOZORDER | SWP_NOACTIVATE); }
            if (_toc != IntPtr.Zero) { SetWindowPos(_toc, IntPtr.Zero, 0, S(60), S(220), S(482), SWP_NOZORDER | SWP_NOACTIVATE); }
            if (_split != IntPtr.Zero) { SetWindowPos(_split, IntPtr.Zero, S(220), S(60), S(1), S(482), SWP_NOZORDER | SWP_NOACTIVATE); }
            if (_body != IntPtr.Zero) { SetWindowPos(_body, IntPtr.Zero, S(221), S(60), S(599), S(482), SWP_NOZORDER | SWP_NOACTIVATE); }
            // ★ 顶栏提到最前（HWND_TOP=0），底栏紧随其后 → 两者不重叠也不会被中间区盖住 ✓
            if (_top != IntPtr.Zero)
            {
                SetWindowPos(_top, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            if (_bottom != IntPtr.Zero)
            {
                SetWindowPos(_bottom, _top, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            // ★★ 实测坑（最费时间的一个）：STATIC 类面板收不到 WM_PAINT ✗
            //    实测证据：InvalidateRect + PostMessage(WM_PAINT) 之后，
            //              日志里 WM_PAINT 仍然只到达主窗，面板一个都没到 ✗
            //    修法：用 SendMessageW 同步送 WM_PAINT（不走消息队列 ✓ 一定能到 ✓）
            foreach (IntPtr p in new[] { _top, _bottom, _split })
            {
                if (p == IntPtr.Zero) { continue; }
                InvalidateRect(p, IntPtr.Zero, true);
                SendMessageW(p, WM_PAINT, IntPtr.Zero, IntPtr.Zero);
            }
        }

        /// <summary>★ 反馈窗布局：顶栏 52 / 承载区 500 / 底栏 88（客户区 760x640 ✓）</summary>
        private static void LayoutFeedback()
        {
            if (_fbTop != IntPtr.Zero) { SetWindowPos(_fbTop, IntPtr.Zero, 0, 0, S(760), S(52), SWP_NOZORDER | SWP_NOACTIVATE); }
            if (_fbHost != IntPtr.Zero) { SetWindowPos(_fbHost, IntPtr.Zero, 0, S(52), S(760), S(500), SWP_NOZORDER | SWP_NOACTIVATE); }
            if (_fbBottom != IntPtr.Zero) { SetWindowPos(_fbBottom, IntPtr.Zero, 0, S(552), S(760), S(88), SWP_NOZORDER | SWP_NOACTIVATE); }
            if (_fbTop != IntPtr.Zero)
            {
                SetWindowPos(_fbTop, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            if (_fbBottom != IntPtr.Zero)
            {
                SetWindowPos(_fbBottom, _fbTop, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            foreach (IntPtr p in new[] { _fbTop, _fbHost, _fbBottom })
            {
                if (p == IntPtr.Zero) { continue; }
                InvalidateRect(p, IntPtr.Zero, true);
                SendMessageW(p, WM_PAINT, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static void ShowSection(int idx)
        {
            var secs = Program.Sections;
            if (idx < 0 || idx >= secs.Length) { return; }
            string title = secs[idx].Title ?? "";
            string body = secs[idx].Body ?? "";
            // ★ 标题 + 两个换行 + 正文 —— 与旧版 AppendText(sec.title + NL + NL) 完全一致 ✓
            string text = title + "\r\n\r\n" + body;
            if (_body != IntPtr.Zero)
            {
                bool setOk = SetWindowTextW(_body, text);
                int readLen = GetWindowTextLengthW(_body);
                L("  SetWindowTextW 返回=" + setOk + " 之后窗口文本长度=" + readLen
                  + " 期望=" + text.Length + " err=" + Marshal.GetLastWin32Error());
                SendMessageW(_body, EM_SETSEL, IntPtr.Zero, IntPtr.Zero);         // SelectionStart = 0 ✓
                SendMessageW(_body, EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);    // ScrollToCaret ✓
                InvalidateRect(_body, IntPtr.Zero, true);
            }
            L("切换章节 #" + idx + " 「" + title + "」正文 " + body.Length + " 字");
        }

        // ====================================================================
        //  反馈窗
        // ====================================================================
        private static bool ShowFeedback(IntPtr inst)
        {
            string title = WardenLang.Lang.T("fb.title");
            ClientToWindow(760, 640, WS_CAPTION | WS_SYSMENU, out int ww, out int wh);
            _fb = CreateWindowExW(0, ClsFb, title, WS_CAPTION | WS_SYSMENU,
                0, 0, ww, wh, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (_fb == IntPtr.Zero)
            {
                L("★ CreateWindowExW(feedback) 失败 err=" + Marshal.GetLastWin32Error());
                Msg("反馈窗口创建失败。", MB_ICONERROR);
                return false;
            }
            L("反馈窗 hwnd=" + _fb + " 客户区 760x640 → 窗口 " + ww + "x" + wh + " 标题=" + title);
            SetWindowPos(_fb, IntPtr.Zero,
                (GetSystemMetrics(0) - ww) / 2, (GetSystemMetrics(1) - wh) / 2, 0, 0,
                SWP_NOZORDER | SWP_NOSIZE);
            ShowWindow(_fb, SW_SHOW);
            UpdateWindow(_fb);
            SetFocus(_fb);
            return true;
        }

        private static void BuildFeedbackControls(IntPtr h)
        {
            L("WM_CREATE(反馈窗) hwnd=" + h);
            _fontPreview = NewFont(9f, 400, "Consolas");   // 预览用等宽字体 ✓

            _fbTop = CreateWindowExW(0, ClsPanel, "", WS_CHILD | WS_VISIBLE | SS_LEFT,
                0, 0, S(760), S(52), h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Lab(_fbTop, WardenLang.Lang.T("fb.heading"), 18, 12, 700, 28, _fontHeading);
            PaintPanel(_fbTop, CLR_BLUE);

            // 中间承载区（旧版是 AutoScroll 的 host 面板 ✓ 坐标相对它算 ✓）
            _fbHost = CreateWindowExW(0, ClsPanel, "", WS_CHILD | WS_VISIBLE | SS_LEFT,
                0, 0, S(760), S(500), h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            PaintPanel(_fbHost, CLR_WHITE);

            int y = 62;
            // ---- 类型 ----
            Lab(_fbHost, WardenLang.Lang.T("fb.kind"), 16, y, 70, 22, _fontBase);
            _cbKind = CreateWindowExW(0, "COMBOBOX", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST,
                S(92), S(y - 3), S(220), S(24), _fbHost, (IntPtr)ID_FB_KIND, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(_cbKind, CB_ADDSTRING, IntPtr.Zero, WardenLang.Lang.T("fb.kind.bug"));
            SendMessageW(_cbKind, CB_ADDSTRING, IntPtr.Zero, WardenLang.Lang.T("fb.kind.idea"));
            SendMessageW(_cbKind, CB_ADDSTRING, IntPtr.Zero, WardenLang.Lang.T("fb.kind.other"));
            SendMessageW(_cbKind, CB_SETCURSEL, IntPtr.Zero, IntPtr.Zero);       // SelectedIndex = 0 ✓
            SendMessageW(_cbKind, CB_SETMINVISIBLE, (IntPtr)3, IntPtr.Zero);     // 下拉能看到三项 ✓
            y += 36;

            // ---- 标题 ----
            Lab(_fbHost, WardenLang.Lang.T("fb.subject"), 16, y, 70, 22, _fontBase);
            _txtSubject = CreateWindowExW(0, "EDIT", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | ES_LEFT,
                S(92), S(y - 3), S(630), S(26), _fbHost, (IntPtr)ID_FB_SUBJECT, IntPtr.Zero, IntPtr.Zero);
            y += 36;

            // ---- 详情 ----
            Lab(_fbHost, WardenLang.Lang.T("fb.detail"), 16, y, 120, 22, _fontBase);
            Lab(_fbHost, WardenLang.Lang.T("fb.detailHint"), 140, y, 580, 22, _fontBase);
            y += 24;
            _txtDetail = CreateWindowExW(0, "EDIT", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | WS_VSCROLL
                | ES_LEFT | ES_MULTILINE | ES_AUTOVSCROLL,
                S(16), S(y), S(706), S(120), _fbHost, (IntPtr)ID_FB_DETAIL, IntPtr.Zero, IntPtr.Zero);
            y += 130;

            // ---- 联系方式 ----
            Lab(_fbHost, WardenLang.Lang.T("fb.contact"), 16, y, 70, 22, _fontBase);
            _txtContact = CreateWindowExW(0, "EDIT", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | ES_LEFT,
                S(92), S(y - 3), S(300), S(26), _fbHost, (IntPtr)ID_FB_CONTACT, IntPtr.Zero, IntPtr.Zero);
            Lab(_fbHost, WardenLang.Lang.T("fb.contactHint"), 400, y, 320, 22, _fontBase);
            y += 36;

            // ---- 附带诊断信息（默认勾选 ✓）----
            _ckDiag = CreateWindowExW(0, "BUTTON", WardenLang.Lang.T("fb.attachDiag"),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                S(16), S(y), S(700), S(24), _fbHost, (IntPtr)ID_FB_DIAG, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(_ckDiag, BM_SETCHECK, (IntPtr)1, IntPtr.Zero);          // Checked = true ✓
            y += 26;
            Lab(_fbHost, WardenLang.Lang.T("fb.diagHint"), 34, y, 680, 34, _fontBase);
            y += 40;

            // ---- 预览 ----
            Lab(_fbHost, WardenLang.Lang.T("fb.preview"), 16, y, 200, 22, _fontBody);
            y += 24;
            _preview = CreateWindowExW(0, "EDIT", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | ES_LEFT | ES_MULTILINE | ES_READONLY,
                S(16), S(y), S(706), S(130), _fbHost, (IntPtr)ID_FB_PREVIEW, IntPtr.Zero, IntPtr.Zero);

            // ---- 底部按钮 ----
            _fbBottom = CreateWindowExW(0, ClsPanel, "", WS_CHILD | WS_VISIBLE | SS_LEFT,
                0, 0, S(760), S(88), h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            PaintPanel(_fbBottom, CLR_BOTTOM);

            Btn(_fbBottom, WardenLang.Lang.T("fb.btn.github"), 16, 10, 180, 34, ID_FB_GITHUB, false);
            Btn(_fbBottom, WardenLang.Lang.T("fb.btn.copy"), 204, 10, 180, 34, ID_FB_COPY, false);
            Btn(_fbBottom, WardenLang.Lang.T("fb.btn.save"), 392, 10, 180, 34, ID_FB_SAVE, false);
            Btn(_fbBottom, WardenLang.Lang.T("common.close"), 652, 10, 120, 34, ID_FB_CLOSE, true);

            _status = CreateWindowExW(0, "STATIC", WardenLang.Lang.T("fb.noAccountHint"),
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                S(16), S(52), S(720), S(30), _fbBottom, (IntPtr)ID_FB_STATUS, IntPtr.Zero, IntPtr.Zero);
            _labelBg[_status] = Bg(CLR_BOTTOM);
            SendMessageW(_status, WM_SETFONT, _fontBase, (IntPtr)1);

            L("反馈窗控件: kind=" + _cbKind + " subject=" + _txtSubject + " detail=" + _txtDetail
              + " contact=" + _txtContact + " diag=" + _ckDiag + " preview=" + _preview
              + " status=" + _status + " err=" + Marshal.GetLastWin32Error());

            foreach (IntPtr c in new[] { _cbKind, _txtSubject, _txtDetail, _txtContact, _ckDiag })
            {
                if (c != IntPtr.Zero) { SendMessageW(c, WM_SETFONT, _fontBase, (IntPtr)1); }
            }
            if (_preview != IntPtr.Zero) { SendMessageW(_preview, WM_SETFONT, _fontPreview, (IntPtr)1); }

            RefreshPreview();
            LayoutFeedback();
            L("反馈窗布局完成：顶栏(0,0,760,52) 承载区(0,52,760,500) 底栏(0,552,760,88)");
            InvalidateRect(h, IntPtr.Zero, true);
        }

        /// <summary>
        /// 静态标签。
        /// ★ 2026-10-11 实测修掉的真 bug：原来给标签加了 WS_EX_TRANSPARENT，
        ///   意思是"父窗先画好底色，标签只写字" ✗ 但父窗的 WM_PAINT 我根本没处理 →
        ///   顶栏的蓝底从来没被画出来（实测采样整片都是 #F0F0F0 ✗）
        ///   现在改成正常标签 + WM_CTLCOLORSTATIC 返回所在面板的底色 ✓（对照组样板就是这么做 ✓）
        /// </summary>
        private static void Lab(IntPtr parent, string text, int x, int y, int w, int h, IntPtr font)
        {
            IntPtr c = CreateWindowExW(0, "STATIC", text, WS_CHILD | WS_VISIBLE | SS_LEFT,
                S(x), S(y), S(w), S(h), parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (c != IntPtr.Zero)
            {
                SendMessageW(c, WM_SETFONT, font, (IntPtr)1);
                _labelBg[c] = Bg(CLR_WHITE);   // 先按承载区白底登记，PaintPanel 会覆盖成面板底色 ✓
            }
        }

        private static void Btn(IntPtr parent, string text, int x, int y, int w, int h, int id, bool def)
        {
            IntPtr c = CreateWindowExW(0, "BUTTON", text,
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | (def ? BS_DEFPUSHBUTTON : BS_PUSHBUTTON),
                S(x), S(y), S(w), S(h), parent, (IntPtr)id, IntPtr.Zero, IntPtr.Zero);
            if (c != IntPtr.Zero) { SendMessageW(c, WM_SETFONT, _fontBase, (IntPtr)1); }
        }

        /// <summary>
        /// <summary>
        /// 面板底色登记（★ 不登记到 _labelBg：面板的底色由 WM_ERASEBKGND 自己填 ✓）
        /// 【为什么不用 WM_PAINT】实测：STATIC 类面板收不到自己的 WM_PAINT ✗
        ///   而 WM_ERASEBKGND 是父窗在绘制时发过来的，稳定到达 ✓
        /// </summary>
        private static void PaintPanel(IntPtr panel, int color)
        {
            if (panel == _top) { _topColor = color; }
            else if (panel == _bottom) { _bottomColor = color; }
            else if (panel == _split) { _splitColor = color; }
            else if (panel == _fbTop) { _fbTopColor = color; }
            else if (panel == _fbHost) { _fbHostColor = color; }
            else if (panel == _fbBottom) { _fbBottomColor = color; }
        }

        /// <summary>这个句柄是不是面板？是就返回它的底色（不是返回 -1 ✓）</summary>
        private static int PanelColor(IntPtr h)
        {
            if (h == _top) { return _topColor; }
            if (h == _bottom) { return _bottomColor; }
            if (h == _split) { return _splitColor; }
            if (h == _fbTop) { return _fbTopColor; }
            if (h == _fbHost) { return _fbHostColor; }
            if (h == _fbBottom) { return _fbBottomColor; }
            return -1;
        }

        private static int _topColor = -1, _bottomColor = -1, _splitColor = -1,
                           _fbTopColor = -1, _fbHostColor = -1, _fbBottomColor = -1;
        private static bool _paintedLogged;
        private static int _panelMsgProbe;
        private static int _paintProbe;

        /// <summary>顶栏两行白字（旧版是 top 面板里的两个 SS_LEFT 标签 ✓ 位置/字号/颜色一字不差 ✓）</summary>
        private static void DrawTopBarText(IntPtr dc)
        {
            int oldBk = SetBkMode(dc, TRANSPARENT);
            IntPtr oldF = SelectObject(dc, _fontHeading);
            SetTextColor(dc, CLR_WHITE);
            RECT r1 = new() { L = S(18), T = S(8), R = S(618), B = S(34) };
            DrawTextW(dc, WardenLang.Lang.T("help.heading"), -1, ref r1,
                DT_LEFT | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(dc, oldF);

            oldF = SelectObject(dc, _fontBase);
            SetTextColor(dc, CLR_SUBHEAD);
            RECT r2 = new() { L = S(20), T = S(34), R = S(720), B = S(54) };
            DrawTextW(dc, WardenLang.Lang.T("help.subheading"), -1, ref r2,
                DT_LEFT | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(dc, oldF);
            SetBkMode(dc, oldBk);
        }

        /// <summary>反馈窗顶栏一行白字（对应 fb.heading 标签 ✓）</summary>
        private static void DrawFeedbackTopText(IntPtr dc)
        {
            int oldBk = SetBkMode(dc, TRANSPARENT);
            IntPtr oldF = SelectObject(dc, _fontHeading);
            SetTextColor(dc, CLR_WHITE);
            RECT r1 = new() { L = S(18), T = S(12), R = S(718), B = S(40) };
            DrawTextW(dc, WardenLang.Lang.T("fb.heading"), -1, ref r1,
                DT_LEFT | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(dc, oldF);
            SetBkMode(dc, oldBk);
        }

        private static readonly Dictionary<int, IntPtr> _brushes = new();
        private static IntPtr Bg(int color)
        {
            if (!_brushes.TryGetValue(color, out IntPtr b) || b == IntPtr.Zero)
            {
                b = CreateSolidBrush(color);
                _brushes[color] = b;
            }
            return b;
        }

        // ====================================================================
        //  反馈窗逻辑（与旧版 FeedbackForm 一一对应 ✓）
        // ====================================================================
        private static string KindText()
        {
            long i = _cbKind != IntPtr.Zero ? SendMessageW(_cbKind, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64() : 0;
            if (i == 1) { return WardenLang.Lang.T("fb.kind.idea"); }
            if (i == 2) { return WardenLang.Lang.T("fb.kind.other"); }
            return WardenLang.Lang.T("fb.kind.bug");
        }

        private static string TextOf(IntPtr h)
        {
            if (h == IntPtr.Zero) { return ""; }
            int n = GetWindowTextLengthW(h);
            if (n <= 0) { return ""; }
            char[] buf = new char[n + 1];
            int got = GetWindowTextW(h, buf, buf.Length);
            return got <= 0 ? "" : new string(buf, 0, got);
        }

        private static bool IsChecked(IntPtr h)
            => h != IntPtr.Zero && SendMessageW(h, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt64() == 1;

        private static void SetStatus(string s)
        {
            if (_status != IntPtr.Zero) { SetWindowTextW(_status, s ?? ""); }
            L("状态行 = " + s);
        }

        private static string BuildReport()
            => HelpCore.BuildReport(KindText(), TextOf(_txtContact), TextOf(_txtDetail), IsChecked(_ckDiag));

        private static void RefreshPreview()
        {
            if (_preview == IntPtr.Zero) { return; }
            SetWindowTextW(_preview, BuildReport());
        }

        /// <summary>★ 方式一：浏览器打开预填好的 issue 页（客户端里绝不放 Token ✗）</summary>
        private static void OpenOnGitHub()
        {
            string title = TextOf(_txtSubject).Trim();
            if (title.Length == 0) { title = WardenLang.Lang.T("fb.defaultTitle"); }

            string url = "https://github.com/" + Program.RepoOwner + "/" + Program.RepoName
                + "/issues/new?title=" + Uri.EscapeDataString("[" + KindText() + "] " + title)
                + "&body=" + Uri.EscapeDataString(BuildReport());

            // ★ URL 太长会被截断 → 明确询问，不静默丢内容 ✓
            if (url.Length > 7000)
            {
                int r = MessageBoxW(_fb,
                    WardenLang.Lang.T("fb.tooLong") + Environment.NewLine + Environment.NewLine
                    + WardenLang.Lang.T("fb.tooLongAsk"),
                    Program.AppName, MB_YESNO | MB_ICONWARNING);
                if (r != IDYES) { return; }
                url = "https://github.com/" + Program.RepoOwner + "/" + Program.RepoName
                    + "/issues/new?title="
                    + Uri.EscapeDataString("[" + KindText() + "] " + title);
                CopyToClipboard(true);
                SetStatus(WardenLang.Lang.T("fb.copiedForPaste"));
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                SetStatus(WardenLang.Lang.T("fb.openedInBrowser"));
                L("已用浏览器打开 issue 页（长度 " + url.Length + "）");
            }
            catch (Exception e)
            {
                MessageBoxW(_fb, WardenLang.Lang.T("help.openFailed") + Environment.NewLine + e.Message,
                    Program.AppName, MB_OK | MB_ICONWARNING);
            }
        }

        private static void CopyToClipboard(bool silent)
        {
            try
            {
                SetClipboard(BuildReport());
                SetStatus(WardenLang.Lang.T("fb.copied"));
                if (!silent)
                {
                    MessageBoxW(_fb, WardenLang.Lang.T("fb.copiedLong"), Program.AppName,
                        MB_OK | MB_ICONINFORMATION);
                }
            }
            catch (Exception e)
            {
                MessageBoxW(_fb, e.Message, Program.AppName, MB_OK | MB_ICONWARNING);
            }
        }

        /// <summary>★ 原生剪贴板（取代 WinForms Clipboard ✗）：GlobalAlloc + SetClipboardData ✓</summary>
        private static void SetClipboard(string text)
        {
            if (!OpenClipboard(_fb)) { throw new InvalidOperationException("打不开剪贴板"); }
            IntPtr mem = IntPtr.Zero;
            try
            {
                EmptyClipboard();
                int bytes = (text.Length + 1) * 2;
                mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (mem == IntPtr.Zero) { throw new InvalidOperationException("剪贴板内存分配失败"); }
                IntPtr dst = GlobalLock(mem);
                if (dst == IntPtr.Zero) { throw new InvalidOperationException("剪贴板内存加锁失败"); }
                byte[] raw = new byte[bytes];
                fixed (char* src = text)
                {
                    Marshal.Copy((IntPtr)src, raw, 0, text.Length * 2);
                }
                Marshal.Copy(raw, 0, dst, bytes);
                GlobalUnlock(mem);
                if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero)
                {
                    throw new InvalidOperationException("写入剪贴板失败");
                }
                mem = IntPtr.Zero;   // ★ 所有权已交给系统 ✓ 不能再释放 ✓
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>★ 方式三：comdlg32 系统原生「另存为」→ UTF-8（无 BOM）✓</summary>
        private static void SaveToFile()
        {
            string path;
            try
            {
                path = PickSaveFile("WARDEN-反馈-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            }
            catch (Exception e)
            {
                MessageBoxW(_fb, e.Message, Program.AppName, MB_OK | MB_ICONWARNING);
                return;
            }
            if (path == null) { L("另存为：用户取消 ✓"); return; }
            try
            {
                File.WriteAllText(path, BuildReport(), new UTF8Encoding(false));
                L("另存为成功：" + path + "  " + new FileInfo(path).Length + " B");
                SetStatus(WardenLang.Lang.T("fb.saved", path));
            }
            catch (Exception e)
            {
                MessageBoxW(_fb, e.Message, Program.AppName, MB_OK | MB_ICONWARNING);
            }
        }

        /// <summary>系统原生「另存为」；用户取消返回 null ✓</summary>
        private static string PickSaveFile(string defaultName)
        {
            IntPtr pFilter = IntPtr.Zero, pTitle = IntPtr.Zero, pDefExt = IntPtr.Zero, pFile = IntPtr.Zero;
            try
            {
                // "文本文件 (*.txt)|*.txt|Markdown (*.md)|*.md" → Win32 双 \0 形式 ✓
                pFilter = Marshal.StringToHGlobalUni(
                    "文本文件 (*.txt)" + NUL + "*.txt" + NUL + "Markdown (*.md)" + NUL + "*.md" + NUL + NUL);
                pTitle = Marshal.StringToHGlobalUni("保存反馈内容");
                pDefExt = Marshal.StringToHGlobalUni("txt");
                const int maxFile = 4096;
                pFile = Marshal.AllocHGlobal(maxFile * 2);
                for (int i = 0; i < maxFile; i++) { Marshal.WriteInt16(pFile, i * 2, 0); }
                fixed (char* s = defaultName)
                {
                    int n = Math.Min(defaultName.Length, maxFile - 1);
                    for (int i = 0; i < n; i++) { Marshal.WriteInt16(pFile, i * 2, (short)s[i]); }
                    Marshal.WriteInt16(pFile, n * 2, 0);
                }

                var ofn = new OPENFILENAMEW
                {
                    lStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                    hwndOwner = _fb,
                    lpstrFilter = pFilter,
                    nFilterIndex = 1,
                    lpstrFile = pFile,
                    nMaxFile = maxFile,
                    lpstrTitle = pTitle,
                    lpstrDefExt = pDefExt,
                    Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY,
                };
                if (!GetSaveFileNameW(ref ofn)) { return null; }   // 用户取消 ✓
                return Marshal.PtrToStringUni(pFile);
            }
            finally
            {
                if (pFilter != IntPtr.Zero) { Marshal.FreeHGlobal(pFilter); }
                if (pTitle != IntPtr.Zero) { Marshal.FreeHGlobal(pTitle); }
                if (pDefExt != IntPtr.Zero) { Marshal.FreeHGlobal(pDefExt); }
                if (pFile != IntPtr.Zero) { Marshal.FreeHGlobal(pFile); }
            }
        }

        // ====================================================================
        //  共用：打开浏览器 / 启动同目录兄弟程序
        // ====================================================================
        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) { return; }
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                L("打开链接：" + url);
            }
            catch (Exception e)
            {
                MessageBoxW(_main, WardenLang.Lang.T("help.openFailed") + Environment.NewLine + e.Message,
                    Program.AppName, MB_OK | MB_ICONWARNING);
            }
        }

        /// <summary>
        /// ★ 打开同目录下的兄弟程序。
        /// 【为什么找不到时要明确报错】正常安装时五个组件是强制安装的，
        ///   找不到 = 安装不完整 → 必须告诉用户，不能静默失败 ✗
        /// </summary>
        internal static void LaunchSibling(string exe, string args)
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                string path = Path.Combine(dir, exe);
                if (!File.Exists(path))
                {
                    L("★ 兄弟程序缺失：" + path);
                    MessageBoxW(IntPtr.Zero, WardenLang.Lang.T("help.siblingMissing", exe),
                        Program.AppName, MB_OK | MB_ICONWARNING);
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = args ?? "",
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                });
                L("已启动兄弟程序：" + path);
            }
            catch (Exception e)
            {
                MessageBoxW(IntPtr.Zero, e.Message, Program.AppName, MB_OK | MB_ICONWARNING);
            }
        }

        // ====================================================================
        //  字体
        // ====================================================================
        private static IntPtr NewFont(float pt, int weight, string face)
        {
            int h = -(int)Math.Round(pt * _scale * 96.0 / 72.0);
            IntPtr f = CreateFontW(h, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 0, 0, face);
            if (f == IntPtr.Zero) { f = GetStockObject(DEFAULT_GUI_FONT); }   // ★ gdi32 ✓
            if (f != IntPtr.Zero) { _fonts.Add(f); }
            return f;
        }

        private static void BuildFonts()
        {
            if (_fonts.Count > 0) { return; }               // 幂等 ✓ 只建一次
            _fallback = GetStockObject(DEFAULT_GUI_FONT);
            _fontBase = NewFont(9.5f, 400, "Microsoft YaHei UI");    // 窗体默认 9.5pt ✓
            _fontToc = NewFont(10f, 400, "Microsoft YaHei UI");      // 目录 10pt ✓
            _fontHeading = NewFont(13f, 700, "Microsoft YaHei UI");  // 大标题 13pt 粗 ✓
            _fontBody = NewFont(10f, 700, "Microsoft YaHei UI");     //「预览」小标题 10pt 粗 ✓
            L("字体创建 base=" + _fontBase + " toc=" + _fontToc + " heading=" + _fontHeading
              + " bodyBold=" + _fontBody + " preview=" + _fontPreview + " fallback=" + _fallback);
        }

        // ====================================================================
        //  消息处理
        // ====================================================================
        private static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l, bool isFb)
        {
            // ★ 诊断（2026-10-11）：无条件记录每条消息 —— 用来判定 WM_COMMAND 到底有没有到主窗
            //   （Help 四个按钮全无反应，日志里连一条点击记录都没有 ✓ 必须看到 0x111 才能往下查）
            try { if (m == 0x0111 || m == 0x0100 || m == 0x0201) { L("WndProc msg=0x" + m.ToString("X") + " hwnd=" + h + " w=" + w.ToInt64() + " isFb=" + isFb); } } catch { }
            switch (m)
            {
                case WM_CREATE:
                    if (isFb) { BuildFeedbackControls(h); } else { BuildMainControls(h); }
                    return IntPtr.Zero;

                case WM_COMMAND:
                {
                    int id = (int)(w.ToInt64() & 0xFFFF);
                    int code = (int)((w.ToInt64() >> 16) & 0xFFFF);
                    return isFb ? OnFeedbackCommand(h, id, code) : OnMainCommand(h, id, code);
                }

                case WM_MEASUREITEM:
                {
                    var mis = *(MEASUREITEMSTRUCT*)l;
                    if (mis.CtlID == ID_TOC)
                    {
                        mis.itemHeight = (uint)S(30);        // ★ 行高 30 ✓
                        *(MEASUREITEMSTRUCT*)l = mis;
                        return (IntPtr)1;
                    }
                    return IntPtr.Zero;
                }

                case WM_DRAWITEM:
                    DrawTocItem(l);
                    return (IntPtr)1;

                case WM_PAINT:
                    // ★★ 2026-10-11 实测结论（这一版是被实测逼出来的 ✓）：
                    //   子面板（STATIC 类）【收不到自己的 WM_PAINT】✗
                    //   实测证据：InvalidateRect + PostMessage(WM_PAINT) + SendMessageW(WM_PAINT)
                    //             三种都试过，日志里面板一条 WM_PAINT 都没到 ✗
                    //   修法：面板底色改由 WM_ERASEBKGND 填（父窗绘制时发的，稳定到达 ✓ 见下面那条 ✓）
                    //   主窗自己：DefWindowProc 会 BeginPaint/EndPaint 清背景 ✓ 不需要额外处理 ✓
                    break;

                case WM_CTLCOLORSTATIC:
                {
                    // ★ 让每个标签用自己那块面板的底色（顶栏蓝 / 底栏浅灰 / 承载区白 ✓）
                    //   ★ 面板自己【不登记】（见 PaintPanel：面板走 WM_ERASEBKGND 自己填 ✓）
                    if (_labelBg.TryGetValue(w, out IntPtr br) && br != IntPtr.Zero) { return br; }
                    return DefWindowProcW(h, m, w, l);
                }

                case WM_GETMINMAXINFO:
                {
                    // ★ WinForms 的 MinimumSize 等价物（主窗 700x500 / 反馈窗 680x560 ✓）
                    var mmi = *(MINMAXINFO*)l;
                    ClientToWindow(isFb ? 680 : 700, isFb ? 560 : 500, WS_OVERLAPPEDWINDOW,
                        out int mw, out int mh);
                    mmi.ptMinTrackSize.X = mw; mmi.ptMinTrackSize.Y = mh;
                    *(MINMAXINFO*)l = mmi;
                    return IntPtr.Zero;
                }

                case WM_SETFOCUS:
                {
                    // ★ 与 WinForms 一致：窗口拿到焦点就转给第一个可 Tab 的控件
                    IntPtr first = isFb ? _cbKind : _toc;
                    if (first != IntPtr.Zero) { SetFocus(first); }
                    return IntPtr.Zero;
                }

                case WM_CLOSE:
                    DestroyWindow(h);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    L("WM_DESTROY hwnd=" + h + "（" + (isFb ? "反馈窗" : "主窗") + "）");
                    if (isFb) { _fb = IntPtr.Zero; } else { _main = IntPtr.Zero; }
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return DefWindowProcW(h, m, w, l);
        }

        private static IntPtr OnMainCommand(IntPtr h, int id, int code)
        {
            if (id == ID_TOC && code == LBN_SELCHANGE)
            {
                int sel = (int)SendMessageW(_toc, LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64();
                L("目录选择变化 → #" + sel);
                ShowSection(sel);
                return IntPtr.Zero;
            }
            if (code == BN_CLICKED)
            {
                switch (id)
                {
                    case ID_BTN_FEEDBACK:
                        L("点击：反馈问题");
                        ShowFeedback(GetModuleHandleW(null));
                        return IntPtr.Zero;
                    case ID_BTN_OPENPAGE:
                        L("点击：打开发布页");
                        OpenUrl("https://github.com/" + Program.RepoOwner + "/" + Program.RepoName);
                        return IntPtr.Zero;
                    case ID_BTN_SETTINGS:
                        L("点击：打开设置界面");
                        LaunchSibling("Settings.exe", "");
                        return IntPtr.Zero;
                    case ID_BTN_CLOSE:
                        L("点击：关闭");
                        // ★ 2026-10-11 修（真缺陷）：按钮的父窗口是底栏 _bottom ✗
                        //   → WM_COMMAND 是发给底栏的 ✓ 所以这里的 h 是底栏而不是主窗 ✗
                        //   → 原来 DestroyWindow(h) 只销毁了底栏 ✓ 主窗当然还在 ✓
                        //   必须销毁主窗句柄 _main ✓
                        DestroyWindow(_main);
                        return IntPtr.Zero;
                }
            }
            // ★ IsDialogMessageW 会把 Esc 翻成 WM_COMMAND(IDCANCEL=2) ✓ 要能关窗 ✓
            // ★ 同上：Esc 也要销毁主窗（h 可能是底栏 ✗）
            if (id == 2 && (code == 0 || code == 1)) { L("Esc → 关闭主窗"); DestroyWindow(_main); return IntPtr.Zero; }
            return IntPtr.Zero;
        }

        private static IntPtr OnFeedbackCommand(IntPtr h, int id, int code)
        {
            if (id == ID_FB_KIND && code == CBN_SELCHANGE) { L("反馈类型改为：" + KindText()); RefreshPreview(); return IntPtr.Zero; }
            if ((id == ID_FB_SUBJECT || id == ID_FB_DETAIL || id == ID_FB_CONTACT) && code == EN_CHANGE)
            {
                RefreshPreview();
                return IntPtr.Zero;
            }
            if (id == ID_FB_DIAG && code == BN_CLICKED)
            {
                L("附带诊断信息勾选状态 = " + IsChecked(_ckDiag));
                RefreshPreview();
                return IntPtr.Zero;
            }
            if (code == BN_CLICKED)
            {
                switch (id)
                {
                    case ID_FB_GITHUB: L("点击：在 GitHub 上打开"); OpenOnGitHub(); return IntPtr.Zero;
                    case ID_FB_COPY: L("点击：复制内容"); CopyToClipboard(false); return IntPtr.Zero;
                    case ID_FB_SAVE: L("点击：存成文件"); SaveToFile(); return IntPtr.Zero;
                    case ID_FB_CLOSE: L("点击：关闭反馈窗"); DestroyWindow(h); return IntPtr.Zero;
                }
            }
            if (id == 2 && (code == 0 || code == 1)) { L("Esc → 关闭反馈窗"); DestroyWindow(h); return IntPtr.Zero; }
            return IntPtr.Zero;
        }

        // ====================================================================
        //  自绘目录（行高 + 选中高亮）
        // ====================================================================
        private static void DrawTocItem(IntPtr l)
        {
            var dis = *(DRAWITEMSTRUCT*)l;
            if (dis.CtlID != ID_TOC || dis.hDC == IntPtr.Zero) { return; }

            int idx = (int)dis.itemID;
            var secs = Program.Sections;
            string text = idx >= 0 && idx < secs.Length ? "  " + secs[idx].Title : "";

            bool sel = (dis.itemState & ODS_SELECTED) != 0;
            RECT rc = dis.rcItem;
            FillRect(dis.hDC, ref rc, Bg(sel ? CLR_SEL_BG : CLR_TOC_BG));

            int oldBk = SetBkMode(dis.hDC, TRANSPARENT);
            int oldFg = SetTextColor(dis.hDC, sel ? CLR_WHITE : CLR_TITLE);
            IntPtr oldFont = SelectObject(dis.hDC, _fontToc);
            RECT tx = dis.rcItem;
            tx.R -= S(6);
            DrawTextW(dis.hDC, text, -1, ref tx, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(dis.hDC, oldFont);
            SetTextColor(dis.hDC, oldFg);
            SetBkMode(dis.hDC, oldBk);
        }
    }
}
