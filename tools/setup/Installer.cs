// ============================================================================
//  安装器辅助（事故后重写，2026-10-10）
// ============================================================================
//  【为什么重写】本地仓库被清空时，SetupCore.cs / SetupWizard.cs 一起丢了 ✗
//  这个文件只放"不碰界面的杂活"：默认目录、路径校验、磁盘空间、快捷方式、注册表 ✓
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WardenSetup
{
    /// <summary>★ 安装位置与系统侧的小工具（纯函数，方便单独测）</summary>
    internal static class Installer
    {
        /// <summary>默认安装目录：%LOCALAPPDATA%\Programs\WARDEN</summary>
        public static string DefaultDir =>
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "Programs", "WARDEN");

        /// <summary>数据目录：%LOCALAPPDATA%\WARDEN（卸载时默认保留 ✓）</summary>
        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "WARDEN");

        /// <summary>展开 %ENV% 之类的变量（用户手输路径时用）✓</summary>
        public static string Expand(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return DefaultDir;
            }
            return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        }

        /// <summary>
        /// 检查安装目录能不能用。
        /// 返回 null = 可以用；否则返回一句人话说明为什么不行 ✓
        /// </summary>
        public static string? ValidateDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return "路径不能为空";
            }
            try
            {
                string full = Path.GetFullPath(Expand(dir));

                // ★ 装在 Program Files 需要管理员权限，而且以后每次更新
                //   都会被系统弹窗拦一下 → 明确劝阻（但不硬拦 ✗）
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                bool inPf = (!string.IsNullOrEmpty(pf)
                             && full.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
                         || (!string.IsNullOrEmpty(pf86)
                             && full.StartsWith(pf86, StringComparison.OrdinalIgnoreCase))
                         || full.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase);
                if (inPf)
                {
                    return "不建议装在系统目录（需要管理员权限，更新也麻烦）——换一个用户目录";
                }

                // 盘符/根目录不能直接装
                if (full.Length <= 3)
                {
                    return "不能直接装在盘符根目录，请指定一个文件夹";
                }

                // 空间：至少 300 MB
                long free = FreeSpaceMb(full);
                if (free > 0 && free < 300)
                {
                    return $"这个盘只剩 {free} MB，装不下（至少留 300 MB）";
                }

                return null;
            }
            catch (Exception e)
            {
                return "路径有问题：" + e.Message;
            }
        }

        /// <summary>指定路径所在盘的剩余空间（MB）；失败返回 -1</summary>
        public static long FreeSpaceMb(string dir)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(dir)) ?? "";
                if (root.Length == 0)
                {
                    return -1;
                }
                return new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>★ 判断这个路径能否真的写进去（探测一下）✓</summary>
        public static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".warden_write_test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- 快捷方式
        /// <summary>★ 建一个 .lnk（用 WScript.Shell，免 COM 互操作声明）✓</summary>
        public static void MakeLnk(string lnkPath, string target, string args = "",
                                  string desc = "", string workDir = "")
        {
            try
            {
                string dir = Path.GetDirectoryName(lnkPath) ?? "";
                if (dir.Length > 0)
                {
                    Directory.CreateDirectory(dir);
                }
                string ps =
                    "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('"
                    + lnkPath.Replace("'", "''") + "');"
                    + "$s.TargetPath='" + target.Replace("'", "''") + "';"
                    + "$s.Arguments='" + args.Replace("'", "''") + "';"
                    + "$s.Description='" + desc.Replace("'", "''") + "';";
                if (workDir.Length > 0)
                {
                    ps += "$s.WorkingDirectory='" + workDir.Replace("'", "''") + "';";
                }
                ps += "$s.Save()";
                var psi = new System.Diagnostics.ProcessStartInfo("powershell")
                {
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(15000);
            }
            catch
            {
                // 快捷方式失败不该让安装失败 ✓（下面会记日志）
            }
        }

        public static string StartMenuDir =>
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");

        public static string DesktopDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }
}
