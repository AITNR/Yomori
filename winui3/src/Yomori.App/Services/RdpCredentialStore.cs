// -*- coding: utf-8 -*-
using System;
using System.Runtime.InteropServices;

namespace Yomori.Services;

/// <summary>
/// 目标账户密码的存取。
///
/// 为什么不直接用 mstsc 那套：<c>cmdkey /generic:TERMSRV/xxx</c> 只在 mstsc 里会自动取用，
/// 内嵌的 FreeRDP 客户端不会去读凭据管理器的那一条，所以那条路径对它无效。
/// 这里直接用 CredWrite 写一条会话级凭证，连接时 CredRead 取回来交给客户端。
///
/// 键 = <c>Yomori/RDP/&lt;主机&gt;|&lt;账户&gt;</c>：
/// **同一个主机的多个账户必须各存一条**，否则后保存的会把前一个覆盖掉 ——
/// 多会话通道并行时，这正是"B 通道拿着 A 的密码去登录"的成因。
/// 密码只存在 Windows 凭据管理器里，不落 config.json。
/// </summary>
internal static class RdpCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    private const int ErrorNotFound = 1168;

    /// <summary>凭据管理器里显示的名字，加了前缀避免和别人的条目撞。</summary>
    private const string TargetPrefix = "Yomori/RDP/";

    /// <summary>
    /// 本条目对应的凭据管理器键（主机 + 账户）。
    ///
    /// 账户名先归一化（去 "机器名\" / "域\" 前缀 + 转小写）：
    /// 同一个账户写成 "PC\Player2" 与 "player2" 时必须落到同一条，
    /// 否则会出现"刚保存的凭据读不回来"，而且很难看出原因。
    /// </summary>
    private static string BuildTarget(string host, string? userName)
    {
        var user = BareName(userName).ToLowerInvariant();
        return user.Length == 0 ? TargetPrefix + host : $"{TargetPrefix}{host}|{user}";
    }

    /// <summary>旧版本（只有主机、不含账户）的键，读取时用于回落兼容。</summary>
    private static string LegacyTarget(string host) => TargetPrefix + host;

    public static bool Write(string host, string userName, string password)
    {
        if (host.Length == 0 || userName.Length == 0 || password.Length == 0)
        {
            return false;
        }

        return WriteTarget(BuildTarget(host, userName), userName, password);
    }

    /// <summary>
    /// 读取密码。先查新键（主机 + 账户）；没有则回落旧键（只有主机），
    /// 但旧条目里记录的账户名必须与请求的账户一致才认 —— 否则会拿错账户的密码去登录。
    /// </summary>
    public static string? Read(string host, string? userName)
    {
        if (host.Length == 0)
        {
            return null;
        }

        var direct = ReadTarget(BuildTarget(host, userName));
        if (direct.Password is not null)
        {
            return direct.Password;
        }

        var legacy = ReadTarget(LegacyTarget(host));
        if (legacy.Password is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(userName))
        {
            return legacy.Password;
        }

        return SameUser(legacy.UserName, userName) ? legacy.Password : null;
    }

    /// <summary>删除凭据：新键与旧键一起清掉（旧键只可能属于升级前那唯一一个账户）。</summary>
    public static void Delete(string host, string? userName)
    {
        if (host.Length == 0)
        {
            return;
        }

        CredDeleteW(BuildTarget(host, userName), CredTypeGeneric, 0);
        CredDeleteW(LegacyTarget(host), CredTypeGeneric, 0);
    }

    /// <summary>判断凭据是不是真的存在（比解析 cmdkey 的输出可靠）。</summary>
    public static bool Exists(string host, string? userName) => Read(host, userName) is not null;

    /// <summary>
    /// 写一条自定义 target 的秘密（例如 GitHub 访问令牌），复用同一套 CredWrite。
    /// </summary>
    /// <remarks>
    /// 存在理由：秘密一律只进 Windows 凭据管理器，不进 config.json ——
    /// 配置会被导出/备份/贴给别人看，凭据不会。这里把通道打开，让别的服务
    /// 也能遵守同一条规则，而不是各自去写一个明文文件。
    /// </remarks>
    internal static bool WriteSecret(string target, string userName, string secret) =>
        target.Length > 0 && userName.Length > 0 && secret.Length > 0
        && WriteTarget(target, userName, secret);

    /// <summary>读一条自定义 target 的秘密；不存在返回 null。</summary>
    internal static string? ReadSecret(string target) => ReadTarget(target).Password;

    /// <summary>删一条自定义 target 的秘密（不存在也算成功）。</summary>
    internal static void DeleteSecret(string target) =>
        CredDeleteW(target, CredTypeGeneric, 0);

    /// <summary>"机器名\账户" / "域\账户" 与裸账户名视为同一账户（机器名/域名不参与区分）。</summary>
    private static bool SameUser(string? left, string? right) =>
        string.Equals(BareName(left), BareName(right), StringComparison.OrdinalIgnoreCase);

    private static string BareName(string? userName)
    {
        var text = (userName ?? string.Empty).Trim();
        var slash = text.LastIndexOf('\\');
        return slash >= 0 ? text[(slash + 1)..] : text;
    }

    private static bool WriteTarget(string target, string userName, string password)
    {
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni(userName);
        var blobPtr = Marshal.StringToCoTaskMemUni(password);

        try
        {
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)(password.Length * 2),
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };

            return CredWriteW(ref credential, 0);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
            Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    /// <summary>读一条凭据，同时取回密码与条目里记的账户名（账户名用于旧键的归属校验）。</summary>
    private static (string? Password, string? UserName) ReadTarget(string target)
    {
        if (!CredReadW(target, CredTypeGeneric, 0, out var handle) || handle == IntPtr.Zero)
        {
            return (null, null);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return (null, null);
            }

            var password = Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            var user = credential.UserName == IntPtr.Zero ? null : Marshal.PtrToStringUni(credential.UserName);
            return (password, user);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            CredFree(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>凭据不存在时的 Win32 错误码，用来区分"真没存"和"读取失败"。</summary>
    public static bool IsNotFound => Marshal.GetLastWin32Error() == ErrorNotFound;
}
