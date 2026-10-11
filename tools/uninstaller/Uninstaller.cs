// ============================================================================
//  WARDEN 卸载器（事故后重写，2026-10-10）
// ============================================================================
//  【设计要点（都是踩过坑总结出来的）】
//   ① 先请 WARDEN 自己退出：
//        · 写【带合法令牌】的 .stop（StopToken）→ 它自己干净退出 ✓
//        · 卡住不动的旧版本 → 不强杀，只提示用户手动退出 ✗
//          （我们自己就是反强杀的工具，卸载器更不该到处乱杀 ✗）
//   ② 【默认保留用户数据】（配置/日志/证据）✗ 不删
//        · 用户说得很清楚：卸载不等于清除痕迹
//        · 要删得自己勾选（而且要二次确认 ✓）
//   ③ 快捷方式、注册表项、开机启动 都要清干净 ✓
//   ④ 全程写日志（setup.log 同一处），卸载失败也要留下证据 ✓
//
//  命令行：
//    Uninstaller.exe [--silent] [--keep-config] [--purge-data]
//    --silent      不弹界面直接卸（自动化测试用）
//    --keep-config 保留数据（默认行为；显式写出来便于脚本阅读）
//    --purge-data  连数据目录一起删（危险，需二次确认；静默下必须显式给）
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

using WardenSetup;   // ★ 复用安装器里的停止令牌实现 ✓

namespace WardenUninstall
{
    internal static class Program
    {
        private static readonly string InstallDir = AppContext.BaseDirectory.TrimEnd('\\');
        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");
        private static readonly string LogPath = Path.Combine(DataDir, "setup.log");
        private static readonly StringBuilder LogBuf = new StringBuilder();

        public static void Log(string s)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  [卸载] " + s;
            LogBuf.AppendLine(line);
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(LogPath, line + Environment.NewLine,
                                   new UTF8Encoding(false));   // ★ 无 BOM ✓
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            bool silent = false, purge = false;
            foreach (string a in args)
            {
                if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase)) { silent = true; }
                if (a.Equals("--purge-data", StringComparison.OrdinalIgnoreCase)) { purge = true; }
            }

            Log("================ WARDEN 卸载开始 ================");
            Log("安装目录: " + InstallDir);
            Log("数据目录: " + DataDir + (purge ? "（将删除）" : "（保留）"));

            if (!silent)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // 0.2.6 修（实测出来的真 bug，0.2.5 存在）：
                //   Ask 的返回值是「用户是否确认卸载」，不是「是否删除数据」
                //   原来直接赋给 purge，导致不勾选复选框也会删除配置与日志
                bool confirmed = UninstallForm.Ask(purge);
                if (!confirmed || UninstallForm.Cancelled)
                {
                    Log("用户取消卸载");
                    return;
                }
                purge = UninstallForm.Purge;   // 数据删不删，只由复选框说了算
                Log("用户确认卸载；删除数据=" + purge + "（来自复选框）");
            }

            bool ok = Run(purge);
            Log(ok ? "卸载完成 ✓" : "卸载过程中有失败项（见上）✗");

            if (!silent)
            {
                MessageBox.Show(ok
                        ? "WARDEN 已卸载。\n\n" + (purge
                            ? "配置与日志也已删除。"
                            : "你的配置与日志【保留】在：\n" + DataDir + "\n（要一起删就再运行一次并勾选删除）")
                        : "卸载完成，但有部分文件没删掉（可能还在运行）。\n详情见：\n" + LogPath,
                    "WARDEN 卸载", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            Environment.Exit(ok ? 0 : 4);
        }

        private static bool Run(bool purge)
        {
            bool allOk = true;

            // ---- ① 请 WARDEN 自己退出 ----
            if (Process.GetProcessesByName("WARDEN").Length > 0)
            {
                Log("WARDEN 正在运行 → 写合法令牌请它退出");
                try
                {
                    string stop = Path.Combine(DataDir, "WARDEN.stop");
                    Directory.CreateDirectory(DataDir);
                    if (StopToken.WriteRequest(DataDir, stop, Log))
                    {
                        for (int i = 0; i < 20 && Process.GetProcessesByName("WARDEN").Length > 0; i++)
                        {
                            System.Threading.Thread.Sleep(500);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log("★ 写停止请求失败: " + e.Message);
                }
                if (Process.GetProcessesByName("WARDEN").Length > 0)
                {
                    Log("★ WARDEN 仍在运行 —— 不强行结束它，请先手动退出再卸载 ✗");
                    return false;
                }
                Log("WARDEN 已退出 ✓");
            }

            // ---- ② 清快捷方式 ----
            string sm = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "WARDEN");
            TryDeleteDir(sm, "开始菜单文件夹", ref allOk);

            string desk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "WARDEN.lnk");
            TryDeleteFile(desk, "桌面快捷方式", ref allOk);
            TryDeleteFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "WARDEN 红队自测.lnk"), "红队自测快捷方式", ref allOk);

            // ---- ③ 清注册表（卸载入口 + 开机启动）----
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WARDEN",
                    throwOnMissingSubKey: false);
                using RegistryKey run = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
                run?.DeleteValue("WARDEN", throwOnMissingValue: false);
                Log("注册表项已清理 ✓");
            }
            catch (Exception e)
            {
                Log("★ 清注册表失败: " + e.Message);
                allOk = false;
            }

            // ---- ④ 删程序文件（退到上一级再删自己这一层，避免"正在使用中"✗）----
            string parent = Path.GetDirectoryName(InstallDir) ?? "";
            if (Path.GetFileName(InstallDir).Equals("WARDEN", StringComparison.OrdinalIgnoreCase)
                && parent.Length > 3)
            {
                // 用 cmd 延迟删除：先退回去，等本进程退出后再删目录 ✓
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + InstallDir + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
                Log("已安排删除程序目录（等本进程退出后执行）✓");
            }
            else
            {
                Log("★ 安装目录看着不像 WARDEN 的目录，拒绝自动删除：" + InstallDir + " ✗");
                allOk = false;
            }

            // ---- ⑤ 数据目录：默认保留 ✓ ----
            if (purge)
            {
                TryDeleteDir(DataDir, "数据目录（用户已选择删除）", ref allOk);
            }
            else
            {
                Log("数据目录【保留】✓ " + DataDir);
            }

            return allOk;
        }

        private static void TryDeleteFile(string path, string what, ref bool ok)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); Log(what + " 已删除 ✓"); }
            }
            catch (Exception e)
            {
                Log("★ 删 " + what + " 失败: " + e.Message);
                ok = false;
            }
        }

        private static void TryDeleteDir(string path, string what, ref bool ok)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    Log(what + " 已删除 ✓");
                }
            }
            catch (Exception e)
            {
                Log("★ 删 " + what + " 失败: " + e.Message);
                ok = false;
            }
        }
    }
}
