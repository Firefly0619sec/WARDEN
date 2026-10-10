// ============================================================================
//  WARDEN 停止令牌 —— 防止别人偷偷把 WARDEN 关掉
// ============================================================================
//  【为什么需要它】（用户 2026-10-10 指出的漏洞）
//
//      用户原话：
//        "Stop 文件内部不应该是空的，应该有校验。
//          比如，只有本次安装或启动时随机生成的 GUID，
//          或者对应的令牌、PGP 签名。"
//
//    【原来的漏洞有多大】
//        原来只要往 %LOCALAPPDATA%\WARDEN\WARDEN.stop 里写点东西，
//        WARDEN 就自己退出了 ✗
//        ★ 流氓软件【不需要任何权限】就能关掉 WARDEN ✗✗✗
//        这比"用管理员权限强杀"省事多了 —— 等于防了个寂寞 ✗
//
//    【现在的做法】
//        ① 安装/首次运行时生成 32 字节随机密钥
//        ② 用 Windows 的 DPAPI（当前用户范围）加密后存盘
//           -> ★ 只有【同一个用户】能解出来
//              （换用户/换机器复制走文件也解不开 ✓）
//        ③ .stop 里必须写 HMAC-SHA256(密钥, "warden-stop") 的十六进制
//           -> 空文件、乱写、旧的 "stop" 一律拒绝 ✓
//        ④ 拒绝时【当成攻击处理】：
//              记日志 [TAMPER] + 弹窗告警 ✓（这本身就是证据 ✓）
//
//    【为什么不直接用 DPAPI 加密 "stop" 字符串】
//        DPAPI 密文每次加密结果都不同（带随机盐）
//        -> 没法直接比对 ✗
//        所以要"解出密钥 + 算 HMAC"这种方式 ✓
//
//    【为什么用 P/Invoke 而不是 .NET 的 ProtectedData】
//        ProtectedData 要额外引 NuGet 包 ✗
//        主程序是 Native AOT，能少一个依赖就少一个 ✓
//        crypt32.dll 是系统自带的 ✓
//
//    【★ 老实说这道防线能挡住什么】
//        · 挡得住：随便写个文件就想关掉我们 ✗
//        · 挡得住：换个用户/复制文件过来 ✗
//        · ✗ 挡不住：以【同一个用户】身份运行、并且会调用 DPAPI 的程序
//                   （它能把密钥解出来）
//          -> 所以这层是"提高门槛 + 留下证据"，不是保险箱 ✓
//            真正的强杀还是靠 DACL 加固 + 日志留证 ✓
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WardenSetup
{
    internal static class StopToken
    {
        // ---- DPAPI ----
        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        // ★ CRYPTPROTECT_LOCAL_MACHINE 不设 -> 用【当前用户】范围 ✓
        private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        private static readonly object _lock = new();
        private static byte[]? _key;

        /// <summary>★ 令牌用途标记（换用途就不会互相顶替）。</summary>
        private const string PURPOSE = "warden-stop-v1";

        private static string KeyPath(string toolDir) =>
            Path.Combine(toolDir, "WARDEN.key");

        // ================================================================
        //  密钥管理
        // ================================================================

        /// <summary>
        /// ★ 确保密钥存在（没有就生成并落盘）。
        ///   返回密钥字节；失败返回 null（调用方要按"拒绝一切"处理 ✗）。
        /// </summary>
        public static byte[]? EnsureKey(string toolDir, Action<string>? log = null)
        {
            lock (_lock)
            {
                if (_key != null) return _key;

                string path = KeyPath(toolDir);
                try
                {
                    if (File.Exists(path))
                    {
                        byte[] blob = File.ReadAllBytes(path);
                        byte[]? k = Unprotect(blob);
                        if (k != null && k.Length == 32)
                        {
                            _key = k;
                            return _key;
                        }
                        // ★ 解不开（换过用户/文件被改）-> 重新生成
                        log?.Invoke("[STOPTOKEN] existing key unreadable, regenerating");
                    }

                    byte[] fresh = RandomNumberGenerator.GetBytes(32);
                    byte[] enc = Protect(fresh);
                    Directory.CreateDirectory(toolDir);
                    File.WriteAllBytes(path, enc);
                    _key = fresh;
                    log?.Invoke("[STOPTOKEN] generated new stop key");
                    return _key;
                }
                catch (Exception e)
                {
                    log?.Invoke("[STOPTOKEN] ensure key failed: " + e.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// ★ 算出"合法停止请求"应该长什么样。
        ///   HMAC-SHA256(密钥, 用途字符串) 的十六进制小写。
        /// </summary>
        public static string? Expected(string toolDir, Action<string>? log = null)
        {
            byte[]? key = EnsureKey(toolDir, log);
            if (key == null) return null;

            try
            {
                using var hmac = new HMACSHA256(key);
                byte[] mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(PURPOSE));
                return Convert.ToHexString(mac).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// ★★★ 校验一个 .stop 文件的内容是不是合法请求。
        ///
        /// 【为什么要常数时间比较】
        ///   普通字符串比较会在第一个不同的字符处提前返回 ✗
        ///   攻击者能靠计时差异一个字符一个字符猜出令牌 ✗
        ///   -> 必须用固定时间比较 ✓
        ///   （这里虽然攻击面不大，但这是基本纪律）
        /// </summary>
        public static bool Verify(string toolDir, string? content, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;

            string? expected = Expected(toolDir, log);
            if (expected == null) return false;

            string got = content.Trim();
            if (got.Length != expected.Length) return false;

            int diff = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                diff |= expected[i] ^ got[i];
            }
            return diff == 0;
        }

        /// <summary>★ 写一个合法的停止请求（给安装程序/卸载程序用）。</summary>
        public static bool WriteRequest(string toolDir, string stopPath, Action<string>? log = null)
        {
            string? token = Expected(toolDir, log);
            if (token == null)
            {
                log?.Invoke("[STOPTOKEN] cannot build stop request (no key)");
                return false;
            }
            try
            {
                Directory.CreateDirectory(toolDir);
                File.WriteAllText(stopPath, token, new UTF8Encoding(false));
                return true;
            }
            catch (Exception e)
            {
                log?.Invoke("[STOPTOKEN] write stop request failed: " + e.Message);
                return false;
            }
        }

        // ================================================================
        //  DPAPI 包装
        // ================================================================
        private static byte[] Protect(byte[] data)
        {
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            IntPtr pIn = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, pIn, data.Length);
                inBlob.cbData = data.Length;
                inBlob.pbData = pIn;

                if (!CryptProtectData(ref inBlob, "WARDEN stop key", IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                {
                    throw new InvalidOperationException("CryptProtectData failed");
                }

                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(pIn);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static byte[]? Unprotect(byte[] blob)
        {
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            IntPtr pIn = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, pIn, blob.Length);
                inBlob.cbData = blob.Length;
                inBlob.pbData = pIn;

                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                {
                    return null;
                }

                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(pIn);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
    }
}
