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
                LogViewWin32.Msg(owner, "报告已导出：\n\n" + Path.GetFileName(outPath),
                                "WARDEN 日志查看器", true);
            }
            catch (Exception e)
            {
                LogViewWin32.L("导出报告失败：" + e.Message);
                LogViewWin32.Msg(owner, "导出失败：" + e.Message, "WARDEN 日志查看器", false);
            }
        }

        private static void Main()
        {
            LogViewForm.Run();
        }
    }
}
