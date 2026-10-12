// ============================================================================
//  ConfigIntegrity.cs —— 配置安全字段完整性（0.3.6 / CP1）
// ============================================================================
//  【这条漏洞是什么】
//    WARDEN.json 就在用户目录里，任何以这个用户身份运行的程序都能直接改 ✗
//    而"改配置"是最省事的绕过方式 ✗
//      · 把 Rules 清空 → 要拦的窗口不拦了 ✓
//      · 把 dry_run / action 改掉 → 只记日志、不动手 ✓
//      · 把 process_whitelist 塞满 → 想杀的进程全在白名单里 ✓
//    WARDEN 原来只保护【自己的 exe】（SelfCheck 哈希 ✓），完全不看配置文件 ✗
//
//  【怎么做】
//    把 8 个安全相关键规范化成一条文本 → 用 DPAPI 加密存成快照 ✓
//      %LOCALAPPDATA%\WARDEN\.cfgintegrity
//    启动时解密比对：
//      · 没有快照      → 建立基线（首次运行 ✓）
//      · 与上次一致    → 安静 ✓
//      · 不一致        → 写日志报警 ★ 并列出【哪几个键被改了】✓ 然后重建基线
//                        （重建是为了不天天重复报警 ✓ 改动的记录留在日志里 ✓）
//
//  【为什么用 DPAPI 而不是普通哈希】
//    普通哈希谁都能重算 ✗ 攻击者改完配置顺手把哈希也改对就行了 ✗
//    DPAPI 绑定"当前用户"，密文只有这个用户能解 ✓ 顺手改快照没那么容易 ✓
//
//  【诚实的边界（不能吹）】
//    · 同一用户身份下运行的程序【仍然能】改配置、也【仍然能】重新加密快照 ✗
//      → 它挡不住同用户攻击者 ✓ 挡的是"别的软件/配置编辑器顺手改了"✓
//        以及"改了之后你能第一时间知道"✓
//    · 所以它是【篡改可见性】而不是【篡改不可行】✓ 这个区别我不含糊 ✓
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WARDEN
{
    internal static class ConfigIntegrity
    {
        /// <summary>要被保护的安全相关顶层键（从真实配置里核对过的 ✓）。</summary>
        private static readonly string[] Keys =
        {
            "Rules",
            "protect",
            "process_whitelist",
            "action",
            "behavior",
            "expected_self_hash",
            "self_check_on_start",
            "self_check_block_on_fail",
        };

        /// <summary>快照文件。测试可以用环境变量改到别处（免得碰真实数据目录 ✓）。</summary>
        private static string SnapshotPath
        {
            get
            {
                string? over = Environment.GetEnvironmentVariable("WARDEN_CFGINTEGRITY_PATH");
                if (!string.IsNullOrEmpty(over)) { return over!; }
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WARDEN", ".cfgintegrity");
            }
        }

        // ---------------------------------------------------------------- DPAPI
        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static byte[]? Protect(byte[] plain)
        {
            IntPtr inPtr = IntPtr.Zero;
            DATA_BLOB inBlob = default, outBlob = default;
            try
            {
                inPtr = Marshal.AllocHGlobal(plain.Length);
                Marshal.Copy(plain, 0, inPtr, plain.Length);
                inBlob.cbData = plain.Length;
                inBlob.pbData = inPtr;
                if (!CryptProtectData(ref inBlob, "WARDEN-config", IntPtr.Zero, IntPtr.Zero,
                                      IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                {
                    return null;
                }
                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            catch { return null; }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) { LocalFree(outBlob.pbData); }
                if (inPtr != IntPtr.Zero) { Marshal.FreeHGlobal(inPtr); }
            }
        }

        private static byte[]? Unprotect(byte[] cipher)
        {
            IntPtr inPtr = IntPtr.Zero;
            DATA_BLOB inBlob = default, outBlob = default;
            try
            {
                inPtr = Marshal.AllocHGlobal(cipher.Length);
                Marshal.Copy(cipher, 0, inPtr, cipher.Length);
                inBlob.cbData = cipher.Length;
                inBlob.pbData = inPtr;
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                        IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                {
                    return null;
                }
                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            catch { return null; }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) { LocalFree(outBlob.pbData); }
                if (inPtr != IntPtr.Zero) { Marshal.FreeHGlobal(inPtr); }
            }
        }

        // ------------------------------------------------------- 规范化 + 主流程
        /// <summary>把 8 个安全键规范成一条文本（键名 + 原始 JSON 值 ✓ 顺序固定 ✓）。</summary>
        public static string Canonical(string json)
        {
            var sb = new StringBuilder();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "<不是 JSON 对象>";
            }
            foreach (string k in Keys)
            {
                sb.Append(k).Append('=');
                if (doc.RootElement.TryGetProperty(k, out JsonElement v))
                {
                    sb.Append(v.GetRawText());
                }
                else
                {
                    sb.Append("<缺失>");
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>列出两条规范化文本里不同的键名 ✓</summary>
        private static List<string> DiffKeys(string a, string b)
        {
            var changed = new List<string>();
            string[] la = a.Split('\n'), lb = b.Split('\n');
            int n = Math.Min(la.Length, lb.Length);
            for (int i = 0; i < n; i++)
            {
                if (!string.Equals(la[i], lb[i], StringComparison.Ordinal))
                {
                    int eq = la[i].IndexOf('=');
                    changed.Add(eq > 0 ? la[i].Substring(0, eq) : ("第" + (i + 1) + "行"));
                }
            }
            return changed;
        }

        /// <summary>
        /// ★ 启动时调用：比对 + 刷新基线，返回一行日志。
        /// </summary>
        public static string CheckAndRefresh(string json)
        {
            string now;
            try { now = Canonical(json); }
            catch (Exception e) { return "· 配置完整性：配置不是合法 JSON（" + e.GetType().Name + "）"; }

            string? prev = null;
            try
            {
                if (File.Exists(SnapshotPath))
                {
                    byte[] cipher = File.ReadAllBytes(SnapshotPath);
                    byte[]? plain = Unprotect(cipher);
                    if (plain != null) { prev = new UTF8Encoding(false).GetString(plain); }
                }
            }
            catch { prev = null; }

            // 总是刷新基线（这样不会每次启动都重复报警 ✓ 变更记录留在日志里 ✓）
            try
            {
                byte[]? cipher = Protect(new UTF8Encoding(false).GetBytes(now));
                if (cipher != null)
                {
                    string? dir = Path.GetDirectoryName(SnapshotPath);
                    if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir!); }
                    File.WriteAllBytes(SnapshotPath, cipher);
                }
            }
            catch { }

            if (prev == null)
            {
                return "· 配置完整性：已建立基线 ✓（下次启动开始比对 ✓）";
            }
            if (string.Equals(prev, now, StringComparison.Ordinal))
            {
                return "✓ 配置完整性：安全字段与上次启动一致 ✓";
            }
            List<string> changed = DiffKeys(prev, now);
            return "★ 配置完整性：安全字段被改过 ✗ 改动键 = " + string.Join("、", changed.ToArray())
                 + "   （若是你自己用设置界面改的，可忽略；基线已重建 ✓）";
        }
    }
}
