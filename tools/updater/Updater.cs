// ============================================================================
//  WARDEN 更新器（事故后重写，2026-10-10）
// ============================================================================
//  【设计原则】
//    ① 【离线包优先】用户下载一个 zip → 这个工具负责装上去 ✓
//       为什么：WARDEN 本体是"不联网"的（只有这个更新器例外 ✓），
//       而且用户机器上不一定能直连 GitHub ✗（我们自己的机器就是 ✗）
//    ② 装之前【先校验哈希】（zip 同目录下的 <zip名>.sha256 ✓）
//       不校验就覆盖，等于给了别人一个"投毒"的机会 ✗
//    ③ 装之前【先备份】现在的程序目录（压成 zip 放数据目录 ✓）
//       更新失败可以一键回退 ✓ —— 这是用户明确要的"方便回滚" ✓
//    ④ 更新时【不动用户数据】（配置/日志/证据都在 %LOCALAPPDATA%\WARDEN ✓）
//    ⑤ 全程写日志（setup.log ✓）
//
//  【在线检查更新】等 WARDEN 仓库建好后做（F3）✓
//    那时这里会加一个"检查更新"按钮去读 releases 里的 version.txt ✓
//
//  命令行：
//    WARDEN-Updater.exe <更新包.zip> [--silent] [--no-launch]
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using WardenSetup;   // ★ 复用安装器的 StopToken（写合法停止令牌）✓

namespace WardenUpdater
{
    internal static class Program
    {
        private static readonly string InstallDir = AppContext.BaseDirectory.TrimEnd('\\');
        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");
        private static readonly string LogPath = Path.Combine(DataDir, "setup.log");

