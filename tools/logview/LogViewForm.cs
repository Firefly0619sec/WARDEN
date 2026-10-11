// ============================================================================
//  WARDEN 日志查看器 —— 主窗口的原生实现（2026-10-11 · Beta 3 / 0.3.2）
//  文件：LogViewForm.cs
//  规格：诊断\测试报告\WARDEN-0.3.2-LogView规格与清单.md 第二/三节逐条对齐 ✓
//
//  控件与坐标（客户区坐标，900×600 基准 ✓ 缩放后按比例）
//    _files   COMBOBOX (12,12) 200×26  CBS_DROPDOWNLIST 四项 默认第 0 项
//    标签     STATIC   (224,16) 50×22  文案「过滤：」
//    _filter  EDIT     (270,14) 260×24  输入变化 → Reload（实时过滤 ✓）
//    _onlyKey BUTTON   (540,16) 320×24  BS_AUTOCHECKBOX 勾选变化 → Reload
//    _view    EDIT     (12,48) 876×480  多行/只读/双向滚动/不换行/Consolas 9pt/白底
//    _stats   STATIC   (12,534) 560×22  深灰前景
//    刷新     BUTTON   (586,532) 70×28
//    打开文件夹 BUTTON (662,532) 100×28
//    导出报告 BUTTON   (768,532) 100×28
//    上报证据 BUTTON   (768,496) 100×28
//    定时器   SetTimer 3000ms，仅当窗口可见时 Reload ✓
// ============================================================================

using System;
using System.IO;
using System.Text;

namespace WardenLogView
{
    internal static class LogViewForm
    {
        // 控件 ID（★ 全部放在 CreateWindowExW 的 hMenu 参数里 ✓ 放错就全是 ID 0 ✗）
        private const int ID_FILES = 1001, ID_LABEL1 = 1002, ID_FILTER = 1003, ID_ONLYKEY = 1004,
                          ID_VIEW = 1005, ID_STATS = 1006,
                          ID_REFRESH = 1010, ID_FOLDER = 1011, ID_REPORT = 1012, ID_EVIDENCE = 1013;
        private const int ID_TIMER = 1;
        private const uint WM_APP_RELOAD = 0x8000 + 1;   // 自定义：首次 Reload ✓

        private const string Cls = "WardenLogViewWnd";
        internal const string Title = "WARDEN 日志查看器";
        private const int CW = 900, CH = 600;            // ★ 规格：客户区 900×600 ✓

        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000,
                           WS_BORDER = 0x00800000, WS_VSCROLL = 0x00200000, WS_HSCROLL = 0x00100000;
        private const uint BS_PUSHBUTTON = 0, BS_AUTOCHECKBOX = 3;
        private const uint ES_LEFT = 0, ES_MULTILINE = 4, ES_AUTOVSCROLL = 0x40, ES_AUTOHSCROLL = 0x80,
                           ES_READONLY = 0x800, ES_NOHIDESEL = 0x100;
        // ★ 血的教训：CBS_DROPDOWNLIST = 3 ✓ 我一开始写成 2 = CBS_SIMPLE ✗
        //   症状：下拉列表根本不弹（listbox 贴在控件里、只有 2 像素高、点不着）✗
        //   对照实测：style 值写错不会报错，只会"看起来像坏了" ✓
        private const uint CBS_DROPDOWNLIST = 3, CBS_HASSTRINGS = 0x200;

        private const uint WM_CREATE = 0x0001, WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_CLOSE = 0x0010,
                           WM_QUERYENDSESSION = 0x0011, WM_COMMAND = 0x0111, WM_TIMER = 0x0113,
                           WM_CTLCOLORSTATIC = 0x0138, WM_CTLCOLOREDIT = 0x0133, WM_GETDLGCODE = 0x0087;
        private const uint DLGC_WANTARROWS = 0x0001, DLGC_WANTTAB = 0x0002;
        private const uint EM_SETSEL = 0x00B1, EM_GETSEL = 0x00B0, EM_LINESCROLL = 0x00B6,
                           EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const uint CB_ADDSTRING = 0x0143, CB_SETCURSEL = 0x014E, CB_GETCURSEL = 0x0147,
                           CB_GETCOUNT = 0x0146;
        private const int CBN_SELCHANGE = 1, EN_CHANGE = 0x0300, BN_CLICKED = 0;
        private const int COLOR_WINDOW = 5, TRANSPARENT = 1;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        private static readonly string[] FileNames = { "WARDEN.log", "EMERGENCY.log", "TAMPER.log", "setup.log" };

