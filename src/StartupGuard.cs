// ============================================================================
//  StartupGuard.cs —— 启动自检：开机启动项（0.3.6 / CP3）
// ============================================================================
//  【为什么要有这个】
//    原来已经有"自身 exe 的 SHA256 自检" ✓（SelfCheck）
//    但漏了一种非常常见、也非常阴的做法：把【开机启动项】改指向别的程序 ✗
//      HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WARDEN
//        原本指向  C:\Users\...\Programs\WARDEN\WARDEN.exe
//        被改成    D:\tmp\evil.exe
//      → 用户以后每次开机启动的都是别人的程序 ✗
//      → 而 WARDEN 自己完全察觉不到 ✗（它自己还在跑，只是开机不再自启 ✓）
//
//  【设计原则：只报告，绝不修复】
//    · 发现问题只写日志 + 返回一句话给上层 ✓
//    · 绝不静默改注册表 ✗ 绝不静默重装 ✗（用户明确要求：丢了要"告诉他"✓）
//    · 读写注册表用的都是 HKCU ✓ 不需要管理员 ✓ 也不需要写权限 ✓
//
//  【为什么"没有开机启动项"不算异常】
//    用户可能本来就选择不开机自启 ✓ 也可能手动删了 ✓
//    所以这种情况只报"没有设置"✓ 不报成"被删了"✗ 免得每天误报 ✓
//    真正值得报警的是【指向变了】✗ 和【指向的文件没了】✗
// ============================================================================

using System;
using System.IO;
using Microsoft.Win32;

namespace WARDEN
{
    internal static class StartupGuard
    {
        public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValueName = "WARDEN";

        /// <summary>开机启动项当前的值（没有 / 读不到 → null ✓）。</summary>
        public static string? CurrentAutostartTarget()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                if (k == null) { return null; }
                return k.GetValue(RunValueName) as string;
            }
            catch { return null; }
        }

        /// <summary>
        /// ★ 启动自检。返回一行日志用的话：
        ///   "✓ ..." 正常 ✓   "★ ..." 有问题 ✗   "· ..." 不确定/跳过 ✓
        ///   exePath = 当前正在运行的程序路径（一般是 SelfCheck.SelfPath ✓）
        /// </summary>
        public static string Check(string exePath)
        {
            try
            {
                string self;
                try { self = Path.GetFullPath(exePath); } catch { self = exePath; }

                if (!File.Exists(self))
                {
                    return "★ 自身 exe 不见了：" + self;
                }

                string? target = CurrentAutostartTarget();
                if (string.IsNullOrWhiteSpace(target))
                {
                    return "· 开机启动项：未设置或读不到 —— 不算异常 ✓（可能你本来就没开自启 ✓）";
                }

                // 值可能是： "C:\...\WARDEN.exe" --silent  或  C:\...\WARDEN.exe --silent
                string got = target.Trim();
                if (got.StartsWith("\"", StringComparison.Ordinal))
                {
                    int end = got.IndexOf('"', 1);
                    if (end > 1) { got = got.Substring(1, end - 1); }
                }
                else
                {
                    int sp = got.IndexOf(" -", StringComparison.Ordinal);
                    if (sp > 0) { got = got.Substring(0, sp); }
                }

                string normalTarget;
                try { normalTarget = Path.GetFullPath(got); } catch { normalTarget = got; }

                if (!string.Equals(normalTarget, self, StringComparison.OrdinalIgnoreCase))
                {
                    return "★ 开机启动项指向的不是我 ✗ 现在指向：" + got
                         + "    （我自己在：" + self + "）";
                }
                if (!File.Exists(normalTarget))
                {
                    return "★ 开机启动项指向的文件不存在（被删了？）：" + normalTarget;
                }
                return "✓ 开机启动项正常，指向：" + normalTarget;
            }
            catch (Exception e)
            {
                return "· 启动自检出错（不影响使用）：" + e.GetType().Name;
            }
        }
    }
}
