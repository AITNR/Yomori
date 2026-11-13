// S1 第八批：Global\ 命名共享内存的 ACL 授权（NC3）。
//
// 背景：采集端跑在目标账户的 RDP 会话、主控端跑在 Console 会话，是两个不同登录会话。
// Global\ 前缀只解决「命名空间可见」，不解决「谁能打开」——默认 DACL 只给创建者账户。
//
// ⚠️ .NET 8 已移除 MemoryMappedFileSecurity 与带 security 的 CreateNew 重载
//    （那是 .NET Framework 时代的 API）。所以这里改成：先用 MemoryMappedFile
//    创建/打开映射，再对内核命名对象调 SetKernelObjectSecurity 补一条 Everyone ACE。
//    安全描述符完全在托管侧用 RawSecurityDescriptor / CommonAce 构造，不手拼字节。
//
// 权限前置条件：创建 Global\ 内核对象需要 SeCreateGlobalPrivilege（默认授予
// Administrators / SYSTEM / 服务，普通交互用户可能没有）；设置 DACL 需要 WRITE_DAC
// （创建者默认具备）。任一失败都由 _probe/S1/GlobalAclProbe 在真机定位，
// 生产侧则退回「无帧」→ 保持 RDP 帧流，不会黑屏。
using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Yomori.Services
{
    public static class CaptureSharedMemoryAcl
    {
        // FILE_MAP_ALL_ACCESS = STANDARD_RIGHTS_REQUIRED | SECTION_QUERY | SECTION_MAP_WRITE |
        //                       SECTION_MAP_READ | SECTION_MAP_EXECUTE | SECTION_EXTEND_SIZE
        private const uint FileMapAllAccess = 0x000F001F;
        private const uint DaclSecurityInformation = 0x00000004;
        private const byte AclRevision = 2; // ACL_REVISION

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetKernelObjectSecurity(IntPtr handle,
            uint securityInformation, byte[] securityDescriptor);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>Everyone: FILE_MAP_ALL_ACCESS 的自相对安全描述符字节。</summary>
        public static byte[] BuildWorldReadWriteDescriptor()
        {
            var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var ace = new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
                                    (int)FileMapAllAccess, world, false, null);
            var acl = new RawAcl(AclRevision, 1);
            acl.InsertAce(0, ace);
            // 参数顺序：owner / group / SystemAcl / DiscretionaryAcl
            var sd = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent,
                                               null, null, null, acl);
            byte[] bytes = new byte[sd.BinaryLength];
            sd.GetBinaryForm(bytes, 0);
            return bytes;
        }

        /// <summary>
        /// 给已存在的命名映射补 Everyone ACE。必须在创建之后、消费端打开之前调用。
        /// 返回 false 不掀进程，由调用方决定降级（本机同账户仍可能可用）。
        /// </summary>
        public static bool TryApplyWorldDacl(string mapName, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(mapName)) { error = "映射名为空"; return false; }
            IntPtr handle = IntPtr.Zero;
            try
            {
                byte[] descriptor = BuildWorldReadWriteDescriptor();
                handle = OpenFileMappingW(FileMapAllAccess, false, mapName);
                if (handle == IntPtr.Zero)
                {
                    error = "OpenFileMappingW 失败，Win32=" + Marshal.GetLastWin32Error();
                    return false;
                }
                if (!SetKernelObjectSecurity(handle, DaclSecurityInformation, descriptor))
                {
                    error = "SetKernelObjectSecurity 失败，Win32=" + Marshal.GetLastWin32Error();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }

        /// <summary>
        /// 创建或打开命名映射并尽力补 DACL。
        /// <paramref name="error"/> 只承载「创建失败原因」；DACL 设置失败时映射仍可用，
        /// 其告警单独由 <see cref="TryApplyWorldDacl"/> 暴露，供探针与日志定位。
        /// </summary>
        public static bool TryCreateOrOpen(string mapName, long capacity,
                                          out MemoryMappedFile? mmf, out string error)
        {
            mmf = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(mapName)) { error = "映射名为空"; return false; }
            if (capacity <= 0) { error = "容量非法"; return false; }

            try
            {
                mmf = MemoryMappedFile.CreateOrOpen(mapName, capacity);
            }
            catch (UnauthorizedAccessException ex)
            {
                // 多半是缺 SeCreateGlobalPrivilege（Global\ 专属）。
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            // DACL 失败不否定映射本身：同账户/同会话仍可打开，真机跨账户失败会在探针里暴露。
            TryApplyWorldDacl(mapName, out _);
            return true;
        }

        /// <summary>适配 CaptureFrameRelay / CaptureAgentLoop 的映射工厂委托。</summary>
        public static MemoryMappedFile? Factory(string mapName, long capacity)
            => TryCreateOrOpen(mapName, capacity, out var mmf, out _) ? mmf : null;

        /// <summary>
        /// <b>只打开、不创建</b>：NC3 实测（2026-11-13）非提升进程的 token 里
        /// 完全没有 SeCreateGlobalPrivilege，创建 Global\ 内核对象必定 ACCESS_DENIED(5)。
        /// 而「打开」已有对象不需要该特权，只要创建方给过 Everyone ACE 即可。
        /// 因此创建方是**提升运行的主控端**（Yomori.exe requireAdministrator），
        /// 目标会话里 asInvoker 的 Agent 只用本工厂打开。
        /// </summary>
        public static MemoryMappedFile? OpenExistingFactory(string mapName, long capacity)
        {
            try
            {
                return MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite);
            }
            catch
            {
                return null; // 主控端还没创建 / 已释放 → 采集循环下一拍重试
            }
        }
    }
}
