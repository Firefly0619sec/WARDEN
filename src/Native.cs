// ============================================================================
//  Win32 互操作层
// ============================================================================
//  【为什么手写 P/Invoke 而不引入第三方库】
//      实测过 CsWin32 和 Vanara，两者在 Native AOT 下都可用 ✓
//      但本工具需要的 API 就那么十几个，手写约 100 行，
//      换来的收益是：零依赖、产物最小、行为完全可控 ✓
//      （如果将来 API 面变大，可以再换成 CsWin32 的源生成器。）
//
//  ★★ 一条用血换来的经验（沙箱测试台里踩过两次）：
//      Win32 函数【返回句柄的必须声明 restype】，【收句柄的必须声明 argtypes】。
//      少任何一个，64 位句柄都会被 .NET 的默认封送当成 32 位处理，
//      表现为：返回值莫名其妙变成 0，或者抛 "int too long to convert"。
//
//  【★ 事件驱动而非轮询】
//      SetWinEventHook 让我们在窗口【创建/显示/获得焦点】的那一刻就被通知，
//      而不是每 2 秒扫一遍。对"行为拦截"来说这是关键：
//      弹窗还来不及抢焦点，我们就已经知道它出现了。
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WARDEN
{
    internal static class Native
    {
        // ------------------------------------------------------------ 常量
        public const int WM_CLOSE = 0x0010;   // ★ 常量 WM_CLOSE
        public const int WM_QUIT = 0x0012;   // ★ 常量 WM_QUIT
        public const int WM_SYSCOMMAND = 0x0112;   // ★ 常量 WM_SYSCOMMAND
        public const int SC_CLOSE = 0xF060;   // ★ 常量 SC_CLOSE

        public const uint GW_CHILD = 5;   // ★ 常量 GW_CHILD
        public const uint GW_OWNER = 4;   // ★ 常量 GW_OWNER

        /// <summary>★ 隐藏窗口 —— 对"受保护进程"（有合法签名/在白名单）的最温和处置。</summary>
        public const int SW_HIDE = 0;   // ★ 隐藏窗口

        public const int GWL_EXSTYLE = -20;   // ★ 常量 GWL_EXSTYLE
        public const int GWL_STYLE = -16;   // ★ 常量 GWL_STYLE
        public const int WS_EX_TOPMOST = 0x00000008;   // ★ 常量 WS_EX_TOPMOST
        public const int WS_EX_TOOLWINDOW = 0x00000080;   // ★ 常量 WS_EX_TOOLWINDOW
        public const int WS_EX_NOACTIVATE = 0x08000000;   // ★ 常量 WS_EX_NOACTIVATE
        public const int WS_POPUP = unchecked((int)0x80000000);   // ★ 常量 WS_POPUP

        public const uint TH32CS_SNAPPROCESS = 0x00000002;   // ★ 快照类型：包含所有进程（CreateToolhelp32Snapshot 用）
        public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;   // ★ 查询进程信息的最小权限（拿映像路径用，Win7+ 支持）
        public const int PROCESS_TERMINATE = 0x0001;   // ★ 结束进程所需权限

        // SetWinEventHook 的事件常量
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;   // ★ 常量 EVENT_SYSTEM_FOREGROUND
        public const uint EVENT_OBJECT_CREATE = 0x8000;   // ★ 常量 EVENT_OBJECT_CREATE
        public const uint EVENT_OBJECT_DESTROY = 0x8001;   // ★ 常量 EVENT_OBJECT_DESTROY
        public const uint EVENT_OBJECT_SHOW = 0x8002;   // ★ 常量 EVENT_OBJECT_SHOW
        public const uint EVENT_OBJECT_HIDE = 0x8003;   // ★ 常量 EVENT_OBJECT_HIDE
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;   // ★ 常量 EVENT_OBJECT_LOCATIONCHANGE
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;   // ★ 常量 WINEVENT_OUTOFCONTEXT
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;   // ★ 常量 WINEVENT_SKIPOWNPROCESS

        public const uint SMTO_ABORTIFHUNG = 0x0002;   // ★ 目标无响应就立刻返回，不要死等（SendMessageTimeout 用）
        public const uint SMTO_NORMAL = 0x0000;   // ★ 正常发送（不特殊处理）

        // ------------------------------------------------------------ 委托
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType,
                                          IntPtr hwnd, int idObject, int idChild,
                                          uint dwEventThread, uint dwmsEventTime);

        // ------------------------------------------------------------ user32
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);   // 遍历所有顶层窗口

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumChildWindows(IntPtr hWndParent,
                                                   EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);   // ★ 窗口当前是否可见（被 ShowWindow(SW_HIDE) 之后为 false）

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);   // 句柄是否仍是个有效窗口

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);   // ★ 取窗口标题（Unicode 版）

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLengthW(IntPtr hWnd);   // ★ 取窗口标题长度（用来先分配缓冲区）

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);   // ★ 取窗口类名（某些流氓窗口靠类名识别）

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);   // ★ 由窗口句柄反查它属于哪个进程

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();   // ★ 当前前台（用户正在操作）的窗口

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);   // ★ 取窗口的亲属（GW_OWNER 等），用来判断"是不是某窗口的附属"

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);   // 取父窗口（为空表示顶层窗口）

        [DllImport("user32.dll")]
        public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);   // ★ 读窗口样式（WS_EX_TOPMOST 等）

        /// <summary>★ 异步投递"请关闭"。不阻塞，程序可拒绝。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);   // 投递消息（不等待处理，适合发 WM_CLOSE）

        /// <summary>
        /// ★ 同步发送，带超时。比 PostMessage 强硬：
        /// 即使目标线程卡住，也会在超时后返回（配合 SMTO_ABORTIFHUNG）。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam,   // ★ 发消息并限时等待（超时就放弃，避免被挂起的窗口拖死）
                                                        IntPtr lParam, uint fuFlags,
                                                        uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);   // 取窗口在屏幕上的位置与尺寸

        [DllImport("user32.dll")]
        public static extern bool IsHungAppWindow(IntPtr hWnd);   // ★ 窗口是否已"无响应"（挂起的流氓窗口会一直 True）

        /// <summary>★ 显示/隐藏窗口 —— 对"受保护进程"用 SW_HIDE 做最温和处置。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);   // 设置窗口显示状态（SW_HIDE / SW_SHOW）

        /// <summary>
        /// ★★★ A52A 实测新增：异步版 ShowWindow —— 只投递请求，绝不等待 ✗
        ///
        /// 【为什么必须用它】
        ///   ShowWindow 对【别的进程】的窗口会【同步发送 WM_SHOWWINDOW】✗
        ///   目标线程忙着造窗口、或不泵消息（流氓软件假死）时，我们只能干等 ✗
        ///   最坏等到系统的"无响应"判定超时（一次约 5 秒）✗✗
        ///   实测现象：看门狗反复报"停在：处置窗口 python.exe"，
        ///             而 Act 里除了这个调用没有别的会阻塞 ✗
        ///
        ///   ★ 对安全工具来说这是致命缺陷：
        ///     守护进程绝不能被被守护对象拖住 ✗
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);   // ★ 异步隐藏/显示（不阻塞）

        // ★ 事故后补回：挂起进程要用的访问权（0x0800）
        public const int PROCESS_SUSPEND_RESUME = 0x0800;

        /// <summary>★ 挂起进程的全部线程（只冻不杀，规矩 2）</summary>
        [DllImport("ntdll.dll")]
        public static extern int NtSuspendProcess(IntPtr hProcess);
        /// <summary>★ 事件钩子：窗口创建/显示/焦点变化时回调，替代轮询。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,   // ★ 注册窗口事件钩子（窗口创建/显示/隐藏/前台切换时收到通知）
                                                    IntPtr hmodWinEventProc,
                                                    WinEventProc lpfnWinEventProc,
                                                    uint idProcess, uint idThread,
                                                    uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);   // ★ 撤销事件钩子

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,   // ★ 取消息（返回 0 表示收到 WM_QUIT；-1 表示出错）
                                             uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);   // ★ 翻译键盘消息

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessageW(ref MSG lpMsg);   // ★ 把消息派发给窗口过程

        [DllImport("user32.dll")]
        public static extern bool PostThreadMessageW(uint idThread, uint Msg, IntPtr wParam,
                                                     IntPtr lParam);

        // ------------------------------------------------------------ kernel32
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);   // 创建进程快照（遍历进程用）

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);   // 遍历进程：取第一个

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);   // 遍历进程：取下一个

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);   // 关闭句柄

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, uint pid);   // ★ 打开进程句柄（拿到才能查询或结束它）

        /// <summary>★★ 强杀。这是最后手段 —— 调用前必须过完 Safety 的四道闸。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);   // ★ 强制结束进程（本工具最重的手段）

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,   // ★ 取进程的完整映像路径（判断是否在系统目录用）
                                                             StringBuilder lpExeName,
                                                             ref uint lpdwSize);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        // ------------------------------------------------------------ 结构体
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;   // 消息所属窗口
            public uint message;   // 消息编号（如 WM_CLOSE = 0x0010）
            public IntPtr wParam;   // 消息附带的参数一
            public IntPtr lParam;   // 消息附带的参数二
            public uint time;   // 消息产生的时间
            public int ptX;
            public int ptY;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize;   // 结构体大小（必须先填）
            public uint cntUsage;   // 引用计数（不用）
            public uint th32ProcessID;   // ★ 进程 ID
            public IntPtr th32DefaultHeapID;   // 默认堆 ID（不用）
            public uint th32ModuleID;   // 模块 ID（不用）
            public uint cntThreads;   // 线程数
            public uint th32ParentProcessID;   // ★ 父进程 ID（多进程守护检测要靠它）
            public int pcPriClassBase;   // 基础优先级
            public uint dwFlags;   // 标志
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;   // ★ 可执行文件名
        }
    }
}
