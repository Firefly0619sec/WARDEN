// ============================================================================
//  安装向导界面（事故后重写，2026-10-10）
//  类名仍是 SetupWizard（SetupCore 里 new SetupWizard(...) 用它）✓
//  三页：① 环境检测  ② 安装位置（含可选组件）  ③ 完成
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WardenSetup
{
    internal sealed class SetupWizard : Form
    {
        private readonly EnvReport _env;
        private string _dir;
        private readonly Panel _body = new Panel { Dock = DockStyle.Fill };
        private readonly Label _title = new Label();
        private readonly Button _back = new Button { Text = "上一步", Width = 90 };
        private readonly Button _next = new Button { Text = "下一步", Width = 90 };
        private readonly Button _cancel = new Button { Text = "取消", Width = 90 };

        private int _page;
        private TextBox _txtDir = new TextBox();
        private Label _lblCheck = new Label();
        private CheckBox _ckDesktop = new CheckBox();
        private CheckBox _ckAuto = new CheckBox();
        private CheckBox _ckRed = new CheckBox();
        private TextBox _log = new TextBox();
        // ★ 安装进度（用户要求：离线安装包也要有进度条 ✓）
        private ProgressBar _bar = new ProgressBar();
        private Label _step = new Label();
        private volatile bool _installing;

        private bool DesktopWanted => _ckDesktop.Checked;
        private bool AutostartWanted => _ckAuto.Checked;
        private bool RedTeamWanted => _ckRed.Checked;

        public SetupWizard(EnvReport env, string dir, bool desktop, bool autostart,
                           bool redTeam)
        {
            _env = env;
            _dir = dir;

            Text = "WARDEN 安装程序";
            ClientSize = new Size(760, 480);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);

            // ★ 标题栏蓝色背景必须用 Panel.BackColor ✗
            //   以前是给 Panel 手绘蓝色 ✓ 但 Label 不会继承"手绘"的底色 ✗
            //   → Label 自己刷成浅灰 ✓ 白字压在浅灰上 = 看不清 ✓（用户实测反馈 ✓）
            //   设 BackColor 后子控件会继承 ✓ 白字就清楚了 ✓
            var top = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68,
                BackColor = Color.FromArgb(0, 120, 212),
            };
            _title.SetBounds(20, 12, 700, 26);
            _title.ForeColor = Color.White;
            _title.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            var sub = new Label
            {
                Text = "窗口行为拦截工具 · 按行为特征识别并处置干扰性弹窗",
                ForeColor = Color.WhiteSmoke,
                Bounds = new Rectangle(22, 40, 700, 20),
            };
            top.Controls.Add(_title);
            top.Controls.Add(sub);

            var nav = new Panel { Dock = DockStyle.Bottom, Height = 56 };
            _back.SetBounds(360, 12, 100, 32);
            _next.SetBounds(470, 12, 110, 32);
            _cancel.SetBounds(590, 12, 90, 32);
            _next.Click += (s, e) => Next();
            _back.Click += (s, e) => { _page = Math.Max(0, _page - 1); Render(); };
            _cancel.Click += (s, e) => Close();
            nav.Controls.Add(_back);
            nav.Controls.Add(_next);
            nav.Controls.Add(_cancel);

            Controls.Add(_body);
            Controls.Add(nav);
            Controls.Add(top);

            Render();
        }

        private void Next()
        {
            if (_page == 0)
            {
                if (!_env.CanInstall)
                {
                    MessageBox.Show(this, "环境不满足，暂时不能安装。\n\n"
                        + _env.ToText(true), "WARDEN 安装",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _page = 1;
                Render();
                return;
            }
            if (_page == 1)
            {
                string? problem = Installer.ValidateDir(_txtDir.Text);
                if (problem != null)
                {
                    MessageBox.Show(this, problem, "WARDEN 安装",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _dir = Installer.Expand(_txtDir.Text);
                _page = 2;
                Render();
                DoInstall();     // 进入第 3 页就开始装 ✓
                return;
            }
            if (_installing) { return; }
            Close();
        }

        private void Render()
        {
            _body.Controls.Clear();
            switch (_page)
            {
                case 0:
                    _title.Text = "第 1 步 / 3：环境检测";
                    _back.Enabled = false;
                    _next.Text = "下一步";
                    BuildEnvPage();
                    break;
                case 1:
                    _title.Text = "第 2 步 / 3：选择安装位置";
                    _back.Enabled = true;
                    _next.Text = "开始安装";
                    BuildDirPage();
                    break;
                default:
                    _title.Text = "第 3 步 / 3：正在安装";
                    _back.Enabled = false;
                    _next.Text = "完成";
                    BuildInstallPage();
                    break;
            }
        }

        // ---------------------------------------------------------------- 第 1 页
        private void BuildEnvPage()
        {
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Bounds = new Rectangle(20, 20, 720, 340),
                Text = _env.ToText(true).Replace("\n", Environment.NewLine),
                BackColor = Color.White,
            };
            var again = new Button
            {
                Text = "重新检测",
                Bounds = new Rectangle(20, 372, 110, 30),
            };
            again.Click += (s, e) =>
            {
                EnvReport r = EnvCheck.Run(_dir, probeNetwork: _env.NeedComponent);
                box.Text = r.ToText(true).Replace("\n", Environment.NewLine);
                box.Tag = r;
            };
            _body.Controls.Add(box);
            _body.Controls.Add(again);
        }

        // ---------------------------------------------------------------- 第 2 页
        private void BuildDirPage()
        {
            _body.Controls.Add(new Label
            {
                Text = "WARDEN 会安装到这里。你可以改成别的位置（比如 D 盘）。",
                Bounds = new Rectangle(20, 16, 700, 22),
            });
            _body.Controls.Add(new Label
            {
                Text = "安装目录：",
                Bounds = new Rectangle(20, 52, 70, 22),
            });
            _txtDir = new TextBox
            {
                Text = _dir,
                Bounds = new Rectangle(96, 50, 500, 24),
            };
            _txtDir.TextChanged += (s, e) => CheckDir();
            var browse = new Button
            {
                Text = "浏览...",
                Bounds = new Rectangle(606, 49, 90, 26),
            };
            browse.Click += (s, e) =>
            {
                using var d = new FolderBrowserDialog
                {
                    Description = "选择 WARDEN 的安装目录",
                    ShowNewFolderButton = true,
                };
                if (Directory.Exists(_txtDir.Text))
                {
                    d.SelectedPath = _txtDir.Text;
                }
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    _txtDir.Text = Path.Combine(d.SelectedPath, "WARDEN");
                }
            };
            _lblCheck = new Label
            {
                Bounds = new Rectangle(96, 80, 600, 22),
                ForeColor = Color.Gray,
            };
            _body.Controls.Add(_txtDir);
            _body.Controls.Add(browse);
            _body.Controls.Add(_lblCheck);

            // ★ 这段说明以前被裁掉了 ✗（标签高度只给 70，文字要 4~5 行）
            //   → 高度 120 ✓（2026-10-10 用户反馈已修）
            _body.Controls.Add(new Label
            {
                Bounds = new Rectangle(20, 112, 720, 120),
                Text = "★ 为什么不建议装在 Program Files：" + Environment.NewLine
                     + "  那需要管理员权限，而且以后每次更新都会被系统弹窗拦一下。"
                     + Environment.NewLine + Environment.NewLine
                     + "★ 默认位置（用户目录下）不需要管理员权限，更新也顺畅。",
            });

            _ckDesktop = new CheckBox
            {
                Text = "在桌面放一个快捷方式",
                Checked = DesktopWanted || true,
                Bounds = new Rectangle(20, 244, 400, 24),
            };
            _ckAuto = new CheckBox
            {
                Text = "开机自动启动 WARDEN（推荐：不然弹窗没人拦）",
                Checked = true,
                Bounds = new Rectangle(20, 272, 500, 24),
            };
            var redText = "★ 可选组件：红队自测工具（弹一批「关不掉」的窗口，"
                        + "用来验证 WARDEN 拦不拦得住）";
            _ckRed = new CheckBox
            {
                Text = redText,
                Checked = RedTeamWanted,
                Bounds = new Rectangle(20, 306, 720, 48),
            };
            _body.Controls.Add(_ckDesktop);
            _body.Controls.Add(_ckAuto);
            _body.Controls.Add(_ckRed);

            CheckDir();
        }

        private void CheckDir()
        {
            string? problem = Installer.ValidateDir(_txtDir.Text);
            if (problem == null)
            {
                _lblCheck.Text = "✓ 这个位置可以使用";
                _lblCheck.ForeColor = Color.FromArgb(20, 130, 50);
            }
            else
            {
                _lblCheck.Text = "✗ " + problem;
                _lblCheck.ForeColor = Color.FromArgb(180, 40, 40);
            }
        }

        // ---------------------------------------------------------------- 第 3 页
        private void BuildInstallPage()
        {
            _step = new Label
            {
                Text = "正在准备安装…",
                Bounds = new Rectangle(20, 16, 720, 24),
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            };
            _bar = new ProgressBar
            {
                Bounds = new Rectangle(20, 46, 720, 22),
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous,
                Value = 0,
            };
            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Bounds = new Rectangle(20, 78, 720, 282),
                BackColor = Color.White,
                Font = new Font("Consolas", 9F),
            };
            _body.Controls.Add(_step);
            _body.Controls.Add(_bar);
            _body.Controls.Add(_log);
        }

        private void DoInstall()
        {
            var log = SetupLog.Capture();
            _installing = true;
            _next.Enabled = false;
            _next.Text = "安装中…";
            _bar.Value = 0;
            _step.Text = "正在准备安装…";

            // ★ 装的时候必须在后台线程 ✗
            //   原来是在 UI 线程同步跑 ✓ 窗口整个冻住 → 用户看到的就是"没有进度"✗
            var worker = new System.Threading.Thread(() =>
            {
                void Report(int pct, string msg)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _bar.Value = Math.Max(0, Math.Min(100, pct));
                            _step.Text = msg;
                            _log.AppendText(msg + Environment.NewLine);
                            _log.SelectionStart = _log.TextLength;
                            _log.ScrollToCaret();
                        }));
                    }
                    catch { }
                }

                bool ok = SetupCore.Install(_dir, DesktopWanted, AutostartWanted,
                                            RedTeamWanted, out string err, Report);
                try
                {
                    BeginInvoke(new Action(() => Finish(ok, err, log)));
                }
                catch { }
            })
            { IsBackground = true };
            worker.Start();
        }

        /// <summary>安装结束（回到 UI 线程 ✓）</summary>
        private void Finish(bool ok, string err, string log)
        {
            _installing = false;
            _bar.Value = ok ? 100 : _bar.Value;
            _next.Enabled = true;

            _log.Text = (log.Length > 0 ? log : "") + Environment.NewLine
                      + (ok ? "✓ 安装完成。" : "★ 安装失败：" + err);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();

            if (ok)
            {
                _step.Text = "安装完成 ✓";
                _title.Text = "第 3 步 / 3：安装完成";
                _next.Text = "完成";
                if (MessageBox.Show(this, "安装完成 ✓\n\n现在就启动 WARDEN 吗？",
                        "WARDEN 安装", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(
                                Path.Combine(_dir, "WARDEN.exe"))
                            { WorkingDirectory = _dir, UseShellExecute = true });
                    }
                    catch { }
                }
            }
            else
            {
                _step.Text = "安装失败 ✗ " + err;
                _next.Text = "关闭";
            }
        }
    }
}
