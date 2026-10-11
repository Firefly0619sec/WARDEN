// ============================================================================
//  WARDEN 更新器（事故后重写，2026-10-10）
// ============================================================================
//  【设计原则】
//    ① 【离线包优先】用户下载一个 zip → 这个工具负责装上去 ✓
//       为什么：WARDEN 本体是"不联网"的（只有这个更新器例外 ✓），
//       而且用户机器上不一定能直连 GitHub ✗（我们自己的机器就是 ✗）
//    ② 装之前【先校验哈希】（zip 同目录下的 <zip名>.sha256 ✓）
//       不校验就覆盖，等于给了别人一个"投毒"的机会 ✗
//    ③ 装之前【先备份】现在的程序目录（压成 zip 放数据目录 ✓）
//       更新失败可以一键回退 ✓ —— 这是用户明确要的"方便回滚" ✓
//    ④ 更新时【不动用户数据】（配置/日志/证据都在 %LOCALAPPDATA%\WARDEN ✓）
//    ⑤ 全程写日志（setup.log ✓）
//
//  【在线检查更新】等 WARDEN 仓库建好后做（F3）✓
//    那时这里会加一个"检查更新"按钮去读 releases 里的 version.txt ✓
//
//  命令行：
//    Updater.exe <更新包.zip> [--silent] [--no-launch]
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
// 0.3.1：不再依赖 WinForms（改用 UpdaterWin32.cs 的原生接口）以便 Native AOT
using WardenSetup;   // ★ 复用安装器的 StopToken（写合法停止令牌）✓

namespace WardenUpdater
{
    internal static class Program
    {
        private static readonly string InstallDir = AppContext.BaseDirectory.TrimEnd('\\');
        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");
        private static readonly string LogPath = Path.Combine(DataDir, "setup.log");

