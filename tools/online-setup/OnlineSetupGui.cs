// ============================================================================
//  WARDEN 在线安装器 —— 图形界面版（2026-10-11）
// ============================================================================
//  【为什么从控制台改成窗口】
//    之前是黑底白字控制台 ✗ 还问 "Continue? (Y/n)" ✗
//    而我们 README 写的是"把安静的桌面还给他"✗ 给普通用户黑窗口就是打自己脸 ✓
//
//  【为什么不用 WinForms】自包含约 47 MB ✗ 与离线包一样大，"在线"就没意义 ✓
//    → 原生 AOT + 纯 Win32（系统自带的 STATIC / PROGRESS / BUTTON 控件 ✓）
//      体积约 2~4 MB ✓ 观感就是标准 Windows 窗口 ✓
//
//  【流程】查版本 → 显示 → 下载（进度条动 ✓）→ 校验 SHA256 → 通过后可点安装 ✓
//  【★ 安全】校验不过 → 明确拒绝运行 ✗ 文件留着让你自己看 ✓ 不静默降级 ✓
//  【参数】--check / --proxy <url> / --version / --help（控制台输出 ✓ 给脚本用 ✓）
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WardenOnlineSetup
{
    internal static class Program
    {
        private const string Owner = "Firefly0619sec";
        private const string Repo = "WARDEN";
        private const string Version = "0.2.2";
        private static readonly string ApiLatest = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        private static readonly string ApiList = $"https://api.github.com/repos/{Owner}/{Repo}/releases";
        private static readonly string Releases = $"https://github.com/{Owner}/{Repo}/releases";

        private static bool _en;
        private static string T(string zh, string eng) => _en ? eng : zh;

        private static volatile int _percent;
        private static volatile string _status = "", _version = "", _err = "", _proxy = "", _file = "";
        private static volatile int _phase;          // 0 查询 2 下载/校验 3 就绪 9 失败
        private static volatile bool _cancel;

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
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr h, string t, string c, uint type);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(
            int h, int w, int e, int o, int weight, uint i, uint u, uint s, uint cs, uint p, uint cl, uint q, uint pi, string face);
        [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref ICC icc);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ShellExecuteW(
            IntPtr h, string op, string file, string param, string dir, int show);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? n);

        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS { public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? menu, cls; }
        [StructLayout(LayoutKind.Sequential)] private struct ICC { public uint size, icc; }

        private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000;
        private const uint WS_OVERLAPPED = 0, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
        private const uint WM_COMMAND = 0x0111, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_SETFONT = 0x0030, WM_SETTEXT = 0x000C;
        private const uint PBM_SETRANGE = 0x0401, PBM_SETPOS = 0x0402;
        private const uint WM_UPDATE = 0x8001, WM_DONE = 0x8002;
        private const uint ICC_PROGRESS = 0x20, ICC_STD = 0x4000;
        private const int SW_SHOW = 5;
        private const int IDOK = 1000, IDCANCELB = 1001, IDMANUAL = 1002;

        private static IntPtr _w, _hVer, _hStatus, _hNote, _hBar, _hOk, _hCancel, _hManual;
        private delegate IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l);
        private static readonly Proc _cb = OnMsg;

        [STAThread]
        private static int Main(string[] args)
        {
            _en = !System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            _proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? "";
            bool check = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--check": check = true; break;
                    case "--proxy": if (i + 1 < args.Length) { _proxy = args[++i]; } break;
                    case "--version": Console.WriteLine("WARDEN " + T("在线安装器", "online installer") + " " + Version); return 0;
                    case "-h":
                    case "--help":
                        Console.WriteLine("WARDEN " + T("在线安装器", "online installer") + " " + Version);
                        Console.WriteLine(T("  不带参数双击 = 窗口界面 ✓", "  No arguments = GUI"));
                        Console.WriteLine("  --check        " + T("只检查新版本", "check only"));
                        Console.WriteLine("  --proxy <url>  " + T("走代理", "use a proxy"));
                        Console.WriteLine("  " + T("离线安装包：", "Offline installer: ") + Releases);
                        return 0;
                }
            }
            return check ? RunConsole() : RunGui();
        }

        // ------------------------------------------------------------ 控制台（--check ✓）
        private static int RunConsole()
        {
            using var http = MakeHttp();
            try
            {
                Console.WriteLine((M(http.GetStringAsync(ApiList).GetAwaiter().GetResult(),
                                     "\"tag_name\"\\s*:\\s*\"([^\"]+)\"") ?? "?") + T("   ← 最新版本", "  <- latest"));
                Console.WriteLine(T("✓ 只检查 → 不下载", "✓ check only -> nothing downloaded"));
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine(T("★ 查不到：", "★ Failed: ") + e.Message + "\n" + Releases);
                return 2;
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
                cls = "WARDENOnlineSetup",
                hbrBackground = (IntPtr)16,
            };
            RegisterClassW(ref wc);

            int w = 720, h = 300;
            _w = CreateWindowExW(0, "WARDENOnlineSetup", T("WARDEN 在线安装", "WARDEN online setup"),
                 WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
                 (GetSystemMetrics(0) - w) / 2, (GetSystemMetrics(1) - h) / 2, w, h,
                 IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_w == IntPtr.Zero)
            {
                MessageBoxW(IntPtr.Zero, T("窗口创建失败 ✗", "Window creation failed"), "WARDEN", 0x10);
                return 3;
            }
            var big = CreateFontW(-22, 0, 0, 0, 700, 0, 0, 0, 134, 0, 0, 0, 0, "Microsoft YaHei UI");
            var norm = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 134, 0, 0, 0, 0, "Microsoft YaHei UI");

            _hVer = Mk("STATIC", T("正在查询最新版本…", "Checking the latest release…"), 24, 18, 660, 36, big);
            _hStatus = Mk("STATIC", T("请稍候 ✓", "Please wait"), 24, 62, 660, 50, norm);
            _hBar = Mk("msctls_progress32", "", 24, 120, 660, 24, IntPtr.Zero);
            SendMessageW(_hBar, PBM_SETRANGE, IntPtr.Zero, (IntPtr)((100 << 16) | 0));
            _hNote = Mk("STATIC", T("★ 主程序不联网 —— 只有这个窗口会访问 GitHub 一次 ✓\n"
                                  + "★ 下载完会先校验 SHA256，对不上就拒绝安装 ✗",
                                  "★ The app itself is offline; only this window contacts GitHub.\n"
                                  + "★ The download is verified against SHA256 before it runs."),
                        24, 152, 660, 44, norm);
            _hManual = Mk("BUTTON", T("手动下载", "Download page"), 24, 208, 130, 34, norm);
            _hCancel = Mk("BUTTON", T("取消", "Cancel"), 424, 208, 110, 34, norm);
            _hOk = Mk("BUTTON", T("开始安装", "Install now"), 544, 208, 140, 40, norm);
            EnableWindow(_hOk, false);

            ShowWindow(_w, SW_SHOW);
            UpdateWindow(_w);
            SetForegroundWindow(_w);
            new Thread(Work) { IsBackground = true }.Start();

            while (GetMessageW(out MSG m, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }
            _cancel = true;
            return 0;
        }

        private static IntPtr Mk(string cls, string text, int x, int y, int w, int h, IntPtr font)
        {
            IntPtr hh = CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                                        x, y, w, h, _w, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (font != IntPtr.Zero) { SendMessageW(hh, WM_SETFONT, font, (IntPtr)1); }
            return hh;
        }

        private static void SetText(IntPtr h, string s)
        {
            if (h != IntPtr.Zero)
            {
                SendMessageW(h, WM_SETTEXT, IntPtr.Zero, Marshal.StringToHGlobalUni(s));
            }
        }

        private static IntPtr OnMsg(IntPtr h, uint m, IntPtr wp, IntPtr lp)
        {
            switch (m)
            {
                case WM_UPDATE:
                    SendMessageW(_hBar, PBM_SETPOS, (IntPtr)_percent, IntPtr.Zero);
                    SetText(_hStatus, _status);
                    SetText(_hVer, _version);
                    return IntPtr.Zero;
                case WM_DONE:
                    SendMessageW(_hBar, PBM_SETPOS, (IntPtr)_percent, IntPtr.Zero);
                    SetText(_hStatus, _status);
                    SetText(_hVer, _version);
                    if (_phase == 3)
                    {
                        SetText(_hNote, T("✓ 校验通过 —— 点「开始安装」，之后的向导由你确认 ✓",
                                          "✓ Verified — click \"Install now\"; the wizard takes it from there"));
                        EnableWindow(_hOk, true);
                        EnableWindow(_hCancel, false);
                    }
                    else
                    {
                        SetText(_hNote, _err);
                        SetText(_hCancel, T("关闭", "Close"));
                    }
                    return IntPtr.Zero;
                case WM_COMMAND:
                    int id = (int)(wp.ToInt64() & 0xFFFF);
                    if (id == IDOK)
                    {
                        if (_file.Length > 0 && File.Exists(_file))
                        {
                            ShellExecuteW(IntPtr.Zero, "open", _file, "", null, 1);
                        }
                        return IntPtr.Zero;
                    }
                    if (id == IDMANUAL)
                    {
                        ShellExecuteW(IntPtr.Zero, "open", Releases, "", null, 1);
                        return IntPtr.Zero;
                    }
                    if (id == IDCANCELB)
                    {
                        _cancel = true;
                        if (_phase == 3 || _phase == 9) { DestroyWindow(h); }
                        else { SetText(_hStatus, T("正在取消…", "Cancelling…")); }
                        return IntPtr.Zero;
                    }
                    return IntPtr.Zero;
                case WM_CLOSE:
                    _cancel = true;
                    DestroyWindow(h);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return DefWindowProcW(h, m, wp, lp);
        }

        // ------------------------------------------------------------ 干活线程
        private static void Work()
        {
            try
            {
                using var http = MakeHttp();
                _version = T("正在查询最新版本…", "Checking the latest release…");
                Post(WM_UPDATE);
                string json;
                try { json = http.GetStringAsync(ApiLatest).GetAwaiter().GetResult(); }
                catch
                {
                    json = http.GetStringAsync(ApiList).GetAwaiter().GetResult();
                    int cut = json.IndexOf("\"assets\"");
                    if (cut > 0) { json = json.Substring(0, cut + 4000); }
                    if (!json.Contains("tag_name")) { throw new HttpRequestException("no release"); }
                }
                if (_cancel) { return; }
                string tag = M(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"") ?? "?";
                string name = M(json, "\"name\"\\s*:\\s*\"([^\"]+)\"") ?? tag;
                _version = name + "   (" + tag + ")";
                _status = T("正在挑选下载文件…", "Choosing the download…");
                Post(WM_UPDATE);

                var list = new System.Collections.Generic.List<(string n, string u)>();
                foreach (Match mm in Regex.Matches(json,
                         "\"name\"\\s*:\\s*\"([^\"]+)\"[^}]*?\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
                {
                    list.Add((mm.Groups[1].Value, mm.Groups[2].Value));
                }
                if (list.Count == 0) { throw new Exception(T("这个版本没有可下载的文件 ✗", "This release has no assets")); }
                var pick = list.Find(a => a.n.Equals("WARDEN-Setup.exe", StringComparison.OrdinalIgnoreCase));
                if (pick.n == null) { pick = list.Find(a => a.n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)); }
                if (pick.n == null) { pick = list[0]; }

                string tmp = Path.Combine(Path.GetTempPath(), "WARDEN-" + tag.Replace('/', '_') + "-" + pick.n);
                _status = T("正在下载…", "Downloading…");
                Post(WM_UPDATE);
                using (var resp = http.GetAsync(pick.u, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 0;
                    using var src = resp.Content.ReadAsStream();
                    using var dst = File.Create(tmp);
                    var buf = new byte[65536];
                    long got = 0;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (_cancel) { return; }
                        dst.Write(buf, 0, n);
                        got += n;
                        _percent = total > 0 ? (int)(got * 100 / total) : 0;
                        _status = T("正在下载… ", "Downloading… ") + _percent + "%   ("
                                  + (got / 1048576.0).ToString("0.0") + " / "
                                  + (total / 1048576.0).ToString("0.0") + " MB)";
                        Post(WM_UPDATE);
                    }
                }
                _file = tmp;
                _percent = 100;
                _status = T("正在校验 SHA256…", "Verifying SHA256…");
                Post(WM_UPDATE);

                var sums = list.Find(a => a.n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
                if (sums.u == null)
                {
                    _err = T("★ 这个版本没有 SHA256SUMS.txt → 为安全起见不自动运行 ✗\n文件留着：", 
                             "★ No SHA256SUMS.txt -> not running it automatically. File kept at: ") + tmp;
                    _phase = 9;
                    Post(WM_DONE);
                    return;
                }
                string st = http.GetStringAsync(sums.u).GetAwaiter().GetResult();
                string want = "";
                foreach (string line in st.Split('\n'))
                {
                    if (line.Trim().EndsWith(pick.n, StringComparison.OrdinalIgnoreCase))
                    {
                        want = line.Trim().Split(' ')[0].ToLowerInvariant();
                        break;
                    }
                }
                using var sha = SHA256.Create();
                using var rd = File.OpenRead(tmp);
                string got2 = BitConverter.ToString(sha.ComputeHash(rd)).Replace("-", "").ToLowerInvariant();
                if (want.Length == 0 || want != got2)
                {
                    _err = T("★★ 校验不通过 → 【拒绝运行】✗ 文件可能被篡改\n期望 ", "★★ Verification failed -> refusing to run\nExpected ")
                           + want.Substring(0, Math.Min(16, want.Length)) + "…   "
                           + T("实际 ", "actual ") + got2.Substring(0, 16) + "…\n"
                           + T("文件留着：", "File kept at: ") + tmp;
                    _phase = 9;
                    Post(WM_DONE);
                    return;
                }
                _phase = 3;
                _status = T("✓ 校验通过", "✓ Verified");
                Post(WM_DONE);
            }
            catch (Exception e)
            {
                _phase = 9;
                _err = T("★ 出错：", "★ Error: ") + e.Message + "\n\n"
                     + T("可能原因：没联网 / 需要代理 / 还没发布过版本 ✓\n手动下载：",
                         "Possible causes: offline / proxy needed / nothing released yet.\nDownload: ") + Releases;
                Post(WM_DONE);
            }
        }

        private static void Post(uint m) => PostMessageW(_w, m, IntPtr.Zero, IntPtr.Zero);

        private static HttpClient MakeHttp()
        {
            var h = new HttpClientHandler();
            if (!string.IsNullOrEmpty(_proxy)) { h.Proxy = new WebProxy(_proxy); h.UseProxy = true; }
            var http = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WARDEN-Online-Setup/" + Version);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        private static string? M(string t, string p)
        {
            var m = Regex.Match(t, p);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
