// ============================================================================
//  warden-cli（B4，2026-10-10）
// ============================================================================
//  【用途】给脚本/自动化/排障用的小命令行：
//     warden-cli status      看它有没有在跑 + 最近几条日志
//     warden-cli tail [n]    打印主日志最近 n 行（默认 30）
//     warden-cli stop        写【带合法令牌】的停止请求，请它干净退出 ✓
//     warden-cli rules       打印当前规则（从配置里读）
//     warden-cli report      导出 Markdown 日志报告到桌面（= B8 的命令行版）
//     warden-cli version     版本
//  参数：--data <目录>  指定数据目录（便携版/测试用 ✓）
//
//  【为什么值得做】
//    托盘菜单只能人点 ✗；批量部署、自动化测试、远程排障都需要命令行 ✓
//    （A52A 测试里我就是用 Python 拼这些东西的 —— 有 CLI 会干净得多 ✓）
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using WardenSetup;   // ★ 复用安装器的 StopToken（同一份令牌逻辑）✓

namespace WardenCli
{
    internal static class Program
    {
        private static string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");

        private static int Main(string[] args)
        {
            string? cmd = null;
            int n = 30;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--data" && i + 1 < args.Length)
                {
                    DataDir = args[++i];
                    continue;
                }
                if (cmd == null)
                {
                    cmd = args[i].ToLowerInvariant();
                    continue;
                }
                int.TryParse(args[i], out n);
            }

            // 便携版：exe 同目录有 WARDEN.portable → 用 <exe目录>\data ✓
            try
            {
                string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
                if (File.Exists(Path.Combine(exeDir, "WARDEN.portable")))
                {
                    DataDir = Path.Combine(exeDir, "data");
                }
            }
            catch { }

            switch (cmd)
            {
                case "status": return Status();
                case "tail": return Tail(n);
                case "stop": return Stop();
                case "rules": return Rules();
                case "report": return Report();
                case "version":
                    Console.WriteLine("WARDEN 0.1.0-beta.2 (AGPL-3.0-or-later)");
                    Console.WriteLine("数据目录: " + DataDir);
                    return 0;
                default:
                    Console.WriteLine("warden-cli —— WARDEN 的命令行工具");
                    Console.WriteLine();
                    Console.WriteLine("  warden-cli status       看有没有在跑 + 最近日志");
                    Console.WriteLine("  warden-cli tail [n]     打印主日志最近 n 行（默认 30）");
                    Console.WriteLine("  warden-cli stop         写合法令牌，请 WARDEN 干净退出 ✓");
                    Console.WriteLine("  warden-cli rules        打印当前规则");
                    Console.WriteLine("  warden-cli report       导出 Markdown 日志报告到桌面");
                    Console.WriteLine("  warden-cli version      版本");
                    Console.WriteLine();
                    Console.WriteLine("  可选：--data <目录>  指定数据目录");
                    return cmd == null ? 0 : 1;
            }
        }

        private static string LogPath => Path.Combine(DataDir, "WARDEN.log");

        private static int Status()
        {
            Process[] ps = Process.GetProcessesByName("WARDEN");
            Console.WriteLine(ps.Length > 0 ? "运行中    pid=" + ps[0].Id : "未运行");
            Console.WriteLine("数据目录  " + DataDir);
            string lockFile = Path.Combine(DataDir, "WARDEN.lock");
            if (File.Exists(lockFile))
            {
                Console.WriteLine("锁文件    " + File.ReadAllText(lockFile).Trim());
            }
            foreach (string f in new[] { "EMERGENCY.log", "TAMPER.log" })
            {
                string p = Path.Combine(DataDir, f);
                Console.WriteLine(f.PadRight(14) + (File.Exists(p)
                    ? "有（" + new FileInfo(p).Length + " 字节）" : "无"));
            }
            Console.WriteLine();
            Console.WriteLine("最近日志：");
            return TailLines(8);
        }

        private static int Tail(int n) => TailLines(n);

