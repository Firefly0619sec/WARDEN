// ============================================================================
//  WARDEN 日志查看器（事故后重写，2026-10-10）
//  2026-10-11 · Beta 3 / 0.3.2：去掉 WinForms ✗ 改原生 Win32 + Native AOT ✓
//    界面 → LogViewForm.cs（原生控件）/ LogViewWin32.cs（公共层）
//    本文件只留【数据与算法】：数据目录解析 / 读日志 / 过滤 / 统计 / 导出报告
//    ★ ResolveDataDir / PathOf / Reload / ContainsKey / ExportReport 的算法与文案一字未改 ✓
// ============================================================================
//  【和"打开日志文件夹"的区别】
//    托盘菜单以前是拿资源管理器打开目录 ✗ → 用户还得自己翻文件、自己找重点 ✗
//    这个工具直接帮你看：分类、过滤、只挑关键行、一键导出报告 ✓
//    （用户 2026-10-10 的反馈就是这么提的 ✓）
//
//  【看哪几个文件】
//    WARDEN.log   主日志（启停、拦截、状态）
//    EMERGENCY.log 应急压制证据（进应急、冻结/强杀、退出）
//    TAMPER.log   被干扰/被攻击的证据
//    setup.log    安装/卸载日志
//
//  【导出报告 = B8】
//    一键生成 Markdown：环境 + 关键行摘要 + 各标签计数 ✓
//    以后要发到 GitHub 或贴给别人看，直接复制这一份 ✓
// ============================================================================

using System;
using System.IO;
using System.Text;

namespace WardenLogView
{
    internal static class LogView
    {
        // ★ B3 便携版：exe 同目录有 WARDEN.portable 就用 <exe目录>\data ✓
        internal static readonly string DataDir = ResolveDataDir();

        private static string ResolveDataDir()
        {
            try
            {
                // ★ 0.3.2 验收钩子：设了 WARDEN_LOGVIEW_DATADIR 就用它 ✓
                //   （不设此变量的生产环境完全无影响 ✓ 验收时用它指向 %TEMP% 下的假日志 ✓
                //     铁律：绝不允许写坏真实 %LOCALAPPDATA%\WARDEN ✓）
                string? forced = Environment.GetEnvironmentVariable("WARDEN_LOGVIEW_DATADIR");
                if (!string.IsNullOrEmpty(forced))
                {
                    return forced;
                }
            }
            catch { }
            try
            {
                string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
                if (File.Exists(Path.Combine(exeDir, "WARDEN.portable")))
                {
                    return Path.Combine(exeDir, "data");
                }
            }
            catch { }
            return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");
        }

        /// <summary>★ 关键行的判据（用户最需要一眼看到的那些）✓</summary>
        private static readonly string[] KeyWords =
        {
            "篡改", "TAMPER", "应急", "EMERGENCY", "冻结", "强杀", "KILL", "FREEZE",
            "拒绝", "失败", "错误", "异常", "★", "⚠", "aborted", "FAIL",
        };

        /// <summary>当前选中的日志文件名（原生窗口 Reload 时写入 ✓）</summary>
        internal static string Current = "";

        internal static string PathOf(string name) => Path.Combine(DataDir, name);

