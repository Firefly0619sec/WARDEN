// ============================================================================
//  托盘图标 —— 让用户看得见 WARDEN 在不在跑
// ============================================================================
//  【为什么需要它】（用户 2026-10-10 的建议）
//
//      WARDEN 是个【常驻后台】的工具 ✗
//      但用户完全看不到它在不在运行 ✗
//      设置界面虽然有状态显示，但要打开才知道 ✗
//
//      → 托盘图标 + 右键菜单，这是从"能用"到"像个正经软件"的关键一步 ✓
//
//  【★ 图标的设计】
//      来源：用户的想法 —— 仿《红星照耀中国》经典封面
//        一个红军小号手站在西北黄土高原上，迎着朝阳吹响号角
//      用户的解读："号角手不就是守望者吗？"
//
//      配色：黑白灰 + 一抹点缀的红色（朝阳）✓
//
//  【技术实现】
//      用 Shell_NotifyIcon（Shell32）加托盘图标 ✓
//      纯 Win32，Native AOT 下可用 ✓
//
//      ★ 两个必须注意的地方：
//        ① 托盘图标必须绑定一个【窗口】✗
//           系统会把鼠标消息发到这个窗口 ✓
//           → 所以要注册一个隐藏的"消息窗口" ✓
//        ② 退出前必须 Shell_NotifyIcon(NIM_DELETE) ✗
//           否则图标会【留在托盘里变成僵尸】✗
//           鼠标划过去它才消失 ✗ 很难看 ✗
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WARDEN
{
    /// <summary>
    /// ★ 托盘图标与右键菜单。
    ///
    /// 用法：
    ///   var tray = new TrayIcon(iconPath, log);
    ///   tray.Install(hwnd);        // 挂到主消息窗口上
    ///   tray.SetTooltip("...");    // 更新提示文字
    ///   tray.HandleMessage(msg, wParam, lParam);  // 在窗口过程里调
    ///   tray.Remove();             // 退出前必须调 ✗
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        // ------------------------------------------------------------ 常量
        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        /// <summary>★ 托盘回调消息号。WM_APP(0x8000) + 1，避免和系统消息冲突 ✗</summary>
        public const uint WM_TRAY = 0x8000 + 1;

        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_LBUTTONDBLCLK = 0x0203;

        private const int IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint MF_CHECKED = 0x00000008;

        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_RETURNCMD = 0x0100;

        /// <summary>菜单项 ID。</summary>
        private const int CMD_OPEN_SETTINGS = 1001;
        private const int CMD_PAUSE = 1002;
        private const int CMD_VIEW_LOG = 1003;
        private const int CMD_RELOAD = 1004;
        private const int CMD_EXIT = 1005;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIconW(uint dwMessage,
            ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImageW(IntPtr hinst, string name,
            uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags,
            UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags,
            int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        // ------------------------------------------------------------ 字段
        private readonly Action<string> _log;
        private readonly string _iconPath;
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _hIcon = IntPtr.Zero;
        private bool _installed;
        private string _tip = "WARDEN";

        /// <summary>★ 用户点了"暂停"。true 表示当前暂停中。</summary>
        public bool IsPaused { get; private set; }

        /// <summary>★ B6：托盘状态（正常 / 暂停 / 应急）✓</summary>
        public enum TrayState
        {
            Normal,
            Paused,
            Emergency,
        }

        /// <summary>★ B6：当前状态 ✓</summary>
        public TrayState State { get; private set; } = TrayState.Normal;

        /// <summary>
        /// ★ B6：切换托盘状态 —— 换图标 + 换提示文字 ✓
        ///   【为什么要换图标】托盘图标是用户唯一一直看得见的东西 ✓
        ///     光靠提示文字不行（要鼠标移上去才看到 ✗）
        ///     绿色/原色 = 守护中；黄色 = 暂停；红色 = 应急压制中 ✓
        ///   图标文件载入失败 → 用回原图标并记日志（绝不静默 ✗）
        /// </summary>
        public void SetState(TrayState st)
        {
            State = st;
            string want;
            string tip;
            switch (st)
            {
                case TrayState.Paused:
                    want = "-paused";
                    tip = L.T("WARDEN（已暂停守护）");
                    break;
                case TrayState.Emergency:
                    want = "-emergency";
                    tip = L.T("WARDEN（应急压制中）");
                    break;
                default:
                    want = "";
                    tip = L.T("WARDEN（守护中）");
                    break;
            }

            // 换图标（找不到对应文件就用原来的 ✓）
            try
            {
                string path = _iconPath;
                if (want.Length > 0 && path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(0, path.Length - 4) + want + ".ico";
                }
                IntPtr h = IntPtr.Zero;
                if (File.Exists(path))
                {
                    h = LoadImageW(IntPtr.Zero, path, IMAGE_ICON, 0, 0,
                                   LR_LOADFROMFILE | LR_DEFAULTSIZE);
                }
                if (h == IntPtr.Zero)
                {
                    _log("[托盘] 状态图标不可用（" + path + "），沿用当前图标 ✓");
                }
                else
                {
                    IntPtr old = _hIcon;
                    _hIcon = h;
                    var d = new NOTIFYICONDATA
                    {
                        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                        hWnd = _hwnd,
                        uID = 1,
                        uFlags = NIF_ICON,
                        hIcon = _hIcon,
                    };
                    if (!Shell_NotifyIconW(NIM_MODIFY, ref d))
                    {
                        _log("[托盘] 换图标失败（Shell_NotifyIcon NIM_MODIFY）✗");
                    }
                    if (old != IntPtr.Zero)
                    {
                        DestroyIcon(old);
                    }
                }
            }
            catch (Exception e)
            {
                _log("[托盘] 换图标异常: " + e.Message);
            }

            SetTooltip(tip);
        }

        /// <summary>用户点了"重新加载配置"。</summary>
        public bool ReloadRequested { get; set; }

        /// <summary>用户点了"退出"。</summary>
        public bool ExitRequested { get; set; }

        /// <summary>用户点了"打开设置界面"。</summary>
        public bool OpenSettingsRequested { get; set; }

        /// <summary>用户点了"查看日志"。</summary>
        public bool ViewLogRequested { get; set; }

        public TrayIcon(string iconPath, Action<string> log)
        {
            _iconPath = iconPath ?? string.Empty;
            _log = log ?? (_ => { });
        }

        // ================================================================
        //  安装 / 卸载
        // ================================================================
        /// <summary>
        /// ★ 安装托盘图标。
        /// </summary>
        /// <param name="hwnd">接收托盘鼠标消息的窗口（通常就是主消息窗口）</param>
        public bool Install(IntPtr hwnd)
        {
            if (_installed)
            {
                return true;
            }
            if (hwnd == IntPtr.Zero)
            {
                _log("[托盘] ★ 窗口句柄为空，无法安装托盘图标");
                return false;
            }
            _hwnd = hwnd;

            // ★ 从 .ico 文件加载图标
            //   失败不代表要放弃 —— 可以用系统默认图标兜底 ✓
            if (!string.IsNullOrEmpty(_iconPath) && System.IO.File.Exists(_iconPath))
            {
                _hIcon = LoadImageW(IntPtr.Zero, _iconPath, IMAGE_ICON, 0, 0,
                                    LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (_hIcon == IntPtr.Zero)
                {
                    _log("[托盘] 图标文件加载失败，用系统默认图标代替: " + _iconPath);
                }
            }
            else
            {
                _log("[托盘] 找不到图标文件，用系统默认图标: " + _iconPath);
            }

            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd,
                uID = 1,                       // ★ 同一个窗口可以挂多个图标，用 uID 区分
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY,
                hIcon = _hIcon,
                szTip = Trunc(_tip, 127),
            };

            if (!Shell_NotifyIconW(NIM_ADD, ref data))
            {
                _log("[托盘] ★ Shell_NotifyIcon(NIM_ADD) 失败，错误码 "
                     + Marshal.GetLastWin32Error());
                return false;
            }
            _installed = true;
            _log("[托盘] 图标已安装");
            return true;
        }

        /// <summary>
        /// ★★★ 卸载托盘图标 —— 【退出前必须调】✗
        ///
        /// 不调的后果：图标会留在托盘里变成"僵尸"✗
        ///   进程已经没了，但图标还在 ✗
        ///   用户鼠标划过去它才消失 ✗ 非常难看 ✗
        ///
        /// （这是 Shell_NotifyIcon 用法的经典坑 ✓）
        /// </summary>
        public void Remove()
        {
            if (!_installed)
            {
                return;
            }
            try
            {
                var data = new NOTIFYICONDATA
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hwnd,
                    uID = 1,
                };
                Shell_NotifyIconW(NIM_DELETE, ref data);
                _log("[托盘] 图标已移除");
            }
            catch (Exception e)
            {
                _log("[托盘] 移除图标出错: " + e.Message);
            }
            finally
            {
                _installed = false;
                // ★ 自己 LoadImage 拿到的图标要自己释放 ✗
                if (_hIcon != IntPtr.Zero)
                {
                    DestroyIcon(_hIcon);
                    _hIcon = IntPtr.Zero;
                }
            }
        }

        /// <summary>更新托盘提示文字（鼠标悬停时显示）。</summary>
        public void SetTooltip(string text)
        {
            _tip = text ?? "WARDEN";
            if (!_installed)
            {
                return;
            }
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_TIP,
                szTip = Trunc(_tip, 127),
            };
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }

        // ================================================================
        //  消息处理
        // ================================================================
        /// <summary>
        /// ★ 在窗口过程里调用它，处理托盘的鼠标消息。
        /// </summary>
        /// <returns>true 表示这条消息已被处理</returns>
        public bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg != WM_TRAY)
            {
                return false;
            }

            // ★ lParam 的低 16 位才是鼠标消息 ✗
            //   高 16 位是图标 ID ✗ 不能直接当消息用 ✗
            uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);

            switch (mouseMsg)
            {
                case WM_RBUTTONUP:
                    ShowMenu();
                    return true;

                case WM_LBUTTONDBLCLK:
                    // ★ 双击 = 打开设置界面（最常用的操作）
                    OpenSettingsRequested = true;
                    _log("[托盘] 双击图标 → 打开设置界面");
                    return true;

                case WM_LBUTTONUP:
                    // ★ 单击不做事 ✗
                    //   因为单击经常是误触（鼠标滑过托盘区）✗
                    //   而且双击时也会先收到单击 ✗ 会打架 ✗
                    return true;
            }
            return false;
        }

        /// <summary>弹出右键菜单。</summary>
        private void ShowMenu()
        {
            IntPtr menu = IntPtr.Zero;
            try
            {
                menu = CreatePopupMenu();
                if (menu == IntPtr.Zero)
                {
                    return;
                }

                AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_OPEN_SETTINGS, L.T("打开设置界面"));
                AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenuW(menu, MF_STRING | (IsPaused ? MF_CHECKED : 0),
                            (UIntPtr)CMD_PAUSE,
                            IsPaused ? "继续守护" : "暂停守护");
                AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_RELOAD, L.T("重新加载配置"));
                AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_VIEW_LOG, L.T("查看日志"));
                AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_EXIT, L.T("退出 WARDEN"));

                if (!GetCursorPos(out POINT pt))
                {
                    return;
                }

                // ★★ 必须先 SetForegroundWindow ✗
                //     否则菜单弹出后【点别处不会消失】✗
                //     这是 TrackPopupMenu 的经典坑 ✓
                SetForegroundWindow(_hwnd);

                int cmd = TrackPopupMenu(menu,
                    TPM_RIGHTBUTTON | TPM_RETURNCMD,
                    pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

                switch (cmd)
                {
                    case CMD_OPEN_SETTINGS:
                        OpenSettingsRequested = true;
                        _log("[托盘] 打开设置界面");
                        break;

                    case CMD_PAUSE:
                        IsPaused = !IsPaused;
                        _log("[托盘] " + (IsPaused ? "已暂停守护" : "已继续守护"));
                        SetTooltip(IsPaused ? "WARDEN（已暂停）" : "WARDEN（守护中）");
                        break;

                    case CMD_RELOAD:
                        ReloadRequested = true;
                        _log("[托盘] 请求重新加载配置");
                        break;

                    case CMD_VIEW_LOG:
                        ViewLogRequested = true;
                        _log("[托盘] 打开日志");
                        break;

                    case CMD_EXIT:
                        ExitRequested = true;
                        _log("[托盘] 用户选择退出");
                        break;
                }
            }
            catch (Exception e)
            {
                _log("[托盘] 菜单出错: " + e.Message);
            }
            finally
            {
                if (menu != IntPtr.Zero)
                {
                    DestroyMenu(menu);
                }
            }
        }

        public void Dispose() => Remove();

        private static string Trunc(string s, int n)
            => string.IsNullOrEmpty(s) ? string.Empty
               : (s.Length <= n ? s : s.Substring(0, n));
    }
}
