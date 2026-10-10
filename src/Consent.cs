// ============================================================================
//  授权对话框 —— 基于 TaskDialogIndirect
// ============================================================================
//  【为什么换掉自绘窗口】（用户 2026-10-09 的建议）
//
//      自绘窗口的实测结果：
//        · 窗口创建成功（日志有 hwnd）✓
//        · 但用户看到它【闪一下就没了】✗
//        · EnumWindows 里也找不到它 ✗
//        · 生命周期日志显示"消息循环还没退出"✗ —— 自相矛盾
//      → 问题出在【我自己写的消息循环 / 窗口管理】上 ✗
//
//      TaskDialogIndirect 是 comctl32 的原生任务对话框：
//        ✓ 系统自己管消息循环与模态
//        ✓ 不用 RegisterClass / CreateWindowEx / GetMessage
//        ✓ ★ 原生支持【命令链接】：大按钮 + 主文字 + 说明文字
//        ✓ 原生支持"展开详细信息"
//        ✓ 纯 Win32 导出函数，AOT 下可用（不是 COM）
//
//  【★ 命令链接的格式】
//      按钮文字用 "\n" 分成两行：
//          第一行 = 主文字（粗体大字）
//          第二行 = 说明文字（小字）
//      → 正好满足用户"用词要明白"的要求 ✓
//
//  【★ 两段式】
//      第一段：常规手段失败 N 次 → 问用户
//          允许本次强杀 / 永久允许强杀 / 拒绝，继续尝试隐藏
//      第二段：用户选"继续隐藏"后 15 秒仍压不住 → 再问
//          立即强杀 / 永久允许强杀 / 拒绝，继续尝试隐藏
//          ★ 并明确"常规拦截已完全失效"+ 建议立即强杀
//          ★ 并引导用 Geek 之类工具清除流氓软件
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WARDEN
{
    internal enum ConsentAnswer
    {
        Pending = 0,
        /// <summary>允许本次强杀。</summary>
        KillOnce = 1,
        /// <summary>永久允许强杀。</summary>
        KillAlways = 2,
        /// <summary>拒绝，继续尝试隐藏。</summary>
        KeepHiding = 3,
        /// <summary>超时未答（按继续隐藏处理）。</summary>
        Timeout = 4,
        /// <summary>配置里已永久允许。</summary>
        PreGranted = 5,
    }

    internal enum ConsentStage
    {
        /// <summary>第一段：常规手段失败若干次。</summary>
        First = 0,
        /// <summary>第二段：又等 15 秒仍压不住，确认常规拦截完全失效。</summary>
        Second = 1,
    }

    internal static class ConsentDialog
    {
        // ------------------------------------------------------------ 常量
        private const int ID_BTN_KILL_ONCE = 1001;   // ★ 常量 ID_BTN_KILL_ONCE
        private const int ID_BTN_KILL_ALWAYS = 1002;   // ★ 常量 ID_BTN_KILL_ALWAYS
        private const int ID_BTN_KEEP_HIDING = 1003;   // ★ 常量 ID_BTN_KEEP_HIDING

        private const uint TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;   // ★ 常量 TDF_ALLOW_DIALOG_CANCELLATION
        private const uint TDF_USE_COMMAND_LINKS = 0x0010;   // ★ 常量 TDF_USE_COMMAND_LINKS
        private const uint TDF_POSITION_RELATIVE_TO_WINDOW = 0x1000;   // ★ 常量 TDF_POSITION_RELATIVE_TO_WINDOW
        private const uint TDF_SIZE_TO_CONTENT = 0x01000000;   // ★ 常量 TDF_SIZE_TO_CONTENT
        private const uint TDF_CALLBACK_TIMER = 0x0800;   // ★ 常量 TDF_CALLBACK_TIMER

        // 主图标
        private const int TD_WARNING_ICON = unchecked((int)0xFFFF);   // ★ 常量 TD_WARNING_ICON
        private const int TD_ERROR_ICON = unchecked((int)0xFFFE);   // ★ 常量 TD_ERROR_ICON
        private const int TD_INFORMATION_ICON = unchecked((int)0xFFFD);   // ★ 常量 TD_INFORMATION_ICON

        private const uint TD_CLOSE = 0;   // ★ 常量 TD_CLOSE
        private const uint TD_DESTROY = 2;   // ★ 常量 TD_DESTROY
        private const uint TD_CREATED = 1;   // ★ 常量 TD_CREATED

        // ------------------------------------------------------------ 结构体
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode,
                      Pack = 1)]
        private struct TASKDIALOG_BUTTON
        {
            public int nButtonID;
            public IntPtr pszButtonText;   // ★ 用 IntPtr 自己管内存，避免封送问题
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct TASKDIALOGCONFIG
        {
            public uint cbSize;
            public IntPtr hwndParent;
            public IntPtr hInstance;   // 模块实例句柄
            public uint dwFlags;   // ★ 对话框标志（命令链接 / 允许 Esc 关闭 / 自适应大小…）
            public uint dwCommonButtons;   // ★ 系统标准按钮（我们用 0 = 一个都不要，全自己定义）
            public IntPtr pszWindowTitle;   // ★ 标题栏文字
            public IntPtr hMainIcon;          // 与 pszMainIcon 共用
            public IntPtr pszMainInstruction;
            public IntPtr pszContent;
            public uint cButtons;
            public IntPtr pButtons;
            public int nDefaultButton;
            public uint cRadioButtons;
            public IntPtr pRadioButtons;
            public int nDefaultRadioButton;
            public IntPtr pszVerificationText;   // ★ WARDEN.L.T("不再询问")复选框的文字（我们不用，留空）
            public IntPtr pszExpandedInformation;   // ★ 展开后的详细信息（我们不用）
            public IntPtr pszExpandedControlText;   // ★ WARDEN.L.T("展开")按钮的文字（我们不用）
            public IntPtr pszCollapsedControlText;   // ★ WARDEN.L.T("折叠")按钮的文字（我们不用）
            public IntPtr hFooterIcon;        // 与 pszFooterIcon 共用
            public IntPtr pszFooter;   // ★ 页脚文字（我们用来放L.T("系统关键进程不会出现在本提示里")这类说明）
            public IntPtr pfCallback;
            public IntPtr lpCallbackData;
            public uint cxWidth;
        }

        [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "TaskDialogIndirect")]
        private static extern int TaskDialogIndirect([In] ref TASKDIALOGCONFIG pTaskConfig,   // ★ 显示任务对话框（本工具的授权弹窗就是它）
            out int pnButton, out int pnRadioButton, out bool pfVerificationFlagChecked);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandleW(string? name);   // 取模块句柄（自校验时用来拿自身实例）

        // ------------------------------------------------------------ 状态
        private static readonly object _lock = new();
        private static readonly HashSet<string> _alreadyAsked =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _always =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>★ 日志出口（由 Program 注入）。</summary>
        public static Action<string>? LogSink;
        private static void L(string m) => LogSink?.Invoke("[弹窗] " + m);

        /// <summary>用户新点的"永久允许强杀"，调用方负责写回配置。</summary>
        public static List<string> NewAlways { get; } = new();

        /// <summary>
        /// ★★★ 被强杀过的程序名单（用户 2026-10-09 的设计）。
        ///
        /// 作用：判断"这个程序是不是杀了又复活"
        ///     · 之前强杀过，现在它又有窗口 → 复活 ✗
        ///     · 这时弹窗的推荐要改成「永久允许强杀」✓
        ///       因为它会反复回来，只有持续打掉它，
        ///       用户才有时间去用专业卸载工具彻底清除 ✓
        /// </summary>
        private static readonly HashSet<string> _terminatedBefore =
            new(StringComparer.OrdinalIgnoreCase);

        public static void LoadTerminatedHistory(IEnumerable<string>? names)
        {
            lock (_lock)
            {
                _terminatedBefore.Clear();
                if (names != null)
                {
                    foreach (var n in names)
                    {
                        if (!string.IsNullOrWhiteSpace(n))
                        {
                            _terminatedBefore.Add(n.Trim());
                        }
                    }
                }
            }
        }

        /// <summary>★ 标记某程序已被强杀（调用方负责写回配置）。</summary>
        public static void MarkTerminated(string proc)
        {
            lock (_lock)
            {
                _terminatedBefore.Add(proc);
            }
        }

        /// <summary>★ 这个程序之前是否被强杀过 → 判断"复活"。</summary>
        public static bool WasTerminatedBefore(string proc)
        {
            lock (_lock)
            {
                if (_terminatedBefore.Contains(proc))
                {
                    return true;
                }
                foreach (var n in _terminatedBefore)
                {
                    if (proc.Contains(n, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public static void LoadAlways(IEnumerable<string>? names)
        {
            lock (_lock)
            {
                _always.Clear();
                if (names != null)
                {
                    foreach (var n in names)
                    {
                        if (!string.IsNullOrWhiteSpace(n))
                        {
                            _always.Add(n.Trim());
                        }
                    }
                }
            }
        }

        // ================================================================
        //  ★★★ A52A：非阻塞的授权询问（2026-10-10）
        // ================================================================
        //  【为什么必须改成异步】
        //      Ask() 内部是 TaskDialogIndirect —— 它会【一直阻塞到用户应答】✗
        //      而它原来是在【扫描线程】上被直接调用的 ✗
        //      → 对话框一弹出来，整个主循环就停住：
        //          不处理弹窗、不查 .stop、应急压制也不动 ✗✗
        //      （实测证据：日志里"累计尝试 6"之后全停，连 [STATUS] 都没了）
        //
        //  【改法】问，但不等 ✓
        //      · RequestConsentAsync()：后台线程弹窗，答案进并发字典
        //      · TryTakeResult()：主循环每圈来看有没有答案
        //      · 等答案期间维持原处置（继续隐藏），绝不干等 ✗
        // ================================================================
        private static readonly System.Collections.Concurrent.ConcurrentDictionary
            <string, ConsentAnswer> _answers =
            new System.Collections.Concurrent.ConcurrentDictionary
                <string, ConsentAnswer>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _pending =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>★ 取某个进程的答案（取走即删除）。</summary>
        public static bool TryTakeResult(string proc, out ConsentAnswer ans)
        {
            if (_answers.TryRemove(proc, out ans))
            {
                lock (_lock)
                {
                    _pending.Remove(proc);
                }
                return true;
            }
            ans = ConsentAnswer.KeepHiding;
            return false;
        }

        /// <summary>★ 这个进程的问询是不是还在等用户应答。</summary>
        public static bool IsPending(string proc)
        {
            lock (_lock)
            {
                return _pending.Contains(proc);
            }
        }

        /// <summary>
        /// ★ 后台弹窗询问：调用线程【立刻返回】，绝不阻塞 ✓
        ///   同一个进程同时只问一次（防弹窗刷屏）✓
        /// </summary>
        public static void RequestConsentAsync(string proc, string imagePath, string title,
                                              int score, int closeFailures, ConsentStage stage)
        {
            lock (_lock)
            {
                if (_pending.Contains(proc))
                {
                    return;
                }
                _pending.Add(proc);
            }

            var t = new System.Threading.Thread(() =>
            {
                ConsentAnswer a;
                try
                {
                    a = Ask(proc, imagePath, title, score, closeFailures, stage);
                }
                catch (Exception e)
                {
                    LogSink?.Invoke("[CONSENT] ask failed: " + e.Message);
                    a = ConsentAnswer.KeepHiding;
                }
                _answers[proc] = a;
                LogSink?.Invoke("[CONSENT] async answer for " + proc + " = " + a);
            })
            {
                IsBackground = true,
                Name = "WARDEN-Consent",
            };
            t.Start();
        }

        public static bool IsGranted(string proc)
        {
            lock (_lock)
            {
                foreach (var n in _always)
                {
                    if (proc.Contains(n, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        // ================================================================
        //  对外接口
        // ================================================================
        public static ConsentAnswer Ask(string proc, string imagePath, string title,
                                        int score, int failures, ConsentStage stage,
                                        int timeoutMs = 180000)
        {
            if (IsGranted(proc))
            {
                L("已在永久授权名单里，跳过询问");
                return ConsentAnswer.PreGranted;
            }
            if (stage == ConsentStage.First)
            {
                lock (_lock)
                {
                    if (_alreadyAsked.Contains(proc))
                    {
                        L("第一段已问过 " + proc + "，不再重复");
                        return ConsentAnswer.KeepHiding;
                    }
                    _alreadyAsked.Add(proc);
                }
            }

            L($"准备弹窗 阶段={stage} 程序={proc} 已失败={failures}");

            // ★★★ 判断"是不是杀了又复活"（用户 2026-10-09 的设计）
            //     之前强杀过它，现在它又有窗口 → 复活 ✗
            //     → 推荐要从「立即强杀」改成「永久允许强杀」✓
            bool revived = WasTerminatedBefore(proc);
            if (revived)
            {
                L($"★ {proc} 之前被强杀过，现在又出现 → 判定为【复活】");
            }

            ConsentAnswer ans = ConsentAnswer.Timeout;
            var th = new Thread(() =>
            {
                try
                {
                    ans = ShowTaskDialog(proc, imagePath, title, failures, stage, revived);
                }
                catch (Exception e)
                {
                    L("弹窗线程异常: " + e.GetType().Name + ": " + e.Message);
                    ans = ConsentAnswer.KeepHiding;
                }
            })
            { IsBackground = true, Name = "consent-taskdialog" };
            th.Start();

            if (!th.Join(timeoutMs))
            {
                L($"等待超时 {timeoutMs}ms，按继续隐藏处理");
                ans = ConsentAnswer.Timeout;
            }
            L("用户答复 = " + ans);

            lock (_lock)
            {
                if (ans == ConsentAnswer.KillAlways)
                {
                    _always.Add(proc);
                    NewAlways.Add(proc);
                }
                else if (ans == ConsentAnswer.KillOnce)
                {
                    _always.Add(proc);
                }
            }
            return ans;
        }

        // ================================================================
        //  对话框本体
        // ================================================================
        private static ConsentAnswer ShowTaskDialog(string proc, string imagePath,
                                                    string title, int failures,
                                                    ConsentStage stage, bool revived)
        {
            // ★ 所有字符串都要自己管内存（非托管）
            var blocks = new List<IntPtr>();
            IntPtr Str(string s)
            {
                IntPtr p = Marshal.StringToHGlobalUni(s);
                blocks.Add(p);
                return p;
            }

            try
            {
                // ★★★ 标题随情况变化
                string windowTitle;
                if (revived)
                {
                    windowTitle = WARDEN.L.T("WARDEN —— 这个程序被强杀后又回来了");
                }
                else if (stage == ConsentStage.First)
                {
                    windowTitle = WARDEN.L.T("WARDEN —— 发现一个关不掉的弹窗");
                }
                else
                {
                    windowTitle = WARDEN.L.T("WARDEN —— 常规手段已确认失效");
                }

                string mainInstruction;
                if (revived)
                {
                    mainInstruction = WARDEN.L.T("强杀之后它又复活了");
                }
                else if (stage == ConsentStage.First)
                {
                    mainInstruction = WARDEN.L.T("这个程序的弹窗关不掉");
                }
                else
                {
                    mainInstruction = WARDEN.L.T("常规拦截手段对这个程序已经完全失效");
                }

                string content = BuildContent(proc, imagePath, title, failures, stage, revived);
                string footer = WARDEN.L.T(WARDEN.L.T("系统关键进程不会出现在本提示里。"))
                              + "  想永久放行某个程序？在配置文件的 consented_processes 里加上它。";

                // ★★★ 命令链接：推荐项随"是否复活"变化
                //
                //   没复活过 → 推荐「允许本次强杀」
                //   ★ 复活过 → 推荐「永久允许强杀」
                //     因为它会反复回来，只有持续打掉它，
                //     用户才有时间去用专业卸载工具彻底清除 ✓
                string b1 = stage == ConsentStage.First
                    ? WARDEN.L.T("允许本次强杀\n这一次强制结束它")
                    : WARDEN.L.T("立即强杀\n立刻强制结束它（建议）");
                string b2 = revived
                    ? WARDEN.L.T("永久允许强杀（建议）\n它一冒头就被打掉，方便你去卸载它")
                    : WARDEN.L.T("永久允许强杀\n以后遇到它直接强杀，不再询问");
                string b3 = WARDEN.L.T("拒绝，继续尝试隐藏\n不使用强制手段");

                IntPtr pTitle = Str(windowTitle);
                IntPtr pMain = Str(mainInstruction);
                IntPtr pContent = Str(content);
                IntPtr pFooter = Str(footer);
                IntPtr pB1 = Str(b1);
                IntPtr pB2 = Str(b2);
                IntPtr pB3 = Str(b3);

                // 按钮数组
                var btnArr = new TASKDIALOG_BUTTON[3];
                btnArr[0].nButtonID = ID_BTN_KILL_ONCE;
                btnArr[0].pszButtonText = pB1;
                btnArr[1].nButtonID = ID_BTN_KILL_ALWAYS;
                btnArr[1].pszButtonText = pB2;
                btnArr[2].nButtonID = ID_BTN_KEEP_HIDING;
                btnArr[2].pszButtonText = pB3;

                int btnSize = Marshal.SizeOf<TASKDIALOG_BUTTON>();
                IntPtr pButtons = Marshal.AllocHGlobal(btnSize * 3);
                blocks.Add(pButtons);
                for (int i = 0; i < 3; i++)
                {
                    Marshal.StructureToPtr(btnArr[i],
                        pButtons + i * btnSize, false);
                }

                var cfg = new TASKDIALOGCONFIG
                {
                    cbSize = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                    hwndParent = IntPtr.Zero,
                    hInstance = GetModuleHandleW(null),
                    dwFlags = TDF_USE_COMMAND_LINKS
                              | TDF_ALLOW_DIALOG_CANCELLATION
                              | TDF_POSITION_RELATIVE_TO_WINDOW
                              | TDF_SIZE_TO_CONTENT,
                    dwCommonButtons = 0,
                    pszWindowTitle = pTitle,
                    // ★ 复活 → 用错误图标（更醒目）；其余用警告图标
                    hMainIcon = new IntPtr(revived ? TD_ERROR_ICON
                                           : stage == ConsentStage.First
                                             ? TD_WARNING_ICON : TD_ERROR_ICON),
                    pszMainInstruction = pMain,
                    pszContent = pContent,
                    cButtons = 3,
                    pButtons = pButtons,
                    // ★ 默认按钮也随推荐变化
                    nDefaultButton = revived
                                     ? ID_BTN_KILL_ALWAYS          // 推荐永久强杀
                                     : stage == ConsentStage.Second
                                       ? ID_BTN_KILL_ONCE          // 推荐立即强杀
                                       : ID_BTN_KEEP_HIDING,
                    pszExpandedInformation = IntPtr.Zero,
                    pszExpandedControlText = IntPtr.Zero,
                    pszCollapsedControlText = IntPtr.Zero,
                    hFooterIcon = IntPtr.Zero,
                    pszFooter = pFooter,
                    pfCallback = IntPtr.Zero,
                    lpCallbackData = IntPtr.Zero,
                    cxWidth = 0,
                };

                L($"调用 TaskDialogIndirect… (复活={revived} 阶段={stage})");
                int hr = TaskDialogIndirect(ref cfg, out int button,
                                            out int radio, out bool verify);
                L($"TaskDialogIndirect 返回 hr=0x{hr:X8} 按钮={button}"
                  + $" 单选框={radio} 勾选={verify}");

                if (hr != 0)
                {
                    L("★ TaskDialogIndirect 失败 —— 按继续隐藏处理");
                    return ConsentAnswer.KeepHiding;
                }

                return button switch
                {
                    ID_BTN_KILL_ONCE => ConsentAnswer.KillOnce,
                    ID_BTN_KILL_ALWAYS => ConsentAnswer.KillAlways,
                    ID_BTN_KEEP_HIDING => ConsentAnswer.KeepHiding,
                    // 用户按 Esc / 点右上角关闭 → 最保守的默认
                    _ => ConsentAnswer.KeepHiding,
                };
            }
            finally
            {
                foreach (var p in blocks)
                {
                    try { Marshal.FreeHGlobal(p); } catch { }
                }
            }
        }

        private static string BuildContent(string proc, string imagePath, string title,
                                           int failures, ConsentStage stage, bool revived)
        {
            var sb = new StringBuilder();
            sb.AppendLine(WARDEN.L.T("程序：") + (string.IsNullOrEmpty(proc) ? WARDEN.L.T("(未知)") : proc));
            sb.AppendLine(WARDEN.L.T("窗口：") + Cut(title, 56));
            if (!string.IsNullOrEmpty(imagePath))
            {
                sb.AppendLine(WARDEN.L.T("位置：") + Cut(imagePath, 80));
            }
            sb.AppendLine();

            if (revived)
            {
                // ★★★ 复活场景（用户 2026-10-09 的设计）
                sb.AppendLine("★ 这个程序之前已经被强制结束过一次，现在它又回来了。");
                sb.AppendLine();
                sb.AppendLine("这说明：");
                sb.AppendLine("  · 它不只是一个弹窗，而是一个会常驻/会自启的程序");
                sb.AppendLine("  · 「强杀一次」对它没用 —— 杀掉之后它还会重新启动");
                sb.AppendLine();
                sb.AppendLine("所以这里建议改用「永久允许强杀」：");
                sb.AppendLine("  它一冒头就被打掉，不会再打扰你。");
                sb.AppendLine("  这样你就有一段安稳的时间去把它彻底卸载。");
            }
            else if (stage == ConsentStage.First)
            {
                sb.AppendLine("这个程序带有合法的数字签名。按照既定规则，只能隐藏它的窗口");
                sb.AppendLine("或者发送关闭请求，不能强制结束它的进程。");
                sb.AppendLine("但它一直在重建窗口，隐藏压不住。");
                sb.AppendLine("已经用常规手段尝试 " + failures + " 次，没有效果。");
            }
            else
            {
                sb.AppendLine("上一次你选择了继续尝试隐藏。又过了一段时间，仍然压不住 ——");
                sb.AppendLine("它还在不停地重建窗口。");
                sb.AppendLine();
                sb.AppendLine("这说明：★ 常规拦截手段对这个程序已经完全失效。");
                sb.AppendLine();
                sb.AppendLine("继续隐藏下去的后果：");
                sb.AppendLine("  · 这个弹窗会反复出现，不会真正消失");
                sb.AppendLine("  · 工具会持续消耗 CPU 去追它的新窗口");
                sb.AppendLine("  · 你的桌面会一直被打扰");
                sb.AppendLine();
                sb.AppendLine("★ 建议选择「立即强杀」。");
            }

            sb.AppendLine();
            sb.AppendLine("════════════════════════════════════════════");
            sb.AppendLine("★ 风险提示");
            sb.AppendLine("  强制结束进程可能造成：");
            sb.AppendLine("    · 该程序里未保存的数据丢失");
            sb.AppendLine("    · 它正在进行的操作被中断");
            sb.AppendLine();
            sb.AppendLine("★★ 强烈建议：用专业卸载工具彻底清除它");
            sb.AppendLine("  强杀 / 隐藏 都只是让它暂时闭嘴，治标不治本。");
            sb.AppendLine("  请使用下列任一工具把它完整卸载：");
            sb.AppendLine("    · Geek Uninstaller（免费，绿色，推荐）");
            sb.AppendLine("    · Revo Uninstaller");
            sb.AppendLine("    · HiBit Uninstaller");
            sb.AppendLine();
            sb.AppendLine("  这些工具能：扫描残留文件与注册表项、删除开机自启、");
            sb.AppendLine("              清理计划任务，比 Windows 自带的卸载干净得多。");
            sb.AppendLine();
            sb.AppendLine("  卸载路径参考：" + Cut(
                string.IsNullOrEmpty(imagePath) ? "(见上面位置)" : imagePath, 70));
            sb.AppendLine("════════════════════════════════════════════");
            return sb.ToString();
        }

        private static string Cut(string s, int n)
            => string.IsNullOrEmpty(s) ? string.Empty
               : (s.Length <= n ? s : s.Substring(0, n) + "…");
    }
}
