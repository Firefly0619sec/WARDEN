// ============================================================================
//  WARDEN 帮助中心 —— 新手教程页 + 反馈页
// ============================================================================
//  【为什么需要它】（用户 2026-10-10）
//
//    "MD 文件可能萌新不会看，但是程序里面肯定要把教程写进去的，
//      也就是要有一个教会萌新怎么用的页面。"
//      -> 教程页：不依赖 README，程序内一步步教
//
//    "首先要加一个反馈页，然后也是直接到 GitHub 的。"
//      -> 反馈页：填完直接提交到 GitHub
//
//  【★★ 反馈页的关键安全设计】
//
//    ✗ 绝对不能在客户端里内嵌 GitHub Token
//       —— exe 会被反编译，Token 一泄漏，
//          别人就能拿你的账号乱发 issue / 删仓库 ✗✗✗
//
//    ✓ 正确做法（本文件采用）：
//        · 把反馈内容拼成【预填好的 GitHub issue 链接】
//        · 用【用户自己的浏览器】打开，由他确认后提交
//        · WARDEN 全程不自动上传任何东西 ✓
//
//    ✓ 没有 GitHub 账号的萌新怎么办（用户提到的场景）：
//        · 一键把完整反馈内容复制到剪贴板
//        · 发给分发者 / 群里，由有账号的人代发 ✓
//        · 也能一键存成文件，方便传给别人
//
//  【为什么教程不用 Markdown 渲染库】
//    本来想用 Markdig（BSD-2-Clause，和 AGPL-3.0 兼容 ✓）
//    但那会引入 NuGet 依赖，而自包含发布要把它一起打包
//    -> 改成【语言包驱动的结构化教程】：
//         文字全在语言包里，翻译者改语言包就能出外语版 ✓
//         不用引任何第三方库 ✓ 也就不用操心协议 ✓
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using WardenLang;

namespace WardenHelp
{
    internal static class Program
    {
        internal const string Version = "0.2.1";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (s, e) =>
                MessageBox.Show("WARDEN 帮助中心出错：" + Environment.NewLine + Environment.NewLine
                    + e.Exception.Message,
                    "WARDEN", MessageBoxButtons.OK, MessageBoxIcon.Error);

            bool feedback = false;
            foreach (var a in args)
            {
                if (a.Equals("--feedback", StringComparison.OrdinalIgnoreCase)) feedback = true;
            }

            try
            {
                Application.Run(new HelpForm(feedback));
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + Environment.NewLine + Environment.NewLine + ex,
                    "WARDEN", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>★ 反馈提交地址（仓库硬编码，防止被改配置引到恶意仓库）。</summary>
        internal const string RepoOwner = "Firefly0619sec";
        internal const string RepoName = "WARDEN";
    }

    // ========================================================================
    //  帮助中心主窗口：左边目录，右边内容
    // ========================================================================
    internal sealed class HelpForm : Form
    {
        private ListBox _toc = null!;
        private RichTextBox _body = null!;
        private Button _btnFeedback = null!;
        private Button _btnOpenPage = null!;
        private Button _btnClose = null!;

        private readonly (string title, string body)[] _sections;

        public HelpForm(bool startOnFeedback)
        {
            // ★ 教程内容全部来自语言包（翻译者改语言包就能出外语版）
            _sections = new (string, string)[]
            {
                // ★ 开源许可（用户 2026-10-11 要求：应用内也要有声明）
                (WardenLang.Lang.T("help.sec.license"),
                 WardenLang.Lang.T("help.sec.license.body")),
                (Lang.T("help.sec.quick.title"),    Lang.T("help.sec.quick.body")),
                (Lang.T("help.sec.tray.title"),     Lang.T("help.sec.tray.body")),
                (Lang.T("help.sec.blocked.title"),  Lang.T("help.sec.blocked.body")),
                (Lang.T("help.sec.dryrun.title"),   Lang.T("help.sec.dryrun.body")),
                (Lang.T("help.sec.rules.title"),    Lang.T("help.sec.rules.body")),
                (Lang.T("help.sec.safety.title"),   Lang.T("help.sec.safety.body")),
                (Lang.T("help.sec.update.title"),   Lang.T("help.sec.update.body")),
                (Lang.T("help.sec.uninstall.title"),Lang.T("help.sec.uninstall.body")),
                (Lang.T("help.sec.faq.title"),      Lang.T("help.sec.faq.body")),
            };

            BuildUI();
            if (_toc.Items.Count > 0) _toc.SelectedIndex = 0;

            if (startOnFeedback)
            {
                // ★ 从托盘点「反馈问题」进来时，直接弹反馈窗口
                Shown += (s, e) =>
                {
                    using var f = new FeedbackForm();
                    f.ShowDialog(this);
                };
            }
        }

