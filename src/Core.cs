// ============================================================================
//  窗口行为拦截引擎 —— 核心数据结构与安全判定
// ============================================================================
//  这是 WARDEN 的核心：把"关弹窗"从【布尔匹配】升级成【行为评分 + 分级处置】。
//
//  【为什么不能只靠标题匹配】
//      流氓软件的对抗手段（沙箱模拟器里都复现了）：
//        · 每次换标题        → 纯标题匹配失效 ✗
//        · 每次换窗口类名    → 纯类名匹配失效 ✗
//        · 关掉立刻重建      → "关掉就完事"失效 ✗
//        · 抢焦点 / 强制置顶 → 只关不防，体验依旧很差 ✗
//      所以要综合【行为特征】打分，而不是看单一字段。
//
//  【★ 处置的三级升级阶梯】（用户明确要求：关不掉要能强杀，但要快、要安全）
//      L1  WM_CLOSE              礼貌请求，程序可弹保存提示、可拒绝
//      L2  WM_CLOSE + 线程劝退    更强硬，但仍不破坏进程
//      L3  TerminateProcess       ★ 强杀，必须有严格安全闸
//
//  【★★ 四道安全闸 —— 防的是"把系统搞崩"】
//      ① 硬黑名单：系统关键进程，硬编码，配置文件改不动
//      ② 系统目录：映像路径在 System32 一律不杀 ← 最强的一道
//      ③ 用户白名单
//      ④ 升级前置条件：必须已失败 N 次、且没有"正常"窗口
//
//  【★ 时限】用户要求"时间必须快"
//      整个 L1→L3 的过程硬性限制在 KillDeadlineMs（默认 4500ms）以内，
//      超时就不再等了，直接进入下一级或放弃。绝不拖住主循环。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace WARDEN
{
    // ========================================================================
    //  配置模型
    // ========================================================================

    /// <summary>处置策略。</summary>
    internal sealed class ActionPolicy
    {
        /// <summary>是否允许使用强杀（L3）。★ 默认关闭，需显式开启。</summary>
        [JsonPropertyName("allow_terminate")]
        public bool AllowTerminate { get; set; } = false;

        /// <summary>L1 发出后等多久（毫秒）才判定"没关掉"。</summary>
        [JsonPropertyName("l1_wait_ms")]
        public int L1WaitMs { get; set; } = 500;

        /// <summary>L2 发出后等多久（毫秒）。</summary>
        [JsonPropertyName("l2_wait_ms")]
        public int L2WaitMs { get; set; } = 1000;

        /// <summary>★ 同一窗口累计失败多少次才允许升级到 L3。</summary>
        [JsonPropertyName("failures_before_kill")]
        public int FailuresBeforeKill { get; set; } = 3;

        /// <summary>★ 从 L1 到 L3 的总时限（毫秒）。超过就不再等。</summary>
        [JsonPropertyName("deadline_ms")]
        public int DeadlineMs { get; set; } = 4500;

        /// <summary>
        /// ★★ 是否遵守"有合法签名的进程禁止强杀"（用户 2026-10-09 的规矩 2）。
        ///     生产环境必须为 true（默认）。
        ///     只有在【沙箱测试】里为了测到 L1/L2/L3 的升级阶梯才会临时关掉，
        ///     而且必须同时设置 DSH_SANDBOX_PIDS 限制强杀范围。
        /// </summary>
        [JsonPropertyName("honor_signatures")]
        public bool HonorSignatures { get; set; } = true;

        /// <summary>
        /// ★ 是否遵守"在白名单的进程禁止强杀"（同一条规矩）。
        ///     测试时可关掉，但生产必须为 true。
        /// </summary>
        [JsonPropertyName("honor_whitelist_for_kill")]
        public bool HonorWhitelistForKill { get; set; } = true;

        /// <summary>
        /// ★★ 受保护进程压不住时，是否弹窗询问用户是否允许强制结束
        ///     （用户 2026-10-09 的设计：把选择权交给用户）。
        ///
        ///     为什么这是安全的：交给用户的是【操作方式的选择】，
        ///     不是【安全判定】；用户本来就想关掉那个弹窗，
        ///     需求与选择权一致 ✓
        ///
        ///     ★ 硬黑名单里的系统关键进程【永远不弹窗】✗
        ///       杀了会蓝屏，不该给用户这个选项。
        /// </summary>
        [JsonPropertyName("allow_consent_prompt")]
        public bool AllowConsentPrompt { get; set; } = true;

        /// <summary>★ 受保护进程失败多少次后才弹窗询问（避免一上来就打扰）。</summary>
        [JsonPropertyName("consent_after_failures")]
        public int ConsentAfterFailures { get; set; } = 6;

        /// <summary>
        /// ★★ 第二段弹窗的触发方式（用户 2026-10-09 要求"缩短时间，但要确定真的压不住"）
        ///
        /// 【为什么不能只靠时间】
        ///     原来写死 15 秒就弹第二次 ✗
        ///     问题：15 秒是拍脑袋定的 ✗
        ///           · 工具其实压得住 → 用户白等 15 秒 ✗
        ///           · 工具真的压不住 → 也要等满 15 秒才知道 ✗
        ///
        /// 【改成用"追不追得上"判断】
        ///     主判据：已经隐藏了 SecondStageMinHides 次，
        ///             但该进程的窗口数【没有减少】→ 明确追不上 ✓
        ///             这种情况下 2~3 秒就能出结论 ✓
        ///     兜底：SecondStageMaxWaitMs 毫秒到了也弹（防止极端情况僵住）
        /// </summary>
        [JsonPropertyName("second_stage_min_hides")]
        public int SecondStageMinHides { get; set; } = 8;

        /// <summary>★ 第二段弹窗的兜底等待上限（毫秒）。从 15000 缩短到 6000。</summary>
        [JsonPropertyName("second_stage_max_wait_ms")]
        public int SecondStageMaxWaitMs { get; set; } = 6000;

        /// <summary>
        /// ★ 兼容旧配置：如果只写了 second_stage_delay_ms，仍然按它当兜底上限。
        /// </summary>
        [JsonPropertyName("second_stage_delay_ms")]
        public int SecondStageDelayMs { get; set; } = 0;
    }

    /// <summary>行为评分权重。</summary>
    internal sealed class BehaviorWeights
    {
        /// <summary>命中规则基础分。</summary>
        [JsonPropertyName("rule_hit")]
        public int RuleHit { get; set; } = 40;

        /// <summary>抢焦点（该窗口是前台）。</summary>
        [JsonPropertyName("steals_focus")]
        public int StealsFocus { get; set; } = 12;

        /// <summary>强制置顶。</summary>
        [JsonPropertyName("topmost")]
        public int Topmost { get; set; } = 10;

        /// <summary>无输入框（真实对话框通常有输入控件）。</summary>
        [JsonPropertyName("no_edit")]
        public int NoEdit { get; set; } = 8;

        /// <summary>无子控件（纯展示型窗口，广告的典型特征）。</summary>
        [JsonPropertyName("no_children")]
        public int NoChildren { get; set; } = 6;

        /// <summary>同一个进程短时间内反复创建窗口（关一个弹一个）。</summary>
        [JsonPropertyName("respawn")]
        public int Respawn { get; set; } = 15;

        /// <summary>窗口尺寸小且位置靠边（悬浮广告条）。</summary>
        [JsonPropertyName("small_floating")]
        public int SmallFloating { get; set; } = 8;

        /// <summary>标题变化频繁（同一进程同尺寸窗口换标题）。</summary>
        [JsonPropertyName("title_churn")]
        public int TitleChurn { get; set; } = 10;

        /// <summary>★ 达到这个分数才动手。</summary>
        [JsonPropertyName("apply_threshold")]
        public int ApplyThreshold { get; set; } = 55;

        /// <summary>★ 达到这个分数才允许考虑强杀（更高门槛）。</summary>
        [JsonPropertyName("terminate_threshold")]
        public int TerminateThreshold { get; set; } = 75;
    }

    /// <summary>窗口行为拦截配置（与旧的 popup-rules.json 兼容，字段都有默认值）。</summary>
    internal sealed class GuardConfig
    {
        public EmergencyPolicy? Emergency { get; set; } = new EmergencyPolicy();

        public List<Rule>? Rules { get; set; }

        [JsonPropertyName("protect")]
        public Protect? Protect { get; set; }

        [JsonPropertyName("process_whitelist")]
        public List<string>? ProcessWhitelist { get; set; }

        [JsonPropertyName("action")]
        public ActionPolicy? Action { get; set; }

        [JsonPropertyName("behavior")]
        public BehaviorWeights? Behavior { get; set; }

        // ====================================================================
        //  ★★★ 启动时自校验（防调包，用户 2026-10-09 的要求）
        //      "发现它被调包了就能拉停，而不是被感染"
        // ====================================================================

        /// <summary>
        /// ★★★ 自身文件的期望 SHA256（由签名脚本在签名后写入）。
        ///     启动时算自己的哈希 → 与这里比对 ✗ 不符 → 拒绝运行 ✓
        /// ★ 局限：能改配置的人也能改这里 ✗
        ///   所以它只作辅助 ✗ 主判据是 Authenticode 签名状态 ✓
        /// </summary>
        [JsonPropertyName("expected_self_hash")]
        public string? ExpectedSelfHash { get; set; }

        /// <summary>★ 启动时是否做自校验。默认 true。关掉 = 被调包也不拦 ✗</summary>
        [JsonPropertyName("self_check_on_start")]
        public bool SelfCheckOnStart { get; set; } = true;

        /// <summary>★ 自校验失败时是否拒绝运行。默认 true（拉停）✓</summary>
        [JsonPropertyName("self_check_block_on_fail")]
        public bool SelfCheckBlockOnFail { get; set; } = true;

        /// <summary>
        /// ★★ 用户永久授权可以强制结束的程序（由授权弹窗写入）。
        ///     用户可以随时在配置文件里删掉某一项来撤销 ✓
        /// </summary>
        [JsonPropertyName("consented_processes")]
        public List<string>? ConsentedProcesses { get; set; }

        /// <summary>
        /// ★★★ 强杀历史 —— 用于检测"杀了又复活"（用户 2026-10-09 的设计）。
        ///
        /// 记录格式：
        ///     ["python.exe", "adware.exe"]
        ///
        /// 作用：
        ///     如果某个程序【被强杀过】却又冒出了窗口 ✗
        ///     → 说明"强杀一次"不够用，它还会回来 ✗
        ///     → 这时弹窗的推荐要从「立即强杀」改成「永久允许强杀」✓
        ///     → 因为永久强杀能让它一冒头就被打掉，
        ///        用户才有时间去用专业卸载工具彻底清除 ✓
        /// </summary>
        [JsonPropertyName("terminated_history")]
        public List<string>? TerminatedHistory { get; set; }

        /// <summary>★ 硬黑名单：无论怎么配都不允许强杀。写死在代码里。</summary>
        [JsonPropertyName("_硬黑名单说明")]
        public string? HardBlockNote { get; set; }
    }

    // ========================================================================
    //  ★★ 硬编码安全闸 —— 这一层配置改不动，是"防把系统搞崩"的最后一道
    // ========================================================================
    internal static class Safety
    {
        /// <summary>
        /// 绝对不允许强杀的进程。杀其中任何一个都会导致蓝屏或系统重启。
        /// ★ 故意硬编码：即使配置文件被写坏、被恶意修改，也拦得住。
        /// </summary>
        public static readonly HashSet<string> HardBlock = new(StringComparer.OrdinalIgnoreCase)
        {
            // —— 内核与核心子系统：杀 = 立刻蓝屏 ——
            "system", "registry", "memory compression", "memcompression",
            "smss.exe",        // 会话管理器
            "csrss.exe",       // ★ 客户端/服务器运行时 —— 杀它必蓝屏
            "wininit.exe",     // ★ 初始化进程 —— 杀它必蓝屏
            "winlogon.exe",    // ★ 登录进程 —— 杀它必蓝屏
            "services.exe",    // ★ 服务控制管理器 —— 杀它必蓝屏
            "lsass.exe",       // ★ 安全认证 —— 杀它系统重启
            "svchost.exe",     // ★ 承载大量系统服务
            "dwm.exe",         // 桌面窗口管理器 —— 杀了桌面没了
            "audiodg.exe",     // 音频设备图
            "fontdrvhost.exe", // 字体驱动宿主
            "sihost.exe",      // shell 基础设施
            "taskhostw.exe",   // 任务宿主
            "ctfmon.exe",      // 输入法框架
            "explorer.exe",    // ★ 桌面/任务栏 —— 能恢复但极烦，禁止
            "runtimebroker.exe",
            "shellexperiencehost.exe",
            "startmenuexperiencehost.exe",
            "searchhost.exe",
            "searchindexer.exe",
            "securityhealthservice.exe",
            "msmpeng.exe",     // Defender 引擎
            "nissrv.exe",      // Defender 网络检查
            "wmiprvse.exe",
            "spoolsv.exe",     // 打印后台
            "conhost.exe",
            "dllhost.exe",
            "backgroundtaskhost.exe",
            "applicationframehost.exe",
            "textinputhost.exe",
            "useroobebroker.exe",
            "wudfhost.exe",
            "printfilterpipelinesvc.exe",
            "lsaiso.exe",
            "sgrmbroker.exe",
            "vmcompute.exe",
            "vmmem",
            "wslservice.exe",
        };

        /// <summary>
        /// 系统目录前缀。★ 最强的一道闸：
        /// Windows 自带的关键组件都在 System32 / SysWOW64 里，
        /// 而流氓软件不可能把自己装到那里去（需要 TrustedInstaller 权限）。
        /// → 映像路径落在这些目录下的进程，一律不杀。
        /// </summary>
        public static readonly string[] SystemDirMarkers =
        {
            @"\windows\system32\",
            @"\windows\syswow64\",
            @"\windows\winsxs\",
            @"\windows\servicing\",
            @"\windows\boot\",
            @"\windows\systemapps\",
        };

        /// <summary>
        /// ★★ 沙箱白名单（用户 2026-10-09 的规矩 1）：
        ///     沙箱测试时，只能杀【测试脚本自己生成的模拟器进程】。
        ///
        /// 用法：测试台启动工具时设置环境变量
        ///         DSH_SANDBOX_PIDS=1234,5678
        ///       设置了它，强杀目标就必须在这个列表里 ✗ 否则一律拒绝强杀。
        ///       没有设置这个变量 → 表示不是沙箱测试，走常规安全闸。
        ///
        /// ★ 这条约束的意义：即使前面所有闸都被绕过，
        ///   沙箱里的工具也【物理上】够不到用户真实的进程。
        /// </summary>
        private static HashSet<uint>? _sandboxPids;

        public static bool InSandboxMode => _sandboxPids != null;

        public static void LoadSandboxPids()
        {
            string? raw = Environment.GetEnvironmentVariable("DSH_SANDBOX_PIDS");
            if (string.IsNullOrWhiteSpace(raw))
            {
                _sandboxPids = null;
                return;
            }
            var set = new HashSet<uint>();
            foreach (var part in raw.Split(new[] { ',', ';', ' ' },
                                           StringSplitOptions.RemoveEmptyEntries))
            {
                if (uint.TryParse(part.Trim(), out uint pid))
                {
                    set.Add(pid);
                }
            }
            _sandboxPids = set;
        }

        /// <summary>沙箱模式下：这个 PID 是否是自己生成的。</summary>
        public static bool SandboxAllows(uint pid)
        {
            if (_sandboxPids == null)
            {
                return true;   // 非沙箱模式，不施加这条约束
            }
            return _sandboxPids.Contains(pid);
        }

        /// <summary>
        /// ★★ 是否启用签名闸。默认 true。
        ///     沙箱测试时可临时关掉，用来验证 L1/L2/L3 升级阶梯本身是否工作。
        ///     ★ 即便如此，沙箱 PID 约束（闸⓪）依然生效，够不到真实进程 ✓
        /// </summary>
        private static bool _honorSignatures = true;

        public static void SetHonorSignatures(bool value) => _honorSignatures = value;

        /// <summary>
        /// ★★ 判断"能不能就这个进程去问用户"。
        ///
        /// 【为什么不能用 MayTerminate 来判断】
        ///     MayTerminate 的职责是"能不能直接强杀"，它会把
        ///     【签名有效】的进程也拒掉 ✗
        ///     但签名有效恰恰是【最该问用户】的那一类 ✗✗✗
        ///     —— 2026-10-09 实测就栽在这：弹窗永远不触发，
        ///        因为 MayTerminate 恒为 false。
        ///
        /// 【这个方法检查什么】
        ///     · 沙箱约束（规矩 1）—— 不给用户选择权 ✗
        ///     · 硬黑名单 —— 杀了会蓝屏，不给选择权 ✗
        ///     · 系统目录 —— 同上 ✗
        ///     · 签名状态必须是 Valid —— 只有"程序本身正经、只是行为讨厌"
        ///       这一种情形才适合交给用户决定 ✓
        ///       其余（不可判断/被篡改/不受信任/无签名）都不该问用户 ✗
        /// </summary>
        public static bool MayAskUser(string procName, string imagePath, uint pid,
                                      out string reason)
        {
            reason = string.Empty;

            // 闸⓪ 沙箱约束 —— 沙箱测试里不许弹窗打扰用户 ✗
            if (!SandboxAllows(pid))
            {
                reason = $"沙箱模式：pid {pid} 不在本测试生成的进程列表里";
                return false;
            }

            // 闸① 硬黑名单 —— 系统关键进程永远不给用户这个选项 ✗
            if (HardBlock.Contains(procName))
            {
                reason = "系统关键进程，不提供强制结束选项";
                return false;
            }

            // 闸② 系统目录 —— 同上 ✗
            if (!string.IsNullOrEmpty(imagePath))
            {
                string lower = imagePath.Replace('/', '\\').ToLowerInvariant();
                foreach (var marker in SystemDirMarkers)
                {
                    if (lower.Contains(marker))
                    {
                        reason = "映像位于系统目录，不提供强制结束选项";
                        return false;
                    }
                }
            }

            // 闸③ ★ 签名必须有效 —— 这是"可以交给用户"的唯一情形 ✓
            var st = Signer.Verify(imagePath);
            if (st != SignatureState.Valid)
            {
                reason = "签名状态为「" + Signer.Describe(st)
                         + "」，不属于可交给用户决定的范围";
                return false;
            }

            return true;
        }

        /// <summary>是否绝对禁止强杀。任一条件命中就返回 false（不允许）。</summary>
        public static bool MayTerminate(string procName, string imagePath, uint pid,
                                        out string reason)
            => MayTerminate(procName, imagePath, pid, false, out reason);

        /// <summary>
        /// 同上，但可以声明"用户已经明确授权对这个程序用强制手段"。
        ///
        /// ★★ 即使用户授权了，下面这些也【一律不放过】✗：
        ///     · 沙箱约束（规矩 1）
        ///     · 硬黑名单（系统关键进程，杀了会蓝屏）
        ///     · 系统目录路径
        ///     · 签名状态"不可判断 / 被篡改 / 不受信任"
        ///   用户授权只能放行【签名有效】这一种情况 ✓
        ///   —— 因为"有合法签名"是唯一一条"程序本身是正经的、
        ///      只是行为讨厌"的情形；其余情况属于不该由我们动手 ✗
        /// </summary>
        public static bool MayTerminate(string procName, string imagePath, uint pid,
                                        bool userConsented, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(procName))
            {
                reason = "进程名为空，无法确认身份";
                return false;
            }

            // 闸⓪：★★ 沙箱约束（规矩 1）—— 只允许杀测试脚本自己生成的进程
            if (!SandboxAllows(pid))
            {
                reason = $"沙箱模式：pid {pid} 不在本测试生成的进程列表里，禁止强杀";
                return false;
            }

            // 闸①：硬黑名单
            if (HardBlock.Contains(procName))
            {
                reason = "在系统关键进程硬黑名单里（杀它会导致蓝屏/重启）";
                return false;
            }

            // 闸②：★★ 系统目录 —— 最强的一道
            if (!string.IsNullOrEmpty(imagePath))
            {
                string lower = imagePath.Replace('/', '\\').ToLowerInvariant();
                foreach (var marker in SystemDirMarkers)
                {
                    if (lower.Contains(marker))
                    {
                        reason = "映像位于系统目录（" + marker.Trim('\\') + "），禁止强杀";
                        return false;
                    }
                }
            }

            // 闸③：★★★ 数字签名 —— 用户 2026-10-09 定的规矩 2/3
            //        有合法签名的进程，不论开多少窗口，最多只允许隐藏或 WM_CLOSE
            //        ★ 判定保守：只有明确"无签名"才放行，
            //          验签出错/不确定一律按已签名处理
            //
            // ★★ 唯一的例外：用户通过授权弹窗【明确同意】了对这个程序用强制手段 ✓
            //    而且只放行 SignatureState.Valid 这一种 ✓
            //    其余（不可判断 / 被篡改 / 不受信任）即使是用户也没得选 ✗
            if (_honorSignatures)
            {
                var st = Signer.Verify(imagePath);
                if (userConsented)
                {
                    if (st == SignatureState.Valid)
                    {
                        // 用户已明确授权，且签名有效 → 放行这一道闸 ✓
                        // （前面的沙箱/黑名单/系统目录已经过了）
                    }
                    else
                    {
                        reason = "用户虽已授权，但签名状态为「" + Signer.Describe(st)
                                 + "」，不属于可授权范围，仍禁止强杀";
                        return false;
                    }
                }
                else if (Signer.IsProtectedFromKill(imagePath, out string sigReason))
                {
                    reason = sigReason;
                    return false;
                }
            }

            return true;
        }

        /// <summary>是不是"看起来正常"的窗口 —— 有输入框或有多个子控件。</summary>
        public static bool LooksLikeRealWindow(int childCount, bool hasEdit)
        {
            // 真实对话框一般有成组控件；广告弹窗往往只有一个展示区
            return hasEdit || childCount >= 3;
        }
    }

    // ========================================================================
    //  行为档案：记录一个窗口/进程的历史，用于评分
    // ========================================================================
    internal sealed class BehaviorRecord
    {
        /// <summary>★ 最近一次评分（应急选址用）</summary>
        public int LastScore;


        /// <summary>★ 处置日志限速用（防止窗口轰炸把日志刷爆）</summary>
        public long LastActLogTick;

        /// <summary>该进程创建过多少个窗口（越多越可疑）。</summary>
        /// <summary>
        /// ★★ A52A 实测新增：这个进程的名字（【记档时就存下来】）✓
        ///
        /// 【为什么必须存】
        ///   应急时要按 pid 反查进程名，但窗口被我们隐藏之后现取会得到空串 ✗
        ///   → 目标被"取不到名字就不动手"这条安全判据挡掉 ✗✗
        ///   （实测证据：两个模拟器 score=109/97、处置了 10/11 次，
        ///     却因为 proc= 空而全被滤掉 → 应急一次都没开火）
        ///   记档时存下来，就永远不会取不到 ✓
        /// </summary>
        public string Proc = string.Empty;

        public int WindowsSeen;

        /// <summary>我们尝试关闭过几次。</summary>
        public int CloseAttempts;

        /// <summary>尝试关闭后仍存活的次数。</summary>
        public int CloseFailures;

        /// <summary>第一次见到的时间。</summary>
        public DateTime FirstSeen = DateTime.Now;

        /// <summary>最近一次尝试关闭的时间。</summary>
        public DateTime LastAttempt = DateTime.MinValue;

        /// <summary>见过的标题集合（用于判断"换标题"）。</summary>
        public readonly HashSet<string> TitlesSeen = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>见过的类名集合（用于判断"换类名"）。</summary>
        public readonly HashSet<string> ClassesSeen = new(StringComparer.OrdinalIgnoreCase);

        // ====================================================================
        //  ★★ 滑动时间窗统计（2026-10-09 评分调优）
        // ====================================================================
        //  【为什么要改】
        //      原来用 TitlesSeen.Count 这种【累计值】判断"换标题频繁"✗
        //      问题：程序跑得越久数字越大 ✗
        //            → 一个跑了一天的正常程序会被无限加分 ✗
        //            → 而刚启动的流氓软件反而分不够 ✗
        //      这是典型的"累计量当强度用"的错误 ✓
        //
        //  【改成】只统计【最近 WindowMs 内】新建的窗口数 ✓
        //      这样"短时间内疯狂弹窗"才会得高分 ✓
        //      跑得久不再自动加分 ✓
        // ====================================================================

        /// <summary>滑动窗口长度（毫秒）。</summary>
        public const int WindowMs = 10000;   // ★ 常量 WindowMs

        /// <summary>最近窗口内新建窗口的时间戳（毫秒 tick）。</summary>
        public readonly List<long> RecentCreateTicks = new();

        /// <summary>最近窗口内换过标题的时间戳。</summary>
        public readonly List<long> RecentTitleTicks = new();

        /// <summary>第一次见到该进程的时间（毫秒 tick）。</summary>
        public long FirstSeenTick = Environment.TickCount64;

        /// <summary>记录一次"新建窗口"。</summary>
        public void NoteNewWindow()
        {
            long now = Environment.TickCount64;
            RecentCreateTicks.Add(now);
            PruneTicks(RecentCreateTicks, now);
        }

        /// <summary>记录一次"换了新标题"。</summary>
        public void NoteNewTitle()
        {
            long now = Environment.TickCount64;
            RecentTitleTicks.Add(now);
            PruneTicks(RecentTitleTicks, now);
        }

        private static void PruneTicks(List<long> list, long now)
        {
            // ★ 清掉窗口之外的，同时给列表一个上限（防内存增长）
            int keep = list.Count;
            while (keep > 0 && now - list[keep - 1] > WindowMs)
            {
                keep--;
            }
            if (keep < list.Count)
            {
                list.RemoveRange(keep, list.Count - keep);
            }
            // 极端情况下的硬上限
            const int MaxKeep = 5000;
            if (list.Count > MaxKeep)
            {
                list.RemoveRange(0, list.Count - MaxKeep);
            }
        }

        /// <summary>★ 最近窗口内新建了多少个窗口。</summary>
        public int RecentNewWindows() => RecentCreateTicks.Count;

        /// <summary>★ 最近窗口内换过多少次标题。</summary>
        public int RecentTitleChanges() => RecentTitleTicks.Count;

        /// <summary>
        /// ★ 这个进程是不是"刚出现"（存活时间很短）。
        ///   弹窗广告的典型特征：突然冒出来 ✓
        ///   而正常程序往往开很久了 ✓
        /// </summary>
        public bool IsYoungProcess(int youngMs = 15000)
            => Environment.TickCount64 - FirstSeenTick <= youngMs;

        // ====================================================================
        //  ★★★ 多进程守护检测（U3 场景，用户 2026-10-09 要求）
        // ====================================================================
        //  【守护是什么】
        //      强杀掉 A，B 进程立刻把 A 重启 ✗
        //      → 单进程强杀完全无效 ✗
        //      → 只杀 A 会陷入"杀-重启-杀-重启"的循环 ✗
        //
        //  【怎么发现】
        //      我们强杀过某个进程名 ✗
        //      如果【同名进程】在短时间内又出现 ✓
        //      → 说明有东西在重启它 ✗
        //
        //  【怎么办】
        //      ★ 不盲目连坐杀守护者 ✗（守护者可能是计划任务/服务 ✗）
        //      → 明确告知用户 + 引导用专业卸载工具根治 ✓
        // ====================================================================

        /// <summary>最近一次被强杀的时刻（毫秒 tick）。</summary>
        public long LastTerminatedTick = 0;

        /// <summary>★ 被判定为"有守护进程"（杀完又重现）。</summary>
        public bool SuspectedGuarded = false;

        /// <summary>杀完重现的次数。</summary>
        public int RespawnAfterKill = 0;

        /// <summary>
        /// ★ 判断"是不是强杀后又被重启了"。
        ///   在进程再次出现时调用 ✓
        /// </summary>
        /// <param name="withinMs">多久之内重现算"被守护"</param>
        public bool NoteReappearAfterKill(int withinMs = 30000)
        {
            if (LastTerminatedTick == 0)
            {
                return false;
            }
            long delta = Environment.TickCount64 - LastTerminatedTick;
            if (delta > 0 && delta <= withinMs)
            {
                RespawnAfterKill++;
                SuspectedGuarded = true;
                return true;
            }
            return false;
        }

        /// <summary>是否曾经是前台（抢过焦点）。</summary>
        public bool EverForeground;

        /// <summary>是否曾经置顶。</summary>
        public bool EverTopmost;

        /// <summary>★ 是否已经对它用过强杀（避免重复升级动作）。</summary>
        public bool Terminated;

        /// <summary>
        /// ★★ 第一次弹窗的时间。用于实现【两段式弹窗】：
        ///     第一次问过之后，如果用户选了"继续隐藏"，
        ///     再过 SecondStageDelayMs 毫秒仍压不住 → 弹第二次 ✓
        /// </summary>
        public DateTime ConsentAskedAt = DateTime.MinValue;

        /// <summary>★ 是否已经弹过第二次（第二轮确认）—— 不重复弹 ✗</summary>
        public bool SecondStageAsked;

        // ====================================================================
        //  ★★ 第二段弹窗的"追不追得上"判据（取代单纯等 15 秒）
        // ====================================================================

        /// <summary>第一次弹窗之后，我们隐藏了多少次。</summary>
        public int HidesSinceConsent;

        /// <summary>第一次弹窗那一刻，该进程有多少个窗口。</summary>
        public int WindowsAtConsent;

        /// <summary>最近一次观察到的该进程窗口数。</summary>
        public int LastWindowCount;

        /// <summary>第一次弹窗之后，该进程新建了多少个窗口（用标题数增量估算）。</summary>
        public int NewWindowsSinceConsent;

        /// <summary>第一次弹窗时该进程已见过的标题数（用于算增量）。</summary>
        public int TitlesAtConsent;

        /// <summary>
        /// ★ 判断"常规手段是否已经确认失效"。
        ///
        /// 逻辑：
        ///   已经隐藏了足够多次（minHides），但窗口数【没有比弹窗时少】✗
        ///   → 说明关掉的速度追不上重建的速度 → 确认无效 ✓
        ///
        /// ★ 这个判据比"等固定时间"精确得多：
        ///     真追不上时，几秒内就能得出结论 ✓
        ///     真在起效时，不会误判（窗口数在降 → 不满足条件）✓
        /// </summary>
        public bool IsConfirmedIneffective(int minHides)
        {
            if (HidesSinceConsent < minHides)
            {
                return false;
            }
            // ★ 窗口数没有减少 = 追不上
            return LastWindowCount >= WindowsAtConsent;
        }

        /// <summary>★ 工具是否在起效（窗口数在下降）。用于日志说明。</summary>
        public bool IsMakingProgress()
            => LastWindowCount < WindowsAtConsent;

        /// <summary>最近 60 秒内是否已经过了 deadline 计时起点。</summary>
        public DateTime FirstAttempt = DateTime.MinValue;

        public bool WithinDeadline(int deadlineMs)
        {
            if (FirstAttempt == DateTime.MinValue)
            {
                return true;
            }
            return (DateTime.Now - FirstAttempt).TotalMilliseconds <= deadlineMs;
        }
    }
}