        public static void Log(string s)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  [更新] " + s;
            Console.WriteLine(line);
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(LogPath, line + Environment.NewLine,
                                   new UTF8Encoding(false));
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            string? zip = null;
            bool silent = false, noLaunch = false;
            foreach (string a in args)
            {
                if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase)) { silent = true; }
                else if (a.Equals("--no-launch", StringComparison.OrdinalIgnoreCase)) { noLaunch = true; }
                else if (!a.StartsWith("--", StringComparison.Ordinal)) { zip = a; }
            }

            Log("================ WARDEN 更新开始 ================");
            Log("程序目录: " + InstallDir);

            if (zip == null)
            {
                if (silent)
                {
                    Log("★ 静默模式必须给更新包路径 ✗");
                    Environment.Exit(2);
                    return;
                }
                Application.EnableVisualStyles();
                using var d = new OpenFileDialog
                {
                    Title = "选择 WARDEN 更新包（zip）",
                    Filter = "WARDEN 更新包 (*.zip)|*.zip|所有文件|*.*",
                };
                if (d.ShowDialog() != DialogResult.OK)
                {
                    Log("用户取消");
                    return;
                }
                zip = d.FileName;
            }

            if (!File.Exists(zip))
            {
                Log("★ 找不到更新包：" + zip);
                Environment.Exit(3);
                return;
            }

            // ---- ① 校验哈希（有 .sha256 就必须对 ✓）
            if (!VerifyHash(zip))
            {
                Log("★★ 哈希校验不通过 → 拒绝更新 ✗（这个包可能被篡改过）");
                Environment.Exit(5);
                return;
            }

            // ---- ② 备份现在的程序目录（方便回滚 ✓）
            string backup = Backup();

            // ---- ③ 请 WARDEN 退出（写合法令牌 ✓）
            if (!StopWarden())
            {
                Log("★ WARDEN 没能退出 → 中止更新（别在半途覆盖文件 ✗）");
                Environment.Exit(6);
                return;
            }

            // ---- ④ 解压覆盖（不碰数据目录 ✓）
            if (!Apply(zip))
            {
                Log("★ 解压失败 —— 现在可以用备份回退：" + backup);
                Environment.Exit(7);
                return;
            }

            Log("更新完成 ✓  备份：" + backup);

            if (!noLaunch)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, "WARDEN.exe"))
                    {
                        WorkingDirectory = InstallDir,
                        UseShellExecute = true,
                    });
                    Log("已重新启动 WARDEN ✓");
                }
                catch (Exception e)
                {
                    Log("★ 启动失败: " + e.Message);
                }
            }

            if (!silent)
            {
                Application.EnableVisualStyles();
                MessageBox.Show(
                    "更新完成 ✓\n\n如果新版有问题，可以回退到这一次的备份：\n" + backup,
                    "WARDEN 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            Environment.Exit(0);
        }

        /// <summary>★ 同名 .sha256 存在就必须校验（格式：一行十六进制）✓</summary>
        private static bool VerifyHash(string zip)
        {
            string sidecar = zip + ".sha256";
            if (!File.Exists(sidecar))
            {
                Log("没有找到 " + Path.GetFileName(sidecar) + " → 跳过哈希校验（建议作者提供 ✓）");
                return true;
            }
            try
            {
                string want = File.ReadAllText(sidecar).Trim().Split(' ', '\t')[0].ToLowerInvariant();
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(zip);
                string got = BitConverter.ToString(sha.ComputeHash(fs))
                                      .Replace("-", "").ToLowerInvariant();
                Log("期望哈希: " + want);
                Log("实际哈希: " + got);
                return want == got;
            }
            catch (Exception e)
            {
                Log("★ 校验出错 → 按不通过处理: " + e.Message);
                return false;
            }
        }

        /// <summary>★ 备份程序目录到数据目录（zip），返回备份路径 ✓</summary>
        private static string Backup()
        {
            string outZip = Path.Combine(DataDir,
                "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            try
            {
                Directory.CreateDirectory(DataDir);
                if (File.Exists(outZip)) { File.Delete(outZip); }
                ZipFile.CreateFromDirectory(InstallDir, outZip,
                    CompressionLevel.Optimal, includeBaseDirectory: false);
                Log("已备份当前程序目录 → " + outZip);
            }
            catch (Exception e)
            {
                Log("★ 备份失败（继续更新，但没有回退点 ✗）: " + e.Message);
                return "(备份失败)";
            }
            return outZip;
        }

        private static bool StopWarden()
        {
            Process[] ps = Process.GetProcessesByName("WARDEN");
            if (ps.Length == 0)
            {
                return true;
            }
            Log("WARDEN 正在运行 → 写合法令牌请它退出");
            try
            {
                string stop = Path.Combine(DataDir, "WARDEN.stop");
                Directory.CreateDirectory(DataDir);
                if (!StopToken.WriteRequest(DataDir, stop, Log))
                {
                    Log("★ 停止令牌生成失败");
                    return false;
                }
            }
            catch (Exception e)
            {
                Log("★ 写停止请求失败: " + e.Message);
                return false;
            }
            for (int i = 0; i < 20; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (Process.GetProcessesByName("WARDEN").Length == 0)
                {
                    Log("WARDEN 已退出 ✓");
                    return true;
                }
            }
            return false;
        }

        private static bool Apply(string zip)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                int n = 0;
                string root = Path.GetFullPath(InstallDir);
                foreach (ZipArchiveEntry e in archive.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name))
                    {
                        continue;
                    }
                    string target = Path.GetFullPath(Path.Combine(InstallDir, e.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("★ 跳过越界条目：" + e.FullName);
                        continue;
                    }
                    string? parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) { Directory.CreateDirectory(parent); }
                    e.ExtractToFile(target, overwrite: true);
                    // ★ 同安装器：时间设为现在，否则主程序的自检自愈不触发 ✗
                    try { File.SetLastWriteTimeUtc(target, DateTime.UtcNow); } catch { }
                    n++;
                }
                Log("已覆盖 " + n + " 个文件 ✓");
                return n > 0;
            }
            catch (Exception e)
            {
                Log("★ 解压失败: " + e.Message);
                return false;
            }
        }
    }
}