        private void BuildUI()
        {
            Text = Lang.T("help.title");
            ClientSize = new Size(820, 600);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(700, 500);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            Icon = SystemIcons.Information;

            // ---- 顶部 ----
            var top = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                BackColor = Color.FromArgb(0, 120, 212),
            };
            Controls.Add(top);

            top.Controls.Add(new Label
            {
                Text = Lang.T("help.heading"),
                Location = new Point(18, 8),
                Size = new Size(600, 26),
                Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
            });

            top.Controls.Add(new Label
            {
                Text = Lang.T("help.subheading"),
                Location = new Point(20, 34),
                Size = new Size(700, 20),
                ForeColor = Color.FromArgb(225, 238, 252),
                BackColor = Color.Transparent,
            });

            // ---- 底部 ----
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 58,
                BackColor = Color.FromArgb(248, 249, 251),
            };
            Controls.Add(bottom);

            _btnFeedback = new Button
            {
                Text = Lang.T("help.btn.feedback"),
                Location = new Point(16, 14),
                Size = new Size(120, 32),
            };
            _btnFeedback.Click += (s, e) =>
            {
                using var f = new FeedbackForm();
                f.ShowDialog(this);
            };
            bottom.Controls.Add(_btnFeedback);

            _btnOpenPage = new Button
            {
                Text = Lang.T("help.btn.openPage"),
                Location = new Point(144, 14),
                Size = new Size(130, 32),
            };
            _btnOpenPage.Click += (s, e) => OpenUrl(
                "https://github.com/" + Program.RepoOwner + "/" + Program.RepoName);
            bottom.Controls.Add(_btnOpenPage);

            var btnSettings = new Button
            {
                Text = Lang.T("help.btn.settings"),
                Location = new Point(282, 14),
                Size = new Size(130, 32),
            };
            btnSettings.Click += (s, e) => LaunchSibling("WARDEN-Settings.exe", "");
            bottom.Controls.Add(btnSettings);

            _btnClose = new Button
            {
                Text = Lang.T("common.close"),
                Location = new Point(686, 14),
                Size = new Size(118, 32),
            };
            _btnClose.Click += (s, e) => Close();
            bottom.Controls.Add(_btnClose);

            // ---- 左边目录 ----
            _toc = new ListBox
            {
                Dock = DockStyle.Left,
                Width = 220,
                IntegralHeight = false,
                ItemHeight = 30,
                Font = new Font("Microsoft YaHei UI", 10f),
                BorderStyle = BorderStyle.None,
            };
            foreach (var s in _sections) _toc.Items.Add("  " + s.title);
            _toc.SelectedIndexChanged += (s, e) =>
            {
                if (_toc.SelectedIndex >= 0 && _toc.SelectedIndex < _sections.Length)
                {
                    ShowSection(_sections[_toc.SelectedIndex]);
                }
            };
            Controls.Add(_toc);

            var split = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Color.FromArgb(226, 229, 234) };
            Controls.Add(split);

