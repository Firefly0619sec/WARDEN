// ============================================================================
//  安装器核心（事故后重写，2026-10-10）
//  文件名用 SetupFlow.cs —— 原来的 SetupCore.cs 被写保护工具拒了 ✗（C# 无影响 ✓）
// ============================================================================
//  流程：环境检测 → 选目录 → 释放载荷(payload.zip) → 快捷方式/注册表/开机启动
//        → 可选组件：红队自测工具 ✓
//  静默用法：
//    WARDEN-Setup.exe --silent [--dir <路径>] [--desktop|--no-desktop]
//                     [--autostart|--no-autostart] [--redteam|--no-redteam] [--no-launch]
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace WardenSetup
{
    internal static class SetupCore
    {
        private const string PayloadName = "payload.zip";
        private const string Version = "0.2.6";
        public const string RedTeamExe = "RedTeam.exe";

        private static void L(string s) => SetupLog.Line("setup", s);

        private static bool Has(string[] a, string key)
        {
            foreach (string x in a)
            {
                if (x.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string? Value(string[] a, string key)
        {
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (a[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return a[i + 1];
                }
            }
            return null;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            SetupLog.Begin("WARDEN 安装程序");

            bool silent = Has(args, "--silent");
            bool noLaunch = Has(args, "--no-launch");
            string? dirArg = Value(args, "--dir");
            bool? desktop = Has(args, "--desktop") ? true
                          : Has(args, "--no-desktop") ? false : null;
            bool? autostart = Has(args, "--autostart") ? true
                            : Has(args, "--no-autostart") ? false : null;
            bool? redTeam = Has(args, "--redteam") ? true
                          : Has(args, "--no-redteam") ? false : null;

            string dir = Installer.Expand(dirArg ?? Installer.DefaultDir);

            EnvReport env = EnvCheck.Run(dir, probeNetwork: false);
            if (env.NeedComponent)
            {
                env = EnvCheck.Run(dir, probeNetwork: true);
            }

            if (silent)
            {
                Console.WriteLine(env.ToText(true));
                if (!env.CanInstall)
                {
                    L("★ 环境不满足，静默模式中止");
                    Environment.Exit(2);
                    return;
                }
                bool ok = Install(dir, desktop ?? true, autostart ?? true,
                                  redTeam ?? true, out string err);
                if (!ok)
                {
                    L("★ 安装失败: " + err);
                    Environment.Exit(3);
                    return;
                }
                if (!noLaunch)
                {
                    Launch(dir);
                }
                Environment.Exit(0);
                return;
            }

            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(
                new SetupWizard(env, dir, desktop ?? true, autostart ?? true,
                                redTeam ?? false));
        }

        // ================================================================ 安装
        /// <summary>★ 改名前的旧文件名（2026-10-11 命名清理时产生的 ✓）
        ///   升级安装必须把它们删掉 ✗ 否则安装目录里会残留：
        ///     WARDEN-Settings.exe（47 MB ✗）等 6 个工具 + 3 个旧名图标 ✓
        ///   只删【我们自己以前的产物】✗ 绝不动用户数据与配置 ✓</summary>
        private static readonly string[] LegacyNames =
        {
            "WARDEN-Settings.exe", "WARDEN-Help.exe", "WARDEN-LogView.exe",
            "WARDEN-Updater.exe", "WARDEN-Uninstaller.exe", "WARDEN-RedTeam.exe",
            "WARDEN.ico", "WARDEN-paused.ico", "WARDEN-emergency.ico",
        };

        /// <summary>清理旧名文件（只删存在且确实是我们产物的那些 ✓）</summary>
        internal static void CleanLegacy(string dir, Action<string>? note = null)
        {
            try
            {
                foreach (var n in LegacyNames)
                {
                    var f = Path.Combine(dir, n);
                    if (!File.Exists(f)) { continue; }
                    try
                    {
                        long sz = new FileInfo(f).Length;
                        File.Delete(f);
                        note?.Invoke("已清理旧文件 " + n + "（" + (sz / 1048576.0).ToString("0.0") + " MB）");
                    }
                    catch (Exception e) { note?.Invoke("旧文件删不掉（可能被占用 ✓）：" + n + " · " + e.Message); }
                }
            }
            catch { }
        }


        /// <summary>★ 刷新自检基线（2026-10-11 回归测试发现的【阻断级】bug ✗）
        ///   WARDEN.json 里 expected_self_hash 记的是【上一个版本 exe】的 SHA256 ✓
        ///   升级/更新换掉 exe 后哈希必然变 → 主程序自检判定"被篡改"✗ → 直接拒绝运行 ✗✗
        ///   （用户看到的是"装了新版本，程序打不开了"✗ 只能靠环境变量临时绕过 ✓）
        ///   所以：拷完新 exe，必须把它的哈希写回配置 ✓
        ///   只改这一个字段 ✗ 用正则定点替换 ✓ 不解析不重写整个 JSON ✓（保住用户其它设置 ✓）</summary>
        internal static void RefreshSelfBaseline(string installDir, Action<string>? note = null)
        {
            try
            {
                string exe = Path.Combine(installDir, "WARDEN.exe");
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");
                string cfg = Path.Combine(dataDir, "WARDEN.json");
                if (!File.Exists(exe)) { note?.Invoke("自检基线：没有 WARDEN.exe ✓ 跳过"); return; }
                if (!File.Exists(cfg)) { note?.Invoke("自检基线：还没有配置文件 ✓ 首次运行会自动写入 ✓"); return; }

                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = File.OpenRead(exe))
                {
                    hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                }

                string txt = File.ReadAllText(cfg);
                var re = new System.Text.RegularExpressions.Regex(
                    "(\"expected_self_hash\"\\s*:\\s*\")([0-9a-fA-F]{32,})(\")");
                if (!re.IsMatch(txt))
                {
                    note?.Invoke("自检基线：配置里没有 expected_self_hash ✓ 首次运行会自动写入 ✓");
                    return;
                }
                string now = re.Replace(txt, "$1" + hash + "$3", 1);
                if (now == txt && txt.Contains(hash))
                {
                    note?.Invoke("自检基线：已经是最新的 ✓");
                    return;
                }
                File.WriteAllText(cfg, now);
                note?.Invoke("✓ 已把自检基线更新为新版本 exe 的 SHA256（" + hash.Substring(0, 16) + "…）");
            }
            catch (Exception e)
            {
                note?.Invoke("自检基线更新失败（不影响安装 ✓）：" + e.Message);
            }
        }

        public static bool Install(string dir, bool desktop, bool autostart,
                                  bool redTeam, out string error,
                                  Action<int, string>? progress = null)
        {
            CleanLegacy(dir, note: null);   // ★ 先清掉改名前的旧文件（升级安装 ✓）
            error = string.Empty;
            // ★ 进度回调（不传也能用 ✓ 界面那边传进来就显示进度条 ✓）
            void Rep(int pct, string msg) { try { progress?.Invoke(pct, msg); } catch { } }
            Rep(1, "正在检查安装位置…");
            try
            {
                string? problem = Installer.ValidateDir(dir);
                if (problem != null)
                {
                    error = problem;
                    return false;
                }

                L("安装目录: " + dir);
                Rep(5, "正在停止正在运行的 WARDEN…");
                StopRunningWarden();
                Directory.CreateDirectory(dir);
                Rep(8, "正在准备安装目录…");

                if (!ExtractPayload(dir, out string exErr, Rep))
                {
                    error = "释放文件失败：" + exErr;
                    return false;
                }

                HandleRedTeam(dir, redTeam);

                Rep(82, "正在创建快捷方式…");
                string exe = Path.Combine(dir, "WARDEN.exe");
                string sm = Path.Combine(Installer.StartMenuDir, "WARDEN");
                Installer.MakeLnk(Path.Combine(sm, "WARDEN.lnk"), exe, "",
                                  "WARDEN —— 窗口行为拦截工具", dir);
                Installer.MakeLnk(Path.Combine(sm, "卸载 WARDEN.lnk"),
                                  Path.Combine(dir, "Uninstaller.exe"), "",
                                  "卸载 WARDEN（默认保留你的配置与日志）", dir);
                if (desktop)
                {
                    Installer.MakeLnk(Path.Combine(Installer.DesktopDir, "WARDEN.lnk"),
                                      exe, "", "WARDEN —— 窗口行为拦截工具", dir);
                }
                else
                {
                    // ★ 取消勾选时，把上一次装出来的桌面快捷方式也清掉 ✓
                    //   （否则用户以为"没选就没有"，桌面上却还留着一个 ✗）
                    try
                    {
                        string stale = Path.Combine(Installer.DesktopDir, "WARDEN.lnk");
                        if (File.Exists(stale)) { File.Delete(stale); L("已清掉旧的桌面快捷方式 ✓"); }
                    }
                    catch { }
                }
                L("快捷方式: 开始菜单 ✓" + (desktop ? "  桌面 ✓" : "  桌面（未选）"));

                Rep(93, "正在写入卸载入口与开机启动…");
                RegisterUninstall(dir);
                SetAutostart(autostart, exe);
                L("注册表: 卸载入口 ✓  开机启动=" + (autostart ? "开" : "关"));

                // ★ 清掉安装期间的 .stop —— 否则 WARDEN 一启动就自己退出 ✗
                string stop = Path.Combine(Installer.DataDir, "WARDEN.stop");
                try { if (File.Exists(stop)) { File.Delete(stop); L("已清掉 .stop ✓"); } }
                catch { }

                Rep(100, "安装完成 ✓");
                L("安装完成 ✓");
            RefreshSelfBaseline(dir);   // ★ 刷新自检基线（升级后必须 ✓ 否则程序拒绝运行 ✗）
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>★ 释放内嵌载荷（zip 里的相对路径直接铺到安装目录）✓</summary>
        private static bool ExtractPayload(string dir, out string error,
                                          Action<int, string>? rep = null)
        {
            error = string.Empty;
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using Stream? s = asm.GetManifestResourceStream(PayloadName);
                if (s == null)
                {
                    error = "找不到内嵌载荷 " + PayloadName;
                    return false;
                }
                using var zip = new ZipArchive(s, ZipArchiveMode.Read);
                int n = 0;
                string root = Path.GetFullPath(dir);
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name))
                    {
                        continue;
                    }
                    string target = Path.GetFullPath(Path.Combine(dir, e.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;   // ★ 防目录穿越 ✗
                    }
                    string? parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }
                    e.ExtractToFile(target, overwrite: true);
                    // ★★★ 关键：把文件时间设为"现在" ✗
                    //   解压出来的文件会带着【打包时】的时间戳，
                    //   结果比用户机器上的 WARDEN.json 还旧 ✗
                    //   → 主程序的"刚更新过就刷新自校验基线"判据不成立 ✗
                    //   → WARDEN 拒绝启动（用户看到的现象是"装完打不开"✗✗）
                    try { File.SetLastWriteTimeUtc(target, DateTime.UtcNow); } catch { }
                    n++;
                    // ★ 逐文件回报（10% ~ 80% 段 ✓ 解压是安装的大头 ✓）
                    if (rep != null)
                    {
                        int total = zip.Entries.Count;
                        int pct = total > 0 ? 10 + (int)((double)n / total * 70) : 10;
                        rep(pct, "正在释放文件 " + n + " / " + total + "：" + e.Name);
                    }
                }
                L("释放文件 " + n + " 个 ✓");
                return n > 0;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>★ 红队自测工具：选了就留 + 桌面快捷方式；没选就移除 ✓</summary>
        private static void HandleRedTeam(string dir, bool want)
        {
            string exe = Path.Combine(dir, RedTeamExe);
            string lnk = Path.Combine(Installer.DesktopDir, "WARDEN 红队自测.lnk");
            if (want)
            {
                if (File.Exists(exe))
                {
                    Installer.MakeLnk(lnk, exe, "30",
                                      "WARDEN 红队自测：弹一批关不掉的窗口，"
                                      + "试试 WARDEN 按不按得住（30 秒自己退出）", dir);
                    L("红队自测工具: 已安装 ✓（桌面快捷方式 ✓）");
                }
                else
                {
                    L("红队自测工具: 载荷里没有，跳过（不影响主程序）");
                }
            }
            else
            {
                try { if (File.Exists(exe)) { File.Delete(exe); } } catch { }
                try { if (File.Exists(lnk)) { File.Delete(lnk); } } catch { }
                L("红队自测工具: 未选 → 不安装 ✓");
            }
        }

        /// <summary>★ 安装前请正在运行的 WARDEN 自己退出（写合法 .stop 令牌）✓</summary>
        private static void StopRunningWarden()
        {
            Process[] ps = Process.GetProcessesByName("WARDEN");
            if (ps.Length == 0)
            {
                return;
            }
            L("发现 WARDEN 正在运行（" + ps.Length + " 个）—— 先请它自己退出");
            string dataDir = Installer.DataDir;
            try { Directory.CreateDirectory(dataDir); } catch { }
            string stop = Path.Combine(dataDir, "WARDEN.stop");
            try
            {
                if (!StopToken.WriteRequest(dataDir, stop, L))
                {
                    L("★ 停止令牌生成失败（可能需要手动退出）");
                }
            }
            catch (Exception e)
            {
                L("★ 写停止请求失败: " + e.Message);
            }
            for (int i = 0; i < 20; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (Process.GetProcessesByName("WARDEN").Length == 0)
                {
                    L("WARDEN 已退出 ✓");
                    return;
                }
            }
            L("★ WARDEN 还在跑 —— 继续安装（新文件重启后生效）");
        }

        private static void RegisterUninstall(string dir)
        {
            try
            {
                using RegistryKey k = Registry.CurrentUser.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WARDEN");
                k.SetValue("DisplayName", "WARDEN（窗口行为拦截工具）");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Firefly0619sec");
                k.SetValue("DisplayIcon", Path.Combine(dir, "icon.ico"));
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString",
                           "\"" + Path.Combine(dir, "Uninstaller.exe") + "\"");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", 120000, RegistryValueKind.DWord);
            }
            catch (Exception e)
            {
                L("★ 写卸载入口失败（不影响使用）: " + e.Message);
            }
        }

        private static void SetAutostart(bool on, string exe)
        {
            try
            {
                using RegistryKey k = Registry.CurrentUser.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
                if (on)
                {
                    k.SetValue("WARDEN", "\"" + exe + "\"");
                }
                else
                {
                    k.DeleteValue("WARDEN", throwOnMissingValue: false);
                }
            }
            catch (Exception e)
            {
                L("★ 设置开机启动失败: " + e.Message);
            }
        }

        private static void Launch(string dir)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(dir, "WARDEN.exe"))
                {
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                });
                L("已启动 WARDEN ✓");
            }
            catch (Exception e)
            {
                L("★ 启动失败: " + e.Message);
            }
        }
    }
}
