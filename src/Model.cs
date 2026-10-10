// ============================================================================
//  数据模型层
// ============================================================================
//  含：窗口信息采集（WindowInfo）、规则匹配（Rule）、保护设置（Protect）、
//      JSON 源生成上下文（AOT 必需）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WARDEN
{
    // ========================================================================
    //  规则
    // ========================================================================
    internal sealed class Rule
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("process")]
        public string? Process { get; set; }

        [JsonPropertyName("title_contains")]
        public List<string>? TitleContains { get; set; }   // ★ 标题包含这些词就命中（最常见的方式）

        [JsonPropertyName("title_regex")]
        public string? TitleRegex { get; set; }   // ★ 标题正则（更灵活，但要注意别写太宽松导致误伤）

        [JsonPropertyName("class_contains")]
        public string? ClassContains { get; set; }   // ★ 类名包含这些词就命中（对付"标题留空"的流氓窗口）

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }   // ★ 这条规则是否启用
    }

    internal sealed class Protect
    {
        [JsonPropertyName("skip_if_foreground")]
        public bool SkipIfForeground { get; set; } = true;   // ★ 当前活动窗口不处理（防止误关用户正在用的窗口）

        [JsonPropertyName("skip_if_has_edit")]
        public bool SkipIfHasEdit { get; set; } = true;   // ★ 含输入框的窗口不处理（那是正常界面，不是广告）

        [JsonPropertyName("skip_if_has_child_window")]
        public bool SkipIfHasChildWindow { get; set; } = true;   // ★ 含子控件的窗口不处理（正常界面几乎都有子控件）

        [JsonPropertyName("dry_run")]
        public bool DryRun { get; set; } = true;   // ★ 演习模式：只记日志不真动手（建议第一次用先开着）
    }

    /// <summary>★ AOT 用的 JSON 源生成上下文。</summary>
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(GuardConfig))]
    internal sealed partial class GuardJsonContext : JsonSerializerContext
    {
    }

    // ========================================================================
    //  窗口信息
    // ========================================================================
    internal sealed class WindowInfo
    {
        public IntPtr Hwnd;
        public uint Pid;
        public string Title = string.Empty;
        public string Class = string.Empty;
        public string Proc = string.Empty;
        /// <summary>★ 映像完整路径 —— 强杀安全闸②靠它判断是不是系统目录。</summary>
        public string ImagePath = string.Empty;

        public bool IsVisible;
        public bool IsTopmost;
        public bool IsToolWindow;
        public bool IsNoActivate;
        public bool IsPopup;

        public int ChildCount;   // ★ 这个窗口有几个子控件（正常界面多，纯展示广告少）
        public bool HasEdit;   // ★ 这个窗口里有没有输入框
        public int Width;   // ★ 窗口宽度（像素）
        public int Height;   // ★ 窗口高度（像素）
        public int X;   // ★ 窗口左上角 X 坐标
        public int Y;   // ★ 窗口左上角 Y 坐标

        /// <summary>★ 同进程里是否还有"看起来正常"的窗口（有输入框/多子控件）。</summary>
        public bool SiblingLooksReal;

        /// <summary>小尺寸 + 靠边 → 悬浮广告条的典型形态。</summary>
        public bool IsSmallFloating =>
            Width >= 80 && Height >= 20 && Width <= 400 && Height <= 220
            && Y <= 220;

        public string Key => Pid + "|" + Class;

        // ------------------------------------------------------------ 采集
        private static readonly Dictionary<uint, string> _procNameCache = new();
        private static readonly Dictionary<uint, string> _imageCache = new();

        public static void ClearCaches()
        {
            _procNameCache.Clear();
            _imageCache.Clear();
        }

        /// <summary>
        /// 枚举顶层窗口。
        ///
        /// ★★ includeHidden 很关键，别想当然：
        ///     本工具的核心动作就是【把窗口隐藏】✗
        ///     如果只用"可见窗口"做统计，就会自己把自己的依据毁掉 ✗
        ///     表现为：隐藏后数不到窗口 → 窗口数=0 → 判据永远不成立 ✗
        ///     （2026-10-09 实测踩到：日志里"窗口数=0(弹窗时0)"）
        ///
        ///     所以要判断"这个进程到底开了多少窗口、我们追不追得上"，
        ///     必须【包含隐藏窗口】✓
        /// </summary>
        public static List<WindowInfo> Enumerate(bool includeNoTitle = false,
                                                 bool includeHidden = false)
        {
            var list = new List<WindowInfo>();

            Native.EnumWindows((h, _) =>
            {
                try
                {
                    var w = FromHandle(h, includeNoTitle, includeHidden);
                    if (w != null)
                    {
                        list.Add(w);
                    }
                }
                catch
                {
                    // 单个窗口失败不影响整体
                }
                return true;
            }, IntPtr.Zero);

            // 补一次"同进程是否有正常窗口"的判断
            var byPid = new Dictionary<uint, bool>();
            foreach (var w in list)
            {
                bool real = Safety.LooksLikeRealWindow(w.ChildCount, w.HasEdit);
                byPid.TryGetValue(w.Pid, out bool cur);
                byPid[w.Pid] = cur || real;
            }
            foreach (var w in list)
            {
                bool real = Safety.LooksLikeRealWindow(w.ChildCount, w.HasEdit);
                w.SiblingLooksReal = byPid.TryGetValue(w.Pid, out bool any) && any && !real;
            }

            return list;
        }

        public static WindowInfo? FromHandle(IntPtr h, bool includeNoTitle = false,
                                             bool includeHidden = false)
        {
            bool visible = Native.IsWindowVisible(h);
            if (!visible && !includeHidden)
            {
                return null;
            }
            int len = Native.GetWindowTextLengthW(h);
            if (len <= 0 && !includeNoTitle)
            {
                return null;
            }

            var sbTitle = new StringBuilder(Math.Max(512, len + 2));
            // ★★ A52A 实测：这里原来直接用 GetWindowTextW ✗
            //    它对【别的进程】的窗口会同步发送 WM_GETTEXT，
            //    目标线程不泵消息时（流氓软件假死）我们会被卡在那里 ✗✗
            //    → 改成防卡死版：先判卡死，再带 80ms 超时地问 ✓
            GetTextSafe(h, sbTitle, sbTitle.Capacity);

            var sbClass = new StringBuilder(256);
            Native.GetClassNameW(h, sbClass, 256);

            Native.GetWindowThreadProcessId(h, out uint pid);

            var w = new WindowInfo
            {
                Hwnd = h,
                Pid = pid,
                Title = sbTitle.ToString(),
                Class = sbClass.ToString(),
                Proc = ProcName(pid),
                ImagePath = ImagePathOf(pid),
                IsVisible = visible,
            };

            int ex = Native.GetWindowLongW(h, Native.GWL_EXSTYLE);
            int style = Native.GetWindowLongW(h, Native.GWL_STYLE);
            w.IsTopmost = (ex & Native.WS_EX_TOPMOST) != 0;
            w.IsToolWindow = (ex & Native.WS_EX_TOOLWINDOW) != 0;
            w.IsNoActivate = (ex & Native.WS_EX_NOACTIVATE) != 0;
            w.IsPopup = (style & Native.WS_POPUP) != 0;

            w.ChildCount = ChildCountOf(h);
            w.HasEdit = HasEditOf(h);

            if (Native.GetWindowRect(h, out var rc))
            {
                w.X = rc.Left;
                w.Y = rc.Top;
                w.Width = rc.Width;
                w.Height = rc.Height;
            }

            return w;
        }

        /// <summary>
        /// ★★★ 防卡死地取窗口标题（A52A 实测新增）。
        ///
        /// 【为什么不能用 GetWindowTextW】
        ///   对别的进程的窗口，它会同步发送 WM_GETTEXT ✗
        ///   对方线程不泵消息（假死/被挂起）→ 我们一直等 ✗✗
        ///   实测后果：一轮扫描被拖到 69 秒，循环完全卡住
        ///   （日志停在"即将取前台窗口"之后，连 .stop 都不再检查）
        ///
        /// 【现在的做法】
        ///   ① IsHungAppWindow(h) 为真 → 直接放弃取标题（它已经卡死，问了就轮到我卡）
        ///   ② 否则 SendMessageTimeoutW(WM_GETTEXT, SMTO_ABORTIFHUNG, 80ms)
        ///      超时就当标题为空 ✓ 宁可少一个标题，也绝不把整个守护线程搭进去
        /// </summary>
        /// <summary>
        /// ★ A52A：标题缓存（同一窗口 2 秒内只取一次）✓
        ///   一轮扫描会枚举【三遍】，不缓存就要取三遍 ✗
        /// </summary>
        private static readonly Dictionary<IntPtr, KeyValuePair<long, string>> _titleCache
            = new Dictionary<IntPtr, KeyValuePair<long, string>>();

        private static void GetTextSafe(IntPtr h, StringBuilder sb, int cap)
        {
            try
            {
                if (Native.IsHungAppWindow(h))
                {
                    return;
                }

                int bytes = cap * 2;
                IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
                try
                {
                    // 清零（后面按 UTF-16 读回来）
                    for (int i = 0; i < bytes; i++)
                    {
                        System.Runtime.InteropServices.Marshal.WriteByte(buf, i, 0);
                    }

                    IntPtr smOk = Native.SendMessageTimeoutW(
                        h, WM_GETTEXT, new IntPtr(cap), buf,
                        Native.SMTO_ABORTIFHUNG, 10, out _);   // ★ 80→10ms（窗口一多会被拖死）
                    if (smOk == IntPtr.Zero)
                    {
                        return;   // ★ 超时 → 当作没有标题（绝不阻塞）✓
                    }

                    string s = System.Runtime.InteropServices.Marshal
                        .PtrToStringUni(buf) ?? string.Empty;
                    sb.Clear();
                    sb.Append(s);
                    if (_titleCache.Count < 4096) { _titleCache[h] = new KeyValuePair<long, string>(Environment.TickCount64, s); }
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
                }
            }
            catch
            {
                // 取标题失败不影响其它判断 ✓
            }
        }

        /// <summary>★ WM_GETTEXT（防卡死取标题用）</summary>
        private const uint WM_GETTEXT = 0x000D;

        private static int ChildCountOf(IntPtr h)
        {
            int n = 0;
            Native.EnumChildWindows(h, (c, _) => { n++; return true; }, IntPtr.Zero);
            return n;
        }

        private static bool HasEditOf(IntPtr h)
        {
            bool found = false;
            Native.EnumChildWindows(h, (c, _) =>
            {
                var sb = new StringBuilder(128);
                Native.GetClassNameW(c, sb, 128);
                string cn = sb.ToString().ToLowerInvariant();
                if (cn.Contains("edit") || cn.Contains("richedit") || cn.Contains("combobox"))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>进程名（小写）。用 Win32 快照，避免引入 System.Diagnostics。</summary>
        public static string ProcName(uint pid)
        {
            if (_procNameCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }
            string name = string.Empty;
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap != IntPtr.Zero && snap != new IntPtr(-1))
            {
                try
                {
                    var e = new Native.PROCESSENTRY32W
                    {
                        dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>(),
                        szExeFile = string.Empty,
                    };
                    if (Native.Process32FirstW(snap, ref e))
                    {
                        do
                        {
                            if (e.th32ProcessID == pid)
                            {
                                name = (e.szExeFile ?? string.Empty).ToLowerInvariant();
                                break;
                            }
                        }
                        while (Native.Process32NextW(snap, ref e));
                    }
                }
                finally
                {
                    Native.CloseHandle(snap);
                }
            }
            _procNameCache[pid] = name;
            return name;
        }

        /// <summary>★ 进程映像完整路径 —— 强杀安全闸的关键输入。</summary>
        public static string ImagePathOf(uint pid)
        {
            if (_imageCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }
            string path = string.Empty;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    uint size = (uint)sb.Capacity;
                    if (Native.QueryFullProcessImageNameW(h, 0, sb, ref size))
                    {
                        path = sb.ToString();
                    }
                }
                finally
                {
                    Native.CloseHandle(h);
                }
            }
            _imageCache[pid] = path;
            return path;
        }

        // ------------------------------------------------------------ 匹配
        public static bool MatchRule(WindowInfo w, Rule r)
        {
            bool any = false;
            string proc = (r.Process ?? string.Empty).Trim();
            if (proc.Length > 0)
            {
                any = true;
                if (w.Proc.IndexOf(proc, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            var tc = r.TitleContains;
            if (tc != null && tc.Count > 0)
            {
                any = true;
                bool hit = false;
                foreach (var k in tc)
                {
                    if (!string.IsNullOrEmpty(k)
                        && w.Title.Contains(k, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = true;
                        break;
                    }
                }
                if (!hit)
                {
                    return false;
                }
            }

            string rx = (r.TitleRegex ?? string.Empty).Trim();
            if (rx.Length > 0)
            {
                any = true;
                try
                {
                    if (!Regex.IsMatch(w.Title, rx, RegexOptions.IgnoreCase))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;   // 正则写错就不匹配，绝不误伤
                }
            }

            string cc = (r.ClassContains ?? string.Empty).Trim();
            if (cc.Length > 0)
            {
                any = true;
                if (w.Class.IndexOf(cc, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            return any;   // ★ 至少要有一个条件，不做"空规则关所有"
        }
    }
}
