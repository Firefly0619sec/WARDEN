// ============================================================================
//  WARDEN 在线安装器 —— 向导式窗口（2026-10-11 第二版）
// ============================================================================
//  【用户的两点要求（都照做了 ✓）】
//    ① 界面参考【离线安装包】（WARDEN-Setup）：蓝色标题栏 + 向导感 ✓
//    ② 体积一定要下来 ✓
//
//  【怎么把体积压下来】
//    · 不用 HttpClient ✗ → 改用系统自带的 curl.exe（Win10 1803+ / Win11 都有 ✓）
//        HttpClient 会拖进 Sockets / TLS / 代理一整条链 ✗ 约 2 MB ✓
//        curl.exe 是系统的 ✗ 我们只是调它 ✓ 自己做进度（轮询临时文件大小 ✓）
//    · InvariantGlobalization=true → 不再打包 ICU ✓（又省约 1 MB ✓
//        而且少一份 Unicode 许可声明 ✓）
//    · 只保留必需：user32 / gdi32 / comctl32 / shell32 / kernel32 + 单个 SHA256 ✓
//    → 目标 1.5~2.5 MB（原 5.5 MB ✓）
//
//  【界面（照离线安装包的观感）】
//    蓝色标题区：WARDEN 在线安装 + 一行说明（白字 ✓）
//    白色正文区：当前状态大字 + 进度条 + 两行提示
//    底部按钮：手动下载 / 取消 / 开始安装（校验通过前灰着 ✗）
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
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
        private const string UA = "WARDEN-Online-Setup/" + Version;

        private static bool _en;
        private static string T(string zh, string en) => _en ? en : zh;

        private static volatile int _percent;
        private static volatile string _status = "", _version = "", _err = "", _proxy = "", _file = "";
        private static volatile int _phase;          // 0 查询 2 下载 3 就绪 9 失败
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
        [DllImport("user32.dll")] private static extern int SetTextColor(IntPtr dc, int color);
        [DllImport("user32.dll")] private static extern uint SetBkColor(IntPtr dc, uint color);
        [DllImport("user32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
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
        private const uint WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_OVERLAPPED = 0;
        private const uint WM_COMMAND = 0x0111, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
        private const uint WM_SETFONT = 0x0030, WM_SETTEXT = 0x000C, WM_CTLCOLORSTATIC = 0x0138;
        private const uint PBM_SETRANGE = 0x0401, PBM_SETPOS = 0x0402;
        private const uint WM_UPDATE = 0x8001, WM_DONE = 0x8002;
        private const uint ICC_PROGRESS = 0x20, ICC_STD = 0x4000;
        private const int SW_SHOW = 5;
        private const int IDOK = 1000, IDCANCELB = 1001, IDMANUAL = 1002;
        private const uint BLUE = 0x00D27800;   // BGR：和离线安装包的标题蓝一致 ✓
        private const int WHITE = 0x00FFFFFF;

        private static IntPtr _w, _hTitle, _hSub, _hVer, _hStatus, _hBar, _hNote, _hOk, _hCancel, _hManual;
        private static IntPtr _blueBrush;
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
                        Console.WriteLine(T("  不带参数双击 = 向导窗口 ✓", "  No arguments = wizard window"));
                        Console.WriteLine("  --check        " + T("只检查新版本", "check only"));
                        Console.WriteLine("  --proxy <url>  " + T("走代理", "use a proxy"));
                        Console.WriteLine("  " + T("离线安装包：", "Offline installer: ") + Releases);
                        return 0;
                }
            }
            if (check)
            {
                string j = Curl(ApiList);
                var m0 = Regex.Match(j, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                Console.WriteLine((m0.Success ? m0.Groups[1].Value : "?") + T("   ← 最新版本", "  <- latest"));
                return m0.Success ? 0 : 2;
            }
            return RunGui();
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
            _blueBrush = CreateSolidBrush(BLUE);

            int w = 720, h = 400;
            _w = CreateWindowExW(0, "WARDENOnlineSetup", T("WARDEN 在线安装", "WARDEN online setup"),
                 WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
                 (GetSystemMetrics(0) - w) / 2, (GetSystemMetrics(1) - h) / 2, w, h,
                 IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_w == IntPtr.Zero)
            {
                MessageBoxW(IntPtr.Zero, T("窗口创建失败 ✗", "Window creation failed"), "WARDEN", 0x10);
                return 3;
            }
            var big = CreateFontW(-24, 0, 0, 0, 700, 0, 0, 0, 134, 0, 0, 0, 0, "Microsoft YaHei UI");
            var mid = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 134, 0, 0, 0, 0, "Microsoft YaHei UI");
            var sml = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, 134, 0, 0, 0, 0, "Microsoft YaHei UI");

            // ★ 蓝色标题区（照离线安装包 ✓）
            _hTitle = Mk("STATIC", "WARDEN " + T("在线安装", "online setup"), 0, 0, w, 46, big);
            _hSub = Mk("STATIC", T("自动获取最新版，下载后先校验 SHA256 再交给安装向导 ✓",
                                   "Fetches the latest release, verifies SHA256, then hands off to the installer"),
                       0, 46, w, 30, mid);

            _hVer = Mk("STATIC", T("正在查询最新版本…", "Checking the latest release…"), 24, 96, w - 48, 34, big);
            _hStatus = Mk("STATIC", T("请稍候 ✓", "Please wait"), 24, 138, w - 48, 26, mid);
            _hBar = Mk("msctls_progress32", "", 24, 172, w - 48, 22, IntPtr.Zero);
            SendMessageW(_hBar, PBM_SETRANGE, IntPtr.Zero, (IntPtr)((100 << 16) | 0));
            _hNote = Mk("STATIC", T("★ 主程序不联网 —— 只有这个窗口会访问 GitHub 一次 ✓\n"
                                  + "★ 校验不通过就【拒绝安装】，不会静默降级 ✗",
                                  "★ The app itself is offline; only this window contacts GitHub.\n"
                                  + "★ If the hash does not match, it refuses to install."),
                        24, 206, w - 48, 50, sml);
            _hManual = Mk("BUTTON", T("手动下载", "Download page"), 24, 300, 130, 34, mid);
            _hCancel = Mk("BUTTON", T("取消", "Cancel"), 424, 300, 110, 34, mid);
            _hOk = Mk("BUTTON", T("开始安装", "Install now"), 544, 300, 140, 38, mid);
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
            if (h != IntPtr.Zero) { SendMessageW(h, WM_SETTEXT, IntPtr.Zero, Marshal.StringToHGlobalUni(s)); }
        }

        private static IntPtr OnMsg(IntPtr h, uint m, IntPtr wp, IntPtr lp)
        {
            switch (m)
            {
                case WM_CTLCOLORSTATIC:
                    // ★ 标题区两行：白字 + 蓝底（其余行保持默认 ✓）
                    if (lp == _hTitle || lp == _hSub)
                    {
                        SetTextColor(wp, WHITE);
                        SetBkColor(wp, BLUE);
                        SetBkMode(wp, 1);   // TRANSPARENT
                        return _blueBrush;
                    }
                    break;
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
                    if (id == IDOK && _file.Length > 0 && File.Exists(_file))
                    {
                        ShellExecuteW(IntPtr.Zero, "open", _file, "", null, 1);
                    }
                    else if (id == IDMANUAL)
                    {
                        ShellExecuteW(IntPtr.Zero, "open", Releases, "", null, 1);
                    }
                    else if (id == IDCANCELB)
                    {
                        _cancel = true;
                        if (_phase == 3 || _phase == 9) { DestroyWindow(h); }
                        else { SetText(_hStatus, T("正在取消…", "Cancelling…")); }
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

        // ------------------------------------------------------------ curl 调用
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
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add("Accept: application/vnd.github+json");
            AddProxy(psi);
            psi.ArgumentList.Add(url);
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(70000);
            return outp;
        }

        private static void AddProxy(ProcessStartInfo psi)
        {
            if (!string.IsNullOrEmpty(_proxy))
            {
                psi.ArgumentList.Add("--proxy");
                psi.ArgumentList.Add(_proxy);
            }
        }

        // ------------------------------------------------------------ 干活
        private static void Work()
        {
            try
            {
                _version = T("正在查询最新版本…", "Checking the latest release…");
                Post(WM_UPDATE);

                string json = Curl(ApiLatest);
                if (!json.Contains("tag_name"))
                {
                    string list = Curl(ApiList);
                    int first = list.IndexOf("\"tag_name\"");
                    int second = first >= 0 ? list.IndexOf("\"tag_name\"", first + 10) : -1;
                    if (first < 0) { throw new Exception(T("查不到发布信息（没联网 / 需要代理 / 还没发布过版本）",
                                                          "Could not read release info (offline / proxy needed / nothing released)")); }
                    json = second > first ? list.Substring(first - 20, second - first + 20) : list.Substring(first - 20);
                }
                if (_cancel) { return; }
                var mt = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                var mn = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                string tag = mt.Success ? mt.Groups[1].Value : "?";
                string name = mn.Success ? mn.Groups[1].Value : tag;
                _version = name + "   (" + tag + ")";
                _status = T("正在挑选下载文件…", "Choosing the download…");
                Post(WM_UPDATE);

                var list2 = new System.Collections.Generic.List<(string n, string u, long sz)>();
                foreach (Match mm in Regex.Matches(json,
                         "\"name\"\\s*:\\s*\"([^\"]+)\"[^}]*?\"size\"\\s*:\\s*(\\d+)[^}]*?\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
                {
                    list2.Add((mm.Groups[1].Value, mm.Groups[3].Value, long.Parse(mm.Groups[2].Value)));
                }
                if (list2.Count == 0) { throw new Exception(T("这个版本没有可下载的文件 ✗", "This release has no assets")); }
                var pick = list2.Find(a => a.n.Equals("WARDEN-Setup.exe", StringComparison.OrdinalIgnoreCase));
                if (pick.n == null) { pick = list2.Find(a => a.n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)); }
                if (pick.n == null) { pick = list2[0]; }

                string tmp = Path.Combine(Path.GetTempPath(), "WARDEN-" + tag.Replace('/', '_') + "-" + pick.n);
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
                _status = T("正在下载…", "Downloading…");
                Post(WM_UPDATE);

                // ★ 用 curl 下载 ✓ 自己轮询文件大小做进度（不再依赖 HttpClient ✓）
                var cp = new ProcessStartInfo("curl.exe")
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                cp.ArgumentList.Add("-L");
                cp.ArgumentList.Add("--fail");
                cp.ArgumentList.Add("--silent");
                cp.ArgumentList.Add("--show-error");
                cp.ArgumentList.Add("-A");
                cp.ArgumentList.Add(UA);
                AddProxy(cp);
                cp.ArgumentList.Add("-o");
                cp.ArgumentList.Add(tmp);
                cp.ArgumentList.Add(pick.u);
                using var proc = Process.Start(cp)!;
                while (!proc.HasExited)
                {
                    if (_cancel) { try { proc.Kill(); } catch { } return; }
                    long got = 0;
                    try { got = new FileInfo(tmp).Length; } catch { }
                    _percent = pick.sz > 0 ? (int)Math.Min(100, got * 100 / pick.sz) : 0;
                    _status = T("正在下载… ", "Downloading… ") + _percent + "%   ("
                              + (got / 1048576.0).ToString("0.0") + " / "
                              + (pick.sz / 1048576.0).ToString("0.0") + " MB)";
                    Post(WM_UPDATE);
                    Thread.Sleep(300);
                }
                if (proc.ExitCode != 0) { throw new Exception(T("下载失败（curl 退出码 ", "Download failed (curl exit ") + proc.ExitCode + ")"); }

                _file = tmp;
                _percent = 100;
                _status = T("正在校验 SHA256…", "Verifying SHA256…");
                Post(WM_UPDATE);

                var sums = list2.Find(a => a.n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
                if (sums.u == null)
                {
                    _err = T("★ 这个版本没有 SHA256SUMS.txt → 为安全起见不自动运行 ✗\n文件留着：",
                             "★ No SHA256SUMS.txt -> not running it automatically. File kept at: ") + tmp;
                    _phase = 9; Post(WM_DONE); return;
                }
                string st = Curl(sums.u);
                string want = "";
                foreach (string line in st.Split('\n'))
                {
                    if (line.Trim().EndsWith(pick.n, StringComparison.OrdinalIgnoreCase))
                    {
                        want = line.Trim().Split(' ')[0].ToLowerInvariant(); break;
                    }
                }
                using var sha = SHA256.Create();
                using var rd = File.OpenRead(tmp);
                string got2 = BitConverter.ToString(sha.ComputeHash(rd)).Replace("-", "").ToLowerInvariant();
                if (want.Length == 0 || want != got2)
                {
                    _err = T("★★ 校验不通过 → 【拒绝运行】✗ 文件可能被篡改\n期望 ", "★★ Verification failed -> refusing to run\nExpected ")
                           + want.Substring(0, Math.Min(16, want.Length)) + "…   " + T("实际 ", "actual ")
                           + got2.Substring(0, 16) + "…\n" + T("文件留着：", "File kept at: ") + tmp;
                    _phase = 9; Post(WM_DONE); return;
                }
                _phase = 3;
                _status = T("✓ 校验通过", "✓ Verified");
                Post(WM_DONE);
            }
            catch (Exception e)
            {
                _phase = 9;
                _err = T("★ 出错：", "★ Error: ") + e.Message + "\n"
                     + T("手动下载：", "Download manually: ") + Releases;
                Post(WM_DONE);
            }
        }

        private static void Post(uint m) => PostMessageW(_w, m, IntPtr.Zero, IntPtr.Zero);
    }
}