        public static void Log(string s)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  [更新] " + s;
            Console.WriteLine(line);
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(LogPath, line + Environment.NewLine,
                                   new UTF8Encoding(false));
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            string? zip = null;
            bool silent = false, noLaunch = false;
            foreach (string a in args)
            {
                if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase)) { silent = true; }
                else if (a.Equals("--no-launch", StringComparison.OrdinalIgnoreCase)) { noLaunch = true; }
                else if (!a.StartsWith("--", StringComparison.Ordinal)) { zip = a; }
            }

            Log("================ WARDEN 更新开始 ================");
            Log("程序目录: " + InstallDir);

            if (zip == null)
            {
                if (silent)
                {
                    Log("★ 静默模式必须给更新包路径 ✗");
                    Environment.Exit(2);
                    return;
                }
                // 0.3.1：改用系统原生「打开」对话框（comdlg32.dll ✓ AOT 安全 ✓）
                //   过滤串用 \0 分隔（原生格式 ✓）不再是 WinForms 的竖线格式
                string? picked = Win32Ui.PickZipFile(
                    "选择 WARDEN 更新包（zip）",
                    "WARDEN 更新包 (*.zip)\0*.zip\0所有文件\0*.*\0");
                if (string.IsNullOrEmpty(picked))
                {
                    Log("用户取消");
                    return;
                }
                zip = picked!;
            }

            if (!File.Exists(zip))
            {
                Log("★ 找不到更新包：" + zip);
                Environment.Exit(3);
                return;
            }

            // ---- ① 校验哈希（有 .sha256 就必须对 ✓）
            if (!VerifyHash(zip))
            {
                Log("★★ 哈希校验不通过 → 拒绝更新 ✗（这个包可能被篡改过）");
                Environment.Exit(5);
                return;
            }

            // ---- ② 备份现在的程序目录（方便回滚 ✓）
            string backup = Backup();

            // ---- ③ 请 WARDEN 退出（写合法令牌 ✓）
            if (!StopWarden())
            {
                Log("★ WARDEN 没能退出 → 中止更新（别在半途覆盖文件 ✗）");
                Environment.Exit(6);
                return;
            }

            // ---- ④ 解压覆盖（不碰数据目录 ✓）
            if (!Apply(zip))
            {
                Log("★ 解压失败 —— 现在可以用备份回退：" + backup);
                Environment.Exit(7);
                return;
            }

            Log("更新完成 ✓  备份：" + backup);

            if (!noLaunch)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, "WARDEN.exe"))
                    {
                        WorkingDirectory = InstallDir,
                        UseShellExecute = true,
                    });
                    Log("已重新启动 WARDEN ✓");
                }
                catch (Exception e)
                {
                    Log("★ 启动失败: " + e.Message);
                }
            }

            if (!silent)
            {
                Win32Ui.Msg("更新完成 ✓\n\n如果新版有问题，可以回退到这一次的备份：\n" + backup,
                    "WARDEN 更新", true);
            }
            Environment.Exit(0);
        }

        /// <summary>★ 同名 .sha256 存在就必须校验（格式：一行十六进制）✓</summary>
        private static bool VerifyHash(string zip)
        {
            string sidecar = zip + ".sha256";
            if (!File.Exists(sidecar))
            {
                Log("没有找到 " + Path.GetFileName(sidecar) + " → 跳过哈希校验（建议作者提供 ✓）");
                return true;
            }
            try
            {
                string want = File.ReadAllText(sidecar).Trim().Split(' ', '\t')[0].ToLowerInvariant();
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(zip);
                string got = BitConverter.ToString(sha.ComputeHash(fs))
                                      .Replace("-", "").ToLowerInvariant();
                Log("期望哈希: " + want);
                Log("实际哈希: " + got);
                return want == got;
            }
            catch (Exception e)
            {
                Log("★ 校验出错 → 按不通过处理: " + e.Message);
                return false;
            }
        }

        /// <summary>★ 备份程序目录到数据目录（zip），返回备份路径 ✓</summary>
        private static string Backup()
        {
            string outZip = Path.Combine(DataDir,
                "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            try
            {
                Directory.CreateDirectory(DataDir);
                if (File.Exists(outZip)) { File.Delete(outZip); }
                ZipFile.CreateFromDirectory(InstallDir, outZip,
                    CompressionLevel.Optimal, includeBaseDirectory: false);
                Log("已备份当前程序目录 → " + outZip);
            }
            catch (Exception e)
            {
                Log("★ 备份失败（继续更新，但没有回退点 ✗）: " + e.Message);
                return "(备份失败)";
            }
            return outZip;
        }

        private static bool StopWarden()
        {
            Process[] ps = Process.GetProcessesByName("WARDEN");
            if (ps.Length == 0)
            {
                return true;
            }
            Log("WARDEN 正在运行 → 写合法令牌请它退出");
            try
            {
                string stop = Path.Combine(DataDir, "WARDEN.stop");
                Directory.CreateDirectory(DataDir);
                if (!StopToken.WriteRequest(DataDir, stop, Log))
                {
                    Log("★ 停止令牌生成失败");
                    return false;
                }
            }
            catch (Exception e)
            {
                Log("★ 写停止请求失败: " + e.Message);
                return false;
            }
            for (int i = 0; i < 20; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (Process.GetProcessesByName("WARDEN").Length == 0)
                {
                    Log("WARDEN 已退出 ✓");
                    return true;
                }
            }
            return false;
        }


        /// <summary>★ 刷新自检基线（2026-10-11 回归测试发现的【阻断级】bug ✗）
        ///   WARDEN.json 里 expected_self_hash 记的是【上一个版本 exe】的 SHA256 ✓
        ///   升级/更新换掉 exe 后哈希必然变 → 主程序自检判定"被篡改"✗ → 直接拒绝运行 ✗✗
        ///   （用户看到的是"装了新版本，程序打不开了"✗ 只能靠环境变量临时绕过 ✓）
        ///   所以：拷完新 exe，必须把它的哈希写回配置 ✓
        ///   只改这一个字段 ✗ 用正则定点替换 ✓ 不解析不重写整个 JSON ✓（保住用户其它设置 ✓）</summary>
        internal static void RefreshSelfBaseline(string installDir, Action<string>? note = null)
        {
            try
            {
                string exe = Path.Combine(installDir, "WARDEN.exe");
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDEN");
                string cfg = Path.Combine(dataDir, "WARDEN.json");
                if (!File.Exists(exe)) { note?.Invoke("自检基线：没有 WARDEN.exe ✓ 跳过"); return; }
                if (!File.Exists(cfg)) { note?.Invoke("自检基线：还没有配置文件 ✓ 首次运行会自动写入 ✓"); return; }

                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = File.OpenRead(exe))
                {
                    hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                }

                string txt = File.ReadAllText(cfg);
                var re = new System.Text.RegularExpressions.Regex(
                    "(\"expected_self_hash\"\\s*:\\s*\")([0-9a-fA-F]{32,})(\")");
                if (!re.IsMatch(txt))
                {
                    note?.Invoke("自检基线：配置里没有 expected_self_hash ✓ 首次运行会自动写入 ✓");
                    return;
                }
                // ★★★ 2026-10-11 信息安全体检抓到的【数据破坏级】bug ✗✗（与安装器同源 ✗）
                //   原来替换串写成  "$1" + hash + "$3"  ✗
                //   .NET 的 Regex.Replace 会把 $ 后面【连续数字】整段当成组号 ✗
                //   → 新哈希以数字开头（本次 4164b4df…）就拼成 "$14164b4df…" ✗
                //   → 没有 14164 号捕获组 → 按字面量原样吐出 ✗
                //   → 配置那行变成   $14164b4dfe8523da…a5b8",   ✗ 键名与引号一起被吃掉 ✗
                //   → JSON 语法错误 ✗ 主程序下次读配置直接失败 ✗（用户："更新完就打不开了"✗）
                //   ★ 这段逻辑在 setup 与 updater 里各抄了一份 ✗ 所以我上一轮只修了 setup ✓
                //     这一轮按体检清单把 updater 这份也修掉 ✓（并加回归闸门防再犯 ✓）
                //   修法两条：① 组引用一律加花括号 ${1} / ${3} ✓
                //             ② 替换后【自检】✗ 不满足就一个字都不写 ✓（宁可不动也不能写坏 ✓）
                string now = re.Replace(txt, "${1}" + hash + "${3}", 1);
                if (!System.Text.RegularExpressions.Regex.IsMatch(
                        now, "\"expected_self_hash\"\\s*:\\s*\"" + hash + "\""))
                {
                    note?.Invoke("★ 自检基线：替换后自检没过 → 不写配置文件 ✓（绝不写坏用户的配置 ✓）");
                    return;
                }
                if (now == txt && txt.Contains(hash))
                {
                    note?.Invoke("自检基线：已经是最新的 ✓");
                    return;
                }
                // ★ 写之前先留一份原样备份 ✓（真出事能一字不差退回去 ✓）
                try { File.Copy(cfg, cfg + ".setup-bak", true); } catch { }
                File.WriteAllText(cfg, now);
                note?.Invoke("✓ 已把自检基线更新为新版本 exe 的 SHA256（" + hash.Substring(0, 16) + "…）");
            }
            catch (Exception e)
            {
                note?.Invoke("自检基线更新失败（不影响安装 ✓）：" + e.Message);
            }
        }

        private static bool Apply(string zip)
        {
            try
            {
                // ★★★ 2026-10-11 信息安全体检新增：反降级门限 ✗
                //   【为什么】更新包里 WARDEN.exe 的版本必须【不低于】当前已安装的版本 ✓
                //   → 否则拿到一个旧版本包就等于被降级 ✗（旧版可能带着已知漏洞 ✓
                //     而且它会把 expected_self_hash 也改成旧 exe 的 ✓ 用户完全看不出来 ✓）
                //   完全无法比对（读不到版本）时【不拦】✓ 只记一条日志 ✓（不误伤正常升级 ✓）
                if (!CheckNotDowngrade(zip)) { return false; }
                using var archive = ZipFile.OpenRead(zip);
                int n = 0;
                string root = Path.GetFullPath(InstallDir);
                foreach (ZipArchiveEntry e in archive.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name))
                    {
                        continue;
                    }
                    string target = Path.GetFullPath(Path.Combine(InstallDir, e.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("★ 跳过越界条目：" + e.FullName);
                        continue;
                    }
                    string? parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) { Directory.CreateDirectory(parent); }
                    e.ExtractToFile(target, overwrite: true);
                    // ★ 同安装器：时间设为现在，否则主程序的自检自愈不触发 ✗
                    try { File.SetLastWriteTimeUtc(target, DateTime.UtcNow); } catch { }
                    n++;
                }
                Log("已解压 " + n + " 个文件 ✓");
            RefreshSelfBaseline(InstallDir);   // ★ 刷新自检基线（更新后必须 ✓）
                return n > 0;
            }
            catch (Exception e)
            {
                Log("★ 解压失败: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// ★ 反降级门限（2026-10-11 信息安全体检新增 ✓）
        ///   规则：包里 WARDEN.exe 的 FileVersion 必须 ≥ 已安装 WARDEN.exe 的 ✓
        ///        读不到任何一边 → 放行（只记日志 ✓ 绝不误伤正常升级 ✓）
        ///       新版更低 → 拒绝更新 + 明确告诉用户 ✓（不静默 ✗）
        /// </summary>
        private static bool CheckNotDowngrade(string zip)
        {
            try
            {
                string installed = Path.Combine(InstallDir, "WARDEN.exe");
                if (!File.Exists(installed)) { Log("反降级：还没有已安装的 WARDEN.exe ✓ 跳过比对"); return true; }
                var vOld = FileVersionInfo.GetVersionInfo(installed).FileVersion;
                if (string.IsNullOrWhiteSpace(vOld)) { Log("反降级：读不到已安装版本 ✓ 跳过比对"); return true; }

                // 从 zip 里把 WARDEN.exe 读成临时文件再读版本（不改动安装目录 ✓）
                string tmp = Path.Combine(Path.GetTempPath(), "warden-upd-probe-" + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    using (var a = ZipFile.OpenRead(zip))
                    {
                        ZipArchiveEntry? e = null;
                        foreach (var x in a.Entries)     // ★ 不引 System.Linq ✓ 手写循环 ✓
                        {
                            if (string.Equals(x.FullName, "WARDEN.exe", StringComparison.OrdinalIgnoreCase)) { e = x; break; }
                        }
                        if (e == null) { Log("反降级：包里没有 WARDEN.exe ✓ 跳过比对"); return true; }
                        e.ExtractToFile(tmp, overwrite: true);
                    }
                    var vNew = FileVersionInfo.GetVersionInfo(tmp).FileVersion;
                    if (string.IsNullOrWhiteSpace(vNew)) { Log("反降级：读不到包里版本 ✓ 跳过比对"); return true; }
                    var a1 = new Version(vOld.Split('+')[0]);
                    var a2 = new Version(vNew.Split('+')[0]);
                    if (a2 < a1)
                    {
                        Log("★★ 拒绝更新：这是【降级】✗ 包里 " + vNew + " < 已安装 " + vOld
                            + " ✓ 已中止，一个文件都没动 ✓");
                        Win32Ui.Msg("拒绝更新：这是一个更旧的版本 ✗\n\n包里：" + vNew + "\n已装：" + vOld
                             + "\n\n降级会让已知问题回来 ✓ 已中止，没有改动任何文件 ✓", "WARDEN 更新器", true);
                        return false;
                    }
                    Log("反降级检查通过 ✓ 包里 " + vNew + " ≥ 已装 " + vOld);
                    return true;
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            catch (Exception e)
            {
                Log("反降级检查异常（放行 ✓）：" + e.Message);
                return true;
            }
        }
    }
}
