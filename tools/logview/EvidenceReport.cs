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

//  2026-10-11 · Beta 3 / 0.3.2：去掉 WinForms ✗ 改原生 Win32 + Native AOT ✓
//    · 24 处 MessageBox.Show → 原生 MessageBoxW（★ YesNo 拿返回值 / 这里全是 OK 型 ✓）
//    · SaveFileDialog → comdlg32 GetSaveFileNameW ✓
//    · Show(IWin32Window) → Show(IntPtr owner) ✓
//    · Clipboard.SetText → 原生剪贴板 ✓ Application.ProductVersion → 原生读版本资源 ✓
//    · 窗口本体 → EvidenceForm.cs（原生控件 ✓ 默认不上传 ✗ 明文/PGP 二选一 ✓ 预览可见 ✓）
//    · ★ Mask / BuildBody / Encrypt 三个算法与文案一字未改 ✓

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WardenLogView
{
    internal static class EvidenceReport
    {
        /// <summary>★ 作者 PGP 公钥（见 docs\PGP公钥-在哪里与怎么用.md ✓）</summary>
        private const string PgpKeyId = "E23876CBBDAE330F";

        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");

        internal const string IssueUrl =
            "https://github.com/Firefly0619sec/WARDEN/issues/new";

        /// <summary>原生窗口用来取两版正文（明文 / PGP ✓ 都是已脱敏的同一个 BuildBody ✓）</summary>
        internal static string PlainBody() => BuildBody(false);
        internal static string PgpBody() => BuildBody(true);

        /// <summary>原生窗口调用的 PGP 加密入口（Encrypt 本体原样保留 ✓）</summary>
        internal static string EncryptForUi(string text, IntPtr owner) => Encrypt(text, owner);

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
            sb.AppendLine("* 版本：" + LogViewWin32.ProductVersion());
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

        /// <summary>★ 打开上报窗口（预览 + 选传输方式 + 三个出口 ✓）
        ///   owner 改成原生窗口句柄 ✓（不再依赖 WinForms 的 IWin32Window ✓）
        ///   窗口本体在 EvidenceForm.cs（原生控件 ✓ 与旧版同一套布局 ✓）</summary>
        public static void Show(IntPtr owner)
        {
            try
            {
                EvidenceForm.Show(owner);
            }
            catch (Exception ex)
            {
                LogViewWin32.L("★ 上报证据窗口异常：" + ex.GetType().Name + " " + ex.Message);
                LogViewWin32.Msg(owner, "打开上报窗口失败：" + ex.Message, "上报证据", false);
            }
        }

        /// <summary>
        /// ★ PGP 加密（调系统的 gpg ✓）
        ///   没装 gpg → 明确告诉用户"改选明文或用文件下载"✗ 不静默降级 ✗
        /// </summary>
        private static string Encrypt(string text, IntPtr owner)
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
                    LogViewWin32.L("PGP 加密失败：exit=" + p.ExitCode + " err=" + Head(err));
                    LogViewWin32.Msg(owner, "PGP 加密失败：" + err + "\n\n"
                        + "可能原因：没装 GnuPG / 没有作者公钥（" + PgpKeyId + "）\n"
                        + "→ 可以先选【明文】提交，或把 WARDEN.log 手动发给我 ✓",
                        "上报证据", false);
                    return text;   // 返回明文（用户已在预览里看过 ✓）
                }
                LogViewWin32.L("PGP 加密成功：" + outp.Length + " 字符 ✓");
                return outp;
            }
            catch (Exception ex)
            {
                LogViewWin32.L("调用 gpg 失败：" + ex.GetType().Name + " " + ex.Message);
                LogViewWin32.Msg(owner, "调用 gpg 失败：" + ex.Message + "\n\n"
                    + "→ 请改选【明文】提交 ✓", "上报证据", false);
                return text;
            }
        }

        private static string Head(string s)
        {
            int i = s.IndexOf('\n');
            string t = (i < 0 ? s : s.Substring(0, i)).Trim();
            return t.Length > 80 ? t.Substring(0, 80) + "…" : t;
        }
    }
}
