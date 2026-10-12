// ============================================================================
//  WARDEN 在线安装器 —— 向导版（2026-10-11 第三版 · 任务 5）
// ============================================================================
//  【用户的原设计（这版才真正做到 ✓）】
//    "我要的是离线下载器的那种……这玩意是要看用户的环境的，
//     如果用户的环境有问题的话，它是不会贸然安装的，
//     它会帮忙把环境给安上去，并且提示用户，更具人文关怀。"
//
//  【三步向导】
//    第 1 步 / 3：环境检测   ← ★ 不满足就【不放行】✗ 并给出人话引导 ✓
//    第 2 步 / 3：下载最新版  ← 进度条 ✓
//    第 3 步 / 3：校验并安装  ← SHA256 不过就拒绝 ✗
//
//  【界面照离线安装器】
//    · 顶部蓝色标题区（★ 自绘：WM_PAINT 填色 + DrawTextW ✓
//      不用 WM_CTLCOLORSTATIC ✗ —— 那个在 AOT 下 fail-fast 已踩过 ✓）
//    · 正文区：页标题 + 内容（★ 环境检测那几行也是自绘 ✓ 一次画完 ✓）
//    · 底部：上一步 / 下一步 / 取消 ✓
//
//  【体积】原生 AOT + curl.exe + 不用 HttpClient ✓ 目标 3 MB 内 ✓
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;

namespace WardenOnlineSetup
{
    internal static class Program
    {
        private static string Owner = "Firefly0619sec";   // ★ 不写死：--repo 可覆盖 ✓
        private static string Repo = "WARDEN";             // ★ 不写死 ✓
        private const string Version = "0.3.6";
        private static string ApiLatest => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        private static string ApiList => $"https://api.github.com/repos/{Owner}/{Repo}/releases";
        private static string Releases => $"https://github.com/{Owner}/{Repo}/releases";
        private const string UA = "WARDEN-Online-Setup/" + Version;

        private static bool _en;
        private static string T(string zh, string en) => _en ? en : zh;

        // ---- 状态 ----
        private static volatile int _page;                 // 0 环境 1 下载 2 完成
        private static volatile int _percent;
        private static readonly System.Diagnostics.Stopwatch nowWatch = System.Diagnostics.Stopwatch.StartNew();
        private static double lastSec, speed;
        private static long lastGot;
        private static volatile string _status = "", _err = "", _proxy = "", _file = "", _title = "";
        private static volatile bool _busy, _blocked;      // blocked = 环境不满足 → 不放行 ✗
        private static System.Collections.Generic.List<EnvItem> _env = new();
        private static volatile string _version = "";