        private static int TailLines(int n)
        {
            if (!File.Exists(LogPath))
            {
                Console.WriteLine("(没有日志文件：" + LogPath + ")");
                return 1;
            }
            string[] all = File.ReadAllLines(LogPath, Encoding.UTF8);
            int from = Math.Max(0, all.Length - Math.Max(1, n));
            for (int i = from; i < all.Length; i++)
            {
                Console.WriteLine(all[i]);
            }
            return 0;
        }

        private static int Stop()
        {
            string stop = Path.Combine(DataDir, "WARDEN.stop");
            Directory.CreateDirectory(DataDir);
            if (!StopToken.WriteRequest(DataDir, stop, s => Console.WriteLine(s)))
            {
                Console.WriteLine("★ 停止令牌生成失败");
                return 2;
            }
            Console.WriteLine("已写停止请求：" + stop);
            for (int i = 0; i < 20; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (Process.GetProcessesByName("WARDEN").Length == 0)
                {
                    Console.WriteLine("WARDEN 已退出 ✓");
                    return 0;
                }
            }
            Console.WriteLine("★ 等了 10 秒还没退出（可能卡住了）");
            return 3;
        }

        private static int Rules()
        {
            string cfg = Path.Combine(DataDir, "WARDEN.json");
            if (!File.Exists(cfg))
            {
                Console.WriteLine("(没有配置文件：" + cfg + ")");
                return 1;
            }
            // 不做完整反序列化：直接找 rules 段里的名字/进程，够排障用 ✓
            string text = File.ReadAllText(cfg, Encoding.UTF8);
            int i = text.IndexOf("\"rules\"", StringComparison.Ordinal);
            if (i < 0)
            {
                Console.WriteLine("(配置里没有 rules 段)");
                return 1;
            }
            int end = text.IndexOf(']', i);
            string seg = end > i ? text.Substring(i, end - i) : text.Substring(i);
            int count = 0;
            foreach (string line in seg.Split('\n'))
            {
                string s = line.Trim();
                if (s.StartsWith("\"name\"", StringComparison.Ordinal)
                    || s.StartsWith("\"process\"", StringComparison.Ordinal)
                    || s.StartsWith("\"enabled\"", StringComparison.Ordinal))
                {
                    Console.WriteLine("  " + s.TrimEnd(','));
                    if (s.StartsWith("\"name\"", StringComparison.Ordinal)) { count++; }
                }
            }
            Console.WriteLine("共 " + count + " 条规则");
            return 0;
        }

        private static int Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# WARDEN 日志报告（warden-cli 生成）");
            sb.AppendLine();
            sb.AppendLine("* 时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("* 数据目录：`" + DataDir + "`");
            sb.AppendLine();
            foreach (string f in new[] { "WARDEN.log", "EMERGENCY.log", "TAMPER.log" })
            {
                string p = Path.Combine(DataDir, f);
                sb.AppendLine("## " + f);
                sb.AppendLine();
                if (!File.Exists(p))
                {
                    sb.AppendLine("（无此文件）");
                    sb.AppendLine();
                    continue;
                }
                string[] all = File.ReadAllLines(p, Encoding.UTF8);
                sb.AppendLine("共 " + all.Length + " 行。关键行：");
                sb.AppendLine();
                sb.AppendLine("```");
                int shown = 0;
                foreach (string l in all)
                {
                    if (l.Contains("篡改") || l.Contains("TAMPER") || l.Contains("应急")
                        || l.Contains("[enter]") || l.Contains("[freeze") || l.Contains("[kill")
                        || l.Contains("★"))
                    {
                        sb.AppendLine(l);
                        if (++shown >= 150) { break; }
                    }
                }
                if (shown == 0) { sb.AppendLine("（没有关键行 ✓）"); }
                sb.AppendLine("```");
                sb.AppendLine();
            }
            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "WARDEN-日志报告-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine("已导出：" + outPath);
            return 0;
        }
    }
}
