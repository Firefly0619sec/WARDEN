// ============================================================================
//  WARDEN 自保护 —— WNDPROC 拦截 + 进程加固
// ============================================================================
//  【用户 2026-10-10 的红队提醒】
//
//    "有的流氓软件可能会直接把我的提权弹窗关掉。
//     或者他们会利用管理员权限（流氓软件特有的乱要权限）
//     把整个程序杀掉的。要防御住这个，并且检测到这种就直接弹窗表示问题，
//     要用户动用更加强制的战略手段。给用户的卸载或者清除病毒争取时间，
//     但我们不会卸载或清除病毒，因为那不是我们的活。"
//
//    ★ 用户随后定的技术路线："停一下，不用看门狗，直接用 WNDPROC 拦截。"
//
//  【为什么不用看门狗】
//    看门狗 = 再起一个进程盯着 ✗
//      · 攻击面翻倍（多一个进程能被打）
//      · 管理员权限的流氓软件可以把两个一起杀 ✗
//      · 重启逻辑有死循环风险（杀了重启、重启又被杀）
//    -> 改成在自己进程里【拦窗口消息】✓ 更轻、更直接 ✓
//
//  【★★★ 老实说能防到什么程度】
//
//    ① 外部想关我们的窗口（WM_CLOSE / SC_CLOSE）
//       -> ★ 能挡住 ✓（在窗口过程里直接吃掉这条消息）
//    ② 外部想强行销毁窗口（DestroyWindow）
//       -> 能【检测到】✓ 但挡不住（DestroyWindow 不走消息，不给拒绝的机会）
//    ③ 外部强杀进程（TerminateProcess）
//       -> ★ 看权限：
//            非提权 -> 我们的 DACL 加固能挡住 ✓
//            提权   -> 挡不住 ✗（管理员能抢所有权，这是 Windows 机制）
//
//    ★ 所以最终目标不是"绝对防住"，而是：
//        能挡的挡 ✓  挡不住的立刻让用户知道 ✓  并留下证据 ✓
//
//  【★ 我们不做的事】
//    不替用户卸载、不清毒、不动可疑程序 ✗
//    那不是我们的活，乱动还可能毁掉证据 ✓
//    我们只争取时间 + 把问题明确告诉用户 ✓
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WARDEN
{
    internal static class SelfProtect
    {
        // ================================================================
        //  P/Invoke
        // ================================================================
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_OPERATION = 0x0008;

        private const int DACL_SECURITY_INFORMATION = 0x00000004;
        private const int SE_KERNEL_OBJECT = 6;
        private const byte ACL_REVISION = 2;

        // ---- 窗口消息 ----
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_NCDESTROY = 0x0082;
        private const uint WM_SYSCOMMAND = 0x0112;
        private const uint SC_CLOSE = 0xF060;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text,
                                              string caption, uint type);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr h);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool InitializeSecurityDescriptor(IntPtr pSD, uint revision);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetSecurityDescriptorDacl(
            IntPtr pSD, bool daclPresent, IntPtr pDacl, bool daclDefaulted);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool InitializeAcl(IntPtr pAcl, uint size, uint rev);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AddAccessDeniedAce(
            IntPtr pAcl, uint rev, uint accessMask, IntPtr pSid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint SetSecurityInfo(
            IntPtr handle, int objectType, int securityInfo,
            IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AllocateAndInitializeSid(
            IntPtr pIdentifierAuthority, byte subAuthorityCount,
            uint a0, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6, uint a7,
            out IntPtr pSid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr FreeSid(IntPtr pSid);

        // ---- 窗口子类化（用 comctl32 的 SetWindowSubclass，比 SetWindowLongPtr 稳）----
        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam,
                                             IntPtr lParam, UIntPtr uIdSubclass,
                                             UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
                                                     UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg,
                                                     IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // ================================================================
        //  状态
        // ================================================================
        private static Action<string>? _log;
        private static readonly ConcurrentDictionary<IntPtr, byte> _protected = new();
        /// <summary>★ 我们自己主动关窗口时置位，避免把自己的关闭当成攻击。</summary>
        private static int _closingSelf = 0;
        private static long _lastAlertTicks = 0;
        /// <summary>★ 保持委托引用，防止被 GC 回收（AOT 下尤其重要）。</summary>
        private static SUBCLASSPROC? _procRef;
        private static EnumWindowsProc? _enumRef;
        private static Thread? _scanner;

        private const string DataDirName = "WARDEN";

        private static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataDirName);

        // ================================================================
        //  ① 启动：加固 + 开始拦截
        // ================================================================
        public static void Start(Action<string> log)
        {
            _log = log;
            Harden();

            // ★ 拦截已经存在的窗口（消息窗口、托盘相关窗口）
            ProtectOwnWindows();

            // ★ 持续扫描：以后新建的窗口（比如授权弹窗）也要拦上
            //   为什么要扫：我们没法在每个窗口创建处都插一脚，
            //   而扫描很便宜（一个进程也就几个窗口）✓
            _scanner = new Thread(ScanLoop)
            {
                IsBackground = true,
                Name = "WARDEN-SelfProtect",
            };
            _scanner.Start();
            log("[SELFPROTECT] started (WNDPROC guard + process hardening)");
        }

        private static void ScanLoop()
        {
            while (true)
            {
                try { ProtectOwnWindows(); }
                catch { }
                Thread.Sleep(1000);
            }
        }

        /// <summary>★ 把我们自己进程的所有顶层窗口都子类化。</summary>
        private static void ProtectOwnWindows()
        {
            uint myPid = (uint)Environment.ProcessId;
            _enumRef ??= (hWnd, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == myPid) ProtectOne(hWnd);
                }
                catch { }
                return true;
            };
            EnumWindows(_enumRef, IntPtr.Zero);
        }

        /// <summary>
        /// ★★ 事故后重建的桥接：给【指定的那个窗口】加同一套 WNDPROC 保护 ✓
        ///    全屏红警窗口（EmergencyWarning）会调用它 ——
        ///    不然流氓软件可以直接把红警关掉 ✗（用户 2026-10-10 提醒过）
        ///    实际干活的是 ProtectOne() ✓
        /// </summary>
        public static void ProtectHandle(IntPtr hwnd) => ProtectOne(hwnd);
        private static void ProtectOne(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            if (_protected.ContainsKey(hWnd)) return;

            _procRef ??= GuardProc;
            // ★ uIdSubclass 用固定值；dwRefData 空
            if (SetWindowSubclass(hWnd, _procRef, (UIntPtr)1, UIntPtr.Zero))
            {
                _protected[hWnd] = 1;
                _log?.Invoke("[SELFPROTECT] window guarded hwnd=0x"
                             + hWnd.ToString("X"));
            }
        }

        // ================================================================
        //  ② WNDPROC 拦截
        // ================================================================
        private static IntPtr GuardProc(IntPtr hWnd, uint uMsg, IntPtr wParam,
                                        IntPtr lParam, UIntPtr uIdSubclass,
                                        UIntPtr dwRefData)
        {
            try
            {
                switch (uMsg)
                {
                    // ---- 外部想关窗口 ----
                    case WM_CLOSE:
                        if (Interlocked.CompareExchange(ref _closingSelf, 0, 0) == 0)
                        {
                            // ★★★ 这就是用户说的"流氓软件直接把我的弹窗关掉"
                            //     我们在窗口过程里【吃掉】这条消息 -> 关不掉 ✓
                            _log?.Invoke("[TAMPER] external WM_CLOSE blocked on hwnd=0x"
                                         + hWnd.ToString("X"));
                            AlertTamper("close-attempt");
                            return IntPtr.Zero;   // ★ 不往下传 = 窗口不关
                        }
                        break;

                    case WM_SYSCOMMAND:
                        // ★ Alt+F4 和点右上角 × 都会走这里（SC_CLOSE）
                        if (((uint)wParam & 0xFFF0) == SC_CLOSE)
                        {
                            if (Interlocked.CompareExchange(ref _closingSelf, 0, 0) == 0)
                            {
                                _log?.Invoke("[TAMPER] external SC_CLOSE blocked on hwnd=0x"
                                             + hWnd.ToString("X"));
                                AlertTamper("close-attempt");
                                return IntPtr.Zero;
                            }
                        }
                        break;

                    // ---- 窗口真的被销毁了（DestroyWindow 挡不住，只能记录）----
                    case WM_DESTROY:
                    case WM_NCDESTROY:
                        _log?.Invoke("[TAMPER] window destroyed hwnd=0x"
                                     + hWnd.ToString("X") + " msg=0x"
                                     + uMsg.ToString("X"));
                        _protected.TryRemove(hWnd, out _);
                        // ★ 这里不告警：正常的程序退出也会走这条
                        //   如果是被强行销毁，进程通常马上也要没了，
                        //   那种情况靠日志留证据 ✓ 不要弹一堆窗骚扰用户 ✗
                        break;
                }
            }
            catch (Exception e)
            {
                _log?.Invoke("[SELFPROTECT] guard exception: " + e.Message);
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        /// <summary>
        /// ★ 我们主动关窗口时调这个，把窗口放行。
        ///   否则自己关不掉自己的窗口（那会很难看）✗
        /// </summary>
        public static void BeginSelfClose()
        {
            Interlocked.Exchange(ref _closingSelf, 1);
            // 给一点时间让关闭走完，然后复位
            var t = new Thread(() =>
            {
                Thread.Sleep(2000);
                Interlocked.Exchange(ref _closingSelf, 0);
            })
            { IsBackground = true };
            t.Start();
        }

        // ================================================================
        //  ③ 进程加固（DACL：拒绝他人强杀）
        // ================================================================
        private static void Harden()
        {
            IntPtr pSid = IntPtr.Zero, pAcl = IntPtr.Zero, pSd = IntPtr.Zero;
            IntPtr pAuth = IntPtr.Zero;
            try
            {
                // Everyone 的 SID = S-1-1-0
                pAuth = Marshal.AllocHGlobal(6);
                for (int i = 0; i < 6; i++) Marshal.WriteByte(pAuth, i, 0);
                Marshal.WriteByte(pAuth, 5, 1);
                if (!AllocateAndInitializeSid(pAuth, 1, 0, 0, 0, 0, 0, 0, 0, 0, out pSid))
                {
                    _log?.Invoke("[SELFPROTECT] SID init failed");
                    return;
                }

                const int ACL_SIZE = 512;
                pAcl = LocalAlloc(0x0040, (UIntPtr)ACL_SIZE);
                if (pAcl == IntPtr.Zero || !InitializeAcl(pAcl, ACL_SIZE, ACL_REVISION))
                {
                    _log?.Invoke("[SELFPROTECT] ACL init failed");
                    return;
                }
                uint deny = PROCESS_TERMINATE | PROCESS_VM_WRITE | PROCESS_VM_OPERATION;
                if (!AddAccessDeniedAce(pAcl, ACL_REVISION, deny, pSid))
                {
                    _log?.Invoke("[SELFPROTECT] deny-ACE failed");
                    return;
                }

                pSd = LocalAlloc(0x0040, (UIntPtr)64);
                if (pSd == IntPtr.Zero || !InitializeSecurityDescriptor(pSd, 1)
                    || !SetSecurityDescriptorDacl(pSd, true, pAcl, false))
                {
                    _log?.Invoke("[SELFPROTECT] SD init failed");
                    return;
                }

                uint rc = SetSecurityInfo(GetCurrentProcess(), SE_KERNEL_OBJECT,
                                          DACL_SECURITY_INFORMATION,
                                          IntPtr.Zero, IntPtr.Zero, pAcl, IntPtr.Zero);
                if (rc != 0)
                {
                    // ★ 失败不致命，如实记下来 ✓
                    _log?.Invoke("[SELFPROTECT] SetSecurityInfo rc=" + rc
                                 + " (hardening unavailable on this machine)");
                }
                else
                {
                    _log?.Invoke("[SELFPROTECT] process hardened "
                                 + "(non-elevated processes cannot terminate us)");
                }
            }
            catch (Exception e)
            {
                _log?.Invoke("[SELFPROTECT] harden exception: " + e.Message);
            }
            finally
            {
                if (pAuth != IntPtr.Zero) { try { Marshal.FreeHGlobal(pAuth); } catch { } }
                if (pSid != IntPtr.Zero) { try { FreeSid(pSid); } catch { } }
                if (pAcl != IntPtr.Zero) { try { LocalFree(pAcl); } catch { } }
                if (pSd != IntPtr.Zero) { try { LocalFree(pSd); } catch { } }
            }
        }

        // ================================================================
        //  ④ 告警
        // ================================================================
        /// <summary>
        /// ★★★ 检测到攻击就弹窗（用户 2026-10-10 明确要求）。
        ///
        /// 【要说清三件事】
        ///   ① 出了什么事
        ///   ② 那可能是什么（很可能就是你最近装的流氓软件）
        ///   ③ 你该做什么（用更强的手段）
        ///
        /// 【★ 我们不做的事】
        ///   不替用户卸载、不清毒 ✓ 那不是我们的活
        /// </summary>
        public static void AlertTamper(string kind)
        {
            // ★ 30 秒内只弹一次 —— 否则流氓软件反复试探会弹满屏幕 ✗
            long now = DateTime.Now.Ticks;
            long last = Interlocked.Read(ref _lastAlertTicks);
            if (last != 0 && (now - last) < TimeSpan.TicksPerSecond * 30)
            {
                _log?.Invoke("[TAMPER] alert suppressed (rate limit)");
                return;
            }
            Interlocked.Exchange(ref _lastAlertTicks, now);

            // ★ 先留证据（万一弹窗被挡住，日志里还有）
            try
            {
                File.AppendAllText(Path.Combine(DataDir, "TAMPER.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + "  kind=" + kind + "\r\n", Encoding.UTF8);
            }
            catch { }

            string title = "WARDEN 正在被干扰";
            string main = "有程序在试图关掉 WARDEN 的窗口";

            var sb = new StringBuilder();
            sb.AppendLine("刚才有别的程序试图强行关闭 WARDEN 的窗口。");
            sb.AppendLine("这一条已经被我们拦下来了（窗口没有关掉）。");
            sb.AppendLine();
            sb.AppendLine("★ 会干这种事的，通常就是你最近装的那个「流氓软件」：");
            sb.AppendLine("    · 它不希望 WARDEN 拦住它的弹窗");
            sb.AppendLine("    · 它多半向你要过管理员权限");
            sb.AppendLine();
            sb.AppendLine("★ 老实说：");
            sb.AppendLine("    · 关窗口这种，我们能挡住");
            sb.AppendLine("    · 但如果它用管理员权限【直接杀进程】，我们挡不住");
            sb.AppendLine("      （那是 Windows 的机制，任何用户态程序都挡不住）");
            sb.AppendLine("      —— 那种情况我们会把证据写进日志，请你留意");
            sb.AppendLine();
            sb.AppendLine("──── 建议你现在做 ────");
            sb.AppendLine();
            sb.AppendLine("① 想想最近装了什么。多半就是它。");
            sb.AppendLine();
            sb.AppendLine("② 用专业卸载工具卸掉它");
            sb.AppendLine("     Geek Uninstaller（免费）能扫残留注册表和文件，");
            sb.AppendLine("     比「控制面板 → 卸载」彻底得多");
            sb.AppendLine();
            sb.AppendLine("③ 卸不掉 / 卸完又回来 → 进【安全模式】再卸");
            sb.AppendLine("     安全模式下它启动不了，就老实了");
            sb.AppendLine("     设置 → 系统 → 恢复 → 高级启动 → 立即重新启动");
            sb.AppendLine();
            sb.AppendLine("④ 再用杀毒软件全盘扫一遍（Windows 安全中心就够）");
            sb.AppendLine();
            sb.AppendLine("⑤ 搞不定就来反馈：托盘图标右键 → 反馈问题");
            sb.AppendLine();
            sb.AppendLine("★ WARDEN 不会替你去卸载或清除那个软件 ——");
            sb.AppendLine("  那不是我们的活，乱动还可能把证据毁掉。");
            sb.AppendLine("  我们只负责挡、报、留证据，争取时间给你。");

            try
            {
                // ★ 置顶 + 抢焦点，尽量让用户看见
                MessageBoxW(IntPtr.Zero, main + "\r\n\r\n" + sb.ToString(), title,
                    0x00000010u | 0x00040000u | 0x00010000u | 0x00001000u);
            }
            catch
            {
                _log?.Invoke("[TAMPER] alert dialog failed (evidence kept in TAMPER.log)");
            }
        }

        /// <summary>★ 当前进程是否提权运行（写进日志，诊断时有用）。</summary>
        public static bool IsElevated()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(
                    System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