// ============================================================================
//  ★ A46 应急压制策略（事故后重建：该类在更早会话加进 Core.cs，恢复版里没有 ✗）
//     被攻击时不再等用户点"同意"，先用高频压制按住流氓软件 ✓
// ============================================================================
internal sealed class EmergencyPolicy
{
    [System.Text.Json.Serialization.JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("seconds")]
    public int Seconds { get; set; } = 60;

    [System.Text.Json.Serialization.JsonPropertyName("hard_cap_seconds")]
    public int HardCapSeconds { get; set; } = 600;

    [System.Text.Json.Serialization.JsonPropertyName("interval_ms")]
    public int IntervalMs { get; set; } = 50;

    [System.Text.Json.Serialization.JsonPropertyName("min_score")]
    public int MinScore { get; set; } = 60;

    [System.Text.Json.Serialization.JsonPropertyName("min_failures")]
    public int MinFailures { get; set; } = 2;

    [System.Text.Json.Serialization.JsonPropertyName("calm_seconds")]
    public int CalmSeconds { get; set; } = 4;

    [System.Text.Json.Serialization.JsonPropertyName("kill_signed")]
    public bool KillSigned { get; set; } = false;

    /// <summary>
    /// ★ 应急时对"不让杀"的进程（签名/白名单）要不要【冻结】。
    ///   规矩 2 原话是"最多只能隐藏或发 WM_CLOSE"，而冻结比隐藏更强 ✗
    ///   应急下只隐藏压不住 → 默认只冻不杀 ✓
    ///   不认同就改成 false → 退回"只隐藏 + WM_CLOSE" ✓
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("freeze_pinned")]
    public bool FreezePinned { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("max_actions_per_tick")]
    public int MaxActionsPerTick { get; set; } = 4;
}
