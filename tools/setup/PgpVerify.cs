// ============================================================================
//  WARDEN PGP 签名校验 —— 用主人的私钥授权"关闭 WARDEN"
// ============================================================================
//  【这个设计是谁的】（用户 2026-10-10 提的，比我原来的方案强）
//
//      用户原话：
//        "我给你一个方式，可以更上一层楼：给我的 Stop 文件用 PGP 私钥签名，
//          然后在程序里内置我的公钥。公钥本身泄露出去影响不大，
//          而且刚好可以用公钥扩展我的反馈选项，也就是对我的反馈进行加密。
//          这样别人看不见，反而还能加强隐私。
//          校验 Stop 文件的时候，就看签名验证。"
//
//  【★★★ 比 DPAPI + HMAC 强在哪】
//
//      原来（DPAPI + HMAC）：
//        密钥用 DPAPI 保护，存盘在同一个用户目录下
//        ✗ 同一个用户跑的任何程序都能调 DPAPI 把密钥解出来
//        -> 流氓软件照样能伪造关闭请求 ✗
//
//      现在（PGP 签名）：
//        ★ 私钥【永远不离开主人】✓
//        ★ 公钥内嵌在程序里，公开无害 ✓
//        -> 流氓软件拿不到私钥，就【签不出】合法关闭请求 ✓✓✓
//        -> 这是真正的"只有主人能关" ✓
//
//  【为什么调 gpg.exe 而不是自己实现 OpenPGP】
//      · OpenPGP 自己实现太重、太容易出错（解析包、校验子包、签名类型…）
//      · BouncyCastle 能用（MIT，和 AAGPL-3.0 兼容 ✓）但会引入大依赖，
//        而且要操心 AOT 裁剪 ✗
//      · 用户机器上已经有 Gpg4win ✓
//      · ★ 我们只是【调用命令行】，没有链接它的代码
//        -> 不受 GPL 传染，也符合"不要重复造轮子" ✓
//
//  【★ 安全细节（很重要）】
//
//      ① 用【独立的临时 GNUPGHOME】✗ 不用用户的密钥环
//         避免污染用户的 keyring，也避免受用户 keyring 里的
//         其他密钥影响（否则别人导入自己的公钥就能签了 ✗）
//
//      ② 只导入我们内嵌的那一把公钥 ✓
//
//      ③ 校验通过后还要【比对指纹】✗
//         gpg 只说"这个签名有效"，但可能是别人签的
//         -> 必须确认 VALIDSIG 的指纹 == 内嵌公钥的指纹 ✓
//
//      ④ 指纹写死在源码里（要改就得重新编译）✓
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WardenSetup
{
    internal static class PgpVerify
    {
        /// <summary>
        /// ★★★ 主人的签名公钥指纹（写死，改它就得重新编译）。
        ///
        /// 这行是【信任根】：只要它不被改，别人就伪造不了关闭请求 ✓
        /// </summary>
        public const string OwnerFingerprint =
            "EBFB6B895D8EEC8F59CE0A26E23876CBBDAE330F";

        /// <summary>★ 主人的公钥（Armored），用来校验签名。</summary>
        public const string OwnerPublicKey = @"-----BEGIN PGP PUBLIC KEY BLOCK-----

mDMEasWY0BYJKwYBBAHaRw8BAQdARpPrxzqfzgnaaUCavC+VJqJ57fDvwz7eHPZ3
sn0v6KC0QkZpcmVmbHkwNjE5c2VjIDwzMTEwOTc2NTYrRmlyZWZseTA2MTlzZWNA
dXNlcnMubm9yZXBseS5naXRodWIuY29tPoi4BBMWCgBgGxSAAAAAAAQADm1hbnUy
LDIuNSsxLjEyLDIsMQIbAwUJA8JnAAULCQgHAgIiAgYVCgkICwIEFgIDAQIeBwIX
gBYhBOv7a4ldjuyPWc4KJuI4dsu9rjMPBQJqx5uQAhkBAAoJEOI4dsu9rjMP/8AB
AIK/fi3Jr4CSJj7DEUcurSkHB7zFA0zcNsTWp8eic4cAAQDjm6oVdEVGNuP1yO37
5uHBNgDmq8381BsUQ9eRD/D+BbRCd2VpamlhbWluZzA1MDQgPDMxMTA5NzY1Nit3
ZWlqaWFtaW5nMDUwNEB1c2Vycy5ub3JlcGx5LmdpdGh1Yi5jb20+iLUEExYKAF0W
IQTr+2uJXY7sj1nOCibiOHbLva4zDwUCasWY0BsUgAAAAAAEAA5tYW51MiwyLjUr
MS4xMiwyLDECGwMFCQPCZwAFCwkIBwICIgIGFQoJCAsCBBYCAwECHgcCF4AACgkQ
4jh2y72uMw/X/gD8CS7EChBMgpwqqBFvvzE/Zpz7Uya4JtizUiWtjwkhTmMA/3rY
//9bjKX/xyU6DSv6vmVXseCRH2hSZQfYVMB69h0DuDgEasWY0BIKKwYBBAGXVQEF
AQEHQBr6tVcKt8d3w99dhZ6WHRvEK9FUsxZj4ER7w8bCIZ5sAwEIB4iaBBgWCgBC
FiEE6/triV2O7I9Zzgom4jh2y72uMw8FAmrFmNAbFIAAAAAABAAObWFudTIsMi41
KzEuMTIsMiwxAhsMBQkDwmcAAAoJEOI4dsu9rjMPRosBAOhz08Yl4pm1csBaP3k0
PJ7OHy9D9E8vmU67MfeBvL0HAP4xog8DYfeWj11sF09nS3nkYZgGSSKPVW+YA62d
6ihNDLgzBGrNC1MWCSsGAQQB2kcPAQEHQAq+FEUC7bqcb+nvokjCYg2Ubr/0NZGm
Dv/5LK0xSOloiQERBBgWCgBCFiEE6/triV2O7I9Zzgom4jh2y72uMw8FAmrNC1Mb
FIAAAAAABAAObWFudTIsMi41KzEuMTIsMiwxAhsCBQkDwmcAAIEJEOI4dsu9rjMP
diAEGRYKAB0WIQTNLe58qIXKZxFa1HFt8lvUojYjlgUCas0LUwAKCRBt8lvUojYj
llmPAP9cSSpkKv5cNW+p8dcQymbJZffpUtg64zQE7e86uc1vTwEAvMuGOkRX68kq
ok1hVzZjZ+0datsyVngNt8JdddS4eAiFQAEAqPRuGKGfUSrQqSWSaaxa/iMispFL
pkKoC36pF8d+HRQBAI71Q0swo3/ChNKKFnLeq1nSfd0BXbKLfmByh1l0zSUB
=UJ+h
-----END PGP PUBLIC KEY BLOCK-----
-----END PGP PUBLIC KEY BLOCK-----";

        private static string? _gpgPath;
        private static bool _probed;

        /// <summary>★ 找 gpg.exe（找不到就退回 HMAC 令牌方案）。</summary>
        public static string? FindGpg()
        {
            if (_probed) return _gpgPath;
            _probed = true;

            string[] candidates =
            {
                @"C:\Program Files\GnuPG\bin\gpg.exe",
                @"C:\Program Files (x86)\GnuPG\bin\gpg.exe",
                @"C:\Program Files\Gpg4win\bin\gpg.exe",
                @"C:\Program Files (x86)\Gpg4win\bin\gpg.exe",
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) { _gpgPath = c; return _gpgPath; }
            }
            // 再试 PATH
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "gpg.exe",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (var line in o.Split('\n'))
                    {
                        string s = line.Trim();
                        if (s.Length > 0 && File.Exists(s)) { _gpgPath = s; return _gpgPath; }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// ★★★ 校验一个【分离签名】。
        ///
        ///   dataPath  —— 被签名的内容（.stop 文件）
        ///   sigPath   —— 分离签名（.stop.asc）
        ///
        ///   返回 true 表示：签名有效【且】签名者就是主人 ✓
        /// </summary>
        public static bool VerifyDetached(string dataPath, string sigPath,
                                          Action<string>? log = null)
        {
            string? gpg = FindGpg();
            if (gpg == null)
            {
                log?.Invoke("[PGP] gpg.exe not found -> cannot verify signature");
                return false;
            }
            if (!File.Exists(dataPath) || !File.Exists(sigPath))
            {
                return false;
            }

            string home = Path.Combine(Path.GetTempPath(),
                "warden-gpg-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            try
            {
                Directory.CreateDirectory(home);

                // ★ 关键①：独立临时 GNUPGHOME，不碰用户密钥环 ✓
                string pubPath = Path.Combine(home, "owner.asc");
                File.WriteAllText(pubPath, OwnerPublicKey, new UTF8Encoding(false));

                if (!RunGpg(gpg, home, "--import \"" + pubPath + "\"", out string impOut, log))
                {
                    log?.Invoke("[PGP] import public key failed: " + Short(impOut));
                    return false;
                }

                // ---- 校验签名 ----
                //   --status-fd 1 让我们能拿到机器可读的结果 ✓
                string args = "--status-fd 1 --verify \"" + sigPath + "\" \"" + dataPath + "\"";
                RunGpg(gpg, home, args, out string outText, log);

                // ---- 关键③：必须看到 VALIDSIG 且指纹是我们的主人 ✓ ----
                var m = Regex.Match(outText, @"VALIDSIG\s+([0-9A-Fa-f]{40})");
                if (!m.Success)
                {
                    log?.Invoke("[PGP] no VALIDSIG in gpg output -> signature invalid");
                    if (outText.Contains("BADSIG")) log?.Invoke("[PGP] BADSIG: signature does not match");
                    if (outText.Contains("NO_PUBKEY")) log?.Invoke("[PGP] NO_PUBKEY: unknown signing key");
                    return false;
                }

                string fpr = m.Groups[1].Value.ToUpperInvariant();
                if (!string.Equals(fpr, OwnerFingerprint, StringComparison.Ordinal))
                {
                    // ★★★ 签名有效，但签名者不是主人 -> 拒绝 ✗
                    log?.Invoke("[PGP] signature valid but WRONG SIGNER: " + fpr);
                    return false;
                }

                log?.Invoke("[PGP] detached signature verified, signer=" + fpr.Substring(0, 16) + "…");
                return true;
            }
            catch (Exception e)
            {
                log?.Invoke("[PGP] verify exception: " + e.Message);
                return false;
            }
            finally
            {
                try { Directory.Delete(home, true); } catch { }
            }
        }

        private static bool RunGpg(string gpg, string home, string args,
                                   out string output, Action<string>? log)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = gpg,
                    Arguments = "--homedir \"" + home + "\" --batch --no-tty " + args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                p.WaitForExit(20000);
                output = so + "\n" + se;
                return p.ExitCode == 0;
            }
            catch (Exception e)
            {
                output = e.Message;
                return false;
            }
        }

        private static string Short(string s) =>
            s.Length > 160 ? s.Substring(0, 160) : s;

        /// <summary>
        /// ★ 导出内嵌公钥（给用户看 / 让用户能加密反馈用）。
        ///   ★ 公钥公开无害 ✓（用户说的）
        /// </summary>
        public static string GetOwnerPublicKey() => OwnerPublicKey;

        /// <summary>
        /// ★★★ 用主人的公钥【加密】一段内容（反馈用）。
        ///
        ///   用户原话：
        ///     "刚好可以用公钥扩展我的反馈选项，也就是对我的反馈进行加密。
        ///      这样别人看不见，反而还能加强隐私。"
        ///
        ///   -> 反馈内容用主人的公钥加密后提交，
        ///      即使贴在公开的 GitHub issue 上，别人也看不懂 ✓
        ///      只有主人能用私钥解开 ✓ 隐私反而更强 ✓
        /// </summary>
        public static bool Encrypt(string plainText, out string armored, Action<string>? log = null)
        {
            armored = "";
            string? gpg = FindGpg();
            if (gpg == null)
            {
                log?.Invoke("[PGP] gpg.exe not found -> cannot encrypt feedback");
                return false;
            }

            string home = Path.Combine(Path.GetTempPath(),
                "warden-gpg-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            try
            {
                Directory.CreateDirectory(home);
                string pubPath = Path.Combine(home, "owner.asc");
                File.WriteAllText(pubPath, OwnerPublicKey, new UTF8Encoding(false));

                if (!RunGpg(gpg, home, "--import \"" + pubPath + "\"", out _, log))
                {
                    return false;
                }

                string inPath = Path.Combine(home, "plain.txt");
                File.WriteAllText(inPath, plainText, new UTF8Encoding(false));

                // --trust-model always：我们本来就不走 web-of-trust，
                // 公钥是内嵌的，指纹我们自己比对 ✓
                string args = "--trust-model always --armor --encrypt "
                            + "--recipient " + OwnerFingerprint + " "
                            + "--output - \"" + inPath + "\"";
                RunGpg(gpg, home, args, out string outText, log);

                if (outText.Contains("BEGIN PGP MESSAGE"))
                {
                    // 只取 armored 部分（gpg 可能混进 stderr 的提示）
                    int i = outText.IndexOf("-----BEGIN PGP MESSAGE-----", StringComparison.Ordinal);
                    int j = outText.IndexOf("-----END PGP MESSAGE-----", StringComparison.Ordinal);
                    if (i >= 0 && j > i)
                    {
                        armored = outText.Substring(i, j - i + 25);
                        log?.Invoke("[PGP] feedback encrypted for owner only");
                        return true;
                    }
                }

                log?.Invoke("[PGP] encrypt failed");
                return false;
            }
            catch (Exception e)
            {
                log?.Invoke("[PGP] encrypt exception: " + e.Message);
                return false;
            }
            finally
            {
                try { Directory.Delete(home, true); } catch { }
            }
        }
    }
}
