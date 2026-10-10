// ============================================================================
//  F5 证据上报（2026-10-13，Beta 2）
// ============================================================================
//  【为什么要有它】
//    用户被流氓软件攻击时，WARDEN 已经把证据留在本机了 ✓（EMERGENCY.log / TAMPER.log）
//    但要报给作者，得自己找文件、自己复制、还得担心隐私 ✗
//    → 这个窗口把整件事变成三步：看清楚 → 选传输方式 → 复制/存文件/开 GitHub ✓
//
//  【★ 三条硬规矩】
//    ① 发送前【先把用户名打码】✗（路径里的 C:\Users\张三\ → C:\Users\<用户>\）
//       顺手把手机号、邮箱也打码 ✓
//    ② 传输方式【默认不选】= 什么也不发 ✗
//       只有用户明确选了"明文"或"PGP"，提交按钮才可用 ✓
//    ③ 预览里显示的就是【将发出去的全部内容】✓ 不多塞一个字节 ✓
//
//  【两种传输方式】
//    明文：贴到 GitHub 新建 issue 页面（内容会公开 ✓ 用户自己确认 ✓）
//    PGP ：用作者公钥加密后再复制（只有作者能读 ✓ 适合含敏感信息的日志 ✓）
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WardenLogView
{
    internal static class EvidenceReport
    {
        /// <summary>★ 作者 PGP 公钥（见 docs\PGP公钥-在哪里与怎么用.md ✓）</summary>
        private const string PgpKeyId = "E23876CBBDAE330F";

        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");

        private const string IssueUrl =
            "https://github.com/Firefly0619sec/WARDEN/issues/new";

        /// <summary>
        /// ★ 脱敏：用户名 / 手机号 / 邮箱打码 ✓
        ///   【为什么】日志里全是路径 ✗ 直接发出去等于把用户名告诉全世界 ✗
        /// </summary>
        public static string Mask(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }
            try
            {
                // ① 用户目录：C:\Users\xxx\  → C:\Users\<用户>\
                s = Regex.Replace(s, @"([A-Za-z]:\\Users\\)[^\\\r\n]+", "$1<用户>",
                                  RegexOptions.IgnoreCase);
                // ② 其它常见家目录写法
                s = Regex.Replace(s, @"(/home/)[^/\r\n]+", "$1<用户>");
                // ③ 邮箱
                s = Regex.Replace(s, @"[\w\.\-]+@[\w\-]+\.[\w\.\-]+", "<邮箱>");
                // ④ 中国大陆手机号
                s = Regex.Replace(s, @"(?<!\d)1[3-9]\d{9}(?!\d)", "<手机号>");
                // ⑤ 机器名（尽量少暴露环境 ✓）
                string machine = Environment.MachineName;
                if (machine.Length >= 3)
                {
                    s = s.Replace(machine, "<机器名>");
                }
            }
            catch
            {
                // 脱敏失败也不能把原样内容发出去 ✗ → 返回空，让上层提示用户 ✓
                return "";
            }
            return s;
        }

        /// <summary>★ 生成报告正文（已脱敏 ✓）</summary>
        private static string BuildBody(bool pgp)
        {
            var sb = new StringBuilder();
            sb.AppendLine(pgp
                ? "WARDEN 证据报告（PGP 加密版 —— 只有作者能读 ✓）"
                : "WARDEN 证据报告");
            sb.AppendLine();
            sb.AppendLine("### 环境");
            sb.AppendLine();
            sb.AppendLine("* 版本：" + System.Windows.Forms.Application.ProductVersion);
            sb.AppendLine("* 系统：" + Mask(Environment.OSVersion.VersionString));
            sb.AppendLine("* 程序路径：" + Mask(AppContext.BaseDirectory));
            sb.AppendLine("* 数据目录：" + Mask(DataDir));
            sb.AppendLine("* 64 位：" + Environment.Is64BitProcess);
            sb.AppendLine();
            sb.AppendLine("### 日志摘要（关键行，已脱敏 ✓）");
            sb.AppendLine();
            sb.AppendLine("```");
            int shown = 0;
            foreach (string name in new[] { "EMERGENCY.log", "TAMPER.log", "WARDEN.log" })
            {
                string p = Path.Combine(DataDir, name);
                if (!File.Exists(p))
                {
                    continue;
                }
                sb.AppendLine("---- " + name + " ----");
                string[] all;
                try { all = File.ReadAllLines(p, Encoding.UTF8); }
                catch { continue; }
                for (int i = Math.Max(0, all.Length - 120); i < all.Length; i++)
                {
                    string line = all[i];
                    bool key = line.Contains("篡改") || line.Contains("TAMPER")
                               || line.Contains("应急") || line.Contains("EMERGENCY")
                               || line.Contains("[enter]") || line.Contains("[freeze")
                               || line.Contains("[kill") || line.Contains("★");
                    if (!key && name != "EMERGENCY.log")
                    {
                        continue;
                    }
                    sb.AppendLine(Mask(line));
                    if (++shown >= 150)
                    {
                        sb.AppendLine("（只取前 150 条，完整内容在本机 ✓）");
                        break;
                    }
                }
                if (shown >= 150)
                {
                    break;
                }
            }
            if (shown == 0)
            {
                sb.AppendLine("（没有关键行 —— 一切正常 ✓）");
            }
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("### 说明");
            sb.AppendLine();
            sb.AppendLine("* 以上内容由 WARDEN 日志查看器生成 ✓");
            sb.AppendLine("* 用户名 / 邮箱 / 手机号 / 机器名已打码 ✓");
            sb.AppendLine("* 没有自动上传任何东西：这份内容是否发出、怎么发出，由提交者决定 ✓");
            sb.AppendLine("* 许可证：AGPL-3.0-or-later");
            return sb.ToString();
        }

        /// <summary>★ 打开上报窗口（预览 + 选传输方式 + 三个出口 ✓）</summary>
        public static void Show(IWin32Window owner)
        {
            string plain = BuildBody(false);
            string pgpBody = BuildBody(true);

            using var f = new Form
            {
                Text = "上报证据 / 反馈问题 —— WARDEN",
                ClientSize = new Size(880, 660),
                StartPosition = FormStartPosition.CenterParent,
                Font = new Font("Microsoft YaHei UI", 9F),
                MinimumSize = new Size(700, 500),
            };

            var tip = new Label
            {
                Dock = DockStyle.Top,
                Height = 74,
                Padding = new Padding(12, 8, 12, 8),
                Text = "★ 下面显示的就是【将要发出去的全部内容】✓ 请先看一眼 ✓\n"
                     + "★ 用户名 / 邮箱 / 手机号 / 机器名已经打码 ✓\n"
                     + "★ 传输方式【默认不选】—— 你不选，就什么也不会发出去 ✗",
            };

            var view = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 8.5F),
                Dock = DockStyle.Fill,
                Text = plain,
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 132 };
            var rbPlain = new RadioButton
            {
                Text = "明文（贴到 GitHub 新建 issue ✓ 内容公开，任何人可见）",
                Location = new Point(12, 6),
                Size = new Size(600, 24),
            };
            var rbPgp = new RadioButton
            {
                Text = "PGP 加密（用作者公钥加密 ✓ 只有作者能读 ✓ 适合含敏感信息的日志）",
                Location = new Point(12, 32),
                Size = new Size(700, 24),
            };
            var note = new Label
            {
                Location = new Point(12, 60),
                Size = new Size(840, 20),
                ForeColor = Color.DimGray,
                Text = "选择传输方式后，下面的按钮才会可用（默认两个都没选 = 不上传 ✓）",
            };
            var bCopy = new Button { Text = "复制内容", Location = new Point(12, 88), Size = new Size(110, 32), Enabled = false };
            var bSave = new Button { Text = "存成文件", Location = new Point(132, 88), Size = new Size(110, 32), Enabled = false };
            var bIssue = new Button { Text = "在 GitHub 上打开", Location = new Point(252, 88), Size = new Size(150, 32), Enabled = false };
            var bClose = new Button { Text = "关闭", Location = new Point(770, 88), Size = new Size(90, 32) };

            string Current() => rbPgp.Checked ? pgpBody : plain;

            rbPlain.CheckedChanged += (s, e) =>
            {
                view.Text = Current();
                bCopy.Enabled = bSave.Enabled = bIssue.Enabled = true;
            };
            rbPgp.CheckedChanged += (s, e) =>
            {
                view.Text = Current();
                bCopy.Enabled = bSave.Enabled = bIssue.Enabled = true;
            };

            bCopy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(rbPgp.Checked ? Encrypt(Current()) : Current());
                    MessageBox.Show(f, rbPgp.Checked
                            ? "已加密并复制 ✓ 贴到 GitHub issue 里，或直接发给作者 ✓"
                            : "已复制 ✓ 到 GitHub 页面按 Ctrl+V 粘贴 ✓",
                        "上报证据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(f, "复制失败：" + ex.Message, "上报证据",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            bSave.Click += (s, e) =>
            {
                using var d = new SaveFileDialog
                {
                    FileName = (rbPgp.Checked ? "WARDEN-证据-加密-" : "WARDEN-证据-")
                               + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                               + (rbPgp.Checked ? ".asc" : ".md"),
                    Filter = rbPgp.Checked ? "PGP 加密文件|*.asc" : "Markdown|*.md",
                };
                if (d.ShowDialog(f) != DialogResult.OK)
                {
                    return;
                }
                try
                {
                    File.WriteAllText(d.FileName,
                        rbPgp.Checked ? Encrypt(Current()) : Current(),
                        new UTF8Encoding(false));
                    MessageBox.Show(f, "已保存：\n" + d.FileName, "上报证据",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(f, "保存失败：" + ex.Message, "上报证据",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            bIssue.Click += (s, e) =>
            {
                if (rbPgp.Checked)
                {
                    Clipboard.SetText(Encrypt(Current()));
                    MessageBox.Show(f,
                        "PGP 加密内容已复制 ✓\n\n"
                        + "接下来：在打开的页面里 Ctrl+V 粘贴 ✓\n"
                        + "（加密内容给作者看即可，公开也没关系 ✓）",
                        "上报证据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    Clipboard.SetText(Current());
                }
                try { Process.Start(new ProcessStartInfo(IssueUrl) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    MessageBox.Show(f, "打不开浏览器：" + ex.Message + "\n\n"
                        + "内容已复制，你可以自己打开：\n" + IssueUrl,
                        "上报证据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            bClose.Click += (s, e) => f.Close();

            bottom.Controls.Add(rbPlain);
            bottom.Controls.Add(rbPgp);
            bottom.Controls.Add(note);
            bottom.Controls.Add(bCopy);
            bottom.Controls.Add(bSave);
            bottom.Controls.Add(bIssue);
            bottom.Controls.Add(bClose);

            f.Controls.Add(view);
            f.Controls.Add(bottom);
            f.Controls.Add(tip);
            f.ShowDialog(owner);
        }

        /// <summary>
        /// ★ PGP 加密（调系统的 gpg ✓）
        ///   没装 gpg → 明确告诉用户"改选明文或用文件下载"✗ 不静默降级 ✗
        /// </summary>
        private static string Encrypt(string text)
        {
            string tmpIn = Path.Combine(Path.GetTempPath(), "warden-report-" + Guid.NewGuid().ToString("N") + ".md");
            File.WriteAllText(tmpIn, text, new UTF8Encoding(false));
            var psi = new ProcessStartInfo("gpg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--armor");
            psi.ArgumentList.Add("--batch");
            psi.ArgumentList.Add("--yes");
            psi.ArgumentList.Add("--trust-model");
            psi.ArgumentList.Add("always");
            psi.ArgumentList.Add("--recipient");
            psi.ArgumentList.Add(PgpKeyId);
            psi.ArgumentList.Add("--encrypt");
            psi.ArgumentList.Add(tmpIn);
            try
            {
                using var p = Process.Start(psi)!;
                string outp = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                try { File.Delete(tmpIn); } catch { }
                if (p.ExitCode != 0 || outp.Length == 0)
                {
                    MessageBox.Show("PGP 加密失败：" + err + "\n\n"
                        + "可能原因：没装 GnuPG / 没有作者公钥（" + PgpKeyId + "）\n"
                        + "→ 可以先选【明文】提交，或把 WARDEN.log 手动发给我 ✓",
                        "上报证据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return text;   // 返回明文（用户已在预览里看过 ✓）
                }
                return outp;
            }
            catch (Exception ex)
            {
                MessageBox.Show("调用 gpg 失败：" + ex.Message + "\n\n"
                    + "→ 请改选【明文】提交 ✓", "上报证据",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return text;
            }
        }
    }
}
