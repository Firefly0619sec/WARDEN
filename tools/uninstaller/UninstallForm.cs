// ============================================================================
//  卸载确认窗（事故后重写）
//  ★ 默认【保留】配置与日志 ✓ —— 卸载不等于清除痕迹
//     要删必须自己勾，而且勾了会再做一次确认 ✓
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WardenUninstall
{
    internal sealed class UninstallForm : Form
    {
        public static bool Cancelled;

        private readonly CheckBox _ckPurge = new CheckBox();
        private bool _result;

        public static bool Ask(bool defaultPurge)
        {
            using var f = new UninstallForm(defaultPurge);
            f.ShowDialog();
            return f._result;
        }

        private UninstallForm(bool defaultPurge)
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WARDEN");
            long dataMb = 0;
            try
            {
                if (Directory.Exists(dataDir))
                {
                    foreach (string f in Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories))
                    {
                        dataMb += new FileInfo(f).Length;
                    }
                    dataMb /= (1024 * 1024);
                }
            }
            catch { }

            Text = "卸载 WARDEN";
            ClientSize = new Size(560, 330);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);

            var top = new Panel { Dock = DockStyle.Top, Height = 60 };
            top.Paint += (s, e) =>
            {
                using var b = new SolidBrush(Color.FromArgb(0, 120, 212));
                e.Graphics.FillRectangle(b, top.ClientRectangle);
            };
            top.Controls.Add(new Label
            {
                Text = "卸载 WARDEN",
                ForeColor = Color.White,
                // ★ 2026-10-11 修（截图看出来的 bug ✓）：
                //   Label 默认【不透明】✗ 会带着浅灰底色盖住蓝底 ✓
                //   于是白字压浅灰 → 标题几乎看不清 ✓（见 截图-0.3.0\01-主窗口.png）
                //   AOT 版是 DrawTextW 直接画在蓝底上 ✓ 天生没这个问题 ✓
                BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
                Bounds = new Rectangle(20, 16, 400, 30),
            });

            Controls.Add(new Label
            {
                Bounds = new Rectangle(20, 76, 520, 120),
                Text = "卸载会做这些事：" + Environment.NewLine
                     + "  ✓ 请 WARDEN 自己退出（不强杀）" + Environment.NewLine
                     + "  ✓ 删除程序文件、快捷方式、开机启动、注册表项" + Environment.NewLine
                     + "  ✓ 配置与日志【默认保留】—— 卸载不等于清除痕迹",
            });

            _ckPurge.Text = "同时删除配置与日志（" + dataMb + " MB，不可恢复）";
            _ckPurge.Bounds = new Rectangle(20, 196, 520, 26);
            _ckPurge.Checked = defaultPurge;
            Controls.Add(_ckPurge);

            Controls.Add(new Label
            {
                Bounds = new Rectangle(20, 224, 520, 40),
                ForeColor = Color.FromArgb(180, 80, 0),
                Text = "★ 数据目录里可能存着「被攻击的证据」（EMERGENCY.log / TAMPER.log）✗"
                     + Environment.NewLine
                     + "  要留着给别人看、或者以后举报流氓软件，就别勾这一项。",
            });

            var ok = new Button { Text = "卸载", Bounds = new Rectangle(340, 280, 100, 32) };
            var cancel = new Button { Text = "取消", Bounds = new Rectangle(450, 280, 90, 32) };
            ok.Click += (s, e) =>
            {
                if (_ckPurge.Checked)
                {
                    // ★ 2026-10-11 修（截图看出来的 ✓）：危险动作的默认按钮必须是【否】✗
                    //   原来默认落在【是】上 ✓ 手快按个回车就把证据删了 ✓
                    var r = MessageBox.Show(this,
                        "确定连配置与日志一起删掉吗？\n\n删了就找不回来了 ✗",
                        "WARDEN 卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (r != DialogResult.Yes)
                    {
                        return;
                    }
                }
                _result = true;
                DialogResult = DialogResult.OK;
                Close();
            };
            cancel.Click += (s, e) =>
            {
                Cancelled = true;
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(ok);
            Controls.Add(cancel);
            Controls.Add(top);
        }
    }
}
