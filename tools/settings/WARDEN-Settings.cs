// ============================================================================
//  WARDEN 设置工具 —— 业务逻辑层
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
//      → 改成【独立的 exe】✓
//        · 双击直接运行 ✓ 任务栏一个图标 ✓ 关它才关界面 ✓
//        · 不依赖 PowerShell ✗
//
//  【★ 2026-10-11：界面从 WinForms 改成原生 Win32/GDI（本文件只留业务逻辑）】
//      · WinForms 不支持 Native AOT ✗（PublishAot 直接编译失败）
//      · 原来 SelfContained 单文件 47.03 MB ✗ —— 而界面代码只有几百 KB ✓
//      · 现在：原生控件 + AOT = 一个几 MB 的 exe，机器上不用装 .NET ✓
//      · 界面实现全部搬到 SettingsWin32.cs ✓
//        本文件里的【配置读写 / 规则增删改 / SHA256 / 导入导出格式 / 语言口径】
//        一个字都没改 ✗ 只把 WinForms 控件换成 SettingsWin32 的读写接口 ✓
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WardenSettings
{
    /// <summary>规则编辑对话框。</summary>
    internal sealed class RuleDialog : IDisposable
    {
        public string RuleName = "";
        public string ProcName = "";
        public string Keywords = "";
        public void Dispose() { }
    }

    internal static class Program
    {
        /// <summary>
        /// 程序入口。
        /// ★ 特意加了全局异常处理：出问题时弹窗告诉用户，
        ///   而不是让窗口静默消失（那样根本没法排查）。
        ///   （原生版：AppDomain.UnhandledException 取代 WinForms 的 Application.ThreadException ✓）
        /// </summary>
        [STAThread]
        private static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                string msg = e.ExceptionObject is Exception ex ? ex.ToString() : (e.ExceptionObject?.ToString() ?? "");
                SettingsWin32.Error(IntPtr.Zero, WardenLang.Lang.Tr("发生错误：\n") + msg);
            };
            try
            {
                // ★ 界面在 SettingsWin32 里（原生窗口 + 消息循环 ✓）
                SettingsWin32.Run();
            }
            catch (Exception ex)
            {
                SettingsWin32.Error(IntPtr.Zero, WardenLang.Lang.Tr("启动失败：\n") + ex);
            }
        }
    }

    /// <summary>
    /// 一条规则在界面上的样子（取代原来的 ListViewItem ✓ 字段一一对应 ✓）
    /// </summary>
    internal sealed class Rule
    {
        public string Name = "";
        public string Process = "";
        public string Keywords = "";
        public bool Enabled = true;
    }

    /// <summary>
    /// 设置工具的全部业务逻辑（配置读写 / 规则 / 哈希 / 导入导出 ✓）
    /// ★ 与界面完全解耦：界面只管调用，逻辑只管实现 ✓
    /// </summary>
    internal static class Settings
    {
        // ---- 路径 ----
        // ★ 测试钩子：环境变量 WARDEN_SETTINGS_DATA_DIR 可以把数据目录指到沙箱
        //   （0.3.3 实测要求：绝不动真实 %LOCALAPPDATA%\WARDEN ✓）
        //   生产环境不设这个变量 → 行为与之前完全一样 ✓
        internal static readonly string ToolDir = ResolveToolDir();
        internal static string CfgPath => Path.Combine(ToolDir, "WARDEN.json");
        internal static string LogPath => Path.Combine(ToolDir, "WARDEN.log");
        internal static string StopPath => Path.Combine(ToolDir, "WARDEN.stop");
        internal static string ExePath => Path.Combine(AppContext.BaseDirectory, "WARDEN.exe");

        private static string ResolveToolDir()
        {
            string def = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");
            try
            {
                string forced = Environment.GetEnvironmentVariable("WARDEN_SETTINGS_DATA_DIR");
                if (!string.IsNullOrEmpty(forced) && Path.IsPathRooted(forced))
                {
                    SettingsWin32.L("★ 测试钩子生效：数据目录改到沙箱 " + forced + "（真实配置不会被碰 ✓）");
                    return forced;
                }
            }
            catch { }
            return def;
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
        internal static void LoadConfig()
        {
            string json = "";
            try
            {
                if (File.Exists(CfgPath)) json = File.ReadAllText(CfgPath, Encoding.UTF8);
            }
            catch { }

            // 保护（0..3 ✓）
            SettingsWin32.SetCheck(0, JsonBool(json, "skip_if_foreground", true));
            SettingsWin32.SetCheck(1, JsonBool(json, "skip_if_has_edit", true));
            SettingsWin32.SetCheck(2, JsonBool(json, "skip_if_has_child_window", true));
            SettingsWin32.SetCheck(3, JsonBool(json, "dry_run", true));

            // 处置
            SettingsWin32.SetCheck(4, JsonBool(json, "allow_terminate", false));
            SettingsWin32.SetCheck(5, JsonBool(json, "allow_consent_prompt", true));
            SettingsWin32.SetCheck(6, JsonBool(json, "honor_signatures", true));
            SettingsWin32.SetCheck(7, JsonBool(json, "honor_whitelist_for_kill", true));
            SettingsWin32.SetNum(0, (int)JsonNum(json, "apply_threshold", 55));
            SettingsWin32.SetNum(1, (int)JsonNum(json, "terminate_threshold", 75));
            SettingsWin32.SetNum(2, (int)JsonNum(json, "failures_before_kill", 3));
            SettingsWin32.SetNum(3, (int)JsonNum(json, "consent_after_failures", 6));
            SettingsWin32.SetNum(4, (int)JsonNum(json, "second_stage_min_hides", 8));
            SettingsWin32.SetNum(5, (int)JsonNum(json, "second_stage_max_wait_ms", 6000));

            // 名单
            SettingsWin32.SetText(0, string.Join("\r\n", JsonStrArray(json, "process_whitelist")));
            SettingsWin32.SetText(1, string.Join("\r\n", JsonStrArray(json, "consented_processes")));
            SettingsWin32.SetText(2, string.Join("\r\n", JsonStrArray(json, "terminated_history")));

            // 安全
            SettingsWin32.SetText(3, JsonStr(json, "expected_self_hash", ""));
            // ★★★ 用户要求（2026-10-10）：哈希要【默认写入】✗
            //   以前这里是空的（得用户自己点按钮）→ 现在打开就自动填 ✓
            if (string.IsNullOrWhiteSpace(SettingsWin32.GetText(3)))
            {
                SettingsWin32.SetText(3, HashOfSelf());
            }
            SettingsWin32.SetCheck(8, JsonBool(json, "self_check_on_start", true));
            SettingsWin32.SetCheck(9, JsonBool(json, "self_check_block_on_fail", true));

            // 规则
            var rules = new List<Rule>();
            foreach (var blk in RegexBlocks(json, "rules"))
            {
                var name = JsonStr(blk, "name", "");
                if (string.IsNullOrEmpty(name)) continue;
                rules.Add(new Rule
                {
                    Name = name,
                    Process = JsonStr(blk, "process", ""),
                    Keywords = string.Join(", ", JsonStrArray(blk, "title_contains")),
                    Enabled = JsonBool(blk, "enabled", true),
                });
            }
            SettingsWin32.WriteRules(rules);
            SettingsWin32.L("LoadConfig 完成：规则 " + rules.Count + " 条；配置存在=" + File.Exists(CfgPath)
                            + "（" + CfgPath + "）");
        }

        /// <summary>
        /// 从 JSON 里按大括号配对，逐个取出对象块。
        /// ★ 为什么不用完整 JSON 解析器：
        ///   这个小工具刻意不引入依赖，简易解析够用 ✓
        /// ★★★ 0.3.3 全量测试抓到的真缺陷（严重）：
        ///   主程序写出来的键是【大写】"Rules" ✗（Model.cs 的属性名就是 Rules ✓）
        ///   而这里原来只带 Singleline、【不区分大小写没开】✗
        ///   → 真实配置里的规则一条都读不出来 ✗ 规则表永远空的 ✗
        ///   → 更要命的是保存：读出来是空 → 保存时把 "rules": [] 写回去 ✗✗
        ///     用户的规则就这么没了 ✓（= 打开设置点一下保存就删规则 ✓）
        ///   → 加 IgnoreCase ✓（与主程序 PropertyNameCaseInsensitive=true 口径一致 ✓）
        ///   （如实标注：这个缺陷在 WinForms 版里就存在 ✗ 不是本次移植引入的 ✓）
        /// </summary>
        private static IEnumerable<string> RegexBlocks(string json, string arrayKey)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json,
                "\"" + arrayKey + "\"\\s*:\\s*\\[(.*)\\]\\s*[,}]",
                System.Text.RegularExpressions.RegexOptions.Singleline
                | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
        internal static void SaveConfig(IntPtr owner)
        {
            try
            {
                Directory.CreateDirectory(ToolDir);
                var rules = SettingsWin32.ReadRules();
                // ★★ 0.3.3 全量测试抓到的第二个真缺陷：本工具是【整份重写】配置 ✗
                //    界面里没有的键会被一起丢掉 ✗（真实配置第一层 15 个键，本工具只写 12 个 ✗）
                //    丢掉的恰好是主程序要用的：Emergency / first_run_done / HardBlockNote … ✓
                //    → 保存前先把"不归本工具管"的顶层块原样抄过来 ✓（见 UnmanagedTopLevel ✓）
                string oldJson = "";
                try { if (File.Exists(CfgPath)) { oldJson = File.ReadAllText(CfgPath, Encoding.UTF8); } } catch { }
                var keep = UnmanagedTopLevel(oldJson);
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_说明\": [");
                sb.AppendLine("    \"WARDEN 配置 —— 由设置工具生成\",");
                sb.AppendLine("    \"★ 改完保存即可，WARDEN 每轮会重新读取配置，不用重启\"");
                sb.AppendLine("  ],");

                sb.AppendLine("  \"rules\": [");
                for (int i = 0; i < rules.Count; i++)
                {
                    var it = rules[i];
                    var kws = it.Keywords
                        .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    sb.Append("    {\"name\": ").Append(J(it.Name));
                    sb.Append(", \"process\": ").Append(J(it.Process));
                    sb.Append(", \"title_contains\": ").Append(JArr(kws));
                    sb.Append(", \"enabled\": ").Append(it.Enabled ? "true" : "false");
                    sb.Append("}").AppendLine(i == rules.Count - 1 ? "" : ",");
                }
                sb.AppendLine("  ],");

                sb.AppendLine("  \"behavior\": {");
                sb.AppendLine("    \"rule_hit\": 40, \"steals_focus\": 12, \"topmost\": 10,");
                sb.AppendLine("    \"no_edit\": 8, \"no_children\": 6, \"respawn\": 15,");
                sb.AppendLine("    \"small_floating\": 8, \"title_churn\": 10,");
                sb.Append("    \"apply_threshold\": ").Append(SettingsWin32.GetNum(0)).AppendLine(",");
                sb.Append("    \"terminate_threshold\": ").Append(SettingsWin32.GetNum(1)).AppendLine();
                sb.AppendLine("  },");

                sb.AppendLine("  \"action\": {");
                sb.Append("    \"allow_terminate\": ").Append(SettingsWin32.GetCheck(4) ? "true" : "false").AppendLine(",");
                sb.AppendLine("    \"l1_wait_ms\": 500, \"l2_wait_ms\": 1000,");
                sb.Append("    \"failures_before_kill\": ").Append(SettingsWin32.GetNum(2)).AppendLine(",");
                sb.AppendLine("    \"deadline_ms\": 4500,");
                sb.Append("    \"honor_signatures\": ").Append(SettingsWin32.GetCheck(6) ? "true" : "false").AppendLine(",");
                sb.Append("    \"honor_whitelist_for_kill\": ").Append(SettingsWin32.GetCheck(7) ? "true" : "false").AppendLine(",");
                sb.Append("    \"allow_consent_prompt\": ").Append(SettingsWin32.GetCheck(5) ? "true" : "false").AppendLine(",");
                sb.Append("    \"consent_after_failures\": ").Append(SettingsWin32.GetNum(3)).AppendLine(",");
                sb.Append("    \"second_stage_min_hides\": ").Append(SettingsWin32.GetNum(4)).AppendLine(",");
                sb.Append("    \"second_stage_max_wait_ms\": ").Append(SettingsWin32.GetNum(5)).AppendLine();
                sb.AppendLine("  },");

                sb.AppendLine("  \"protect\": {");
                sb.Append("    \"skip_if_foreground\": ").Append(SettingsWin32.GetCheck(0) ? "true" : "false").AppendLine(",");
                sb.Append("    \"skip_if_has_edit\": ").Append(SettingsWin32.GetCheck(1) ? "true" : "false").AppendLine(",");
                sb.Append("    \"skip_if_has_child_window\": ").Append(SettingsWin32.GetCheck(2) ? "true" : "false").AppendLine(",");
                sb.Append("    \"dry_run\": ").Append(SettingsWin32.GetCheck(3) ? "true" : "false").AppendLine();
                sb.AppendLine("  },");

                var Split = new Func<string, List<string>>(t =>
                    (t ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(x => x.Trim()).Where(x => x.Length > 0).ToList());

                sb.Append("  \"process_whitelist\": ").Append(JArr(Split(SettingsWin32.GetText(0)))).AppendLine(",");
                sb.Append("  \"consented_processes\": ").Append(JArr(Split(SettingsWin32.GetText(1)))).AppendLine(",");
                sb.Append("  \"terminated_history\": ").Append(JArr(Split(SettingsWin32.GetText(2)))).AppendLine(",");
                sb.Append("  \"expected_self_hash\": ").Append(J(SettingsWin32.GetText(3).Trim())).AppendLine(",");
                // ★ 语言选择（用户手选过就固化下来 ✓）
                string langCode = SettingsWin32.CurrentLangCode();
                if (!string.IsNullOrEmpty(langCode))
                {
                    sb.Append("  \"lang\": ").Append(J(langCode)).AppendLine(",");
                }
                sb.Append("  \"self_check_on_start\": ").Append(SettingsWin32.GetCheck(8) ? "true" : "false").AppendLine(",");
                sb.Append("  \"self_check_block_on_fail\": ").Append(SettingsWin32.GetCheck(9) ? "true" : "false");
                if (keep.Count > 0) { sb.Append(","); }     // ★ 后面还要接保留下来的块 ✓ 逗号不能漏 ✗
                sb.AppendLine();
                for (int i = 0; i < keep.Count; i++)
                {
                    sb.Append("  ").Append(keep[i].Trim());
                    sb.AppendLine(i == keep.Count - 1 ? "" : ",");
                }
                sb.AppendLine("}");
                if (keep.Count > 0)
                {
                    SettingsWin32.L("保存时保留了本工具不管的顶层块 " + keep.Count + " 个 ✓（不会被删了 ✓）");
                }

                File.WriteAllText(CfgPath, sb.ToString(), new UTF8Encoding(false));
                SettingsWin32.L("保存配置成功：" + CfgPath + "  " + new FileInfo(CfgPath).Length + " B，规则 "
                                + rules.Count + " 条 ✓");
                SettingsWin32.Info(owner, WardenLang.Lang.Tr("已保存到：\n") + CfgPath);
            }
            catch (Exception ex)
            {
                // ★ 保存失败必须明确报错 ✗ 不许静默失败 ✓（清单第 3 条 ✓）
                SettingsWin32.L("★ 保存配置失败：" + ex.GetType().Name + " " + ex.Message);
                SettingsWin32.Error(owner, WardenLang.Lang.Tr("保存失败：\n") + ex.Message);
            }
        }

        /// <summary>本工具【会写】的顶层键 —— 其余的一律原样保留 ✓（比较时大小写不敏感 ✓）</summary>
        private static readonly string[] ManagedKeys = new[]
        {
            "_说明", "rules", "behavior", "action", "protect",
            "process_whitelist", "consented_processes", "terminated_history",
            "expected_self_hash", "lang", "self_check_on_start", "self_check_block_on_fail",
        };

        /// <summary>
        /// ★ 把旧配置里"不归本工具管"的顶层成员整块抄出来（例：Emergency / first_run_done ✓）
        ///   实现：先按【顶层逗号】切块（字符串里的逗号与嵌套大括号都不会误切 ✓），再看每块开头的键 ✓
        /// </summary>
        private static List<string> UnmanagedTopLevel(string json)
        {
            var kept = new List<string>();
            foreach (var piece in TopLevelPieces(json))
            {
                var m = System.Text.RegularExpressions.Regex.Match(piece, "^\\s*\"([^\"]+)\"\\s*:");
                if (!m.Success) { continue; }
                string key = m.Groups[1].Value;
                bool managed = false;
                foreach (var k in ManagedKeys)
                {
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { managed = true; break; }
                }
                if (!managed) { kept.Add(piece.Trim()); }
            }
            return kept;
        }

        /// <summary>按顶层逗号把 JSON 对象切成成员块（字符串与嵌套结构都不受影响 ✓）</summary>
        private static List<string> TopLevelPieces(string json)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(json)) { return res; }
            int b = json.IndexOf('{');
            if (b < 0) { return res; }
            int depth = 1, start = b + 1;
            bool inStr = false, esc = false;
            for (int p = b + 1; p < json.Length; p++)
            {
                char ch = json[p];
                if (inStr)
                {
                    if (esc) { esc = false; }
                    else if (ch == '\\') { esc = true; }
                    else if (ch == '"') { inStr = false; }
                    continue;
                }
                if (ch == '"') { inStr = true; }
                else if (ch == '{' || ch == '[') { depth++; }
                else if (ch == '}' || ch == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (p > start) { res.Add(json.Substring(start, p - start)); }
                        break;
                    }
                }
                else if (ch == ',' && depth == 1)
                {
                    res.Add(json.Substring(start, p - start));
                    start = p + 1;
                }
            }
            return res;
        }

        // ================================================================
        //  规则导入 / 导出
        // ================================================================
        /// <summary>
        /// ★ B5：把当前规则导出成一个 json 文件 ✓
        ///   【为什么要能导出】换机器 / 备份 / 把规则分享给别人 ✓
        ///   格式自带标识（warden-rules/1）→ 以后改格式也能认出来 ✓
        /// </summary>
        internal static void ExportRules(IntPtr owner)
        {
            var rules = SettingsWin32.ReadRules();
            if (rules.Count == 0)
            {
                SettingsWin32.L("导出规则：当前没有规则 → 提示 ✓");
                SettingsWin32.Info(owner, WardenLang.Lang.Tr("现在还没有规则可以导出。"));
                return;
            }
            string file = SettingsWin32.PickSaveFile(owner,
                WardenLang.Lang.Tr("导出当前规则到文件"),
                "WARDEN *" + (char)0 + "*.json" + (char)0 + (char)0,
                "WARDEN-rules-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            if (file == null)
            {
                SettingsWin32.L("导出规则：用户取消 ✓");
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
                for (int i = 0; i < rules.Count; i++)
                {
                    var it = rules[i];
                    var kws = it.Keywords
                        .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    sb.Append("    {\"name\": ").Append(J(it.Name));
                    sb.Append(", \"process\": ").Append(J(it.Process));
                    sb.Append(", \"title_contains\": ").Append(JArr(kws));
                    sb.Append(", \"enabled\": ").Append(it.Enabled ? "true" : "false");
                    sb.Append("}").AppendLine(i == rules.Count - 1 ? "" : ",");
                }
                sb.AppendLine("  ]");
                sb.AppendLine("}");
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                SettingsWin32.L("导出规则成功：" + file + "  " + rules.Count + " 条 ✓");
                SettingsWin32.Info(owner,
                    WardenLang.Lang.Tr("已导出：\n") + file + "\n\n"
                    + rules.Count + WardenLang.Lang.Tr(" 条规则"));
            }
            catch (Exception ex)
            {
                SettingsWin32.L("★ 导出规则失败：" + ex.Message);
                SettingsWin32.Error(owner, WardenLang.Lang.Tr("导出失败：\n") + ex.Message);
            }
        }

        /// <summary>
        /// ★ B5：从 json 文件导入规则 ✓
        ///   校验：必须有 rules 段；每条要有名称，且进程名或标题关键词至少一个 ✓
        ///   不合法的不静默丢弃 ✗ —— 报告"导入几条、跳过几条"✓
        /// </summary>
        internal static void ImportRules(IntPtr owner)
        {
            string file = SettingsWin32.PickOpenFile(owner,
                WardenLang.Lang.Tr("从文件导入规则"),
                "WARDEN *" + (char)0 + "*.json" + (char)0 + (char)0);
            if (file == null)
            {
                SettingsWin32.L("导入规则：用户取消 ✓");
                return;
            }
            string json;
            try
            {
                json = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                SettingsWin32.Error(owner, WardenLang.Lang.Tr("读不了这个文件：\n") + ex.Message);
                return;
            }

            if (!json.Contains("\"rules\""))
            {
                SettingsWin32.L("导入规则：文件里没有 rules 段 → 提示 ✓");
                SettingsWin32.Warn(owner, WardenLang.Lang.Tr("这不是 WARDEN 规则文件（里面没有 rules 段）。"));
                return;
            }

            var good = new List<Rule>();
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
                good.Add(new Rule
                {
                    Name = name,
                    Process = proc,
                    Keywords = string.Join(", ", kws),
                    Enabled = JsonBool(blk, "enabled", true),
                });
            }

            if (good.Count == 0)
            {
                SettingsWin32.L("导入规则：没有可导入的规则（跳过 " + skipped + " 条）→ 提示 ✓");
                SettingsWin32.Warn(owner, WardenLang.Lang.Tr("没有可导入的规则。"));
                return;
            }

            // ★ 旧版 MessageBoxButtons.YesNoCancel ✓（是=追加 / 否=替换 / 取消=什么都不做 ✓）
            int ans = SettingsWin32.AskYesNoCancel(owner,
                good.Count + WardenLang.Lang.Tr(" 条规则：\n\n「是」= 追加到现有规则\n"
                + "「否」= 替换现有规则\n「取消」= 什么都不做"));
            if (ans == 2)   // IDCANCEL
            {
                SettingsWin32.L("导入规则：用户取消 ✓");
                return;
            }
            var rules = SettingsWin32.ReadRules();
            if (ans == 7)   // IDNO = 替换
            {
                rules.Clear();
            }
            rules.AddRange(good);
            SettingsWin32.WriteRules(rules);

            SettingsWin32.L("导入规则完成：新增 " + good.Count + " 条，跳过 " + skipped + " 条，共 "
                            + SettingsWin32.ReadRules().Count + " 条 ✓");
            SettingsWin32.Info(owner,
                WardenLang.Lang.Tr("导入完成：新增 ") + good.Count
                + WardenLang.Lang.Tr(" 条") + (skipped > 0
                    ? WardenLang.Lang.Tr("，跳过 ") + skipped
                      + WardenLang.Lang.Tr(" 条（缺少名称或匹配条件）") : "")
                + "\n\n" + WardenLang.Lang.Tr("记得点「保存配置」才会生效 ✓"));
        }

        // ================================================================
        //  动作
        // ================================================================
        /// <summary>
        /// ★ A59：算 WARDEN.exe 的 SHA256（算不出返回空串，绝不抛）✓
        ///   抽出来是为了让"打开设置界面时自动填入"也能用 ✓
        /// </summary>
        internal static string HashOfSelf()
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

        /// <summary>
        /// 计算 WARDEN.exe 的 SHA256 并填进输入框。
        /// ★ 这个值就是启动自校验的基线 ✓
        /// </summary>
        internal static void FillHash(IntPtr owner)
        {
            if (!File.Exists(ExePath))
            {
                SettingsWin32.L("哈希填入：找不到 " + ExePath + " → 提示 ✓");
                SettingsWin32.Warn(owner, WardenLang.Lang.Tr("找不到：\n") + ExePath + "\n\n请把本工具和 WARDEN.exe 放在同一个目录。");
                return;
            }
            string h = HashOfSelf();
            if (h.Length == 64)
            {
                SettingsWin32.SetText(3, h);
                SettingsWin32.L("哈希填入成功：" + h + "（来自 " + ExePath + " ✓）");
                SettingsWin32.Info(owner, WardenLang.Lang.Tr("已填入当前 WARDEN.exe 的 SHA256：\n") + h);
            }
            else
            {
                SettingsWin32.L("★ 哈希填入失败：算不出（返回长度 " + h.Length + "）✓");
                SettingsWin32.Warn(owner, WardenLang.Lang.Tr("总算不出哈希 —— 检查一下 WARDEN.exe 能不能读 ✓"));
            }
        }

        /// <summary>打开数据目录（取代旧版 Process.Start("explorer.exe", _toolDir) ✓）</summary>
        internal static void OpenToolDir(IntPtr owner)
        {
            try
            {
                if (Directory.Exists(ToolDir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", ToolDir);
                    SettingsWin32.L("已打开目录：" + ToolDir);
                }
                else
                {
                    SettingsWin32.L("打开目录：目录不存在 " + ToolDir + " → 不动作 ✓");
                }
            }
            catch (Exception ex)
            {
                SettingsWin32.L("★ 打开目录失败：" + ex.Message);
                SettingsWin32.Error(owner, WardenLang.Lang.Tr("启动失败：\n") + ex.Message);
            }
        }

        /// <summary>WARDEN 是否在运行（>0 = PID ✓ 与旧版 GetProcessesByName 等价 ✓）</summary>
        internal static int WardenPid()
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("WARDEN");
                return procs.Length > 0 ? procs[0].Id : 0;
            }
            catch { return 0; }
        }

        /// <summary>日志里最后一条自检结果（没有就返回 null ✓）</summary>
        internal static string SelfCheckLine()
        {
            try
            {
                if (!File.Exists(LogPath)) { return null; }
                var tail = TailLines(LogPath, 400);
                return tail.LastOrDefault(l => l.Contains("[自检] 结果="));
            }
            catch { return null; }
        }

        /// <summary>
        /// 读文件最后 N 行。
        /// ★ 用 FileShare.ReadWrite 打开：
        ///   因为 WARDEN 正在写这个日志，独占打开会失败 ✗
        /// </summary>
        internal static List<string> TailLines(string path, int n)
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

        /// <summary>★ 语言切换时记一笔 setup.log（与原逻辑一致：只追加一行 ✓ 失败不抛 ✓）</summary>
        internal static void AppendSetupLog(string line)
        {
            try
            {
                string lg = Path.Combine(ToolDir, "setup.log");
                Directory.CreateDirectory(ToolDir);
                File.AppendAllText(lg,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + line + Environment.NewLine,
                    new UTF8Encoding(false));
                SettingsWin32.L("setup.log 追加：" + line);
            }
            catch (Exception e) { SettingsWin32.L("setup.log 写入失败（忽略）：" + e.Message); }
        }
    }
}
