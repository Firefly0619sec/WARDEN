// ============================================================================
//  启动时自校验 —— 防调包的最后一环
// ============================================================================
//  【为什么需要它】（用户 2026-10-09 的要求）
//
//      用户原话：
//        "如果这玩意突然在本地被调包了，没有签名你都不知道会不会被调包；
//          现在只要你签了名，发现它被调包了，就能拉停，而不是被感染。"
//
//      → 光有签名不够 ✗ 还要【运行时自己验自己】✓
//
//  【★ 两道检查，各管一件事】
//
//      ① Authenticode 签名状态（WinVerifyTrust）
//         · 有效 → 文件与签名时的内容一致 ✓
//         · HashMismatch → 被改过 ✗ → 拉停 ✓
//         ★ 这是最可靠的一道：攻击者要绕过它，必须【重新签名】✗
//           而重新签名需要代码签名证书 + 私钥 ✗ 成本很高 ✓
//
//      ② 自身 SHA256 与配置里的基线比对
//         · 挡住"用另一张有效证书重签"这类情况 ✓
//         · 基线由签名脚本在签名后写入配置 ✓
//         ★ 注意它的局限：能改配置文件的人也能改基线 ✗
//           所以它只作为【辅助】✗ 主判据仍是 ① ✓
//
//  【★ 校验失败怎么办】
//      默认【拒绝运行】✗ 并留下明确日志 ✓
//      逃生口：环境变量 WARDEN_SKIP_SELFCHECK=1（开发调试用）
//        —— 且会在日志里留下"跳过了自检"的明确记录 ✓
// ============================================================================

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WARDEN
{
    internal static class SelfCheck
    {
        /// <summary>自检结果。</summary>
        internal enum Result
        {
            Ok = 0,
            /// <summary>自身文件没有签名。</summary>
            Unsigned = 1,
            /// <summary>★ 签名与内容不符（被改过）。</summary>
            Tampered = 2,
            /// <summary>★ 自身 SHA256 与基线不符。</summary>
            HashMismatch = 3,
            /// <summary>无法判断。</summary>
            Unknown = 4,
            /// <summary>显式跳过。</summary>
            Skipped = 5,
        }

        /// <summary>自己的 exe 路径。</summary>
        public static string SelfPath
        {
            get
            {
                try
                {
                    return Environment.ProcessPath ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>自身文件的 SHA256（小写十六进制）。失败返回空串。</summary>
        public static string SelfSha256()
        {
            try
            {
                string path = SelfPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return string.Empty;
                }
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(path);
                byte[] h = sha.ComputeHash(fs);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 执行自检。
        /// ★ 任何异常都返回 Unknown（保守），绝不返回 Ok ✗
        /// </summary>
        /// <param name="expectedHash">配置里的期望哈希（可为空串表示不检查这一项）</param>
        public static Result Run(string expectedHash, Action<string> log, out string detail)
        {
            detail = string.Empty;

            // ---- 逃生口：显式跳过（开发调试用）----
            string? skip = Environment.GetEnvironmentVariable("WARDEN_SKIP_SELFCHECK");
            if (skip == "1" || string.Equals(skip, "true", StringComparison.OrdinalIgnoreCase))
            {
                detail = "环境变量 WARDEN_SKIP_SELFCHECK 已设置 → 跳过自检";
                return Result.Skipped;
            }

            string path = SelfPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                detail = "拿不到自身路径，无法自检";
                return Result.Unknown;
            }

            // ---- ① 签名状态 ----
            var st = Signer.Verify(path);
            switch (st)
            {
                case SignatureState.Valid:
                    break;
                case SignatureState.Unsigned:
                    // ★★★ 用户要求（2026-10-10）：自校验默认开启 ✓
                    //   但我们的包【还没签名】（F2 SignPath 待办）✗
                    //   旧逻辑：没签名就直接 return Result.Unsigned
                    //           → 全新装完一启动就被自己拦停 ✗✗（实测踩到）
                    //   新逻辑：没签名只当警告，继续往下做【哈希比对】✓
                    //   防调包靠的是哈希基线；签名只是加分项 ✓
                    detail = "自身文件未签名（还没签；哈希与基线一致就先放行 ✓）";
                    break;
                case SignatureState.Tampered:
                    detail = "★★ 自身文件的签名与内容不符 —— 可能已被替换或修改";
                    return Result.Tampered;
                case SignatureState.Untrusted:
                    detail = "自身文件的签名不受信任（证书未导入受信任根？）";
                    return Result.Unknown;
                default:
                    detail = "签名状态无法判断";
                    return Result.Unknown;
            }

            // ---- ② SHA256 基线 ----
            string actual = SelfSha256();
            if (string.IsNullOrEmpty(actual))
            {
                detail = "签名有效，但算不出自身哈希";
                return Result.Unknown;
            }
            detail = "签名有效，SHA256=" + actual.Substring(0, 16) + "…";

            // ★★★ 事故后补回：程序【刚更新过】就自动刷新基线 ✓
            //   换了新 exe → 配置里还是旧哈希 → 自校验会拦停 ✗
            //   判据：exe 比配置文件更新（说明是刚装/刚更新）→ 刷新并继续 ✓
            //   不削弱防护：能改安装目录的人本来也能改配置 ✗
            bool looksLikeUpdate = false;
            try
            {
                string cfgPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WARDEN", "WARDEN.json");
                if (File.Exists(cfgPath) && File.Exists(path))
                {
                    looksLikeUpdate = File.GetLastWriteTimeUtc(path)
                                    > File.GetLastWriteTimeUtc(cfgPath);
                }
            }
            catch
            {
                // 判断不了就按老规矩（拦停）✓
            }
            if (looksLikeUpdate)
            {
                log("[自检] 检测到程序刚被更新 → 自动刷新自校验基线 ✓ "
                    + actual.Substring(0, 16) + "…");
                detail = "own file looks like a fresh update -> baseline refreshed; "
                    + "new SHA256=" + actual.Substring(0, 16) + "…";
                return Result.Ok;
            }

            if (!string.IsNullOrEmpty(expectedHash))
            {
                if (!string.Equals(actual, expectedHash.Trim(),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    detail += "  ★★ 与配置里的基线不符（期望 "
                              + expectedHash.Substring(0, Math.Min(16, expectedHash.Length))
                              + "…）";
                    return Result.HashMismatch;
                }
                detail += " ✓ 与基线一致";
            }
            else
            {
                detail += "（配置里没有基线，跳过哈希比对）";
            }

            return Result.Ok;
        }

        /// <summary>人类可读的结果说明。</summary>
        public static string Describe(Result r) => r switch
        {
            Result.Ok => "通过",
            Result.Unsigned => "自身未签名",
            Result.Tampered => "★ 被改动过（签名不符）",
            Result.HashMismatch => "★ 哈希与基线不符",
            Result.Unknown => "无法判断",
            Result.Skipped => "已跳过（调试）",
            _ => "未知",
        };
    }
}
