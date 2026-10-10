// ============================================================================
//  A34 环境检测 —— 详情窗口
// ============================================================================
//  【为什么要单独一个窗口，而不是塞几个 MessageBox】
//      MessageBox 只能装一句话 ✗
//      而环境检测要同时说清三件事：
//        ① 现在什么情况（十项逐条）
//        ② 哪一项有问题、为什么
//        ③ 你具体该做什么（可复制、可点开官方页面）
//      → 而且用户可能想"重新检测"（装完运行库回来再看一眼）✓
//
//  【★ 设计要点】
//      · 结果文本和安装日志共用 EnvCheck 里那一份（绝不两处各写一套 ✗）
//      · 官方链接只给【微软官网 / aka.ms】，不给任何第三方下载站 ✓
//      · 离线时也能用：链接可复制，用户拿手机/别的电脑去下 ✓
//      · 窗口底部写明日志文件路径 —— 出问题让用户直接把文件发过来 ✓
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace WardenSetup
{
    internal sealed class EnvDialog : Form
    {
        private readonly string _dir;
        private EnvReport _r;

        private Label _lblTone = null!;
        private TextBox _txt = null!;
        private Button _btnAgain = null!;
        private Button _btnRuntime = null!;
        private Button _btnCopy = null!;
        private Button _btnClose = null!;

        /// <summary>★ 给调用方取回最新一次检测结果（用户可能点了"重新检测"）。</summary>
        public EnvReport Report { get { return _r; } }

        public EnvDialog(EnvReport report, string dir)
        {
            _r = report;
            _dir = dir;
            BuildChrome();
            RefreshView();
        }

        private void BuildChrome()
        {
            Text = "环境检测详情";
            ClientSize = new Size(724, 552);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.White;
            Icon = SystemIcons.Information;

            _lblTone = new Label
            {
                Location = new Point(20, 14),
                Size = new Size(684, 48),
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            };
            Controls.Add(_lblTone);

            _txt = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Location = new Point(20, 66),
                Size = new Size(684, 384),
                BackColor = Color.FromArgb(250, 251, 252),
                Font = new Font("Microsoft YaHei UI", 9f),
            };
            Controls.Add(_txt);

            _btnAgain = new Button
            {
                Text = "重新检测",
                Location = new Point(20, 464),
                Size = new Size(110, 32),
                FlatStyle = FlatStyle.System,
            };
            _btnAgain.Click += (s, e) => Recheck();
            Controls.Add(_btnAgain);

            _btnRuntime = new Button
            {
                Text = "打开官方下载页",
                Location = new Point(140, 464),
                Size = new Size(150, 32),
                FlatStyle = FlatStyle.System,
            };
            _btnRuntime.Click += (s, e) => OpenUrl(EnvCheck.RuntimeExeUrl);
            Controls.Add(_btnRuntime);

            _btnCopy = new Button
            {
                Text = "复制下载链接",
                Location = new Point(300, 464),
                Size = new Size(130, 32),
                FlatStyle = FlatStyle.System,
            };
            _btnCopy.Click += (s, e) => CopyUrl();
            Controls.Add(_btnCopy);

            _btnClose = new Button
            {
                Text = "关闭",
                Location = new Point(584, 464),
                Size = new Size(120, 32),
                FlatStyle = FlatStyle.System,
            };
            _btnClose.Click += (s, e) => Close();
            Controls.Add(_btnClose);

            Controls.Add(new Label
            {
                Location = new Point(20, 504),
                Size = new Size(684, 40),
                ForeColor = Color.FromArgb(110, 116, 126),
                Text = "★ 这份检测结果也会写进安装日志。"
                     + "如果装不上，把这个文件发给我就能定位：" + Environment.NewLine
                     + "   " + SetupLog.FilePath,
            });
        }

        private void RefreshView()
        {
            _txt.Text = _r.ToText(true);
            _txt.SelectionStart = 0;
            _lblTone.Text = _r.Summary;

            int tone = _r.SummaryTone;
            if (tone == 0) _lblTone.ForeColor = Color.FromArgb(20, 130, 50);
            else if (tone == 1) _lblTone.ForeColor = Color.FromArgb(190, 120, 20);
            else _lblTone.ForeColor = Color.FromArgb(190, 60, 40);
        }

        /// <summary>
        /// ★ 重新检测（这次连网络一起测）。
        ///   用途：用户照着提示去装了运行库，回来点一下就能看到"好了"。
        /// </summary>
        private void Recheck()
        {
            _btnAgain.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                _r = EnvCheck.Run(_dir, probeNetwork: true);
            }
            finally
            {
                Cursor = Cursors.Default;
                _btnAgain.Enabled = true;
            }
            RefreshView();
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                SetupLog.Line("ENV", "opened official url=" + url);
            }
            catch (Exception e)
            {
                SetupLog.Line("ENV", "open url failed: " + e.Message);
                MessageBox.Show(
                    "没能自动打开浏览器。" + Environment.NewLine + Environment.NewLine
                    + "请手动把这个网址复制到浏览器里：" + Environment.NewLine
                    + url,
                    "环境检测", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void CopyUrl()
        {
            try
            {
                Clipboard.SetText(EnvCheck.RuntimeExeUrl);
                MessageBox.Show(
                    "链接已经复制好了。" + Environment.NewLine + Environment.NewLine
                    + "你可以把它发到手机上，或者粘到能上网的电脑上打开："
                    + Environment.NewLine + EnvCheck.RuntimeExeUrl,
                    "环境检测", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch
            {
                MessageBox.Show(
                    "复制失败（剪贴板被别的程序占用了）。" + Environment.NewLine
                    + "手动抄这个网址就行：" + Environment.NewLine
                    + EnvCheck.RuntimeExeUrl,
                    "环境检测", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
