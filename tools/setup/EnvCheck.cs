// ============================================================================
//  A34 环境检测
// ============================================================================
//  【用户要求】
//      "不支持的时候，要给出提示，而不是报错。"
//      "离线的时候要引导用户怎么办；在线的时候直接去取微软官方运行库。"
//
//  【★ 为什么安装器必须"先查再装"】
//      数字弱势群体最常卡住的地方，不是"装不上"，而是"装上了不知道哪里不对"：
//        ✗ 系统太老      → 主程序一闪就没了，用户以为是病毒 ✗
//        ✗ 盘快满了      → 解压到一半失败，只剩半截文件 ✗
//        ✗ 缺少系统组件  → 弹一个 0xc000007b，谁都看不懂 ✗
//      → 所以：装之前先查清，查完【用人话说清楚】，并且给出下一步怎么办 ✓
//
//  【★★ 一个实测结论（2026-10-10，写代码之前先验过）】
//      用二进制导入表实测 dist\app\WARDEN.exe，得到：
//        · 没有 vcruntime140.dll / MSVCP140.dll  → CRT 是【静态链接】的
//        · 只有 api-ms-win-crt-*.dll             → 那是 Windows 10 自带的 UCRT
//      → 所以提示里【绝对不能】写"你需要安装 VC++ 运行库"（那是错的 ✗）
//      → 正确结论：
//          Windows 10 / 11：什么都不用装 ✓
//          Windows 8.1 / 7：需要打 KB2999226 补上 UCRT ✓
//      （先验再写 —— 免得给用户一个"看起来专业但其实错误"的指引 ✗）
//
//  【★ 检测项一览】
//      ① 操作系统版本    ② 系统位数       ③ 目标盘剩余空间
//      ④ 系统运行库 UCRT ⑤ 要不要装 .NET  ⑥ 网络连通性（缺组件时才查）
//      ⑦ 是否已装旧版本  ⑧ 旧数据目录     ⑨ WARDEN 是否在运行
//      ⑩ 是否以管理员身份安装（这个反而是【要劝退】的）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace WardenSetup
{
    /// <summary>★ 检测结果等级（顺序即严重程度）。</summary>
    internal enum EnvLevel
    {
        /// <summary>✓ 通过。</summary>
        Ok,
        /// <summary>· 仅供参考的信息。</summary>
        Info,
        /// <summary>! 可以用，但建议看一下。</summary>
        Warn,
        /// <summary>✗ 不满足要求（默认拦住安装，但允许用户强行继续）。</summary>
        Block,
    }

    internal sealed class EnvItem
    {
        public string Id = string.Empty;       // 英文标记（写日志用）
        public string Name = string.Empty;     // 中文名（界面用）
        public EnvLevel Level = EnvLevel.Info;
        public string Detail = string.Empty;   // 现在是什么情况
        public string Advice = string.Empty;   // 怎么办（可以为空）
        public string LogLine = string.Empty;  // 英文日志行
    }

    internal sealed class EnvReport
    {
        public readonly List<EnvItem> Items = new List<EnvItem>();
        public string TargetDir = string.Empty;
        public string OsText = string.Empty;
        public string ArchText = string.Empty;
        public long FreeMB;
        public bool Online;
        public bool ProbeDone;
        public bool NeedComponent;      // 缺系统运行库
        public bool NeedDotnet;         // 需要用户另外装 .NET（当前=否，自带）

        public int BlockCount
        {
            get
            {
                int n = 0;
                foreach (var i in Items) if (i.Level == EnvLevel.Block) n++;
                return n;
            }
        }

        public int WarnCount
        {
            get
            {
                int n = 0;
                foreach (var i in Items) if (i.Level == EnvLevel.Warn) n++;
                return n;
            }
        }

        /// <summary>★ 能不能装（没有 Block 级问题）。</summary>
        public bool CanInstall { get { return BlockCount == 0; } }

        public string FreeText { get { return EnvCheck.Human(FreeMB); } }

        /// <summary>★ 一句话结论（给界面大字用）。</summary>
        public string Summary
        {
            get
            {
                if (BlockCount > 0)
                    return "✗ 有 " + BlockCount + " 项不满足要求 —— 看下面的说明";
                if (WarnCount > 0)
                    return "! 可以安装，但有 " + WarnCount + " 项建议先看一下";
                return "✓ 环境检测通过 —— " + OsText + " · " + ArchText + " · 剩余 " + FreeText;
            }
        }

        /// <summary>★ 结论用颜色（绿 / 橙 / 红）。</summary>
        public int SummaryTone
        {
            get
            {
                if (BlockCount > 0) return 2;
                if (WarnCount > 0) return 1;
                return 0;
            }
        }

        public static string Mark(EnvLevel l)
        {
            switch (l)
            {
                case EnvLevel.Ok: return "✓";
                case EnvLevel.Warn: return "!";
                case EnvLevel.Block: return "✗";
                default: return "·";
            }
        }

        /// <summary>★ 拼成多行文本（界面 / 控制台 / 日志共用同一份，避免两处不一致）。</summary>
        public string ToText(bool withAdvice)
        {
            var sb = new StringBuilder();
            foreach (var it in Items)
            {
                sb.Append(Mark(it.Level)).Append(' ').Append(it.Name)
                  .Append("：").Append(it.Detail).Append(Environment.NewLine);
                if (withAdvice && it.Advice.Length > 0)
                {
                    string adv = it.Advice.Replace(Environment.NewLine,
                        Environment.NewLine + "      ");
                    sb.Append("    → ").Append(adv).Append(Environment.NewLine);
                }
            }
            return sb.ToString();
        }
    }

    internal static class EnvCheck
    {
        // ---- 判定门槛 ----
        /// <summary>★ 硬门槛：Windows 10 1607（内部版本 14393）。</summary>
        public const int MinBuild = 14393;
        /// <summary>★ 建议门槛：Windows 10 1809（内部版本 17763）。</summary>
        public const int NiceBuild = 17763;
        /// <summary>★ 安装至少要留多少 MB。</summary>
        public const long NeedFreeMB = 350;
        /// <summary>★ 舒服的余量。</summary>
        public const long ComfortFreeMB = 800;

        // ---- 官方链接（★ 只给官方，不给第三方下载站）----
        public const string RuntimeExeUrl =
            "https://aka.ms/vs/17/release/vc_redist.x64.exe";
        public const string RuntimePageUrl =
            "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist";
        public const string UcrtKbUrl =
            "https://support.microsoft.com/help/2999226";
        public const string DotnetPageUrl =
            "https://dotnet.microsoft.com/download/dotnet/10.0";

        /// <summary>
        /// ★★★ 主入口：跑一遍全部检测。
        /// </summary>
        /// <param name="targetDir">打算装到哪（影响磁盘检测）。</param>
        /// <param name="probeNetwork">要不要测网络（缺组件时才需要，见调用方）。</param>
        public static EnvReport Run(string targetDir, bool probeNetwork)
        {
            var r = new EnvReport();
            r.TargetDir = targetDir;
            SetupLog.Begin("env check  target=" + targetDir);

            CheckOs(r);
            CheckArch(r);
            CheckDisk(r, targetDir);
            CheckRuntime(r);
            CheckDotnetStory(r);
            if (probeNetwork) CheckNetwork(r);
            CheckExistingInstall(r);
            CheckLegacyData(r);
            CheckRunning(r);
            CheckElevation(r);

            SetupLog.Line("ENV", "result can_install=" + r.CanInstall
                + " blocks=" + r.BlockCount + " warns=" + r.WarnCount
                + " online=" + (r.ProbeDone ? r.Online.ToString() : "not-probed"));
            return r;
        }

        private static void Add(EnvReport r, EnvItem it)
        {
            r.Items.Add(it);
            SetupLog.Line("ENV." + it.Id,
                it.LogLine + "  level=" + it.Level);
        }

        // ================================================================
        //  ① 操作系统
        // ================================================================
        private static void CheckOs(EnvReport r)
        {
            int maj = 0, build = 0;
            GetRealOsVersion(out maj, out build);
            string name = FriendlyOs(maj, build);
            r.OsText = name;

            var it = new EnvItem { Id = "os", Name = "操作系统" };
            it.Detail = name + "（内部版本 " + build + "）";

            if (build == 0)
            {
                it.Level = EnvLevel.Warn;
                it.Detail = "无法确认系统版本";
                it.Advice = "这不影响安装，但如果之后 WARDEN 起不来，"
                    + "请把这个日志文件发给我：" + Environment.NewLine + SetupLog.FilePath;
            }
            else if (build < MinBuild)
            {
                it.Level = EnvLevel.Block;
                it.Advice = "WARDEN 需要 Windows 10（内部版本 14393 / 1607）"
                    + "或更新的系统。" + Environment.NewLine
                    + "你现在的系统比较老，装了也起不来 —— "
                    + "所以这里先拦住你，而不是让你装完再失败。";
            }
            else if (build < NiceBuild)
            {
                it.Level = EnvLevel.Warn;
                it.Advice = "能装，但系统偏老（低于 1809）。"
                    + "部分界面细节可能显示不正常，方便的话建议升级系统。";
            }
            else
            {
                it.Level = EnvLevel.Ok;
            }

            it.LogLine = "os=" + name + " major=" + maj + " build=" + build;
            Add(r, it);
        }

        /// <summary>
        /// ★★ 取【真实】系统版本。
        ///
        /// 为什么不能直接用 Environment.OSVersion：
        ///   没有声明支持的清单时，Windows 会做版本谎报（返回 6.2）✗
        ///   WARDEN 的安装器没有 app.manifest → 只能直接问 ntdll ✓
        /// </summary>
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW info);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RTL_OSVERSIONINFOW
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        private static void GetRealOsVersion(out int major, out int build)
        {
            major = 0;
            build = 0;
            try
            {
                var info = new RTL_OSVERSIONINFOW();
                info.dwOSVersionInfoSize = Marshal.SizeOf(typeof(RTL_OSVERSIONINFOW));
                info.szCSDVersion = string.Empty;
                if (RtlGetVersion(ref info) == 0)
                {
                    major = info.dwMajorVersion;
                    build = info.dwBuildNumber;
                    return;
                }
            }
            catch
            {
                // ★ P/Invoke 失败不要紧，下面用 Environment.OSVersion 兜底 ✓
            }
            try
            {
                var v = Environment.OSVersion.Version;
                major = v.Major;
                build = v.Build;
            }
            catch { }
        }

        private static string FriendlyOs(int major, int build)
        {
            if (build >= 22000) return "Windows 11";
            if (build >= 10240) return "Windows 10";
            if (build >= 9600) return "Windows 8.1";
            if (build >= 9200) return "Windows 8";
            if (build >= 7600) return "Windows 7";
            if (major >= 10) return "Windows " + major;
            return "Windows（内部版本 " + build + "）";
        }

        // ================================================================
        //  ② 系统位数
        // ================================================================
        private static void CheckArch(EnvReport r)
        {
            var arch = RuntimeInformation.OSArchitecture;
            var it = new EnvItem { Id = "arch", Name = "系统位数" };

            if (arch == Architecture.X86)
            {
                r.ArchText = "32 位（x86）";
                it.Level = EnvLevel.Block;
                it.Detail = "32 位系统";
                it.Advice = "WARDEN 只提供 64 位版本。"
                    + "32 位系统装不了 —— 这不是你操作的问题。";
            }
            else if (arch == Architecture.Arm64)
            {
                r.ArchText = "ARM64";
                it.Level = EnvLevel.Warn;
                it.Detail = "ARM64 设备";
                it.Advice = "能用（Windows 会以模拟方式运行 64 位程序），"
                    + "但速度会比普通电脑慢一些。";
            }
            else if (arch == Architecture.X64)
            {
                r.ArchText = "64 位（x64）";
                it.Level = EnvLevel.Ok;
                it.Detail = "64 位（x64）";
            }
            else
            {
                r.ArchText = arch.ToString();
                it.Level = EnvLevel.Warn;
                it.Detail = arch.ToString();
                it.Advice = "没有见过这种架构，如果装完起不来请把这个日志发给我。";
            }

            it.LogLine = "os_arch=" + arch + " process_arch="
                + RuntimeInformation.ProcessArchitecture;
            Add(r, it);
        }

        // ================================================================
        //  ③ 磁盘空间
        // ================================================================
        private static void CheckDisk(EnvReport r, string targetDir)
        {
            var it = DiskItem(targetDir);
            r.FreeMB = _lastFreeMB;
            Add(r, it);
        }

        private static long _lastFreeMB;

        /// <summary>
        /// ★ 单独可调用的磁盘检测（用户在第 2 页改了路径之后要重新查一遍）。
        /// </summary>
        public static EnvItem DiskItem(string targetDir)
        {
            var it = new EnvItem { Id = "disk", Name = "磁盘空间" };
            _lastFreeMB = 0;

            try
            {
                string full = Path.GetFullPath(Installer.Expand(targetDir));
                string root = Path.GetPathRoot(full) ?? "C:\\";
                var di = new DriveInfo(root);
                long freeMB = di.AvailableFreeSpace / (1024L * 1024L);
                long totalMB = di.TotalSize / (1024L * 1024L);
                _lastFreeMB = freeMB;

                it.Detail = root + " 剩余 " + Human(freeMB)
                    + "（共 " + Human(totalMB) + "）";
                it.LogLine = "disk root=" + root + " free_mb=" + freeMB
                    + " total_mb=" + totalMB;

                if (freeMB < NeedFreeMB)
                {
                    it.Level = EnvLevel.Block;
                    it.Advice = "空间不够。WARDEN 装完大约占 150 MB，"
                        + "但解压过程还会临时占用一倍左右，"
                        + "所以至少需要 " + NeedFreeMB + " MB 空余。" + Environment.NewLine
                        + "建议先清理一下，或者在第 2 页把安装位置换到别的盘。";
                }
                else if (freeMB < ComfortFreeMB)
                {
                    it.Level = EnvLevel.Warn;
                    it.Advice = "空间比较紧。装是能装下，"
                        + "但以后自动更新时可能会因为临时空间不足失败，"
                        + "建议清出 " + ComfortFreeMB + " MB 以上。";
                }
                else
                {
                    it.Level = EnvLevel.Ok;
                }
            }
            catch (Exception e)
            {
                it.Level = EnvLevel.Warn;
                it.Detail = "读不到磁盘信息（" + e.Message + "）";
                it.Advice = "不影响安装，但如果装到一半失败，"
                    + "多半是磁盘或权限问题 —— 可以在第 2 页换一个位置试试。";
                it.LogLine = "disk error=" + e.Message;
            }

            return it;
        }

        /// <summary>★ 把 MB 说成人话。</summary>
        public static string Human(long mb)
        {
            if (mb >= 1024) return (mb / 1024.0).ToString("0.#") + " GB";
            if (mb <= 0) return "未知";
            return mb + " MB";
        }

        // ================================================================
        //  ④ 系统运行库（UCRT）
        // ================================================================
        /// <summary>
        /// ★★★ 这几个 DLL 就是 dist\app\WARDEN.exe 导入表里真实出现过的
        ///      （见文件头的实测结论）。
        ///      Windows 10/11 自带；Windows 7/8.1 需要 KB2999226 才有。
        /// </summary>
        private static readonly string[] UcrtNeeded =
        {
            "api-ms-win-crt-runtime-l1-1-0.dll",
            "api-ms-win-crt-stdio-l1-1-0.dll",
            "api-ms-win-crt-heap-l1-1-0.dll",
            "api-ms-win-crt-string-l1-1-0.dll",
            "api-ms-win-crt-convert-l1-1-0.dll",
            "api-ms-win-crt-math-l1-1-0.dll",
            "api-ms-win-crt-locale-l1-1-0.dll",
        };

        private static void CheckRuntime(EnvReport r)
        {
            var it = new EnvItem { Id = "runtime", Name = "系统运行库" };
            var missing = new List<string>();

            try
            {
                string sys = Environment.GetFolderPath(
                    Environment.SpecialFolder.System);
                foreach (var n in UcrtNeeded)
                {
                    if (!File.Exists(Path.Combine(sys, n))) missing.Add(n);
                }
            }
            catch (Exception e)
            {
                it.Level = EnvLevel.Warn;
                it.Detail = "无法确认（" + e.Message + "）";
                it.LogLine = "runtime error=" + e.Message;
                Add(r, it);
                return;
            }

            if (missing.Count == 0)
            {
                it.Level = EnvLevel.Ok;
                it.Detail = "齐全（Windows 自带，不需要另外安装）";
                it.LogLine = "runtime ucrt=ok missing=0";
            }
            else
            {
                r.NeedComponent = true;
                it.Level = EnvLevel.Warn;
                it.Detail = "缺少 " + missing.Count + " 个系统组件";
                it.Advice = "具体缺的是：" + string.Join("、", missing.ToArray())
                    + Environment.NewLine
                    + "这是 Windows 的『通用 C 运行库』。"
                    + "Windows 10 以上一般自带；老系统需要打微软的 KB2999226 补丁。"
                    + Environment.NewLine
                    + "★ 处理办法有两种，任选一种：" + Environment.NewLine
                    + "  1) 点下面『打开官方下载页』——"
                    + "在浏览器里下载微软官方的运行库，装完回来点『重新检测』；"
                    + Environment.NewLine
                    + "  2) 直接照抄这个网址给别的设备：" + Environment.NewLine
                    + "     " + RuntimeExeUrl;
                it.LogLine = "runtime ucrt=missing count=" + missing.Count;
            }

            Add(r, it);
        }

        // ================================================================
        //  ⑤ 要不要装 .NET
        // ================================================================
        private static void CheckDotnetStory(EnvReport r)
        {
            var it = new EnvItem { Id = "dotnet", Name = "要不要装 .NET" };
            it.Level = EnvLevel.Info;
            it.Detail = "不需要 —— WARDEN 自带一份 .NET 运行时";
            it.Advice = "很多人装软件卡在这一步（去微软官网下 .NET，"
                + "下错版本、装完还是打不开）。" + Environment.NewLine
                + "WARDEN 把需要的运行时一起打包了，你什么都不用装。"
                + Environment.NewLine
                + "代价是安装包大一些（约 95 MB），换来的是"
                + "『双击就能用』。";
            it.LogLine = "dotnet self_contained=yes need_install=no";
            Add(r, it);
        }

        // ================================================================
        //  ⑥ 网络（只在缺组件时才值得查）
        // ================================================================
        private static void CheckNetwork(EnvReport r)
        {
            bool online = ProbeNetwork();
            r.Online = online;
            r.ProbeDone = true;

            var it = new EnvItem { Id = "net", Name = "网络" };
            if (online)
            {
                it.Level = EnvLevel.Ok;
                it.Detail = "可以联网";
                if (r.NeedComponent)
                {
                    it.Advice = "上面缺的那个运行库，"
                        + "现在可以直接点『打开官方下载页』去微软官网取。";
                }
            }
            else
            {
                it.Level = r.NeedComponent ? EnvLevel.Warn : EnvLevel.Info;
                it.Detail = "现在连不上网（或网速极慢）";
                it.Advice = "不影响安装 —— WARDEN 本体不联网。"
                    + Environment.NewLine
                    + "但如果上面提示缺系统运行库，就需要联网下载。"
                    + "可以去别的能上网的电脑 / 手机上打开这个网址："
                    + Environment.NewLine
                    + "  " + RuntimeExeUrl + Environment.NewLine
                    + "把下载到的 vc_redist.x64.exe 用 U 盘拷过来，"
                    + "双击装完，再回来点『重新检测』。";
            }

            it.LogLine = "network online=" + online;
            Add(r, it);
        }

        /// <summary>
        /// ★ 测网络。
        ///
        /// 【为什么用这两个地址】
        ///   ① 微软自己的连通性测试（Windows 判断"有没有网"就用它）
        ///   ② 真正要下载的那个运行库地址（能连上就一定能下载）
        /// 【为什么失败不算错误】
        ///   有些人开着代理/防火墙，程序连不上但浏览器能上
        ///   → 所以文案只说"现在连不上"，不说"你没有网络" ✓
        /// </summary>
        public static bool ProbeNetwork()
        {
            string[] urls =
            {
                "http://www.msftconnecttest.com/connecttest.txt",
                RuntimeExeUrl,
            };
            foreach (var u in urls)
            {
                try
                {
                    using var hc = new HttpClient();
                    hc.Timeout = TimeSpan.FromSeconds(4);
                    using var req = new HttpRequestMessage(HttpMethod.Get, u);
                    using var resp = hc.Send(req, HttpCompletionOption.ResponseHeadersRead);
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch
                {
                    // 换下一个地址再试
                }
            }
            return false;
        }

        // ================================================================
        //  ⑦ 是否已经装过
        // ================================================================
        private static void CheckExistingInstall(EnvReport r)
        {
            var it = new EnvItem { Id = "installed", Name = "已有版本" };
            string? ver = null;
            string? loc = null;
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WARDEN");
                if (k != null)
                {
                    ver = k.GetValue("DisplayVersion") as string;
                    loc = k.GetValue("InstallLocation") as string;
                }
            }
            catch { }

            if (ver != null && ver.Length > 0)
            {
                it.Level = EnvLevel.Info;
                it.Detail = "已经装了 " + ver
                    + (string.IsNullOrEmpty(loc) ? "" : "（" + loc + "）")
                    + " —— 这次是覆盖安装";
                it.Advice = "你的配置和日志都会保留，不会丢。";
                it.LogLine = "installed version=" + ver + " dir=" + loc;
            }
            else
            {
                it.Level = EnvLevel.Info;
                it.Detail = "没装过 —— 这是全新安装";
                it.LogLine = "installed none";
            }

            Add(r, it);
        }

        // ================================================================
        //  ⑧ 旧数据目录
        // ================================================================
        private static void CheckLegacyData(EnvReport r)
        {
            try
            {
                string lad = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                string legacy = Path.Combine(lad, "dsh-tools", "WARDEN.json");
                string current = Path.Combine(lad, "WARDEN", "WARDEN.json");
                if (File.Exists(legacy) && !File.Exists(current))
                {
                    var it = new EnvItem { Id = "legacy", Name = "旧版配置" };
                    it.Level = EnvLevel.Info;
                    it.Detail = "发现旧版本留下的配置文件";
                    it.Advice = "第一次启动 WARDEN 时会自动搬过去，"
                        + "你的设置不会丢。";
                    it.LogLine = "legacy config found -> will migrate";
                    Add(r, it);
                }
            }
            catch { }
        }

        // ================================================================
        //  ⑨ WARDEN 是否在运行
        // ================================================================
        private static void CheckRunning(EnvReport r)
        {
            var it = new EnvItem { Id = "running", Name = "运行状态" };
            try
            {
                var procs = Process.GetProcessesByName("WARDEN");
                if (procs.Length > 0)
                {
                    it.Level = EnvLevel.Warn;
                    it.Detail = "WARDEN 正在运行（" + procs.Length + " 个进程）";
                    it.Advice = "安装前必须先让它退出，否则文件被占用会覆盖失败。"
                        + Environment.NewLine
                        + "这一步是自动的：安装程序会给它发一个"
                        + "带签名的『停止请求』，让它自己收工。"
                        + Environment.NewLine
                        + "如果它 15 秒还没退，会明确提示你手动退出"
                        + "（不是默默失败）。";
                    it.LogLine = "running count=" + procs.Length;
                }
                else
                {
                    it.Level = EnvLevel.Ok;
                    it.Detail = "WARDEN 没有在运行";
                    it.LogLine = "running count=0";
                }
            }
            catch (Exception e)
            {
                it.Level = EnvLevel.Info;
                it.Detail = "无法查询进程（" + e.Message + "）";
                it.LogLine = "running error=" + e.Message;
            }
            Add(r, it);
        }

        // ================================================================
        //  ⑩ 管理员身份（★ 这个反而是要劝退的）
        // ================================================================
        private static void CheckElevation(EnvReport r)
        {
            var it = new EnvItem { Id = "elevation", Name = "安装身份" };
            bool admin = false;
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                admin = new WindowsPrincipal(id)
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { }

            if (admin)
            {
                it.Level = EnvLevel.Warn;
                it.Detail = "当前是【管理员】身份";
                it.Advice = "WARDEN 默认装在用户目录，【不需要】管理员权限 ✓"
                    + Environment.NewLine
                    + "以管理员安装会让快捷方式、开机自启、卸载登记项"
                    + "都记到管理员账户名下，"
                    + "你平时用普通账户会发现『装过但找不到』。"
                    + Environment.NewLine
                    + "建议：关掉这个窗口，直接双击安装包运行"
                    + "（不要右键『以管理员身份运行』）。";
                it.LogLine = "elevation admin=true (not recommended)";
            }
            else
            {
                it.Level = EnvLevel.Ok;
                it.Detail = "普通用户权限（推荐）";
                it.LogLine = "elevation admin=false";
            }
            Add(r, it);
        }
    }
}
