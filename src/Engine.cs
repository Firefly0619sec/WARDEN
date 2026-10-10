// ============================================================================
//  行为评分 + 分级处置引擎
// ============================================================================
//  这是"窗口行为拦截"的实际大脑。两件事：
//      ① 给每个候选窗口打一个【可疑度分】，而不是简单的命中/不命中
//      ② 按分数与历史，决定处置等级：L1 礼貌关闭 → L2 劝退 → L3 强杀
//
//  【★ 为什么用评分而不是布尔】
//      流氓软件的对抗是【渐进】的：
//        · 只换标题的，可能只是普通弹窗（低可疑）
//        · 换标题 + 换类名 + 关掉重建 + 抢焦点 → 明显是对抗（高可疑）
//      布尔匹配只能二选一，评分能区分"可疑"与"非常可疑"，
//      从而给出【不同强度的处置】，避免一上来就强杀。
//
//  【★ 时限：用户要求"时间必须快"】
//      处置阶梯有硬性 deadline（默认 4500ms）。
//      做法是不 sleep 等待，而是【记录时间戳、下一轮再检查】：
//      主循环每 100ms 转一圈，到点就升级。这样既不阻塞，
//      又能在 0.5 秒左右完成一次"发出关闭请求 → 确认是否成功"。
//
//  【★★ 强杀的四道闸】（缺一不可，全部通过才允许）
//      ① Safety.MayTerminate：硬黑名单 + System32 路径
//      ② 用户白名单
//      ③ 分数 ≥ terminateThreshold（比普通处置门槛更高）
//      ④ 已经失败过 failuresBeforeKill 次，且没有"正常"窗口
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace WARDEN
{
    /// <summary>一次评分的结果。</summary>
    internal sealed class ScoreResult
    {
        public int Score;
        public readonly List<string> Reasons = new();

        public void Add(int points, string why)
        {
            if (points == 0)
            {
                return;
            }
            Score += points;
            Reasons.Add($"{why}({points:+#;-#;0})");
        }

        public string Why => string.Join(" ", Reasons);
    }

    /// <summary>处置等级。</summary>
    internal enum ActionLevel
    {
        None = 0,
        /// <summary>礼貌请求关闭（PostMessage WM_CLOSE）。</summary>
        L1_Close = 1,
        /// <summary>劝退：同步发送 + 挂起检测，更强硬但仍不破坏进程。</summary>
        L2_Insist = 2,
        /// <summary>★ 强杀：TerminateProcess。必须有安全闸放行。</summary>
        L3_Terminate = 3,
        /// <summary>
        /// ★ 隐藏：对【有合法数字签名】或【在白名单】的进程，
        ///   最多只允许隐藏窗口或发 WM_CLOSE —— 用户 2026-10-09 定的规矩。
        /// </summary>
        Hide = 4,
    }

    internal sealed class Engine
    {
        private GuardConfig _cfg;
        private readonly Action<string> _log;
        /// <summary>窗口句柄 → 行为档案（按进程共享）。</summary>
        private readonly Dictionary<uint, BehaviorRecord> _procHistory = new();
        /// <summary>窗口句柄 → 最近一次尝试时间，避免重复发消息。</summary>
        private readonly Dictionary<IntPtr, DateTime> _lastPoke = new();

        /// <summary>
        /// ★ A52A：本轮扫描已经花在"同步发送"上的毫秒数。
        ///   BeginSweep() 每轮清零；用完预算就只发异步关闭 ✓
        /// </summary>
        private int _blockSpentMs;

        /// <summary>★ 每轮扫描给同步发送的总预算（毫秒）——防止被窗口轰炸拖死 ✓</summary>
        private const int BlockBudgetMs = 200;

        /// <summary>★ 每轮扫描开始时调用（由主循环调用）✓</summary>
        public void BeginSweep()
        {
            _blockSpentMs = 0;
        }

        /// <summary>
        /// ★★ 本次运行中【强杀成功过】的程序名（待写回配置）。
        ///     用途：检测"杀了又复活"✗
        ///     如果它之后又出现窗口 → 说明强杀一次不够 ✗
        ///     → 下次弹窗推荐改成「永久允许强杀」✓
        /// </summary>
        private readonly List<string> _terminatedProcs = new();

        /// <summary>
        /// ★★★ 按【进程名】记录最后一次强杀时刻 —— 用于检测"被守护进程重启"✗
        ///     为什么按名字而不是 PID：
        ///       强杀之后进程没了 ✗ 重启后是【新的 PID】✗
        ///       只有进程名能串起来 ✓
        /// </summary>
        private readonly Dictionary<string, long> _killTickByName =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>★ 被判定为"有守护进程"的进程名（杀完又重现）。</summary>
        private readonly HashSet<string> _guardedProcs =
            new(StringComparer.OrdinalIgnoreCase);

        public bool IsGuarded(string proc)
        {
            foreach (var n in _guardedProcs)
            {
                if (proc.Contains(n, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>取出并清空"已强杀"名单（由 Program 写回配置）。</summary>
        public List<string> PopTerminated()
        {
            var copy = new List<string>(_terminatedProcs);
            _terminatedProcs.Clear();
            return copy;
        }

        public Engine(GuardConfig cfg, Action<string> log)
        {
            _cfg = cfg;
            _log = log;
        }

        /// <summary>
        /// ★★★ 只换配置，【保留行为历史】。
        ///
        /// 之前主循环里每轮都 new Engine(...) ✗
        /// → _procHistory / _lastPoke 每 100ms 就被清空一次 ✗
        /// → CloseFailures 永远是 0 ✗ 升级阶梯完全失效 ✗
        /// → 表现为"只发 L1，从不升级到 L2/L3"，15 秒发了 288 次 L1
        ///
        /// （2026-10-09 红队对抗测试抓出来的，见 R3/R4/R5/R7 场景）
        /// </summary>
        public void UpdateConfig(GuardConfig cfg) => _cfg = cfg;

        private ActionPolicy Policy => _cfg.Action ?? new ActionPolicy();
        private BehaviorWeights Weights => _cfg.Behavior ?? new BehaviorWeights();

        // ====================================================================
        //  评分
        // ====================================================================
        /// <summary>
        /// 给一个窗口打分。
        /// ★ 注意：这里只做"判断"，不产生任何副作用，方便单独测试评分逻辑。
        /// </summary>
        public ScoreResult Score(WindowInfo w, Rule matchedRule, bool isForeground)
        {
            var r = new ScoreResult();
            var bw = Weights;

            // —— 命中用户规则：最直接的证据 ——
            r.Add(bw.RuleHit, "命中规则「" + matchedRule.Name + "」");

            // —— 行为特征 ——
            if (isForeground)
            {
                r.Add(bw.StealsFocus, "抢占了前台焦点");
            }

            if (w.IsTopmost)
            {
                r.Add(bw.Topmost, "强制置顶");
            }

            // ★★ 空标题的窗口额外加分 ✗
            //    为什么：正常的用户界面窗口几乎都有标题 ✓
            //    有窗口、有类名、却故意留空标题 → 是为了绕开标题匹配 ✓
            //    这是"对抗性"的典型特征，值得加权 ✓
            if (string.IsNullOrEmpty(w.Title))
            {
                r.Add(bw.TitleChurn, "标题为空(疑似规避匹配)");
            }

            if (!w.HasEdit)
            {
                // 真实对话框通常有输入控件；纯展示型窗口更像广告
                r.Add(bw.NoEdit, "无输入控件");
            }

            if (w.ChildCount == 0)
            {
                r.Add(bw.NoChildren, "无子控件(纯展示)");
            }

            if (w.IsSmallFloating)
            {
                r.Add(bw.SmallFloating, "小尺寸悬浮窗");
            }

            // —— 历史行为（需要 BehaviorRecord）——
            if (_procHistory.TryGetValue(w.Pid, out var rec))
            {
                // ★★★ 改用【滑动时间窗】统计（2026-10-09 评分调优）
                //     原来用累计值（WindowsSeen / TitlesSeen.Count）✗
                //     问题：程序跑得越久数字越大 ✗
                //          → 一个跑了一天的正常程序被无限加分 ✗
                //          → 刚启动的流氓软件反而分不够 ✗
                //     现在只统计【最近 10 秒内】的行为 ✓
                int recentNew = rec.RecentNewWindows();
                int recentTitle = rec.RecentTitleChanges();

                // 短时间内疯狂新建窗口 → 弹一堆
                if (recentNew >= 4)
                {
                    r.Add(bw.Respawn, $"最近10秒新建 {recentNew} 个窗口");
                }

                // 短时间内频繁换标题 → 明显在对抗匹配
                if (recentTitle >= 4)
                {
                    r.Add(bw.TitleChurn, $"最近10秒换过 {recentTitle} 次标题");
                }

                // ★ 刚出现的进程 + 行为可疑 → 额外加权
                //   弹窗广告的典型特征就是"突然冒出来"✗
                if (rec.IsYoungProcess() && (recentNew >= 2 || recentTitle >= 2))
                {
                    r.Add(bw.Respawn, "新出现的进程且行为异常");
                }

                // 之前关过但没关掉 → 顽固
                if (rec.CloseFailures > 0)
                {
                    r.Add(bw.Respawn, $"已尝试关闭 {rec.CloseFailures} 次未成功");
                }

                if (rec.EverTopmost && !w.IsTopmost)
                {
                    r.Add(bw.Topmost, "曾置顶(行为反复)");
                }
            }

            return r;
        }

        // ====================================================================
        //  处置
        // ====================================================================
        /// <summary>
        /// 决定并执行处置。返回是否已经动手（用于日志统计）。
        /// ★ 这个方法【不会阻塞】—— 它发完消息就返回，是否升级由下一轮判断。
        /// </summary>
        public ActionLevel Act(WindowInfo w, int score, BehaviorRecord rec, bool dryRun)
        {
            var pol = Policy;

            // 还没到动手门槛：只观察
            if (score < Weights.ApplyThreshold)
            {
                return ActionLevel.None;
            }

            // 已经强杀过就不重复
            if (rec.Terminated)
            {
                return ActionLevel.None;
            }

            // 记下本轮起点（用于 deadline 判断）
            bool firstTouch = rec.FirstAttempt == DateTime.MinValue;
            if (firstTouch)
            {
                rec.FirstAttempt = DateTime.Now;
            }

            // 距离上次动手太近 → 等它一会儿，别刷屏
            if (_lastPoke.TryGetValue(w.Hwnd, out var last))
            {
                double since = (DateTime.Now - last).TotalMilliseconds;
                if (since < pol.L1WaitMs)
                {
                    return ActionLevel.None;
                }
            }

            // ★★ 先判断这个进程是不是"受保护"的（有合法签名 / 在白名单）
            //    用户 2026-10-09 的规矩 2：这类进程不论开多少窗口，
            //    最多只能【隐藏】或发 WM_CLOSE，绝不能强杀 ✗
            //    ★ 两个 honor_* 开关生产环境都是 true；
            //      只有沙箱测试才会临时关掉，用来验证升级阶梯本身 ✓
            bool protectedProc =
                (pol.HonorWhitelistForKill && IsWhitelisted(w.Proc))
                || (pol.HonorSignatures && Signer.IsProtectedFromKill(w.ImagePath, out _));

            // 判断该发哪一级
            ActionLevel level;
            if (protectedProc)
            {
                // ★★ 受保护进程（有签名 / 在白名单）：规矩是"只能隐藏或 WM_CLOSE"✗
                //    但如果它反复重建、常规手段压不住，就按用户的设计【两段式弹窗】✓
                //
                //    第一段：失败 ConsentAfterFailures 次 → 问用户
                //      三个选项：允许本次强杀 / 永久允许强杀 / 拒绝继续尝试隐藏
                //    第二段：用户选了"继续隐藏"，再过 SecondStageDelayMs（默认 15 秒）
                //      仍然压不住 → 再问一次，这次确认"常规拦截已完全失效"，
                //      选项变成：立即强杀 / 永久允许强杀 / 继续尝试隐藏
                //
                // ★ 安全顺序：先过硬黑名单/系统目录/沙箱三闸（不给用户选择）✗
                //   只有能问的（签名有效的正经程序）才轮到弹窗 ✓
                bool reachedFirst = rec.CloseFailures >= pol.ConsentAfterFailures;
                string askReason = string.Empty;   // ★ 先声明再赋值，避免 CS0165
                bool canAsk = (reachedFirst || rec.ConsentAskedAt != DateTime.MinValue)
                              && pol.AllowConsentPrompt
                              && Safety.MayAskUser(w.Proc, w.ImagePath, w.Pid,
                                                   out askReason);

                // ★★ 决策点诊断日志（前几轮就是在这里查不出原因，只能靠猜 ✗）
                //    条件收窄一点，避免刷屏
                if (rec.CloseAttempts % 5 == 1 || rec.CloseFailures > 0)
                {
                    _log($"[决策] {w.Proc} 尝试={rec.CloseAttempts} 失败={rec.CloseFailures}"
                         + $" 门槛={pol.ConsentAfterFailures}"
                         + $" reachedFirst={reachedFirst} canAsk={canAsk}"
                         + (canAsk ? "" : " 拦因=" + (askReason.Length > 0 ? askReason : "(未评估)")));
                }

                if (canAsk)
                {
                    // 决定这是第一段还是第二段
                    ConsentStage stage;
                    if (rec.ConsentAskedAt == DateTime.MinValue)
                    {
                        stage = ConsentStage.First;
                    }
                    else if (!rec.SecondStageAsked)
                    {
                        // ★★ 第二段的触发判据（用户要求：缩短时间，但要确定真压不住）
                        //
                        //   ① 主判据：已隐藏 minHides 次，但窗口数没减少 → 追不上 ✓
                        //      真追不上时 2~3 秒就能出结论，不用干等 ✗
                        //   ② 兜底：等满 maxWaitMs 还没出结论也弹（防僵住）
                        int maxWait = pol.SecondStageMaxWaitMs > 0
                                      ? pol.SecondStageMaxWaitMs
                                      : (pol.SecondStageDelayMs > 0
                                         ? pol.SecondStageDelayMs : 6000);

                        bool confirmed = rec.IsConfirmedIneffective(pol.SecondStageMinHides);
                        bool timedOut = (DateTime.Now - rec.ConsentAskedAt)
                                        .TotalMilliseconds >= maxWait;

                        if (confirmed || timedOut)
                        {
                            stage = ConsentStage.Second;
                            rec.SecondStageAsked = true;
                            _log($"[第二段判定] 判据={(confirmed ? "追不上(主判据)" : "兜底超时")}"
                                 + $" 已隐藏={rec.HidesSinceConsent}次"
                                 + $" 窗口数={rec.LastWindowCount}(弹窗时{rec.WindowsAtConsent})"
                                 + $" 新建={rec.NewWindowsSinceConsent}"
                                 + $" 耗时={(DateTime.Now - rec.ConsentAskedAt).TotalMilliseconds:F0}ms");
                        }
                        else
                        {
                            // 还没确认失效 → 继续隐藏，不打扰用户 ✓
                            return ActHide(w, score, rec, pol);
                        }
                    }
                    else
                    {
                        // 第二段也问过了 → 不再重复，只继续隐藏
                        return ActHide(w, score, rec, pol);
                    }

                    if (stage == ConsentStage.First)
                    {
                        rec.ConsentAskedAt = DateTime.Now;
                        // ★ 记下"弹窗那一刻"的基线，用于后续判断是否追得上
                        rec.WindowsAtConsent = rec.LastWindowCount;
                        rec.TitlesAtConsent = rec.TitlesSeen.Count;
                        rec.HidesSinceConsent = 0;
                        rec.NewWindowsSinceConsent = 0;
                    }

                    // ★★★ A52A：问，但【不等】——
                    //   以前这里直接 Ask()，而它是模态的 ✗
                    //   对话框一弹，整个扫描循环就被堵住（连 .stop 都不查）✗✗
                    if (!ConsentDialog.TryTakeResult(w.Proc, out ConsentAnswer ans))
                    {
                        if (!ConsentDialog.IsPending(w.Proc))
                        {
                            ConsentDialog.RequestConsentAsync(w.Proc, w.ImagePath, w.Title,
                                                              score, rec.CloseFailures, stage);
                            _log($"[CONSENT-ASK] {w.Proc} -- 已弹出询问"
                                 + "（★ 不阻塞：等答案期间继续隐藏）");
                        }
                        return ActHide(w, score, rec, pol);   // ★ 等答案期间维持原处置 ✓
                    }

                    if (ans == ConsentAnswer.KillOnce || ans == ConsentAnswer.KillAlways
                        || ans == ConsentAnswer.PreGranted)
                    {
                        _log($"[★用户授权] {w.Proc} —— 允许强制结束"
                             + (ans == ConsentAnswer.KillAlways ? "（永久）"
                                : ans == ConsentAnswer.PreGranted ? "（配置中已永久允许）"
                                : "（本次）"));
                        level = ActionLevel.L3_Terminate;
                    }
                    else
                    {
                        _log($"[用户选择继续隐藏] {w.Proc} —— 第{(stage == ConsentStage.First ? "一" : "二")}段"
                             + "（" + (ans == ConsentAnswer.Timeout ? "超时未答" : "拒绝") + "）");
                        return ActHide(w, score, rec, pol);
                    }
                }
                else
                {
                    if (reachedFirst && pol.AllowConsentPrompt)
                    {
                        _log($"[不询问用户] {w.Proc} —— {askReason}");
                    }
                    return ActHide(w, score, rec, pol);
                }
            }
            else if (rec.CloseFailures == 0)
            {
                level = ActionLevel.L1_Close;
            }
            else if (rec.CloseFailures < pol.FailuresBeforeKill)
            {
                level = ActionLevel.L2_Insist;
            }
            else
            {
                level = ActionLevel.L3_Terminate;
            }

            // ★★ 强杀的最后复核 —— 这里才是真正决定"要不要杀"的地方
            if (level == ActionLevel.L3_Terminate)
            {
                if (!MayKillNow(w, score, rec, dryRun))
                {
                    // 不允许杀 → 退回"隐藏+关闭"，继续用温和手段
                    level = ActionLevel.Hide;
                }
            }

            // deadline：超过总时限还没搞定就不再等了
            if (!rec.WithinDeadline(pol.DeadlineMs) && level != ActionLevel.L3_Terminate)
            {
                // 超时了但仍不允许杀 → 记为放弃本轮
                return ActionLevel.None;
            }

            _lastPoke[w.Hwnd] = DateTime.Now;
            rec.LastAttempt = DateTime.Now;
            rec.CloseAttempts++;

            if (dryRun)
            {
                _log($"[演习] 本应执行 {level}  分数={score}  {w.Proc} 「{Trunc(w.Title)}」");
                return level;
            }

            switch (level)
            {
                case ActionLevel.L1_Close:
                    // 异步投递，不阻塞
                    Native.PostMessageW(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    _log($"[L1 请求关闭] 分数={score} {w.Proc} 「{Trunc(w.Title)}」");
                    break;

                case ActionLevel.L2_Insist:
                    // ★★ A52A 实测：同步发送会阻塞（每次最多 800ms）✗
                    //    窗口一多，一轮扫描能被拖到几十秒 → 循环彻底追不上 ✗✗
                    //    （现象：日志在某个时刻全停，连 .stop 都不再检查）
                    //    → 给整轮设预算：用完就只发异步的，宁可慢一拍也不能被拖死 ✓
                    if (_blockSpentMs < BlockBudgetMs)
                    {
                        long tSync = Environment.TickCount64;
                        uint wait = (uint)Math.Min(
                            Math.Min(pol.L2WaitMs, 250),
                            Math.Max(50, BlockBudgetMs - _blockSpentMs));
                        Native.SendMessageTimeoutW(
                            w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero,
                            Native.SMTO_ABORTIFHUNG, wait, out _);
                        _blockSpentMs += (int)(Environment.TickCount64 - tSync);
                    }
                    else
                    {
                        _log($"[L2-SYNC-BUDGET] 本轮同步预算用完，只发异步关闭"
                             + $" {w.Proc}「{Trunc(w.Title)}」");
                    }
                    // 再补一发异步的，以及一个系统菜单关闭
                    Native.PostMessageW(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    Native.PostMessageW(w.Hwnd, Native.WM_SYSCOMMAND,
                                        new IntPtr(Native.SC_CLOSE), IntPtr.Zero);
                    bool hung = Native.IsHungAppWindow(w.Hwnd);
                    _log($"[L2 劝退] 分数={score} {w.Proc} 「{Trunc(w.Title)}」"
                         + (hung ? " ★目标无响应" : ""));
                    break;

                case ActionLevel.L3_Terminate:
                    DoTerminate(w, score, rec);
                    break;

                case ActionLevel.Hide:
                    // ★ 规矩 2：对【有合法签名/在白名单】的进程，
                    //   最多只允许"隐藏窗口"或"WM_CLOSE"，不能强杀。
                    //   隐藏比关闭更温和：程序还在跑，只是那个窗口看不见了。
                    // ★ A52A：必须用【异步】版！ShowWindow 会同步发 WM_SHOWWINDOW，
            //   目标线程不响应时会把主循环卡住（实测 5 秒起）✗
            Native.ShowWindowAsync(w.Hwnd, Native.SW_HIDE);
                    // 再补一发 WM_CLOSE —— 隐藏不掉的（比如会自己再显示）就顺手关掉
                    Native.PostMessageW(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    _log($"[L2 隐藏+关闭] 分数={score} {w.Proc} 「{Trunc(w.Title)}」"
                         + "  ★受保护进程(有签名/白名单)，不采用强杀");
                    break;
            }

            return level;
        }

        /// <summary>
        /// ★★ 每轮更新"每个进程当前有多少个窗口"。
        ///
        /// 为什么要它：第二段弹窗的主判据是"追不追得上"✗
        ///   判据 = 已隐藏 N 次，但窗口数【没有减少】✓
        ///   所以必须每轮把窗口数喂进来 ✓
        /// </summary>
        public void UpdateWindowCounts(Dictionary<uint, int> counts)
        {
            foreach (var kv in _procHistory)
            {
                kv.Value.LastWindowCount = counts.TryGetValue(kv.Key, out int n) ? n : 0;
            }
        }

        /// <summary>
        /// ★ 温和处置：隐藏窗口 + 补一发关闭请求。
        ///
        /// ★★★ 注意：这里必须【同时更新行为档案】✗
        ///     否则会有这么一串连锁反应（2026-10-09 实测踩到）：
        ///       ActHide 提前 return → CloseAttempts 停在 0 ✗
        ///       → NoteSurvivors 里 `CloseAttempts > 0` 不成立 ✗
        ///       → CloseFailures 永远是 0 ✗
        ///       → reachedFirst 永远 false ✗
        ///       → ★ 弹窗永远不触发 ✗✗✗
        ///     表现就是：日志里 606 次"隐藏+关闭"，但一次弹窗都没有。
        /// </summary>
        private ActionLevel ActHide(WindowInfo w, int score, BehaviorRecord rec,
                                    ActionPolicy pol)
        {
            // ★ 记账：和主路径保持一致，否则升级门槛永远到不了
            _lastPoke[w.Hwnd] = DateTime.Now;
            rec.LastAttempt = DateTime.Now;
            rec.CloseAttempts++;

            // ★★★ 这里【直接】记一次失败 ✗
            //
            //  为什么要在这里记，而不是靠 NoteSurvivors：
            //    原来把 CloseFailures++ 放在 NoteSurvivors 里，
            //    但那一轮的 Prune 会用【有标题的可见窗口】判断进程存活 ✗
            //    而受保护窗口被我们【隐藏】了 → IsWindowVisible=false ✗
            //    → Prune 认为进程没了 → 删掉档案 ✗
            //    → 下一轮从 0 重新计数 ✗
            //    → CloseFailures 永远到不了门槛 ✗ → 弹窗永不触发 ✗✗✗
            //
            //  这里记是【语义正确】的：
            //    能走到 ActHide，就说明"该关它的窗口，但只能用温和手段"✗
            //    也就是"这一次没关掉"✓ 这本来就是一次失败 ✓
            rec.CloseFailures++;

            // ★★ 记录"弹窗之后又隐藏了多少次" —— 第二段弹窗的主判据靠它
            if (rec.ConsentAskedAt != DateTime.MinValue && !rec.SecondStageAsked)
            {
                rec.HidesSinceConsent++;
                // 该进程在此之后新建了多少窗口（用标题数增量估）
                int newTitles = rec.TitlesSeen.Count - rec.TitlesAtConsent;
                rec.NewWindowsSinceConsent = newTitles > 0 ? newTitles : 0;
            }

            if (rec.FirstAttempt == DateTime.MinValue)
            {
                rec.FirstAttempt = DateTime.Now;
            }

            // ★ A52A：必须用【异步】版！ShowWindow 会同步发 WM_SHOWWINDOW，
            //   目标线程不响应时会把主循环卡住（实测 5 秒起）✗
            Native.ShowWindowAsync(w.Hwnd, Native.SW_HIDE);
            Native.PostMessageW(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _log($"[L2 隐藏+关闭] 分数={score} {w.Proc} 「{Trunc(w.Title)}」"
                 + $"  ★受保护进程，不用强杀（累计尝试 {rec.CloseAttempts} 次）");
            return ActionLevel.Hide;
        }

        /// <summary>★ 强杀前的最终复核。四道闸全过才返回 true。</summary>
        private bool MayKillNow(WindowInfo w, int score, BehaviorRecord rec, bool dryRun)
        {
            var pol = Policy;
            var bw = Weights;

            // 闸⓪：用户是否开启了强杀
            if (!pol.AllowTerminate)
            {
                _log($"[拒绝强杀] 配置未开启 allow_terminate  {w.Proc}");
                return false;
            }

            // 闸①：分数门槛（比普通处置更高）
            if (score < bw.TerminateThreshold)
            {
                _log($"[拒绝强杀] 分数 {score} 未达强杀门槛 {bw.TerminateThreshold}  {w.Proc}");
                return false;
            }

            // 闸②：失败次数
            if (rec.CloseFailures < pol.FailuresBeforeKill)
            {
                _log($"[拒绝强杀] 仅失败 {rec.CloseFailures} 次，需 {pol.FailuresBeforeKill} 次  {w.Proc}");
                return false;
            }

            // 闸③：★ 用户白名单
            if (IsWhitelisted(w.Proc))
            {
                _log($"[拒绝强杀] 在白名单里  {w.Proc}");
                return false;
            }

            // 闸④：★ 安全闸全套
            //      ⓠ 沙箱约束（只能杀自己生成的进程）
            //      ① 硬黑名单
            //      ② 系统目录路径
            //      ③ ★★ 数字签名（有合法签名 → 禁止强杀；
            //          但若用户通过授权弹窗明确同意，且签名有效，则放行 ✓）
            bool consented = ConsentDialog.IsGranted(w.Proc);
            if (!Safety.MayTerminate(w.Proc, w.ImagePath, w.Pid, consented,
                                     out string reason))
            {
                _log($"[★拒绝强杀] {w.Proc}(pid={w.Pid}) —— {reason}");
                return false;
            }

            // 闸⑤：目标进程不能有"看起来正常"的窗口
            //       否则可能误杀一个既有广告窗又有正经工作窗的程序
            if (w.SiblingLooksReal)
            {
                _log($"[拒绝强杀] {w.Proc} 还有正常窗口，只关这一个，不杀进程");
                return false;
            }

            return true;
        }

        /// <summary>★★ 执行强杀。调用点只在 MayKillNow 返回 true 之后。</summary>
        private void DoTerminate(WindowInfo w, int score, BehaviorRecord rec)
        {
            IntPtr h = Native.OpenProcess(
                Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED_INFORMATION,
                false, w.Pid);
            if (h == IntPtr.Zero)
            {
                _log($"[强杀失败] 打不开进程 {w.Proc} (pid={w.Pid})，可能权限不足");
                return;
            }

            try
            {
                double ms = rec.FirstAttempt == DateTime.MinValue
                    ? 0 : (DateTime.Now - rec.FirstAttempt).TotalMilliseconds;
                bool ok = Native.TerminateProcess(h, 1);
                rec.Terminated = true;
                // ★★ 记进"已强杀"名单 —— 用于以后判断"杀了又复活"✗
                if (ok)
                {
                    _terminatedProcs.Add(w.Proc);
                    // ★★★ 记下强杀时刻 —— 用于检测"被守护进程重启"✗
                    rec.LastTerminatedTick = Environment.TickCount64;
                    _killTickByName[w.Proc] = Environment.TickCount64;
                }
                _log($"[L3 强杀] 分数={score} {w.Proc} (pid={w.Pid}) "
                     + $"「{Trunc(w.Title)}」 耗时={ms:F0}ms  结果={(ok ? "成功" : "失败")}");
                if (!ok)
                {
                    _log($"[强杀失败] GetLastError={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                }
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        private bool IsWhitelisted(string proc)
        {
            var wl = _cfg.ProcessWhitelist;
            if (wl == null || wl.Count == 0)
            {
                return false;
            }
            foreach (var x in wl)
            {
                if (!string.IsNullOrWhiteSpace(x)
                    && proc.Contains(x.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // ====================================================================
        //  档案维护
        // ====================================================================

        // ====================================================================
        //  ★★★ 应急选址（事故后重建）：找出"最该被压住"的进程 ✓
        //      判据：评分够高、被处置过（关失败或反复隐藏）、且不是正常界面 ✓
        //      ★ 取不到名字的进程【不动手】（安全底线）——
        //        但先退回记档时存下的名字，避免"隐藏之后就取不到"✗
        // ====================================================================
        public List<EmergencyTarget> EmergencyTargets(int minScore, int minFailures,
                                                     bool killSigned)
        {
            var list = new List<EmergencyTarget>();
            var hasReal = new Dictionary<uint, bool>();

            foreach (WindowInfo w in WindowInfo.Enumerate(includeHidden: true,
                                                          includeNoTitle: true))
            {
                bool real = Safety.LooksLikeRealWindow(w.ChildCount, w.HasEdit);
                if (hasReal.TryGetValue(w.Pid, out bool old))
                {
                    hasReal[w.Pid] = old && real;
                }
                else
                {
                    hasReal[w.Pid] = real;
                }
            }

            uint self = (uint)Environment.ProcessId;
            foreach (var kv in _procHistory)
            {
                uint pid = kv.Key;
                BehaviorRecord rec = kv.Value;

                if (pid == self) { continue; }
                if (rec.Terminated) { continue; }

                // ★ 关失败过 N 次【或】被我们处置过 N 次，都算证据 ✓
                //   （受保护进程只涨 CloseAttempts，不涨 CloseFailures ✗）
                if (rec.CloseFailures < minFailures && rec.CloseAttempts < minFailures)
                {
                    continue;
                }
                if (rec.LastScore < minScore) { continue; }

                bool realWin;
                if (hasReal.TryGetValue(pid, out realWin) && realWin) { continue; }

                string proc = WindowInfo.ProcName(pid);
                if (proc.Length == 0) { proc = rec.Proc; }   // ★ 退回档案里的名字 ✓
                if (proc.Length == 0) { continue; }          // 名字都没有 → 不动手 ✗
                if (proc.Contains("warden", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // ★ 不让杀的（签名 / 白名单）→ 只冻结，绝不硬杀（规矩 2）✓
                //   用 MayTerminate 的"带用户授权"重载来判断：
                //     授权了还是不让杀 = pinned（签名/白名单/系统进程…）→ 只冻 ✓
                //     kill_signed=true 时把签名进程也当"已授权"→ 可以杀 ✓
                bool pinned = !Safety.MayTerminate(proc, "", pid, killSigned, out string whyNot);

                list.Add(new EmergencyTarget
                {
                    Pid = pid,
                    Proc = proc,
                    Score = rec.LastScore,
                    Failures = rec.CloseFailures,
                    Pinned = pinned,
                });
            }

            list.Sort((a, b) => b.Score.CompareTo(a.Score));
            return list;
        }
        public BehaviorRecord Record(WindowInfo w)
        {
            if (!_procHistory.TryGetValue(w.Pid, out var rec))
            {
                rec = new BehaviorRecord();
                _procHistory[w.Pid] = rec;

                // ★★★ 这个 PID 是【第一次见到】✗
                //     如果同名进程刚被我们强杀过 ✗
                //     → 说明有东西把它重启了 → 判定"被守护"✓
                if (_killTickByName.TryGetValue(w.Proc, out long killTick))
                {
                    long delta = Environment.TickCount64 - killTick;
                    if (delta > 0 && delta <= 30000)
                    {
                        if (_guardedProcs.Add(w.Proc))
                        {
                            _log($"[★★守护检测] {w.Proc} 在被强杀后 {delta / 1000.0:F1} 秒又出现"
                                 + " → 判定为【有守护进程】✗");
                            _log($"[★★守护检测] 光杀它没用 ✗ 请用专业卸载工具"
                                 + $"（Geek / Revo）彻底清除 {w.Proc} ✓");
                        }
                    }
                }
            }
            // ★ A52A：记档时把进程名存下来（应急选址时现取会取到空 ✗）
            if (w.Proc.Length > 0) { rec.Proc = w.Proc; }

            if (rec.TitlesSeen.Add(w.Title))
            {
                rec.WindowsSeen++;
                // ★★★ 记录到滑动窗口（评分调优）
                rec.NoteNewWindow();
                rec.NoteNewTitle();
            }
            // ★ 类名也记（判断"换类名对抗"）
            if (rec.ClassesSeen.Add(w.Class))
            {
                rec.NoteNewTitle();
            }
            // ★ 历史集合加个上限，防内存无限增长
            const int MaxSeen = 5000;
            if (rec.TitlesSeen.Count > MaxSeen)
            {
                rec.TitlesSeen.Clear();
            }
            if (rec.ClassesSeen.Count > 200)
            {
                rec.ClassesSeen.Clear();
            }
            if (w.IsTopmost)
            {
                rec.EverTopmost = true;
            }
            return rec;
        }

        public void MarkForeground(uint pid)
        {
            if (_procHistory.TryGetValue(pid, out var rec))
            {
                rec.EverForeground = true;
            }
        }

        /// <summary>一轮扫完后调用：统计哪些窗口"该关但没关掉"。</summary>
        public void NoteSurvivors(HashSet<IntPtr> stillAlive)
        {
            // ★★ 注意：CloseFailures 现在由 ActHide 直接维护 ✗
            //    （原来在这里 ++，但 Prune 会因为"窗口被隐藏→看不到"而删档案，
            //      导致计数每轮归零，弹窗门槛永远到不了）
            //    这里只负责清理已经消失的窗口句柄记录 ✓

            var gone = new List<IntPtr>();
            foreach (var kv in _lastPoke)
            {
                if (!Native.IsWindow(kv.Key))
                {
                    gone.Add(kv.Key);
                }
            }
            foreach (var g in gone)
            {
                _lastPoke.Remove(g);
            }
        }

        /// <summary>★ 每轮清理：进程已退出的档案不要留。</summary>
        public void Prune(uint[] alivePids)
        {
            var alive = new HashSet<uint>(alivePids);
            var dead = new List<uint>();
            foreach (var kv in _procHistory)
            {
                if (!alive.Contains(kv.Key))
                {
                    dead.Add(kv.Key);
                }
            }
            foreach (var d in dead)
            {
                _procHistory.Remove(d);
            }
        }

        private static string Trunc(string s) => s.Length <= 48 ? s : s.Substring(0, 48);
    }
}
