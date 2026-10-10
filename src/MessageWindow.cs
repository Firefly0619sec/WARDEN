// ============================================================================
//  隐藏消息窗口 —— 托盘图标的"落点"
// ============================================================================
//  【为什么需要它】
//
//      Shell_NotifyIcon 加托盘图标时，必须指定一个【窗口句柄】✗
//      系统会把托盘的鼠标消息发到那个窗口 ✓
//
//      而 WARDEN 本身【没有窗口】✗
//      它只有事件钩子和巡检循环 ✗
//      → 所以要专门创建一个【不可见的消息窗口】✓
//        只用来收消息，不显示任何东西 ✓
//
//  【★ 和"自绘授权弹窗"那次失败的区别】
//
//      自绘授权弹窗那次是：
//        · 一个【可见的】窗口 ✗
//        · 跑在【独立线程】上 ✗
//        · 自己写完整消息循环（GetMessage 阻塞）✗
//      → 结果窗口创建成功却看不见 ✗
//
//      而这个是：
//        · 一个【不可见的】消息窗口 ✓
//        · 在主线程创建，不占消息循环 ✓
//        · 主循环用 PeekMessage 非阻塞地取消息 ✓
//      → 简单得多，风险也小得多 ✓
//
//  【★ AOT 下的注意点】
//      窗口过程是【函数指针】✗ 必须用一个静态字段长期持有 ✗
//      否则会被 GC 回收 → 消息一到就崩 ✗
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WARDEN
{
    internal sealed class MessageWindow : IDisposable
    {
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_QUIT = 0x0012;
        private const int CW_USEDEFAULT = unchecked((int)0x80000000);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg,
                                                IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName,
            string lpWindowName, uint dwStyle, int x, int y, int w, int h,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg,
            IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd,
            uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        private const uint PM_REMOVE = 0x0001;

        /// <summary>
        /// ★★ 窗口过程必须是静态字段长期持有 ✗
        ///    否则会被 GC 回收，消息一来就崩 ✗
        /// </summary>
        private static WndProcDelegate? _procRef;

        /// <summary>消息处理回调（由外部注入）。</summary>
        private static Func<uint, IntPtr, IntPtr, bool>? _handler;

        /// <summary>窗口过程收到的自定义消息交给谁处理。</summary>
        private static Action<string>? _log;

        private IntPtr _hwnd = IntPtr.Zero;
        private string _className = string.Empty;

        public IntPtr Handle => _hwnd;
        public bool IsValid => _hwnd != IntPtr.Zero;

        /// <summary>
        /// ★ 创建隐藏消息窗口。
        /// </summary>
        /// <param name="handler">消息处理器，返回 true 表示已处理</param>
        /// <param name="log">日志输出</param>
        public bool Create(Func<uint, IntPtr, IntPtr, bool> handler, Action<string> log)
        {
            if (_hwnd != IntPtr.Zero)
            {
                return true;
            }
            _handler = handler;
            _log = log;

            try
            {
                _className = "WARDEN_MsgWnd_" + Environment.ProcessId;

                // ★ 持有委托引用，防止被 GC ✗
                _procRef = WndProc;

                var wc = new WNDCLASS
                {
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procRef),
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = _className,
                };

                ushort atom = RegisterClassW(ref wc);
                if (atom == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    // 1410 = 类已存在（重复创建的情况，可以继续）
                    if (err != 1410)
                    {
                        log($"[消息窗口] RegisterClassW 失败，错误码 {err}");
                        return false;
                    }
                }

                // ★ 创建一个【不显示】的窗口 ✗
                //   样式传 0（没有 WS_VISIBLE）✓ 也不调 ShowWindow ✓
                //   这样它永远不可见，只用来收消息 ✓
                _hwnd = CreateWindowExW(0, _className, "WARDEN",
                    0,                    // ★ 样式 0 = 不可见
                    CW_USEDEFAULT, CW_USEDEFAULT, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    log($"[消息窗口] CreateWindowExW 失败，错误码 {Marshal.GetLastWin32Error()}");
                    return false;
                }

                log($"[消息窗口] 已创建（不可见）hwnd={_hwnd.ToInt64():X}");
                return true;
            }
            catch (Exception e)
            {
                log("[消息窗口] 创建异常: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// ★ 非阻塞地处理挂起的消息。
        ///   在主循环里每轮调一次 ✓ 不要用 GetMessage（那会阻塞 ✗）
        /// </summary>
        /// <returns>处理了多少条消息</returns>
        public int Pump()
        {
            int n = 0;
            // ★ 每轮最多处理 50 条，避免消息洪水把巡检循环饿死 ✗
            while (n < 50 && PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT)
                {
                    break;
                }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
                n++;
            }
            return n;
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                try
                {
                    DestroyWindow(_hwnd);
                    _log?.Invoke("[消息窗口] 已销毁");
                }
                catch { }
                _hwnd = IntPtr.Zero;
            }
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // ★ 先交给外部处理器（托盘消息等）
                if (_handler != null && _handler(msg, wParam, lParam))
                {
                    return IntPtr.Zero;
                }
            }
            catch (Exception e)
            {
                _log?.Invoke("[消息窗口] 处理消息出错: " + e.Message);
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }
}
