// ============================================================================
//  WARDEN 红队自测工具（RedTeam.exe）
// ============================================================================
//  【用途】模拟流氓弹窗软件：疯狂弹置顶无标题小窗、抢前台、【拒绝 WM_CLOSE】
//          用来验证 WARDEN 按不按得住这种东西 ✓
//  【安全边界】只弹自己的窗口；不写注册表、不联网、不碰任何系统进程 ✗
//              默认 30 秒自己退出（免得忘记关）✓
//  【用法】RedTeam.exe [秒数]
//  【技术】原生 AOT + 纯 Win32（和 WARDEN 本体一个路子，不用 WinForms）✓
//          窗口过程对 WM_CLOSE 直接返回 0 = 拒绝关闭 ✓
// ============================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace WardenRedTeam
{
    internal static class Program
    {
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_DESTROY = 0x0002;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private delegate IntPtr WndProcDel(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd; public uint message; public IntPtr wParam;
            public IntPtr lParam; public uint time; public int ptX; public int ptY;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style; public IntPtr lpfnWndProc; public int cbClsExtra;
            public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon;
            public IntPtr hCursor; public IntPtr hbrBackground;
            public string? lpszMenuName; public string lpszClassName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASS wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int ex, string cls, string? title, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after,
            int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG m, IntPtr h,
            uint min, uint max, uint remove);
        [DllImport("user32.dll")] private static extern int BeginPaint(IntPtr h, IntPtr ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, IntPtr ps);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rc, IntPtr br);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DrawTextW(IntPtr hdc, string t, int len, ref RECT rc, uint flags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint c);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr o);
        [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint c);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int h, int w, int e, int o, int weight,
            uint italic, uint underline, uint strike, uint cs, uint prec, uint clip,
            uint q, uint pitch, string face);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? n);

        private static WndProcDel? _proc;      // ★ 长期持有，否则 GC 掉会崩 ✓
        private static int _made;

        private static IntPtr WndProcImpl(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
        {
            // ★★ 拒绝关闭：这就是"流氓软件"的关键特征 ✓
            if (msg == WM_CLOSE || msg == WM_DESTROY)
            {
                return IntPtr.Zero;
            }
            if (msg == 0x000F)   // WM_PAINT
            {
                IntPtr ps = Marshal.AllocHGlobal(64);
                try
                {
                    IntPtr hdc = BeginPaint(h, ps);
                    var rc = new RECT { Left = 0, Top = 0, Right = 320, Bottom = 140 };
                    IntPtr br = CreateSolidBrush(0x00C86400);
                    FillRect(hdc, ref rc, br);
                    DeleteObject(br);
                    SetBkMode(hdc, 1);
                    SetTextColor(hdc, 0x00FFFFFF);
                    IntPtr f = CreateFontW(-22, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0,
                                           "Microsoft YaHei");
                    IntPtr old = SelectObject(hdc, f);
                    var tr = new RECT { Left = 10, Top = 10, Right = 310, Bottom = 130 };
                    DrawTextW(hdc, "红队自测：这是一个「关不掉」的弹窗\n（故意的，用来试 WARDEN）",
                              -1, ref tr, 0x00000010);
                    SelectObject(hdc, old);
                    DeleteObject(f);
                    EndPaint(h, ps);
                }
                finally { Marshal.FreeHGlobal(ps); }
                return IntPtr.Zero;
            }
            return DefWindowProcW(h, msg, wp, lp);
        }

        private static void Pump()
        {
            while (true)
            {
                Thread.Sleep(500);
                while (PeekMessage(out MSG m, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref m);
                    DispatchMessageW(ref m);
                }
            }
        }

        private static void Main(string[] args)
        {
            int seconds = 30;
            if (args.Length > 0 && int.TryParse(args[0], out int s) && s > 0) { seconds = s; }

            Console.WriteLine("WARDEN 红队自测工具");
            Console.WriteLine("  时长 " + seconds + " 秒（到点自己退出）");
            Console.WriteLine("  行为：每 200ms 弹一个【置顶·无标题·拒绝关闭】的小窗");
            Console.WriteLine("  ★ 只动自己的窗口，不碰系统 ✓");

            _proc = WndProcImpl;
            IntPtr inst = GetModuleHandleW(null);
            string cls = "WARDEN_RedTeam_" + Environment.ProcessId;
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = inst,
                lpszClassName = cls,
            };
            if (RegisterClassW(ref wc) == 0)
            {
                Console.WriteLine("★ RegisterClass 失败 err=" + Marshal.GetLastWin32Error());
                return;
            }

            var pump = new Thread(Pump) { IsBackground = true, Name = "RedTeam-Pump" };
            pump.Start();

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                int x = 120 + (_made * 37) % 900;
                int y = 90 + (_made * 23) % 480;
                IntPtr h = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, cls, null,
                    WS_POPUP | WS_VISIBLE, x, y, 320, 140,
                    IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (h != IntPtr.Zero)
                {
                    _made++;
                    SetForegroundWindow(h);
                    SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                }
                if (_made > 0 && _made % 10 == 0)
                {
                    Console.WriteLine("  已弹出窗口数: " + _made);
                }
                Thread.Sleep(200);
            }
            Console.WriteLine("结束：共弹出 " + _made + " 个窗口 ✓（随进程退出消失）");
        }
    }
}
