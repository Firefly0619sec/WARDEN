// ============================================================================
//  WARDEN 帮助中心 —— 主程序 / 界面与逻辑契约（2026-10-11 · Beta 3 / 0.3.3）
//  文件：HelpCore.cs（原来这部分写在 HelpForm.cs 的 Program 类与 FeedbackForm 里）
//
//  【为什么拆出来】
//    界面已经改成原生 Win32（HelpWin32.cs ✓）——WinForms 不支持 Native AOT ✗
//    但「章节表」「反馈报告怎么拼」这些是【逻辑与文案】，与用哪套控件无关 ✓
//    拆到这个文件后：原文案与原文案顺序一个字都没动 ✓ 只是不再挂在 Form 上 ✓
//
//  【不变的部分】
//    · Version 字符串、仓库硬编码（★ 防配置被改引到恶意仓库）
//    · 十个章节的顺序（开源许可在最前 ✓ 常用问题在最后 ✓）
//    · 反馈报告 Markdown 的拼法（逐行对齐旧版 BuildReport ✓）
// ============================================================================

using System;
using System.Text;
using WardenLang;

namespace WardenHelp
{
    /// <summary>章节：目录里的一行 + 右边正文。</summary>
    internal sealed class HelpSection
    {
        public string Title = "";
        public string Body = "";
    }

    internal static class Program
    {
        internal const string Version = "0.3.3";
        internal const string AppName = "WARDEN";

        /// <summary>★ 反馈提交地址（仓库硬编码，防止被改配置引到恶意仓库）。</summary>
        internal const string RepoOwner = "Firefly0619sec";
        internal const string RepoName = "WARDEN";

        private static HelpSection[] _sections;

        /// <summary>
        /// ★ 教程内容全部来自语言包（翻译者改语言包就能出外语版）。
        ///   顺序与旧版 HelpForm 的 _sections 逐条一致 ✓
        /// </summary>
        internal static HelpSection[] Sections
        {
            get
            {
                if (_sections != null) { return _sections; }
                _sections = new[]
                {
                    // ★ 开源许可（用户 2026-10-11 要求：应用内也要有声明）
                    new HelpSection { Title = WardenLang.Lang.T("help.sec.license"),
                                      Body  = WardenLang.Lang.T("help.sec.license.body") },
                    new HelpSection { Title = Lang.T("help.sec.quick.title"),     Body = Lang.T("help.sec.quick.body") },
                    new HelpSection { Title = Lang.T("help.sec.tray.title"),      Body = Lang.T("help.sec.tray.body") },
                    new HelpSection { Title = Lang.T("help.sec.blocked.title"),   Body = Lang.T("help.sec.blocked.body") },
                    new HelpSection { Title = Lang.T("help.sec.dryrun.title"),    Body = Lang.T("help.sec.dryrun.body") },
                    new HelpSection { Title = Lang.T("help.sec.rules.title"),     Body = Lang.T("help.sec.rules.body") },
                    new HelpSection { Title = Lang.T("help.sec.safety.title"),    Body = Lang.T("help.sec.safety.body") },
                    new HelpSection { Title = Lang.T("help.sec.update.title"),    Body = Lang.T("help.sec.update.body") },
                    new HelpSection { Title = Lang.T("help.sec.uninstall.title"), Body = Lang.T("help.sec.uninstall.body") },
                    new HelpSection { Title = Lang.T("help.sec.faq.title"),       Body = Lang.T("help.sec.faq.body") },
                };
                return _sections;
            }
        }

        private static void Main(string[] args)
        {
            bool feedback = false;
            foreach (var a in args)
            {
                if (a.Equals("--feedback", StringComparison.OrdinalIgnoreCase)) { feedback = true; }
            }

            try
            {
                // 全部界面都在 HelpWin32 里（原生 Win32 + GDI ✓ 无 WinForms ✓ 可 Native AOT ✓）
                HelpWin32.Run(feedback);
            }
            catch (Exception ex)
            {
                HelpWin32.Fatal("启动失败：" + Environment.NewLine + Environment.NewLine + ex.ToString());
            }
        }
    }

    // ========================================================================
    //  反馈报告怎么拼 —— 与旧版 FeedbackForm.BuildReport 逐行一致 ✓
    // ========================================================================
    //  【★ 反馈页的关键安全设计】
    //    ✗ 绝对不能在客户端里内嵌 GitHub Token（exe 会被反编译 ✗）
    //    ✓ 把反馈内容拼成【预填好的 GitHub issue 链接】，用用户自己的浏览器打开
    //    ✓ 没有 GitHub 账号的萌新：一键复制 / 一键存文件，发给分发者代发
    // ========================================================================
    internal static class HelpCore
    {
        internal static string BuildReport(string kindText, string contact, string detail, bool withDiag)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### " + Lang.T("fb.r.kind") + "：" + kindText);

            string c = (contact ?? "").Trim();
            if (c.Length > 0)
            {
                sb.AppendLine("### " + Lang.T("fb.r.contact") + "：" + c);
            }

            string d = (detail ?? "").Trim();
            sb.AppendLine();
            sb.AppendLine("### " + Lang.T("fb.r.detail"));
            sb.AppendLine();
            sb.AppendLine(d.Length > 0 ? d : "（这里填你遇到的情况）");

            if (withDiag)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("### " + Lang.T("fb.r.diag"));
                sb.AppendLine();
                sb.AppendLine(Diagnostics.Build());
            }
            return sb.ToString();
        }
    }
}
