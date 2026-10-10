// ============================================================================
//  WARDEN 在线安装器（A33，Beta 2）
// ============================================================================
//  【它和离线安装包的分工】
//    离线包 WARDEN-Setup.exe：几十 MB ✗ 里面塞着完整程序 ✓ 适合没网/转发给别人
//    这个在线安装器：只有 1~2 MB ✓ 去 GitHub 拉最新版再交给离线包安装 ✓
//
//  【流程】
//    ① 查最新 release（GitHub API；支持 --proxy 或环境变量 HTTPS_PROXY ✓）
//    ② 选资产：优先 WARDEN-Setup.exe ✓ 否则第一个 .exe ✓
//    ③ 下载到临时目录 + 校验 SHA256（对照 SHA256SUMS.txt ✓）
//    ④ 交给离线安装包安装（用户自己点向导 ✓ 不静默 ✗）
//
//  【★ 安全】哈希不对就【拒绝运行】✗ 并明确告诉用户哪里不对 ✓
//  【★ 不联网收集任何东西】只做上面四步 ✓
//  【用法】
//    WARDEN-Online-Setup.exe [--check] [--proxy http://127.0.0.1:10808] [--yes]
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WardenOnlineSetup
{
    internal static class Program
    {
        private const string Owner = "Firefly0619sec";
        private const string Repo = "WARDEN";
        private const string Version = "0.1.0-beta.2";
        private static readonly string ApiLatest =
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        private static bool _en;

        private static string T(string zh, string eng) => _en ? eng : zh;

        private static async Task<int> Main(string[] args)
        {
            _en = !System.Globalization.CultureInfo.CurrentUICulture
                        .Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            bool checkOnly = false, assumeYes = false;
            string? proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--check": checkOnly = true; break;
                    case "--yes": assumeYes = true; break;
                    case "--proxy":
                        if (i + 1 < args.Length) { proxy = args[++i]; }
                        break;
                    case "-h":
                    case "--help":
                        Console.WriteLine("WARDEN " + Version + " —— " +
                            T("在线安装器：从 GitHub 拉最新版安装", "online installer: installs the latest release"));
                        Console.WriteLine();
                        Console.WriteLine("  --check             " + T("只检查有没有新版本", "check only"));
                        Console.WriteLine("  --proxy <url>       " + T("走代理，例如 http://127.0.0.1:10808",
                                                                        "use a proxy, e.g. http://127.0.0.1:10808"));
                        Console.WriteLine("  --yes               " + T("不再确认，直接下载安装", "skip the confirmation"));
                        Console.WriteLine();
                        Console.WriteLine("  " + T("离线安装包（不需要网络）:",
                                                    "Offline installer (no network needed):"));
                        Console.WriteLine($"  https://github.com/{Owner}/{Repo}/releases");
                        Console.WriteLine();
                        Console.WriteLine("  协议: AGPL-3.0-or-later");
                        return 0;
                    case "--version":
                        Console.WriteLine("WARDEN 在线安装器 " + Version);
                        return 0;
                }
            }

            Console.WriteLine("WARDEN " + T("在线安装器", "online installer") + " " + Version);
            Console.WriteLine(T("仓库: ", "repo: ") + $"https://github.com/{Owner}/{Repo}");
            if (!string.IsNullOrEmpty(proxy))
            {
                Console.WriteLine(T("代理: ", "proxy: ") + proxy);
            }
            Console.WriteLine();

            using var handler = new HttpClientHandler();
            if (!string.IsNullOrEmpty(proxy))
            {
                handler.Proxy = new WebProxy(proxy);
                handler.UseProxy = true;
            }
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WARDEN-Online-Setup/" + Version);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            // ---- ① 查最新 release ----
            string json;
            try
            {
                Console.WriteLine(T("正在查询最新版本…", "Checking the latest release…"));
                json = await http.GetStringAsync(ApiLatest);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine();
                Console.WriteLine(T("★ 查不到发布信息：", "★ Could not reach the release info: ") + ex.Message);
                Console.WriteLine(T("  可能原因：没联网 ✗ 需要代理 ✗ 或仓库还没发布过版本 ✗",
                                    "  Possible causes: offline, a proxy is required, "
                                    + "or nothing has been released yet"));
                Console.WriteLine();
                Console.WriteLine(T("你可以手动下载：", "Download manually: "));
                Console.WriteLine($"  https://github.com/{Owner}/{Repo}/releases");
                return 2;
            }

            string tag = Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"") ?? "?";
            string rname = Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"") ?? tag;
            Console.WriteLine(T("最新版本: ", "latest: ") + rname + "  (" + tag + ")");
            if (checkOnly)
            {
                Console.WriteLine(T("✓ 只检查（--check）→ 不下载", "✓ check only (--check) → nothing downloaded"));
                return 0;
            }

            // ---- ② 挑资产 ----
            var assets = new System.Collections.Generic.List<(string name, string url, long size)>();
            foreach (Match m in Regex.Matches(json,
                     "\"name\"\\s*:\\s*\"([^\"]+)\"[^}]*?\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                assets.Add((m.Groups[1].Value, m.Groups[2].Value, 0));
            }
            if (assets.Count == 0)
            {
                Console.WriteLine(T("★ 这个版本没有可下载的文件 ✗", "★ This release has no downloadable assets"));
                return 2;
            }
            var pick = assets.Find(a => a.name.Equals("WARDEN-Setup.exe", StringComparison.OrdinalIgnoreCase));
            if (pick.name == null)
            {
                pick = assets.Find(a => a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            }
            if (pick.name == null)
            {
                pick = assets[0];
            }
            Console.WriteLine(T("将下载: ", "will download: ") + pick.name);
            Console.WriteLine(T("  地址: ", "  url: ") + pick.url);

            if (!assumeYes)
            {
                Console.Write(T("继续吗？(Y/n) ", "Continue? (Y/n) "));
                string? k = Console.ReadLine();
                if (k != null && k.Trim().Length > 0
                    && !k.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase)
                    && !k.Trim().StartsWith("Y", StringComparison.Ordinal))
                {
                    Console.WriteLine(T("已取消 ✓", "Cancelled ✓"));
                    return 0;
                }
            }

            // ---- ③ 下载 + 校验 ----
            string tmp = Path.Combine(Path.GetTempPath(),
                "WARDEN-" + tag.Replace('/', '_') + "-" + pick.name);
            try
            {
                Console.WriteLine(T("正在下载…", "Downloading…"));
                using (var resp = await http.GetAsync(pick.url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using var fs = File.Create(tmp);
                    await resp.Content.CopyToAsync(fs);
                }
                var fi = new FileInfo(tmp);
                Console.WriteLine(T("  完成: ", "  done: ") + Math.Round(fi.Length / 1048576.0, 1) + " MB");
            }
            catch (Exception ex)
            {
                Console.WriteLine(T("★ 下载失败: ", "★ Download failed: ") + ex.Message);
                return 3;
            }

            string? sumsUrl = assets.Find(a =>
                a.name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)).url;
            if (sumsUrl == null)
            {
                Console.WriteLine(T("★ 这个版本没有提供 SHA256SUMS.txt → 无法校验 ✗",
                                    "★ No SHA256SUMS.txt in this release → cannot verify"));
                Console.WriteLine(T("  为安全起见不自动运行 ✗ 文件留在这里，你可以自己检查：",
                                    "  Not running it automatically. The file is here if you want to check it:"));
                Console.WriteLine("  " + tmp);
                return 4;
            }
            string sums;
            try { sums = await http.GetStringAsync(sumsUrl); }
            catch (Exception ex)
            {
                Console.WriteLine(T("★ 取校验文件失败: ", "★ Could not fetch the checksum file: ") + ex.Message);
                return 4;
            }
            string want = "";
            foreach (string line in sums.Split('\n'))
            {
                if (line.Trim().EndsWith(pick.name, StringComparison.OrdinalIgnoreCase))
                {
                    want = line.Trim().Split(' ')[0].ToLowerInvariant();
                    break;
                }
            }
            using var sha = SHA256.Create();
            using var rd = File.OpenRead(tmp);
            string got = BitConverter.ToString(sha.ComputeHash(rd)).Replace("-", "").ToLowerInvariant();
            Console.WriteLine(T("  期望哈希: ", "  expected: ") + (want.Length > 0 ? want : "(未找到)"));
            Console.WriteLine(T("  实际哈希: ", "  actual  : ") + got);
            if (want.Length == 0 || want != got)
            {
                Console.WriteLine();
                Console.WriteLine(T("★★ 校验不通过 → 【拒绝运行】✗（文件可能被篡改）",
                                    "★★ Verification failed → refusing to run (the file may be tampered with)"));
                Console.WriteLine("  " + tmp);
                return 5;
            }
            Console.WriteLine(T("✓ 校验通过 ✓", "✓ verified ✓"));

            // ---- ④ 交给离线安装包 ----
            Console.WriteLine();
            Console.WriteLine(T("即将启动安装向导（下一步由你确认 ✓）",
                                "Starting the setup wizard (you confirm the next step)"));
            try
            {
                var psi = new ProcessStartInfo(tmp) { UseShellExecute = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.WriteLine(T("★ 启动失败: ", "★ Launch failed: ") + ex.Message);
                Console.WriteLine("  " + tmp);
                return 6;
            }
            return 0;
        }

        private static string? Match(string text, string pattern)
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
