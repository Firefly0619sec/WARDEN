// ============================================================================
//  上报证据窗 —— 原生实现（2026-10-11 · Beta 3 / 0.3.2）  文件：EvidenceForm.cs
//  取代 WinForms 的 ShowDialog 版（Form / RadioButton / Panel / SaveFileDialog ✗）
//
//  【逐项复刻旧版布局】
//    标题「上报证据 / 反馈问题 —— WARDEN」客户区 880×660，居中于 owner（CenterParent ✓）
//    最小尺寸 700×500 ✓ 微软雅黑 9pt ✓ 预览框 Consolas 8.5pt ✓
//    上：提示 STATIC 高 74（Dock.Top ✓）
//    中：多行只读 EDIT 填满（Dock.Fill ✓ 双向滚动 / 不换行 ✓）显示的就是将发出去的全部内容 ✓
//    下：面板高 132（Dock.Bottom ✓）
//        明文单选 (12,6) 600×24 / PGP 单选 (12,32) 700×24 / 灰提示 (12,60) 840×20
//        复制 (12,88) 110×32 / 存文件 (132,88) 110×32 / GitHub (252,88) 150×32 / 关闭 (770,88) 90×32
//        ★ 默认两个传输方式都没选 → 前三个按钮【不可用】= 什么也不发 ✗
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace WardenLogView
{
    internal static class EvidenceForm
    {
        private const string Cls = "WardenLogViewDlgWnd";
        private const string Title = "上报证据 / 反馈问题 —— WARDEN";
        private const int CW = 880, CH = 660, MINW = 700, MINH = 500;
        private const int TIP_H = 74, BOTTOM_H = 132;

        private const int ID_TIP = 1, ID_VIEW = 2, ID_PLAIN = 3, ID_PGP = 4, ID_NOTE = 5,
                          ID_COPY = 6, ID_SAVE = 7, ID_ISSUE = 8, ID_CLOSE = 9;

        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000,
                           WS_VSCROLL = 0x00200000, WS_HSCROLL = 0x00100000, WS_DISABLED = 0x08000000;
        private const uint BS_PUSHBUTTON = 0, BS_AUTORADIOBUTTON = 9;
        private const uint ES_LEFT = 0, ES_MULTILINE = 4, ES_AUTOVSCROLL = 0x40, ES_AUTOHSCROLL = 0x80,
                           ES_READONLY = 0x800, ES_NOHIDESEL = 0x100;

        private const uint WM_CREATE = 0x0001, WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_CLOSE = 0x0010,
                           WM_COMMAND = 0x0111, WM_GETMINMAXINFO = 0x0024, WM_GETDLGCODE = 0x0087,
                           WM_CTLCOLORSTATIC = 0x0138;
        private const uint DLGC_WANTARROWS = 0x0001;
        private const int CBN_SELCHANGE = 1, EN_CHANGE = 0x0300, BN_CLICKED = 0;
        private const int COLOR_WINDOW = 5, TRANSPARENT = 1;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const int GWL_STYLE = -16;

        private static IntPtr _hwnd, _tip, _view, _plain, _pgp, _note, _copy, _save, _issue, _close;
        private static IntPtr _clsName, _brEdit, _brYellow;
        private static LogViewWin32.Fonts _fonts;
        private static double _scale = 1.0;
        private static uint _dpi = 96;
        private static string _bodyPlain = "", _bodyPgp = "";
        private static bool _running, _closed;

        private static int S(int px) => (int)Math.Round(px * _scale);

        /// <summary>这个句柄是不是上报窗的（AotWndProc 用它分流 ✓）</summary>
        internal static bool Owns(IntPtr h) => _running && h == _hwnd && h != IntPtr.Zero;

        internal static void RegisterClass(IntPtr inst)
        {
            _clsName = Marshal.StringToHGlobalUni(Cls);
            LogViewWin32.RegisterClass(Cls, _clsName, inst);
        }

        /// <summary>★ 打开上报窗口（owner = 原生窗口句柄 ✓ 不再用 IWin32Window ✓）</summary>
        internal static void Show(IntPtr owner)
        {
            if (_running) { LogViewWin32.L("上报证据窗已开着 → 忽略重复打开 ✓"); return; }
            _bodyPlain = EvidenceReport.PlainBody();
            _bodyPgp = EvidenceReport.PgpBody();
            LogViewWin32.L("上报证据窗：明文 " + _bodyPlain.Length + " 字符 / PGP " + _bodyPgp.Length + " 字符 ✓");
            _running = true;
            _closed = false;
            _hwnd = IntPtr.Zero;

            // ★ 建窗【之前】缩放已定好 ✓（用主窗那份，保持一致 ✓）
            LogViewWin32.ResolveScale(out _scale, out _dpi);
            LogViewWin32.ClientToWindow(S(CW), S(CH), WS_OVERLAPPEDWINDOW, out int wWin, out int hWin);
            _hwnd = LogViewWin32.CreateWindowExW(0, Cls, Title, WS_OVERLAPPEDWINDOW, 0, 0, wWin, hWin,
                IntPtr.Zero, IntPtr.Zero, LogViewWin32.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _running = false;
                LogViewWin32.Msg(owner, "打不开上报窗口（错误码 " + Marshal.GetLastWin32Error() + "）", "上报证据", false);
                return;
            }
            LogViewWin32.CenterOnWindow(_hwnd, owner, wWin, hWin);   // CenterParent ✓
            if (owner != IntPtr.Zero) { LogViewWin32.EnableWindow(owner, false); }   // 模态 ✓
            LogViewWin32.ShowWindow(_hwnd, 5 /*SW_SHOW*/);
            LogViewWin32.UpdateWindow(_hwnd);
            LogViewWin32.SetFocus(_view != IntPtr.Zero ? _view : _hwnd);
            LogViewWin32.L("上报证据窗已显示 hwnd=0x" + _hwnd.ToInt64().ToString("X") + " 尺寸 " + wWin + "x" + hWin);

            LogViewWin32.MSG msg;
            while (!_closed && LogViewWin32.GetMessageW(out msg, IntPtr.Zero, 0, 0))
            {
                // ★ 主窗的 3 秒定时器还在跑 ✓ 必须照常派发 ✓（只对本窗做对话框处理 ✓）
                bool handled = false;
                if (msg.hwnd == _hwnd) { try { handled = LogViewWin32.IsDialogMessageW(_hwnd, ref msg); } catch { } }
                if (!handled) { LogViewWin32.TranslateMessage(ref msg); LogViewWin32.DispatchMessageW(ref msg); }
            }
            if (owner != IntPtr.Zero && LogViewWin32.IsWindow(owner))
            {
                LogViewWin32.EnableWindow(owner, true);
                LogViewWin32.SetFocus(owner);
            }
            _running = false;
            _hwnd = IntPtr.Zero;
            if (_brYellow != IntPtr.Zero) { LogViewWin32.DeleteObject(_brYellow); _brYellow = IntPtr.Zero; }
            _fonts.Dispose();
            LogViewWin32.L("上报证据窗已关闭 ✓ 主窗已恢复可用 ✓");
        }

        internal static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            try
            {
                switch (m)
                {
                    case WM_CREATE: OnCreate(h); return IntPtr.Zero;
                    case WM_SIZE: Layout(h); return IntPtr.Zero;
                    case WM_GETMINMAXINFO: OnMinMax(l); return IntPtr.Zero;
                    case WM_GETDLGCODE: return (IntPtr)DLGC_WANTARROWS;
                    case WM_COMMAND: OnCommand(h, w); return IntPtr.Zero;
                    case WM_CLOSE:
                        LogViewWin32.L("上报证据窗收到 WM_CLOSE ✓");
                        _closed = true;
                        LogViewWin32.DestroyWindow(h);
                        return IntPtr.Zero;
                    case WM_CTLCOLORSTATIC: return OnCtlColor(w, l);
                    case WM_DESTROY:
                        _closed = true;
                        return IntPtr.Zero;
                }
                return LogViewWin32.DefWindowProcW(h, m, w, l);
            }
            catch (Exception ex)
            {
                LogViewWin32.L("★ 上报窗 WndProc 抛异常 msg=0x" + m.ToString("X") + " : " + ex.GetType().Name + " " + ex.Message);
                return LogViewWin32.DefWindowProcW(h, m, w, l);
            }
        }

        private static void OnCreate(IntPtr h)
        {
            _fonts = LogViewWin32.CreateFonts(_dpi);
            _brEdit = LogViewWin32.CreateSolidBrush(unchecked((int)LogViewWin32.CLR_WHITE));
            _brYellow = LogViewWin32.CreateSolidBrush(unchecked((int)LogViewWin32.CLR_YELLOW_BG));

            _tip = LogViewWin32.CreateWindowExW(0, "STATIC",
                "★ 下面显示的就是【将要发出去的全部内容】✓ 请先看一眼 ✓\n"
                + "★ 用户名 / 邮箱 / 手机号 / 机器名已经打码 ✓\n"
                + "★ 传输方式【默认不选】—— 你不选，就什么也不会发出去 ✗",
                WS_CHILD | WS_VISIBLE, 0, 0, 100, TIP_H, h, (IntPtr)ID_TIP, IntPtr.Zero, IntPtr.Zero);
            // ★ 先不 WS_VISIBLE（避免 WM_SIZE 前闪一下 100x100 ✓ Layout 里再显示 ✓）
            _view = LogViewWin32.CreateWindowExW(0, "EDIT", _bodyPlain,
                WS_CHILD | WS_TABSTOP | WS_VSCROLL | WS_HSCROLL | ES_LEFT | ES_MULTILINE |
                ES_AUTOVSCROLL | ES_AUTOHSCROLL | ES_READONLY | ES_NOHIDESEL,
                0, 0, 100, 100, h, (IntPtr)ID_VIEW, IntPtr.Zero, IntPtr.Zero);
            _plain = LogViewWin32.CreateWindowExW(0, "BUTTON", "明文（贴到 GitHub 新建 issue ✓ 内容公开，任何人可见）",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTORADIOBUTTON,
                0, 0, 600, 24, h, (IntPtr)ID_PLAIN, IntPtr.Zero, IntPtr.Zero);
            _pgp = LogViewWin32.CreateWindowExW(0, "BUTTON", "PGP 加密（用作者公钥加密 ✓ 只有作者能读 ✓ 适合含敏感信息的日志）",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTORADIOBUTTON,
                0, 0, 700, 24, h, (IntPtr)ID_PGP, IntPtr.Zero, IntPtr.Zero);
            _note = LogViewWin32.CreateWindowExW(0, "STATIC", "选择传输方式后，下面的按钮才会可用（默认两个都没选 = 不上传 ✓）",
                WS_CHILD | WS_VISIBLE, 0, 0, 840, 20, h, (IntPtr)ID_NOTE, IntPtr.Zero, IntPtr.Zero);
            // ★ 前三个按钮默认 WS_DISABLED ✓ 等于"什么也不发" ✗
            _copy = LogViewWin32.CreateWindowExW(0, "BUTTON", "复制内容", WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_DISABLED | BS_PUSHBUTTON,
                0, 0, 110, 32, h, (IntPtr)ID_COPY, IntPtr.Zero, IntPtr.Zero);
            _save = LogViewWin32.CreateWindowExW(0, "BUTTON", "存成文件", WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_DISABLED | BS_PUSHBUTTON,
                0, 0, 110, 32, h, (IntPtr)ID_SAVE, IntPtr.Zero, IntPtr.Zero);
            _issue = LogViewWin32.CreateWindowExW(0, "BUTTON", "在 GitHub 上打开", WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_DISABLED | BS_PUSHBUTTON,
                0, 0, 150, 32, h, (IntPtr)ID_ISSUE, IntPtr.Zero, IntPtr.Zero);
            _close = LogViewWin32.CreateWindowExW(0, "BUTTON", "关闭", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                0, 0, 90, 32, h, (IntPtr)ID_CLOSE, IntPtr.Zero, IntPtr.Zero);
            LogViewWin32.L("上报窗控件: tip=0x" + _tip.ToInt64().ToString("X") + " view=0x" + _view.ToInt64().ToString("X") +
                           " plain=0x" + _plain.ToInt64().ToString("X") + " pgp=0x" + _pgp.ToInt64().ToString("X") +
                           " copy=0x" + _copy.ToInt64().ToString("X") + " save=0x" + _save.ToInt64().ToString("X") +
                           " issue=0x" + _issue.ToInt64().ToString("X") + " close=0x" + _close.ToInt64().ToString("X") +
                           " err=" + Marshal.GetLastWin32Error());

            foreach (IntPtr c in new[] { _tip, _note, _plain, _pgp, _copy, _save, _issue, _close })
            {
                LogViewWin32.SetFont(c, _fonts.Ui);
            }
            LogViewWin32.SetFont(_view, _fonts.MonoSm);   // 预览 Consolas 8.5pt ✓
            SyncBody();
        }

        internal static void Layout(IntPtr h)
        {
            if (!LogViewWin32.GetClientRect(h, out LogViewWin32.RECT cc)) { return; }
            int cw = cc.R - cc.L, chh = cc.B - cc.T;
            int w = (int)Math.Round(cw * 880.0 / S(CW));
            int hh = (int)Math.Round(chh * 660.0 / S(CH));
            if (w < 1) { w = 1; }
            if (hh < 1) { hh = 1; }

            void P(IntPtr c, int x, int y, int cx, int cy)
            {
                if (c == IntPtr.Zero) { return; }
                LogViewWin32.SetWindowPos(c, IntPtr.Zero, S(x * w / 880), S(y * hh / 660),
                    Math.Max(1, S(cx * w / 880)), Math.Max(1, S(cy * hh / 660)),
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
            P(_tip, 0, 0, 880, TIP_H);
            int botY = 660 - BOTTOM_H;
            int viewY = TIP_H, viewH = botY - TIP_H;
            if (_view != IntPtr.Zero)
            {
                LogViewWin32.SetWindowPos(_view, IntPtr.Zero, 0, S(viewY * hh / 660),
                    Math.Max(1, cw), Math.Max(1, S(viewH * hh / 660)), SWP_NOZORDER | SWP_NOACTIVATE);
                if ((LogViewWin32.GetWindowLongValue(_view, GWL_STYLE) & (int)WS_VISIBLE) == 0) { LogViewWin32.ShowWindow(_view, 5 /*SW_SHOW*/); }
            }
            P(_plain, 12, botY + 6, 600, 24);
            P(_pgp, 12, botY + 32, 700, 24);
            P(_note, 12, botY + 60, 840, 20);
            P(_copy, 12, botY + 88, 110, 32);
            P(_save, 132, botY + 88, 110, 32);
            P(_issue, 252, botY + 88, 150, 32);
            P(_close, 770, botY + 88, 90, 32);
        }

        private static void OnMinMax(IntPtr l)
        {
            try
            {
                var mmi = Marshal.PtrToStructure<LogViewWin32.MINMAXINFO>(l);
                LogViewWin32.ClientToWindow(S(MINW), S(MINH), WS_OVERLAPPEDWINDOW, out int mw, out int mh);
                mmi.minTrackX = mw;
                mmi.minTrackY = mh;
                Marshal.StructureToPtr(mmi, l, false);
                LogViewWin32.L("上报窗最小尺寸 = " + mw + "x" + mh + "（客户区 " + MINW + "x" + MINH + " ✓）");
            }
            catch (Exception ex) { LogViewWin32.L("OnMinMax 失败：" + ex.Message); }
        }

        private static bool PgpChecked() => LogViewWin32.Checked(_pgp);

        private static string Current() => PgpChecked() ? _bodyPgp : _bodyPlain;

        /// <summary>★ 切换传输方式：预览跟着换 ✓ 三个按钮变可用 ✓（默认两个都没选 → 全灰 ✗）</summary>
        private static void SyncBody()
        {
            LogViewWin32.SetText(_view, Current());
            bool on = LogViewWin32.Checked(_plain) || PgpChecked();
            LogViewWin32.EnableWindow(_copy, on);
            LogViewWin32.EnableWindow(_save, on);
            LogViewWin32.EnableWindow(_issue, on);
            LogViewWin32.L("传输方式 = " + (PgpChecked() ? "PGP" : (on ? "明文" : "未选")) + "；预览 " + Current().Length + " 字符；三按钮可用=" + on);
        }

        private static void OnCommand(IntPtr h, IntPtr w)
        {
            int id = (int)(w.ToInt64() & 0xFFFF);
            int code = (int)((w.ToInt64() >> 16) & 0xFFFF);
            if (code != BN_CLICKED && code != 0) { LogViewWin32.DefWindowProcW(h, WM_COMMAND, w, IntPtr.Zero); return; }
            LogViewWin32.L("上报窗 WM_COMMAND id=" + id + " code=" + code);
            if (id == ID_PLAIN || id == ID_PGP) { SyncBody(); return; }
            if (id == ID_COPY) { DoCopy(h); return; }
            if (id == ID_SAVE) { DoSave(h); return; }
            if (id == ID_ISSUE) { DoIssue(h); return; }
            if (id == ID_CLOSE || id == LogViewWin32.IDCANCEL || id == LogViewWin32.IDOK)
            {
                _closed = true;
                LogViewWin32.DestroyWindow(h);
                return;
            }
            LogViewWin32.DefWindowProcW(h, WM_COMMAND, w, IntPtr.Zero);
        }

        private static IntPtr OnCtlColor(IntPtr ctrl, IntPtr dc)
        {
            if (ctrl == _view)                        // 预览：白底 ✓
            {
                LogViewWin32.SetTextColor(dc, unchecked((int)LogViewWin32.CLR_BLACK));
                LogViewWin32.SetBkColor(dc, unchecked((int)LogViewWin32.CLR_WHITE));
                return _brEdit != IntPtr.Zero ? _brEdit : (IntPtr)(COLOR_WINDOW + 1);
            }
            if (ctrl == _note)                        // 灰提示 ✓
            {
                LogViewWin32.SetTextColor(dc, unchecked((int)LogViewWin32.CLR_DIMGRAY));
                LogViewWin32.SetBkMode(dc, TRANSPARENT);
                return (IntPtr)(COLOR_WINDOW + 1);
            }
            if (ctrl == _tip && _brYellow != IntPtr.Zero)   // 提示条淡黄 ✓
            {
                LogViewWin32.SetBkColor(dc, unchecked((int)LogViewWin32.CLR_YELLOW_BG));
                return _brYellow;
            }
            return IntPtr.Zero;
        }

        private static void DoCopy(IntPtr h)
        {
            try
            {
                bool pgp = PgpChecked();
                string text = pgp ? EvidenceReport.EncryptForUi(Current(), h) : Current();
                if (!LogViewWin32.SetClipboardText(h, text))
                {
                    LogViewWin32.Msg(h, "复制失败：打不开剪贴板 ✗", "上报证据", false);
                    return;
                }
                LogViewWin32.Msg(h, pgp
                        ? "已加密并复制 ✓ 贴到 GitHub issue 里，或直接发给作者 ✓"
                        : "已复制 ✓ 到 GitHub 页面按 Ctrl+V 粘贴 ✓",
                    "上报证据", true);
            }
            catch (Exception ex)
            {
                LogViewWin32.Msg(h, "复制失败：" + ex.Message, "上报证据", false);
            }
        }

        private static void DoSave(IntPtr h)
        {
            try
            {
                bool pgp = PgpChecked();
                string name = (pgp ? "WARDEN-证据-加密-" : "WARDEN-证据-")
                              + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                              + (pgp ? ".asc" : ".md");
                // ★ comdlg32 的 GetSaveFileNameW（与 Updater 的 GetOpenFileNameW 只差一个标志位 ✓）
                string? path = LogViewWin32.SaveFile(h, "保存证据报告",
                    pgp ? "PGP 加密文件\0*.asc\0所有文件\0*.*\0\0" : "Markdown\0*.md\0所有文件\0*.*\0\0",
                    pgp ? "asc" : "md", name);
                if (path == null) { LogViewWin32.L("保存对话框：用户取消 ✓ 什么都没写 ✓"); return; }
                System.IO.File.WriteAllText(path,
                    pgp ? EvidenceReport.EncryptForUi(Current(), h) : Current(),
                    new System.Text.UTF8Encoding(false));
                LogViewWin32.L("已保存：" + path);
                LogViewWin32.Msg(h, "已保存：\n" + path, "上报证据", true);
            }
            catch (Exception ex)
            {
                LogViewWin32.Msg(h, "保存失败：" + ex.Message, "上报证据", false);
            }
        }

        private static void DoIssue(IntPtr h)
        {
            try
            {
                if (PgpChecked())
                {
                    LogViewWin32.SetClipboardText(h, EvidenceReport.EncryptForUi(Current(), h));
                    LogViewWin32.Msg(h,
                        "PGP 加密内容已复制 ✓\n\n"
                        + "接下来：在打开的页面里 Ctrl+V 粘贴 ✓\n"
                        + "（加密内容给作者看即可，公开也没关系 ✓）",
                        "上报证据", true);
                }
                else
                {
                    LogViewWin32.SetClipboardText(h, Current());
                }
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(EvidenceReport.IssueUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    LogViewWin32.Msg(h, "打不开浏览器：" + ex.Message + "\n\n"
                        + "内容已复制，你可以自己打开：\n" + EvidenceReport.IssueUrl,
                        "上报证据", false);
                }
            }
            catch (Exception ex) { LogViewWin32.L("开 GitHub 失败：" + ex.Message); }
        }
    }
}
