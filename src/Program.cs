// ============================================================================
//  主程序 —— 事件驱动的窗口行为拦截
// ============================================================================
//  【架构】
//      线程 A：SetWinEventHook 消息循环
//              窗口创建/显示/获焦时被唤醒，把句柄丢进待处理队列
//      线程 B：主循环（100ms 一圈）
//              采集窗口 → 评分 → 按分级处置 → 统计
//
//  【★ 为什么要事件 + 轮询两条腿】
//      纯轮询（旧版 2 秒一次）有两个毛病：
//        · 弹窗已经抢完焦点、闪完任务栏，你才看到它 ✗
//        · 关掉之后要等下一个周期才知道有没有关掉 ✗
//      纯事件也不够：事件只告诉你"有变化"，不知道"现在长什么样"，
//      而且有些窗口是改属性而非新建，不发事件 ✗
//      → 事件负责【及时性】，轮询负责【状态确认】✓✓✓
//
//  【★ 时限管理】
//      主循环 100ms 一圈，处置阶梯靠时间戳推进，
//      全程不 sleep 等待 → 关不掉的窗口能在几百毫秒内升级到强杀 ✓
// ============================================================================

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace WARDEN
{
    internal static class Program
    {
        private const int SweepIntervalMs = 100;

        // ★ B3 便携版：有 WARDEN.portable 标记就用 <exe目录>\data ✓
        private static readonly string ToolDir =
            WardenPaths.ResolveDataDir((Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? ".") + @"\WARDEN");
        private static readonly string ConfigPath = ToolDir + @"\WARDEN.json";
        private static readonly string LogPath = ToolDir + @"\WARDEN.log";
        private static readonly string StopPath = ToolDir + @"\WARDEN.stop";
        private static readonly string LockPath = ToolDir + @"\WARDEN.lock";

        /// <summary>
        /// ★★ 旧版数据目录（开发期用的名字）。
        ///
        /// 为什么保留它：早先版本把数据放在 %LOCALAPPDATA%\dsh-tools\ ✗
        ///   那个名字是开发环境的缩写，开源后容易让人困惑 ✗
        ///   → 改成 %LOCALAPPDATA%\WARDEN\ ✓
        ///   但老用户已有的配置不能丢 ✗
        ///   → 启动时如果新目录没配置、旧目录有，就自动搬过来 ✓
        /// </summary>
        private static readonly string LegacyDir =
            (Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? ".") + @"\dsh-tools";

        /// <summary>
        /// ★ 把旧目录里的配置搬过来（只搬一次，不覆盖已有文件）。
        /// </summary>
        private static void MigrateLegacyConfig(Action<string> log)
        {
            try
            {
                if (!Directory.Exists(LegacyDir))
                {
                    return;
                }
                Directory.CreateDirectory(ToolDir);
                foreach (var name in new[] { "WARDEN.json", "WARDEN.log" })
                {
                    string src = Path.Combine(LegacyDir, name);
                    string dst = Path.Combine(ToolDir, name);
                    if (File.Exists(src) && !File.Exists(dst))
                    {
                        File.Copy(src, dst);
                        log($"[迁移] 已从旧目录搬来 {name}");
                    }
                }
            }
            catch
            {
                // ★ 迁移失败不影响运行 ✗ 也不该报错打断用户 ✓
            }
        }

        private static readonly object _logLock = new();

        /// <summary>★ 隐藏消息窗口（托盘消息的落点）。</summary>
        private static MessageWindow? _msgWnd;

        /// <summary>★ 托盘图标。</summary>
        private static TrayIcon? _tray;

        /// <summary>
        /// ★★ 处理消息窗口收到的消息（主要是托盘鼠标事件）。
        /// </summary>
        private static bool OnWindowMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_tray != null && _tray.HandleMessage(msg, wParam, lParam))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// ★ 处理托盘菜单里的请求（暂停 / 重载 / 退出 / 打开设置 / 看日志）。
        ///
        /// 为什么放在主循环里做，而不是在消息处理里直接做：
        ///   消息处理是在窗口过程里 ✗
        ///   窗口过程里做重活（读配置、启动进程）会卡住消息循环 ✗
        ///   → 只置标志位 ✓ 由主循环统一处理 ✓
        /// </summary>
        /// <returns>true 表示要退出</returns>
        private static bool HandleTrayRequests()
        {
            if (_tray == null)
            {
                return false;
            }

            if (_tray.ExitRequested)
            {
                Log("[托盘] 用户点了退出");
                return true;
            }

            if (_tray.ReloadRequested)
            {
                _tray.ReloadRequested = false;
                Log("[托盘] 重新加载配置（下一轮生效）");
            }

            if (_tray.OpenSettingsRequested)
            {
                _tray.OpenSettingsRequested = false;
                OpenSettings();
            }

            if (_tray.ViewLogRequested)
            {
                _tray.ViewLogRequested = false;
                // ★★★ 用户 2026-10-10 反馈：
                //   托盘「查看日志」应当【拉起会分析日志的 exe】，
                //   而不是拿资源管理器打开目录 ✗
                //   项目里已经有 WARDEN-LogView.exe（日志查看/分析器）✓
                try
                {
                    string logView = Path.Combine(AppContext.BaseDirectory,
                                                   "WARDEN-LogView.exe");
                    string logFile = Path.Combine(ToolDir, "WARDEN.log");
                    if (File.Exists(logView))
                    {
                        var psi = new ProcessStartInfo(logView);
                        psi.UseShellExecute = false;
                        psi.ArgumentList.Add(File.Exists(logFile) ? logFile : ToolDir);
                        Process.Start(psi);
                        Log("[TRAY] 已拉起日志查看器 WARDEN-LogView.exe ✓");
                    }
                    else
                    {
                        Log("★ 找不到 WARDEN-LogView.exe，退回打开日志目录"
                            + "（兜底，不是设计意图）");
                        Process.Start("explorer.exe", ToolDir);
                    }
                }
                catch (Exception e)
                {
                    Log("[TRAY] 打开日志失败: " + e.Message);
                }
            }

            return false;
        }

        /// <summary>
        /// ★★ 找到设置界面 WARDEN-Settings.exe 的路径。
        ///
        /// 【为什么需要按顺序找多个地方】（用户 2026-10-10 反馈）
        ///     用户点了托盘菜单的"打开设置界面"，结果【弹出了文件资源管理器】✗
        ///
        ///     原因：编译产物在两个不同的目录 ✗
        ///       src\bin\...\publish\WARDEN.exe
        ///       tools\settings\bin\...\WARDEN-Settings.exe
        ///     主程序按"同目录"找 ✗ 自然找不到 ✗
        ///     找不到就退而打开目录 ✗ 用户看到的就是资源管理器 ✗
        ///
        ///     修法有两条（都做了）：
        ///       ① build.ps1 打包时把两个 exe 放进同一个 dist\ ✓
        ///       ② ★ 这里按顺序多找几个地方 ✗ 兼容开发环境直接运行的情况 ✓
        ///
        /// 搜索顺序：
        ///   1. 环境变量 WARDEN_SETTINGS 指定的路径（最高优先级，便于自定义）
        ///   2. 程序同目录（★ 打包发布后的正常情况）
        ///   3. 程序目录下的 tools\settings\（开发时的布局）
        ///   4. 从程序目录往上找 tools\settings\（bin 深层目录的情况）
        /// </summary>
        private static string? FindSettingsExe()
        {
            var candidates = new List<string>();

            // 1. 环境变量
            string? env = Environment.GetEnvironmentVariable("WARDEN_SETTINGS");
            if (!string.IsNullOrWhiteSpace(env))
            {
                candidates.Add(env);
            }

            string baseDir = AppContext.BaseDirectory;

            // 2. 同目录（★ 最理想的情况）
            candidates.Add(Path.Combine(baseDir, "WARDEN-Settings.exe"));

            // 3. 程序目录下的 tools\settings\
            candidates.Add(Path.Combine(baseDir, "tools", "settings", "WARDEN-Settings.exe"));

            // 4. 从程序目录逐级往上找 tools\settings\bin\Release\...
            try
            {
                var dir = new DirectoryInfo(baseDir);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    string probe = Path.Combine(dir.FullName, "tools", "settings");
                    if (Directory.Exists(probe))
                    {
                        // 在它的 bin 下递归找
                        try
                        {
                            foreach (var f in Directory.GetFiles(probe, "WARDEN-Settings.exe",
                                                                 SearchOption.AllDirectories))
                            {
                                candidates.Add(f);
                            }
                        }
                        catch { }
                    }
                    dir = dir.Parent;
                }
            }
            catch { }

            foreach (var c in candidates)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(c) && File.Exists(c))
                    {
                        return c;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>★ 打开设置界面。</summary>
        private static void OpenSettings()
        {
            try
            {
                string? exe = FindSettingsExe();
                if (exe == null)
                {
                    // ★ 实在找不到 → 明确告诉用户，而不是默默打开资源管理器 ✗
                    //   （用户反馈过：点了"设置"却弹出文件管理器，很困惑 ✗）
                    Log("★★ 找不到 WARDEN-Settings.exe");
                    Log("    已尝试的位置：");
                    Log("      · " + Path.Combine(AppContext.BaseDirectory, "WARDEN-Settings.exe"));
                    Log("      · 程序目录下的 tools\\settings\\");
                    Log("      · 环境变量 WARDEN_SETTINGS 指定的路径");
                    Log("    提示：运行 build.ps1 会打包出 dist\\ 目录，");
                    Log("          里面两个 exe 放在一起，那时就能正常打开了 ✓");
                    MessageBoxW(IntPtr.Zero,
                        "找不到设置界面程序 WARDEN-Settings.exe。\n\n"
                        + "请把 WARDEN-Settings.exe 和 WARDEN.exe 放在同一个目录，\n"
                        + "或者运行 build.ps1 打包出 dist\\ 目录。\n\n"
                        + "（详细信息已写入日志）",
                        "WARDEN", 0x00000030 /* MB_ICONWARNING */);
                    // ★ 必须 return ✗ 否则会继续往下走，用 null 当路径启动进程 ✗
                    return;
                }

                System.Diagnostics.Process.Start(exe);
                Log("[托盘] 已启动设置界面: " + exe);
            }
            catch (Exception e)
            {
                Log("[托盘] 打开设置界面失败: " + e.Message);
            }
        }

        // ★ 用完整限定名 ✗ 这个文件里没有 using System.Runtime.InteropServices ✓
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        /// <summary>★ 打开日志所在文件夹。</summary>
        private static void OpenLogFolder()
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", ToolDir);
            }
            catch (Exception e)
            {
                Log("[托盘] 打开日志目录失败: " + e.Message);
            }
        }

        /// <summary>★ 运行期新增的永久授权（待写回配置）。</summary>
        private static readonly List<string> _pendingConsent = new();

        /// <summary>★ 运行期新增的"强杀过"记录（待写回配置，用于检测复活）。</summary>
        private static readonly List<string> _pendingTerminated = new();

        /// <summary>
        /// ★★★ 把运行期的授权 / 强杀历史写回配置文件。
        ///
        /// 为什么必须写回：
        ///     ① 用户点了"永久允许强杀" → 下次启动不用再问 ✓
        ///     ② ★ 强杀过的程序要记住 ✗
        ///        如果它之后又冒出窗口 → 说明"强杀一次"不够 ✗
        ///        → 下次弹窗的推荐要改成「永久允许强杀」✓
        ///        → 并引导用户用专业卸载工具清除 ✓
        /// </summary>
        private static void SaveRuntimeState()
        {
            try
            {
                var cfg = new GuardConfig();
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    cfg = JsonSerializer.Deserialize(json,
                              GuardJsonContext.Default.GuardConfig) ?? new GuardConfig();
                }

                cfg.ConsentedProcesses ??= new List<string>();
                foreach (var n in _pendingConsent)
                {
                    if (!cfg.ConsentedProcesses.Contains(n, StringComparer.OrdinalIgnoreCase))
                    {
                        cfg.ConsentedProcesses.Add(n);
                    }
                }

                cfg.TerminatedHistory ??= new List<string>();
                foreach (var n in _pendingTerminated)
                {
                    if (!cfg.TerminatedHistory.Contains(n, StringComparer.OrdinalIgnoreCase))
                    {
                        cfg.TerminatedHistory.Add(n);
                    }
                }

                string outp = JsonSerializer.Serialize(cfg,
                                  GuardJsonContext.Default.GuardConfig);
                File.WriteAllText(ConfigPath, outp, new UTF8Encoding(false));

                _pendingConsent.Clear();
                _pendingTerminated.Clear();
            }
            catch (Exception e)
            {
                Log("[错误] 写回配置失败: " + e.Message);
            }
        }

        // ------------------------------------------------------------ 日志
        // ★★★ A52A：主循环看门狗（事故后重新装回）
        //   【为什么必须有】卡住时必须留下证据 ✗
        //   "守护进程静默地死掉"是最坏的失败方式
        private static long _stageTick = Environment.TickCount64;
        private static string _stageTag = "启动";

        private static void Stage(string tag)
        {
            _stageTag = tag;
            System.Threading.Interlocked.Exchange(ref _stageTick, Environment.TickCount64);
        }

        private static void WatchdogThread()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(1000);
                long last = System.Threading.Interlocked.Read(ref _stageTick);
                long age = Environment.TickCount64 - last;
                if (age > 5000)
                {
                    Log("[看门狗] ★ 主扫描已 " + (age / 1000) + " 秒没有进展"
                        + "（停在：" + _stageTag + "）—— 疑似被某个调用卡住 ✗");
                    System.Threading.Interlocked.Exchange(ref _stageTick, Environment.TickCount64);
                }
            }
        }

        // ★★★ B11 本地崩溃收集（不联网、不上传 ✓）
        //   守护进程崩了却什么都不留是最糟的 ✗
        //   这里把异常 + 环境 + 日志尾部写成一个 CRASH-<时间>.log ✓
        public static void WriteCrashReport(string where, Exception ex)
        {
            try
            {
                string path = Path.Combine(ToolDir,
                    "CRASH-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("WARDEN 崩溃报告（本地生成，未上传任何东西 ✓）");
                sb.AppendLine("时间     : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("位置     : " + where);
                sb.AppendLine("版本     : " + Version);
                sb.AppendLine("系统     : " + Environment.OSVersion.VersionString);
                sb.AppendLine("CLR      : " + Environment.Version);
                sb.AppendLine("64 位    : " + Environment.Is64BitProcess);
                sb.AppendLine("程序路径 : " + Environment.ProcessPath);
                sb.AppendLine();
                sb.AppendLine("---- 异常 ----");
                sb.AppendLine(ex.ToString());
                sb.AppendLine();
                sb.AppendLine("---- 日志尾部（最近 80 行）----");
                try
                {
                    string log = Path.Combine(ToolDir, "WARDEN.log");
                    if (File.Exists(log))
                    {
                        string[] all = File.ReadAllLines(log);
                        int from = Math.Max(0, all.Length - 80);
                        for (int i = from; i < all.Length; i++) { sb.AppendLine(all[i]); }
                    }
                }
                catch { }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Log("★ 已写出崩溃报告: " + path);
            }
            catch
            {
                // 连崩溃报告都写不出来 → 只能认了（绝不因此再抛）✗
            }
        }

        private static void Log(string msg)
        {
            lock (_logLock)
            {
                try
                {
                    Directory.CreateDirectory(ToolDir);
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg
                        + Environment.NewLine, new UTF8Encoding(false));

                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        var lines = File.ReadAllLines(LogPath);
                        int keep = Math.Min(600, lines.Length);
                        var tail = new string[keep];
                        Array.Copy(lines, lines.Length - keep, tail, 0, keep);
                        File.WriteAllLines(LogPath, tail, new UTF8Encoding(false));
                    }
                }
                catch
                {
                }
            }
        }

        // ------------------------------------------------------------ 配置
        private static string DefaultConfig() => """
{
  "_说明": [
    "WARDEN 配置 —— 窗口行为拦截",
    "★ 规则只是【起点】，是否动手还要看综合行为分（behavior 段）。",
    "★ action.allow_terminate 默认 false：先跑观察期，确认真会误判再开。"
  ],
  "rules": [
    {
      "name": "示例：某聊天软件的推广弹窗",
      "process": "qq.exe",
      "title_contains": ["活动", "福利", "推荐", "资讯"],
      "enabled": false
    }
  ],
  "behavior": {
    "rule_hit": 40,
    "steals_focus": 12,
    "topmost": 10,
    "no_edit": 8,
    "no_children": 6,
    "respawn": 15,
    "small_floating": 8,
    "title_churn": 10,
    "apply_threshold": 55,
    "terminate_threshold": 75
  },
  "action": {
    "allow_terminate": false,
    "l1_wait_ms": 500,
    "l2_wait_ms": 1000,
    "failures_before_kill": 3,
    "deadline_ms": 4500
  },
  "protect": {
    "skip_if_foreground": true,
    "skip_if_has_edit": true,
    "skip_if_has_child_window": false,
    "dry_run": true
  },
  "process_whitelist": [
    "explorer.exe", "winlogon.exe", "csrss.exe", "wininit.exe",
    "services.exe", "lsass.exe", "smss.exe", "dwm.exe", "svchost.exe",
    "taskmgr.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
    "python.exe", "pythonw.exe", "notepad++.exe", "marktext.exe",
    "chrome.exe", "msedge.exe", "firefox.exe", "code.exe", "devenv.exe",
    "deepseek harness.exe", "liuyingpet.exe"
  ]
}
""";

        private static GuardConfig LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    Directory.CreateDirectory(ToolDir);
                    File.WriteAllText(ConfigPath, DefaultConfig(), new UTF8Encoding(false));
                    Log("已生成默认配置: " + ConfigPath);
                }
                string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                return JsonSerializer.Deserialize(json, GuardJsonContext.Default.GuardConfig)
                       ?? new GuardConfig();
            }
            catch (Exception e)
            {
                Log("读配置失败: " + e.Message);
                return new GuardConfig();
            }
        }

        // ------------------------------------------------------------ 单实例
        private static bool AlreadyRunning()
        {
            try
            {
                if (!File.Exists(LockPath))
                {
                    return false;
                }
                if (!int.TryParse(File.ReadAllText(LockPath).Trim(), out int oldPid))
                {
                    return false;
                }
                // ★★★ 不能只看"PID 还活着"✗
                //   实测踩到过：上一次被强行杀掉，锁文件留在那里，
                //   PID 后来被系统复用给了别的进程 →
                //   WARDEN 以为"已经在运行"，于是【静默退出】✗✗
                //   （现象：双击没反应、连日志都不写）
                //   → 必须核对：那个 PID 到底是不是 WARDEN.exe ✓
                bool isWarden = false;
                try
                {
                    using var op = System.Diagnostics.Process.GetProcessById(oldPid);
                    string name = op.ProcessName;
                    isWarden = name.Equals("WARDEN", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    isWarden = false;   // 进程不存在 / 拿不到 → 当成过期锁 ✓
                }

                if (!isWarden)
                {
                    // ★ 过期锁：回收它，正常启动 ✓（并且留一条日志，便于排查）
                    try
                    {
                        File.Delete(LockPath);
                        Log("[锁] 发现过期锁（pid=" + oldPid + " 不是 WARDEN）→ 已回收，正常启动 ✓");
                    }
                    catch { }
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------ 事件钩子
        private static readonly Native.WinEventProc _hookProc = OnWinEvent;
        private static readonly Queue<IntPtr> _pending = new();
        private static readonly object _pendingLock = new();
        private static int _eventCount;
        private static readonly HashSet<uint> _focusedPids = new();

        /// <summary>
        /// ★ 事件回调：只做最轻的事 —— 记下来，交给主循环处理。
        ///    绝不在回调里做枚举/评分/关闭动作，否则会拖慢系统的事件派发。
        /// </summary>
        private static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd,
                                       int idObject, int idChild,
                                       uint thread, uint time)
        {
            try
            {
                if (idObject != 0 || idChild != 0)
                {
                    return;   // 只关心窗口本身，不关心其子对象
                }
                Interlocked.Increment(ref _eventCount);
                lock (_pendingLock)
                {
                    if (_pending.Count < 256)
                    {
                        _pending.Enqueue(hwnd);
                    }
                }
                if (evt == Native.EVENT_SYSTEM_FOREGROUND && hwnd != IntPtr.Zero)
                {
                    Native.GetWindowThreadProcessId(hwnd, out uint pid);
                    lock (_focusedPids)
                    {
                        _focusedPids.Add(pid);
                    }
                }
            }
            catch
            {
            }
        }

        private static void EventThread()
        {
            var hooks = new List<IntPtr>();
            uint[] events =
            {
                Native.EVENT_OBJECT_CREATE,
                Native.EVENT_OBJECT_SHOW,
                Native.EVENT_OBJECT_HIDE,
                Native.EVENT_OBJECT_DESTROY,
                Native.EVENT_SYSTEM_FOREGROUND,
                Native.EVENT_OBJECT_LOCATIONCHANGE,
            };
            foreach (uint e in events)
            {
                IntPtr h = Native.SetWinEventHook(e, e, IntPtr.Zero, _hookProc, 0, 0,
                                                  Native.WINEVENT_OUTOFCONTEXT
                                                  | Native.WINEVENT_SKIPOWNPROCESS);
                if (h != IntPtr.Zero)
                {
                    hooks.Add(h);
                }
            }
            Log($"已注册 {hooks.Count}/{events.Length} 个窗口事件钩子");

            // 标准消息循环：WinEvent 回调靠它派发
            while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
            foreach (var h in hooks)
            {
                Native.UnhookWinEvent(h);
            }
        }

        /// <summary>取出并清空待处理队列。</summary>
        private static List<IntPtr> DrainPending()
        {
            var list = new List<IntPtr>();
            lock (_pendingLock)
            {
                while (_pending.Count > 0)
                {
                    list.Add(_pending.Dequeue());
                }
            }
            return list;
        }

        // ------------------------------------------------------------ 主循环
        /// <summary>程序版本号（与 csproj 里的保持一致）。</summary>
        private const string Version = "0.1.0-beta.1";

        // ====================================================================
        //  ★★★ 命令行输出：附着到父进程的控制台
        // ====================================================================
        //  【问题】
        //      本程序是 WinExe（窗口子系统）✗ 没有自己的控制台 ✗
        //      → Console.WriteLine 会写到无处，用户什么都看不到 ✗
        //
        //  【为什么不能改成 Exe】
        //      改成控制台子系统的话，直接双击 exe 会弹出一个黑框 ✗
        //      而这个工具是要【静默常驻】的 ✗ 绝对不能有黑框 ✗
        //
        //  【解决】
        //      用 AttachConsole(ATTACH_PARENT_PROCESS) ✓
        //      · 从 cmd / PowerShell 里运行时 → 附着到那个终端 ✓ 输出可见 ✓
        //      · 直接双击运行时 → 没有父控制台 → 什么都不做 ✓ 静默 ✓
        //      这是 Windows 上 GUI 程序支持命令行的标准做法 ✓
        // ====================================================================

        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        /// <summary>
        /// ★★★ 输出一行文本（尽量写到控制台，【同时】写进日志）。
        ///
        /// 【为什么要同时写日志】
        ///     本程序是 WinExe（窗口子系统）✗ 没有自己的控制台 ✗
        ///     实测在 Native AOT 下：
        ///       · Console.OpenStandardOutput() 拿到的是启动时缓存的无效句柄 ✗
        ///       · 打开 "CONOUT$" 在输出被重定向时也拿不到 ✗
        ///       · AttachConsole 在【输出已被重定向】时本身就会失败 ✗
        ///     → 命令行输出在某些调用方式下确实看不到 ✗
        ///
        ///     所以采取【双写】策略：
        ///       ① 尽量写控制台（从 cmd 直接运行时能看到 ✓）
        ///       ② 【一定】写日志文件 ✓
        ///          → 用户哪怕没看到屏幕输出，也能从日志里查到 ✓
        ///
        /// ★ 这是"宁可多记不可少记"原则在命令行场景的延伸 ✓
        /// </summary>
        private static void Emit(string line)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
                // ★ 没有控制台很正常 ✗ 不能因此崩 ✗
            }
            Log(line);
        }

        /// <summary>
        /// ★★★ 附着到父控制台，并尝试重新绑定 stdout。
        ///
        /// 【为什么必须重新绑定】
        ///     .NET 的 Console 在进程启动时就把标准输出句柄【缓存】下来了 ✗
        ///     而 WinExe 启动时那个句柄是无效的 ✗
        ///     光调 AttachConsole 只是把进程挂到父终端上 ✗
        ///     Console.WriteLine 依然往那个【失效的旧句柄】写 ✗
        ///     → 结果就是"命令执行了，但一个字都看不到"✗✗✗
        ///
        ///     所以必须用 Console.OpenStandardOutput() 重新拿一次句柄 ✓
        ///     这也是 Windows 上 GUI 程序支持命令行的标准做法 ✓
        ///
        /// 【失败是正常的】
        ///     直接双击 exe 时没有父控制台 → AttachConsole 返回 false ✓
        ///     此时什么都不做 → 保持静默 ✓（这是必须的，不能弹黑框 ✗）
        /// </summary>
        private static void SetupConsoleForOutput()
        {
            try
            {
                if (!AttachConsole(ATTACH_PARENT_PROCESS))
                {
                    return;   // ★ 没有父控制台 —— 直接双击运行的情况，保持静默 ✓
                }
            }
            catch
            {
                return;
            }

            // ★★★ 关键：不要用 Console.OpenStandardOutput() ✗
            //     实测在 Native AOT 下它拿到的仍是启动时那个【无效句柄】✗
            //     （句柄已经被缓存进 Console 内部了，AttachConsole 不会刷新它）
            //
            //     改用【直接打开 CONOUT$】✓ 这是最可靠的方式 ✓
            //     CONOUT$ 是当前控制台的输出设备名，附着成功后一定可写 ✓
            try
            {
                var fs = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write);
                var sw = new StreamWriter(fs) { AutoFlush = true };
                Console.SetOut(sw);

                var fs2 = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write);
                var sw2 = new StreamWriter(fs2) { AutoFlush = true };
                Console.SetError(sw2);
            }
            catch
            {
                // ★ 拿不到控制台不该让程序崩 ✗
            }
        }

        /// <summary>
        /// ★ 处理命令行参数。
        ///
        /// 支持的参数：
        ///   --version / -v   打印版本号后退出
        ///   --help / -h      打印用法后退出
        ///
        /// ★ 为什么需要它们：
        ///   一个命令行工具连"我是哪个版本"都问不出来，是很不体面的 ✗
        ///   而且报 bug 时用户第一句就该说清版本 ✓
        /// </summary>
        /// <returns>true 表示已处理完（应直接退出）</returns>
        private static bool HandleArgs(string[] args)
        {
            if (args.Length == 0)
            {
                return false;
            }

            // ★★ 先附着控制台并重绑 stdout ✗
            //    只调 AttachConsole 不够 —— .NET 缓存了旧句柄 ✗
            //    不重绑的话，命令执行了但一个字都看不到 ✗
            SetupConsoleForOutput();

            foreach (var raw in args)
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "--version":
                    case "-v":
                        Emit("WARDEN " + Version);
                        Emit("Native AOT 构建，无外部运行时依赖");
                        Emit("协议: AGPL-3.0-or-later");
                        return true;

                    case "--help":
                    case "-h":
                    case "/?":
                        Emit("WARDEN " + Version);
                        Console.WriteLine();
                        Emit("用法: WARDEN.exe [选项]");
                        Console.WriteLine();
                        Console.WriteLine("不带参数 = 后台常驻运行（默认行为）");
                        Console.WriteLine();
                        Console.WriteLine("选项:");
                        Console.WriteLine("  -h, --help      显示本帮助");
                        Console.WriteLine("  -v, --version   显示版本号");
                        Console.WriteLine();
                        Console.WriteLine("配置文件:");
                        Console.WriteLine("  " + ConfigPath);
                        Console.WriteLine();
                        Console.WriteLine("日志文件:");
                        Console.WriteLine("  " + LogPath);
                        Console.WriteLine();
                        Console.WriteLine("数据目录:");
                        Console.WriteLine("  " + ToolDir);
                        Console.WriteLine();
                        Console.WriteLine("★ 建议用设置界面 WARDEN-Settings.exe 改配置，");
                        Console.WriteLine("  比手写 JSON 安全得多。");
                        return true;

                    default:
                        Console.WriteLine("未知参数: " + raw);
                        Console.WriteLine("用 --help 查看可用选项。");
                        return true;
                }
            }
            return false;
        }

        private static void Main(string[] args)
        {
            // ★★ 命令行参数要在【建目录、读配置】之前处理 ✗
            //    否则 --version 也会去创建数据目录、抢锁文件 ✗ 很脏 ✗
            if (HandleArgs(args))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(ToolDir);
                // ★★ 先尝试从旧目录搬家（开发期用的是 dsh-tools）
                //    必须在读配置【之前】做 ✗ 否则读到的还是空配置 ✗
                MigrateLegacyConfig(Log);
                if (AlreadyRunning())
                {
                    return;
                }
                File.WriteAllText(LockPath, Environment.ProcessId.ToString());

                var cfg = LoadConfig();
                var engine = new Engine(cfg, Log);
                var protect = cfg.Protect ?? new Protect();
                var action = cfg.Action ?? new ActionPolicy();
                var weights = cfg.Behavior ?? new BehaviorWeights();

                // ★ 启动时先做一次安全自检：硬黑名单必须非空
                //   ★★ 同时载入沙箱约束（规矩 1）：如果设了 DSH_SANDBOX_PIDS，
                //      那么本次运行【只能强杀列表里的进程】，其他一律拒绝强杀
                Safety.LoadSandboxPids();
                // ★ 把配置里的"是否遵守签名闸"同步给 Safety —— 生产默认 true
                Safety.SetHonorSignatures(action.HonorSignatures);
                // ★ 载入用户【永久授权】的进程名单（授权弹窗答"永久允许"时写入）
                ConsentDialog.LoadAlways(cfg.ConsentedProcesses);
                // ★★★ 载入"被强杀过"的历史 —— 用于检测"杀了又复活"✗
                ConsentDialog.LoadTerminatedHistory(cfg.TerminatedHistory);
                // ★★ 把日志出口接给弹窗模块 —— 它创建失败时必须留下记录 ✗
                //    （以前是静默返回，导致"600 次隐藏却没有一次弹窗"查不出原因）
                ConsentDialog.LogSink = Log;

                // ★★★ 用户要求（2026-10-10）：自身哈希要【默认写入】✗
                //   以前 expected_self_hash 一直是空的 →
                //   自校验没有基线 → 防调包形同虚设 ✗
                //   用户原话："自身哈希要默认写入主程序的哈希值" ✓
                //   → 为空就自动算本 exe 的 SHA256 并写回配置 ✓
                //   ★ 以后【换版本】哈希会变 → 那时点一下设置里的
                //     "用当前 WARDEN.exe 的哈希填入"，或删掉这一行让它重填 ✓
                if (string.IsNullOrWhiteSpace(cfg.ExpectedSelfHash))
                {
                    string autoHash = SelfCheck.SelfSha256();
                    if (autoHash.Length == 64)
                    {
                        cfg.ExpectedSelfHash = autoHash;
                        try
                        {
                            string autoJson = System.Text.Json.JsonSerializer.Serialize(
                                cfg, GuardJsonContext.Default.GuardConfig);
                            File.WriteAllText(ConfigPath, autoJson,
                                new System.Text.UTF8Encoding(false));
                            Log("[SELFCHECK] 首次运行：已自动把本程序 SHA256 写入配置 ✓ "
                                + autoHash.Substring(0, 16) + "…（不用手动点按钮）");
                        }
                        catch (Exception autoEx)
                        {
                            Log("★ 自动写入自身哈希失败（不影响守护）：" + autoEx.Message);
                        }
                    }
                    else
                    {
                        Log("★ 算不出自身哈希 —— 自校验基线留空（不影响守护）");
                    }
                }

                // ★★★ 红队自测规则（用户 2026-10-10："用户可以自己查一下"）✓
                //   只匹配我们自己的 WARDEN-RedTeam.exe ✓
                //   自测工具单独只有 34 分，够不到动手门槛 55 ✗
                //   加这条规则（+40 = 74）→ 一跑就能看到整条链：
                //     隐藏 → 关不掉 → 弹授权询问 →（应急时）冻结 ✓
                //   ★ 用"载入后补一条"的迁移方式，不依赖默认配置的结构 ✓
                try
                {
                    cfg.Rules ??= new List<Rule>();
                    bool hasRed = false;
                    foreach (Rule rr in cfg.Rules)
                    {
                        if (rr.Process != null && rr.Process.Equals(
                                "WARDEN-RedTeam.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            hasRed = true;
                            break;
                        }
                    }
                    if (!hasRed)
                    {
                        cfg.Rules.Add(new Rule
                        {
                            Name = "红队自测（WARDEN-RedTeam.exe）",
                            Process = "WARDEN-RedTeam.exe",
                            Enabled = true,
                        });
                        string rj = System.Text.Json.JsonSerializer.Serialize(
                            cfg, GuardJsonContext.Default.GuardConfig);
                        File.WriteAllText(ConfigPath, rj,
                            new System.Text.UTF8Encoding(false));
                        Log("[CONFIG] 已加入红队自测规则（WARDEN-RedTeam.exe）✓");
                    }
                }
                catch (Exception rEx)
                {
                    Log("★ 加入红队规则失败（不影响守护）：" + rEx.Message);
                }

                // ★★★ A46 应急压制：接线（事故后补回）
                //   被攻击时不等用户点"同意"，先用高频压制按住流氓软件 ✓
                Emergency.Init(ToolDir, cfg, Log);
                Emergency.AlertSink = kind =>
                {
                    // ① 全屏红警：告诉用户"我们被打了"并给几个强硬选项 ✓
                    EmergencyWarning.Show(kind);
                    // ② 再走原有的"被干扰"告警：它负责把证据写进 TAMPER.log ✓
                    SelfProtect.AlertTamper("emergency-" + kind);
                };
                Log("[应急] mode=" + (cfg.Emergency == null || cfg.Emergency.Enabled
                    ? "armed" : "off"));

                // ★ B11：全局兜底 —— 未处理异常一律写成崩溃报告 ✓
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    if (e.ExceptionObject is Exception uex)
                    {
                        WriteCrashReport("AppDomain 未处理异常", uex);
                    }
                };

                // ★★★ 启动时自校验（防调包，用户 2026-10-09 的要求）
                //     "发现它被调包了就能拉停，而不是被感染"
                if (cfg.SelfCheckOnStart)
                {
                    var sc = SelfCheck.Run(cfg.ExpectedSelfHash, Log, out string scDetail);
                    Log($"[自检] 结果={SelfCheck.Describe(sc)}  {scDetail}");
                    Log($"[自检] 自身路径={SelfCheck.SelfPath}");
                    if (sc != SelfCheck.Result.Ok && sc != SelfCheck.Result.Skipped)
                    {
                        if (cfg.SelfCheckBlockOnFail)
                        {
                            Log("★★★ 自校验未通过，配置要求拉停 → 本次【拒绝运行】✗");
                            Log("     若确认正常（例如刚重新编译还没签名），");
                            Log("     可设环境变量 WARDEN_SKIP_SELFCHECK=1 临时跳过 ✗");
                            return;
                        }
                        Log("★ 自校验未通过，但配置允许继续运行（仅告警）✗");
                    }
                }
                Log($"启动 pid={Environment.ProcessId}  扫描间隔={SweepIntervalMs}ms");
                Log($"  硬黑名单条目={Safety.HardBlock.Count}  系统目录标记={Safety.SystemDirMarkers.Length}");
                Log($"  沙箱模式={Safety.InSandboxMode}"
                    + (Safety.InSandboxMode
                       ? "  ★只允许强杀本测试生成的进程" : "  (常规模式)"));
                Log($"  签名闸={action.HonorSignatures}  白名单禁杀={action.HonorWhitelistForKill}");
                Log($"  授权弹窗={action.AllowConsentPrompt}"
                    + $"（受保护进程失败 {action.ConsentAfterFailures} 次后询问）"
                    + $"  已永久授权 {cfg.ConsentedProcesses?.Count ?? 0} 个程序");
                Log($"  演习模式={protect.DryRun}  允许强杀={action.AllowTerminate}  "
                    + $"动手门槛={weights.ApplyThreshold}  强杀门槛={weights.TerminateThreshold}");

                // ★ 看门狗线程（主循环卡住时留下证据）
                var watchdog = new Thread(WatchdogThread)
                {
                    IsBackground = true,
                    Name = "WARDEN-Watchdog",
                };
                watchdog.Start();

                var evt = new Thread(EventThread) { IsBackground = true, Name = "winevent" };
                evt.Start();
                Thread.Sleep(200);

                // ============================================================
                //  ★★ 创建隐藏消息窗口 + 托盘图标（2026-10-10 新增）
                //     用户的要求："常驻工具没托盘，用户不知道它在不在跑"
                // ============================================================
                _msgWnd = new MessageWindow();
                bool wndOk = _msgWnd.Create(OnWindowMessage, Log);
                if (wndOk)
                {
                    string iconPath = Path.Combine(AppContext.BaseDirectory, "WARDEN.ico");
                    _tray = new TrayIcon(iconPath, Log);
                    _tray.Install(_msgWnd.Handle);

            // ★★★ 自保护（用户 2026-10-10 的红队提醒）
            //     流氓软件会关我们的弹窗，或者用管理员权限杀掉我们。
            //     ★ 技术路线（用户定的）：不用看门狗，用 WNDPROC 拦截。
            //        · 在自己的窗口过程里吃掉 WM_CLOSE / SC_CLOSE
            //          -> 外部关不掉我们的窗口
            //        · 检测到就弹窗告警 + 写日志留证据
            //        · 再给自己进程设 DACL，拒绝非提权进程强杀
            //     ★ 挡不住管理员权限的强杀（Windows 机制），
            //       那种情况靠日志留证据，让用户自己判断
            try
            {
                Log("[SELFPROTECT] elevated=" + SelfProtect.IsElevated());
                SelfProtect.Start(m => Log(m));
            }
            catch (Exception ex)
            {
                // ★ 自保护失败不能拖垮主功能 ✗ 但也不能静默 ✓
                Log("[SELFPROTECT] setup failed: " + ex.Message);
            }
                    _tray.SetTooltip("WARDEN（守护中）");
                }
                else
                {
                    Log("★ 消息窗口创建失败 → 没有托盘图标（不影响守护功能）");
                }

                var aliveWindowKeys = new HashSet<IntPtr>();
                // ★★ A52A 实测抓到的真 bug：
                //    ActionLevel.Hide = 4，而这里只有 4 个格子（0..3）✗
                //    → 任何"受保护进程"（有签名 / 在白名单）被隐藏时
                //      stats[4] 越界 → IndexOutOfRangeException → 整轮扫描中断 ✗✗
                //    日志证据：L2-HIDE 之后紧跟 "sweep exception:
                //              Arg_IndexOutOfRangeException"
                //    → 放大到 8 个格子，并且写入前做边界判断 ✓
                var stats = new int[8];
                int lastReport = Environment.TickCount;
                int logCounter = 0;

                while (true)
                {
                    // ★★ 先泵一下消息（托盘菜单、鼠标事件都在这里进来）
                    //    用 PeekMessage 非阻塞 ✗ 不能用 GetMessage（那会卡住巡检）✗
                    _msgWnd?.Pump();

                    // ★ 处理托盘菜单里的请求
                    if (HandleTrayRequests())
                    {
                        break;
                    }

                    // ★★★ 停止请求必须带【合法令牌】（用户 2026-10-10 指出的漏洞）：
                    //   随便写个 .stop 就能关掉 WARDEN 是不行的 ✗
                    //   令牌不对 → 这是有人在偷偷关我们 → 记篡改 + 触发应急压制 ✓
                    if (File.Exists(StopPath))
                    {
                        string req = "";
                        try { req = File.ReadAllText(StopPath); } catch { }
                        if (StopToken.Verify(ToolDir, req, m => Log(m)))
                        {
                            Log("停止请求已验证 ✓ 退出");
                            try { File.Delete(StopPath); } catch { }
                            break;
                        }

                        // ★ 令牌不对 —— 有人在试图偷偷关掉我们 ✗
                        Log("[篡改] 停止令牌无效，已拒绝（长度=" + req.Length + "）");
                        try { File.Delete(StopPath); } catch { }
                        SelfProtect.AlertTamper("fake-stop");
                        Emergency.Trigger("fake-stop");
                        // ★ 注意：不 break —— 我们继续运行 ✓
                    }

                    // ★★★ A46 应急压制：每一圈压一遍（应急时主循环缩短到 50ms）✓
                    //   ★ 放在"暂停"判断【之前】：托盘暂停是别管网上的弹窗，
                    //     但"自己被攻击"是另一回事 —— 不能因为暂停就任人宰割 ✓
                    try
                    {
                        Emergency.Tick(engine);
                    }
                    catch (Exception tickEx)
                    {
                        Log("[应急] tick 异常: " + tickEx.Message);
                    }

                    // ★ 暂停中 → 跳过这一轮扫描（但消息照常处理 ✓）
                    if (_tray != null && _tray.IsPaused)
                    {
                        Stage("一轮扫完");
                    Thread.Sleep(Emergency.SweepMs(SweepIntervalMs));
                        continue;
                    }

                    try
                    {
                        // ★ 每轮重读配置：改完即时生效
                        // ★★ 但【只更新配置，不重建 Engine】——
                        //    重建会把失败计数与节流记录清空，
                        //    导致升级阶梯失效（2026-10-09 红队测试抓到的 bug）
                        var fresh = LoadConfig();
                        engine.UpdateConfig(fresh);
                        Emergency.Reconfigure(fresh);
                        // ★ A52A：重置本轮的"同步发送"预算（防止被窗口轰炸拖死）✓
                        engine.BeginSweep();
                        protect = fresh.Protect ?? new Protect();
                        action = fresh.Action ?? new ActionPolicy();
                        weights = fresh.Behavior ?? new BehaviorWeights();
                        Safety.SetHonorSignatures(action.HonorSignatures);

                        var rules = new List<Rule>();
                        if (fresh.Rules != null)
                        {
                            foreach (var r in fresh.Rules)
                            {
                                if (r.Enabled)
                                {
                                    rules.Add(r);
                                }
                            }
                        }

                        // ★ 事件队列只是"提示"，真正的判断靠全量枚举
                        var hints = DrainPending();

                        IntPtr fg = Native.GetForegroundWindow();
                        Native.GetWindowThreadProcessId(fg, out uint fgPid);

                        Stage("枚举窗口");
                        var all = WindowInfo.Enumerate();
                        Stage("窗口已枚举");
                        // ★★★ 空标题窗口也要纳入评估 ✗
                        //     为什么：流氓软件会用"有窗口、有类名、标题留空"来绕过标题匹配 ✗
                        //     （2026-10-09 红队 Ultra 的 U6 场景实测打穿了这一层）
                        //     ★ 但不能无脑动手：空标题窗口里有很多是系统的 ✗
                        //     → 所以它【只能靠行为分】判定，
                        //       而行为分门槛（apply_threshold）会把它筛掉 ✓
                        var noTitle = WindowInfo.Enumerate(includeNoTitle: true);
                        var seenHwnds = new HashSet<IntPtr>();
                        foreach (var w in all)
                        {
                            seenHwnds.Add(w.Hwnd);
                        }
                        foreach (var w in noTitle)
                        {
                            if (!seenHwnds.Contains(w.Hwnd) && string.IsNullOrEmpty(w.Title))
                            {
                                all.Add(w);
                            }
                        }
                        // ★★ 另外单独数一次"含隐藏窗口"的总数 ✗
                        //    为什么要两份：
                        //      · all      —— 可见有标题的窗口，用于【匹配规则、动手】✓
                        //      · allIncl  —— 含隐藏窗口，用于【判断追不追得上】✓
                        //    因为工具的动作就是隐藏窗口 ✗
                        //    只用可见窗口判断，就会自己毁掉自己的依据 ✗
                        var allIncl = WindowInfo.Enumerate(includeNoTitle: false,
                                                           includeHidden: true);
                        var alivePids = new List<uint>();
                        aliveWindowKeys.Clear();
                        var winCountByPid = new Dictionary<uint, int>();
                        foreach (var w in allIncl)
                        {
                            winCountByPid.TryGetValue(w.Pid, out int c);
                            winCountByPid[w.Pid] = c + 1;
                        }
                        foreach (var w in all)
                        {
                            alivePids.Add(w.Pid);
                        }

                        foreach (var w in all)
                        {
                            var rec = engine.Record(w);

                            // 保护①：跳过当前前台窗口
                            if (protect.SkipIfForeground && w.Pid == fgPid)
                            {
                                continue;
                            }

                            // ★ 先看有没有命中规则
                            Rule? matched = null;
                            foreach (var r in rules)
                            {
                                if (WindowInfo.MatchRule(w, r))
                                {
                                    matched = r;
                                    break;
                                }
                            }
                            if (matched == null)
                            {
                                continue;
                            }

                            // 保护②③：老版的两道"别误关"保护
                            if (protect.SkipIfHasEdit && w.HasEdit)
                            {
                                continue;
                            }
                            if (protect.SkipIfHasChildWindow && w.ChildCount > 0)
                            {
                                continue;
                            }

                            bool isFg = w.Pid == fgPid;
                            if (isFg)
                            {
                                rec.EverForeground = true;
                            }

                            var score = engine.Score(w, matched, isFg);

                            // 只记前几次，避免日志被刷爆
                            if (logCounter++ % 20 == 0 && score.Score >= weights.ApplyThreshold / 2)
                            {
                                Log($"[评分] {score.Score} {w.Proc} 「{Trunc(w.Title)}」 "
                                    + $"{score.Why}");
                            }

                            Stage("处置窗口");
                            var lvl = engine.Act(w, score.Score, rec, protect.DryRun);
                            Stage("窗口已处置");
                            if (lvl != ActionLevel.None)
                            {
                                {
                                    int li = (int)lvl;
                                    if (li >= 0 && li < stats.Length)
                                    {
                                        stats[li]++;
                                    }
                                }
                                if (!Native.IsWindow(w.Hwnd))
                                {
                                    Log($"[成功] {w.Proc} 窗口已消失");
                                }
                            }

                            if (Native.IsWindow(w.Hwnd))
                            {
                                aliveWindowKeys.Add(w.Hwnd);
                            }
                        }

                        engine.NoteSurvivors(aliveWindowKeys);
                        // ★★ 把"每个进程当前有几个窗口（含隐藏）"喂给引擎 ——
                        //    第二段弹窗的"追不上"判据依赖它 ✓
                        engine.UpdateWindowCounts(winCountByPid);
                        engine.Prune(alivePids.ToArray());

                        // ★★★ 把新点的"永久允许强杀"写回配置 ✓
                        //     同时把"强杀过谁"记进历史 —— 用于以后检测"复活"✓
                        if (ConsentDialog.NewAlways.Count > 0)
                        {
                            foreach (var n in ConsentDialog.NewAlways)
                            {
                                _pendingConsent.Add(n);
                                _pendingTerminated.Add(n);
                                Log($"[记录] 用户永久允许强杀: {n}");
                            }
                            ConsentDialog.NewAlways.Clear();
                        }
                        // ★ 每次强杀成功都记历史 —— 用于检测"杀了又复活"✓
                        foreach (var n in engine.PopTerminated())
                        {
                            _pendingTerminated.Add(n);
                            Log($"[记录] 已强杀过: {n}（若它再出现即为复活）");
                        }
                        if (_pendingConsent.Count > 0 || _pendingTerminated.Count > 0)
                        {
                            SaveRuntimeState();
                        }
                        WindowInfo.ClearCaches();

                        // 每 30 秒打一条状态，方便知道它活着
                        if (Environment.TickCount - lastReport > 30000)
                        {
                            lastReport = Environment.TickCount;
                            Log($"[状态] 事件={_eventCount} 窗口={all.Count} "
                                + $"L1={stats[1]} L2={stats[2]} L3={stats[3]}");
                        }
                    }
                    catch (Exception e)
                    {
                        Log("扫描异常: " + e.Message);
                    }

                    Stage("一轮扫完");
                    Thread.Sleep(Emergency.SweepMs(SweepIntervalMs));
                }

                // ★★★ 退出前【必须】移除托盘图标 ✗
                //     不移除的话，图标会留在托盘里变成"僵尸"✗
                //     进程没了但图标还在，鼠标划过去才消失 ✗ 很难看 ✗
                //     （这是 Shell_NotifyIcon 用法的经典坑 ✓）
                try { _tray?.Remove(); } catch { }
                try { _msgWnd?.Dispose(); } catch { }

                try { File.Delete(LockPath); } catch { }
                Log($"退出  L1={stats[1]} L2={stats[2]} L3={stats[3]}");
            }
            catch (Exception e)
            {
                Log("致命错误: " + e);
                WriteCrashReport("主流程", e);
            }
        }

        private static string Trunc(string s) => s.Length <= 40 ? s : s.Substring(0, 40);
    }
}