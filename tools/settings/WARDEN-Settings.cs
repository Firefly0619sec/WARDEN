// ============================================================================
//  WARDEN 设置工具 —— 独立窗口版本
// ============================================================================
//  【为什么从 PowerShell 改成 C# 独立 exe】（用户 2026-10-09 的反馈）
//
//      用户原话：
//        "有的人可能不知道这玩意需要 PS，结果我试了一下，
//          关掉 PS 的窗口，给设置窗口也关掉了。"
//
//      → 原因：PowerShell 脚本跑在 powershell.exe 里 ✗
//              那个进程【就是】界面的宿主 ✗
//              关掉它的控制台 → 进程结束 → 界面一起消失 ✗
//
//      → 改成【独立的 C# WinForms exe】✓
//        · 双击直接运行 ✓ 任务栏一个图标 ✓ 关它才关界面 ✓
//        · 不依赖 PowerShell ✗
//
//  【为什么用 .NET Framework 4.8】
//      · Windows 10 / 11 【系统自带】✓ 用户不用装任何东西 ✓
//      · 编译用系统自带的 csc.exe ✓
//      · 产物只有几百 KB ✓
//      （WARDEN 本体仍是 Native AOT —— 那个不能用 WinForms ✗
//        所以设置工具单独做成这个 ✓ 两者解耦 ✓）
//
//  【界面设计】
//      用户要求"不要太乱" ✗
//      → 按功能分标签页，每页只放同类设置 ✓
//      → 底部固定"保存 / 重新加载 / 启动停止 / 状态" ✓
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WardenSettings
{
    internal static class Program
    {
        [STAThread]
        /// <summary>
        /// 程序入口。
        /// ★ 特意加了全局异常处理：出问题时弹窗告诉用户，
        ///   而不是让窗口静默消失（那样根本没法排查）。
        /// </summary>
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // ★ 出异常不要静默消失 ✗ 弹出来让用户看到 ✓
            Application.ThreadException += (s, e) =>
                MessageBox.Show(WardenLang.Lang.Tr("发生错误：\n") + e.Exception.Message, "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show(WardenLang.Lang.Tr("启动失败：\n") + ex, "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        // ---- 路径 ----
        private readonly string _toolDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "WARDEN");
        private string CfgPath => Path.Combine(_toolDir, "WARDEN.json");
        private string LogPath => Path.Combine(_toolDir, "WARDEN.log");
        private string StopPath => Path.Combine(_toolDir, "WARDEN.stop");
        private string ExePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WARDEN.exe");

        // ---- 控件 ----
        private TabControl _tabs;
        private ListView _lvRules;
        private CheckBox _ckFg, _ckEdit, _ckChild, _ckDry;
        private CheckBox _ckTerm, _ckPrompt, _ckHonorSig, _ckHonorWl;
        private NumericUpDown _numApply, _numTermTh, _numFail, _numAsk, _numHides, _numWait;
        private TextBox _txtWl, _txtConsent, _txtTermHist, _txtHash, _txtHard, _txtLog;
        private NumericUpDown _numLines;
        private Label _lblStatus;
        private ComboBox _cbLang = new ComboBox();   // ★ 语言切换 ✓
        private Timer _timer;

        /// <summary>
        /// 主窗口。
        /// ★ 界面按功能分成六个标签页，避免一屏塞太多东西。
        /// </summary>
        public MainForm()
        {
            Text = WardenLang.Lang.Tr("WARDEN 设置");
            ClientSize = new Size(830, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            MinimumSize = new Size(780, 600);
            Icon = SystemIcons.Shield;

            BuildUI();
            LoadConfig();

            _timer = new Timer { Interval = 3000 };
            _timer.Tick += (s, e) => UpdateStatus();
            _timer.Start();
            Shown += (s, e) => { UpdateStatus(); RefreshLog(); };
            FormClosed += (s, e) => _timer.Stop();
        }

        // ================================================================
        //  界面
        // ================================================================
        /// <summary>
        /// 搭建界面骨架：标签页 + 底部固定按钮栏。
        /// </summary>
        private void BuildUI()
        {
            _tabs = new TabControl { Dock = DockStyle.Fill };
            Controls.Add(_tabs);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56 };
            Controls.Add(bottom);

            BuildRulesTab();
            BuildProtectTab();
            BuildActionTab();
            BuildListsTab();
            BuildSecurityTab();
            BuildLogTab();

            var btnSave = new Button { Text = WardenLang.Lang.Tr("保存配置"), Location = new Point(12, 12), Size = new Size(110, 32) };
            btnSave.Click += (s, e) => SaveConfig();

            // ★★★ 「语言」单独一栏（用户 2026-10-10 要求）：
            //   【为什么单独一栏】底部那条太窄，繁體中文（中國臺灣）(zh-TW)
            //   这种长度会被截断 ✗ 用户明确说"后面的英语编号都被挡住了"
            //   → 单独一页，下拉框做宽（420px），名称与代码都看得全 ✓
            {
                var langTab = new TabPage(WardenLang.Lang.T("set.tab.lang"))
                {
                    Padding = new Padding(16),
                };
                _tabs.TabPages.Add(langTab);

                langTab.Controls.Add(new Label
                {
                    Text = WardenLang.Lang.T("set.lang.label"),
                    Location = new Point(4, 12),
                    Size = new Size(420, 22),
                });

                _cbLang = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(4, 40),
                    Size = new Size(420, 26),
                    Font = new Font("Microsoft YaHei UI", 10F),
                };

                // ★ 贝塔版带 BCP47 代码（给开发者报 issue 用 ✓），正式版只显示名字 ✓
                bool showCode = System.Windows.Forms.Application.ProductVersion
                    .IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (string code in WardenLang.Lang.Available())
                {
                    _cbLang.Items.Add(showCode
                        ? WardenLang.Lang.DisplayName(code) + "  (" + code + ")"
                        : WardenLang.Lang.DisplayName(code));
                }
                if (_cbLang.Items.Count > 0)
                {
                    int idx = -1;
                    for (int i2 = 0; i2 < _cbLang.Items.Count; i2++)
                    {
                        string it = _cbLang.Items[i2] as string ?? "";
                        if (it == WardenLang.Lang.DisplayName(WardenLang.Lang.Code)
                            || it.EndsWith("(" + WardenLang.Lang.Code + ")"))
                        {
                            idx = i2;
                            break;
                        }
                    }
                    _cbLang.SelectedIndex = idx >= 0 ? idx : 0;
                }

                _cbLang.SelectedIndexChanged += (s2, e2) =>
                {
                    string shown = _cbLang.SelectedItem as string ?? "简体中文";
                    int lp = shown.LastIndexOf('(');
                    string code;
                    if (lp > 0)
                    {
                        code = shown.Substring(lp + 1).TrimEnd(')', ' ');
                    }
                    else
                    {
                        code = "zh-Hans";
                        foreach (string c2 in WardenLang.Lang.Available())
                        {
                            if (WardenLang.Lang.DisplayName(c2) == shown)
                            {
                                code = c2;
                                break;
                            }
                        }
                    }
                    try
                    {
                        WardenLang.Lang.Load(code);
                        try
                        {
                            string lg = Path.Combine(
                                Environment.GetFolderPath(
                                    Environment.SpecialFolder.LocalApplicationData),
                                "WARDEN", "setup.log");
                            Directory.CreateDirectory(Path.GetDirectoryName(lg)!);
                            File.AppendAllText(lg,
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                                + "  [语言] 界面语言已切到 " + code
                                + "（保存配置后生效 ✓）" + Environment.NewLine,
                                new System.Text.UTF8Encoding(false));
                        }
                        catch { }
                    }
                    catch
                    {
                        // 语言包不合法 → 保持原语言 ✓
                    }
                };

                langTab.Controls.Add(_cbLang);

                langTab.Controls.Add(new Label
                {
                    Text = WardenLang.Lang.T("set.lang.hint"),
                    Location = new Point(4, 78),
                    Size = new Size(760, 140),
                });
            }

            bottom.Controls.Add(btnSave);

            var btnReload = new Button { Text = WardenLang.Lang.Tr("重新加载"), Location = new Point(130, 12), Size = new Size(100, 32) };
            btnReload.Click += (s, e) =>
            {
                if (MessageBox.Show(WardenLang.Lang.Tr("重新读取配置文件？\n界面上的未保存修改会丢失。"), "WARDEN 设置",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                {
                    LoadConfig();
                }
            };
            bottom.Controls.Add(btnReload);






            _lblStatus = new Label { Location = new Point(495, 18), Size = new Size(330, 24) };
            bottom.Controls.Add(_lblStatus);
        }

        /// <summary>
        /// 新建一个标签页，并返回它内部的内容面板。
        /// </summary>
        private TabPage NewTab(string title, out Panel body)
        {
            var t = new TabPage(title) { Padding = new Padding(12) };
            _tabs.TabPages.Add(t);
            body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            t.Controls.Add(body);
            return t;
        }

        /// <summary>
        /// 生成一个标签控件的快捷方法（统一格式）。
        /// </summary>
        private static Label Lbl(string text, int x, int y, int w = 280)
            => new Label { Text = text, Location = new Point(x, y), Size = new Size(w, 20) };

        /// <summary>
        /// 生成一个复选框。
        /// </summary>
        private static CheckBox Chk(string text, int x, int y, bool val)
            => new CheckBox { Text = text, Location = new Point(x, y), Size = new Size(700, 24), Checked = val };

        /// <summary>
        /// 生成一个数字输入框（带上下限钳制）。
        /// </summary>
        private static NumericUpDown Num(int x, int y, decimal val, decimal min, decimal max)
            => new NumericUpDown
            {
                Location = new Point(x, y),
                Size = new Size(95, 24),
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, val))
            };

        // ---------------------------------------------------------------- 规则
        /// <summary>
        /// 【规则页】哪些窗口算可疑。
        /// ★ 用列表展示，勾选框直接控制启用状态。
        /// </summary>
        private void BuildRulesTab()
        {
            Panel b;
            NewTab(WardenLang.Lang.T("set.tab.rules"), out b);

            _lvRules = new ListView
            {
                Location = new Point(0, 0),
                Size = new Size(780, 430),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                CheckBoxes = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            // ★ 带复选框的 ListView：第一列就是复选框列 ✗
            //   所以列标题写"启用"，数据行的第一列留空 ✓
            _lvRules.Columns.Add(WardenLang.Lang.Tr("启用"), 52);
            _lvRules.Columns.Add(WardenLang.Lang.Tr("名称"), 185);
            _lvRules.Columns.Add(WardenLang.Lang.Tr("进程"), 135);
            _lvRules.Columns.Add(WardenLang.Lang.Tr("标题关键词"), 400);
            b.Controls.Add(_lvRules);

            var btnAdd = new Button { Text = WardenLang.Lang.Tr("新增规则"), Location = new Point(0, 440), Size = new Size(105, 30) };
            btnAdd.Click += (s, e) => AddRuleDialog();
            b.Controls.Add(btnAdd);

            var btnDel = new Button { Text = WardenLang.Lang.Tr("删除选中"), Location = new Point(113, 440), Size = new Size(105, 30) };
            btnDel.Click += (s, e) =>
            {
                foreach (ListViewItem it in _lvRules.SelectedItems.Cast<ListViewItem>().ToList())
                {
                    _lvRules.Items.Remove(it);
                }
            };
            b.Controls.Add(btnDel);

            var btnEdit = new Button { Text = WardenLang.Lang.Tr("编辑选中"), Location = new Point(226, 440), Size = new Size(105, 30) };
            btnEdit.Click += (s, e) => EditRuleDialog();
            b.Controls.Add(btnEdit);

            // ★ B5：规则导入 / 导出（用户 2026-10-13 之前提的需求 ✓）
            var btnExport = new Button
            {
                Text = WardenLang.Lang.Tr("导出规则"),
                Location = new Point(345, 440),
                Size = new Size(105, 30),
            };
            btnExport.Click += (s, e) => ExportRules();
            b.Controls.Add(btnExport);

            var btnImport = new Button
            {
                Text = WardenLang.Lang.Tr("导入规则"),
                Location = new Point(458, 440),
                Size = new Size(105, 30),
            };
            btnImport.Click += (s, e) => ImportRules();
            b.Controls.Add(btnImport);

            b.Controls.Add(Lbl(WardenLang.Lang.Tr("★ 勾选 = 启用该规则。\"进程\"和\"标题关键词\"至少要填一个。"),
                               10, 480, 700));   // ★ 挪到按钮下面一行（原来被挡住 ✗）
        }


        /// <summary>
        /// ★ B5：把当前规则导出成一个 json 文件 ✓
        ///   【为什么要能导出】换机器 / 备份 / 把规则分享给别人 ✓
        ///   格式自带标识（warden-rules/1）→ 以后改格式也能认出来 ✓
        /// </summary>
        private void ExportRules()
        {
            if (_lvRules.Items.Count == 0)
            {
                MessageBox.Show(WardenLang.Lang.Tr("现在还没有规则可以导出。"), "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dlg = new SaveFileDialog
            {
                Title = WardenLang.Lang.Tr("导出当前规则到文件"),
                Filter = "WARDEN *|*.json",
                FileName = "WARDEN-rules-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_说明\": [");
                sb.AppendLine("    \"WARDEN 规则导出文件 —— 可用设置界面的「导入规则」读回\",");
                sb.AppendLine("    \"改坏了也没关系：导入时不合法的条目会被跳过并告诉你\"");
                sb.AppendLine("  ],");
                sb.AppendLine("  \"format\": \"warden-rules/1\",");
                sb.AppendLine("  \"rules\": [");
                for (int i = 0; i < _lvRules.Items.Count; i++)
                {
                    var it = _lvRules.Items[i];
                    var kws = it.SubItems[3].Text
                        .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    sb.Append("    {\"name\": ").Append(J(it.SubItems[1].Text));
                    sb.Append(", \"process\": ").Append(J(it.SubItems[2].Text));
                    sb.Append(", \"title_contains\": ").Append(JArr(kws));
                    sb.Append(", \"enabled\": ").Append(it.Checked ? "true" : "false");
                    sb.Append("}").AppendLine(i == _lvRules.Items.Count - 1 ? "" : ",");
                }
                sb.AppendLine("  ]");
                sb.AppendLine("}");
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
                MessageBox.Show(
                    WardenLang.Lang.Tr("已导出：\n") + dlg.FileName + "\n\n"
                    + _lvRules.Items.Count + WardenLang.Lang.Tr(" 条规则"),
                    "WARDEN 设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(WardenLang.Lang.Tr("导出失败：\n") + ex.Message, "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// ★ B5：从 json 文件导入规则 ✓
        ///   校验：必须有 rules 段；每条要有名称，且进程名或标题关键词至少一个 ✓
        ///   不合法的不静默丢弃 ✗ —— 报告"导入几条、跳过几条"✓
        /// </summary>
        private void ImportRules()
        {
            using var dlg = new OpenFileDialog
            {
                Title = WardenLang.Lang.Tr("从文件导入规则"),
                Filter = "WARDEN *|*.json",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            string json;
            try
            {
                json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageBox.Show(WardenLang.Lang.Tr("读不了这个文件：\n") + ex.Message, "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!json.Contains("\"rules\""))
            {
                MessageBox.Show(
                    WardenLang.Lang.Tr("这不是 WARDEN 规则文件（里面没有 rules 段）。"),
                    "WARDEN 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var good = new List<(string name, string proc, string kws, bool en)>();
            int skipped = 0;
            foreach (var blk in RegexBlocks(json, "rules"))
            {
                string name = JsonStr(blk, "name", "");
                string proc = JsonStr(blk, "process", "");
                var kws = JsonStrArray(blk, "title_contains");
                if (name.Length == 0 || (proc.Length == 0 && kws.Count == 0))
                {
                    skipped++;
                    continue;
                }
                good.Add((name, proc, string.Join(", ", kws), JsonBool(blk, "enabled", true)));
            }

            if (good.Count == 0)
            {
                MessageBox.Show(WardenLang.Lang.Tr("没有可导入的规则。"), "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var ans = MessageBox.Show(
                good.Count + WardenLang.Lang.Tr(" 条规则：\n\n「是」= 追加到现有规则\n"
                + "「否」= 替换现有规则\n「取消」= 什么都不做"),
                "WARDEN 设置", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.Cancel)
            {
                return;
            }
            if (ans == DialogResult.No)
            {
                _lvRules.Items.Clear();
            }

            foreach (var g in good)
            {
                var it = new ListViewItem("");
                it.SubItems.Add(g.name);
                it.SubItems.Add(g.proc);
                it.SubItems.Add(g.kws);
                it.Checked = g.en;
                _lvRules.Items.Add(it);
            }

            MessageBox.Show(
                WardenLang.Lang.Tr("导入完成：新增 ") + good.Count
                + WardenLang.Lang.Tr(" 条") + (skipped > 0
                    ? WardenLang.Lang.Tr("，跳过 ") + skipped
                      + WardenLang.Lang.Tr(" 条（缺少名称或匹配条件）") : "")
                + "\n\n" + WardenLang.Lang.Tr("记得点「保存配置」才会生效 ✓"),
                "WARDEN 设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 弹出"新增规则"对话框。
        /// </summary>
        private void AddRuleDialog()
        {
            using (var dlg = new RuleDialog(WardenLang.Lang.Tr("新增规则"), "", "", ""))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var it = new ListViewItem("");
                    it.SubItems.Add(dlg.RuleName);
                    it.SubItems.Add(dlg.ProcName);
                    it.SubItems.Add(dlg.Keywords);
                    it.Checked = true;
                    _lvRules.Items.Add(it);
                }
            }
        }

        /// <summary>
        /// 弹出"编辑规则"对话框，修改选中行的三个字段。
        /// </summary>
        private void EditRuleDialog()
        {
            if (_lvRules.SelectedItems.Count == 0)
            {
                MessageBox.Show(WardenLang.Lang.Tr("先选中一条规则。"), "WARDEN 设置");
                return;
            }
            var it = _lvRules.SelectedItems[0];
            using (var dlg = new RuleDialog(WardenLang.Lang.Tr("编辑规则"), it.SubItems[1].Text,
                                            it.SubItems[2].Text, it.SubItems[3].Text))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    it.SubItems[1].Text = dlg.RuleName;
                    it.SubItems[2].Text = dlg.ProcName;
                    it.SubItems[3].Text = dlg.Keywords;
                }
            }
        }

        // ---------------------------------------------------------------- 保护
        /// <summary>
        /// 【保护页】什么情况下不动手。
        /// 这几项是防误伤的第一道防线。
        /// </summary>
        private void BuildProtectTab()
        {
            Panel b;
            NewTab(WardenLang.Lang.T("set.tab.protect"), out b);
            int y = 10;
            _ckFg = Chk(WardenLang.Lang.Tr("当前活动窗口不处理（skip_if_foreground）"), 10, y, true); b.Controls.Add(_ckFg); y += 34;
            _ckEdit = Chk(WardenLang.Lang.Tr("含输入框的窗口不处理（skip_if_has_edit）"), 10, y, true); b.Controls.Add(_ckEdit); y += 34;
            _ckChild = Chk(WardenLang.Lang.Tr("含子控件的窗口不处理（skip_if_has_child_window）"), 10, y, true); b.Controls.Add(_ckChild); y += 34;
            _ckDry = Chk(WardenLang.Lang.Tr("★ 演习模式：只记日志不真动手（dry_run）"), 10, y, true); b.Controls.Add(_ckDry); y += 46;
            b.Controls.Add(Lbl(WardenLang.Lang.Tr("★ 建议：第一次用先开着演习模式跑几天，"), 10, y, 600)); y += 24;
            b.Controls.Add(Lbl(WardenLang.Lang.Tr("   看日志里它打算关谁，确认没有误伤，再关掉演习模式。"), 10, y, 600));
        }

        // ---------------------------------------------------------------- 处置
        /// <summary>
        /// 【处置页】处置强度与各种门槛。
        /// </summary>
        private void BuildActionTab()
        {
            Panel b;
            NewTab(WardenLang.Lang.T("set.tab.action"), out b);
            int y = 10;
            _ckTerm = Chk(WardenLang.Lang.Tr("允许使用强制手段（allow_terminate）"), 10, y, false); b.Controls.Add(_ckTerm); y += 42;

            b.Controls.Add(Lbl(WardenLang.Lang.Tr("动手门槛（分数达到才处理）"), 10, y, 250));
            _numApply = Num(270, y, 55, 1, 200); b.Controls.Add(_numApply); y += 32;
            b.Controls.Add(Lbl(WardenLang.Lang.Tr("强杀门槛（分数达到才考虑强杀）"), 10, y, 250));
            _numTermTh = Num(270, y, 75, 1, 300); b.Controls.Add(_numTermTh); y += 42;

            b.Controls.Add(Lbl(WardenLang.Lang.Tr("同一窗口失败几次才升级手段"), 10, y, 250));
            _numFail = Num(270, y, 3, 1, 50); b.Controls.Add(_numFail); y += 32;
            b.Controls.Add(Lbl(WardenLang.Lang.Tr("失败几次后才弹窗询问（受保护进程）"), 10, y, 250));
            _numAsk = Num(270, y, 6, 1, 200); b.Controls.Add(_numAsk); y += 42;

            b.Controls.Add(Lbl(WardenLang.Lang.Tr("第二段判据：至少隐藏多少次"), 10, y, 250));
            _numHides = Num(270, y, 8, 1, 200); b.Controls.Add(_numHides); y += 32;
            b.Controls.Add(Lbl(WardenLang.Lang.Tr("第二段兜底等待（毫秒）"), 10, y, 250));
            _numWait = Num(270, y, 6000, 1000, 60000); b.Controls.Add(_numWait); y += 44;

            _ckPrompt = Chk(WardenLang.Lang.Tr("允许弹窗询问用户（allow_consent_prompt）"), 10, y, true); b.Controls.Add(_ckPrompt); y += 30;
            _ckHonorSig = Chk("遵守\"有合法签名的进程不直接强杀\"（honor_signatures）", 10, y, true); b.Controls.Add(_ckHonorSig); y += 30;
            _ckHonorWl = Chk("遵守\"白名单进程不直接强杀\"（honor_whitelist_for_kill）", 10, y, true); b.Controls.Add(_ckHonorWl); y += 40;

            b.Controls.Add(Lbl("★ 说明：第二段弹窗现在是按\"追不上\"判断的 ——", 10, y, 700)); y += 24;
            b.Controls.Add(Lbl(WardenLang.Lang.Tr("   已隐藏够多次但窗口数没减少，就立刻弹（几秒内），"), 10, y, 700)); y += 24;
            b.Controls.Add(Lbl(WardenLang.Lang.Tr("   上面那个毫秒值只是兜底上限。"), 10, y, 700));
        }

        // ---------------------------------------------------------------- 名单
        /// <summary>
        /// 【名单页】白名单 / 永久授权 / 强杀历史。
        /// ★ 强杀历史可以清空 —— 用于重置"复活"判定。
        /// </summary>
        private void BuildListsTab()
        {
            Panel b;
            NewTab(WardenLang.Lang.T("set.tab.lists"), out b);

            b.Controls.Add(Lbl(WardenLang.Lang.Tr("白名单（这些进程永不处理，一行一个）"), 8, 8, 380));
            _txtWl = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(8, 30),
                Size = new Size(380, 150)
            };
            b.Controls.Add(_txtWl);

            b.Controls.Add(Lbl(WardenLang.Lang.Tr("永久允许强杀（一行一个）"), 400, 8, 380));
            _txtConsent = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(400, 30),
                Size = new Size(380, 150)
            };
            b.Controls.Add(_txtConsent);

            b.Controls.Add(Lbl("★ 强杀历史（用于检测\"杀了又复活\"，清空可重置判定）", 8, 192, 600));
            _txtTermHist = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(8, 216),
                Size = new Size(380, 140)
            };
            b.Controls.Add(_txtTermHist);

            var btnClear = new Button { Text = WardenLang.Lang.Tr("清空强杀历史"), Location = new Point(400, 216), Size = new Size(140, 30) };
            btnClear.Click += (s, e) => _txtTermHist.Text = "";
            b.Controls.Add(btnClear);

            b.Controls.Add(Lbl(WardenLang.Lang.Tr("★ 清空后：如果同一个程序再次出现，"), 400, 256, 380));
            b.Controls.Add(Lbl("   弹窗不会再推荐\"永久允许强杀\"。", 400, 280, 380));
        }

        // ---------------------------------------------------------------- 安全
        /// <summary>
        /// 【安全页】启动自校验与硬黑名单展示。
        /// ★ 硬黑名单是只读的，不允许在这里改 ✗
        /// </summary>
        private void BuildSecurityTab()
        {
            Panel b;
            NewTab(WardenLang.Lang.T("set.tab.safety"), out b);
            int y = 10;
            var ckSelf = Chk(WardenLang.Lang.Tr("启动时自校验（防止程序被调包）"), 10, y, true); b.Controls.Add(ckSelf);
            _ckSelfRef = ckSelf; y += 34;
            var ckBlock = Chk(WardenLang.Lang.Tr("★ 自校验失败时拒绝运行（拉停）"), 10, y, true); b.Controls.Add(ckBlock);
            _ckBlockRef = ckBlock; y += 46;

            b.Controls.Add(Lbl(WardenLang.Lang.Tr("期望的自身哈希（签名脚本会写入）"), 10, y, 400)); y += 24;
            _txtHash = new TextBox { Location = new Point(10, y), Size = new Size(600, 24) };
            b.Controls.Add(_txtHash); y += 32;

            var btnCalc = new Button { Text = WardenLang.Lang.Tr("用当前 WARDEN.exe 的哈希填入"), Location = new Point(10, y), Size = new Size(240, 30) };
            btnCalc.Click += (s, e) => FillHash();
            b.Controls.Add(btnCalc); y += 46;

            b.Controls.Add(Lbl(WardenLang.Lang.Tr("── 硬黑名单（系统关键进程，永不强杀，不可修改）──"), 10, y, 620)); y += 24;
            _txtHard = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(246, 246, 246),
                Font = new Font("Consolas", 9f),
                Location = new Point(10, y),
                Size = new Size(760, 190)
            };
            _txtHard.Text =
                "csrss.exe   wininit.exe   winlogon.exe   services.exe   lsass.exe   smss.exe\r\n" +
                "svchost.exe   dwm.exe   explorer.exe   System   Registry   MemCompression\r\n" +
                "fontdrvhost.exe   ctfmon.exe   sihost.exe   taskhostw.exe   audiodg.exe\r\n" +
                "spoolsv.exe   MsMpEng.exe   SearchIndexer.exe   ... （共 41 条）\r\n" +
                "\r\n" +
                "另外：映像路径在 System32 / SysWOW64 / WinSxS / Servicing /\r\n" +
                "      SystemApps 下的进程，也一律不强杀。\r\n" +
                "\r\n" +
                "★ 这两道是【不可配置】的 —— 它们保护的是系统本身。";
            b.Controls.Add(_txtHard);
        }

        private CheckBox _ckSelfRef;
        private CheckBox _ckBlockRef;

        // ---------------------------------------------------------------- 日志
        /// <summary>
        /// 【日志页】看最近日志。
        /// </summary>
        private void BuildLogTab()
        {
            Panel b;
            NewTab(WardenLang.Lang.T("set.tab.log"), out b);

            var btnRefresh = new Button { Text = WardenLang.Lang.Tr("刷新日志"), Location = new Point(0, 0), Size = new Size(100, 28) };
            btnRefresh.Click += (s, e) => RefreshLog();
            b.Controls.Add(btnRefresh);

            _numLines = new NumericUpDown
            {
                Location = new Point(108, 2),
                Size = new Size(80, 24),
                Minimum = 20,
                Maximum = 5000,
                Value = 200
            };
            _numLines.ValueChanged += (s, e) => RefreshLog();
            b.Controls.Add(_numLines);
            b.Controls.Add(Lbl(WardenLang.Lang.Tr("行（最近的）"), 194, 6, 90));

            var btnOpen = new Button { Text = WardenLang.Lang.Tr("打开日志所在文件夹"), Location = new Point(290, 0), Size = new Size(170, 28) };
            btnOpen.Click += (s, e) =>
            {
                if (Directory.Exists(_toolDir)) System.Diagnostics.Process.Start("explorer.exe", _toolDir);
            };
            b.Controls.Add(btnOpen);

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 8.5f),
                Location = new Point(0, 36),
                Size = new Size(770, 470),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            b.Controls.Add(_txtLog);
        }

        // ================================================================
        //  读写配置
        // ================================================================
        /// <summary>
        /// 从 JSON 里取一个字符串字段（简易解析，够用即可）。
        /// </summary>
        private static string JsonStr(string json, string key, string def)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json,
                "\"" + System.Text.RegularExpressions.Regex.Escape(key) +
                "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value : def;
        }

        /// <summary>
        /// 从 JSON 里取一个布尔字段。
        /// </summary>
        private static bool JsonBool(string json, string key, bool def)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json,
                "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
            return m.Success ? m.Groups[1].Value == "true" : def;
        }

        /// <summary>
        /// 从 JSON 里取一个数字字段。
        /// </summary>
        private static decimal JsonNum(string json, string key, decimal def)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json,
                "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9.]+)");
            if (m.Success && decimal.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal v))
            {
                return v;
            }
            return def;
        }

        /// <summary>
        /// 从 JSON 里取一个字符串数组。
        /// </summary>
        private static List<string> JsonStrArray(string json, string key)
        {
            var res = new List<string>();
            var m = System.Text.RegularExpressions.Regex.Match(json,
                "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*\\[(.*?)\\]",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) return res;
            foreach (System.Text.RegularExpressions.Match mm in
                     System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                res.Add(mm.Groups[1].Value);
            }
            return res;
        }

        /// <summary>
        /// 读配置并填到界面上。
        /// ★ 找不到配置时用内置默认值，不会崩 ✓
        /// </summary>
        private void LoadConfig()
        {
            string json = "";
            try
            {
                if (File.Exists(CfgPath)) json = File.ReadAllText(CfgPath, Encoding.UTF8);
            }
            catch { }

            // 保护
            _ckFg.Checked = JsonBool(json, "skip_if_foreground", true);
            _ckEdit.Checked = JsonBool(json, "skip_if_has_edit", true);
            _ckChild.Checked = JsonBool(json, "skip_if_has_child_window", true);
            _ckDry.Checked = JsonBool(json, "dry_run", true);

            // 处置
            _ckTerm.Checked = JsonBool(json, "allow_terminate", false);
            _ckPrompt.Checked = JsonBool(json, "allow_consent_prompt", true);
            _ckHonorSig.Checked = JsonBool(json, "honor_signatures", true);
            _ckHonorWl.Checked = JsonBool(json, "honor_whitelist_for_kill", true);
            _numApply.Value = JsonNum(json, "apply_threshold", 55);
            _numTermTh.Value = JsonNum(json, "terminate_threshold", 75);
            _numFail.Value = JsonNum(json, "failures_before_kill", 3);
            _numAsk.Value = JsonNum(json, "consent_after_failures", 6);
            _numHides.Value = JsonNum(json, "second_stage_min_hides", 8);
            _numWait.Value = JsonNum(json, "second_stage_max_wait_ms", 6000);

            // 名单
            _txtWl.Text = string.Join("\r\n", JsonStrArray(json, "process_whitelist"));
            _txtConsent.Text = string.Join("\r\n", JsonStrArray(json, "consented_processes"));
            _txtTermHist.Text = string.Join("\r\n", JsonStrArray(json, "terminated_history"));

            // 安全
            _txtHash.Text = JsonStr(json, "expected_self_hash", "");
            // ★★★ 用户要求（2026-10-10）：哈希要【默认写入】✗
            //   以前这里是空的（得用户自己点按钮）→ 现在打开就自动填 ✓
            if (string.IsNullOrWhiteSpace(_txtHash.Text))
            {
                _txtHash.Text = HashOfSelf();
            }
            _ckSelfRef.Checked = JsonBool(json, "self_check_on_start", true);
            _ckBlockRef.Checked = JsonBool(json, "self_check_block_on_fail", true);

            // 规则
            _lvRules.Items.Clear();
            foreach (var blk in RegexBlocks(json, "rules"))
            {
                var name = JsonStr(blk, "name", "");
                if (string.IsNullOrEmpty(name)) continue;
                var it = new ListViewItem("");
                it.SubItems.Add(name);
                it.SubItems.Add(JsonStr(blk, "process", ""));
                it.SubItems.Add(string.Join(", ", JsonStrArray(blk, "title_contains")));
                it.Checked = JsonBool(blk, "enabled", true);
                _lvRules.Items.Add(it);
            }
        }

        /// <summary>
        /// 从 JSON 里按大括号配对，逐个取出对象块。
        /// ★ 为什么不用完整 JSON 解析器：
        ///   这个小工具刻意不引入依赖，简易解析够用 ✓
        /// </summary>
        private static IEnumerable<string> RegexBlocks(string json, string arrayKey)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json,
                "\"" + arrayKey + "\"\\s*:\\s*\\[(.*)\\]\\s*[,}]",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) yield break;
            string body = m.Groups[1].Value;
            int depth = 0, start = -1;
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (body[i] == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        yield return body.Substring(start, i - start + 1);
                        start = -1;
                    }
                }
            }
        }

        /// <summary>
        /// 把字符串转成合法的 JSON 字符串字面量（转义引号、反斜杠等）。
        /// </summary>
        private static string J(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>
        /// 把一组字符串转成 JSON 数组字面量。
        /// </summary>
        private static string JArr(IEnumerable<string> items)
            => "[" + string.Join(",", items.Where(x => !string.IsNullOrWhiteSpace(x))
                                           .Select(x => J(x.Trim()))) + "]";

        /// <summary>
        /// 把界面上的内容写成配置文件。
        /// ★ 手写 JSON 而不是靠序列化库：
        ///   这样字段顺序和注释都能自己控制，人也读得懂 ✓
        /// </summary>
        private void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(_toolDir);
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_说明\": [");
                sb.AppendLine("    \"WARDEN 配置 —— 由设置工具生成\",");
                sb.AppendLine("    \"★ 改完保存即可，WARDEN 每轮会重新读取配置，不用重启\"");
                sb.AppendLine("  ],");

                sb.AppendLine("  \"rules\": [");
                for (int i = 0; i < _lvRules.Items.Count; i++)
                {
                    var it = _lvRules.Items[i];
                    var kws = it.SubItems[3].Text
                        .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    sb.Append("    {\"name\": ").Append(J(it.SubItems[1].Text));
                    sb.Append(", \"process\": ").Append(J(it.SubItems[2].Text));
                    sb.Append(", \"title_contains\": ").Append(JArr(kws));
                    sb.Append(", \"enabled\": ").Append(it.Checked ? "true" : "false");
                    sb.Append("}").AppendLine(i == _lvRules.Items.Count - 1 ? "" : ",");
                }
                sb.AppendLine("  ],");

                sb.AppendLine("  \"behavior\": {");
                sb.AppendLine("    \"rule_hit\": 40, \"steals_focus\": 12, \"topmost\": 10,");
                sb.AppendLine("    \"no_edit\": 8, \"no_children\": 6, \"respawn\": 15,");
                sb.AppendLine("    \"small_floating\": 8, \"title_churn\": 10,");
                sb.Append("    \"apply_threshold\": ").Append((int)_numApply.Value).AppendLine(",");
                sb.Append("    \"terminate_threshold\": ").Append((int)_numTermTh.Value).AppendLine();
                sb.AppendLine("  },");

                sb.AppendLine("  \"action\": {");
                sb.Append("    \"allow_terminate\": ").Append(_ckTerm.Checked ? "true" : "false").AppendLine(",");
                sb.AppendLine("    \"l1_wait_ms\": 500, \"l2_wait_ms\": 1000,");
                sb.Append("    \"failures_before_kill\": ").Append((int)_numFail.Value).AppendLine(",");
                sb.AppendLine("    \"deadline_ms\": 4500,");
                sb.Append("    \"honor_signatures\": ").Append(_ckHonorSig.Checked ? "true" : "false").AppendLine(",");
                sb.Append("    \"honor_whitelist_for_kill\": ").Append(_ckHonorWl.Checked ? "true" : "false").AppendLine(",");
                sb.Append("    \"allow_consent_prompt\": ").Append(_ckPrompt.Checked ? "true" : "false").AppendLine(",");
                sb.Append("    \"consent_after_failures\": ").Append((int)_numAsk.Value).AppendLine(",");
                sb.Append("    \"second_stage_min_hides\": ").Append((int)_numHides.Value).AppendLine(",");
                sb.Append("    \"second_stage_max_wait_ms\": ").Append((int)_numWait.Value).AppendLine();
                sb.AppendLine("  },");

                sb.AppendLine("  \"protect\": {");
                sb.Append("    \"skip_if_foreground\": ").Append(_ckFg.Checked ? "true" : "false").AppendLine(",");
                sb.Append("    \"skip_if_has_edit\": ").Append(_ckEdit.Checked ? "true" : "false").AppendLine(",");
                sb.Append("    \"skip_if_has_child_window\": ").Append(_ckChild.Checked ? "true" : "false").AppendLine(",");
                sb.Append("    \"dry_run\": ").Append(_ckDry.Checked ? "true" : "false").AppendLine();
                sb.AppendLine("  },");

                var Split = new Func<string, List<string>>(t =>
                    (t ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(x => x.Trim()).Where(x => x.Length > 0).ToList());

                sb.Append("  \"process_whitelist\": ").Append(JArr(Split(_txtWl.Text))).AppendLine(",");
                sb.Append("  \"consented_processes\": ").Append(JArr(Split(_txtConsent.Text))).AppendLine(",");
                sb.Append("  \"terminated_history\": ").Append(JArr(Split(_txtTermHist.Text))).AppendLine(",");
                sb.Append("  \"expected_self_hash\": ").Append(J(_txtHash.Text.Trim())).AppendLine(",");
                // ★ 语言选择（用户手选过就固化下来 ✓）
                if (_cbLang != null && _cbLang.SelectedItem is string langCode)
                {
                    sb.Append("  \"lang\": ").Append(J(langCode)).AppendLine(",");
                }
                sb.Append("  \"self_check_on_start\": ").Append(_ckSelfRef.Checked ? "true" : "false").AppendLine(",");
                sb.Append("  \"self_check_block_on_fail\": ").Append(_ckBlockRef.Checked ? "true" : "false");
                sb.AppendLine();
                sb.AppendLine("}");

                File.WriteAllText(CfgPath, sb.ToString(), new UTF8Encoding(false));
                MessageBox.Show(WardenLang.Lang.Tr("已保存到：\n") + CfgPath, "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(WardenLang.Lang.Tr("保存失败：\n") + ex.Message, "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ================================================================
        //  动作
        // ================================================================
        /// <summary>
        /// 计算 WARDEN.exe 的 SHA256 并填进输入框。
        /// ★ 这个值就是启动自校验的基线 ✓
        /// </summary>
        /// <summary>
        /// ★ A59：算 WARDEN.exe 的 SHA256（算不出返回空串，绝不抛）✓
        ///   抽出来是为了让"打开设置界面时自动填入"也能用 ✓
        /// </summary>
        private string HashOfSelf()
        {
            try
            {
                if (!File.Exists(ExePath))
                {
                    return "";
                }
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = File.OpenRead(ExePath);
                return BitConverter.ToString(sha.ComputeHash(fs))
                                  .Replace("-", "").ToLowerInvariant();
            }
            catch
            {
                return "";
            }
        }

        private void FillHash()
        {
            if (!File.Exists(ExePath))
            {
                MessageBox.Show(WardenLang.Lang.Tr("找不到：\n") + ExePath + "\n\n请把本工具和 WARDEN.exe 放在同一个目录。",
                                "WARDEN 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string h = HashOfSelf();
            if (h.Length == 64)
            {
                _txtHash.Text = h;
                MessageBox.Show(WardenLang.Lang.Tr("已填入当前 WARDEN.exe 的 SHA256：\n") + h, "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(WardenLang.Lang.Tr("总算不出哈希 —— 检查一下 WARDEN.exe 能不能读 ✓"),
                                "WARDEN 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 启动 WARDEN.exe。
        /// ★ 先删掉 .stop 文件 —— 否则它会立刻自己退出 ✗
        /// </summary>
        private void StartWarden()
        {
            if (!File.Exists(ExePath))
            {
                MessageBox.Show(WardenLang.Lang.Tr("找不到：\n") + ExePath + "\n\n请把本工具和 WARDEN.exe 放在同一个目录。",
                                "WARDEN 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                if (File.Exists(StopPath)) File.Delete(StopPath);
                System.Diagnostics.Process.Start(ExePath);
                System.Threading.Thread.Sleep(700);
                UpdateStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(WardenLang.Lang.Tr("启动失败：\n") + ex.Message, "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 停止 WARDEN。
        /// ★ 两种方式都要：先写 .stop 文件让它优雅退出，
        ///   如果没退就强制结束 ✓
        /// </summary>
        private void StopWarden()
        {
            try
            {
                Directory.CreateDirectory(_toolDir);
                File.WriteAllText(StopPath, "stop", Encoding.UTF8);
                System.Threading.Thread.Sleep(1500);
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("WARDEN"))
                {
                    try { p.Kill(); } catch { }
                }
                System.Threading.Thread.Sleep(500);
                UpdateStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(WardenLang.Lang.Tr("停止失败：\n") + ex.Message, "WARDEN 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 刷新底部状态栏（运行中？自校验结果？）。
        /// </summary>
        private void UpdateStatus()
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("WARDEN");
                if (procs.Length > 0)
                {
                    _lblStatus.Text = WardenLang.Lang.Tr("状态：✓ 运行中（PID ") + procs[0].Id + "）";
                    _lblStatus.ForeColor = Color.Green;
                }
                else
                {
                    _lblStatus.Text = WardenLang.Lang.Tr("状态：· 未运行");
                    _lblStatus.ForeColor = Color.Gray;
                }

                if (File.Exists(LogPath))
                {
                    var tail = TailLines(LogPath, 400);
                    var sc = tail.LastOrDefault(l => l.Contains("[自检] 结果="));
                    if (sc != null)
                    {
                        if (sc.Contains("结果=通过"))
                        {
                            _lblStatus.Text += "    " + WardenLang.Lang.Tr("自校验：✓ 通过");
                        }
                        else
                        {
                            _lblStatus.Text += "    " + WardenLang.Lang.Tr("自校验：★ 未通过");
                            _lblStatus.ForeColor = Color.Red;
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 读文件最后 N 行。
        /// ★ 用 FileShare.ReadWrite 打开：
        ///   因为 WARDEN 正在写这个日志，独占打开会失败 ✗
        /// </summary>
        private static List<string> TailLines(string path, int n)
        {
            try
            {
                var q = new Queue<string>(n);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        q.Enqueue(line);
                        if (q.Count > n) q.Dequeue();
                    }
                }
                return q.ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>
        /// 刷新日志显示。
        /// </summary>
        private void RefreshLog()
        {
            if (!File.Exists(LogPath))
            {
                _txtLog.Text = WardenLang.Lang.Tr("（还没有日志文件）");
                return;
            }
            var lines = TailLines(LogPath, (int)_numLines.Value);
            _txtLog.Text = string.Join("\r\n", lines);
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }
    }

    /// <summary>规则编辑对话框。</summary>
    internal sealed class RuleDialog : Form
    {
        private readonly TextBox _t1, _t2, _t3;
        public string RuleName => _t1.Text.Trim();
        public string ProcName => _t2.Text.Trim();
        public string Keywords => _t3.Text.Trim();

        /// <summary>
        /// 规则编辑对话框。
        /// </summary>
        public RuleDialog(string title, string name, string proc, string kws)
        {
            Text = title;
            ClientSize = new Size(470, 250);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            Controls.Add(new Label { Text = WardenLang.Lang.Tr("规则名称"), Location = new Point(15, 20), Size = new Size(110, 20) });
            _t1 = new TextBox { Location = new Point(130, 17), Size = new Size(310, 24), Text = name };
            Controls.Add(_t1);

            Controls.Add(new Label { Text = WardenLang.Lang.Tr("进程名（可空）"), Location = new Point(15, 58), Size = new Size(110, 20) });
            _t2 = new TextBox { Location = new Point(130, 55), Size = new Size(310, 24), Text = proc };
            Controls.Add(_t2);

            Controls.Add(new Label { Text = WardenLang.Lang.Tr("标题关键词"), Location = new Point(15, 96), Size = new Size(110, 20) });
            _t3 = new TextBox { Location = new Point(130, 93), Size = new Size(310, 24), Text = kws };
            Controls.Add(_t3);
            Controls.Add(new Label
            {
                Text = WardenLang.Lang.Tr("（多个用英文逗号分隔）"),
                Location = new Point(130, 118),
                Size = new Size(310, 20),
                ForeColor = Color.Gray
            });

            var ok = new Button { Text = WardenLang.Lang.Tr("确定"), Location = new Point(240, 165), Size = new Size(95, 30), DialogResult = DialogResult.OK };
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_t2.Text) && string.IsNullOrWhiteSpace(_t3.Text))
                {
                    MessageBox.Show(WardenLang.Lang.Tr("进程名和标题关键词至少要填一个。"), "WARDEN 设置");
                    DialogResult = DialogResult.None;
                }
            };
            Controls.Add(ok);

            var cc = new Button { Text = WardenLang.Lang.Tr("取消"), Location = new Point(345, 165), Size = new Size(95, 30), DialogResult = DialogResult.Cancel };
            Controls.Add(cc);

            AcceptButton = ok;
            CancelButton = cc;
        }
    }
}
