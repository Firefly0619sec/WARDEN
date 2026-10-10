// ============================================================================
//  WARDEN 日志查看器（事故后重写，2026-10-10）
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
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace WardenLogView
{
    internal sealed class LogViewForm : Form
    {
        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");

        /// <summary>★ 关键行的判据（用户最需要一眼看到的那些）✓</summary>
        private static readonly string[] KeyWords =
        {
            "篡改", "TAMPER", "应急", "EMERGENCY", "冻结", "强杀", "KILL", "FREEZE",
            "拒绝", "失败", "错误", "异常", "★", "⚠", "aborted", "FAIL",
        };

        private readonly ComboBox _files = new ComboBox();
        private readonly TextBox _filter = new TextBox();
        private readonly CheckBox _onlyKey = new CheckBox();
        private readonly TextBox _view = new TextBox();
        private readonly Label _stats = new Label();
        private readonly Timer _timer = new Timer { Interval = 3000 };
        private string _current = "";

        public LogViewForm()
        {
            Text = "WARDEN 日志查看器";
            ClientSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            _files.DropDownStyle = ComboBoxStyle.DropDownList;
            _files.SetBounds(12, 12, 200, 26);
            foreach (string n in new[] { "WARDEN.log", "EMERGENCY.log", "TAMPER.log", "setup.log" })
            {
                _files.Items.Add(n);
            }
            _files.SelectedIndex = 0;
            _files.SelectedIndexChanged += (s, e) => Reload();

            var lblFilter = new Label { Text = "过滤：", Bounds = new Rectangle(224, 16, 50, 22) };
            _filter.SetBounds(270, 14, 260, 24);
            _filter.TextChanged += (s, e) => Reload();

            _onlyKey.Text = "只看关键行（篡改/应急/冻结/强杀/失败）";
            _onlyKey.SetBounds(540, 16, 320, 24);
            _onlyKey.CheckedChanged += (s, e) => Reload();

            _view.Multiline = true;
            _view.ReadOnly = true;
            _view.ScrollBars = ScrollBars.Both;
            _view.WordWrap = false;
            _view.Font = new Font("Consolas", 9F);
            _view.SetBounds(12, 48, 876, 480);
            _view.BackColor = Color.White;

            _stats.SetBounds(12, 534, 560, 22);
            _stats.ForeColor = Color.DimGray;

            var bRefresh = new Button { Text = "刷新", Bounds = new Rectangle(586, 532, 70, 28) };
            var bFolder = new Button { Text = "打开文件夹", Bounds = new Rectangle(662, 532, 100, 28) };
            var bReport = new Button { Text = "导出报告", Bounds = new Rectangle(768, 532, 100, 28) };
            bRefresh.Click += (s, e) => Reload();
            bFolder.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", DataDir); } catch { }
            };
            bReport.Click += (s, e) => ExportReport();

            Controls.Add(_files);
            Controls.Add(lblFilter);
            Controls.Add(_filter);
            Controls.Add(_onlyKey);
            Controls.Add(_view);
            Controls.Add(_stats);
            Controls.Add(bRefresh);
            Controls.Add(bFolder);
            Controls.Add(bReport);

            _timer.Tick += (s, e) => { if (Visible) { Reload(); } };
            _timer.Start();
            Reload();
        }

        private string PathOf(string name) => Path.Combine(DataDir, name);

        private void Reload()
        {
            string name = _files.SelectedItem?.ToString() ?? "WARDEN.log";
            string path = PathOf(name);
            _current = name;

            if (!File.Exists(path))
            {
                _view.Text = "(还没有这个日志文件：" + path + ")";
                _stats.Text = name + "：不存在";
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (Exception e)
            {
                _view.Text = "读日志失败：" + e.Message;
                return;
            }

            string needle = _filter.Text.Trim();
            bool onlyKey = _onlyKey.Checked;
            var shown = new List<string>(lines.Length);
            foreach (string l in lines)
            {
                if (needle.Length > 0
                    && l.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (onlyKey && !ContainsKey(l))
                {
                    continue;
                }
                shown.Add(l);
            }

            _view.Text = string.Join(Environment.NewLine, shown);

            // ★ 统计：总行数 / 关键行数 / 前 3 个高频标签 ✓
            int keyCount = 0;
            var tags = new Dictionary<string, int>();
            foreach (string l in lines)
            {
                if (ContainsKey(l)) { keyCount++; }
                int a = l.IndexOf('[');
                int b = a >= 0 ? l.IndexOf(']', a + 1) : -1;
                if (a >= 0 && b > a)
                {
                    string tag = l.Substring(a + 1, Math.Min(24, b - a - 1));
                    tags[tag] = tags.TryGetValue(tag, out int c) ? c + 1 : 1;
                }
            }
            var top = new List<KeyValuePair<string, int>>(tags);
            top.Sort((x, y) => y.Value.CompareTo(x.Value));
            var sb = new StringBuilder();
            sb.Append(name).Append("：共 ").Append(lines.Length).Append(" 行，关键行 ")
              .Append(keyCount).Append(" 行，当前显示 ").Append(shown.Count).Append(" 行");
            for (int i = 0; i < Math.Min(3, top.Count); i++)
            {
                sb.Append("   ").Append('[').Append(top[i].Key).Append("]×").Append(top[i].Value);
            }
            _stats.Text = sb.ToString();

            if (shown.Count > 0)
            {
                _view.SelectionStart = _view.TextLength;
                _view.ScrollToCaret();
            }
        }

        private static bool ContainsKey(string line)
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
        private void ExportReport()
        {
            string name = _current.Length > 0 ? _current : "WARDEN.log";
            string path = PathOf(name);
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

            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "WARDEN-日志报告-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
            try
            {
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                MessageBox.Show(this, "报告已导出到桌面：\n\n" + Path.GetFileName(outPath),
                                "WARDEN 日志查看器", MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "导出失败：" + e.Message, "WARDEN 日志查看器",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LogViewForm());
        }
    }
}
