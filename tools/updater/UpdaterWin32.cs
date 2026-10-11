// ============================================================================
//  更新器的原生接口层（2026-10-11 · Beta 3 / 0.3.1）
//
//  【为什么写它】Updater 原本用 WinForms 的两样东西 ✗ 而 WinForms 不支持 Native AOT ✓：
//     ① MessageBox.Show  → 换 MessageBoxW（user32 ✓）
//     ② OpenFileDialog    → 换 GetOpenFileNameW（comdlg32 ✓ 系统原生打开对话框 ✓）
//     ③ Application.EnableVisualStyles / SetCompatibleTextRenderingDefault → 直接删掉 ✓
//  改完后 Updater 不再依赖 WinForms ✓ 可开 PublishAot ✓ 49.24 MB → 约 1 MB ✓
//
//  【踩过的坑，这里全部避开】
//    ✗ 结构体里不放 string/bool/数组 ✗ AOT 禁用内置封送器 ✓ 一律用 IntPtr + 自己分配 ✓
//    ✓ 字符串用 Marshal.StringToHGlobalUni / AllocHGlobal，用完 FreeHGlobal ✓
//    ✓ 异常绝不逃出原生回调（本文件没有回调 ✓ 但保留 try/catch 习惯 ✓）
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace WardenUpdater
{
    internal static class Win32Ui
    {
        private const uint MB_OK = 0x00000000, MB_ICONWARNING = 0x00000030, MB_ICONINFORMATION = 0x00000040;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

        /// <summary>原生消息框（取代 WinForms 的 MessageBox ✗ AOT 安全 ✓）</summary>
        public static void Msg(string text, string caption, bool information)
        {
            try { MessageBoxW(IntPtr.Zero, text, caption, MB_OK | (information ? MB_ICONINFORMATION : MB_ICONWARNING)); }
            catch { }
        }

        // ---------------- 原生"打开文件"对话框（comdlg32 ✓） ----------------
        private const int OFN_READONLY = 0x00000001, OFN_PATHMUSTEXIST = 0x00000800, OFN_FILEMUSTEXIST = 0x00001000;

        // ★ 全字段都是指针/整数 → 结构体本身是 blittable ✓（字符串统一用 IntPtr ✓）
        [StructLayout(LayoutKind.Sequential)]
        private struct OPENFILENAMEW
        {
            public uint lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public IntPtr lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public uint nMaxCustFilter;
            public uint nFilterIndex;
            public IntPtr lpstrFile;
            public uint nMaxFile;
            public IntPtr lpstrFileTitle;
            public uint nMaxFileTitle;
            public IntPtr lpstrInitialDir;
            public IntPtr lpstrTitle;
            public uint Flags;
            public ushort nFileOffset;
            public ushort nFileExtension;
            public IntPtr lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr pvReserved;
            public uint dwReserved;
            public uint FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

        /// <summary>弹出系统原生"打开"对话框；用户取消返回 null ✓</summary>
        public static string? PickZipFile(string title, string filter)
        {
            IntPtr pFilter = IntPtr.Zero, pTitle = IntPtr.Zero, pDefExt = IntPtr.Zero;
            IntPtr pFile = IntPtr.Zero;
            try
            {
                pFilter = Marshal.StringToHGlobalUni(filter);   // "描述\0*.zip\0" 形式 ✓
                pTitle = Marshal.StringToHGlobalUni(title);
                pDefExt = Marshal.StringToHGlobalUni("zip");
                const int maxFile = 4096;
                pFile = Marshal.AllocHGlobal(maxFile * 2);      // UTF-16 缓冲 ✓
                for (int i = 0; i < maxFile; i++) { Marshal.WriteInt16(pFile, i * 2, 0); }

                var ofn = new OPENFILENAMEW
                {
                    lStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                    lpstrFilter = pFilter,
                    nFilterIndex = 1,
                    lpstrFile = pFile,
                    nMaxFile = maxFile,
                    lpstrTitle = pTitle,
                    lpstrDefExt = pDefExt,
                    Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_READONLY,
                };
                if (!GetOpenFileNameW(ref ofn)) { return null; }   // 用户取消 ✓
                return Marshal.PtrToStringUni(pFile);
            }
            catch
            {
                return null;   // ★ 绝不让异常冒到调用方（宁可当作"用户取消"✓）
            }
            finally
            {
                if (pFilter != IntPtr.Zero) { Marshal.FreeHGlobal(pFilter); }
                if (pTitle != IntPtr.Zero) { Marshal.FreeHGlobal(pTitle); }
                if (pDefExt != IntPtr.Zero) { Marshal.FreeHGlobal(pDefExt); }
                if (pFile != IntPtr.Zero) { Marshal.FreeHGlobal(pFile); }
            }
        }
    }
}
