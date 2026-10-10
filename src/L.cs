// ============================================================================
//  主程序用的轻量语言表（2026-10-13）
// ============================================================================
//  【为什么不用设置工具那套】
//      WARDEN.exe 是 Native AOT 单文件 ✗ 不引用 WinForms 那套东西 ✗
//      → 这里写个最小的：读同一份语言包的 [text] 段即可 ✓
//
//  【怎么选语言】
//      ① WARDEN.json 里的 "lang"（设置界面里用户选的 ✓ 优先级最高）
//      ② 系统界面语言（zh-TW/zh-HK/zh-MO/zh-CN/en-US → 对应包 ✓）
//      ③ 兜底简体中文 ✓
//
//  【★ 一条硬规矩】读不到就返回原文 ✓
//      托盘/弹窗绝不能因为语言包缺失变成空白或报错 ✗
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WARDEN
{
    internal static class L
    {
        private static Dictionary<string, string>? _table;
        private static string _code = "";
        private static readonly object Gate = new object();

        /// <summary>当前语言代码（诊断用 ✓）</summary>
        public static string Code
        {
            get { Ensure(); return _code; }
        }

        /// <summary>
        /// ★ 按【中文原文】取译文；没有就原样返回 ✓
        /// </summary>
        public static string T(string zh)
        {
            if (string.IsNullOrEmpty(zh))
            {
                return zh;
            }
            Ensure();
            if (_table != null && _table.TryGetValue(zh, out string? hit))
            {
                return hit;
            }
            return zh;
        }

        private static void Ensure()
        {
            if (_table != null)
            {
                return;
            }
            lock (Gate)
            {
                if (_table != null)
                {
                    return;
                }
                var table = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    string code = PickCode();
                    _code = code;
                    foreach (string dir in Dirs())
                    {
                        string f = Path.Combine(dir, code + ".txt");
                        if (!File.Exists(f))
                        {
                            continue;
                        }
                        bool inText = false;
                        foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                        {
                            string ln = line.Trim();
                            if (ln.StartsWith("["))
                            {
                                inText = ln.StartsWith("[text]");
                                continue;
                            }
                            if (!inText || ln.Length == 0 || ln.StartsWith("#"))
                            {
                                continue;
                            }
                            int eq = ln.IndexOf('=');
                            if (eq <= 0)
                            {
                                continue;
                            }
                            // 语言包里 \n 是转义写法，还原成真换行 ✓
                            string k = ln.Substring(0, eq).Replace("\\n", "\n");
                            string v = ln.Substring(eq + 1).Replace("\\n", "\n");
                            table[k] = v;
                        }
                        break;
                    }
                }
                catch
                {
                    // 读语言包失败 → 用空表（等于全中文 ✓ 不影响功能 ✗）
                }
                _table = table;
            }
        }

        private static string PickCode()
        {
            // ① 配置里用户选的
            try
            {
                string cfg = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WARDEN", "WARDEN.json");
                // 便携版：数据在 exe 旁边 ✓
                string portableCfg = Path.Combine(
                    AppContext.BaseDirectory.TrimEnd('\\'), "data", "WARDEN.json");
                if (File.Exists(portableCfg))
                {
                    cfg = portableCfg;
                }
                if (File.Exists(cfg))
                {
                    string text = File.ReadAllText(cfg, Encoding.UTF8);
                    var m = System.Text.RegularExpressions.Regex.Match(
                        text, "\"lang\"\\s*:\\s*\"([^\"]+)\"",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string c = m.Groups[1].Value.Trim();
                        if (c.Length > 0)
                        {
                            return c;
                        }
                    }
                }
            }
            catch
            {
            }

            // ② 系统界面语言
            try
            {
                string ui = System.Globalization.CultureInfo.CurrentUICulture.Name;
                if (ui.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
                    || ui.StartsWith("zh-Hant-TW", StringComparison.OrdinalIgnoreCase))
                {
                    return "zh-TW";
                }
                if (ui.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)
                    || ui.StartsWith("zh-Hant-HK", StringComparison.OrdinalIgnoreCase))
                {
                    return "zh-HK";
                }
                if (ui.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase)
                    || ui.StartsWith("zh-Hant-MO", StringComparison.OrdinalIgnoreCase))
                {
                    return "zh-MO";
                }
                if (ui.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "zh-TW";
                }
                if (ui.StartsWith("en-US", StringComparison.OrdinalIgnoreCase))
                {
                    return "en-US";
                }
                if (ui.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    return "en-GB";
                }
                if (ui.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    return "zh-Hans";
                }
            }
            catch
            {
            }
            return "zh-Hans";
        }

        /// <summary>语言包搜索目录：exe\lang\ 优先，其次数据目录\lang\ ✓</summary>
        private static IEnumerable<string> Dirs()
        {
            yield return Path.Combine(AppContext.BaseDirectory.TrimEnd('\\'), "lang");
            yield return Path.Combine(AppContext.BaseDirectory.TrimEnd('\\'), "data", "lang");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WARDEN", "lang");
        }
    }
}
