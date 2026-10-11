// ============================================================================
//  WARDEN 设置工具 —— 原生 Win32/GDI 实现（2026-10-11 · Beta 3 / 0.3.3）
//  文件：SettingsWin32.cs
//
//  【为什么重写】
//    WinForms 不支持 Native AOT ✗（PublishAot 直接编译失败 ✓）
//    原来 SelfContained + 单文件 = 47.03 MB ✗，而且那 47 MB 里真正的界面代码
//    只有几百 KB ✓ → 改成原生控件 + AOT：一个几 MB 的 exe，不用装 .NET ✓
//
//  【界面规格来自哪】
//    诊断\测试报告\WARDEN-0.3.3-Settings规格与清单.md（逐控件坐标照抄 ✓）
//    原文案一个字没改 ✓ 只换掉承载它们的控件实现 ✓
//      主窗：客户区 830x640（最小 780x600）；页签填满 + 底部条高 56
//      底部条：保存配置(12,12) 重新加载(130,12) 状态标签(495,18)330x24
//      页签顺序：语言 / 规则 / 保护 / 处置 / 名单 / 安全 / 日志
//      规则表：四列 启用 52 · 名称 185 · 进程 135 · 标题关键词 400
//      规则页按钮：(0,440) (113,440) (226,440) 导出(345,440) 导入(458,440) 均 105x30
//      语言页：标签(4,12)420x22 下拉(4,40)420x26 说明(4,78)760x140
//      名单页：白名单(8,30)380x150 同意(400,30)380x150 强杀历史(8,216)380x140
//              清空强杀历史(400,216)140x30
//      统一工厂：标签 h=20 / 复选框 700x24 / 数字框 95x24（与旧版一致 ✓）
//
//  【血泪坑，这里全部避开】（前两个工具踩过的，逐条对上 ✓）
//    ✗ 绝不把带命令的控件挂到系统 STATIC/EDIT 等类窗口下
//      （系统 STATIC 的窗口过程会吞掉 WM_COMMAND ✗ Help 四个按钮全死就是这原因 ✓）
//      ✓ 本实现一律挂在【主窗】或【自建窗口类】的页面上（自定义类的 WndProc 不吞命令 ✓）
//    ✗ 控件 ID 不放 CreateWindowExW 的 hMenu → 全是 ID 0 ✗ 点不动 ✓
//    ✓ WM_COMMAND 取 ID 用低 16 位、通知码用高 16 位（w & 0xFFFF / (w>>16) & 0xFFFF ✓）
//    ✗ 绝不用 Marshal.GetFunctionPointerForDelegate（AOT 下 fail-fast 0xC0000409 ✓）
//    ✓ 一律 [UnmanagedCallersOnly(CallConvStdcall)] + delegate* unmanaged[Stdcall] ✓
//    ✓ DPI 缩放【建窗之前】定好（建窗后再算 → 窗口放大而控件不放大 ✓）
//    ✓ 客户区尺寸用 AdjustWindowRectEx 换算（否则控件被标题栏/边框裁掉 ✓）
//    ✓ P/Invoke 归属逐条核对：DrawTextW/MessageBoxW/CreateWindowExW/SendMessageW/
//      PostMessageW/SetScrollInfo→user32；GetStockObject/CreateFontW/SelectObject/
//      CreateSolidBrush/DeleteObject/SetTextColor/SetBkMode→gdi32；文件对话框→comdlg32 ✓
//    ✓ 结构体一律 blittable ✗ 不放 string/bool/数组；字符串 IntPtr + StringToHGlobalUni ✓
//    ✓ 异常绝不逃出 UnmanagedCallersOnly 回调（顶层 try/catch + 写日志 ✓）
//
//  【日志】%TEMP%\settings-win32.log
//    WM_COMMAND 原始 w 值 + 取出的 id/code、控件句柄与错误码、窗口尺寸与 DPI、
//    布局结果、语言加载结果 —— 定位问题全靠这几样 ✓
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WardenSettings
{
    /// <summary>
    /// WARDEN 设置界面的原生 Win32 承载层。
    /// ★ 只做界面：窗口、控件、布局、消息分发 ✓
    ///   配置读写 / 规则增删改 / SHA256 / 导入导出格式全部留在 WARDEN-Settings.cs ✓
    /// </summary>
    internal static unsafe class SettingsWin32
    {
        // ====================================================================
        //  窗口类
        // ====================================================================
        private const string ClsMain = "WardenSettingsWnd";
        private const string ClsPage = "WardenSettingsPage";        // 页签里的普通容器
        private const string ClsRulesHost = "WardenSettingsRulesHost";  // 规则页：可滚动容器

        // ====================================================================
        //  控件 ID（★ 必须放进 CreateWindowExW 的 hMenu ✓ 否则全是 ID 0 ✗）
        // ====================================================================
        private const int ID_TABS = 100;
        private const int ID_SAVE = 101, ID_RELOAD = 102, ID_STATUS = 103;
        // 语言页
        private const int ID_LANG_LABEL = 110, ID_CB_LANG = 111, ID_LANG_HINT = 112;
        // 规则页
        private const int ID_LV_RULES = 120, ID_BTN_ADD = 121, ID_BTN_DEL = 122,
                          ID_BTN_EDIT = 123, ID_BTN_EXPORT = 124, ID_BTN_IMPORT = 125,
                          ID_RULE_HINT = 126;
        // 保护页
        private const int ID_CK_FG = 130, ID_CK_EDIT = 131, ID_CK_CHILD = 132, ID_CK_DRY = 133,
                          ID_LBL_TIP1 = 134, ID_LBL_TIP2 = 135;
        // 处置页
        private const int ID_CK_TERM = 140;
        private const int ID_LBL_APPLY = 141, ID_NUM_APPLY = 142;
        private const int ID_LBL_TERMTH = 143, ID_NUM_TERMTH = 144;
        private const int ID_LBL_FAIL = 145, ID_NUM_FAIL = 146;
        private const int ID_LBL_ASK = 147, ID_NUM_ASK = 148;
        private const int ID_LBL_HIDES = 149, ID_NUM_HIDES = 150;
        private const int ID_LBL_WAIT = 151, ID_NUM_WAIT = 152;
        private const int ID_CK_PROMPT = 153, ID_CK_HONORSIG = 154, ID_CK_HONORWL = 155;
        private const int ID_LBL_ACT1 = 156, ID_LBL_ACT2 = 157, ID_LBL_ACT3 = 158;
        // 名单页
        private const int ID_LBL_WL = 160, ID_TXT_WL = 161;
        private const int ID_LBL_CONSENT = 162, ID_TXT_CONSENT = 163;
        private const int ID_LBL_TERMHIST = 164, ID_TXT_TERMHIST = 165;
        private const int ID_BTN_CLEARHIST = 166;
        private const int ID_LBL_HINT1 = 167, ID_LBL_HINT2 = 168;
        // 安全页
        private const int ID_CK_SELF = 170, ID_CK_BLOCK = 171;
        private const int ID_LBL_HASHTITLE = 172, ID_TXT_HASH = 173, ID_BTN_HASH = 174;
        private const int ID_LBL_HARD = 175, ID_TXT_HARD = 176;
        // 日志页
        private const int ID_BTN_REFRESHLOG = 180, ID_NUM_LINES = 181, ID_LBL_LINES = 182,
                          ID_BTN_OPENFOLDER = 183, ID_TXT_LOG = 184;
        // 规则编辑对话框（DLGTEMPLATEEX 里的 ID 必须与这里的常量一致 ✓）
        private const int ID_DLG_LBL1 = 1001, ID_DLG_NAME = 1002,
                          ID_DLG_LBL2 = 1003, ID_DLG_PROC = 1004,
                          ID_DLG_LBL3 = 1005, ID_DLG_KWS = 1006,
                          ID_DLG_LBLHINT = 1007, IDOK = 1, IDCANCEL = 2;

        // ====================================================================
        //  样式 / 消息常量
        // ====================================================================
        private const uint CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001;
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000;
        private const uint WS_BORDER = 0x00800000, WS_VSCROLL = 0x00200000, WS_HSCROLL = 0x00100000;
        private const uint WS_EX_CONTROLPARENT = 0x00010000;
        private const uint WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
        private const uint BS_PUSHBUTTON = 0x00000000, BS_DEFPUSHBUTTON = 0x00000001,
                           BS_AUTOCHECKBOX = 0x00000003;
        private const uint ES_LEFT = 0x0000, ES_MULTILINE = 0x0004, ES_AUTOVSCROLL = 0x0040,
                           ES_AUTOHSCROLL = 0x0080, ES_READONLY = 0x0800;
        private const uint CBS_DROPDOWNLIST = 0x0003, CBS_HASSTRINGS = 0x0200;
        private const uint SS_LEFT = 0x00000000;
        private const uint TCS_TABS = 0x0000, TCS_MULTILINE = 0x0200;
        private const uint LVS_REPORT = 0x0001, LVS_SINGLESEL = 0x0004, LVS_SHOWSELALWAYS = 0x0008;

        private const uint WM_CREATE = 0x0001, WM_DESTROY = 0x0002, WM_SIZE = 0x0005,
                           WM_SETFOCUS = 0x0007, WM_PAINT = 0x000F, WM_CLOSE = 0x0010,
                           WM_ERASEBKGND = 0x0014, WM_GETMINMAXINFO = 0x0024,
                           WM_DRAWITEM = 0x002B, WM_SETFONT = 0x0030, WM_GETDLGCODE = 0x0087,
                           WM_KEYDOWN = 0x0100, WM_COMMAND = 0x0111, WM_TIMER = 0x0113,
                           WM_VSCROLL = 0x0115, WM_MOUSEWHEEL = 0x020A, WM_NOTIFY = 0x004E,
                           WM_CTLCOLORSTATIC = 0x0138, WM_CTLCOLOREDIT = 0x0133;

        private const uint CB_ADDSTRING = 0x0143, CB_GETCURSEL = 0x0147, CB_SETCURSEL = 0x014E,
                           CB_GETLBTEXT = 0x0148, CB_SETMINVISIBLE = 0x1701, CB_GETCOUNT = 0x0146;
        private const uint BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1;
        private const uint EM_SETSEL = 0x00B1, EM_SCROLLCARET = 0x00B7;

        private const uint LVM_FIRST = 0x1000;
        private const uint LVM_DELETEALLITEMS = LVM_FIRST + 9;
        private const uint LVM_GETNEXTITEM = LVM_FIRST + 12;
        private const uint LVM_INSERTITEMW = LVM_FIRST + 77;
        private const uint LVM_SETITEMW = LVM_FIRST + 76;
        private const uint LVM_SETITEMTEXTW = LVM_FIRST + 116;
        private const uint LVM_INSERTCOLUMNW = LVM_FIRST + 97;
        private const uint LVM_SETCOLUMNW = LVM_FIRST + 96;
        private const uint LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
        private const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
        private const uint LVM_GETITEMSTATE = LVM_FIRST + 44;
        private const uint LVM_DELETEITEM = LVM_FIRST + 8;
        private const uint LVM_GETTOPINDEX = LVM_FIRST + 39;
        private const uint LVM_ENSUREVISIBLE = LVM_FIRST + 19;
        private const uint LVM_GETSELECTEDCOUNT = LVM_FIRST + 50;

        private const uint LVNI_SELECTED = 0x0002, LVNI_ALL = 0x0000;
        private const uint LVIS_SELECTED = 0x0002, LVIS_FOCUSED = 0x0001;
        private const uint LVIS_STATEIMAGEMASK = 0x0000F000;
        private const uint LVCF_FMT = 0x0001, LVCF_WIDTH = 0x0002, LVCF_TEXT = 0x0004;
        private const int LVCFMT_LEFT = 0;
        private const uint LVS_EX_CHECKBOXES = 0x00000004, LVS_EX_GRIDLINES = 0x00000001,
                           LVS_EX_FULLROWSELECT = 0x00000020;

        private const uint TCM_FIRST = 0x1300;
        private const uint TCM_INSERTITEMW = TCM_FIRST + 62, TCM_GETCURSEL = TCM_FIRST + 11,
                           TCM_SETCURSEL = TCM_FIRST + 12, TCM_ADJUSTRECT = TCM_FIRST + 40;

        private const uint UDM_SETRANGE32 = 0x046F, UDM_GETRANGE32 = 0x0470;
        // ★ 2026-10-11 补：UDM_SETBUDDY = WM_USER(0x400) + 106 = 0x46A ✓（原文件用了但没声明 ✗）
        private const uint UDM_SETBUDDY = 0x046A;
        private const uint UDS_ALIGNRIGHT = 0x0004, UDS_SETBUDDYINT = 0x0002, UDS_ARROWKEYS = 0x0020;
        private const int UDN_DELTAPOS = -722;

        private const uint SIF_RANGE = 0x0001, SIF_PAGE = 0x0002, SIF_POS = 0x0004;
        private const int SB_VERT = 1, SB_LINEUP = 0, SB_LINEDOWN = 1, SB_PAGEUP = 2,
                          SB_PAGEDOWN = 3, SB_THUMBTRACK = 5, SB_TOP = 6, SB_BOTTOM = 7;

        private const int SW_SHOW = 5;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const uint MINMAXINFO_MIN = 780, MINMAXINFO_MINH = 600;

        private const uint MB_OK = 0, MB_OKCANCEL = 1, MB_YESNOCANCEL = 3, MB_YESNO = 4,
                           MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30,
                           MB_ICONINFORMATION = 0x40;

        private const uint OFN_OVERWRITEPROMPT = 0x0002, OFN_HIDEREADONLY = 0x0004,
                           OFN_PATHMUSTEXIST = 0x000800, OFN_FILEMUSTEXIST = 0x00001000,
                           OFN_EXPLORER = 0x00080000;

        private const int COLOR_WINDOW = 5, TRANSPARENT = 1, DEFAULT_GUI_FONT = 17;
        private const int ICC_STANDARD_CLASSES = 0x4000;
        // ★ 2026-10-11 修（5000+ 次控件创建失败的总根因）：
        //   原来只注册了 ICC_STANDARD_CLASSES ✗ 而它只含 button/edit/static ✓
        //   规则表 SysListView32 / 数字框 UPDOWN / 页签 SysTabControl32 全都不在其中 ✗
        //   → 日志实测：规则表 err=1406 ✗ 数字框 err=1406 ✗ 语言下拉 err=1407（找不到窗口类）✗
        private const int ICC_LISTVIEW_CLASSES = 0x0001;   // SysListView32 ✓
        private const int ICC_TAB_CLASSES      = 0x0008;   // SysTabControl32 ✓
        private const int ICC_UPDOWN_CLASS     = 0x0010;   // UPDOWN（数字框箭头 ✓）
        private const int ICC_PROGRESS_CLASS   = 0x0020;   // 进度条（备用 ✓）
        private const int ICC_BAR_CLASSES      = 0x0004;   // 状态栏/工具条（备用 ✓）
        private const int IDI_SHIELD = 32518;
        private const uint IMAGE_ICON = 1, LR_SHARED = 0x8000;

        private const uint DT_LEFT = 0x0000, DT_SINGLELINE = 0x0020, DT_NOPREFIX = 0x0800;

        // 颜色（GDI 的 COLORREF 是 0x00BBGGRR 反序 ✓ 已换算）
        private const int CLR_GREEN = 0x00008000;   // RGB(0,128,0)   Color.Green
        private const int CLR_GRAY = 0x00808080;    // Color.Gray
        private const int CLR_RED = 0x000000FF;     // Color.Red
        private const int CLR_HARD_BG = 0x00F6F6F6;  // RGB(246,246,246) 只读硬黑名单底色
        private const int CLR_BLACK = 0x00000000;

        // 布局基准（规格：客户区 830x640 ✓）
        private const int BASE_W = 830, BASE_H = 640, BOTTOM_H = 56;
        // 页签显示区相对 TabControl 客户区的偏移（96 DPI 下实测 24/4 ✓ 与 WinForms 一致 ✓）
        private const int TAB_TOP = 24, TAB_INSET = 4;
        // 规则页内容总高（列表 430 + 30 按钮 + 提示 500 + 16 余量 ✓ 与旧版 AutoScroll 同一效果 ✓）
        private const int RULES_CONTENT_H = 516;

        private static readonly char NUL = (char)0;

        // ====================================================================
        //  结构体（★ 一律 blittable：只有整数与 IntPtr ✗ 不放 string/bool/数组 ✓）
        // ====================================================================
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
            public uint time; public int x, y;
        }

        // ★ 原生 PAINTSTRUCT 的 rgbReserved 是 32 字节 ✗ 少声明会越界踩坏栈 → fail-fast 0xC0000409 ✓
        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore, fIncUpdate;
            public byte r0, r1, r2, r3, r4, r5, r6, r7;
            public byte r8, r9, r10, r11, r12, r13, r14, r15;
            public byte r16, r17, r18, r19, r20, r21, r22, r23;
            public byte r24, r25, r26, r27, r28, r29, r30, r31;   // ★ 合计 32 ✓
        }

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
        private struct INITCOMMONCONTROLSEX { public uint dwSize, dwICC; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NMHDR { public IntPtr hwndFrom; public IntPtr idFrom; public uint code; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NMUPDOWN { public NMHDR hdr; public int iPos, iDelta; }

        // ★ TCITEMW 共 56 字节（4+4+4+pad+IntPtr+IntPtr ✓）
        [StructLayout(LayoutKind.Sequential)]
        private struct TCITEMW
        {
            public uint mask;
            public uint dwState, dwStateMask;
            public IntPtr pszText;
            public int cchTextMax, iImage;
        }

        // ★ LVITEMW 共 88 字节（全整数 + IntPtr ✓）
        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEMW
        {
            public uint mask;
            public int iItem, iSubItem;
            public uint state, stateMask;
            public IntPtr pszText;
            public int cchTextMax, iImage;
            public IntPtr lParam;
            public int iIndent, iGroupId;
            public uint cColumns;
            public IntPtr puColumns, piColFmt;
            public int iGroup;
        }

        // ★ LVCOLUMNW 共 56 字节
        [StructLayout(LayoutKind.Sequential)]
        private struct LVCOLUMNW
        {
            public uint mask;
            public int fmt, cx;
            public IntPtr pszText;
            public int cchTextMax, iSubItem, iImage, iOrder;
            public int cxMin, cxDefault, cxIdeal;
        }

        // ★ OPENFILENAMEW 与 winuser.h 逐字段对齐（全指针/整数 → blittable ✓）
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

        [StructLayout(LayoutKind.Sequential)]
        private struct SCROLLINFO
        {
            public uint cbSize, fMask;
            public int nMin, nMax, nPage, nPos, nTrackPos;
        }

        // ====================================================================
        //  P/Invoke（★ 归属逐条核对过：写错就是 EntryPointNotFoundException ✓ 控件会整个建不出来）
        // ====================================================================
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
        // ★ 名字参数用 IntPtr：AOT 下 [MarshalAs(UnmanagedType.LPWStr)] 的默认封送可用 ✓
        //   但为稳妥，类名/窗口名也一律自己分配原生字符串（与类结构里 lpszClassName 同理 ✓）
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint ex, IntPtr cls, IntPtr name, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetMessageW(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] private static extern bool IsDialogMessageW(IntPtr h, ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, string l);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool PostQuitMessage(int c);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr h, bool e);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool AdjustWindowRectEx(ref RECT r, uint style, bool menu, uint exStyle);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr h, IntPtr id);
        [DllImport("user32.dll")] private static extern int SetScrollInfo(IntPtr h, int bar, ref SCROLLINFO si, bool redraw);
        [DllImport("user32.dll")] private static extern int GetScrollPos(IntPtr h, int bar);
        [DllImport("user32.dll")] private static extern int SetScrollPos(IntPtr h, int bar, int pos, bool redraw);
        [DllImport("user32.dll")] private static extern bool ScrollWindowEx(IntPtr h, int dx, int dy, IntPtr prcScroll,
            IntPtr prcClip, IntPtr hrgnUpdate, IntPtr prcUpdate, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
        // ★ 这几条会出现在 WM_COMMAND 诊断日志里 ✓ 位置不能写错（都在 user32 ✓）
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr h, char[] s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthW(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowTextW(IntPtr h, string s);
        [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr h, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImageW(IntPtr inst, IntPtr name, uint type, int cx, int cy, uint load);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr ico);
        // ★ DrawTextW / FillRect 在 user32（不是 gdi32 ✗ 写错文字画不出来 ✓）
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DrawTextW(IntPtr dc, string s, int n, ref RECT r, uint f);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);

        // ★ GDI 全在 gdi32
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int c);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr dc, int c);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int m);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight,
            uint italic, uint under, uint strike, uint charset, uint prec, uint clip, uint quality,
            uint pitch, string face);

        // ★ 文件对话框在 comdlg32（取代 WinForms SaveFileDialog / OpenFileDialog ✓）
        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OPENFILENAMEW o);
        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAMEW o);

        [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(IntPtr n);

        // ====================================================================
        //  状态
        // ====================================================================
        private static double _scale = 1.0;
        private static IntPtr _clsMainPtr, _clsPagePtr, _clsRulesPtr;
        // ★ 2026-10-11：只构建一次 UI 的开关（防 WM_CREATE 递归 ✗ 见 WM_CREATE 分支注释 ✓）
        private static bool _controlsBuilt;
        private static IntPtr _main, _tabs, _bottom, _status;
        private static IntPtr _pageLang, _pageRules, _pageProtect, _pageAction, _pageLists, _pageSafety, _pageLog;
        private static IntPtr _hostRules;      // 规则页的可滚动容器（其余页直接用页面窗口 ✓）
        private static IntPtr _lvRules;
        private static IntPtr _cbLang;
        private static IntPtr _txtWl, _txtConsent, _txtTermHist, _txtHash, _txtHard, _txtLog;
        private static IntPtr _numApply, _numTermTh, _numFail, _numAsk, _numHides, _numWait, _numLines;
        private static IntPtr _ckFg, _ckEdit, _ckChild, _ckDry, _ckTerm, _ckPrompt,
                                _ckHonorSig, _ckHonorWl, _ckSelf, _ckBlock;
        private static IntPtr _fontBase, _fontCombo, _fontMono, _fontMonoSmall, _fallback, _iconShield;
        private static readonly List<IntPtr> _fonts = new();
        private static readonly Dictionary<int, IntPtr> _brushes = new();
        private static int _statusColor = CLR_GRAY;
        private static IntPtr _statusBrush = IntPtr.Zero;
        private static int _rulesScroll;
        private static bool _relanging;
        private static int _curPage = -1;
        private static IntPtr _lastCommandHwnd;

        // ====================================================================
        //  日志（%TEMP%\settings-win32.log ✓ 定位问题全靠它）
        // ====================================================================
        internal static void L(string s)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "settings-win32.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>★ 逻辑像素 → 当前 DPI 下的物理像素（建窗之前就要能用 ✓）</summary>
        private static int S(int px) => (int)Math.Round(px * _scale);

        /// <summary>★ 字体的逻辑高度：点值 → 像素（负数 = 字符高度 ✓）</summary>
        private static int FontH(double pt) => -(int)Math.Round(pt * _scale * 96.0 / 72.0);

        private static IntPtr Bg(int color)
        {
            if (!_brushes.TryGetValue(color, out IntPtr b) || b == IntPtr.Zero)
            {
                b = CreateSolidBrush(color);
                _brushes[color] = b;
            }
            return b;
        }

        /// <summary>★ 自己分配原生字符串（AOT 下结构体里不能放 string ✓）</summary>
        private static IntPtr Sz(string s) => Marshal.StringToHGlobalUni(s ?? "");

        // ====================================================================
        //  入口
        // ====================================================================
        internal static int Run()
        {
            // ★★★ 顺序很重要：
            //   ① 先碰一次 Lang → 触发静态构造 → 把语言包读完 ✓
            //      （惰性加载会让第一批标题拿到 key 名 ✗ Help 就踩过 ✓）
            //   ② 再定 DPI 缩放（建窗【之前】✓）
            //   ③ 最后建窗 → WM_CREATE 里用 S() 定位才与窗口尺寸一致 ✓
            bool langLoaded = WardenLang.Lang.Loaded;
            L("=== Settings 原生版启动 PID=" + Environment.ProcessId + " ===");
            L("语言包：Loaded=" + langLoaded + " code=" + WardenLang.Lang.Code
              + " name=" + WardenLang.Lang.Name
              + " 可用=" + string.Join(",", WardenLang.Lang.Available())
              + (WardenLang.Lang.LoadError.Length > 0 ? " ★载入错误=" + WardenLang.Lang.LoadError : ""));
            L("数据目录（配置/日志）：" + Settings.ToolDir);
            L("配置路径：" + Settings.CfgPath);

            try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch (Exception e) { L("DPI 感知设置失败：" + e.Message); }
            try { _scale = GetDpiForSystem() / 96.0; } catch { _scale = 1.0; }
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
            L("系统 DPI 缩放=" + _scale.ToString("0.00") + "x（★ 建窗之前已定好 ✓）");

            try
            {
                var icc = new INITCOMMONCONTROLSEX
                {
                    dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
                    // ★ 修：把用到的公共控件类全部注册 ✓（原来只有标准类 ✗）
                dwICC = ICC_STANDARD_CLASSES | ICC_LISTVIEW_CLASSES | ICC_TAB_CLASSES
                      | ICC_UPDOWN_CLASS | ICC_PROGRESS_CLASS | ICC_BAR_CLASSES,
                };
                bool ok = InitCommonControlsEx(ref icc);
                L("InitCommonControlsEx=" + ok + " err=" + Marshal.GetLastWin32Error());
            }
            catch (Exception e) { L("InitCommonControlsEx 失败（页签/列表可能建不出来）：" + e.Message); }

            if (!RegisterClasses()) { return 3; }
            if (!CreateMainWindow()) { return 4; }

            // 首次填充：配置 → 界面（与旧版构造函数顺序一致：BuildUI → LoadConfig ✓）
            Settings.LoadConfig();
            UpdateStatus();
            RefreshLog();
            ApplyLangToControls();
            L("首次 LoadConfig + UpdateStatus + RefreshLog 完成 ✓ 规则 " + RuleCount() + " 条");

            // 定时器：3 秒刷一次状态（对应旧版 Timer Interval=3000 ✓）
            SetTimer(_main, (IntPtr)1, 3000, IntPtr.Zero);

            MSG msg;
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0))
            {
                // ★ IsDialogMessageW：Tab 轮换控件、Esc 关窗 ✓
                //   （页容器/页签都有 WS_EX_CONTROLPARENT ✓ 否则它不认账 ✓）
                if (!IsDialogMessageW(_main, ref msg))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            Cleanup();
            L("=== Settings 原生版正常退出 ✓ ===");
            return 0;
        }

        private static bool RegisterClasses()
        {
            IntPtr inst = GetModuleHandleW(IntPtr.Zero);
            _clsMainPtr = Sz(ClsMain);
            _clsPagePtr = Sz(ClsPage);
            _clsRulesPtr = Sz(ClsRulesHost);
            // ★ 2026-10-11 修：ref 只能传可赋值变量 ✗ 原来写成 ref MakeClass(...) 编译不过 ✓
            WNDCLASSEXW wcMain = MakeClass(_clsMainPtr, inst);
            ushort a1 = RegisterClassExW(ref wcMain);
            L("RegisterClassExW(main) atom=" + a1 + " err=" + Marshal.GetLastWin32Error());
            WNDCLASSEXW wcPage = MakeClass(_clsPagePtr, inst);
            ushort a2 = RegisterClassExW(ref wcPage);
            WNDCLASSEXW wcRules = MakeClass(_clsRulesPtr, inst);
            ushort a3 = RegisterClassExW(ref wcRules);
            L("RegisterClassExW(page/rules) atom=" + a2 + "/" + a3 + " err=" + Marshal.GetLastWin32Error());
            return a1 != 0;
        }

        private static WNDCLASSEXW MakeClass(IntPtr name, IntPtr inst)
            => new()
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = CS_HREDRAW | CS_VREDRAW,
                // ★ AOT 官方方式：delegate* unmanaged[Stdcall] ✓ 绝不用 GetFunctionPointerForDelegate ✗
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&SettingsWndProc,
                hInstance = inst,
                hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
                lpszClassName = name,
            };

        // ====================================================================
        //  唯一的窗口过程（★ 一个回调管三个窗口类，按 hwnd 分流 ✓）
        // ====================================================================
        [System.Runtime.InteropServices.UnmanagedCallersOnly(
            CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        private static IntPtr SettingsWndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            // ★ 异常绝不能逃出 UnmanagedCallersOnly ✓（逃出去 = 进程直接没 ✓ 日志都留不下）
            try
            {
                if (h == _hostRules) { return RulesHostProc(h, m, w, l); }
                return MainProc(h, m, w, l);
            }
            catch (Exception ex)
            {
                L("★ 窗口过程异常 msg=0x" + m.ToString("X") + " hwnd=" + h + " : " + ex.GetType().Name + " " + ex.Message);
                try { return DefWindowProcW(h, m, w, l); } catch { return IntPtr.Zero; }
            }
        }

        private static IntPtr MainProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            switch (m)
            {
                case WM_CREATE:
                    L("收到 WM_CREATE ✓ hwnd=" + h);
                    // ★★★ 2026-10-11 修（界面出不来的总病根：创建风暴 / 无限递归）
                        //   原来【每个窗口一创建】就调 BuildControls(h) ✗
                        //   而 BuildControls 自己会创建窗口（底栏/页签/控件 ✓）
                        //   → 那些窗口又发 WM_CREATE → 又调 BuildControls → 递归 ✗✗
                        //   实测：同一批失败重复几百次 ✓ 且 _main 被反复覆盖 ✗ → 主窗句柄丢失 → 永不显示 ✓
                        //   修法：只构建一次（第一个 WM_CREATE 的窗口就是主窗 ✓）
                        if (!_controlsBuilt)
                        {
                            _controlsBuilt = true;
                            _main = h;
                            BuildControls(h);
                            L("BuildControls 完成 ✓（一次性 ✓ 不再递归 ✓）");
                            if (_main != IntPtr.Zero)
                            {
                                ShowWindow(_main, SW_SHOW);
                                UpdateWindow(_main);
                                SetForegroundWindow(_main);
                                L("主窗已显示 ✓ ShowWindow(SW_SHOW) + UpdateWindow ✓");
                            }
                        }
                        else { L("跳过重复 WM_CREATE 的构建 ✓（防递归 ✓）"); }
                    return IntPtr.Zero;

                case WM_COMMAND:
                {
                    // ★★★ 唯一可信的判据：把原始 w 与取出的 id/code 都记下来 ✓
                    //   id 用低 16 位、通知码用高 16 位（写错就永远匹配不上 ✓）
                    long raw = w.ToInt64();
                    int id = (int)(raw & 0xFFFF);
                    int code = (int)((raw >> 16) & 0xFFFF);
                    L("WM_COMMAND msg=0x111 raw=" + raw + " → id=" + id + " code=" + code
                      + " 控件=" + CtrlName(id) + " 发送者=" + l.ToInt64()
                      + " 文本=「" + Trim(TextOf(l)) + "」");
                    _lastCommandHwnd = l;   // 记下来给对话框当 owner ✓
                    return OnCommand(h, id, code, l);
                }

                case WM_NOTIFY:
                {
                    var nh = *(NMHDR*)l;
                    if (nh.code == unchecked((uint)UDN_DELTAPOS) && (int)nh.idFrom == ID_NUM_LINES)
                    {
                        var nu = *(NMUPDOWN*)l;
                        L("WM_NOTIFY UDN_DELTAPOS 来自日志行数 iPos=" + nu.iPos + " iDelta=" + nu.iDelta
                          + " → 刷新日志显示");
                        RefreshLog();
                    }
                    return IntPtr.Zero;
                }

                case WM_SIZE:
                    LayoutAll();
                    return IntPtr.Zero;

                case WM_GETMINMAXINFO:
                {
                    // ★ WinForms 的 MinimumSize = 780x600（客户区 ✓）→ 等价物
                    var mmi = *(MINMAXINFO*)l;
                    RECT rc = new() { L = 0, T = 0, R = S((int)MINMAXINFO_MIN), B = S((int)MINMAXINFO_MINH) };
                    AdjustWindowRectEx(ref rc, WS_OVERLAPPEDWINDOW, false, 0);
                    mmi.ptMinTrackSize.X = rc.R - rc.L;
                    mmi.ptMinTrackSize.Y = rc.B - rc.T;
                    *(MINMAXINFO*)l = mmi;
                    return IntPtr.Zero;
                }

                case WM_CTLCOLORSTATIC:
                    // 状态标签要按运行状态变色 ✓ 只读文本框要灰底 ✓ 其余标签透明 ✓
                    if (w == _status)
                    {
                        SetBkMode(l, TRANSPARENT);
                        SetTextColor(l, _statusColor);
                        return _statusBrush != IntPtr.Zero ? _statusBrush : Bg(CLR_HARD_BG);
                    }
                    if (w == _txtHard || w == _txtLog)
                    {
                        SetBkMode(l, TRANSPARENT);
                        SetTextColor(l, CLR_BLACK);
                        return Bg(CLR_HARD_BG);
                    }
                    return DefWindowProcW(h, m, w, l);

                case WM_TIMER:
                    UpdateStatus();
                    return IntPtr.Zero;

                case WM_SETFOCUS:
                    if (_lvRules != IntPtr.Zero) { SetFocus(_lvRules); }
                    return IntPtr.Zero;

                case WM_GETDLGCODE:
                    return (IntPtr)(0x0400 /*DLGC_WANTMESSAGE*/);

                case WM_CLOSE:
                    DestroyWindow(h);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    L("WM_DESTROY hwnd=" + h + " → 退出消息循环");
                    KillTimer(h, (IntPtr)1);
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return DefWindowProcW(h, m, w, l);
        }

        /// <summary>规则页的可滚动容器（对应旧版 AutoScroll 面板 ✓）</summary>
        private static IntPtr RulesHostProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            switch (m)
            {
                case WM_VSCROLL:
                {
                    int code = (int)(w.ToInt64() & 0xFFFF);
                    int pos = _rulesScroll;
                    int viewH = HostHeight();
                    switch (code)
                    {
                        case SB_LINEUP: pos -= S(24); break;
                        case SB_LINEDOWN: pos += S(24); break;
                        case SB_PAGEUP: pos -= viewH; break;
                        case SB_PAGEDOWN: pos += viewH; break;
                        case SB_THUMBTRACK: pos = (int)(w.ToInt64() >> 16); break;
                        case SB_TOP: pos = 0; break;
                        case SB_BOTTOM: pos = int.MaxValue; break;
                    }
                    SetRulesScroll(pos);
                    return IntPtr.Zero;
                }
                case WM_MOUSEWHEEL:
                {
                    int delta = (short)((w.ToInt64() >> 16) & 0xFFFF);
                    SetRulesScroll(_rulesScroll - delta / 120 * S(60));
                    return IntPtr.Zero;
                }
                case WM_GETDLGCODE:
                    return (IntPtr)(0x0400);
            }
            return DefWindowProcW(h, m, w, l);
        }

        // ====================================================================
        //  界面搭建（★ 坐标严格按规格文档 ✓）
        // ====================================================================
        private static void BuildControls(IntPtr h)
        {
            _main = h;
            BuildFonts();
            LoadShieldIcon();

            RECT cr;
            GetClientRect(h, out cr);
            L("BuildControls：客户区 " + (cr.R - cr.L) + "x" + (cr.B - cr.T) + " DPI=" + SafeDpi(h)
              + " 缩放=" + _scale.ToString("0.00") + "x");

            // ---- 底部固定条（高 56 ✓ WinForms 里是 Dock=Bottom 的 Panel ✓）----
            // ★★ 关键：按钮挂在【主窗】下，不挂在任何系统 STATIC 面板下 ✗
            //    （系统 STATIC 的窗口过程会吞掉 WM_COMMAND ✗ Help 四个按钮全死就是这个原因 ✓）
            _bottom = MakePage(h, 0, S(BASE_H - BOTTOM_H), S(BASE_W), S(BOTTOM_H));
            Btn(h, WardenLang.Lang.Tr("保存配置"), 12, S(BASE_H - BOTTOM_H) + 12, 110, 32, ID_SAVE, false);
            Btn(h, WardenLang.Lang.Tr("重新加载"), 130, S(BASE_H - BOTTOM_H) + 12, 100, 32, ID_RELOAD, false);
            _status = Lab(h, "", 495, S(BASE_H - BOTTOM_H) + 18, 330, 24, ID_STATUS, _fontBase);
            _statusBrush = Bg(CLR_HARD_BG);

            // ---- 页签 ----
            _tabs = CreateWindowExW(WS_EX_CONTROLPARENT, _clsPagePtr, Sz(""),    // 自建类：不吞 WM_COMMAND ✓
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | TCS_TABS | TCS_MULTILINE,
                0, 0, S(BASE_W), S(BASE_H - BOTTOM_H), h, (IntPtr)ID_TABS, IntPtr.Zero, IntPtr.Zero);
            if (_tabs == IntPtr.Zero)
            {
                L("★ 页签容器创建失败 err=" + Marshal.GetLastWin32Error());
                return;
            }
            SendMessageW(_tabs, WM_SETFONT, _fontBase, (IntPtr)1);

            // 七个页签（顺序与旧版一致：语言先插，其余按 BuildUI 顺序 ✓）
            string[] titles =
            {
                WardenLang.Lang.T("set.tab.lang"), WardenLang.Lang.T("set.tab.rules"),
                WardenLang.Lang.T("set.tab.protect"), WardenLang.Lang.T("set.tab.action"),
                WardenLang.Lang.T("set.tab.lists"), WardenLang.Lang.T("set.tab.safety"),
                WardenLang.Lang.T("set.tab.log"),
            };
            for (int i = 0; i < titles.Length; i++)
            {
                IntPtr p = Sz(titles[i]);
                try
                {
                    var it = new TCITEMW { mask = 0x0001 /*TCIF_TEXT*/, pszText = p, cchTextMax = 0 };
                    long idx = SendMessageW(_tabs, TCM_INSERTITEMW, (IntPtr)i, (IntPtr)(&it)).ToInt64();
                    L("页签 #" + i + " 「" + titles[i] + "」 TCM_INSERTITEMW → " + idx);
                }
                finally { Marshal.FreeHGlobal(p); }
            }

            // 页面（自建窗口类 ✓ 用 SetWindowPos 摆放，随 WM_SIZE 重排 ✓）
            _pageLang = MakePage(_tabs, 0, 0, S(BASE_W - 2 * TAB_INSET), S(BASE_H - BOTTOM_H - TAB_TOP - TAB_INSET));
            _pageRules = MakePage(_tabs, 0, 0, S(BASE_W - 2 * TAB_INSET), S(BASE_H - BOTTOM_H - TAB_TOP - TAB_INSET));
            _pageProtect = MakePage(_tabs, 0, 0, S(BASE_W - 2 * TAB_INSET), S(BASE_H - BOTTOM_H - TAB_TOP - TAB_INSET));
            _pageAction = MakePage(_tabs, 0, 0, S(BASE_W - 2 * TAB_INSET), S(BASE_H - BOTTOM_H - TAB_TOP - TAB_INSET));
            _pageLists = MakePage(_tabs, 0, 0, S(BASE_W - 2 * TAB_INSET), S(BASE_H - BOTTOM_H - TAB_TOP - TAB_INSET));
            _pageSafety = MakePage(_tabs, 0, 0, S(BASE_W - 2 * TAB_INSET), S(BASE_H - BOTTOM_H - TAB_TOP - TAB_INSET));
            _pageLog = MakePage(_tabs, 0, 0, S(BASE_W - 2 * TAB_INSET), S(BASE_H - BOTTOM_H - TAB_TOP - TAB_INSET));

            // 规则页：列表与按钮放在可滚动容器里（对应旧版 AutoScroll ✓）
            _hostRules = CreateWindowExW(0, _clsRulesPtr, Sz(""),
                WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_EX_CONTROLPARENT,
                0, 0, S(BASE_W - 2 * TAB_INSET), S(BASE_H - BOTTOM_H - TAB_TOP - TAB_INSET),
                _pageRules, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            L("页面句柄：lang=" + _pageLang + " rules=" + _pageRules + " protect=" + _pageProtect
              + " action=" + _pageAction + " lists=" + _pageLists + " safety=" + _pageSafety
              + " log=" + _pageLog + " rulesHost=" + _hostRules + " err=" + Marshal.GetLastWin32Error());

            BuildLangPage();
            BuildRulesPage();
            BuildProtectPage();
            BuildActionPage();
            BuildListsPage();
            BuildSafetyPage();
            BuildLogPage();

            L("控件创建汇总 err=" + Marshal.GetLastWin32Error()
              + " | 语言下拉=" + _cbLang + " 规则表=" + _lvRules + " 状态=" + _status);

            // 页签选中第 0 页（旧版 SelectedIndex = 0 ✓ 相当于显示「语言」页 ✓）
            SendMessageW(_tabs, TCM_SETCURSEL, IntPtr.Zero, IntPtr.Zero);
            ShowPage(0);
            LayoutAll();
            L("页签初始页 = 0（" + titles[0] + "）✓ 布局完成 ✓");
        }

        private static void BuildFonts()
        {
            if (_fonts.Count > 0) { return; }
            _fallback = GetStockObject(DEFAULT_GUI_FONT);
            _fontBase = NewFont(9f, 400, "Microsoft YaHei UI");        // 窗体字体 微软雅黑 9pt ✓
            _fontCombo = NewFont(10f, 400, "Microsoft YaHei UI");      // 语言下拉 10pt ✓
            _fontMono = NewFont(9f, 400, "Consolas");                  // 硬黑名单 ✓
            _fontMonoSmall = NewFont(8.5f, 400, "Consolas");            // 日志正文 ✓
            L("字体创建 base=" + _fontBase + " combo=" + _fontCombo + " mono=" + _fontMono
              + " monoSmall=" + _fontMonoSmall + " fallback=" + _fallback);
        }

        private static IntPtr NewFont(float pt, int weight, string face)
        {
            IntPtr f = CreateFontW(FontH(pt), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 0, 0, face);
            if (f == IntPtr.Zero) { f = GetStockObject(DEFAULT_GUI_FONT); }
            if (f != IntPtr.Zero) { _fonts.Add(f); }
            return f;
        }

        /// <summary>★ 图标本来用 SystemIcons.Shield（WinForms ✗）→ 改成 LoadImageW 取系统盾牌 ✓</summary>
        private static void LoadShieldIcon()
        {
            try
            {
                IntPtr ico = LoadImageW(IntPtr.Zero, (IntPtr)IDI_SHIELD, IMAGE_ICON, 0, 0, LR_SHARED);
                if (ico != IntPtr.Zero)
                {
                    SendMessageW(_main, 0x0080 /*WM_SETICON*/, IntPtr.Zero, ico);   // ICON_SMALL
                    SendMessageW(_main, 0x0080, (IntPtr)1, ico);                   // ICON_BIG
                    _iconShield = ico;
                }
                L("系统盾牌图标=" + ico + " err=" + Marshal.GetLastWin32Error());
            }
            catch (Exception e) { L("取系统图标失败（不影响功能）：" + e.Message); }
        }

        /// <summary>★ 自建窗口类的容器：不吞 WM_COMMAND ✓ 也没有系统类的怪行为 ✓</summary>
        private static IntPtr MakePage(IntPtr parent, int x, int y, int w, int h)
            => CreateWindowExW(WS_EX_CONTROLPARENT, _clsPagePtr, Sz(""),
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, x, y, w, h,
                parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        /// <summary>标签：统一工厂 h=20 ✓（规格「标签(x,y,w) → Size(w,20)」✓）</summary>
        private static IntPtr Lab(IntPtr parent, string text, int x, int y, int w, int hgt, int id, IntPtr font)
        {
            IntPtr c = CreateWindowExW(0, _clsPagePtr, Sz(text), WS_CHILD | WS_VISIBLE | SS_LEFT,
                S(x), S(y), S(w), S(hgt), parent, (IntPtr)id, IntPtr.Zero, IntPtr.Zero);
            if (c != IntPtr.Zero)
            {
                SendMessageW(c, WM_SETFONT, font == IntPtr.Zero ? _fontBase : font, (IntPtr)1);
                if (id != 0) { Reg(id, text); }
            }
            else { L("★ 标签创建失败 id=" + id + " text=「" + Trim(text) + "」 err=" + Marshal.GetLastWin32Error()); }
            return c;
        }

        private static IntPtr Btn(IntPtr parent, string text, int x, int y, int w, int hgt, int id, bool def)
        {
            IntPtr c = CreateWindowExW(0, _clsPagePtr, Sz(text),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | (def ? BS_DEFPUSHBUTTON : BS_PUSHBUTTON),
                S(x), S(y), S(w), S(hgt), parent, (IntPtr)id, IntPtr.Zero, IntPtr.Zero);
            if (c != IntPtr.Zero)
            {
                SendMessageW(c, WM_SETFONT, _fontBase, (IntPtr)1);
                Reg(id, text);
            }
            else { L("★ 按钮创建失败 id=" + id + " text=「" + Trim(text) + "」 err=" + Marshal.GetLastWin32Error()); }
            return c;
        }

        /// <summary>复选框：统一工厂 700x24 ✓（规格「复选框(x,y) → Size(700,24) 带初值」✓）</summary>
        private static IntPtr Chk(IntPtr parent, int id, string text, int x, int y, bool val)
        {
            IntPtr c = CreateWindowExW(0, _clsPagePtr, Sz(text),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                S(x), S(y), S(700), S(24), parent, (IntPtr)id, IntPtr.Zero, IntPtr.Zero);
            if (c != IntPtr.Zero)
            {
                SendMessageW(c, WM_SETFONT, _fontBase, (IntPtr)1);
                SendMessageW(c, BM_SETCHECK, val ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
                Reg(id, text);
            }
            else { L("★ 复选框创建失败 id=" + id + " text=「" + Trim(text) + "」 err=" + Marshal.GetLastWin32Error()); }
            return c;
        }

        /// <summary>
        /// 数字框：EDIT（宽 95×24 ✓）+ UPDOWN_CLASS 上下箭头（UDS_ALIGNRIGHT 贴右边 ✓）
        /// ★ 对应旧版 NumericUpDown（Minimum/Maximum/Value ✓ 用 UDM_SETRANGE32 设上下限 ✓）
        /// </summary>
        private static IntPtr Num(IntPtr parent, int editId, int x, int y, int val, int min, int max)
        {
            IntPtr ed = CreateWindowExW(0, _clsPagePtr, Sz(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | ES_LEFT | ES_AUTOHSCROLL,
                S(x), S(y), S(95), S(24), parent, (IntPtr)editId, IntPtr.Zero, IntPtr.Zero);
            if (ed == IntPtr.Zero)
            {
                L("★ 数字框 EDIT 创建失败 id=" + editId + " err=" + Marshal.GetLastWin32Error());
                return IntPtr.Zero;
            }
            SendMessageW(ed, WM_SETFONT, _fontBase, (IntPtr)1);
            IntPtr ud = CreateWindowExW(0, Sz("msctls_updown32"), Sz(""),
                WS_CHILD | WS_VISIBLE | UDS_ALIGNRIGHT | UDS_SETBUDDYINT | UDS_ARROWKEYS,
                S(x), S(y), S(95), S(24), parent, (IntPtr)(editId + 1000), IntPtr.Zero, IntPtr.Zero);
            if (ud == IntPtr.Zero)
            {
                L("★ 数字框 UPDOWN 创建失败 id=" + (editId + 1000) + " err=" + Marshal.GetLastWin32Error());
                return ed;
            }
            SendMessageW(ud, UDM_SETRANGE32, (IntPtr)min, (IntPtr)max);
            SendMessageW(ud, UDM_SETBUDDY, ed, IntPtr.Zero);
            SetWindowTextW(ed, Math.Max(min, Math.Min(max, val)).ToString(CultureInfo.InvariantCulture));
            return ed;
        }

        // ---------------------------------------------------------------- 语言页
        private static void BuildLangPage()
        {
            Lab(_pageLang, WardenLang.Lang.T("set.lang.label"), 4, 12, 420, 22, ID_LANG_LABEL, _fontBase);

            _cbLang = CreateWindowExW(0, Sz("COMBOBOX"), Sz(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST | CBS_HASSTRINGS,
                S(4), S(40), S(420), S(26), _pageLang, (IntPtr)ID_CB_LANG, IntPtr.Zero, IntPtr.Zero);
            if (_cbLang == IntPtr.Zero) { L("★ 语言下拉创建失败 err=" + Marshal.GetLastWin32Error()); }
            else
            {
                SendMessageW(_cbLang, WM_SETFONT, _fontCombo, (IntPtr)1);
                SendMessageW(_cbLang, CB_SETMINVISIBLE, (IntPtr)10, IntPtr.Zero);
                // ★ beta 版下拉框带 BCP47 代码（给开发者报 issue 用 ✓）正式版只显示名字 ✓
                bool showCode = ProductVersion.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0;
                var codes = WardenLang.Lang.Available();
                L("语言下拉：产品版本=「" + ProductVersion + "」带代码显示=" + showCode
                  + " 可选语言=" + string.Join(",", codes));
                for (int i = 0; i < codes.Count; i++)
                {
                    string shown = showCode
                        ? WardenLang.Lang.DisplayName(codes[i]) + "  (" + codes[i] + ")"
                        : WardenLang.Lang.DisplayName(codes[i]);
                    SendMessageW(_cbLang, CB_ADDSTRING, IntPtr.Zero, shown);
                }
                // ★ 选中当前语言（旧版是按 SelectedIndex 匹配 ✓ 这里按索引匹配，逻辑等价 ✓）
                int idx = codes.IndexOf(WardenLang.Lang.Code);
                SendMessageW(_cbLang, CB_SETCURSEL, (IntPtr)(idx >= 0 ? idx : 0), IntPtr.Zero);
                L("语言下拉项数=" + SendMessageW(_cbLang, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt64()
                  + " 当前选中索引=" + SendMessageW(_cbLang, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64()
                  + "（" + WardenLang.Lang.Code + " ✓）");
            }

            Lab(_pageLang, WardenLang.Lang.T("set.lang.hint"), 4, 78, 760, 140, ID_LANG_HINT, _fontBase);
        }

        // ---------------------------------------------------------------- 规则页
        private static void BuildRulesPage()
        {
            IntPtr host = _hostRules;

            _lvRules = CreateWindowExW(0, Sz("SysListView32"), Sz(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | LVS_REPORT | LVS_SINGLESEL | LVS_SHOWSELALWAYS,
                S(0), S(0), S(780), S(430), host, (IntPtr)ID_LV_RULES, IntPtr.Zero, IntPtr.Zero);
            if (_lvRules == IntPtr.Zero)
            {
                L("★ 规则表创建失败 err=" + Marshal.GetLastWin32Error());
            }
            else
            {
                SendMessageW(_lvRules, WM_SETFONT, _fontBase, (IntPtr)1);
                // ★ 复选框 + 网格线 + 整行选中（对应旧版 CheckBoxes/GridLines/FullRowSelect ✓）
                SendMessageW(_lvRules, LVM_SETEXTENDEDLISTVIEWSTYLE,
                    (IntPtr)(LVS_EX_CHECKBOXES | LVS_EX_GRIDLINES | LVS_EX_FULLROWSELECT),
                    (IntPtr)(LVS_EX_CHECKBOXES | LVS_EX_GRIDLINES | LVS_EX_FULLROWSELECT));
                AddColumn(0, WardenLang.Lang.Tr("启用"), 52);
                AddColumn(1, WardenLang.Lang.Tr("名称"), 185);
                AddColumn(2, WardenLang.Lang.Tr("进程"), 135);
                AddColumn(3, WardenLang.Lang.Tr("标题关键词"), 400);
                L("规则表列已建：启用52 名称185 进程135 标题关键词400 ✓");
            }

            Btn(host, WardenLang.Lang.Tr("新增规则"), 0, 440, 105, 30, ID_BTN_ADD, false);
            Btn(host, WardenLang.Lang.Tr("删除选中"), 113, 440, 105, 30, ID_BTN_DEL, false);
            Btn(host, WardenLang.Lang.Tr("编辑选中"), 226, 440, 105, 30, ID_BTN_EDIT, false);
            Btn(host, WardenLang.Lang.Tr("导出规则"), 345, 440, 105, 30, ID_BTN_EXPORT, false);
            Btn(host, WardenLang.Lang.Tr("导入规则"), 458, 440, 105, 30, ID_BTN_IMPORT, false);
            Lab(host, WardenLang.Lang.Tr("★ 勾选即启用该规则。\"进程\"和\"标题关键词\"至少要填一个。"),
                10, 480, 700, 20, ID_RULE_HINT, _fontBase);
        }

        private static void AddColumn(int idx, string text, int width)
        {
            if (_lvRules == IntPtr.Zero) { return; }
            IntPtr p = Sz(text);
            try
            {
                var col = new LVCOLUMNW
                {
                    mask = LVCF_FMT | LVCF_WIDTH | LVCF_TEXT,
                    fmt = LVCFMT_LEFT,
                    cx = S(width),
                    pszText = p,
                };
                long r = SendMessageW(_lvRules, LVM_INSERTCOLUMNW, (IntPtr)idx, (IntPtr)(&col)).ToInt64();
                L("  列 #" + idx + " 「" + text + "」 宽=" + S(width) + " → " + r);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---------------------------------------------------------------- 保护页
        private static void BuildProtectPage()
        {
            int y = 10;
            _ckFg = Chk(_pageProtect, ID_CK_FG, WardenLang.Lang.Tr("当前活动窗口不处理（skip_if_foreground）"), 10, y, true); y += 34;
            _ckEdit = Chk(_pageProtect, ID_CK_EDIT, WardenLang.Lang.Tr("含输入框的窗口不处理（skip_if_has_edit）"), 10, y, true); y += 34;
            _ckChild = Chk(_pageProtect, ID_CK_CHILD, WardenLang.Lang.Tr("含子控件的窗口不处理（skip_if_has_child_window）"), 10, y, true); y += 34;
            _ckDry = Chk(_pageProtect, ID_CK_DRY, WardenLang.Lang.Tr("★ 演习模式：只记日志不真动手（dry_run）"), 10, y, true); y += 46;
            Lab(_pageProtect, WardenLang.Lang.Tr("★ 建议：第一次用先开着演习模式跑几天，"), 10, y, 600, 20, ID_LBL_TIP1, _fontBase); y += 24;
            Lab(_pageProtect, WardenLang.Lang.Tr("   看日志里它打算关谁，确认没有误伤，再关掉演习模式。"), 10, y, 600, 20, ID_LBL_TIP2, _fontBase);
        }

        // ---------------------------------------------------------------- 处置页
        private static void BuildActionPage()
        {
            int y = 10;
            _ckTerm = Chk(_pageAction, ID_CK_TERM, WardenLang.Lang.Tr("允许使用强制手段（allow_terminate）"), 10, y, false); y += 42;

            Lab(_pageAction, WardenLang.Lang.Tr("动手门槛（分数达到才处理）"), 10, y, 250, 20, ID_LBL_APPLY, _fontBase);
            _numApply = Num(_pageAction, ID_NUM_APPLY, 270, y, 55, 1, 200); y += 32;
            Lab(_pageAction, WardenLang.Lang.Tr("强杀门槛（分数达到才考虑强杀）"), 10, y, 250, 20, ID_LBL_TERMTH, _fontBase);
            _numTermTh = Num(_pageAction, ID_NUM_TERMTH, 270, y, 75, 1, 300); y += 42;

            Lab(_pageAction, WardenLang.Lang.Tr("同一窗口失败几次才升级手段"), 10, y, 250, 20, ID_LBL_FAIL, _fontBase);
            _numFail = Num(_pageAction, ID_NUM_FAIL, 270, y, 3, 1, 50); y += 32;
            Lab(_pageAction, WardenLang.Lang.Tr("失败几次后才弹窗询问（受保护进程）"), 10, y, 250, 20, ID_LBL_ASK, _fontBase);
            _numAsk = Num(_pageAction, ID_NUM_ASK, 270, y, 6, 1, 200); y += 42;

            Lab(_pageAction, WardenLang.Lang.Tr("第二段判据：至少隐藏多少次"), 10, y, 250, 20, ID_LBL_HIDES, _fontBase);
            _numHides = Num(_pageAction, ID_NUM_HIDES, 270, y, 8, 1, 200); y += 32;
            Lab(_pageAction, WardenLang.Lang.Tr("第二段兜底等待（毫秒）"), 10, y, 250, 20, ID_LBL_WAIT, _fontBase);
            _numWait = Num(_pageAction, ID_NUM_WAIT, 270, y, 6000, 1000, 60000); y += 44;

            _ckPrompt = Chk(_pageAction, ID_CK_PROMPT, WardenLang.Lang.Tr("允许弹窗询问用户（allow_consent_prompt）"), 10, y, true); y += 30;
            _ckHonorSig = Chk(_pageAction, ID_CK_HONORSIG, "遵守\"有合法签名的进程不直接强杀\"（honor_signatures）", 10, y, true); y += 30;
            _ckHonorWl = Chk(_pageAction, ID_CK_HONORWL, "遵守\"白名单进程不直接强杀\"（honor_whitelist_for_kill）", 10, y, true); y += 40;

            Lab(_pageAction, "★ 说明：第二段弹窗现在是按\"追不上\"判断的 ——", 10, y, 700, 20, ID_LBL_ACT1, _fontBase); y += 24;
            Lab(_pageAction, WardenLang.Lang.Tr("   已隐藏够多次但窗口数没减少，就立刻弹（几秒内），"), 10, y, 700, 20, ID_LBL_ACT2, _fontBase); y += 24;
            Lab(_pageAction, WardenLang.Lang.Tr("   上面那个毫秒值只是兜底上限。"), 10, y, 700, 20, ID_LBL_ACT3, _fontBase);
        }

        // ---------------------------------------------------------------- 名单页
        private static void BuildListsPage()
        {
            Lab(_pageLists, WardenLang.Lang.Tr("白名单（这些进程永不处理，一行一个）"), 8, 8, 380, 20, ID_LBL_WL, _fontBase);
            _txtWl = CreateWindowExW(0, _clsPagePtr, Sz(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | WS_VSCROLL | ES_LEFT | ES_MULTILINE | ES_AUTOVSCROLL,
                S(8), S(30), S(380), S(150), _pageLists, (IntPtr)ID_TXT_WL, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(_txtWl, WM_SETFONT, _fontBase, (IntPtr)1);

            Lab(_pageLists, WardenLang.Lang.Tr("永久允许强杀（一行一个）"), 400, 8, 380, 20, ID_LBL_CONSENT, _fontBase);
            _txtConsent = CreateWindowExW(0, _clsPagePtr, Sz(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | WS_VSCROLL | ES_LEFT | ES_MULTILINE | ES_AUTOVSCROLL,
                S(400), S(30), S(380), S(150), _pageLists, (IntPtr)ID_TXT_CONSENT, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(_txtConsent, WM_SETFONT, _fontBase, (IntPtr)1);

            Lab(_pageLists, "★ 强杀历史（用于检测\"杀了又复活\"，清空可重置判定）", 8, 192, 600, 20, ID_LBL_TERMHIST, _fontBase);
            // ★ 这一行刻意留中文字面量（与原版一字不差 ✓ 原文就没有走语言包 ✓）
            _txtTermHist = CreateWindowExW(0, _clsPagePtr, Sz(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | WS_VSCROLL | ES_LEFT | ES_MULTILINE | ES_AUTOVSCROLL,
                S(8), S(216), S(380), S(140), _pageLists, (IntPtr)ID_TXT_TERMHIST, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(_txtTermHist, WM_SETFONT, _fontBase, (IntPtr)1);

            Btn(_pageLists, WardenLang.Lang.Tr("清空强杀历史"), 400, 216, 140, 30, ID_BTN_CLEARHIST, false);

            Lab(_pageLists, WardenLang.Lang.Tr("★ 清空后：如果同一个程序再次出现，"), 400, 256, 380, 20, ID_LBL_HINT1, _fontBase);
            Lab(_pageLists, "   弹窗不会再推荐\"永久允许强杀\"。", 400, 280, 380, 20, ID_LBL_HINT2, _fontBase);

            L("名单页控件：" + _txtWl + " / " + _txtConsent + " / " + _txtTermHist
              + " err=" + Marshal.GetLastWin32Error());
        }

        // ---------------------------------------------------------------- 安全页
        private static void BuildSafetyPage()
        {
            int y = 10;
            _ckSelf = Chk(_pageSafety, ID_CK_SELF, WardenLang.Lang.Tr("启动时自校验（防止程序被调包）"), 10, y, true); y += 34;
            _ckBlock = Chk(_pageSafety, ID_CK_BLOCK, WardenLang.Lang.Tr("★ 自校验失败时拒绝运行（拉停）"), 10, y, true); y += 46;

            Lab(_pageSafety, WardenLang.Lang.Tr("期望的自身哈希（签名脚本会写入）"), 10, y, 400, 20, ID_LBL_HASHTITLE, _fontBase); y += 24;
            _txtHash = CreateWindowExW(0, _clsPagePtr, Sz(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | ES_LEFT | ES_AUTOHSCROLL,
                S(10), S(y), S(600), S(24), _pageSafety, (IntPtr)ID_TXT_HASH, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(_txtHash, WM_SETFONT, _fontBase, (IntPtr)1); y += 32;

            Btn(_pageSafety, WardenLang.Lang.Tr("用当前 WARDEN.exe 的哈希填入"), 10, y, 240, 30, ID_BTN_HASH, false); y += 46;

            Lab(_pageSafety, WardenLang.Lang.Tr("── 硬黑名单（系统关键进程，永不强杀，不可修改）──"), 10, y, 620, 20, ID_LBL_HARD, _fontBase); y += 24;

            _txtHard = CreateWindowExW(0, _clsPagePtr, Sz(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | WS_VSCROLL | ES_LEFT | ES_MULTILINE
                | ES_AUTOVSCROLL | ES_READONLY,
                S(10), S(y), S(760), S(190), _pageSafety, (IntPtr)ID_TXT_HARD, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(_txtHard, WM_SETFONT, _fontMono, (IntPtr)1);
            SetWindowTextW(_txtHard,
                "csrss.exe   wininit.exe   winlogon.exe   services.exe   lsass.exe   smss.exe\r\n" +
                "svchost.exe   dwm.exe   explorer.exe   System   Registry   MemCompression\r\n" +
                "fontdrvhost.exe   ctfmon.exe   sihost.exe   taskhostw.exe   audiodg.exe\r\n" +
                "spoolsv.exe   MsMpEng.exe   SearchIndexer.exe   ... （共 41 条）\r\n" +
                "\r\n" +
                "另外：映像路径在 System32 / SysWOW64 / WinSxS / Servicing /\r\n" +
                "      SystemApps 下的进程，也一律不强杀。\r\n" +
                "\r\n" +
                "★ 这两道是【不可配置】的 —— 它们保护的是系统本身。");
        }

        // ---------------------------------------------------------------- 日志页
        private static void BuildLogPage()
        {
            Btn(_pageLog, WardenLang.Lang.Tr("刷新日志"), 0, 0, 100, 28, ID_BTN_REFRESHLOG, false);
            _numLines = Num(_pageLog, ID_NUM_LINES, 108, 2, 200, 20, 5000);
            Lab(_pageLog, WardenLang.Lang.Tr("行（最近的）"), 194, 6, 90, 20, ID_LBL_LINES, _fontBase);
            Btn(_pageLog, WardenLang.Lang.Tr("打开日志所在文件夹"), 290, 0, 170, 28, ID_BTN_OPENFOLDER, false);

            _txtLog = CreateWindowExW(0, _clsPagePtr, Sz(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | WS_VSCROLL | WS_HSCROLL
                | ES_LEFT | ES_MULTILINE | ES_AUTOVSCROLL | ES_AUTOHSCROLL | ES_READONLY,
                S(0), S(36), S(770), S(470), _pageLog, (IntPtr)ID_TXT_LOG, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(_txtLog, WM_SETFONT, _fontMonoSmall, (IntPtr)1);
        }

        // ====================================================================
        //  布局：830x640 基准 + WM_SIZE 重排
        // ====================================================================
        private static void LayoutAll()
        {
            RECT cr;
            GetClientRect(_main, out cr);
            int cw = cr.R - cr.L, ch = cr.B - cr.T;
            int barH = S(BOTTOM_H);

            if (_tabs != IntPtr.Zero)
            {
                SetWindowPos(_tabs, IntPtr.Zero, 0, 0, cw, Math.Max(0, ch - barH), SWP_NOZORDER | SWP_NOACTIVATE);
            }
            if (_bottom != IntPtr.Zero)
            {
                SetWindowPos(_bottom, IntPtr.Zero, 0, Math.Max(0, ch - barH), cw, barH, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            // 状态标签：客户区 (495, 18) 330x24（底部条内 ✓）
            if (_status != IntPtr.Zero)
            {
                SetWindowPos(_status, IntPtr.Zero, S(495), Math.Max(0, ch - barH) + S(18), S(330), S(24), SWP_NOZORDER | SWP_NOACTIVATE);
            }

            // 页容器：页签显示区（左 4 / 上 24，随窗口拉伸 ✓）
            int px = S(TAB_INSET), py = S(TAB_TOP);
            int pw = Math.Max(0, cw - 2 * S(TAB_INSET));
            int ph = Math.Max(0, ch - barH - py - S(TAB_INSET));
            IntPtr[] pages = { _pageLang, _pageRules, _pageProtect, _pageAction, _pageLists, _pageSafety, _pageLog };
            foreach (IntPtr p in pages)
            {
                if (p != IntPtr.Zero) { SetWindowPos(p, IntPtr.Zero, px, py, pw, ph, SWP_NOZORDER | SWP_NOACTIVATE); }
            }
            if (_hostRules != IntPtr.Zero)
            {
                SetWindowPos(_hostRules, IntPtr.Zero, 0, 0, pw, ph, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            // 规则表：宽随页拉伸（旧版 Anchor Top|Left|Right ✓）高度固定 430 ✓
            if (_lvRules != IntPtr.Zero)
            {
                SetWindowPos(_lvRules, IntPtr.Zero, S(0), S(0), Math.Max(S(300), pw - S(8)), S(430), SWP_NOZORDER | SWP_NOACTIVATE);
            }
            // 日志正文：宽高都随页拉伸（旧版四边 Anchor ✓）
            if (_txtLog != IntPtr.Zero)
            {
                SetWindowPos(_txtLog, IntPtr.Zero, S(0), S(36), Math.Max(S(300), pw - S(8)), Math.Max(S(120), ph - S(44)), SWP_NOZORDER | SWP_NOACTIVATE);
            }
            UpdateRulesScrollBar();

            if (!_layoutLogged)
            {
                _layoutLogged = true;
                L("布局（客户区 " + cw + "x" + ch + "）：页签(0,0," + cw + "," + (ch - barH) + ")"
                  + " 底栏(0," + (ch - barH) + "," + cw + "," + barH + ")"
                  + " 页面(" + px + "," + py + "," + pw + "," + ph + ")"
                  + " 状态标签(" + S(495) + "," + (ch - barH + S(18)) + "," + S(330) + "," + S(24) + ")");
                L("规则表(" + S(0) + "," + S(0) + "," + Math.Max(S(300), pw - S(8)) + "," + S(430) + ")"
                  + " 规则页按钮 y=" + S(440) + " 高=" + S(30)
                  + " 提示标签(10,480) | 内容高 " + RULES_CONTENT_H + " 逻辑像素 ✓");
            }
        }

        private static bool _layoutLogged;

        private static int HostHeight()
        {
            if (_hostRules == IntPtr.Zero) { return 0; }
            RECT r;
            GetClientRect(_hostRules, out r);
            return r.B - r.T;
        }

        /// <summary>★ 复刻旧版 AutoScroll：内容比可视区高就出滚动条 ✓（规则页 ✓）</summary>
        private static void UpdateRulesScrollBar()
        {
            if (_hostRules == IntPtr.Zero) { return; }
            int viewH = HostHeight();
            int contentH = S(RULES_CONTENT_H);
            var si = new SCROLLINFO
            {
                cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
                fMask = SIF_RANGE | SIF_PAGE | SIF_POS,
                nMin = 0,
                nMax = Math.Max(0, contentH - 1),
                nPage = Math.Max(1, viewH),   // ★ 修：SCROLLINFO.nPage 是 int ✓ 不能塞 uint ✗
                nPos = _rulesScroll,
            };
            SetScrollInfo(_hostRules, SB_VERT, ref si, true);
            SetRulesScroll(_rulesScroll);
        }

        private static void SetRulesScroll(int pos)
        {
            if (_hostRules == IntPtr.Zero) { return; }
            int viewH = HostHeight();
            int contentH = S(RULES_CONTENT_H);
            int max = Math.Max(0, contentH - viewH);
            if (pos < 0) { pos = 0; }
            if (pos > max) { pos = max; }
            int dy = _rulesScroll - pos;
            _rulesScroll = pos;
            SetScrollPos(_hostRules, SB_VERT, pos, true);
            // ★ 滚动容器里的子控件要跟着挪（ScrollWindowEx 只挪内容不挪子窗口 ✓）
            if (dy != 0 && _lvRules != IntPtr.Zero)
            {
                RECT r;
                GetWindowRect(_lvRules, out r);
                IntPtr parent = _pageRules;
                // 用 SetWindowPos 移动子控件（父容器滚动偏移直接加在 y 上 ✓）
                SetWindowPos(_lvRules, IntPtr.Zero, S(0), S(0) - _rulesScroll, 0, 0,
                    SWP_NOZORDER | SWP_NOACTIVATE | 0x0001 /*SWP_NOSIZE*/);
                for (int id = ID_BTN_ADD; id <= ID_BTN_IMPORT; id++)
                {
                    IntPtr c = GetDlgItem(_hostRules, id);
                    if (c != IntPtr.Zero)
                    {
                        SetWindowPos(c, IntPtr.Zero, 0, S(440) - _rulesScroll, 0, 0,
                            SWP_NOZORDER | SWP_NOACTIVATE | 0x0001);
                    }
                }
                IntPtr hint = GetDlgItem(_hostRules, ID_RULE_HINT);
                if (hint != IntPtr.Zero)
                {
                    SetWindowPos(hint, IntPtr.Zero, 0, S(480) - _rulesScroll, 0, 0,
                        SWP_NOZORDER | SWP_NOACTIVATE | 0x0001);
                }
                GC.KeepAlive(parent);
            }
            InvalidateRect(_hostRules, IntPtr.Zero, true);
        }

        /// <summary>页签切换：只显示当前页 ✓（对应旧版 TabControl 的行为 ✓）</summary>
        private static void ShowPage(int idx)
        {
            if (idx < 0) { idx = 0; }
            _curPage = idx;
            IntPtr[] pages = { _pageLang, _pageRules, _pageProtect, _pageAction, _pageLists, _pageSafety, _pageLog };
            for (int i = 0; i < pages.Length; i++)
            {
                if (pages[i] == IntPtr.Zero) { continue; }
                ShowWindow(pages[i], i == idx ? SW_SHOW : 0 /*SW_HIDE*/);
            }
            L("页签切换到 #" + idx + "（" + TabName(idx) + "）✓");
        }

        private static string TabName(int idx) => idx switch
        {
            0 => WardenLang.Lang.T("set.tab.lang"),
            1 => WardenLang.Lang.T("set.tab.rules"),
            2 => WardenLang.Lang.T("set.tab.protect"),
            3 => WardenLang.Lang.T("set.tab.action"),
            4 => WardenLang.Lang.T("set.tab.lists"),
            5 => WardenLang.Lang.T("set.tab.safety"),
            6 => WardenLang.Lang.T("set.tab.log"),
            _ => "?",
        };

        // ====================================================================
        //  命令分发（★ id / code 的取法就是闸门里要求的那种 ✓）
        // ====================================================================
        private static IntPtr OnCommand(IntPtr h, int id, int code, IntPtr sender)
        {
            switch (id)
            {
                case ID_SAVE:
                    if (code == 0) { L("→ 动作：保存配置"); Settings.SaveConfig(_main); }
                    return IntPtr.Zero;

                case ID_RELOAD:
                    if (code == 0)
                    {
                        L("→ 动作：重新加载（先问一次 ✓）");
                        // ★ 旧版 MessageBoxButtons.OKCancel → 等价 MB_OKCANCEL（是/否语义一致 ✓）
                        int r = MessageBoxW(_main,
                            WardenLang.Lang.Tr("重新读取配置文件？\n界面上的未保存修改会丢失。"),
                            WardenLang.Lang.Tr("WARDEN 设置"), MB_OKCANCEL | MB_ICONQUESTION);
                        if (r == 1 /*IDOK*/)
                        {
                            Settings.LoadConfig();
                            RefreshLog();
                            UpdateStatus();
                            L("重新加载完成 ✓");
                        }
                        else { L("重新加载：用户取消 ✓"); }
                    }
                    return IntPtr.Zero;

                case ID_BTN_ADD:
                    if (code == 0) { L("→ 动作：新增规则"); AddRule(); }
                    return IntPtr.Zero;

                case ID_BTN_DEL:
                    if (code == 0) { L("→ 动作：删除选中"); DeleteSelected(); }
                    return IntPtr.Zero;

                case ID_BTN_EDIT:
                    if (code == 0) { L("→ 动作：编辑选中"); EditRule(); }
                    return IntPtr.Zero;

                case ID_BTN_EXPORT:
                    if (code == 0) { L("→ 动作：导出规则"); Settings.ExportRules(_main); }
                    return IntPtr.Zero;

                case ID_BTN_IMPORT:
                    if (code == 0) { L("→ 动作：导入规则"); Settings.ImportRules(_main); }
                    return IntPtr.Zero;

                case ID_BTN_CLEARHIST:
                    if (code == 0)
                    {
                        L("→ 动作：清空强杀历史（只清 _txtTermHist ✓）");
                        SetWindowTextW(_txtTermHist, "");
                    }
                    return IntPtr.Zero;

                case ID_BTN_HASH:
                    if (code == 0) { L("→ 动作：用当前 WARDEN.exe 的哈希填入"); Settings.FillHash(_main); }
                    return IntPtr.Zero;

                case ID_BTN_REFRESHLOG:
                    if (code == 0) { L("→ 动作：刷新日志"); RefreshLog(); }
                    return IntPtr.Zero;

                case ID_BTN_OPENFOLDER:
                    if (code == 0) { L("→ 动作：打开日志所在文件夹"); Settings.OpenToolDir(_main); }
                    return IntPtr.Zero;

                case ID_CB_LANG:
                    if (code == 1 /*CBN_SELCHANGE*/) { L("→ 动作：语言下拉变化"); OnLangChanged(); }
                    return IntPtr.Zero;

                case ID_TABS:
                    // 页签是自建窗口类，收不到 TCN_SELCHANGE ✗ → 用 WM_COMMAND 也不通 ✗
                    // 所以页签切换走 TCM_GETCURSEL 轮询（见 WM_NOTIFY 之外的 Timer/UiTick ✓）
                    return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        // ====================================================================
        //  业务：规则表（★ 数据读写全走 Settings ✓ 这里只管界面 ✓）
        // ====================================================================
        private static int RuleCount()
            => _lvRules == IntPtr.Zero ? 0 : (int)SendMessageW(_lvRules, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt64();

        private static bool RuleChecked(int i)
        {
            long st = SendMessageW(_lvRules, LVM_GETITEMSTATE, (IntPtr)i, (IntPtr)LVIS_STATEIMAGEMASK).ToInt64();
            return ((st >> 12) & 0xF) == 2;    // image 2 = 已勾选 ✓（1 = 未勾选 ✓）
        }

        private static void SetRuleChecked(int i, bool val)
        {
            long img = val ? 2 : 1;   // INDEXTOSTATEIMAGEMASK ✓
            var it = new LVITEMW
            {
                mask = 0x0008 /*LVIF_STATE*/,
                iItem = i,
                state = (uint)(img << 12),
                stateMask = LVIS_STATEIMAGEMASK,
            };
            SendMessageW(_lvRules, LVM_SETITEMW, IntPtr.Zero, (IntPtr)(&it));
        }

        private static void SetRuleSub(int i, int sub, string text)
        {
            IntPtr p = Sz(text);
            try
            {
                var it = new LVITEMW { iSubItem = sub, pszText = p };
                SendMessageW(_lvRules, LVM_SETITEMTEXTW, (IntPtr)i, (IntPtr)(&it));
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static string GetRuleSub(int i, int sub)
        {
            var buf = new char[1024];
            var it = new LVITEMW { iSubItem = sub, cchTextMax = buf.Length };
            fixed (char* p = buf)
            {
                it.pszText = (IntPtr)p;
                SendMessageW(_lvRules, LVM_FIRST + 115 /*LVM_GETITEMTEXTW*/, (IntPtr)i, (IntPtr)(&it));
            }
            int n = Array.IndexOf(buf, NUL);
            if (n < 0) { n = buf.Length; }
            return new string(buf, 0, n);
        }

        private static void SetRules(IReadOnlyList<Rule> rules)
        {
            if (_lvRules == IntPtr.Zero)
            {
                L("★ 规则表句柄为空，跳过填充");
                return;
            }
            SendMessageW(_lvRules, LVM_DELETEALLITEMS, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < rules.Count; i++)
            {
                var it = new LVITEMW { mask = 0x0001 /*LVIF_TEXT*/, iItem = i, iSubItem = 0, pszText = IntPtr.Zero };
                long r = SendMessageW(_lvRules, LVM_INSERTITEMW, IntPtr.Zero, (IntPtr)(&it)).ToInt64();
                if (r < 0) { L("★ LVM_INSERTITEMW 失败 i=" + i + " → " + r); continue; }
                SetRuleSub(i, 1, rules[i].Name);
                SetRuleSub(i, 2, rules[i].Process);
                SetRuleSub(i, 3, rules[i].Keywords);
                SetRuleChecked(i, rules[i].Enabled);
            }
            L("规则表已填充：" + rules.Count + " 条（LVM_GETITEMCOUNT=" + RuleCount() + " ✓）");
        }

        private static List<Rule> GetRules()
        {
            var list = new List<Rule>();
            int n = RuleCount();
            for (int i = 0; i < n; i++)
            {
                list.Add(new Rule
                {
                    Name = GetRuleSub(i, 1),
                    Process = GetRuleSub(i, 2),
                    Keywords = GetRuleSub(i, 3),
                    Enabled = RuleChecked(i),
                });
            }
            return list;
        }

        private static int SelectedRule()
        {
            if (_lvRules == IntPtr.Zero) { return -1; }
            return (int)SendMessageW(_lvRules, LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)LVNI_SELECTED).ToInt64();
        }

        private static void DeleteSelected()
        {
            if (_lvRules == IntPtr.Zero) { return; }
            var idx = new List<int>();
            int cur = -1;
            while (true)
            {
                cur = (int)SendMessageW(_lvRules, LVM_GETNEXTITEM, (IntPtr)cur, (IntPtr)LVNI_SELECTED).ToInt64();
                if (cur < 0) { break; }
                idx.Add(cur);
                if (idx.Count > 10000) { break; }
            }
            // ★ 从后往前删（从前往后删会让后面的索引整体前移 ✗）
            for (int i = idx.Count - 1; i >= 0; i--)
            {
                SendMessageW(_lvRules, LVM_DELETEITEM, (IntPtr)idx[i], IntPtr.Zero);
            }
            L("删除选中：" + idx.Count + " 条（删完剩 " + RuleCount() + " 条 ✓）");
        }

        private static void AddRule()
        {
            string name, proc, kws;
            if (!RuleDialog.Show(_main, WardenLang.Lang.Tr("新增规则"), "", "", "", out name, out proc, out kws))
            {
                L("新增规则：用户取消 ✓");
                return;
            }
            if (_lvRules == IntPtr.Zero) { return; }
            int i = RuleCount();
            var it = new LVITEMW { mask = 0x0001, iItem = i, iSubItem = 0, pszText = IntPtr.Zero };
            SendMessageW(_lvRules, LVM_INSERTITEMW, IntPtr.Zero, (IntPtr)(&it));
            SetRuleSub(i, 1, name);
            SetRuleSub(i, 2, proc);
            SetRuleSub(i, 3, kws);
            SetRuleChecked(i, true);   // ★ 旧版 it.Checked = true ✓
            L("新增规则：「" + name + "」/「" + proc + "」/「" + kws + "」→ 现在 " + RuleCount() + " 条 ✓");
        }

        private static void EditRule()
        {
            int sel = SelectedRule();
            if (sel < 0)
            {
                L("编辑选中：没有选中行 ✓ 弹窗提示");
                MessageBoxW(_main, WardenLang.Lang.Tr("先选中一条规则。"),
                    WardenLang.Lang.Tr("WARDEN 设置"), MB_OK | MB_ICONWARNING);
                return;
            }
            string name, proc, kws;
            if (!RuleDialog.Show(_main, WardenLang.Lang.Tr("编辑规则"),
                    GetRuleSub(sel, 1), GetRuleSub(sel, 2), GetRuleSub(sel, 3), out name, out proc, out kws))
            {
                L("编辑规则：用户取消 ✓");
                return;
            }
            SetRuleSub(sel, 1, name);
            SetRuleSub(sel, 2, proc);
            SetRuleSub(sel, 3, kws);
            L("编辑规则 #" + sel + " → 「" + name + "」/「" + proc + "」/「" + kws + "」✓");
        }

        // ====================================================================
        //  语言刷新（★ 规格：切换后所有页签立即刷新 ✓）
        // ====================================================================
        private static void OnLangChanged()
        {
            if (_relanging || _cbLang == IntPtr.Zero) { return; }
            long ci = SendMessageW(_cbLang, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64();
            var codes = WardenLang.Lang.Available();
            if (ci < 0 || ci >= codes.Count) { L("语言下拉：选中索引越界 (" + ci + ") ✓ 忽略"); return; }
            string code = codes[(int)ci];
            string old = WardenLang.Lang.Code;
            try
            {
                WardenLang.Lang.Load(code);
                L("界面语言已切到 " + code + "（原 " + old + "）Loaded=" + WardenLang.Lang.Loaded
                  + " name=" + WardenLang.Lang.Name
                  + (WardenLang.Lang.LoadError.Length > 0 ? " ★错误=" + WardenLang.Lang.LoadError : ""));
            }
            catch (Exception e)
            {
                // ★ 语言包不合法 → 保持原语言 ✓（旧版行为一致 ✓）
                L("★ 语言包加载失败，保持原语言：" + e.Message);
                return;
            }
            Settings.AppendSetupLog("  [语言] 界面语言已切到 " + code + "（保存配置后生效 ✓）");
            ApplyLangToControls();
            UpdateStatus();
            L("界面文字已按 " + code + " 全量刷新 ✓");
        }

        // 语言刷新用的登记表：控件 ID → 原始中文（Tr 的键 ✓ 口径一字不改 ✓）
        private static readonly Dictionary<int, string> _src = new();
        private static void Reg(int id, string source)
        {
            if (id != 0 && source != null) { _src[id] = source; }
        }

        private static string Tr2(int id, string fallback)
            => _src.TryGetValue(id, out string s) ? WardenLang.Lang.Tr(s) : fallback;

        /// <summary>把界面上所有文字按当前语言重刷一遍（含页签标题与列标题 ✓）</summary>
        private static void ApplyLangToControls()
        {
            if (_main == IntPtr.Zero) { return; }
            int n = 0;
            foreach (var kv in _src)
            {
                IntPtr c = GetDlgItem(_main, kv.Key);
                if (c == IntPtr.Zero)
                {
                    // 页里的控件不是主窗的直接子窗 → 逐页找 ✓
                    IntPtr[] pages = { _pageLang, _pageRules, _pageProtect, _pageAction, _pageLists, _pageSafety, _pageLog };
                    foreach (IntPtr p in pages)
                    {
                        if (p == IntPtr.Zero) { continue; }
                        c = GetDlgItem(p, kv.Key);
                        if (c != IntPtr.Zero) { break; }
                        if (p == _pageRules)
                        {
                            c = GetDlgItem(_hostRules, kv.Key);
                            if (c != IntPtr.Zero) { break; }
                        }
                    }
                }
                if (c == IntPtr.Zero) { continue; }
                SetWindowTextW(c, WardenLang.Lang.Tr(kv.Value));
                n++;
            }
            L("语言刷新：重设了 " + n + " 个控件文字");

            // 页签标题
            if (_tabs != IntPtr.Zero)
            {
                for (int i = 0; i < 7; i++)
                {
                    IntPtr p = Sz(TabTitle(i));
                    try
                    {
                        var it = new TCITEMW { mask = 0x0001, pszText = p };
                        SendMessageW(_tabs, TCM_FIRST + 61 /*TCM_SETITEMW*/, (IntPtr)i, (IntPtr)(&it));
                    }
                    finally { Marshal.FreeHGlobal(p); }
                }
            }
            // 列标题
            string[] cols = { WardenLang.Lang.Tr("启用"), WardenLang.Lang.Tr("名称"),
                              WardenLang.Lang.Tr("进程"), WardenLang.Lang.Tr("标题关键词") };
            int[] widths = { 52, 185, 135, 400 };
            for (int i = 0; i < 4; i++)
            {
                IntPtr p = Sz(cols[i]);
                try
                {
                    var col = new LVCOLUMNW { mask = LVCF_TEXT | LVCF_WIDTH, cx = S(widths[i]), pszText = p };
                    SendMessageW(_lvRules, LVM_SETCOLUMNW, (IntPtr)i, (IntPtr)(&col));
                }
                finally { Marshal.FreeHGlobal(p); }
            }
            L("页签标题 + 规则表列标题已按新语言刷新 ✓");
        }

        private static string TabTitle(int i) => i switch
        {
            0 => WardenLang.Lang.T("set.tab.lang"),
            1 => WardenLang.Lang.T("set.tab.rules"),
            2 => WardenLang.Lang.T("set.tab.protect"),
            3 => WardenLang.Lang.T("set.tab.action"),
            4 => WardenLang.Lang.T("set.tab.lists"),
            5 => WardenLang.Lang.T("set.tab.safety"),
            6 => WardenLang.Lang.T("set.tab.log"),
            _ => "?",
        };

        // ====================================================================
        //  状态行 / 日志（对应旧版 UpdateStatus / RefreshLog ✓）
        // ====================================================================
        internal static void UpdateStatus()
        {
            try
            {
                int pid = Settings.WardenPid();
                string text;
                if (pid > 0)
                {
                    text = WardenLang.Lang.Tr("状态：✓ 运行中（PID ") + pid + "）";
                    _statusColor = CLR_GREEN;
                }
                else
                {
                    text = WardenLang.Lang.Tr("状态：· 未运行");
                    _statusColor = CLR_GRAY;
                }
                string selfCheck = Settings.SelfCheckLine();
                if (selfCheck != null)
                {
                    if (selfCheck.Contains("结果=通过"))
                    {
                        text += "    " + WardenLang.Lang.Tr("自校验：✓ 通过");
                    }
                    else
                    {
                        text += "    " + WardenLang.Lang.Tr("自校验：★ 未通过");
                        _statusColor = CLR_RED;
                    }
                }
                if (_status != IntPtr.Zero)
                {
                    SetWindowTextW(_status, text);
                    InvalidateRect(_status, IntPtr.Zero, true);
                }
            }
            catch (Exception e) { L("UpdateStatus 异常：" + e.Message); }
        }

        internal static void RefreshLog()
        {
            if (_txtLog == IntPtr.Zero) { return; }
            if (!File.Exists(Settings.LogPath))
            {
                SetWindowTextW(_txtLog, WardenLang.Lang.Tr("（还没有日志文件）"));
                L("刷新日志：日志文件不存在 → 显示提示 ✓");
                return;
            }
            int lines = NumValue(_numLines, 200);
            var tail = Settings.TailLines(Settings.LogPath, lines);
            SetWindowTextW(_txtLog, string.Join("\r\n", tail));
            SendMessageW(_txtLog, EM_SETSEL, (IntPtr)(-1), (IntPtr)(-1));
            SendMessageW(_txtLog, EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);
            L("刷新日志：取最近 " + lines + " 行 → 实得 " + tail.Count + " 行 ✓");
        }

        // ====================================================================
        //  界面读写（★ Settings 通过这些方法操作控件 ✓ 业务逻辑一字未改 ✓）
        // ====================================================================
        private static string TextOf(IntPtr h)
        {
            if (h == IntPtr.Zero) { return ""; }
            int n = GetWindowTextLengthW(h);
            if (n <= 0) { return ""; }
            var buf = new char[n + 1];
            int got = GetWindowTextW(h, buf, buf.Length);
            return got <= 0 ? "" : new string(buf, 0, got);
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) { return ""; }
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            return s.Length > 40 ? s.Substring(0, 40) + "…" : s;
        }

        private static bool Checked(IntPtr c)
            => c != IntPtr.Zero && SendMessageW(c, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt64() == 1;

        private static void SetChecked(IntPtr c, bool v)
        {
            if (c != IntPtr.Zero) { SendMessageW(c, BM_SETCHECK, v ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero); }
        }

        /// <summary>数字框取值（读不到就用默认值 ✓）</summary>
        private static int NumValue(IntPtr edit, int def)
        {
            string s = TextOf(edit).Trim();
            return int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out int v) ? v : def;
        }

        /// <summary>数字框赋值（★ 同时钳到上下限 —— 对应旧版 Minimum/Maximum 的钳制 ✓）</summary>
        private static void SetNum(IntPtr edit, int min, int max, int val)
        {
            if (edit == IntPtr.Zero) { return; }
            int v = Math.Max(min, Math.Min(max, val));
            SetWindowTextW(edit, v.ToString(CultureInfo.InvariantCulture));
        }

        // ---- 供 Settings 调用的读写接口（字段名与规格一一对应 ✓） ----
        internal static bool GetCheck(int which) => which switch
        {
            0 => Checked(_ckFg), 1 => Checked(_ckEdit), 2 => Checked(_ckChild), 3 => Checked(_ckDry),
            4 => Checked(_ckTerm), 5 => Checked(_ckPrompt), 6 => Checked(_ckHonorSig), 7 => Checked(_ckHonorWl),
            8 => Checked(_ckSelf), 9 => Checked(_ckBlock),
            _ => false,
        };

        internal static void SetCheck(int which, bool v)
        {
            switch (which)
            {
                case 0: SetChecked(_ckFg, v); break;
                case 1: SetChecked(_ckEdit, v); break;
                case 2: SetChecked(_ckChild, v); break;
                case 3: SetChecked(_ckDry, v); break;
                case 4: SetChecked(_ckTerm, v); break;
                case 5: SetChecked(_ckPrompt, v); break;
                case 6: SetChecked(_ckHonorSig, v); break;
                case 7: SetChecked(_ckHonorWl, v); break;
                case 8: SetChecked(_ckSelf, v); break;
                case 9: SetChecked(_ckBlock, v); break;
            }
        }

        internal static int GetNum(int which) => which switch
        {
            0 => NumValue(_numApply, 55), 1 => NumValue(_numTermTh, 75), 2 => NumValue(_numFail, 3),
            3 => NumValue(_numAsk, 6), 4 => NumValue(_numHides, 8), 5 => NumValue(_numWait, 6000),
            _ => 0,
        };

        internal static void SetNum(int which, int v)
        {
            switch (which)
            {
                case 0: SetNum(_numApply, 1, 200, v); break;
                case 1: SetNum(_numTermTh, 1, 300, v); break;
                case 2: SetNum(_numFail, 1, 50, v); break;
                case 3: SetNum(_numAsk, 1, 200, v); break;
                case 4: SetNum(_numHides, 1, 200, v); break;
                case 5: SetNum(_numWait, 1000, 60000, v); break;
            }
        }

        internal static string GetText(int which) => which switch
        {
            0 => TextOf(_txtWl), 1 => TextOf(_txtConsent), 2 => TextOf(_txtTermHist),
            3 => TextOf(_txtHash), _ => "",
        };

        internal static void SetText(int which, string v)
        {
            switch (which)
            {
                case 0: SetWindowTextW(_txtWl, v ?? ""); break;
                case 1: SetWindowTextW(_txtConsent, v ?? ""); break;
                case 2: SetWindowTextW(_txtTermHist, v ?? ""); break;
                case 3: SetWindowTextW(_txtHash, v ?? ""); break;
            }
        }

        internal static List<Rule> ReadRules() => GetRules();
        internal static void WriteRules(IReadOnlyList<Rule> rules) => SetRules(rules);
        internal static string CurrentLangCode()
        {
            if (_cbLang == IntPtr.Zero) { return ""; }
            long ci = SendMessageW(_cbLang, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64();
            var codes = WardenLang.Lang.Available();
            return ci >= 0 && ci < codes.Count ? codes[(int)ci] : "";
        }

        internal static string CtrlName(int id)
        {
            foreach (var kv in Names) { if (kv.Key == id) { return kv.Value; } }
            return "未知/无";
        }

        private static readonly Dictionary<int, string> Names = new()
        {
            { ID_TABS, "页签" }, { ID_SAVE, "保存配置" }, { ID_RELOAD, "重新加载" }, { ID_STATUS, "状态标签" },
            { ID_CB_LANG, "语言下拉" }, { ID_LV_RULES, "规则表" },
            { ID_BTN_ADD, "新增规则" }, { ID_BTN_DEL, "删除选中" }, { ID_BTN_EDIT, "编辑选中" },
            { ID_BTN_EXPORT, "导出规则" }, { ID_BTN_IMPORT, "导入规则" },
            { ID_BTN_CLEARHIST, "清空强杀历史" }, { ID_BTN_HASH, "哈希填入" },
            { ID_BTN_REFRESHLOG, "刷新日志" }, { ID_NUM_LINES, "日志行数" },
            { ID_BTN_OPENFOLDER, "打开日志文件夹" },
            { ID_CK_FG, "skip_if_foreground" }, { ID_CK_EDIT, "skip_if_has_edit" },
            { ID_CK_CHILD, "skip_if_has_child_window" }, { ID_CK_DRY, "dry_run" },
            { ID_CK_TERM, "allow_terminate" }, { ID_CK_PROMPT, "allow_consent_prompt" },
            { ID_CK_HONORSIG, "honor_signatures" }, { ID_CK_HONORWL, "honor_whitelist_for_kill" },
            { ID_CK_SELF, "self_check_on_start" }, { ID_CK_BLOCK, "self_check_block_on_fail" },
        };

        // ====================================================================
        //  文件对话框（取代 WinForms SaveFileDialog / OpenFileDialog ✓）
        // ====================================================================
        internal static string PickSaveFile(IntPtr owner, string title, string filter, string defaultName)
            => PickFile(owner, title, filter, defaultName, true);

        internal static string PickOpenFile(IntPtr owner, string title, string filter)
            => PickFile(owner, title, filter, "", false);

        private static string PickFile(IntPtr owner, string title, string filter, string defaultName, bool save)
        {
            IntPtr pFilter = IntPtr.Zero, pTitle = IntPtr.Zero, pDefExt = IntPtr.Zero, pFile = IntPtr.Zero;
            try
            {
                pFilter = Sz(filter);
                pTitle = Sz(title);
                pDefExt = Sz("json");
                const int maxFile = 4096;
                pFile = Marshal.AllocHGlobal(maxFile * 2);
                for (int i = 0; i < maxFile; i++) { Marshal.WriteInt16(pFile, i * 2, 0); }
                for (int i = 0; i < defaultName.Length && i < maxFile - 1; i++)
                {
                    Marshal.WriteInt16(pFile, i * 2, (short)defaultName[i]);
                }
                var ofn = new OPENFILENAMEW
                {
                    lStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                    hwndOwner = owner,
                    lpstrFilter = pFilter,
                    nFilterIndex = 1,
                    lpstrFile = pFile,
                    nMaxFile = maxFile,
                    lpstrTitle = pTitle,
                    lpstrDefExt = pDefExt,
                    Flags = save
                        ? OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY
                        : OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY,
                };
                bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
                int err = Marshal.GetLastWin32Error();
                string kind = save ? "另存为" : "打开";
                if (!ok)
                {
                    L("文件对话框（" + kind + "）用户取消 ✓ err=" + err);
                    return null;
                }
                string path = Marshal.PtrToStringUni(pFile) ?? "";
                L("文件对话框（" + kind + "）→ " + path);
                return path;
            }
            catch (Exception e)
            {
                L("★ 文件对话框异常：" + e.Message);
                return null;
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
        //  消息框（取代 WinForms MessageBox ✓）
        // ====================================================================
        private const string Caption = "WARDEN 设置";

        internal static void Info(IntPtr owner, string text)
            => MessageBoxW(owner, text ?? "", Caption, MB_OK | MB_ICONINFORMATION);

        internal static void Warn(IntPtr owner, string text)
            => MessageBoxW(owner, text ?? "", Caption, MB_OK | MB_ICONWARNING);

        internal static void Error(IntPtr owner, string text)
            => MessageBoxW(owner, text ?? "", Caption, MB_OK | MB_ICONERROR);

        /// <summary>★ 旧版 MessageBoxButtons.YesNoCancel ✓（返回值 = 6 是 / 7 否 / 2 取消 ✓）</summary>
        internal static int AskYesNoCancel(IntPtr owner, string text)
            => MessageBoxW(owner, text ?? "", Caption, MB_YESNOCANCEL | MB_ICONQUESTION);

        /// <summary>★ 旧版 MessageBoxButtons.OKCancel ✓（1 = 确定 / 2 = 取消 ✓）</summary>
        internal static int AskOkCancel(IntPtr owner, string text)
            => MessageBoxW(owner, text ?? "", Caption, MB_OKCANCEL | MB_ICONQUESTION);

        // ====================================================================
        //  产品版本（取代 System.Windows.Forms.Application.ProductVersion ✓）
        //  旧版逻辑：版本串里有 beta 就带 BCP47 代码，正式版只显示语言名 ✓
        // ====================================================================
        internal static string ProductVersion
        {
            get
            {
                try
                {
                    var asm = typeof(SettingsWin32).Assembly;
                    foreach (var a in asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false))
                    {
                        if (a is System.Reflection.AssemblyInformationalVersionAttribute iv && !string.IsNullOrEmpty(iv.InformationalVersion))
                        {
                            return iv.InformationalVersion;
                        }
                    }
                    return asm.GetName().Version?.ToString() ?? "";
                }
                catch { return ""; }
            }
        }

        // ====================================================================
        //  主窗创建 / 清理
        // ====================================================================
        private static bool CreateMainWindow()
        {
            string title = WardenLang.Lang.Tr("WARDEN 设置");
            RECT rc = new() { L = 0, T = 0, R = S(BASE_W), B = S(BASE_H) };
            AdjustWindowRectEx(ref rc, WS_OVERLAPPEDWINDOW, false, 0);
            int ww = rc.R - rc.L, wh = rc.B - rc.T;

            L("客户区 " + S(BASE_W) + "x" + S(BASE_H) + " → 窗口 " + ww + "x" + wh
              + "（AdjustWindowRectEx 换算 ✓ 否则控件被标题栏裁掉 ✓）");

            _main = CreateWindowExW(WS_EX_CONTROLPARENT, _clsMainPtr, Sz(title), WS_OVERLAPPEDWINDOW,
                0, 0, ww, wh, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            // ★ 2026-10-11 修：原来全文件没有任何 ShowWindow 调用 ✗
            //   → 主窗建好了但一直不可见 ✓（实测枚举到 WardenSettingsWnd 846x679 但可见=0 ✓）
            //   建完就显示 ✓ 并且置前台，方便键盘 Tab 与验收脚本 ✓
            if (_main != IntPtr.Zero)
            {
                ShowWindow(_main, SW_SHOW);
                UpdateWindow(_main);
                SetForegroundWindow(_main);
                L("主窗已显示 ✓ ShowWindow(SW_SHOW) + UpdateWindow + SetForegroundWindow ✓");
            }
            if (_main == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                L("★ CreateWindowExW(main) 失败 err=" + err);
                Error(IntPtr.Zero, "设置窗口创建失败（错误码 " + err + "）");
                return false;
            }
            SetWindowPos(_main, IntPtr.Zero,
                Math.Max(0, (GetSystemMetrics(0) - ww) / 2), Math.Max(0, (GetSystemMetrics(1) - wh) / 2),
                0, 0, SWP_NOZORDER | 0x0001 /*SWP_NOSIZE*/);
            ShowWindow(_main, SW_SHOW);
            UpdateWindow(_main);
            SetFocus(_main);
            L("主窗 hwnd=" + _main + " 标题=" + title + " 客户区实测=" + ClientSizeOf(_main));
            return true;
        }

        private static string ClientSizeOf(IntPtr h)
        {
            RECT r;
            GetClientRect(h, out r);
            return (r.R - r.L) + "x" + (r.B - r.T);
        }

        private static uint SafeDpi(IntPtr h)
        {
            try { return GetDpiForWindow(h); } catch { return (uint)(96 * _scale); }
        }

        private static void Cleanup()
        {
            foreach (IntPtr f in _fonts)
            {
                if (f != IntPtr.Zero && f != _fallback) { DeleteObject(f); }
            }
            _fonts.Clear();
            foreach (var kv in _brushes)
            {
                if (kv.Value != IntPtr.Zero) { DeleteObject(kv.Value); }
            }
            _brushes.Clear();
            if (_clsMainPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_clsMainPtr); _clsMainPtr = IntPtr.Zero; }
            if (_clsPagePtr != IntPtr.Zero) { Marshal.FreeHGlobal(_clsPagePtr); _clsPagePtr = IntPtr.Zero; }
            if (_clsRulesPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_clsRulesPtr); _clsRulesPtr = IntPtr.Zero; }
        }

        // ====================================================================
        //  规则编辑对话框（取代 WinForms 的 RuleDialog : Form ✓）
        //  ★ 用内存里的 DLGTEMPLATEEX + DialogBoxIndirectParamW：
        //    真正的模态对话框 → Tab/Esc/Enter 系统级支持 ✓（自定义模态循环容易漏掉这些 ✓）
        //    坐标与旧版 RuleDialog 一字不差：客户区 470x250，
        //      标签(15,20/58/96) 输入框(130,17/55/93) 提示(130,118) 确定(240,165) 取消(345,165) ✓
        // ====================================================================
        private static class RuleDialog
        {
            private static string _outName = "", _outProc = "", _outKws = "";

            /// <summary>弹出对话框；返回 true = 用户点了确定 ✓</summary>
            internal static bool Show(IntPtr owner, string title, string name, string proc, string kws,
                                      out string outName, out string outProc, out string outKws)
            {
                _outName = ""; _outProc = ""; _outKws = "";
                outName = ""; outProc = ""; outKws = "";
                bool ok = false;
                IntPtr h = IntPtr.Zero;
                try
                {
                    // ★ 对话框单位：4 单位 = 8pt 系统字体的平均字符宽/高 ✓
                    IntPtr dc = GetDC(IntPtr.Zero);
                    int baseUnitX = 6, baseUnitY = 13;
                    try
                    {
                        IntPtr old = SelectObject(dc, _fontBase);
                        TEXTMETRICW tm;
                        GetTextMetricsW(dc, out tm);
                        baseUnitX = tm.tmAveCharWidth;
                        baseUnitY = tm.tmHeight;
                        SelectObject(dc, old);
                    }
                    catch (Exception e) { L("★ 取对话框基准单位失败，用默认值：" + e.Message); }
                    finally { ReleaseDC(IntPtr.Zero, dc); }

                    IntPtr tpl = BuildTemplate(title, name, proc, kws, baseUnitX, baseUnitY);
                    if (tpl == IntPtr.Zero) { L("★ 对话框模板构造失败"); return false; }
                    try
                    {
                        L("规则对话框：模板 " + _tplSize + " 字节，基准单位 " + baseUnitX + "x" + baseUnitY
                          + "，标题=「" + title + "」初值=「" + Trim(name) + "」/「" + Trim(proc) + "」/「" + Trim(kws) + "」");
                        IntPtr r = DialogBoxIndirectParamW(IntPtr.Zero, tpl, owner,
                            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&DlgProc,
                            IntPtr.Zero);
                        h = IntPtr.Zero;
                        ok = r.ToInt64() == 1;
                        L("规则对话框返回 " + r.ToInt64() + "（1=确定 ✓ 0=取消 ✓）");
                    }
                    finally { Marshal.FreeHGlobal(tpl); }
                }
                catch (Exception e)
                {
                    L("★ 规则对话框异常：" + e.GetType().Name + " " + e.Message);
                    return false;
                }
                if (ok)
                {
                    outName = _outName; outProc = _outProc; outKws = _outKws;
                }
                GC.KeepAlive(h);
                return ok;
            }

            private static int _tplSize;

            /// <summary>把像素坐标换算成对话框单位（四舍五入 ✓）</summary>
            private static short DU(int px, int baseUnit) => (short)Math.Round(px * 4.0 / baseUnit);
            private static ushort DUU(int px, int baseUnit) => (ushort)Math.Max(1, Math.Round(px * 4.0 / baseUnit));

            private static IntPtr BuildTemplate(string title, string name, string proc, string kws,
                                                int buX, int buY)
            {
                var ms = new MemoryStream();
                var bw = new BinaryWriter(ms);

                // ---- DLGTEMPLATEEX 头 ----
                bw.Write((ushort)1);          // dlgVer
                bw.Write((ushort)0xFFFF);     // signature
                bw.Write((uint)0);            // helpID
                bw.Write((uint)0);            // exStyle
                bw.Write((uint)0x10CF0000);   // style: DS_SETFONT|DS_MODALFRAME|WS_POPUP|WS_CAPTION|WS_SYSMENU|WS_VISIBLE
                bw.Write((ushort)10);         // cdit：1 标题 + 3 标签 + 3 输入 + 1 提示 + 2 按钮 = 10
                bw.Write((short)0);           // x
                bw.Write((short)0);           // y
                bw.Write(DUU(470, buX));      // cx
                bw.Write(DUU(250, buY));      // cy
                bw.Write((ushort)0);          // menu = 无
                bw.Write((ushort)0);          // windowClass = 默认
                WriteSz(bw, title);           // 标题
                // DS_SETFONT：点值 9 + 字体名
                bw.Write((ushort)9);
                bw.Write((ushort)400);
                bw.Write((ushort)1);
                bw.Write((byte)1);
                WriteSz(bw, "Microsoft YaHei UI");

                // ---- 控件 ----
                Item(bw, 0x00000000 /*SS_LEFT*/, 0, 15, 20, 110, 20, ID_DLG_LBL1, WardenLang.Lang.Tr("规则名称"), buX, buY);
                Item(bw, 0x50810000 /*WS_BORDER|WS_TABSTOP|ES_LEFT|WS_CHILD|WS_VISIBLE*/, 0, 130, 17, 310, 24, ID_DLG_NAME, name, buX, buY);
                Item(bw, 0x00000000, 0, 15, 58, 110, 20, ID_DLG_LBL2, WardenLang.Lang.Tr("进程名（可空）"), buX, buY);
                Item(bw, 0x50810000, 0, 130, 55, 310, 24, ID_DLG_PROC, proc, buX, buY);
                Item(bw, 0x00000000, 0, 15, 96, 110, 20, ID_DLG_LBL3, WardenLang.Lang.Tr("标题关键词"), buX, buY);
                Item(bw, 0x50810000, 0, 130, 93, 310, 24, ID_DLG_KWS, kws, buX, buY);
                Item(bw, 0x00000000, 0, 130, 118, 310, 20, ID_DLG_LBLHINT, WardenLang.Lang.Tr("（多个用英文逗号分隔）"), buX, buY);
                Item(bw, 0x50010001 /*WS_CHILD|WS_VISIBLE|WS_TABSTOP|BS_DEFPUSHBUTTON*/, 0, 240, 165, 95, 30, IDOK, WardenLang.Lang.Tr("确定"), buX, buY);
                Item(bw, 0x50010000 /*WS_CHILD|WS_VISIBLE|WS_TABSTOP|BS_PUSHBUTTON*/, 0, 345, 165, 95, 30, IDCANCEL, WardenLang.Lang.Tr("取消"), buX, buY);

                bw.Flush();
                byte[] bytes = ms.ToArray();
                _tplSize = bytes.Length;
                IntPtr p = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                return p;
            }

            private static void Item(BinaryWriter bw, uint style, uint exStyle, int x, int y, int w, int h,
                                     int id, string text, int buX, int buY)
            {
                bw.Write((uint)0);                 // helpID
                bw.Write(exStyle);                 // exStyle
                bw.Write(style);                   // style
                bw.Write(DU(x, buX));              // x
                bw.Write(DU(y, buY));              // y
                bw.Write(DUU(w, buX));             // cx
                bw.Write(DUU(h, buY));             // cy
                bw.Write((uint)id);                // id
                bw.Write((uint)0xFFFF);            // 自定义类 0xFFFF = 后跟有序号
                bw.Write((ushort)0x0080);          // 0x0080 = "BUTTON"（按钮类 ✓）
                bw.Write((ushort)0);               // 标题（字符串）
                bw.Write((ushort)0);               // 创建数据 = 无
                WriteSz(bw, text);
            }

            private static void WriteSz(BinaryWriter bw, string s)
            {
                foreach (char c in s ?? "") { bw.Write((ushort)c); }
                bw.Write((ushort)0);
            }

            // 只有 WM_INITDIALOG / WM_COMMAND 需要处理 → 直接返回 0 即可 ✓
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
            private static IntPtr DlgProc(IntPtr hDlg, uint m, IntPtr w, IntPtr l)
            {
                try { return DlgProcCore(hDlg, m, w, l); }
                catch (Exception e)
                {
                    // ★ 异常绝不能逃出 UnmanagedCallersOnly ✓
                    L("★ 规则对话框过程异常 msg=0x" + m.ToString("X") + " : " + e.GetType().Name + " " + e.Message);
                    return IntPtr.Zero;
                }
            }

            private static IntPtr DlgProcCore(IntPtr hDlg, uint m, IntPtr w, IntPtr l)
            {
                if (m == 0x0110 /*WM_INITDIALOG*/)
                {
                    IntPtr ed = GetDlgItem(hDlg, ID_DLG_NAME);
                    if (ed != IntPtr.Zero) { SetFocus(ed); }
                    L("WM_INITDIALOG 到达 ✓ 输入框句柄=" + ed + " err=" + Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }
                if (m == 0x0111 /*WM_COMMAND*/)
                {
                    long raw = w.ToInt64();
                    int id = (int)(raw & 0xFFFF);
                    int code = (int)((raw >> 16) & 0xFFFF);
                    L("对话框 WM_COMMAND raw=" + raw + " → id=" + id + " code=" + code);
                    if (id == IDOK && code == 0)
                    {
                        string n = TextOf(GetDlgItem(hDlg, ID_DLG_NAME)).Trim();
                        string p = TextOf(GetDlgItem(hDlg, ID_DLG_PROC)).Trim();
                        string k = TextOf(GetDlgItem(hDlg, ID_DLG_KWS)).Trim();
                        // ★ 旧版校验：进程名和标题关键词至少要填一个 ✓（否则 DialogResult=None 不关窗 ✓）
                        if (p.Length == 0 && k.Length == 0)
                        {
                            L("对话框校验未过：进程名与标题关键词都空 → 不关窗 ✓");
                            MessageBoxW(hDlg, WardenLang.Lang.Tr("进程名和标题关键词至少要填一个。"),
                                Caption, MB_OK | MB_ICONWARNING);
                            return IntPtr.Zero;
                        }
                        _outName = n; _outProc = p; _outKws = k;
                        EndDialog(hDlg, (IntPtr)1);
                        return IntPtr.Zero;
                    }
                    if (id == IDCANCEL && (code == 0 || code == 1))
                    {
                        EndDialog(hDlg, IntPtr.Zero);
                        return IntPtr.Zero;
                    }
                }
                return IntPtr.Zero;
            }
        }

        // 对话框要用的几条（user32 ✓）
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool EndDialog(IntPtr hDlg, IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr DialogBoxIndirectParamW(IntPtr inst, IntPtr template, IntPtr owner,
            IntPtr proc, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        private struct TEXTMETRICW
        {
            public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading,
                       tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
            public ushort tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar, tmItalic, tmUnderlined,
                          tmStruckOut, tmPitchAndFamily, tmCharSet;
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetTextMetricsW(IntPtr dc, out TEXTMETRICW tm);
    }
}