        private static IntPtr _hwnd, _files, _label1, _filter, _onlyKey, _view, _stats,
                               _bRefresh, _bFolder, _bReport, _bEvidence;
        private static IntPtr _clsName, _brEdit;
        private static LogViewWin32.Fonts _fonts;
        private static double _scale = 1.0;
        private static uint _dpi = 96;
        private static bool _quit;

        private static int S(int px) => (int)Math.Round(px * _scale);

        // ====================================================================
        //  入口
        // ====================================================================
        internal static void Run()
        {
            LogViewWin32.L("================ LogView 0.3.2（原生 AOT）启动 ================");
            LogViewWin32.InitVisualStyles();
            try { LogViewWin32.SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { }   // PER_MONITOR_AWARE_V2 ✓

            // ★ 建窗【之前】就把缩放定好 ✓（子控件在 WM_CREATE 里就要用它 ✓）
            LogViewWin32.ResolveScale(out _scale, out _dpi);
            LogViewWin32.L("数据目录 = " + LogView.DataDir);
            LogViewWin32.L("下拉框四项 = " + string.Join(" / ", FileNames));

            IntPtr inst = LogViewWin32.GetModuleHandleW(null);
            _clsName = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(Cls);
            if (!LogViewWin32.RegisterClass(Cls, _clsName, inst)) { return; }
            EvidenceForm.RegisterClass(inst);   // 上报证据窗的类（同一个 AotWndProc ✓）

            // ★ 客户区 900×600 → 窗口尺寸（不然控件被标题栏+边框裁掉 ✓）
            LogViewWin32.ClientToWindow(S(CW), S(CH), WS_OVERLAPPEDWINDOW, out int wWin, out int hWin);
            LogViewWin32.L("客户区 " + CW + "x" + CH + " → 窗口 " + wWin + "x" + hWin + "（缩放 " + _scale.ToString("0.00") + "x）");

            _hwnd = LogViewWin32.CreateWindowExW(0, Cls, Title, WS_OVERLAPPEDWINDOW, 0, 0, wWin, hWin,
                IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                LogViewWin32.Error(IntPtr.Zero, "创建窗口失败（错误码 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "）", Title);
                return;
            }
            LogViewWin32.L("CreateWindowExW hwnd=0x" + _hwnd.ToInt64().ToString("X") +
                           "（窗口 DPI=" + LogViewWin32.GetDpiForWindow(_hwnd) + "，本次缩放 " + _scale.ToString("0.00") + "x ✓）");
            LogViewWin32.CenterOnScreen(_hwnd, wWin, hWin);   // 屏幕居中 ✓
            LogViewWin32.GetWindowRect(_hwnd, out LogViewWin32.RECT cr);
            LogViewWin32.GetClientRect(_hwnd, out LogViewWin32.RECT cc);
            LogViewWin32.L("屏幕居中后：窗口=(" + cr.L + "," + cr.T + ") " + (cr.R - cr.L) + "x" + (cr.B - cr.T) +
                           "，客户区 " + (cc.R - cc.L) + "x" + (cc.B - cc.T));

            LogViewWin32.ShowWindow(_hwnd, 5 /*SW_SHOW*/);
            LogViewWin32.UpdateWindow(_hwnd);
            LogViewWin32.SetFocus(_hwnd);

            LogViewWin32.MSG msg;
            while (LogViewWin32.GetMessageW(out msg, IntPtr.Zero, 0, 0))
            {
                bool handled = false;
                // ★ IsDialogMessageW 让 Tab/Enter/Esc 生效 ✓（靠 WM_GETDLGCODE 认账 ✓）
                try { handled = LogViewWin32.IsDialogMessageW(msg.hwnd, ref msg); } catch { }
                if (!handled)
                {
                    LogViewWin32.TranslateMessage(ref msg);
                    LogViewWin32.DispatchMessageW(ref msg);
                }
            }
            LogViewWin32.L("消息循环结束（_quit=" + _quit + "）");
            _fonts.Dispose();
            if (_brEdit != IntPtr.Zero) { LogViewWin32.DeleteObject(_brEdit); }
            if (_clsName != IntPtr.Zero) { System.Runtime.InteropServices.Marshal.FreeHGlobal(_clsName); }
        }

        // ====================================================================
        //  窗口过程（★ 由 LogViewWin32.AotWndProc 转发进来 ✓）
        // ====================================================================
        internal static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            try
            {
                switch (m)
                {
                    case WM_CREATE: OnCreate(h); return IntPtr.Zero;
                    case WM_SIZE: Layout(h); return IntPtr.Zero;
                    case WM_COMMAND: OnCommand(h, w); return IntPtr.Zero;
                    case WM_TIMER:
                        if (w.ToInt64() == ID_TIMER) { OnTimer(); return IntPtr.Zero; }
                        break;
                    case WM_APP_RELOAD: Reload(); return IntPtr.Zero;
                    case WM_CTLCOLORSTATIC:
                    case WM_CTLCOLOREDIT:
                        {
                            IntPtr br = OnCtlColor(w, l);
                            if (br != IntPtr.Zero) { return br; }
                            break;
                        }
                    case WM_GETDLGCODE:
                        return (IntPtr)DLGC_WANTARROWS;
                    case WM_CLOSE:
                        LogViewWin32.L("收到 WM_CLOSE ✓");
                        // ★ 上报窗开着时主窗被禁用 ✗ 此时不许被 Esc 顺手关掉 ✓
                        if (LogViewWin32.IsWindowEnabled(h)) { LogViewWin32.DestroyWindow(h); }
                        return IntPtr.Zero;
                    case WM_QUERYENDSESSION: return (IntPtr)1;
                    case WM_DESTROY:
                        LogViewWin32.KillTimer(h, (IntPtr)ID_TIMER);
                        LogViewWin32.L("收到 WM_DESTROY ✓ 结束消息循环");
                        _quit = true;
                        LogViewWin32.PostQuitMessage(0);
                        return IntPtr.Zero;
                }
                return LogViewWin32.DefWindowProcW(h, m, w, l);
            }
            catch (Exception ex)
            {
                LogViewWin32.L("★ 主窗 WndProc 抛异常 msg=0x" + m.ToString("X") + " : " + ex.GetType().Name + " " + ex.Message);
                return LogViewWin32.DefWindowProcW(h, m, w, l);
            }
        }

        private static void OnCreate(IntPtr h)
        {
            _fonts = LogViewWin32.CreateFonts(_dpi);
            _brEdit = LogViewWin32.CreateSolidBrush(unchecked((int)LogViewWin32.CLR_WHITE));

            // ★ 控件 ID 全部放在 hMenu 参数里 ✓
            _files = LogViewWin32.CreateWindowExW(0, "COMBOBOX", null,
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST | CBS_HASSTRINGS,
                0, 0, 100, 200, h, (IntPtr)ID_FILES, IntPtr.Zero, IntPtr.Zero);
            _label1 = LogViewWin32.CreateWindowExW(0, "STATIC", "过滤：", WS_CHILD | WS_VISIBLE,
                0, 0, 50, 22, h, (IntPtr)ID_LABEL1, IntPtr.Zero, IntPtr.Zero);
            _filter = LogViewWin32.CreateWindowExW(0, "EDIT", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_BORDER | ES_LEFT | ES_AUTOHSCROLL,
                0, 0, 260, 24, h, (IntPtr)ID_FILTER, IntPtr.Zero, IntPtr.Zero);
            _onlyKey = LogViewWin32.CreateWindowExW(0, "BUTTON", "只看关键行（篡改/应急/冻结/强杀/失败）",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                0, 0, 320, 24, h, (IntPtr)ID_ONLYKEY, IntPtr.Zero, IntPtr.Zero);
            _view = LogViewWin32.CreateWindowExW(0, "EDIT", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | WS_HSCROLL | ES_LEFT | ES_MULTILINE |
                ES_AUTOVSCROLL | ES_AUTOHSCROLL | ES_READONLY | ES_NOHIDESEL,
                0, 0, 876, 480, h, (IntPtr)ID_VIEW, IntPtr.Zero, IntPtr.Zero);
            _stats = LogViewWin32.CreateWindowExW(0, "STATIC", "", WS_CHILD | WS_VISIBLE,
                0, 0, 560, 22, h, (IntPtr)ID_STATS, IntPtr.Zero, IntPtr.Zero);
            _bRefresh = LogViewWin32.CreateWindowExW(0, "BUTTON", "刷新",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON, 0, 0, 70, 28, h, (IntPtr)ID_REFRESH, IntPtr.Zero, IntPtr.Zero);
            _bFolder = LogViewWin32.CreateWindowExW(0, "BUTTON", "打开文件夹",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON, 0, 0, 100, 28, h, (IntPtr)ID_FOLDER, IntPtr.Zero, IntPtr.Zero);
            _bReport = LogViewWin32.CreateWindowExW(0, "BUTTON", "导出报告",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON, 0, 0, 100, 28, h, (IntPtr)ID_REPORT, IntPtr.Zero, IntPtr.Zero);
            _bEvidence = LogViewWin32.CreateWindowExW(0, "BUTTON", "上报证据",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON, 0, 0, 100, 28, h, (IntPtr)ID_EVIDENCE, IntPtr.Zero, IntPtr.Zero);
            LogViewWin32.L("控件创建: files=0x" + _files.ToInt64().ToString("X") + " filter=0x" + _filter.ToInt64().ToString("X") +
                           " onlyKey=0x" + _onlyKey.ToInt64().ToString("X") + " view=0x" + _view.ToInt64().ToString("X") +
                           " stats=0x" + _stats.ToInt64().ToString("X") + " 四个按钮=" +
                           (_bRefresh != IntPtr.Zero && _bFolder != IntPtr.Zero && _bReport != IntPtr.Zero && _bEvidence != IntPtr.Zero) +
                           " err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());

            foreach (IntPtr c in new[] { _files, _label1, _filter, _onlyKey, _view, _stats, _bRefresh, _bFolder, _bReport, _bEvidence })
            {
                LogViewWin32.SetFont(c, c == _view ? _fonts.Mono : _fonts.Ui);
            }

            // ★ 下拉框四项 ✓ 默认第 0 项 ✓
            IntPtr pName = IntPtr.Zero;
            try
            {
                foreach (string n in FileNames)
                {
                    pName = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(n);
                    LogViewWin32.SendMessageW(_files, CB_ADDSTRING, IntPtr.Zero, pName);
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(pName);
                    pName = IntPtr.Zero;
                }
            }
            finally { if (pName != IntPtr.Zero) { System.Runtime.InteropServices.Marshal.FreeHGlobal(pName); } }
            LogViewWin32.SendMessageW(_files, CB_SETCURSEL, IntPtr.Zero, IntPtr.Zero);
            LogViewWin32.L("下拉框项数=" + LogViewWin32.SendMessageW(_files, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt64() +
                           " 当前选中=" + LogViewWin32.SendMessageW(_files, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64() + "（应为第 0 项 ✓）");

            Layout(h);
            LogViewWin32.SetTimer(h, (IntPtr)ID_TIMER, 3000, IntPtr.Zero);   // ★ 3 秒自动刷新 ✓
            LogViewWin32.L("SetTimer 3000ms 已设置 ✓");
            LogViewWin32.PostMessageW(h, WM_APP_RELOAD, IntPtr.Zero, IntPtr.Zero);   // 首次 Reload ✓
        }

        /// <summary>★ WM_SIZE：按 900×600 基准等比例重排（大日志框跟着拉伸 ✓）</summary>
        private static void Layout(IntPtr h)
        {
            if (!LogViewWin32.GetClientRect(h, out LogViewWin32.RECT cc)) { return; }
            int cw = cc.R - cc.L, chh = cc.B - cc.T;
            int w = (int)Math.Round(cw * 900.0 / S(CW));
            int hh = (int)Math.Round(chh * 600.0 / S(CH));
            if (w < 1) { w = 1; }
            if (hh < 1) { hh = 1; }

            void P(IntPtr c, int x, int y, int cx, int cy)
            {
                if (c == IntPtr.Zero) { return; }
                LogViewWin32.SetWindowPos(c, IntPtr.Zero, S(x * w / 900), S(y * hh / 600),
                    Math.Max(1, S(cx * w / 900)), Math.Max(1, S(cy * hh / 600)),
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
            P(_files, 12, 12, 200, 26);
            P(_label1, 224, 16, 50, 22);
            P(_filter, 270, 14, 260, 24);
            P(_onlyKey, 540, 16, 320, 24);
            P(_stats, 12, 534, 560, 22);
            P(_bRefresh, 586, 532, 70, 28);
            P(_bFolder, 662, 532, 100, 28);
            P(_bReport, 768, 532, 100, 28);
            P(_bEvidence, 768, 496, 100, 28);
            // 日志框：左 12 / 顶 48 / 右留 12 / 底到 528（规格 876×480 @ 900×600 ✓）
            if (_view != IntPtr.Zero)
            {
                int x = S(12 * w / 900), y = S(48 * hh / 600);
                int cx = Math.Max(1, cw - x - S(12 * w / 900));
                int cy = Math.Max(1, S(528 * hh / 600) - y);
                LogViewWin32.SetWindowPos(_view, IntPtr.Zero, x, y, cx, cy, SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        private static void OnTimer()
        {
            // ★ 仅当窗口可见时才刷新 ✓（不可见时不动 ✓ 与 WinForms 版一致 ✓）
            if (!LogViewWin32.IsWindowVisible(_hwnd)) { LogViewWin32.L("WM_TIMER：窗口不可见 → 跳过刷新 ✓"); return; }
            LogViewWin32.L("WM_TIMER：窗口可见 → 自动 Reload ✓");
            Reload();
        }

        private static void OnCommand(IntPtr h, IntPtr w)
        {
            int id = (int)(w.ToInt64() & 0xFFFF);
            int code = (int)((w.ToInt64() >> 16) & 0xFFFF);
            if (code == CBN_SELCHANGE && id == ID_FILES) { LogViewWin32.L("下拉框切换 → 第 " + SelectedIndex() + " 项「" + SelectedName() + "」"); Reload(); return; }
            if (code == EN_CHANGE && id == ID_FILTER) { LogViewWin32.L("过滤框变化 → 「" + FilterText() + "」"); Reload(); return; }
            if (code == BN_CLICKED && id == ID_ONLYKEY) { LogViewWin32.L("复选框 → " + (OnlyKeyChecked() ? "勾选" : "取消") + "，Reload ✓"); Reload(); return; }
            if (code == BN_CLICKED && id == ID_REFRESH) { LogViewWin32.L("点「刷新」→ Reload ✓"); Reload(); return; }
            if (code == BN_CLICKED && id == ID_FOLDER) { LogViewWin32.L("点「打开文件夹」→ " + LogView.DataDir); OpenFolder(); return; }
            if (code == BN_CLICKED && id == ID_REPORT) { LogViewWin32.L("点「导出报告」→ ExportReport ✓"); LogView.ExportReport(_hwnd); return; }
            if (code == BN_CLICKED && id == ID_EVIDENCE) { LogViewWin32.L("点「上报证据」→ EvidenceReport.Show ✓"); EvidenceReport.Show(_hwnd); return; }
            // ★ IsDialogMessageW 会把 Esc/Enter 翻成 WM_COMMAND(IDCANCEL/IDOK) ✗ 只认自己那套 ✓
            if ((id == LogViewWin32.IDCANCEL || id == LogViewWin32.IDOK) && code == 0)
            {
                LogViewWin32.L("收到 ID" + (id == LogViewWin32.IDCANCEL ? "CANCEL" : "OK") + " → 关窗判定（窗口可用才关 ✓）");
                if (LogViewWin32.IsWindowEnabled(h)) { LogViewWin32.DestroyWindow(h); }
                return;
            }
            LogViewWin32.DefWindowProcW(h, WM_COMMAND, w, IntPtr.Zero);
        }

        private static IntPtr OnCtlColor(IntPtr ctrl, IntPtr dc)
        {
            if (ctrl == _stats)                       // 状态标签：深灰前景 ✓
            {
                LogViewWin32.SetTextColor(dc, unchecked((int)LogViewWin32.CLR_DIMGRAY));
                LogViewWin32.SetBkMode(dc, TRANSPARENT);
                return (IntPtr)(COLOR_WINDOW + 1);
            }
            if (ctrl == _view || ctrl == _filter)     // 日志框/过滤框：白底 ✓
            {
                LogViewWin32.SetTextColor(dc, unchecked((int)LogViewWin32.CLR_BLACK));
                LogViewWin32.SetBkColor(dc, unchecked((int)LogViewWin32.CLR_WHITE));
                return _brEdit != IntPtr.Zero ? _brEdit : (IntPtr)(COLOR_WINDOW + 1);
            }
            return IntPtr.Zero;
        }

        // ====================================================================
        //  刷新（算法与旧版逐字一致 ✓）
        // ====================================================================
        private static string FilterText() => LogViewWin32.GetText(_filter);

        private static bool OnlyKeyChecked() => LogViewWin32.Checked(_onlyKey);

        internal static int SelectedIndex()
        {
            if (_files == IntPtr.Zero) { return 0; }
            int i = (int)LogViewWin32.SendMessageW(_files, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64();
            return i < 0 ? 0 : i;
        }

        internal static string SelectedName()
        {
            int i = SelectedIndex();
            return i >= 0 && i < FileNames.Length ? FileNames[i] : FileNames[0];
        }

        private static void SetViewText(string text)
        {
            if (_view == IntPtr.Zero) { return; }
            // ★ 保留滚动位置与光标（否则过滤框每敲一个字就跳回顶部 ✗）
            long sel = LogViewWin32.SendMessageW(_view, EM_GETSEL, IntPtr.Zero, IntPtr.Zero).ToInt64();
            int caret = (int)(sel & 0xFFFFFFFF);
            int first = (int)LogViewWin32.SendMessageW(_view, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt64();
            LogViewWin32.SetText(_view, text);
            int c2 = Math.Min(caret, text.Length);
            LogViewWin32.SendMessageW(_view, EM_SETSEL, (IntPtr)c2, (IntPtr)c2);
            int now = (int)LogViewWin32.SendMessageW(_view, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt64();
            if (now != first) { LogViewWin32.SendMessageW(_view, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(first - now)); }
        }

        internal static void Reload()
        {
            try
            {
                string name = SelectedName();
                string path = LogView.PathOf(name);
                LogView.Current = name;

                if (!File.Exists(path))
                {
                    SetViewText("(还没有这个日志文件：" + path + ")");
                    LogViewWin32.SetText(_stats, name + "：不存在");
                    LogViewWin32.L("Reload：" + name + " → 不存在（" + path + "）");
                    return;
                }

                string[] lines;
                try
                {
                    lines = File.ReadAllLines(path, Encoding.UTF8);
                }
                catch (Exception e)
                {
                    SetViewText("读日志失败：" + e.Message);   // ★ 不许静默失败 ✓
                    LogViewWin32.L("Reload：" + name + " → 读日志失败：" + e.Message);
                    return;
                }

                string needle = FilterText().Trim();
                bool onlyKey = OnlyKeyChecked();
                var shown = new System.Collections.Generic.List<string>(lines.Length);
                foreach (string l in lines)
                {
                    if (needle.Length > 0
                        && l.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    if (onlyKey && !LogView.ContainsKey(l))
                    {
                        continue;
                    }
                    shown.Add(l);
                }

                SetViewText(string.Join("\r\n", shown));   // 原生 EDIT 用 CRLF ✓

                // ★ 统计：总行数 / 关键行数 / 前 3 个高频标签 ✓
                int keyCount = 0;
                var tags = new System.Collections.Generic.Dictionary<string, int>();
                foreach (string l in lines)
                {
                    if (LogView.ContainsKey(l)) { keyCount++; }
                    int a = l.IndexOf('[');
                    int b = a >= 0 ? l.IndexOf(']', a + 1) : -1;
                    if (a >= 0 && b > a)
                    {
                        string tag = l.Substring(a + 1, Math.Min(24, b - a - 1));
                        tags[tag] = tags.TryGetValue(tag, out int c) ? c + 1 : 1;
                    }
                }
                var top = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(tags);
                top.Sort((x, y) => y.Value.CompareTo(x.Value));
                var sb = new StringBuilder();
                sb.Append(name).Append("：共 ").Append(lines.Length).Append(" 行，关键行 ")
                  .Append(keyCount).Append(" 行，当前显示 ").Append(shown.Count).Append(" 行");
                for (int i = 0; i < Math.Min(3, top.Count); i++)
                {
                    sb.Append("   ").Append('[').Append(top[i].Key).Append("]×").Append(top[i].Value);
                }
                LogViewWin32.SetText(_stats, sb.ToString());
                LogViewWin32.L("Reload：" + name + " → " + sb.ToString());
            }
            catch (Exception ex)
            {
                LogViewWin32.L("★ Reload 抛异常：" + ex.GetType().Name + " " + ex.Message);
                try { LogViewWin32.SetText(_stats, "刷新失败：" + ex.Message); } catch { }
            }
        }

        private static void OpenFolder()
        {
            try
            {
                string dir = LogView.DataDir;
                try { if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); } } catch { }
                System.Diagnostics.Process.Start("explorer.exe", dir);
                LogViewWin32.L("资源管理器已打开：" + dir);
            }
            catch (Exception ex) { LogViewWin32.L("打开文件夹失败：" + ex.Message); }
        }
    }
}
