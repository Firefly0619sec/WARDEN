// ============================================================================
//  WARDEN 日志查看器 —— 原生 Win32 公共层（2026-10-11 · Beta 3 / 0.3.2）
//  文件：LogViewWin32.cs
//
//  【为什么重写】WinForms 不支持 Native AOT ✗（PublishAot 直接编译失败 ✓）
//    WinForms 自包含 49.25 MB ✗  →  本实现 AOT 约 3 MB ✓ 且无需装 .NET ✓
//  行为规格：诊断\测试报告\WARDEN-0.3.2-LogView规格与清单.md（逐条对齐 ✓）
//
//  【血泪坑，这里全部避开】
//    ① 窗口回调：UnmanagedCallersOnly + delegate* unmanaged[Stdcall] ✓
//       ✗ 绝对不用 Marshal.GetFunctionPointerForDelegate（AOT 下 fail-fast 0xC0000409）
//    ② 客户区尺寸用 AdjustWindowRectEx 换算 ✓（否则控件被标题栏/边框裁掉 ✓）
//    ③ 建窗【之前】就定好 DPI 缩放 ✓（建窗后再算 → 窗口放大而控件不放大 ✓）
//    ④ P/Invoke 归属：DrawTextW/GetWindowTextW/GetDlgItem→user32 ✓
//       GetStockObject/CreateFontW/SelectObject/CreateSolidBrush→gdi32 ✓
//       GetSaveFileNameW→comdlg32 ✓ 写错就是 EntryPointNotFoundException ✓
//    ⑤ 结构体一律 blittable ✗ 不放 string/bool/数组 ✓ 字符串 IntPtr + StringToHGlobalUni ✓
//    ⑥ 异常绝不逃出 UnmanagedCallersOnly 回调 ✓ 兜住并写日志 ✓
//    ⑦ 控件 ID 放 CreateWindowExW 的 hMenu ✓（放错全是 ID 0 ✗ 点不动 ✓）
//    ⑧ IsDialogMessageW 只认对话框类 ✗ → 靠 WM_GETDLGCODE 让它认账（Tab/Esc ✓）
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WardenLogView
{
    /// <summary>本工具用到的原生 Win32 接口层（窗口过程 / 消息框 / 文件对话框 / 剪贴板 / 版本号 / 日志 ✓）</summary>
    internal static unsafe class LogViewWin32
    {
        // ---------------- 日志（排查全靠它 ✓） ----------------
        internal const string LogFile = "logview-win32.log";

        internal static void L(string s)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), LogFile),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine);
            }
            catch { }
        }

        internal static int MulDiv(int a, int b, int c)
        {
            try { return (int)((long)a * b / c); } catch { return a; }
        }

        // ---------------- 常量 ----------------
        internal const uint MB_OK = 0, MB_YESNO = 4, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40,
                            MB_ICONERROR = 0x10;
        internal const int IDYES = 6, IDOK = 1, IDCANCEL = 2;
        internal const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000;

        internal const int COLOR_WINDOW = 5, TRANSPARENT = 1, DEFAULT_GUI_FONT = 17;
        internal const int ICC_STANDARD_CLASSES = 0x4000;

        internal const uint CLR_BLACK = 0x00000000, CLR_WHITE = 0x00FFFFFF, CLR_DIMGRAY = 0x00696969,
                            CLR_YELLOW_BG = 0x00C0FFFF;   // GDI 的 COLORREF 是 BGR ✓

        internal const uint WM_SETFONT = 0x0030, WM_CTLCOLORSTATIC = 0x0138, WM_CTLCOLOREDIT = 0x0133;
        internal const uint BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1;
        internal const uint CB_ADDSTRING = 0x0143, CB_SETCURSEL = 0x014E, CB_GETCURSEL = 0x0147;
        internal const int CBN_SELCHANGE = 1, EN_CHANGE = 0x0300, BN_CLICKED = 0;

        // ---------------- P/Invoke 声明（归属逐个核对过 ✓） ----------------
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int L, T, R, B; }
        // ★ 原生 PAINTSTRUCT 的 rgbReserved 是 32 字节 ✗ 少声明会越界踩坏栈 → fail-fast ✓
        [StructLayout(LayoutKind.Sequential)]
        internal struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore, fIncUpdate;
            public byte r0, r1, r2, r3, r4, r5, r6, r7;
            public byte r8, r9, r10, r11, r12, r13, r14, r15;
            public byte r16, r17, r18, r19, r20, r21, r22, r23;
            public byte r24, r25, r26, r27, r28, r29, r30, r31;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct INITCOMMONCONTROLSEX { public uint dwSize, dwICC; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MINMAXINFO { public int x0, y0, x1, y1, maxTrackX, maxTrackY, minTrackX, minTrackY, maxPosX, maxPosY, maxSizeX, maxSizeY; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEXW
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public IntPtr lpszMenuName, lpszClassName;   // ★ 原生 LPCWSTR（AOT 不能放 string ✗）
            public IntPtr hIconSm;
        }
        // ★ 全字段都是指针/整数 → blittable ✓（字符串统一 IntPtr ✓）
        [StructLayout(LayoutKind.Sequential)]
        internal struct OPENFILENAMEW
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowExW(uint ex, string cls, string? name, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] internal static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern bool GetMessageW(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] internal static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] internal static extern bool IsDialogMessageW(IntPtr h, ref MSG m);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int c);
        [DllImport("user32.dll")] internal static extern bool UpdateWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool EnableWindow(IntPtr h, bool e);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int c);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll")] internal static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] internal static extern bool AdjustWindowRectEx(ref RECT r, uint style, int menu, uint exStyle);
        [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] internal static extern bool KillTimer(IntPtr h, IntPtr id);
        [DllImport("user32.dll")] internal static extern IntPtr GetDlgItem(IntPtr h, int id);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLongW32(IntPtr h, int index);
        internal static int GetWindowLongValue(IntPtr h, int index)
        {
            try { return GetWindowLongW32(h, index); } catch { return 0; }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLengthW(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool SetWindowTextW(IntPtr h, string s);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int DrawTextW(IntPtr dc, string s, int n, ref RECT r, uint f);   // ★ user32 ✓
        [DllImport("user32.dll")] internal static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        [DllImport("user32.dll")] internal static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] internal static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
        // 剪贴板（取代 WinForms Clipboard ✗）
        [DllImport("user32.dll")] internal static extern bool OpenClipboard(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
        [DllImport("user32.dll")] internal static extern IntPtr SetClipboardData(uint fmt, IntPtr mem);
        [DllImport("user32.dll")] internal static extern bool CloseClipboard();
        // ★ gdi32 归属 ✓
        [DllImport("gdi32.dll")] internal static extern IntPtr GetStockObject(int i);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateSolidBrush(int c);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] internal static extern int SetTextColor(IntPtr dc, int c);
        [DllImport("gdi32.dll")] internal static extern int SetBkMode(IntPtr dc, int m);
        [DllImport("gdi32.dll")] internal static extern int SetBkColor(IntPtr dc, int c);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic,
            uint under, uint strike, uint charset, uint prec, uint clip, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        // ★ comdlg32 归属 ✓
        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OPENFILENAMEW ofn);
        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandleW(string? n);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr h);
        [DllImport("version.dll", CharSet = CharSet.Unicode)]
        private static extern int GetFileVersionInfoSizeW(string file, out uint handle);
        [DllImport("version.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetFileVersionInfoW(string file, uint handle, uint len, IntPtr data);
        [DllImport("version.dll", CharSet = CharSet.Unicode)]
        private static extern bool VerQueryValueW(IntPtr block, string sub, out IntPtr buf, out uint len);
        [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

        // ====================================================================
        //  唯一的窗口过程（★ 只注册这一个回调 ✓ 免得再踩 fail-fast ✓）
        //  主窗口 → LogViewForm.WndProc；上报窗 → EvidenceForm.WndProc
        // ====================================================================
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        internal static IntPtr AotWndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            try
            {
                if (EvidenceForm.Owns(h)) { return EvidenceForm.WndProc(h, m, w, l); }
                return LogViewForm.WndProc(h, m, w, l);
            }
            catch (Exception ex)
            {
                // ★ 异常绝不能逃出 UnmanagedCallersOnly ✓
                L("★ AotWndProc 顶层兜住异常 msg=0x" + m.ToString("X") + " : " + ex.GetType().Name + " " + ex.Message);
                try { return DefWindowProcW(h, m, w, l); } catch { return IntPtr.Zero; }
            }
        }

        /// <summary>注册窗口类（★ 回调只用 AotWndProc ✓）</summary>
        internal static bool RegisterClass(string name, IntPtr clsNamePtr, IntPtr inst)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                // ★ AOT 官方方式 ✓ 绝不用 Marshal.GetFunctionPointerForDelegate ✗
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&AotWndProc,
                hInstance = inst,
                hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
                lpszClassName = clsNamePtr,
            };
            ushort atom = RegisterClassExW(ref wc);
            int err = Marshal.GetLastWin32Error();
            L("RegisterClassExW(" + name + ") atom=" + atom + " err=" + err);
            return atom != 0 || err == 1410;   // 1410 = 类已存在 ✓
        }

        // ====================================================================
        //  消息框 / 文件对话框 / 剪贴板 / 版本号
        // ====================================================================
        internal static void Msg(string text, string caption, bool information)
            => Msg(IntPtr.Zero, text, caption, information);

        internal static void Msg(IntPtr owner, string text, string caption, bool information)
        {
            try
            {
                MessageBoxW(owner, text, caption, (uint)(MB_OK | (information ? MB_ICONINFORMATION : MB_ICONWARNING)));
                L("MessageBox(OK/" + (information ? "info" : "warn") + ") 「" + caption + "」 " + Head(text));
            }
            catch (Exception ex) { L("MessageBox 抛异常：" + ex.Message); }
        }

        internal static void Error(IntPtr owner, string text, string caption)
        {
            try { MessageBoxW(owner, text, caption, (uint)(MB_OK | MB_ICONERROR)); L("MessageBox(ERROR) " + Head(text)); }
            catch { }
        }

        /// <summary>确认框：★ YesNo 必须拿到返回值 ✗ 不能只弹提示 ✓</summary>
        internal static bool Confirm(IntPtr owner, string text, string caption)
        {
            try
            {
                int r = MessageBoxW(owner, text, caption, (uint)(MB_YESNO | MB_ICONWARNING));
                L("MessageBox(YesNo) " + Head(text) + " → " + (r == IDYES ? "Yes" : "No"));
                return r == IDYES;
            }
            catch (Exception ex) { L("Confirm 抛异常：" + ex.Message); return false; }
        }

        private static string Head(string s)
        {
            int i = s.IndexOf('\n');
            string t = (i < 0 ? s : s.Substring(0, i)).Trim();
            return t.Length > 60 ? t.Substring(0, 60) + "…" : t;
        }

        /// <summary>原生保存对话框（comdlg32 ✓）；用户取消返回 null ✓</summary>
        internal static string? SaveFile(IntPtr owner, string title, string filter, string defExt, string fileName)
            => FileDialog(owner, title, filter, defExt, fileName, true, "");

        /// <summary>原生保存对话框 + 指定初始目录 ✓（导出报告默认落在桌面 ✓）</summary>
        internal static string? SaveFile(IntPtr owner, string title, string filter, string defExt, string fileName, string initialDir)
            => FileDialog(owner, title, filter, defExt, fileName, true, initialDir);

        /// <summary>原生打开对话框（comdlg32 ✓）；用户取消返回 null ✓</summary>
        internal static string? OpenFile(IntPtr owner, string title, string filter, string defExt)
            => FileDialog(owner, title, filter, defExt, "", false, "");

        private static string? FileDialog(IntPtr owner, string title, string filter, string defExt,
                                          string fileName, bool save, string initialDir)
        {
            const int OFN_READONLY = 0x00000001, OFN_OVERWRITEPROMPT = 0x00000002, OFN_PATHMUSTEXIST = 0x00000800,
                      OFN_FILEMUSTEXIST = 0x00001000, OFN_NOCHANGEDIR = 0x00000008, OFN_EXPLORER = 0x00080000;
            IntPtr pFilter = IntPtr.Zero, pTitle = IntPtr.Zero, pDefExt = IntPtr.Zero, pFile = IntPtr.Zero, pDir = IntPtr.Zero;
            try
            {
                pFilter = Marshal.StringToHGlobalUni(filter);   // "描述\0*.md\0" 形式 ✓
                pTitle = Marshal.StringToHGlobalUni(title);
                pDefExt = Marshal.StringToHGlobalUni(defExt);
                if (!string.IsNullOrEmpty(initialDir)) { pDir = Marshal.StringToHGlobalUni(initialDir); }
                const int maxFile = 4096;
                pFile = Marshal.AllocHGlobal(maxFile * 2);
                for (int i = 0; i < maxFile; i++) { Marshal.WriteInt16(pFile, i * 2, 0); }
                if (!string.IsNullOrEmpty(fileName))
                {
                    char[] cs = fileName.ToCharArray();
                    for (int i = 0; i < cs.Length && i < maxFile - 1; i++) { Marshal.WriteInt16(pFile, i * 2, (short)cs[i]); }
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
                    lpstrInitialDir = pDir,
                    Flags = (uint)(OFN_EXPLORER | OFN_NOCHANGEDIR |
                            (save ? OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST
                                  : OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_READONLY)),
                };
                bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
                string got = ok ? (Marshal.PtrToStringUni(pFile) ?? "") : "";
                L("文件对话框(" + (save ? "保存" : "打开") + ") owner=0x" + owner.ToInt64().ToString("X") +
                  " → " + (ok ? got : "用户取消"));
                return ok ? got : null;
            }
            catch (Exception ex)
            {
                L("文件对话框抛异常：" + ex.Message);
                return null;   // ★ 绝不让异常冒到调用方（宁可当作"用户取消"✓）
            }
            finally
            {
                if (pFilter != IntPtr.Zero) { Marshal.FreeHGlobal(pFilter); }
                if (pTitle != IntPtr.Zero) { Marshal.FreeHGlobal(pTitle); }
                if (pDefExt != IntPtr.Zero) { Marshal.FreeHGlobal(pDefExt); }
                if (pFile != IntPtr.Zero) { Marshal.FreeHGlobal(pFile); }
                if (pDir != IntPtr.Zero) { Marshal.FreeHGlobal(pDir); }
            }
        }

        /// <summary>原生剪贴板（取代 WinForms Clipboard ✗）</summary>
        internal static bool SetClipboardText(IntPtr owner, string text)
        {
            const uint GMEM_MOVEABLE = 0x0002, CF_UNICODETEXT = 13;
            IntPtr hMem = IntPtr.Zero;
            try
            {
                int bytes = (text.Length + 1) * 2;
                hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hMem == IntPtr.Zero) { L("剪贴板：GlobalAlloc 失败"); return false; }
                IntPtr p = GlobalLock(hMem);
                if (p == IntPtr.Zero) { GlobalFree(hMem); L("剪贴板：GlobalLock 失败"); return false; }
                char[] cs = text.ToCharArray();
                for (int i = 0; i < cs.Length; i++) { Marshal.WriteInt16(p, i * 2, (short)cs[i]); }
                Marshal.WriteInt16(p, cs.Length * 2, 0);
                GlobalUnlock(hMem);
                if (!OpenClipboard(owner)) { GlobalFree(hMem); L("剪贴板：OpenClipboard 失败"); return false; }
                try
                {
                    EmptyClipboard();
                    if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) { L("剪贴板：SetClipboardData 失败"); return false; }
                    hMem = IntPtr.Zero;   // ★ 所有权已交给系统 ✓ 不能再 free ✓
                    L("剪贴板：已写入 " + text.Length + " 字符 ✓");
                    return true;
                }
                finally { CloseClipboard(); }
            }
            catch (Exception ex) { L("剪贴板抛异常：" + ex.Message); return false; }
            finally { if (hMem != IntPtr.Zero) { GlobalFree(hMem); } }
        }

        /// <summary>取自身的 ProductVersion（取代 WinForms Application.ProductVersion ✗ 文案不变 ✓）</summary>
        internal static string ProductVersion()
        {
            try
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) { return ""; }
                uint h;
                int size = GetFileVersionInfoSizeW(exe, out h);
                if (size <= 0) { return ""; }
                IntPtr block = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetFileVersionInfoW(exe, 0, (uint)size, block)) { return ""; }
                    IntPtr buf; uint len;
                    if (!VerQueryValueW(block, @"\StringFileInfo\040904B0\ProductVersion", out buf, out len) || buf == IntPtr.Zero)
                    {
                        if (!VerQueryValueW(block, @"\StringFileInfo\040904B0\FileVersion", out buf, out len) || buf == IntPtr.Zero) { return ""; }
                    }
                    string v = Marshal.PtrToStringUni(buf) ?? "";
                    int plus = v.IndexOf('+');               // ★ 与旧版一致：去掉 "+哈希" ✓
                    return plus > 0 ? v.Substring(0, plus) : v;
                }
                finally { Marshal.FreeHGlobal(block); }
            }
            catch (Exception ex) { L("读版本号失败：" + ex.Message); return ""; }
        }

        // ====================================================================
        //  公共小工具
        // ====================================================================
        internal static void InitVisualStyles()
        {
            try
            {
                var icc = new INITCOMMONCONTROLSEX
                {
                    dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
                    dwICC = ICC_STANDARD_CLASSES,
                };
                bool ok = InitCommonControlsEx(ref icc);
                L("InitCommonControlsEx=" + ok + " err=" + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { L("InitCommonControlsEx 失败（不影响功能）：" + ex.Message); }
        }

        internal struct Fonts
        {
            public IntPtr Ui, Mono, MonoSm, Fallback;
            public void Dispose()
            {
                foreach (IntPtr x in new[] { Ui, Mono, MonoSm })
                {
                    if (x != IntPtr.Zero && x != Fallback) { try { DeleteObject(x); } catch { } }
                }
            }
        }

        internal static Fonts CreateFonts(uint dpi)
        {
            var f = new Fonts();
            f.Fallback = GetStockObject(DEFAULT_GUI_FONT);
            f.Ui = CreateFontW(-MulDiv(9, (int)dpi, 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Microsoft YaHei UI");
            f.Mono = CreateFontW(-MulDiv(9, (int)dpi, 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Consolas");
            f.MonoSm = CreateFontW(-MulDiv(17, (int)dpi, 144), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Consolas");
            if (f.Ui == IntPtr.Zero) { f.Ui = f.Fallback; }
            if (f.Mono == IntPtr.Zero) { f.Mono = f.Ui; }
            if (f.MonoSm == IntPtr.Zero) { f.MonoSm = f.Ui; }
            return f;
        }

        internal static string GetText(IntPtr h)
        {
            if (h == IntPtr.Zero) { return ""; }
            try
            {
                int n = GetWindowTextLengthW(h);
                var sb = new StringBuilder(n + 2);
                GetWindowTextW(h, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        internal static void SetText(IntPtr h, string s)
        {
            try { if (h != IntPtr.Zero) { SetWindowTextW(h, s); } }
            catch (Exception ex) { L("SetWindowTextW 失败：" + ex.Message); }
        }

        internal static bool Checked(IntPtr h)
            => h != IntPtr.Zero && SendMessageW(h, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt64() == 1;

        internal static void Check(IntPtr h, bool on)
        {
            if (h != IntPtr.Zero) { SendMessageW(h, BM_SETCHECK, (IntPtr)(on ? 1 : 0), IntPtr.Zero); }
        }

        internal static void SetFont(IntPtr c, IntPtr font)
        {
            if (c != IntPtr.Zero && font != IntPtr.Zero) { SendMessageW(c, WM_SETFONT, font, (IntPtr)1); }
        }

        /// <summary>客户区 → 窗口尺寸（★ 不换算控件就被标题栏裁掉 ✓）</summary>
        internal static void ClientToWindow(int cw, int ch, uint style, out int wWin, out int hWin)
        {
            RECT want = new() { L = 0, T = 0, R = cw, B = ch };
            AdjustWindowRectEx(ref want, style, 0, 0);
            wWin = want.R - want.L;
            hWin = want.B - want.T;
        }

        internal static void CenterOnScreen(IntPtr h, int wWin, int hWin)
        {
            SetWindowPos(h, IntPtr.Zero, (GetSystemMetrics(0) - wWin) / 2, (GetSystemMetrics(1) - hWin) / 2,
                wWin, hWin, 0x0004 /*SWP_NOZORDER*/);
        }

        internal static void CenterOnWindow(IntPtr h, IntPtr owner, int wWin, int hWin)
        {
            int x = (GetSystemMetrics(0) - wWin) / 2, y = (GetSystemMetrics(1) - hWin) / 2;
            if (owner != IntPtr.Zero)
            {
                RECT or;
                if (GetWindowRect(owner, out or))
                {
                    x = or.L + ((or.R - or.L) - wWin) / 2;
                    y = or.T + ((or.B - or.T) - hWin) / 2;
                }
            }
            SetWindowPos(h, IntPtr.Zero, x, y, wWin, hWin, 0x0004);
        }

        /// <summary>只用 GetDpiForSystem（建窗前 ✓）定缩放；WARDEN_TEST_DPI 是验收钩子 ✓</summary>
        internal static void ResolveScale(out double scale, out uint dpi)
        {
            dpi = GetDpiForSystem();
            scale = dpi / 96.0;
            try
            {
                string? forced = Environment.GetEnvironmentVariable("WARDEN_TEST_DPI");
                if (!string.IsNullOrEmpty(forced) && uint.TryParse(forced, out uint fd) && fd >= 96)
                {
                    dpi = fd;
                    scale = fd / 96.0;
                    L("★ 测试钩子生效：按 DPI " + fd + " 渲染（缩放 " + scale.ToString("0.00") + "x）");
                }
                else { L("系统 DPI=" + dpi + " 缩放=" + scale.ToString("0.00") + "x"); }
            }
            catch { }
        }
    }
}
