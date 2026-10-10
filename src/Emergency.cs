// ============================================================================
//  A46 应急压制模式
// ============================================================================
//  【用户的要求（2026-10-10）】
//      "允许 WARDEN 在【不再向用户申请提权】的情况下，
//       用高频强杀把流氓软件压住，
//       然后弹一个全屏红色警告，告诉用户去用专业安全软件。"
//
//  【为什么必须有这个模式】
//      红队演练暴露的场景：
//        ① 流氓软件直接把我们【授权弹窗】关掉 → 用户根本来不及点"允许"✗
//        ② 流氓软件用管理员权限直接杀 WARDEN → 它还没死就要先挨打✗
//      这两种情况下还坚持"必须用户先同意才能动手"＝放弃抵抗 ✗
//      → 应急模式：已经挨打了 → 先高频压住 → 再把用户叫醒去用专业工具 ✓
//
//  【★ 它仍然守着的四条红线（"应急"两个字不能拿来当借口）】
//      ① 硬黑名单 / 系统目录进程 → 永远不碰（杀了会蓝屏）✗
//      ② 沙箱约束（DSH_SANDBOX_PIDS）→ 测试时只允许杀自己拉起的进程 ✓
//      ③ 有合法数字签名的 → 默认【冻结】而不是杀（用户 2026-10-09 规矩 2）
//         签名不是"免死金牌"（用户说过"也不能一概而论"）✓
//         所以冻结 = 让它彻底动不了，但不销毁进程与数据 ✓
//         真想连签名进程一起杀，得自己在配置里把 kill_signed 打开 ✓
//      ④ 只压制进程，绝不替用户卸载 / 删文件（"那不是我们的活"）✓
//
//  【★ 什么算"已经明确是流氓"（应急只打这种，不乱杀）】
//      ① 行为分 ≥ min_score（默认 60）
//      ② 我们关它失败过 ≥ min_failures 次（默认 2）→ 证明"关不掉"
//      ③ 它没有"看起来正常"的窗口（有输入框/多子控件）→ 不是用户正在干活的程序
//      ④ 还得过得了 Safety 的四道闸（黑名单/系统目录/白名单/沙箱）
//      → 不满足就不动手；宁可少杀，不可误杀 ✓
//
//  【★ 怎么退出（不会无限期压下去）】
//      · 敌情消失 calm_seconds 秒 → 自动退出 ✓
//      · 单次触发到期（seconds，敌情还在会自动续期）✓
//      · 硬上限 hard_cap_seconds 到 → 强制退出并留证据 ✓
//
//  【★ 留痕】
//      每次进入/每次压制/每次退出都写两处：
//        · 主日志（英文标记，界面翻译）
//        · %LOCALAPPDATA%\WARDEN\EMERGENCY.log（只追加，人可读证据）
//      —— 用户要求"每个程序都要记日志"，应急这种最重的动作更不能例外 ✓
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WARDEN
{
    /// <summary>★ 应急压制的一个目标（一个"已经明确是流氓"的进程）。</summary>
    internal sealed class EmergencyTarget
    {
        public uint Pid;
        public string Proc = string.Empty;
        public string ImagePath = string.Empty;
        public int Score;
        public int Failures;

        /// <summary>★ true = 安全闸不让杀（签名/白名单）→ 只能冻结。</summary>
        public bool Pinned;

        /// <summary>不让杀的原因（写日志用）。</summary>
        public string PinReason = string.Empty;

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Proc).Append("(pid=").Append(Pid).Append(')');
            if (ImagePath.Length > 0)
            {
                sb.Append(" [").Append(ImagePath).Append(']');
            }
            return sb.ToString();
        }
    }

    internal static class Emergency
    {
        private static readonly object Gate = new object();

        private static GuardConfig? _cfg;
        private static Action<string>? _log;
        private static string _evidencePath = string.Empty;

        /// <summary>
        /// ★ A47 的入口：进入应急时叫醒用户（全屏红色警告）。
        ///   A46 只负责"触发"，长什么样由 A47 决定 ✓
        /// </summary>
        public static Action<string>? AlertSink;

        // ---- 状态 ----
        private static bool _active;
        private static DateTime _deadline = DateTime.MinValue;
        private static DateTime _hardCap = DateTime.MinValue;
        private static DateTime _lastHostile = DateTime.MinValue;
        private static string _reason = string.Empty;
        private static int _rounds;
        private static int _killed;
        private static int _frozen;
        private static int _failed;

        /// <summary>★ 用户每按一次"加倍压制"就 +1（等级越高越狠，最多 5 级）</summary>
        private static int _intensity = 1;

        /// <summary>★ 用户按过"冻结全部"：之后每一轮都把所有目标冻住（只冻不杀）</summary>
        private static bool _freezeAllRequested;

        /// <summary>★ 导出证据要用到数据目录</summary>
        private static string _dataDir = string.Empty;

        /// <summary>
        /// ★ 本次应急的证据文件夹。
        ///   为什么缓存它：用户按每个按钮都会刷新导出 ✗
        ///   不缓存的话，桌面上会堆出一堆 WARDEN-证据-* 文件夹 ✗
        /// </summary>
        private static string _exportDir = string.Empty;

        /// <summary>★ 最近一次自动导出的提示语（红警界面要显示给用户看）</summary>
        private static string _exportNote = string.Empty;

        private static readonly Dictionary<uint, long> _handled = new();
        private static readonly Dictionary<string, long> _throttle = new();

        public static bool Active
        {
            get { lock (Gate) { return _active; } }
        }

        public static int KilledCount { get { lock (Gate) { return _killed; } } }
        public static int FrozenCount { get { lock (Gate) { return _frozen; } } }

        private static EmergencyPolicy Pol
        {
            get
            {
                GuardConfig? c = _cfg;
                if (c == null || c.Emergency == null)
                {
                    return new EmergencyPolicy();
                }
                return c.Emergency;
            }
        }

        public static void Init(string dataDir, GuardConfig cfg, Action<string> log)
        {
            _cfg = cfg;
            _log = log;
            _dataDir = dataDir;
            _evidencePath = Path.Combine(dataDir, "EMERGENCY.log");
        }

        /// <summary>★ 配置热重载时同步（主循环每 100ms 会重新读配置）。</summary>
        public static void Reconfigure(GuardConfig cfg)
        {
            _cfg = cfg;
        }

        /// <summary>★ 应急期间把主循环间隔缩短 = "高频"。</summary>
        public static int SweepMs(int normalMs)
        {
            if (!Active)
            {
                return normalMs;
            }
            int ms = EscalatedInterval();
            if (ms < 10)
            {
                ms = 10;                 // ★ 再快也没意义，CPU 会先受不了
            }
            if (ms > normalMs)
            {
                ms = normalMs;
            }
            return ms;
        }

        // ================================================================
        //  进入 / 续期
        // ================================================================
        /// <summary>
        /// ★★★ 触发应急模式（幂等：已经在应急里就续期）。
        ///   调用点：SelfProtect 发现被攻击时（关我们窗口 / 伪造停止文件 …）
        /// </summary>
        public static void Trigger(string kind)
        {
            GuardConfig? cfg = _cfg;
            if (cfg == null)
            {
                return;
            }

            EmergencyPolicy pol = Pol;
            if (!pol.Enabled)
            {
                // ★ 用户关掉了应急模式 → 我们只能记下来，不越权动手 ✓
                _log?.Invoke("[EMERGENCY] disabled by config -- tamper recorded only  kind=" + kind);
                Note("disabled", "kind=" + kind);
                return;
            }

            bool wasActive;
            DateTime now = DateTime.Now;
            lock (Gate)
            {
                wasActive = _active;
                _active = true;
                _deadline = now.AddSeconds(ClampSec(pol.Seconds, 10, 3600));
                if (!wasActive)
                {
                    _hardCap = now.AddSeconds(ClampSec(pol.HardCapSeconds, 30, 7200));
                    _reason = kind;
                    _rounds = 0;
                    _killed = 0;
                    _frozen = 0;
                    _failed = 0;
                    _intensity = 1;
                    _freezeAllRequested = false;
                    _exportDir = string.Empty;
                    _exportNote = string.Empty;
                    _handled.Clear();
                }
                _lastHostile = now;
            }

            Note(wasActive ? "extend" : "enter",
                "kind=" + kind + " seconds=" + pol.Seconds
                + " min_score=" + pol.MinScore + " min_failures=" + pol.MinFailures
                + " kill_signed=" + pol.KillSigned + " interval_ms=" + pol.IntervalMs);

            _log?.Invoke("[EMERGENCY] " + (wasActive ? "extended" : "ENTERED")
                + " reason=" + kind
                + " -- high-frequency suppression started (interval=" + pol.IntervalMs + "ms)");

            if (!wasActive)
            {
                // ★ 先把用户叫醒（A47 全屏红警）；叫不醒也不能耽误压制 ✓
                Action<string>? sink = AlertSink;
                if (sink != null)
                {
                    try
                    {
                        sink(kind);
                    }
                    catch (Exception e)
                    {
                        _log?.Invoke("[EMERGENCY] alert sink failed: " + e.Message);
                    }
                }
            }
        }

        // ================================================================
        //  每一圈：压制
        // ================================================================
        public static void Tick(Engine engine)
        {
            if (!Active)
            {
                return;
            }

            EmergencyPolicy pol = Pol;
            DateTime now = DateTime.Now;

            // ---- 退出条件（先判，免得压过头）----
            if (now > _hardCap)
            {
                Exit("hard-cap", true);
                return;
            }
            if (now > _deadline)
            {
                Exit("timeout", false);
                return;
            }

            List<EmergencyTarget> targets;
            try
            {
                // ★ 用户按过"加倍压制"之后门槛会放宽（见 Escalated* 系列）✓
                targets = engine.EmergencyTargets(EscalatedMinScore(),
                                                  EscalatedMinFailures(),
                                                  pol.KillSigned);
            }
            catch (Exception e)
            {
                _log?.Invoke("[EMERGENCY] target scan failed: " + e.Message);
                return;
            }

            if (targets.Count == 0)
            {
                // 表面平静 —— 但要等够 calm_seconds 才算真的平静 ✓
                if ((now - _lastHostile).TotalSeconds >= pol.CalmSeconds)
                {
                    Exit("calm", false);
                }
                return;
            }

            // ---- 敌情还在 → 续期（但绝不突破硬上限）----
            _lastHostile = now;
            DateTime next = now.AddSeconds(ClampSec(pol.Seconds, 10, 3600));
            if (next < _hardCap)
            {
                _deadline = next;
            }
            else
            {
                _deadline = _hardCap;
            }

            int max = EscalatedMaxPerTick(pol);
            int done = 0;
            foreach (EmergencyTarget t in targets)
            {
                if (done >= max)
                {
                    break;
                }
                if (Cooling(t.Pid))
                {
                    continue;
                }
                done++;

                // ★ 用户按过"冻结全部" → 一律只冻不杀：
                //   ① 用户按它主要为了【拖时间】② 冻结不销毁证据 ✓
                //   ③ 有签名的进程本来就不许硬杀，冻结正好合规 ✓
                if (FreezeAllActive())
                {
                    if (!Freeze(t))
                    {
                        // 冻不住（权限不足）再退回常规压制，绝不静默放过 ✓
                        Suppress(t);
                    }
                }
                else
                {
                    Suppress(t);
                }
            }

            lock (Gate)
            {
                _rounds++;
            }
        }

        /// <summary>
        /// ★ 压制一个目标：能杀就杀，不能杀（签名/白名单/权限不足）就冻结。
        /// </summary>
        private static void Suppress(EmergencyTarget t)
        {
            Mark(t.Pid);

            if (!t.Pinned && Kill(t))
            {
                return;
            }

            if (Freeze(t))
            {
                return;
            }

            lock (Gate)
            {
                _failed++;
            }
            Note("fail", t.Describe() + " cannot kill nor freeze");
            _log?.Invoke("[EMERGENCY-FAIL] " + t.Describe()
                + " -- 杀不掉也冻不住（权限不足？）");
        }

        /// <summary>★ 强杀一个应急目标（安全闸已在 Engine.EmergencyTargets 里过完）。</summary>
        private static bool Kill(EmergencyTarget t)
        {
            IntPtr h = Native.OpenProcess(
                Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED_INFORMATION,
                false, t.Pid);
            if (h == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                bool ok = Native.TerminateProcess(h, 1);
                if (!ok)
                {
                    return false;
                }
                lock (Gate)
                {
                    _killed++;
                }
                Note("kill", t.Describe() + " score=" + t.Score + " fails=" + t.Failures);
                _log?.Invoke("[EMERGENCY-KILL] score=" + t.Score
                    + " fails=" + t.Failures + " " + t.Describe());
                return true;
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        /// <summary>
        /// ★★★ 冻结（挂起所有线程）—— 对【有合法签名】或【白名单】的进程用它。
        ///   规矩 2 说的是"不硬杀"，不是"不管" ✓
        /// </summary>
        private static bool Freeze(EmergencyTarget t)
        {
            // ★★ 冻结同样要守"沙箱测试只能动自己拉起的进程"这条规矩 ✗
            //    （A52A 实测发现：强杀那条路有 Safety 把关，冻结这条漏了）
            if (Safety.InSandboxMode && !Safety.SandboxAllows(t.Pid))
            {
                Note("freeze-denied", t.Describe() + " not in sandbox pid list");
                _log?.Invoke("[EMERGENCY-DENIED] freeze refused: " + t.Describe()
                    + " (sandbox: only test-spawned PIDs are touchable)");
                return false;
            }

            IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, t.Pid);
            if (h == IntPtr.Zero)
            {
                return false;
            }

            bool ok;
            try
            {
                ok = Native.NtSuspendProcess(h) == 0;
            }
            finally
            {
                Native.CloseHandle(h);
            }

            // 冻住了也把它窗口藏起来 —— 否则它还糊在屏幕上，用户以为没生效 ✗
            HideWindowsOf(t.Pid);

            if (ok)
            {
                lock (Gate)
                {
                    _frozen++;
                }
                string why = t.PinReason.Length > 0 ? t.PinReason : "kill-unavailable";
                Note("freeze", t.Describe() + " reason=" + why);
                _log?.Invoke("[EMERGENCY-FREEZE] " + t.Describe()
                    + " -- 已冻结（不做硬杀） 原因=" + why);
            }
            return ok;
        }

        private static void HideWindowsOf(uint pid)
        {
            try
            {
                List<WindowInfo> ws = WindowInfo.Enumerate(
                    includeNoTitle: true, includeHidden: true);
                foreach (WindowInfo w in ws)
                {
                    if (w.Pid != pid)
                    {
                        continue;
                    }
                    // ★ A52A：必须用【异步】版！ShowWindow 会同步发 WM_SHOWWINDOW，
            //   目标线程不响应时会把主循环卡住（实测 5 秒起）✗
            Native.ShowWindowAsync(w.Hwnd, Native.SW_HIDE);
                    Native.PostMessageW(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch
            {
                // 藏窗口失败不影响冻结本身 ✓
            }
        }

        // ================================================================
        //  退出
        // ================================================================
        private static void Exit(string why, bool forced)
        {
            int r, k, f, bad;
            string reason;
            lock (Gate)
            {
                if (!_active)
                {
                    return;
                }
                r = _rounds;
                k = _killed;
                f = _frozen;
                bad = _failed;
                reason = _reason;
                _active = false;
                _deadline = DateTime.MinValue;
            }

            string summary = "exit why=" + why + " reason=" + reason
                + " rounds=" + r + " killed=" + k + " frozen=" + f
                + " failed=" + bad + " forced=" + forced;
            Note(why, summary);
            _log?.Invoke("[EMERGENCY] EXIT " + summary);

            if (k + f > 0)
            {
                _log?.Invoke("[EMERGENCY] this round: killed=" + k + " frozen=" + f
                    + " -- suppression is NOT removal;"
                    + " use professional security software to clean it up");
            }
        }

        // ================================================================
        //  ★ 用户在红警窗口上按出来的"更强硬选项"（A47 第二轮）
        //     全部只用【用户态 + 系统本来就允许的权限】：
        //     不装驱动、不碰内核、不挂钩子 ✓
        // ================================================================
        /// <summary>
        /// ★ 加倍压制：间隔减半、每轮多压、门槛放宽。每按一次升一级（最多 5 级）。
        ///   只改强度，四条红线一条都不动 ✓
        /// </summary>
        public static string Escalate()
        {
            int lvl;
            lock (Gate)
            {
                _intensity = Math.Min(5, _intensity + 1);
                lvl = _intensity;
            }
            int ms = EscalatedInterval();
            int score = EscalatedMinScore();
            int per = EscalatedMaxPerTick(Pol);

            Note("escalate", "intensity=" + lvl + " interval_ms=" + ms
                + " min_score=" + score + " min_failures=" + EscalatedMinFailures()
                + " max_per_tick=" + per);
            _log?.Invoke("[EMERGENCY] user escalated intensity to level " + lvl
                + " interval=" + ms + "ms min_score=" + score
                + " max_per_tick=" + per);
            return "压制等级 " + lvl + "：间隔 " + ms + "ms，门槛 " + score
                + " 分，每轮最多 " + per + " 个";
        }

        /// <summary>★ 延长压制（拖时间用）：单次期限与硬上限一起往后推。</summary>
        public static string Extend(int seconds)
        {
            if (seconds < 30)
            {
                seconds = 30;
            }
            if (seconds > 1800)
            {
                seconds = 1800;
            }

            DateTime until;
            lock (Gate)
            {
                DateTime now = DateTime.Now;
                _hardCap = _hardCap == DateTime.MinValue
                    ? now.AddSeconds(seconds) : _hardCap.AddSeconds(seconds);
                _deadline = _deadline == DateTime.MinValue
                    ? now.AddSeconds(seconds) : _deadline.AddSeconds(seconds);
                until = _hardCap;
            }

            Note("extend", "seconds=" + seconds + " hard_cap=" + until.ToString("HH:mm:ss"));
            _log?.Invoke("[EMERGENCY] user extended suppression by " + seconds
                + "s (hard cap -> " + until.ToString("HH:mm:ss") + ")");
            return "已延长 " + (seconds / 60) + " 分钟，最晚压到 "
                + until.ToString("HH:mm:ss");
        }

        /// <summary>
        /// ★ 冻结全部可疑进程（持续生效）—— 只冻不杀。
        ///   为什么不是"全部强杀"：
        ///     ① 用户按它的目的是【拖时间】，冻住就够 ✓
        ///     ② 冻结不销毁证据，后面专业工具还能查 ✓
        ///     ③ 有签名的进程按规矩不许硬杀，冻结正好合规 ✓
        /// </summary>
        public static string RequestFreezeAll()
        {
            lock (Gate)
            {
                _freezeAllRequested = true;
            }
            Note("freeze-all", "requested by user");
            _log?.Invoke("[EMERGENCY] user requested FREEZE-ALL"
                + " (suspend only, never kill -- evidence preserved)");
            return "已开启冻结全部：每轮把所有可疑进程冻住（只冻不杀）";
        }

        /// <summary>
        /// ★ 把证据导出到桌面 —— 用户要拿给专业工具/人看。
        ///   只复制文件，不改系统、不碰内核 ✓
        /// </summary>
        public static string ExportEvidence()
        {
            try
            {
                string desk = Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory);
                string dst;
                lock (Gate)
                {
                    if (_exportDir.Length == 0)
                    {
                        _exportDir = Path.Combine(desk,
                            "WARDEN-证据-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    }
                    dst = _exportDir;
                }
                Directory.CreateDirectory(dst);

                string[] names = { "WARDEN.log", "EMERGENCY.log", "TAMPER.log", "WARDEN.json" };
                int n = 0;
                foreach (string nm in names)
                {
                    string s = Path.Combine(_dataDir, nm);
                    if (File.Exists(s))
                    {
                        File.Copy(s, Path.Combine(dst, nm), true);
                        n++;
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("这些是 WARDEN 在攻击期间留下的证据。");
                sb.AppendLine("交给专业工具或专业人员时，把这个文件夹整个发过去即可：");
                sb.AppendLine("  · WARDEN.log     主日志（它做了什么、拦了什么）");
                sb.AppendLine("  · EMERGENCY.log  应急压制记录（压了谁、什么时候）");
                sb.AppendLine("  · TAMPER.log     被攻击的证据（谁试图关掉 WARDEN）");
                sb.AppendLine("  · WARDEN.json    当时的配置");
                sb.AppendLine();
                sb.AppendLine("★ 里面不含你的密码，也不含私钥。");
                sb.AppendLine("★ 建议顺序：先用 Geek Uninstaller 卸载，"
                              + "再用 Windows 安全中心 / 卡巴斯基扫描。");
                File.WriteAllText(Path.Combine(dst, "说明-先读我.txt"),
                                  sb.ToString(), new UTF8Encoding(false));

                Note("export", "dir=" + dst + " files=" + n);
                _log?.Invoke("[EMERGENCY] evidence exported to " + dst
                    + " (" + n + " files)");
                return "证据已复制到桌面：" + Path.GetFileName(dst);
            }
            catch (Exception e)
            {
                _log?.Invoke("[EMERGENCY] export failed: " + e.Message);
                return "导出失败：" + e.Message;
            }
        }

        /// <summary>★ 当前压制等级（1 = 常规）。</summary>
        public static int Intensity
        {
            get { lock (Gate) { return _intensity; } }
        }

        private static bool FreezeAllActive()
        {
            lock (Gate) { return _freezeAllRequested; }
        }

        // ---- 等级换算：越狠 = 间隔越短、门槛越低、每轮越多 ----
        private static int EscalatedInterval()
        {
            int ms = Pol.IntervalMs / Intensity;
            if (ms < 10)
            {
                ms = 10;
            }
            return ms;
        }

        private static int EscalatedMinScore()
        {
            int s = Pol.MinScore - (Intensity - 1) * 10;
            if (s < 20)
            {
                s = 20;
            }
            return s;
        }

        private static int EscalatedMinFailures()
        {
            int f = Pol.MinFailures - (Intensity - 1);
            if (f < 1)
            {
                f = 1;
            }
            return f;
        }

        private static int EscalatedMaxPerTick(EmergencyPolicy pol)
        {
            int m = pol.MaxActionsPerTick * Intensity;
            if (m < 1)
            {
                m = 1;
            }
            if (m > 30)
            {
                m = 30;
            }
            return m;
        }

        /// <summary>
        /// ★★★ 默认动作：自动把证据导出到桌面（用户 2026-10-10 的要求）。
        ///
        ///   【为什么默认就要导】
        ///     应急模式下我们随时可能被管理员权限强行杀掉 ✗
        ///     到那时候再想导出就来不及了 →
        ///     【一进应急就先落盘】✓ 用户手上永远有一份现场记录 ✓
        ///
        ///   【为什么必须显示给用户】
        ///     默认把数据写到桌面 = 用户必须知道 ✗
        ///     → 返回一句提示语，红警的状态行会显示它 ✓
        ///
        ///   ★ 调用点要放后台线程（磁盘 IO 别卡住界面）✓
        /// </summary>
        public static string AutoExport()
        {
            string msg = ExportEvidence();
            lock (Gate)
            {
                _exportNote = msg;
            }
            _log?.Invoke("[EMERGENCY] auto export: " + msg);
            return msg;
        }

        /// <summary>★ 最近一次自动导出的提示语（界面用）。</summary>
        public static string ExportNote
        {
            get { lock (Gate) { return _exportNote; } }
        }

        // ================================================================
        //  小工具
        // ================================================================
        private static int ClampSec(int v, int lo, int hi)
        {
            if (v < lo)
            {
                return lo;
            }
            if (v > hi)
            {
                return hi;
            }
            return v;
        }

        /// <summary>★ 同一个 pid 压制过之后，2 秒内不重复动手（它可能正在死）。</summary>
        private static bool Cooling(uint pid)
        {
            lock (Gate)
            {
                if (_handled.TryGetValue(pid, out long tick))
                {
                    return Environment.TickCount64 - tick < 2000;
                }
                return false;
            }
        }

        private static void Mark(uint pid)
        {
            lock (Gate)
            {
                _handled[pid] = Environment.TickCount64;
            }
        }

        /// <summary>
        /// ★ 写应急证据文件（只追加、人可读）。
        ///   失败【绝不能】影响压制 —— 所以吞异常但记一次？
        ///   不行 ✗ 记不了就是记不了，绝不能因为日志把防御打断 ✓
        /// </summary>
        private static void Note(string kind, string text)
        {
            string? line = null;
            try
            {
                // ★ 同一个 pid 的同一类动作 1 秒内只写一行（防刷屏）
                string key = kind + "|" + text;
                lock (Gate)
                {
                    if (_throttle.TryGetValue(key, out long last)
                        && Environment.TickCount64 - last < 1000)
                    {
                        return;
                    }
                    _throttle[key] = Environment.TickCount64;
                }

                line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                     + "  [" + kind + "] " + text;
                string dir = Path.GetDirectoryName(_evidencePath) ?? ".";
                Directory.CreateDirectory(dir);
                File.AppendAllText(_evidencePath, line + Environment.NewLine, new UTF8Encoding(false));   // ★ 无 BOM：用户要求日志干净 ✓
            }
            catch
            {
                // ★ 见上面的说明：日志失败不打断防御
                _ = line;
            }
        }
    }
}
