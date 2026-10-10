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
using System.Linq;
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
        // ★ 两个来源：exe 目录的 lang\（我们发的包 ✓）+ 数据目录的 lang\（用户自己加的 ✓）
        private static readonly string PackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN", "lang");   // 兼容旧引用 ✓
        private static readonly string[] PackDirs =
        {
            Path.Combine(AppContext.BaseDirectory.TrimEnd('\\'), "lang"),
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "WARDEN", "lang"),
        };

        private static readonly Dictionary<string, string> Ui = new(StringComparer.Ordinal);
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
            try { Load(DetectCode()); }
            catch (Exception e)
            {
                LoadError = e.Message;
                // ★ 加载失败不能崩 ✗ 也不能静默 ✗
                //   内置兜底表会让程序继续能用 ✓ 同时把原因记下来 ✓
                Fallback();
            }
        }

        /// <summary>★ 决定用哪个语言：环境变量 → 系统界面语言 → 简体中文</summary>
        
        /// <summary>★ 配置里指定的语言（用户手选过就听他的 ✓）</summary>
        private static string ReadConfiguredLang()
        {
            try
            {
                string cfg = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WARDEN", "WARDEN.json");
                if (!File.Exists(cfg))
                {
                    return "";
                }
                string text = File.ReadAllText(cfg);
                var mm = System.Text.RegularExpressions.Regex.Match(
                    text, "\"lang\"\\s*:\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return mm.Success ? mm.Groups[1].Value.Trim() : "";
            }
            catch
            {
                return "";
            }
        }

public static string DetectCode()
        {
            // ★★★ 用户手选的语言优先（设置界面里的"界面语言"下拉框 ✓）
            string configured = ReadConfiguredLang();
            if (configured.Length > 0 && Available().Contains(configured))
            {
                return configured;
            }

            // ★ 五种语言（用户 2026-10-10 补充要求）：
            //    简中 / 英语 / 繁中（台灣·香港·澳門 各自一份 ✓ 用词不同 ✗）
            var avail = Available();
            string ui = System.Globalization.CultureInfo.CurrentUICulture.Name;

            string want;
            if (ui.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
                || ui.StartsWith("zh-Hant-TW", StringComparison.OrdinalIgnoreCase))
            {
                want = "zh-TW";
            }
            else if (ui.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)
                     || ui.StartsWith("zh-Hant-HK", StringComparison.OrdinalIgnoreCase))
            {
                want = "zh-HK";
            }
            else if (ui.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase)
                     || ui.StartsWith("zh-Hant-MO", StringComparison.OrdinalIgnoreCase))
            {
                want = "zh-MO";
            }
            else if (ui.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                     && ui.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                want = "zh-TW";     // 其它繁体地区 → 先按台灣 ✓
            }
            else if (ui.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                want = "zh-Hans";   // 简中（含 zh-CN / zh-SG）✓
            }
            else
            {
                want = ui.StartsWith("en-US", StringComparison.OrdinalIgnoreCase)
                    ? "en-US"          // 美式英语 ✓
                    : "en-GB";         // 其它英语 → 英式 ✓（用户要求先补英式）
            }

            if (avail.Contains(want))
            {
                return want;
            }
            if (avail.Contains("en") && want == "en")
            {
                return "en";
            }
            return "zh-Hans";
        }

        /// <summary>★ 有哪些语言可用（内置 + 用户放的）。</summary>
        /// <summary>
        /// ★ 语言显示名（下拉框里显示这个，不显示 BCP47 代码 ✗）
        ///   例：繁體中文（中國臺灣）✓  繁体地区用官方称呼 + 当地用字
        /// </summary>
        public static string DisplayName(string code)
        {
            try
            {
                foreach (string dir in PackDirs)
                {
                    string f = Path.Combine(dir, code + ".txt");
                    if (!File.Exists(f))
                    {
                        continue;
                    }
                    foreach (string line in File.ReadAllLines(f))
                    {
                        if (line.StartsWith("name=", StringComparison.Ordinal))
                        {
                            return line.Substring(5).Trim();
                        }
                    }
                }
            }
            catch { }
            return code;   // 读不到名字就退回代码 ✓
        }

        public static List<string> Available()
        {
            var list = new List<string> { "zh-Hans" };
            try
            {
                if (Directory.Exists(PackDir))
                {
                    foreach (var f in PackDirs.SelectMany(d => Directory.Exists(d)
                ? Directory.GetFiles(d, "*.txt") : new string[0]))
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

                if (section == "meta" || section == "ui" || section == "log.tag")
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    v = Unescape(v);
                    if (section == "log.tag") Tags[k] = v;
                    else Ui[k] = v;
                }
                else if (section == "log.rule")
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
            void U(string k, string v) { Ui[k] = v; }
            void G(string k, string v) { Tags[k] = v; }

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
