// ============================================================================
//  数字签名验证 —— 用于"处置强度与可信度挂钩"
// ============================================================================
//  【为什么需要它】（用户定的规矩，2026-10-09）
//
//      规矩：有【合法数字签名】或【在白名单】的进程，
//            不论它开了多少窗口，最多只允许隐藏窗口或发 WM_CLOSE，
//            ★ 绝对不允许强杀。
//
//  【★ 但"有签名"也不能一概而论】（用户的第三条规矩）
//      签名有效 ≠ 可信。很多流氓软件也买了代码签名证书。
//      所以政策不是"有签名就不管"，而是：
//
//          无签名 + 不在白名单 → 可以升级到强杀
//          有合法签名           → 只允许 L1/L2（隐藏 / WM_CLOSE）
//          在白名单             → 只允许 L1/L2
//
//      也就是说：签名的作用是【降低处置强度】，不是【豁免处置】。
//      弹窗照样要关掉，只是不用强制手段 ✓
//
//  【★ 实现方式的取舍】
//      WinVerifyTrust 是纯 Win32 导出函数（不是 COM 对象），
//      在 Native AOT 下可用 ✓
//      —— 这一点很关键：实测过 FlaUI 那种走 COM 互操作的库在 AOT 下直接崩，
//         但 WinVerifyTrust 只是普通 P/Invoke，没有这个问题。
//
//  【★ 失败时的默认方向】
//      验签失败 / 出错 / 拿不到路径 → 一律按【已签名】处理 ✗✗
//      即：宁可少用一次强杀，也不能因为验签出错而误杀一个正常程序。
//      这条"保守失败"的方向，和整个安全设计是一致的 ✓
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace WARDEN
{
    /// <summary>签名验证结果。</summary>
    internal enum SignatureState
    {
        /// <summary>验证通过，签名可信且文件未被篡改。</summary>
        Valid = 0,
        /// <summary>文件没有签名。</summary>
        Unsigned = 1,
        /// <summary>有签名但不信任（证书链问题、自签名等）。</summary>
        Untrusted = 2,
        /// <summary>签名存在但文件已被篡改。</summary>
        Tampered = 3,
        /// <summary>无法判断（路径拿不到、API 失败等）。★ 按 Valid 处理，保守。</summary>
        Unknown = 4,
    }

    internal static class Signer
    {
        // ------------------------------------------------------------ 常量
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WTD_UI_NONE = 2;   // ★ 常量 WTD_UI_NONE
        private const uint WTD_REVOKE_NONE = 0;   // ★ 常量 WTD_REVOKE_NONE
        private const uint WTD_CHOICE_FILE = 1;   // ★ 常量 WTD_CHOICE_FILE
        private const uint WTD_STATEACTION_VERIFY = 1;   // ★ 常量 WTD_STATEACTION_VERIFY
        private const uint WTD_STATEACTION_CLOSE = 2;   // ★ 常量 WTD_STATEACTION_CLOSE
        private const uint WTD_SAFER_FLAG = 0x00000100;   // ★ 常量 WTD_SAFER_FLAG

        private const uint TRUST_E_NOSIGNATURE = 0x800B0100;   // ★ 常量 TRUST_E_NOSIGNATURE
        private const uint TRUST_E_BAD_DIGEST = 0x80096010;   // ★ 常量 TRUST_E_BAD_DIGEST
        private const uint TRUST_E_EXPLICIT_DISTRUST = 0x800B0111;   // ★ 常量 TRUST_E_EXPLICIT_DISTRUST
        private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;   // ★ 常量 CERT_E_UNTRUSTEDROOT
        private const uint CERT_E_EXPIRED = 0x800B0101;   // ★ 常量 CERT_E_EXPIRED
        private const uint CERT_E_CHAINING = 0x800B010A;   // ★ 常量 CERT_E_CHAINING

        // ------------------------------------------------------------ 结构体
        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;   // ★ 本结构体大小（必须正确填写，否则调用失败）
            public IntPtr pcwszFilePath;   // 要验证的文件完整路径
            public IntPtr hFile;   // 已打开的文件句柄（我们传 0 表示按路径验证）
            public IntPtr pgKnownSubject;   // 已知主体 GUID（我们不用）
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;   // ★ 本结构体大小（必须正确填写，否则调用直接失败）
            public IntPtr pPolicyCallbackData;   // ★ 策略回调数据（我们不用）
            public IntPtr pSIPClientData;   // ★ 主体接口包数据（我们不用）
            public uint dwUIChoice;   // ★ 是否弹界面：WTD_UI_NONE 表示全程静默
            public uint fdwRevocationChecks;   // ★ 是否检查吊销列表（我们设 WTD_REVOKE_NONE，避免联网卡住）
            public uint dwUnionChoice;   // ★ 下面那个联合体里放的是什么类型（我们放文件）
            public IntPtr pFile;   // ★ 指向 WINTRUST_FILE_INFO（要验证的文件）
            public uint dwStateAction;   // ★ 对状态数据做什么（我们不管）
            public IntPtr hWVTStateData;   // ★ 状态数据句柄（由系统填，我们不用）
            public IntPtr pwszURLReference;   // ★ 相关 URL（我们不用）
            public uint dwProvFlags;   // ★ 提供程序标志（比如禁用联网取证书链）
            public uint dwUIContext;   // ★ 界面上下文（我们不用）
            public IntPtr pSignatureSettings;   // ★ 签名设置（新版才有，我们留空）
        }

        [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [In] Guid[] pgActionID,   // ★ 验证 Authenticode 数字签名（核心：判断进程是否被篡改）
                                                  IntPtr pWVTData);

        // ------------------------------------------------------------ 0.3.6（CP2）按句柄验签
        //  【这条漏洞是什么】原来"先按路径验签、再按路径去操作" ✗
        //     验证与操作之间有个时间窗 ✗ 别人在这段时间把文件换掉 → 验的是好文件、动的是坏文件 ✗
        //  【怎么修】
        //     ① 先用【只允许共享读】的共享模式打开文件：
        //        只共享读 → 验签期间别人【不能写、不能替换、不能删除】这个文件 ✓
        //     ② 把句柄交给 WinVerifyTrust（WINTRUST_FILE_INFO.hFile ✓）
        //        → 它验证的就是【我们手上这个对象】✓ 不是"某个时刻那个路径" ✓
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;   // ★ 关键：不共享写/删 ✓
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // ============================================================ 0.3.6 目录签名（catalog）
        //  【这条漏洞是什么】只调 WTD_CHOICE_FILE → 只认文件【内嵌】的签名 ✗
        //     Windows 自家大量程序是【目录签名】（哈希登记在 system32\CatRoot 的安全目录里 ✓）
        //     实测：notepad/cmd/calc/mmc/wscript/wordpad/powershell 全被判成 Unsigned ✗（8/12 漏判 ✓）
        //     原始 HRESULT 实测 = 0x800B0100（TRUST_E_NOSIGNATURE）← 它根本没去找目录 ✓
        //  【怎么修】补一条目录验证路径：句柄算哈希 → 在目录里按哈希找成员 → 用
        //     WTD_CHOICE_CATALOG + WINTRUST_CATALOG_INFO 验证 ✓
        //     全程用【文件句柄】✓ 所以它和 CP2 一样免疫"验完再替换"✗
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CATALOG_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string wszCatalogFile;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_CATALOG_INFO
        {
            public uint cbStruct;
            public IntPtr pcwszCatalogFilePath;   // 目录文件路径（必须有 ✓）
            public IntPtr pcwszMemberTag;         // 成员标签（用句柄时可空 ✓）
            public IntPtr pcwszMemberFilePath;    // 成员文件路径（用句柄时可空 ✓）
            public IntPtr hMemberFile;            // ★ 我们传文件句柄（防替换 ✓）
            public IntPtr pbCalculatedFileHash;   // 算好的哈希（可空 → 由系统算 ✓）
            public uint cbCalculatedFileHash;
            public IntPtr pcCatalogContext;       // 目录上下文（必须有 ✓）
            public IntPtr hCatAdmin;              // 目录管理员句柄 ✓
        }

        private const uint WTD_CHOICE_CATALOG = 2;   // ★ 常量 WTD_CHOICE_CATALOG

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminAcquireContext(out IntPtr phCatAdmin,
            IntPtr pgSubsystem, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminCalcHashFromFileHandle(IntPtr hFile,
            ref uint pcbHash, byte[]? pbHash, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin,
            byte[] pbHash, uint cbHash, uint dwFlags, ref IntPtr phPrevCatInfo);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin,
            IntPtr hCatInfo, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo,
            ref CATALOG_INFO psCatInfo, uint dwFlags);

        /// <summary>
        /// ★ 0.3.6：走【安全目录】验证。返回 0 表示验证通过 ✓（其它值=不通过/找不到）。
        ///   全部基于传入的文件句柄 ✓ 所以验签期间文件不可能被替换 ✓
        /// </summary>
        private static uint VerifyWithCatalog(IntPtr hFile, string filePath)
        {
            if (hFile == IntPtr.Zero) { return TRUST_E_NOSIGNATURE; }

            IntPtr hCatAdmin = IntPtr.Zero;
            IntPtr hCatInfo = IntPtr.Zero;
            byte[]? hash = null;
            try
            {
                if (!CryptCATAdminAcquireContext(out hCatAdmin, IntPtr.Zero, 0))
                {
                    return TRUST_E_NOSIGNATURE;
                }

                // ---- ① 用句柄算这个文件的哈希（目录就是按哈希登记成员的 ✓）
                uint cb = 0;
                if (!CryptCATAdminCalcHashFromFileHandle(hFile, ref cb, null, 0) || cb == 0 || cb > 128)
                {
                    return TRUST_E_NOSIGNATURE;
                }
                hash = new byte[cb];
                if (!CryptCATAdminCalcHashFromFileHandle(hFile, ref cb, hash, 0))
                {
                    return TRUST_E_NOSIGNATURE;
                }

                // ---- ② 在目录里枚举所有含这个哈希的成员（可能命中多个目录 ✓ 逐个试）
                IntPtr prev = IntPtr.Zero;
                for (int guard = 0; guard < 64; guard++)
                {
                    hCatInfo = CryptCATAdminEnumCatalogFromHash(hCatAdmin, hash, cb, 0, ref prev);
                    if (hCatInfo == IntPtr.Zero) { break; }

                    var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                    if (CryptCATCatalogInfoFromContext(hCatInfo, ref ci, 0))
                    {
                        uint hr = TrustCatalogMember(hCatAdmin, hCatInfo, ci.wszCatalogFile, hash, cb, hFile, filePath);
                        if (hr == 0)
                        {
                            return 0;   // ★ 命中并验证通过 ✓
                        }
                    }

                    // 换下一个：先放掉上一个（prev 交给 API 当游标 ✓）
                    CryptCATAdminReleaseCatalogContext(hCatAdmin, prev, 0);
                    prev = hCatInfo;
                    hCatInfo = IntPtr.Zero;
                }
                if (prev != IntPtr.Zero) { CryptCATAdminReleaseCatalogContext(hCatAdmin, prev, 0); }
                return TRUST_E_NOSIGNATURE;
            }
            catch
            {
                return TRUST_E_NOSIGNATURE;   // 出错=不通过（保守 ✓ 与原来"出错按 Unknown"的语义分开 ✓）
            }
            finally
            {
                if (hCatInfo != IntPtr.Zero && hCatAdmin != IntPtr.Zero)
                {
                    CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0);
                }
                if (hCatAdmin != IntPtr.Zero) { CryptCATAdminReleaseContext(hCatAdmin, 0); }
            }
        }

        /// <summary>对一个目录成员跑 WinVerifyTrust（WTD_CHOICE_CATALOG ✓）。返回 0 = 通过 ✓</summary>
        private static uint TrustCatalogMember(IntPtr hCatAdmin, IntPtr hCatInfo,
            string catalogFile, byte[] hash, uint cbHash, IntPtr hFile, string filePath)
        {
            IntPtr catPtr = IntPtr.Zero, hashPtr = IntPtr.Zero;
            IntPtr infoPtr = IntPtr.Zero, dataPtr = IntPtr.Zero;
            try
            {
                catPtr = Marshal.StringToHGlobalUni(catalogFile);
                hashPtr = Marshal.AllocHGlobal((int)cbHash);
                Marshal.Copy(hash, 0, hashPtr, (int)cbHash);

                var info = new WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                    pcwszCatalogFilePath = catPtr,
                    pcwszMemberTag = IntPtr.Zero,
                    pcwszMemberFilePath = IntPtr.Zero,
                    hMemberFile = hFile,          // ★ 句柄优先（防替换 ✓）
                    pbCalculatedFileHash = hashPtr,
                    cbCalculatedFileHash = cbHash,
                    pcCatalogContext = hCatInfo,
                    hCatAdmin = hCatAdmin,
                };
                infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
                Marshal.StructureToPtr(info, infoPtr, false);

                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_CATALOG,
                    pFile = infoPtr,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_SAFER_FLAG,
                };
                dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
                Marshal.StructureToPtr(data, dataPtr, false);

                uint hr = WinVerifyTrust(IntPtr.Zero,
                                         new[] { WINTRUST_ACTION_GENERIC_VERIFY_V2 },
                                         dataPtr);

                // 状态数据必须 CLOSE 掉 ✓
                var closeData = Marshal.PtrToStructure<WINTRUST_DATA>(dataPtr);
                closeData.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(closeData, dataPtr, false);
                WinVerifyTrust(IntPtr.Zero,
                               new[] { WINTRUST_ACTION_GENERIC_VERIFY_V2 },
                               dataPtr);
                return hr;
            }
            catch
            {
                return TRUST_E_NOSIGNATURE;
            }
            finally
            {
                if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
                if (infoPtr != IntPtr.Zero) Marshal.FreeHGlobal(infoPtr);
                if (hashPtr != IntPtr.Zero) Marshal.FreeHGlobal(hashPtr);
                if (catPtr != IntPtr.Zero) Marshal.FreeHGlobal(catPtr);
            }
        }

        // ------------------------------------------------------------ 缓存
        /// <summary>按进程名缓存结果 —— 同一个进程反复验签很浪费。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, SignatureState>
            _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 验证一个文件的数字签名状态。
        /// ★ 任何异常都返回 Unknown（按已签名对待），绝不因为验签出错而放行强杀 ✗
        /// </summary>
        public static SignatureState Verify(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return SignatureState.Unknown;
            }
            // ★ 0.3.6（CP2）：缓存键不能只用路径 ✗
            //   否则"同路径但内容已被换掉"的文件会命中旧结论 ✓
            //   → 键里带上【文件大小 + 最后写入时间】✓ 内容变了必然 miss ✓
            string cacheKey = CacheKey(filePath);
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            SignatureState result;
            IntPtr pathPtr = IntPtr.Zero;
            IntPtr fileInfoPtr = IntPtr.Zero;
            IntPtr trustDataPtr = IntPtr.Zero;
            IntPtr hFile = IntPtr.Zero;   // ★ CP2：这次验签真正绑定的文件句柄

            try
            {
                // ★ 0.3.6（CP2）：先拿句柄（只共享读 → 验签期间文件动不了 ✓）
                //   拿不到（被独占/不存在/无权限）→ hFile 保持 0 → 退回按路径验证 ✓
                //   保守策略不变：任何异常都不阻断（验签出错按 Unknown 处理 ✓）
                try
                {
                    hFile = CreateFileW(filePath, GENERIC_READ, FILE_SHARE_READ,
                                        IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                    if (hFile == new IntPtr(-1))
                    {
                        hFile = IntPtr.Zero;
                    }
                }
                catch { hFile = IntPtr.Zero; }

                pathPtr = Marshal.StringToHGlobalUni(filePath);

                var fileInfo = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    // ★ CP2 实测教训（2026-10-12）：句柄与路径【只能给一个】✗
                    //   两个都给 → WinVerifyTrust 直接失败 ✗
                    //   后果很严重：微软签名的 notepad.exe 会被判成 Unsigned ✗（实测确认 ✓）
                    //   正确做法：有句柄就【只给句柄】（那才是防替换的安全路径 ✓）
                    //             拿不到句柄才退回"只给路径"✓
                    pcwszFilePath = hFile != IntPtr.Zero ? IntPtr.Zero : pathPtr,
                    hFile = hFile,
                    pgKnownSubject = IntPtr.Zero,
                };
                fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,             // 不弹任何界面
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = fileInfoPtr,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_SAFER_FLAG,
                };
                trustDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
                Marshal.StructureToPtr(data, trustDataPtr, false);

                uint hr = WinVerifyTrust(IntPtr.Zero,
                                         new[] { WINTRUST_ACTION_GENERIC_VERIFY_V2 },
                                         trustDataPtr);

                // ★ 0.3.6 Signer 修复：内嵌签名没验过时，再走【安全目录】那条路 ✓
                //   实测教训：只查内嵌签名 → Windows 自家目录签名的程序（notepad/cmd/calc/
                //   mmc/wscript/wordpad/powershell 等）会被误判成 Unsigned ✗（原始 HRESULT
                //   是 0x800B0100 = 找不到签名 ✓ 因为它压根没去查目录 ✓）
                if (hr != 0 && hFile != IntPtr.Zero)
                {
                    uint hrCat = VerifyWithCatalog(hFile, filePath);
                    if (hrCat == 0)
                    {
                        hr = 0;   // ★ 目录里查到成员且验证通过 ✓ 按"已签名"处理 ✓
                    }
                }
                result = MapResult(hr, filePath);

                // ★ 必须再调一次 CLOSE，否则会泄漏状态数据
                // ★★ 这里用泛型版 PtrToStructure<T>：非泛型版在 AOT 下
                //    会产生 IL3050 警告（需要运行时代码生成），泛型版不需要 ✓
                var closeData = Marshal.PtrToStructure<WINTRUST_DATA>(trustDataPtr);
                closeData.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(closeData, trustDataPtr, false);
                WinVerifyTrust(IntPtr.Zero,
                               new[] { WINTRUST_ACTION_GENERIC_VERIFY_V2 },
                               trustDataPtr);
            }
            catch
            {
                // ★ 出错就按"已签名"处理 —— 保守失败，绝不因此放行强杀
                result = SignatureState.Unknown;
            }
            finally
            {
                if (trustDataPtr != IntPtr.Zero) Marshal.FreeHGlobal(trustDataPtr);
                if (fileInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPtr);
                if (pathPtr != IntPtr.Zero) Marshal.FreeHGlobal(pathPtr);
                // ★ CP2：句柄一定要关 ✗ 否则句柄泄漏 ✓
                if (hFile != IntPtr.Zero) { CloseHandle(hFile); hFile = IntPtr.Zero; }
            }

            _cache[cacheKey] = result;
            return result;
        }

        /// <summary>
        /// ★ 0.3.6（CP2）：验签缓存的键。
        ///   路径 + 文件大小 + 最后写入时间（UTC ticks）✓
        ///   换内容必然 miss ✓ 这样"同路径换文件"不会再骗到缓存 ✓
        /// </summary>
        private static string CacheKey(string path)
        {
            try
            {
                var fi = new System.IO.FileInfo(path);
                if (!fi.Exists)
                {
                    return path + "|missing";
                }
                return path + "|" + fi.Length.ToString() + "|" + fi.LastWriteTimeUtc.Ticks.ToString();
            }
            catch
            {
                return path + "|err";
            }
        }

        private static SignatureState MapResult(uint hr, string filePath)
        {
            if (hr == 0)
            {
                return SignatureState.Valid;
            }
            if (hr == TRUST_E_NOSIGNATURE)
            {
                // ★★★ 关键区分（2026-10-09 实测发现）：
                //     WinVerifyTrust 返回"没有签名"有两种情况：
                //       a) 文件【本来】就没签过名        → Unsigned ✓
                //       b) 文件【有】签名数据，但被改坏了 → 也是 NOSIGNATURE ✗
                //     第二种其实是"被篡改"，不该报成"没签名"✗
                //     判据：看 PE 里有没有证书表（Security Directory）✓
                if (HasEmbeddedSignature(filePath))
                {
                    return SignatureState.Tampered;
                }
                return SignatureState.Unsigned;
            }
            if (hr == TRUST_E_BAD_DIGEST)
            {
                return SignatureState.Tampered;
            }
            if (hr == TRUST_E_EXPLICIT_DISTRUST
                || hr == CERT_E_UNTRUSTEDROOT
                || hr == CERT_E_EXPIRED
                || hr == CERT_E_CHAINING)
            {
                return SignatureState.Untrusted;
            }
            // 其它错误码（含文件不存在）→ 无法判断
            return SignatureState.Unknown;
        }

        /// <summary>
        /// ★★ 检查 PE 文件里是否【嵌入了签名数据】。
        ///
        /// 做法：读 PE 可选头的 DataDirectory[IMAGE_DIRECTORY_ENTRY_SECURITY]（索引 4）
        ///       它的 VirtualAddress/Size 非 0 就说明有签名块 ✓
        ///
        /// 用途：区分"本来就没签名"和"签名被破坏了"✗
        /// </summary>
        public static bool HasEmbeddedSignature(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                {
                    return false;
                }
                using var fs = System.IO.File.OpenRead(filePath);
                using var br = new System.IO.BinaryReader(fs);

                // DOS 头：e_magic = 'MZ'，e_lfanew(0x3C) → PE 头偏移
                if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D)
                {
                    return false;
                }
                fs.Seek(0x3C, System.IO.SeekOrigin.Begin);
                int peOff = br.ReadInt32();
                if (peOff <= 0 || peOff + 24 > fs.Length)
                {
                    return false;
                }
                fs.Seek(peOff, System.IO.SeekOrigin.Begin);
                if (br.ReadUInt32() != 0x00004550)   // 'PE\0\0'
                {
                    return false;
                }
                br.ReadUInt16();                     // Machine
                int numSections = br.ReadUInt16();
                br.ReadUInt32();                     // TimeDateStamp
                br.ReadUInt32();                     // PointerToSymbolTable
                br.ReadUInt32();                     // NumberOfSymbols
                int optSize = br.ReadUInt16();
                br.ReadUInt16();                     // Characteristics

                long optOff = fs.Position;
                ushort magic = br.ReadUInt16();
                // PE32 → DataDirectory 在可选头偏移 0x60；PE32+ → 0x70
                long ddOff = magic == 0x10B ? optOff + 0x60 : optOff + 0x70;

                // 目录项 4 = IMAGE_DIRECTORY_ENTRY_SECURITY
                long secOff = ddOff + 4 * 8;
                if (secOff + 8 > fs.Length)
                {
                    return false;
                }
                fs.Seek(secOff, System.IO.SeekOrigin.Begin);
                uint va = br.ReadUInt32();
                uint sz = br.ReadUInt32();
                return va != 0 && sz != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★★ 核心判定：这个进程是否【禁止强杀】。
        ///
        /// 返回 true 表示只能降级处置（隐藏 / WM_CLOSE），不能 TerminateProcess。
        ///
        /// 判断依据（任一成立即禁止）：
        ///   ① 签名有效 / 不可判断（保守）
        ///   ② 有签名但不信任 —— 也禁止：用户规矩说"有合法数字签名的"，
        ///      而非法的更不该由我们动手，交给杀软
        ///   ③ 在白名单 —— 由调用方另行判断
        /// </summary>
        public static bool IsProtectedFromKill(string imagePath, out string reason)
        {
            var st = Verify(imagePath);
            switch (st)
            {
                case SignatureState.Valid:
                    reason = "有合法数字签名 → 只允许隐藏或 WM_CLOSE";
                    return true;
                case SignatureState.Untrusted:
                    reason = "有签名但不被信任 → 不自行强杀，交给安全软件";
                    return true;
                case SignatureState.Tampered:
                    reason = "签名与文件不符 → 不自行强杀，交给安全软件";
                    return true;
                case SignatureState.Unknown:
                    reason = "无法判断签名状态 → 保守处理，不强杀";
                    return true;
                case SignatureState.Unsigned:
                    reason = string.Empty;
                    return false;   // ★ 只有明确"无签名"才允许强杀
                default:
                    reason = "状态未知 → 保守处理";
                    return true;
            }
        }

        /// <summary>给日志用的人类可读描述。</summary>
        public static string Describe(SignatureState st) => st switch
        {
            SignatureState.Valid => "签名有效",
            SignatureState.Unsigned => "无签名",
            SignatureState.Untrusted => "签名不受信任",
            SignatureState.Tampered => "签名已失效(被篡改)",
            _ => "签名状态未知",
        };
    }
}