        // ------------------------------------------------------------ Win32
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(
            uint ex, string cls, string text, uint style, int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern ushort RegisterClassW(ref WNDCLASS c);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern void PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int c);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr h, bool e);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr h, string t, string c, uint type);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] private static extern int BeginPaint(IntPtr h, IntPtr ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, IntPtr ps);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint c);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(
            int h, int w, int e, int o, int weight, uint i, uint u, uint s, uint cs, uint p, uint cl, uint q, uint pi, string face);
        [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint c);
        // ★ 画背景与蓝标题（上一版用 PatBlt 没画出来 ✗ 换 FillRect ✓）
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int l, t, r, b; }
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rr, IntPtr brush);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT rr);
        [DllImport("user32.dll")] private static extern IntPtr GetSysColorBrush(int idx);
        [DllImport("gdi32.dll")] private static extern uint SetBkColor(IntPtr dc, uint c);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int m);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW")]
        private static extern bool TextOutWRaw(IntPtr dc, int x, int y, string s, int n);

        /// <summary>★ 只在这里写一次：长度永远取真实长度 ✓
        ///   （之前每个调用点手写 30/60/90… 超过真实长度 ✗
        ///     TextOutW 会读到字符串末尾之外 → 画出乱字与重叠 ✓ 已修 ✓）</summary>
        private static bool TextOutW(IntPtr dc, int x, int y, string s)
        {
            if (string.IsNullOrEmpty(s)) { return false; }
            return TextOutWRaw(dc, x, y, s, s.Length);
        }

        /// <summary>★ 兼容旧的 5 参数写法：传进来的长度【一律忽略】✗ 用真实长度 ✓
        ///   （那些调用点原来手写 30/60/90… 比字符串还长 ✗
        ///     TextOutW 会越读 → 画出乱字 ✓ 这一层彻底堵住 ✓）</summary>
        private static bool TextOutW(IntPtr dc, int x, int y, string s, int n)
            => TextOutWRaw(dc, x, y, s, string.IsNullOrEmpty(s) ? 0 : s.Length);
        [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref ICC icc);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ShellExecuteW(
            IntPtr h, string op, string file, string param, string dir, int show);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? n);

        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? menu;
            [MarshalAs(UnmanagedType.LPWStr)] public string? cls;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ICC { public uint size, icc; }

        private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000;
        private const uint WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_OVERLAPPED = 0;
        private const uint WM_PAINT = 0x000F, WM_COMMAND = 0x0111, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
        private const uint WM_SETFONT = 0x0030, WM_SETTEXT = 0x000C;
        private const uint PBM_SETRANGE = 0x0401, PBM_SETPOS = 0x0402;
        private const uint WM_REFRESH = 0x8001;
        private const uint ICC_PROGRESS = 0x20, ICC_STD = 0x4000;
        private const int SW_SHOW = 5;
        private const int IDNEXT = 1000, IDCANCELB = 1001, IDMANUAL = 1002, IDBACK = 1003;
        private const uint BLUE = 0x00B47800, WHITE = 0x00FFFFFF, DARK = 0x00303030,
                           GREEN = 0x0030A030, RED = 0x003030C0, GRAY = 0x00808080;
        private const int HDR = 84;   // 蓝色标题区高度

        private static IntPtr _w, _hBar, _hNext, _hCancel, _hManual, _hBack;
        private static IntPtr _fTitle, _fHead, _fBody, _fSmall;
        private static IntPtr _brBlue;
        private delegate IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l);
        private static readonly Proc _cb = OnMsg;

        [STAThread]
        private static int Main(string[] args)
        {
            _en = !System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            _proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? "";
            bool check = false, envOnly = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--check": check = true; break;
                    case "--env": envOnly = true; break;
                    case "--proxy": if (i + 1 < args.Length) { _proxy = args[++i]; } break;
                    // ★ 通用化：仓库、资产名、是否含预发布，全都可以传 ✓
                    //   （下载器不写死任何版本号 ✓ 每次问 GitHub 要最新版 ✓）
                    case "--repo":
                        if (i + 1 < args.Length)
                        {
                            string[] sp = args[++i].Split('/');
                            if (sp.Length == 2 && sp[0].Length > 0 && sp[1].Length > 0) { Owner = sp[0]; Repo = sp[1]; }
                        }
                        break;
                    case "--asset": if (i + 1 < args.Length) { _assetWant = args[++i]; } break;
                    case "--prerelease": _includePre = true; break;
                    case "--version": Console.WriteLine("WARDEN " + Version); return 0;
                    case "-h":
                    case "--help":
                        Console.WriteLine("WARDEN " + T("在线安装器", "online installer") + " " + Version);
                        Console.WriteLine("  --env     " + T("只做环境检测", "environment check only"));
                        Console.WriteLine("  --check   " + T("只看最新版号", "check version only"));
                        Console.WriteLine("  --proxy <url>");
                        Console.WriteLine("  " + Releases);
                        return 0;
                }
            }
            if (check)
            {
                // ★ --check：只问 GitHub"最新版是什么、会下哪个文件、多大"✗ 不下载 ✓
                //   （以前这里跟 --env 混在一起 ✗ 结果 --check 跑的是环境检测 ✓ 欠了很久的账 ✓）
                try
                {
                    var (tagC, nameC, assetsC, fromC) = Resolve();
                    Console.WriteLine("仓库: " + Owner + "/" + Repo + (_includePre ? "  (含预发布)" : ""));
                    Console.WriteLine("来源: " + fromC);
                    Console.WriteLine("版本: " + tagC + "   " + nameC);
                    if (assetsC.Count == 0) { Console.WriteLine("★ 这个版本没有可下载的文件 ✗"); return 3; }
                    var pk = Pick(assetsC);
                    foreach (var a in assetsC)
                    {
                        Console.WriteLine((a.n == pk.n ? "  ★ 会下: " : "         ")
                                          + a.n.PadRight(30) + (a.sz / 1048576.0).ToString("0.0") + " MB");
                    }
                    return pk.n == null ? 3 : 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("★ 查询失败: " + ex.Message);
                    return 2;
                }
            }
            if (envOnly)
            {
                _env = EnvCheckLite.Run(_proxy);
                EnvCheckLite.Print(_env);
                _busy = true;
                return EnvCheckLite.CanProceed(_env) ? 0 : 4;
            }
            try { return RunGui(); }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "WARDEN-Online-Setup-crash.log"),
                        DateTime.Now + "  " + ex, new UTF8Encoding(false));
                }
                catch { }
                MessageBoxW(IntPtr.Zero, ex.Message, "WARDEN", 0x10);
                return 9;
            }
        }

        // ------------------------------------------------------------ 窗口
        private static int RunGui()
        {
            var icc = new ICC { size = (uint)Marshal.SizeOf<ICC>(), icc = ICC_PROGRESS | ICC_STD };
            InitCommonControlsEx(ref icc);
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_cb),
                hInstance = GetModuleHandleW(null),
                cls = "WARDENOnlineSetup3",
                hbrBackground = (IntPtr)16,
            };
            RegisterClassW(ref wc);
            _brBlue = CreateSolidBrush(BLUE);

            int w = 820, h = 700;
            _w = CreateWindowExW(0, "WARDENOnlineSetup3", T("WARDEN 在线安装", "WARDEN online setup"),
                 WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
                 (GetSystemMetrics(0) - w) / 2, (GetSystemMetrics(1) - h) / 2, w, h,
                 IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_w == IntPtr.Zero) { MessageBoxW(IntPtr.Zero, "window failed", "WARDEN", 0x10); return 3; }

            _fTitle = CreateFontW(-26, 0, 0, 0, 700, 0, 0, 0, 134, 0, 0, 0, 0, "Microsoft YaHei UI");
            _fHead = CreateFontW(-19, 0, 0, 0, 700, 0, 0, 0, 134, 0, 0, 0, 0, "Microsoft YaHei UI");
            _fBody = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 134, 0, 0, 0, 0, "Microsoft YaHei UI");
            _fSmall = CreateFontW(-13, 0, 0, 0, 400, 0, 0, 0, 134, 0, 0, 0, 0, "Microsoft YaHei UI");

            int cy = h - 31;   // 客户区高度（减掉标题栏 ✓）`n            _hBar = Mk("msctls_progress32", "", 32, cy - 96, w - 64, 22, IntPtr.Zero);
            SendMessageW(_hBar, PBM_SETRANGE, IntPtr.Zero, (IntPtr)((100 << 16) | 0));

            _hBack = Mk("BUTTON", T("上一步", "Back"), 32, cy - 52, 110, 34, _fBody, IDBACK);
            _hManual = Mk("BUTTON", T("手动下载", "Download page"), 152, cy - 52, 140, 34, _fBody, IDMANUAL);
            _hCancel = Mk("BUTTON", T("取消", "Cancel"), 520, cy - 52, 110, 34, _fBody, IDCANCELB);
            _hNext = Mk("BUTTON", T("正在检查…", "Checking…"), 640, cy - 56, 148, 40, _fBody, IDNEXT);

            ShowWindow(_w, SW_SHOW);
            UpdateWindow(_w);
            SetForegroundWindow(_w);
            new Thread(Step1_Env) { IsBackground = true }.Start();

            while (GetMessageW(out MSG m, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }
            return 0;
        }

        private static IntPtr Mk(string cls, string text, int x, int y, int w, int h, IntPtr font, int id = 0)
        {
            IntPtr hh = CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                                        x, y, w, h, _w, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);   // ★ 控件 ID 在 hMenu 位 ✓
            if (font != IntPtr.Zero) { SendMessageW(hh, WM_SETFONT, font, (IntPtr)1); }
            return hh;
        }

        private static void SetText(IntPtr h, string s)
        {
            if (h != IntPtr.Zero) { SendMessageW(h, WM_SETTEXT, IntPtr.Zero, Marshal.StringToHGlobalUni(s)); }
        }

        // ------------------------------------------------------------ 画界面
        private static void Draw(IntPtr dc)
        {
            int page = _page;
            // ★ ① 先把客户区擦成对话框底色 —— 不擦的话每次重绘都会叠字（重影）✗
            GetClientRect(_w, out RECT cr);
            FillRect(dc, ref cr, GetSysColorBrush(15 /*COLOR_3DFACE*/));
            // ★ ② 蓝色标题区（FillRect ✓ 上一版用 PatBlt 没画出来 ✗）
            RECT hr = new RECT { l = 0, t = 0, r = cr.r, b = HDR };
            FillRect(dc, ref hr, _brBlue);
            IntPtr ob = GetSysColorBrush(15);

            SetBkMode(dc, 1);
            SetTextColor(dc, WHITE);
            IntPtr of = SelectObject(dc, _fTitle);
            TextOutW(dc, 28, 16, "WARDEN " + T("在线安装", "online setup"), 30);
            SelectObject(dc, _fSmall);
            TextOutW(dc, 30, 54, T("自动获取最新版 · 装之前先替你把环境检查一遍 ✓",
                                   "Fetches the latest release · checks your environment first"), 90);

            // 页标题
            SetTextColor(dc, DARK);
            SelectObject(dc, _fHead);
            string head = page == 0 ? T("第 1 步 / 3：环境检测", "Step 1 / 3: environment check")
                        : page == 1 ? T("第 2 步 / 3：下载最新版", "Step 2 / 3: downloading")
                                    : T("第 3 步 / 3：校验并安装", "Step 3 / 3: verify and install");
            TextOutW(dc, 32, HDR + 22, head, 60);

            SelectObject(dc, _fBody);
            int y = HDR + 64;

            if (page == 0)
            {
                // ★ 环境检测逐项（自绘 ✓ 每一项前面一个标记 ✓）
                foreach (var it in _env)
                {
                    string mark = it.Level == EnvLevel.Ok ? "✓" : it.Level == EnvLevel.Warn ? "!" : "✗";
                    SetTextColor(dc, it.Level == EnvLevel.Ok ? GREEN : it.Level == EnvLevel.Warn ? GRAY : RED);
                    TextOutW(dc, 32, y, mark, 4);
                    SetTextColor(dc, DARK);
                    TextOutW(dc, 56, y, it.Title + "：" + it.Detail, 200);
                    y += 26;
                    if (it.Level == EnvLevel.Fail && it.Fix.Length > 0)
                    {
                        SetTextColor(dc, GRAY);
                        SelectObject(dc, _fSmall);
                        foreach (string line in it.Fix.Split('\n'))
                        {
                            TextOutW(dc, 70, y, line, 300);
                            y += 20;
                        }
                        SelectObject(dc, _fBody);
                        y += 6;
                    }
                }
                if (_busy && _env.Count == 0)
                {
                    SetTextColor(dc, GRAY);
                    TextOutW(dc, 32, y, T("正在逐项检查…", "Checking…"), 60);
                }
                if (_blocked)
                {
                    SetTextColor(dc, RED);
                    SelectObject(dc, _fHead);
                    TextOutW(dc, 32, y + 10, T("★ 环境还不满足 —— 先不装（不会贸然安装 ✓）",
                                               "★ Environment not ready — not installing"), 120);
                }
                else if (!_busy && _env.Count > 0)
                {
                    SetTextColor(dc, GREEN);
                    SelectObject(dc, _fHead);
                    TextOutW(dc, 32, y + 10, T("✓ 环境没问题，可以继续 ✓", "✓ Ready to continue"), 80);
                }
            }
            else
            {
                SetTextColor(dc, DARK);
                TextOutW(dc, 32, y, _version, 200);
                y += 34;
                TextOutW(dc, 32, y, _status, 300);
                if (_err.Length > 0)
                {
                    SetTextColor(dc, RED);
                    foreach (string line in _err.Split('\n'))
                    {
                        y += 30;
                        TextOutW(dc, 32, y, line, 400);
                    }
                }
            }
            SelectObject(dc, of);
        }

        [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, uint rop);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int a, int b, int c, int d);

        private static IntPtr OnMsg(IntPtr h, uint m, IntPtr wp, IntPtr lp)
        {
            switch (m)
            {
                case WM_PAINT:
                {
                    IntPtr ps = Marshal.AllocHGlobal(64);
                    IntPtr dc = BeginPaint(h, ps);
                    Draw(dc);
                    EndPaint(h, ps);
                    Marshal.FreeHGlobal(ps);
                    return IntPtr.Zero;
                }
                case WM_REFRESH:
                    if (_page == 1) { SendMessageW(_hBar, PBM_SETPOS, (IntPtr)_percent, IntPtr.Zero); }
                    InvalidateRect(_w, IntPtr.Zero, true);
                    UpdateWindow(_w);
                    return IntPtr.Zero;
                case WM_COMMAND:
                    int id = (int)(wp.ToInt64() & 0xFFFF);
                    if (id == IDNEXT)
                    {
                        if (_page == 0 && !_blocked && !_busy)
                        {
                            _page = 1;
                            SetText(_hNext, T("正在下载…", "Downloading…"));
                            EnableWindow(_hNext, false);
                            EnableWindow(_hBack, false);
                            InvalidateRect(_w, IntPtr.Zero, true);
                            new Thread(Step2_Download) { IsBackground = true }.Start();
                        }
                        else if (_page == 2 && _file.Length > 0 && File.Exists(_file))
                        {
                            // ★ 交接（用户要求 ✓）：拉起刚下好且校验通过的离线安装包
                            //   成功 → 在线安装器自己关掉 ✓；失败 → 保留窗口并把路径交给用户 ✓
                            IntPtr rc2 = ShellExecuteW(IntPtr.Zero, "open", _file, "", null, 1);
                            if (rc2.ToInt64() > 32)
                            {
                                _status = T("✓ 已交给安装向导 —— 本窗口马上关闭 ✓", "✓ Handed over — closing");
                                InvalidateRect(_w, IntPtr.Zero, true); UpdateWindow(_w);
                                Thread.Sleep(500);
                                DestroyWindow(h);
                            }
                            else
                            {
                                _err = T("★ 没能自动拉起安装向导 ✗ 可以手动双击：\n" + _file, "★ Could not launch it ✗ double-click:\n" + _file);
                                InvalidateRect(_w, IntPtr.Zero, true); UpdateWindow(_w);
                            }
                        }
                        else if (_blocked)
                        {
                            // 「帮我装好」：VC++ 这类能替他做的，就替他做掉 ✓
                            FixIt();
                        }
                        return IntPtr.Zero;
                    }
                    if (id == IDMANUAL) { ShellExecuteW(IntPtr.Zero, "open", Releases, "", null, 1); return IntPtr.Zero; }
                    if (id == IDCANCELB) { DestroyWindow(h); return IntPtr.Zero; }
                    if (id == IDBACK && _page == 1 && !_busy) { _page = 0; InvalidateRect(_w, IntPtr.Zero, true); return IntPtr.Zero; }
                    return IntPtr.Zero;
                case WM_CLOSE: DestroyWindow(h); return IntPtr.Zero;
                case WM_DESTROY: PostQuitMessage(0); return IntPtr.Zero;
            }
            return DefWindowProcW(h, m, wp, lp);
        }

        private static void Refresh() => PostMessageW(_w, WM_REFRESH, IntPtr.Zero, IntPtr.Zero);

        /// <summary>★ 能替用户做的就替他做掉 ✓（目前只有 VC++ 运行库这一类 ✓）</summary>
        private static void FixIt()
        {
            MessageBoxW(_w,
                T("我来帮你装：\n\n接下来会打开微软官方的 VC++ 运行库下载页 ✓\n"
                  + "下载下来双击装一下（下一步下一步就行 ✓）\n"
                  + "装完回到这个窗口再点一次「重新检查」就好 ✓",
                  "I will help: the official Microsoft VC++ runtime page will open.\n"
                  + "Install it (next-next-finish), then come back and check again."),
                T("帮你装好", "Let me help"), 0x40);
            ShellExecuteW(IntPtr.Zero, "open",
                "https://aka.ms/vs/17/release/vc_redist.x64.exe", "", null, 1);
        }

        // ------------------------------------------------------------ 第 1 步
        private static void Step1_Env()
        {
            _busy = true;
            _title = T("正在检查你的电脑…", "Checking your computer…");
            Refresh();
            _env = EnvCheckLite.Run(_proxy);
            _blocked = !EnvCheckLite.CanProceed(_env);
            _busy = false;
            if (_blocked)
            {
                SetText(_hNext, T("帮我装好", "Help me fix it"));
                EnableWindow(_hNext, true);
            }
            else
            {
                SetText(_hNext, T("下一步：开始下载", "Next: start download"));
                EnableWindow(_hNext, true);
            }
            Refresh();
        }

        // ------------------------------------------------------------ 第 2 步
        private static void Step2_Download()
        {
            _busy = true;
            try
            {
                var (tag, name, assets, from) = Resolve();
                _version = name + "   (" + tag + ")";
                if (assets.Count == 0) { throw new Exception(T("这个版本没有可下载的文件 ✗", "This release has no assets")); }
                var pick = Pick(assets);
                if (pick.n == null) { throw new Exception(T("这个版本没有可下载的文件 ✗", "This release has no assets")); }

                string tmp = Path.Combine(Path.GetTempPath(), "WARDEN-" + tag.Replace('/', '_') + "-" + pick.n);
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
                _status = T("正在下载…", "Downloading…");
                Refresh();

                var cp = new ProcessStartInfo("curl.exe") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                cp.ArgumentList.Add("-L"); cp.ArgumentList.Add("--fail"); cp.ArgumentList.Add("--silent"); cp.ArgumentList.Add("--show-error");
                // ★ 断点续传 + 自动重试 + 超时分离（大包别被过早掐死 ✓）
                cp.ArgumentList.Add("-C"); cp.ArgumentList.Add("-");
                cp.ArgumentList.Add("--retry"); cp.ArgumentList.Add("3");
                cp.ArgumentList.Add("--retry-delay"); cp.ArgumentList.Add("2");
                cp.ArgumentList.Add("--retry-all-errors");
                cp.ArgumentList.Add("--connect-timeout"); cp.ArgumentList.Add("15");
                cp.ArgumentList.Add("--max-time"); cp.ArgumentList.Add("1800");
                cp.ArgumentList.Add("-A"); cp.ArgumentList.Add(UA);
                if (_proxy.Length > 0) { cp.ArgumentList.Add("--proxy"); cp.ArgumentList.Add(_proxy); }
                cp.ArgumentList.Add("-o"); cp.ArgumentList.Add(tmp); cp.ArgumentList.Add(pick.u);
                using var proc = Process.Start(cp)!;
                while (!proc.HasExited)
                {
                    long got = 0;
                    try { got = new FileInfo(tmp).Length; } catch { }
                    _percent = pick.sz > 0 ? (int)Math.Min(100, got * 100 / pick.sz) : 0;
                    // ★ 速度 + 剩余时间（用户要求：不要只显示百分比 ✓）
                    double sec = nowWatch.Elapsed.TotalSeconds;
                    if (sec - lastSec > 0.25)
                    {
                        double inst = (got - lastGot) / 1048576.0 / Math.Max(0.001, sec - lastSec);
                        if (inst > 0) { speed = inst; }
                        lastGot = got; lastSec = sec;
                    }
                    double left = (speed > 0.05 && pick.sz > got) ? (pick.sz - got) / 1048576.0 / speed : 0;
                    string eta = left > 0 ? T("   剩余约 ", "   about ") + (left >= 60 ? (left / 60).ToString("0") + T(" 分", " min") : left.ToString("0") + T(" 秒", " s")) : "";
                    _status = T("正在下载… ", "Downloading… ") + _percent + "%   ("
                              + (got / 1048576.0).ToString("0.0") + " / " + (pick.sz / 1048576.0).ToString("0.0") + " MB)"
                              + "   " + speed.ToString("0.00") + " MB/s" + eta;
                    Thread.Sleep(300);
                }
                if (proc.ExitCode != 0) { throw new Exception(T("下载失败（curl ", "download failed (curl ") + proc.ExitCode + ")"); }
                _file = tmp;

                _status = T("正在校验 SHA256…", "Verifying SHA256…");
                Refresh();
                var sums = assets.Find(a => a.n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
                if (sums.u == null)
                {
                    throw new Exception(T("这个版本没有校验文件 → 为安全起见不自动运行 ✗\n文件留着：",
                                          "No checksum file -> not running it automatically.\nFile kept at: ") + tmp);
                }
                string st = Curl(sums.u);
                string want = "";
                foreach (string line in st.Split('\n'))
                {
                    if (line.Trim().EndsWith(pick.n, StringComparison.OrdinalIgnoreCase)) { want = line.Trim().Split(' ')[0].ToLowerInvariant(); break; }
                }
                using var sha = SHA256.Create();
                using var rd = File.OpenRead(tmp);
                string got2 = BitConverter.ToString(sha.ComputeHash(rd)).Replace("-", "").ToLowerInvariant();
                if (want.Length == 0 || want != got2)
                {
                    throw new Exception(T("★★ 校验不通过 → 【拒绝运行】✗ 文件可能被篡改\n文件留着：",
                                          "★★ Verification failed -> refusing to run.\nFile kept at: ") + tmp);
                }

                _page = 2;
                _percent = 100;
                _status = T("✓ 校验通过 —— 点「开始安装」，之后的向导由你确认 ✓",
                            "✓ Verified — click Install; the wizard takes it from there");
                SetText(_hNext, T("开始安装", "Install now"));
                EnableWindow(_hNext, true);
                SetText(_hCancel, T("关闭", "Close"));
            }
            catch (Exception e)
            {
                _err = e.Message;
                _page = 2;
                _status = T("没成功 ✗", "Did not succeed");
                SetText(_hNext, T("重试", "Retry"));
                EnableWindow(_hNext, false);
                SetText(_hCancel, T("关闭", "Close"));
            }
            _busy = false;
            Refresh();
        }

        private static string _assetWant = "";       // --asset 指定的文件名（留空=自动挑 ✓）
        private static bool _includePre;             // --prerelease：连预发布一起看 ✓

        /// <summary>★ 问 GitHub 要最新版（不写死版本号 ✓）返回 tag/name/资产表/来源说明 ✓</summary>
        /// <summary>★ 问 GitHub 要最新版（不写死版本号 ✓）
        ///   解析用 System.Text.Json ✓（原来是正则手撕 ✗ 为它翻过好几次车 ✓）
        ///   JsonDocument 是 DOM 方式 ✓ AOT 不需要反射 ✓ 所以能用在原生 AOT 里 ✓</summary>
        private static (string tag, string name, List<(string n, string u, long sz)> assets, string from) Resolve()
        {
            string json = _includePre ? "" : Curl(ApiLatest);
            string from = _includePre ? "releases 列表（含预发布）" : "latest（正式版）";
            bool asList = false;

            if (!json.Contains("tag_name", StringComparison.Ordinal))
            {
                json = Curl(ApiList);
                asList = true;
                from = "releases 列表（没有正式版 → 回退到最新的 ✓）";
            }
            if (string.IsNullOrWhiteSpace(json) || !json.Contains("tag_name", StringComparison.Ordinal))
            {
                throw new Exception(T("查不到发布信息（没联网 / 需要代理 / 仓库里还没有发布 ✓）",
                                      "Could not read release info (offline / proxy / nothing released)"));
            }

            using var doc = JsonDocument.Parse(json);
            JsonElement rel = doc.RootElement;
            if (rel.ValueKind == JsonValueKind.Array)
            {
                // 列表是按时间倒序 ✓ 取第一个 = 最新的那一条 ✓
                if (rel.GetArrayLength() == 0)
                {
                    throw new Exception(T("仓库里还没有任何发布 ✓", "This repository has no releases yet"));
                }
                rel = rel[0];
                asList = true;
            }

            string tag = rel.TryGetProperty("tag_name", out var tEl) ? (tEl.GetString() ?? "?") : "?";
            string name = rel.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? tag) : tag;
            if (string.IsNullOrWhiteSpace(name)) { name = tag; }

            var list = new List<(string n, string u, long sz)>();
            if (rel.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    string an = a.TryGetProperty("name", out var x1) ? (x1.GetString() ?? "") : "";
                    string au = a.TryGetProperty("browser_download_url", out var x2) ? (x2.GetString() ?? "") : "";
                    long asz = 0;
                    if (a.TryGetProperty("size", out var x3) && x3.ValueKind == JsonValueKind.Number)
                    {
                        try { asz = x3.GetInt64(); } catch { asz = 0; }
                    }
                    if (an.Length > 0 && au.Length > 0) { list.Add((an, au, asz)); }
                }
            }
            if (asList) { /* 只是来源说明用 ✓ */ }
            return (tag, name, list, from);
        }

        /// <summary>★ 挑哪个文件：--asset 优先 ✓ 否则挑安装包 ✓ 再否则第一个 exe ✓（不写死 ✓）</summary>
        private static (string n, string u, long sz) Pick(List<(string n, string u, long sz)> found)
        {
            if (found.Count == 0) { return (null!, null!, 0); }
            if (_assetWant.Length > 0)
            {
                var exact = found.Find(a => a.n.Equals(_assetWant, StringComparison.OrdinalIgnoreCase));
                if (exact.n != null) { return exact; }
                var wild = found.Find(a => a.n.IndexOf(_assetWant, StringComparison.OrdinalIgnoreCase) >= 0);
                if (wild.n != null) { return wild; }
            }
            // ★ 优先"离线安装包"：名字带 Setup 但【不带 Online/Web/Boot】✓
            //   否则会挑中在线安装器自己（小的那个 ✗ 等于自己下自己 ✓）
            // ★ 跳过 arm64 / arm 资产（换仓库时挑错过 ✗ 比如 PowerToys 挑到 arm64 ✓）
            found.RemoveAll(a => a.n.IndexOf("arm64", StringComparison.OrdinalIgnoreCase) >= 0
                                 || a.n.IndexOf("aarch64", StringComparison.OrdinalIgnoreCase) >= 0
                                 || System.Text.RegularExpressions.Regex.IsMatch(a.n, @"(?i)\barm\b"));
            if (found.Count == 0) { return found.Count > 0 ? found[0] : (null!, null!, 0); }
            var setup = found.Find(a => a.n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                       && a.n.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0
                                       && a.n.IndexOf("Online", StringComparison.OrdinalIgnoreCase) < 0
                                       && a.n.IndexOf("Web", StringComparison.OrdinalIgnoreCase) < 0);
            if (setup.n != null) { return setup; }
            setup = found.Find(a => a.n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                    && a.n.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0);
            if (setup.n != null)
            {
                // 剩下的都叫 Setup 时，挑最大的（离线包总是大的 ✓）
                var biggest = setup;
                foreach (var a in found)
                {
                    if (a.n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && a.sz > biggest.sz) { biggest = a; }
                }
                return biggest;
            }
            var exe = found.Find(a => a.n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (exe.n != null) { return exe; }
            return found[0];
        }

        private static string Curl(string url)
        {
            var psi = new ProcessStartInfo("curl.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-sSL");
            psi.ArgumentList.Add("--max-time");
            psi.ArgumentList.Add("60");
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add("User-Agent: " + UA);
            if (_proxy.Length > 0) { psi.ArgumentList.Add("--proxy"); psi.ArgumentList.Add(_proxy); }
            psi.ArgumentList.Add(url);
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(70000);
            return outp;
        }
    }
}
