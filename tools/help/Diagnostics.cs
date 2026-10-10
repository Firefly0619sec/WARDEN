// ============================================================================
//  诊断信息收集
// ============================================================================
//  【为什么要它】
//      用户原话："你可以塞一个用于分析日志的程序包" + "要加一个反馈页"
//      反馈时最有用的不是"我这儿坏了"，而是"版本 + 系统 + 日志摘要"
//      -> 自动带上，开发者不用反复问 ✓
//
//  【★ 隐私】
//      只收集：版本号、系统版本、配置摘要、日志统计与最近若干行
//      ★ 不收集：用户名、路径里的用户名会被打码、IP、任何硬件信息
//      ★ 而且【你不点提交，它哪里也不去】✓
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Text;

using WardenLang;
namespace WardenHelp
{
    internal static class Diagnostics
    {
        private static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WARDEN");

        /// <summary>★ 把路径里的用户名打码（隐私）。</summary>
        private static string Scrub(string s)
        {
            try
            {
                string user = Environment.UserName;
                if (user.Length > 1) s = s.Replace(user, "***");
                string profile = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
                if (profile.Length > 3) s = s.Replace(profile, "%USERPROFILE%");
            }
            catch { }
            return s;
        }

        public static string Build()
        {
            var sb = new StringBuilder();

            sb.AppendLine("**" + Lang.T("diag.env") + "**");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("WARDEN-Help  " + Program.Version);
            sb.AppendLine("OS           " + Environment.OSVersion.VersionString);
            sb.AppendLine("64-bit       " + Environment.Is64BitOperatingSystem);
            sb.AppendLine("CLR          " + Environment.Version);
            sb.AppendLine("界面语言     " + WardenLang.Lang.Code
                          + " (" + WardenLang.Lang.Name + ")");
            sb.AppendLine("语言包加载   " + (WardenLang.Lang.Loaded ? "成功" : "用了内置兜底表"));
            sb.AppendLine("```");
            sb.AppendLine();

            // ---- 装了哪几个组件 ----
            sb.AppendLine("**" + Lang.T("diag.files") + "**");
            sb.AppendLine();
            sb.AppendLine("```");
            string dir = AppContext.BaseDirectory;
            foreach (var exe in new[]
            {
                "WARDEN.exe", "WARDEN-Settings.exe", "WARDEN-LogView.exe",
                "WARDEN-Updater.exe", "WARDEN-Uninstaller.exe", "WARDEN-Help.exe",
            })
            {
                string p = Path.Combine(dir, exe);
                if (File.Exists(p))
                {
                    var fi = new FileInfo(p);
                    sb.AppendLine(string.Format("{0,-24} {1,9:N0} B  {2:yyyy-MM-dd HH:mm}",
                        exe, fi.Length, fi.LastWriteTime));
                }
                else
                {
                    // ★ 缺组件要说出来 —— 正常安装时是强制安装的
                    sb.AppendLine(string.Format("{0,-24} ★缺失", exe));
                }
            }
            sb.AppendLine("```");
            sb.AppendLine();

            // ---- 配置摘要 ----
            sb.AppendLine("**" + Lang.T("diag.config") + "**");
            sb.AppendLine();
            string cfg = Path.Combine(DataDir, "WARDEN.json");
            if (File.Exists(cfg))
            {
                sb.AppendLine("```");
                sb.AppendLine("路径 " + Scrub(cfg));
                sb.AppendLine("大小 " + new FileInfo(cfg).Length + " B");
                try
                {
                    string text = File.ReadAllText(cfg, Encoding.UTF8);
                    // ★ 只统计，不整份粘贴（配置里可能有用户的规则，属于隐私）
                    sb.AppendLine("规则条数 " + Count(text, "\"title_contains\"")
                                  + " / 正则 " + Count(text, "\"title_regex\""));
                    sb.AppendLine("白名单条数 " + Count(text, "\".exe\""));
                }
                catch (Exception e) { sb.AppendLine("读取失败 " + e.Message); }
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine("（还没有配置文件 —— WARDEN 没运行过）");
            }
            sb.AppendLine();

            // ---- 日志统计 ----
            sb.AppendLine("**" + Lang.T("diag.log") + "**");
            sb.AppendLine();
            string log = Path.Combine(DataDir, "WARDEN.log");
            if (File.Exists(log))
            {
                try
                {
                    // ★ FileShare.ReadWrite：WARDEN 可能正在写这个文件
                    string[] lines;
                    using (var fs = new FileStream(log, FileMode.Open,
                               FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        lines = sr.ReadToEnd().Split((char)10);
                    }
                    string[] tags = { "L3-KILL", "KILL-DENIED", "KILL-ESCALATED",
                                      "GUARD", "CONSENT-OK", "ERROR", "SELFCHECK" };
                    sb.AppendLine("```");
                    sb.AppendLine("总行数 " + lines.Length);
                    foreach (var t in tags)
                    {
                        int n = lines.Count(l => l.Contains("[" + t + "]"));
                        if (n > 0) sb.AppendLine(string.Format("{0,-16} {1}", t, n));
                    }
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine("---- 最近 30 行 ----");
                    foreach (var l in lines.Skip(Math.Max(0, lines.Length - 31)))
                    {
                        if (l.Trim().Length > 0) sb.AppendLine(Scrub(l.Trim()));
                    }
                    sb.AppendLine("```");
                }
                catch (Exception e)
                {
                    sb.AppendLine("读取日志失败：" + e.Message);
                }
            }
            else
            {
                sb.AppendLine("（还没有日志文件）");
            }

            return sb.ToString();
        }

        private static int Count(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                n++; i += needle.Length;
            }
            return n;
        }
    }
}
