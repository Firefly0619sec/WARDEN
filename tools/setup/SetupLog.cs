// ============================================================================
//  安装程序日志（A34 顺手补上）
// ============================================================================
//  【为什么要写文件】
//      安装器的日志原来只显示在界面上，窗口一关就没了 ✗
//      用户说"装不上"的时候，我们手上一点证据都没有 ✗
//      用户要求"工作留痕" → 安装这种关键动作必须落盘 ✓
//
//  【写在哪】
//      %LOCALAPPDATA%\WARDEN\setup.log
//      和主程序的数据目录同一处：用户找得到，我们排查时也找得到 ✓
//
//  【为什么日志是英文、界面是中文】（项目约定）
//      日志是给我们排查用的，短标记更好扫 ✓
//      界面是给用户看的，必须说人话 ✓
//
//  【★ 一条硬规矩】
//      写日志失败【绝对不能】影响安装 ✗
//      （数字弱势群体的机器上权限怪事很多，不能因为日志把安装搞崩）
//      → 每个入口都吞异常，但吞得心里有数：写不进去就是不写，不报错 ✓
// ============================================================================

using System;
using System.IO;
using System.Text;

namespace WardenSetup
{
    internal static class SetupLog
    {
        private static readonly object Gate = new object();

        /// <summary>★ 日志文件（%LOCALAPPDATA%\WARDEN\setup.log）。</summary>
        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WARDEN", "setup.log");

        /// <summary>★ 超过这个大小就轮转一次（别让它无限长大）。</summary>
        private const long MaxBytes = 512 * 1024;

        public static void Line(string tag, string text)
        {
            try
            {
                lock (Gate)
                {
                    string dir = Path.GetDirectoryName(FilePath) ?? ".";
                    Directory.CreateDirectory(dir);

                    // ★ 轮转：旧的改名成 setup.log.1，只留一代
                    try
                    {
                        var fi = new FileInfo(FilePath);
                        if (fi.Exists && fi.Length > MaxBytes)
                        {
                            string old = FilePath + ".1";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(FilePath, old);
                        }
                    }
                    catch
                    {
                        // 轮转失败就继续往原文件追加 ✓ 不能因此丢失日志
                    }

                    var sb = new StringBuilder();
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    sb.Append("  [").Append(tag).Append("] ").Append(text);
                    File.AppendAllText(FilePath,
                        sb.ToString() + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // ★ 见文件头：日志失败不影响安装
            }
        }

        /// <summary>★ 分隔行（一条明显的横线，方便人肉定位"这一次安装"）。</summary>
        public static void Begin(string title)
        {
            Line("====", "================ " + title + " ================");
            Line("====", "version=" + Program.Version
                + "  os=" + Environment.OSVersion.VersionString
                + "  clr=" + Environment.Version
                + "  exe=" + Environment.ProcessPath);
        }
    }
}