        internal static bool ContainsKey(string line)
        {
            foreach (string k in KeyWords)
            {
                if (line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>★ B8：导出 Markdown 报告（环境 + 关键行 + 计数）✓</summary>
        internal static void ExportReport(IntPtr owner)
        {
            string name = Current.Length > 0 ? Current : "WARDEN.log";
            var sb = new StringBuilder();
            sb.AppendLine("# WARDEN 日志报告");
            sb.AppendLine();
            sb.AppendLine("* 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("* 数据目录：`" + DataDir + "`");
            sb.AppendLine("* 主日志：" + (File.Exists(PathOf("WARDEN.log")) ? "有" : "无"));
            sb.AppendLine("* 应急证据：" + (File.Exists(PathOf("EMERGENCY.log")) ? "有" : "无"));
            sb.AppendLine("* 被攻击记录：" + (File.Exists(PathOf("TAMPER.log")) ? "有" : "无"));
            sb.AppendLine();

            foreach (string f in new[] { "WARDEN.log", "EMERGENCY.log", "TAMPER.log" })
            {
                string p = PathOf(f);
                if (!File.Exists(p))
                {
                    continue;
                }
                string[] all;
                try { all = File.ReadAllLines(p, Encoding.UTF8); } catch { continue; }
                sb.AppendLine("## " + f + "（" + all.Length + " 行）");
                sb.AppendLine();
                sb.AppendLine("```");
                int shown = 0;
                foreach (string l in all)
                {
                    if (ContainsKey(l))
                    {
                        sb.AppendLine(l);
                        if (++shown >= 200)
                        {
                            sb.AppendLine("（只取前 200 条关键行，完整内容见上面的数据目录）");
                            break;
                        }
                    }
                }
                if (shown == 0)
                {
                    sb.AppendLine("（没有关键行 —— 一切正常 ✓）");
                }
                sb.AppendLine("```");
                sb.AppendLine();
            }

            string defName = "WARDEN-日志报告-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md";
            // ★ 规格第 9 条：「导出报告」→ 弹保存对话框（SaveFileDialog ✓ 原生 comdlg32 ✓）
            //   旧版这里直接写桌面 ✗ 对齐规格：先让用户选路径 ✓ 用户取消就什么都不写 ✓
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string? outPath = LogViewWin32.SaveFile(owner, "导出 WARDEN 日志报告",
                "Markdown\0*.md\0所有文件\0*.*\0\0", "md", defName, desktop);
            if (outPath == null) { LogViewWin32.L("导出报告：用户在保存对话框取消 ✓ 什么都没写 ✓"); return; }
            try
            {
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                LogViewWin32.L("导出报告成功：" + outPath + "（" + sb.Length + " 字符，当前文件 " + name + "）");

                // ★ 0.3.6（CP4）：同时导出一份【脱敏版】✓
                //   为什么另出一份而不是直接脱敏原报告：
                //     原报告是取证用的 ✗ 脱敏后就丢了证据价值 ✓
                //     而用户拿去找人看/发帖时又需要不带 PII 的版本 ✓
                //     → 两份都给 ✓ 用户自己选哪份外发 ✓（不动原算法与原文案 ✓）
                string? dir2 = Path.GetDirectoryName(outPath);
                if (string.IsNullOrEmpty(dir2)) { dir2 = desktop; }
                string maskedPath = Path.Combine(dir2!,
                    Path.GetFileNameWithoutExtension(outPath) + ".masked.md");
                string extra = "";
                try
                {
                    int hits;
                    string masked = RedactText(sb.ToString(), out hits);
                    File.WriteAllText(maskedPath, masked, new UTF8Encoding(false));
                    extra = "\n\n另外生成了一份脱敏版：\n" + Path.GetFileName(maskedPath)
                          + "\n（替换掉 " + hits + " 处个人信息 ✓ 外发用这份 ✓）";
                    LogViewWin32.L("已同时导出脱敏版：" + maskedPath + "（替换 " + hits + " 处 ✓）");
                }
                catch (Exception me)
                {
                    LogViewWin32.L("脱敏版导出失败（不影响原报告 ✓）：" + me.Message);
                }

                LogViewWin32.Msg(owner, "报告已导出：\n\n" + Path.GetFileName(outPath) + extra,
                                "WARDEN 日志查看器", true);
            }
            catch (Exception e)
            {
                LogViewWin32.L("导出报告失败：" + e.Message);
                LogViewWin32.Msg(owner, "导出失败：" + e.Message, "WARDEN 日志查看器", false);
            }
        }

        /// <summary>
        /// ★ 0.3.6（CP4）：把报告里的个人可识别信息（PII）替换掉。
        ///
        ///   覆盖这四类（都是这份报告里真实会出现的 ✓）：
        ///     ① 用户目录路径   C:\Users\wei20\...   → C:\Users\&lt;用户&gt;\...
        ///     ② WARDEN 数据目录 %LOCALAPPDATA%\WARDEN\... → &lt;WARDEN数据目录&gt;\...
        ///     ③ 当前用户名 wei20 / 机器名 DESKTOP-XXXX → &lt;用户&gt; / &lt;机器名&gt;
        ///     ④ 标题类字段值   标题=xxx / 窗口名=xxx / title: xxx → &lt;已脱敏&gt;
        ///
        ///   ★ 诚实的边界：不保证覆盖所有 PII ✗
        ///     （例如窗口标题里被人写进日志的其它自由文本、命令行参数 ✓）
        ///     所以它是"外发前降低暴露面"✓ 不是"保证匿名"✗
        /// </summary>
        internal static string RedactText(string text, out int count)
        {
            int n = 0;
            string s = text;

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dataDirPath = DataDir;
            string user = Environment.UserName;
            string machine = Environment.MachineName;

            // 长串先替换（否则 C:\Users\wei20 会被后面的用户名单独替换掉一部分 ✓）
            s = ReplaceCounted(s, profile, @"C:\Users\<用户>", ref n);
            if (!string.IsNullOrEmpty(dataDirPath)) { s = ReplaceCounted(s, dataDirPath, "<WARDEN数据目录>", ref n); }
            if (!string.IsNullOrEmpty(user)) { s = ReplaceCounted(s, user, "<用户>", ref n); }
            if (!string.IsNullOrEmpty(machine)) { s = ReplaceCounted(s, machine, "<机器名>", ref n); }

            // 标题类字段
            s = System.Text.RegularExpressions.Regex.Replace(s,
                @"(?m)^(\s*[^\r\n]*?(?:标题|窗口名|title)\s*[=:]\s*)(.+?)(\s{2,}|$)",
                m =>
                {
                    n++;
                    return m.Groups[1].Value + "<已脱敏>" + m.Groups[3].Value;
                });

            count = n;
            return s;
        }

        /// <summary>替换并计数（不区分大小写 ✓ 循环替换直到没有 ✓）</summary>
        private static string ReplaceCounted(string s, string from, string to, ref int count)
        {
            if (string.IsNullOrEmpty(from)) { return s; }
            int idx = s.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                s = s.Substring(0, idx) + to + s.Substring(idx + from.Length);
                count++;
                idx = s.IndexOf(from, idx + to.Length, StringComparison.OrdinalIgnoreCase);
            }
            return s;
        }

        private static void Main()
        {
            LogViewForm.Run();
        }
    }
}
