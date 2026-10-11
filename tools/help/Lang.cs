// ============================================================================
//  WARDEN 语言包引擎
// ============================================================================
//  【为什么需要它】（用户 2026-10-10）
//
//      用户原话：
//        "这块你不给个注释吗？全英文谁看得懂啊？英国人看得懂吗？
//          问题在于我们这个是简体中文模式啊。
//          对了，你现在没把语言写死吧？别告诉我语言已经写死了，
//          我到时候还要做语言支持。你现在给我改成语言包模式。"
//
//      原来的问题：
//        ✗ 界面文字全是 C# 里的中文字面量（约 200 处）
//        ✗ 日志分析工具的翻译表是硬编码的 switch
//        ✗ 想加一门语言得改代码重新编译
//
//  【★ 语言包格式：纯文本，不用 JSON】
//
//      为什么不用 JSON：
//        · Native AOT 下 JSON 需要源生成器，麻烦
//        · JSON 里正则的 \d \s \. 转义容易写错，翻译者容易踩坑
//        · 翻译者多数不是程序员
//
//      格式（UTF-8，记事本可编辑）：
//
//          [meta]
//          code=zh-Hans
//          name=简体中文
//
//          [ui]
//          tray.open_settings=打开设置界面
//
//          [log.tag]
//          CONFIG=配置
//
//          [log.rule]
//          ^config changed<TAB>配置变了，重新校验
//
//      · [ui]      —— 界面文字，key=value
//      · [log.tag] —— 日志前缀的中文名，key=value
//      · [log.rule]—— 日志整行的翻译规则，正则<TAB>译文
//                     译文里 {1} {2} 引用正则捕获组
//      · # 开头是注释
//
//  【★★ 用户可以自己加语言】
//      放到 %LOCALAPPDATA%\WARDEN\lang\ 下即可，不用重新编译 ✓
//      内置的 zh-Hans 是保底，被外部同名文件覆盖 ✓
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WardenLang
{
    internal sealed class LogRule
    {
        public Regex Pattern = null!;
        public string Original = "";
        public string Replacement = "";
    }

    internal static class Lang
    {
        // ★ 语言包目录（用户可放自己的语言包）
        private static readonly string PackDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WARDEN", "lang");

        // ★ 2026-10-11 修（最后一个键 help.btn.openPage 解析不到的根因）：
        //   原来是 Ordinal（大小写敏感 ✓）→ 语言包写 openPage、代码里若写 openpage 就查不到 ✗
        //   改成忽略大小写 ✓ 对键名这种标识符来说更稳 ✓ 不影响任何语义 ✓
        private static readonly Dictionary<string, string> Ui = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Tags = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<LogRule> Rules = new();

        public static string Code { get; private set; } = "zh-Hans";
        public static string Name { get; private set; } = "简体中文";
        public static bool Loaded { get; private set; }
        public static string LoadError { get; private set; } = "";

        private static char TAB = (char)9;
        private static char LF = (char)10;
        private static char CR = (char)13;

        static Lang()
        {
            try { Load(DetectCode());
            // ★ 2026-10-11 修（Help 文案显示成 help.title 的根因）：
            //   原来 Fallback() 只在【加载之前】调用 ✓ 而加载过程会清空/覆盖 Ui ✗
            //   → 点号键全部查不到 → 界面显示一堆键名 ✗
            //   现在加载之后再兜一次（且 U/G 只补缺失 ✓ 不会覆盖语言包的正式译文 ✓）
            Fallback(); }
            catch (Exception e)
            {
                LoadError = e.Message;
                // ★ 加载失败不能崩 ✗ 也不能静默 ✗
                //   内置兜底表会让程序继续能用 ✓ 同时把原因记下来 ✓
                Fallback();
            }
        }

        /// <summary>★ 决定用哪个语言：环境变量 → 系统界面语言 → 简体中文</summary>
        public static string DetectCode()
        {
            string env = Environment.GetEnvironmentVariable("WARDEN_LANG") ?? "";
            if (env.Trim().Length > 0) return env.Trim();

            try
            {
                string ui = System.Globalization.CultureInfo.CurrentUICulture.Name;
                if (ui.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    // ★ 繁体也先走简体（以后加 zh-Hant 包就自动切过去）
                    return Available().Contains("zh-Hant") && ui.Contains("Hant")
                        ? "zh-Hant" : "zh-Hans";
                }
                if (ui.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                    && Available().Contains("en"))
                {
                    return "en";
                }
            }
            catch { }
            return "zh-Hans";
        }

        /// <summary>★ 有哪些语言可用（内置 + 用户放的）。</summary>
        public static List<string> Available()
        {
            var list = new List<string> { "zh-Hans" };
            try
            {
                if (Directory.Exists(PackDir))
                {
                    foreach (var f in Directory.GetFiles(PackDir, "*.txt"))
                    {
                        string c = Path.GetFileNameWithoutExtension(f);
                        if (!list.Contains(c)) list.Add(c);
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>★ 加载语言包。找不到就退回内置默认。</summary>
        public static void Load(string code)
        {
            Ui.Clear();
            Tags.Clear();
            Rules.Clear();
            Loaded = false;
            LoadError = "";

            // ★ 先铺一层内置兜底（保证任何情况下界面都有字 ✓）
            Fallback();

            string text = null;
            string used = "";

            // ① 优先用用户自己放的
            try
            {
                string ext = Path.Combine(PackDir, code + ".txt");
                if (File.Exists(ext))
                {
                    text = File.ReadAllText(ext, Encoding.UTF8);
                    used = ext;
                }
            }
            catch (Exception e) { LoadError = e.Message; }

            // ② 用内置的
            if (text == null)
            {
                text = EmbeddedDefault();
                used = "内置";
            }

            if (text == null)
            {
                return;   // 只剩兜底表
            }

            Parse(text);
            Code = code;
            Name = Ui.TryGetValue("meta.name", out var n) ? n : code;
            Loaded = true;
        }

        private static void Parse(string text)
        {
            string section = "";
            foreach (var rawLine in text.Split(LF))
            {
                string line = rawLine.TrimEnd(CR);
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;

                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    section = t.Substring(1, t.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }

                // ★ 2026-10-11 修（Help 界面显示成 help.title 的【真正根因】）：
                //   原来只认 [meta] / [ui] / [log.tag] 三种分节 ✗
                //   而共享语言包里的 help.* 键落在别的分节里 ✓ → 被整段丢弃 ✗
                //   → 73 个点号键一个都进不来 → 界面全是键名 ✓
                //   修法：除 log.rule（正则表 ✓）外，【任何分节】的 key=value 都按界面键收下 ✓
                //        这样以后语言包再加新分节也不会失效 ✓（且不必改共享语言包 ✓）
                if (section == "log.rule")
                {
                    // 正则<TAB>译文（见下面分支 ✓）
                }
                else
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    v = Unescape(v);
                    if (section == "log.tag") Tags[k] = v;
                    else Ui[k] = v;
                }
                if (section == "log.rule")   // ★ 与上面的分支配对 ✓
                {
                    // ★ 正则<TAB>译文（用 Tab 分隔，这样正则里可以有 = ）
                    int tab = line.IndexOf(TAB);
                    if (tab <= 0) continue;
                    string pat = line.Substring(0, tab).Trim();
                    string rep = line.Substring(tab + 1).Trim();
                    try
                    {
                        Rules.Add(new LogRule
                        {
                            Pattern = new Regex(pat, RegexOptions.Compiled
                                                     | RegexOptions.IgnoreCase),
                            Original = pat,
                            Replacement = Unescape(rep),
                        });
                    }
                    catch
                    {
                        // ★ 单条规则写错不该让整个日志工具废掉 ✗ 跳过就好 ✓
                    }
                }
            }
        }

        /// <summary>★ 译文里的 \n \t 还原成真字符。</summary>
        private static string Unescape(string s)
        {
            if (s.IndexOf((char)92) < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == (char)92 && i + 1 < s.Length)
                {
                    char c = s[i + 1];
                    if (c == 'n') { sb.Append(LF); i++; continue; }
                    if (c == 't') { sb.Append(TAB); i++; continue; }
                    if (c == (char)92) { sb.Append((char)92); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ================================================================
        //  对外接口
        // ================================================================

        /// <summary>★ 取界面文字。找不到就返回 key（不会显示成空白）✓</summary>
        public static string T(string key)
            => Ui.TryGetValue(key, out var v) ? v : key;

        /// <summary>★ 取界面文字并填占位符 {0} {1}。</summary>
        public static string T(string key, params object[] args)
        {
            string f = T(key);
            try { return string.Format(f, args); }
            catch { return f; }
        }

        /// <summary>★ 取日志前缀的中文名。</summary>
        public static string Tag(string tag)
            => Tags.TryGetValue(tag, out var v) ? v : tag;

        /// <summary>
        /// ★★★ 把一行英文日志整行翻译成中文。
        ///
        /// 规则按顺序匹配，第一条命中的就用 ✓
        /// 译文里 {1} {2} 引用正则的捕获组 ✓
        /// 都不命中 → 原样返回（并在界面上标注出来，方便补规则）✓
        /// </summary>
        public static (string text, bool matched) Translate(string body)
        {
            foreach (var r in Rules)
            {
                var m = r.Pattern.Match(body);
                if (!m.Success) continue;

                string outText = r.Replacement;
                // ★ {0} 是整行，{1} 起是捕获组
                for (int i = 0; i <= m.Groups.Count - 1; i++)
                {
                    outText = outText.Replace("{" + i + "}",
                        m.Groups[i].Value);
                }
                return (outText, true);
            }
            return (body, false);
        }

        // ================================================================
        //  内置兜底表
        // ================================================================
        //  ★ 只放最关键的几十条 ✗
        //    目的：即使语言包丢了/写坏了，界面也不会变成一堆 key ✓
        //    完整的语言包在构造时从内嵌资源或外部文件读 ✓
        // ================================================================
        private static void Fallback()
        {
            // ★ 2026-10-11 修：改成【只在缺失时写入】✓
            //   这样即使把它放在语言包加载【之后】再兜一次底，也不会覆盖正式翻译 ✓
            void U(string k, string v) { if (!Ui.ContainsKey(k)) { Ui[k] = v; } }
            void G(string k, string v) { if (!Tags.ContainsKey(k)) { Tags[k] = v; } }

            U("meta.name", "简体中文");
            U("app.title", "WARDEN");
            U("common.ok", "确定");
            U("common.cancel", "取消");
            U("common.close", "关闭");
            U("common.yes", "是");
            U("common.no", "否");
            U("common.refresh", "刷新");

            G("DECISION", "决策");
            G("SCORE", "评估");
            G("L1-CLOSE", "礼貌关闭");
            G("L2-HIDE", "隐藏");
            G("L2-DISMISS", "劝退");
            G("L3-KILL", "强制结束");
            G("KILL-DENIED", "拒绝强杀");
            G("KILL-ESCALATED", "手动提权");
            G("KILL-FAIL", "强杀失败");
            G("GUARD", "守护检测");
            G("CONSENT-OK", "授权通过");
            G("CONSENT-KEEP", "继续隐藏");
            G("CONSENT-SKIP", "跳过询问");
            G("CONFIG", "配置");
            G("SELFCHECK", "自校验");
            G("STAGE2", "第二段判据");
            G("DRYRUN", "演习模式");
            G("CLEARED", "已解决");
            G("RECORD", "记录");
            G("TRAY", "托盘");
            G("MSGWND", "消息窗口");
            G("MIGRATE", "数据迁移");
            G("STATUS", "状态");
            G("ERROR", "(启动信息)");
        }

        /// <summary>★ 读内嵌的默认语言包。</summary>
        private static string EmbeddedDefault()
        {
            try
            {
                var asm = typeof(Lang).Assembly;
                foreach (var n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("zh-Hans.txt", StringComparison.OrdinalIgnoreCase)
                        || n.EndsWith("langpack.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        using var s = asm.GetManifestResourceStream(n);
                        if (s == null) continue;
                        using var r = new StreamReader(s, Encoding.UTF8);
                        return r.ReadToEnd();
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
