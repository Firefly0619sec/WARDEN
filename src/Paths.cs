// ============================================================================
//  路径解析（B3 便携版支持，2026-10-10）
// ============================================================================
//  【约定】
//    exe 同目录下如果有 WARDEN.portable 这个文件
//      → 数据目录 = <exe目录>\data（便携模式，不写 %LOCALAPPDATA% ✓）
//    否则
//      → 数据目录 = %LOCALAPPDATA%\WARDEN（安装版，默认 ✓）
//
//  【为什么要有它】
//    用户要把 WARDEN 放 U 盘/绿色使用 ✓
//    但日志、配置、证据都写进 %LOCALAPPDATA% 的话，拔了 U 盘还留一地文件 ✗
// ============================================================================

using System;
using System.IO;

namespace WARDEN
{
    internal static class WardenPaths
    {
        public const string PortableMarker = "WARDEN.portable";

        /// <summary>★ 按"有没有便携标记"决定真正的数据目录 ✓</summary>
        public static string ResolveDataDir(string fallback)
        {
            try
            {
                string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
                if (File.Exists(Path.Combine(exeDir, PortableMarker)))
                {
                    return Path.Combine(exeDir, "data");
                }
            }
            catch
            {
                // 判断不了就用默认值 ✓
            }
            return fallback;
        }

        /// <summary>★ 便携模式？（界面/日志里说明用）✓</summary>
        public static bool IsPortable
        {
            get
            {
                try
                {
                    return File.Exists(Path.Combine(
                        AppContext.BaseDirectory.TrimEnd('\\'), PortableMarker));
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