            // ---- 右边内容 ----
            _body = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10f),
                DetectUrls = true,
            };
            // ★ 点内容里的链接能打开浏览器（新手教程里会有链接）
            _body.LinkClicked += (s, e) =>
            {
                try { OpenUrl(e.LinkText ?? ""); } catch { }
            };
            Controls.Add(_body);
            _body.BringToFront();
        }

        private void ShowSection((string title, string body) sec)
        {
            _body.Clear();
            _body.SelectionFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            _body.SelectionColor = Color.FromArgb(30, 34, 42);
            _body.AppendText(sec.title + Environment.NewLine + Environment.NewLine);

            _body.SelectionFont = new Font("Microsoft YaHei UI", 10f);
            _body.SelectionColor = Color.FromArgb(52, 58, 68);
            _body.AppendText(sec.body);
            _body.SelectionStart = 0;
            _body.ScrollToCaret();
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                MessageBox.Show(Lang.T("help.openFailed") + Environment.NewLine + e.Message,
                    "WARDEN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// ★ 打开同目录下的兄弟程序。
        ///
        /// 【为什么找不到时要明确报错】
        ///     正常安装时五个组件是【强制安装】的
        ///     找不到说明安装不完整 -> 要告诉用户，不能静默失败 ✗
        /// </summary>
        internal static void LaunchSibling(string exe, string args)
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                string path = Path.Combine(dir, exe);
                if (!File.Exists(path))
                {
                    MessageBox.Show(Lang.T("help.siblingMissing", exe),
                        "WARDEN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = args ?? "",
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                });
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "WARDEN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // ========================================================================
    //  反馈页
    // ========================================================================
    internal sealed class FeedbackForm : Form
    {
        private ComboBox _cbKind = null!;
        private TextBox _txtTitle = null!;
        private TextBox _txtBody = null!;
        private TextBox _txtContact = null!;
        private CheckBox _ckDiag = null!;
        private TextBox _preview = null!;
        private Label _status = null!;

        public FeedbackForm()
        {
            BuildUI();
            RefreshPreview();
        }

        private void BuildUI()
        {
            Text = Lang.T("fb.title");
            ClientSize = new Size(760, 640);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(680, 560);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            Icon = SystemIcons.Information;

            var top = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Color.FromArgb(0, 120, 212),
            };
            Controls.Add(top);
            top.Controls.Add(new Label
            {
                Text = Lang.T("fb.heading"),
                Location = new Point(18, 12),
                Size = new Size(700, 28),
                Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
            });

            int y = 62;
            var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 8, 16, 8) };
            Controls.Add(host);
            host.BringToFront();

            host.Controls.Add(new Label
            {
                Text = Lang.T("fb.kind"),
                Location = new Point(16, y), Size = new Size(70, 22),
            });
            _cbKind = new ComboBox
            {
                Location = new Point(92, y - 3),
                Size = new Size(220, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _cbKind.Items.AddRange(new object[]
            {
                Lang.T("fb.kind.bug"),
                Lang.T("fb.kind.idea"),
                Lang.T("fb.kind.other"),
            });
            _cbKind.SelectedIndex = 0;
            _cbKind.SelectedIndexChanged += (s, e) => RefreshPreview();
            host.Controls.Add(_cbKind);
            y += 36;

            host.Controls.Add(new Label
            {
                Text = Lang.T("fb.subject"),
                Location = new Point(16, y), Size = new Size(70, 22),
            });
            _txtTitle = new TextBox
            {
                Location = new Point(92, y - 3),
                Size = new Size(630, 26),
            };
            _txtTitle.TextChanged += (s, e) => RefreshPreview();
            host.Controls.Add(_txtTitle);
            y += 36;

            host.Controls.Add(new Label
            {
                Text = Lang.T("fb.detail"),
                Location = new Point(16, y), Size = new Size(120, 22),
            });
            host.Controls.Add(new Label
            {
                Text = Lang.T("fb.detailHint"),
                Location = new Point(140, y), Size = new Size(580, 22),
                ForeColor = Color.Gray,
            });
            y += 24;

            _txtBody = new TextBox
            {
                Location = new Point(16, y),
                Size = new Size(706, 120),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
            };
            _txtBody.TextChanged += (s, e) => RefreshPreview();
            host.Controls.Add(_txtBody);
            y += 130;

            host.Controls.Add(new Label
            {
                Text = Lang.T("fb.contact"),
                Location = new Point(16, y), Size = new Size(70, 22),
            });
            _txtContact = new TextBox
            {
                Location = new Point(92, y - 3),
                Size = new Size(300, 26),
            };
            _txtContact.TextChanged += (s, e) => RefreshPreview();
            host.Controls.Add(_txtContact);
            host.Controls.Add(new Label
            {
                Text = Lang.T("fb.contactHint"),
                Location = new Point(400, y), Size = new Size(320, 22),
                ForeColor = Color.Gray,
            });
            y += 36;

            _ckDiag = new CheckBox
            {
                Text = Lang.T("fb.attachDiag"),
                Location = new Point(16, y),
                Size = new Size(700, 24),
                Checked = true,
            };
            _ckDiag.CheckedChanged += (s, e) => RefreshPreview();
            host.Controls.Add(_ckDiag);
            y += 26;

            host.Controls.Add(new Label
            {
                Text = Lang.T("fb.diagHint"),
                Location = new Point(34, y), Size = new Size(680, 34),
                ForeColor = Color.Gray,
            });
            y += 40;

            host.Controls.Add(new Label
            {
                Text = Lang.T("fb.preview"),
                Location = new Point(16, y), Size = new Size(200, 22),
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            });
            y += 24;

            _preview = new TextBox
            {
                Location = new Point(16, y),
                Size = new Size(706, 130),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 251, 252),
                Font = new Font("Consolas", 9f),
            };
            host.Controls.Add(_preview);

            // ---- 底部按钮 ----
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 88,
                BackColor = Color.FromArgb(248, 249, 251),
            };
            Controls.Add(bottom);

            var btnGithub = new Button
            {
                Text = Lang.T("fb.btn.github"),
                Location = new Point(16, 10),
                Size = new Size(180, 34),
            };
            btnGithub.Click += (s, e) => OpenOnGitHub();
            bottom.Controls.Add(btnGithub);

            var btnCopy = new Button
            {
                Text = Lang.T("fb.btn.copy"),
                Location = new Point(204, 10),
                Size = new Size(180, 34),
            };
            btnCopy.Click += (s, e) => CopyToClipboard();
            bottom.Controls.Add(btnCopy);

            var btnSave = new Button
            {
                Text = Lang.T("fb.btn.save"),
                Location = new Point(392, 10),
                Size = new Size(180, 34),
            };
            btnSave.Click += (s, e) => SaveToFile();
            bottom.Controls.Add(btnSave);

            var btnClose = new Button
            {
                Text = Lang.T("common.close"),
                Location = new Point(652, 10),
                Size = new Size(120, 34),
            };
            btnClose.Click += (s, e) => Close();
            bottom.Controls.Add(btnClose);

            _status = new Label
            {
                Location = new Point(16, 52),
                Size = new Size(720, 30),
                ForeColor = Color.FromArgb(90, 96, 106),
                Text = Lang.T("fb.noAccountHint"),
            };
            bottom.Controls.Add(_status);
        }

        // ---------------------------------------------------------------
        //  内容拼装
        // ---------------------------------------------------------------
        private string KindText() => _cbKind.SelectedIndex switch
        {
            1 => Lang.T("fb.kind.idea"),
            2 => Lang.T("fb.kind.other"),
            _ => Lang.T("fb.kind.bug"),
        };

        private string BuildReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("### " + Lang.T("fb.r.kind") + "：" + KindText());

            if (_txtContact.Text.Trim().Length > 0)
            {
                sb.AppendLine("### " + Lang.T("fb.r.contact") + "：" + _txtContact.Text.Trim());
            }

            sb.AppendLine();
            sb.AppendLine("### " + Lang.T("fb.r.detail"));
            sb.AppendLine();
            sb.AppendLine(_txtBody.Text.Trim().Length > 0
                ? _txtBody.Text.Trim() : "（这里填你遇到的情况）");

            if (_ckDiag.Checked)
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

        private void RefreshPreview()
        {
            _preview.Text = BuildReport().Replace(Environment.NewLine, Environment.NewLine);
        }

        // ---------------------------------------------------------------
        //  三种提交方式
        // ---------------------------------------------------------------
        /// <summary>
        /// ★ 方式一：在 GitHub 上打开预填好的新建 issue 页面。
        ///
        /// 【为什么用浏览器打开而不是直接调 API】
        ///     直接调 API 需要在客户端里放 Token ✗
        ///     exe 会被反编译，Token 一泄漏别人就能拿你的账号乱来 ✗✗✗
        ///     -> 用浏览器打开，由用户自己的账号确认提交 ✓ 安全 ✓
        /// </summary>
        private void OpenOnGitHub()
        {
            string title = _txtTitle.Text.Trim();
            if (title.Length == 0) title = Lang.T("fb.defaultTitle");

            string url = "https://github.com/" + Program.RepoOwner + "/" + Program.RepoName
                + "/issues/new?title=" + Uri.EscapeDataString("[" + KindText() + "] " + title)
                + "&body=" + Uri.EscapeDataString(BuildReport());

            // ★ GitHub 的 URL 有长度限制，太长会被截断
            //   超过就提示用户改用「复制内容」的方式 ✓ 不静默丢内容 ✓
            if (url.Length > 7000)
            {
                var r = MessageBox.Show(
                    Lang.T("fb.tooLong") + Environment.NewLine + Environment.NewLine
                    + Lang.T("fb.tooLongAsk"),
                    "WARDEN", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
                url = "https://github.com/" + Program.RepoOwner + "/" + Program.RepoName
                    + "/issues/new?title="
                    + Uri.EscapeDataString("[" + KindText() + "] " + title);
                CopyToClipboard(silent: true);
                _status.Text = Lang.T("fb.copiedForPaste");
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                _status.Text = Lang.T("fb.openedInBrowser");
            }
            catch (Exception e)
            {
                MessageBox.Show(Lang.T("help.openFailed") + Environment.NewLine + e.Message,
                    "WARDEN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CopyToClipboard(bool silent = false)
        {
            try
            {
                Clipboard.SetText(BuildReport());
                _status.Text = Lang.T("fb.copied");
                if (!silent)
                {
                    MessageBox.Show(Lang.T("fb.copiedLong"), "WARDEN",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "WARDEN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SaveToFile()
        {
            try
            {
                using var d = new SaveFileDialog
                {
                    Filter = "文本文件 (*.txt)|*.txt|Markdown (*.md)|*.md",
                    FileName = "WARDEN-反馈-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt",
                };
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(d.FileName, BuildReport(), new UTF8Encoding(false));
                _status.Text = Lang.T("fb.saved", d.FileName);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "WARDEN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
