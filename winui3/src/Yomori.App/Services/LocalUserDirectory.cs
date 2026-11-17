// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>本机账户的一条扫描结果。<b>纯数据</b>，显示文案由 <see cref="Detail"/> / <see cref="Note"/> 派生。</summary>
public sealed class LocalUserEntry
{
    public string Name { get; init; } = "";

    /// <summary>账户描述（<c>usri3_comment</c>），系统内置账户自带本地化说明。</summary>
    public string Comment { get; init; } = "";

    /// <summary>已启用（没有 UF_ACCOUNTDISABLE）。</summary>
    public bool Enabled { get; init; }

    /// <summary>系统内置账户（Administrator / Guest / DefaultAccount / WDAGUtilityAccount / WsiAccount）。</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>Microsoft 账户（<c>USER_INFO_24.usri24_internet_identity</c>）。密码在微软那边，本机改不了。</summary>
    public bool IsMicrosoftAccount { get; init; }

    /// <summary>Administrators 组成员。</summary>
    public bool IsAdministrator { get; init; }

    /// <summary>Remote Desktop Users（或 Administrators）组成员 —— 决定它能不能远程登录。</summary>
    public bool CanRemoteLogon { get; init; }

    /// <summary>密码永不过期（UF_DONT_EXPIRE_PASSWD）。</summary>
    public bool PasswordNeverExpires { get; init; }

    /// <summary>密码已设置多少天；-1 = 读不到。</summary>
    public int PasswordAgeDays { get; init; }

    /// <summary>最近一次登录时间；<c>null</c> = 从未登录或读不到。</summary>
    public DateTimeOffset? LastLogon { get; init; }

    /// <summary>是不是主控端当前登录的账户（拿它建通道会和当前会话抢）。</summary>
    public bool IsCurrentUser { get; init; }

    /// <summary>
    /// 是否被从**交互式登录界面**隐藏（<c>Winlogon\SpecialAccounts\UserList</c> 里为 0）。
    /// 只影响欢迎屏幕的账户磁贴与「切换用户」列表，不影响远程桌面与凭据登录。
    /// </summary>
    public bool HiddenFromLogon { get; init; }

    /// <summary>登录过（<c>usri3_last_logon</c> 非 0）。</summary>
    public bool HasLoggedOn => LastLogon is not null;

    /// <summary>
    /// 能当通道的目标账户用。
    /// </summary>
    /// <remarks>
    /// 除了「已启用 + 非系统内置」，还要排除**纯服务 / 沙箱身份**：从未登录过、又没有任何远程登录权限
    /// （既不是管理员也不在远程桌面组）的账户，本质是给某个程序当运行身份用的 —— 典型例子是
    /// Codex CLI 在 Windows 上建的 <c>CodexSandboxOffline</c> / <c>CodexSandboxOnline</c>
    /// （见《通道账户选择器》笔记）。它们选进通道也连不上远程桌面，列出来纯属噪音。
    /// <para>反过来，只要「登录过」或「有远程登录权限」就保留：前者是人类账户，后者是能直接用的账户。</para>
    /// </remarks>
    public bool Usable => Enabled && !IsBuiltIn && (CanRemoteLogon || HasLoggedOn);

    /// <summary>下拉列表里显示的状态行（一行说清这台机器上它是什么状态）。</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string> { Enabled ? "已启用" : "已禁用" };

            if (IsBuiltIn)
            {
                parts.Add("系统内置账户");
            }

            if (IsMicrosoftAccount)
            {
                parts.Add("Microsoft 账户");
            }

            if (IsAdministrator)
            {
                parts.Add("本机管理员");
            }
            else if (CanRemoteLogon)
            {
                parts.Add("远程桌面用户");
            }
            else
            {
                parts.Add("非管理员、不在远程桌面组");
            }

            parts.Add(PasswordNeverExpires
                ? "密码永不过期"
                : PasswordAgeDays >= 0 ? $"密码 {PasswordAgeDays} 天前设置" : "密码期限不详");

            if (HiddenFromLogon)
            {
                parts.Add("登录界面已隐藏");
            }

            parts.Add(LastLogon is { } at
                ? $"最近登录 {at.LocalDateTime:MM-dd HH:mm}"
                : "从未登录");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>选中时额外要提醒的一句；没有提醒时为空串。</summary>
    public string Note
    {
        get
        {
            if (IsBuiltIn)
            {
                return "系统内置账户不参与通道，别拿它建通道。";
            }

            if (!Enabled)
            {
                return "该账户已禁用，选它之前得先去 Windows 里启用。";
            }

            if (IsCurrentUser)
            {
                return "这是主控端当前登录的账户：拿它建通道会和当前会话抢同一个桌面。";
            }

            if (IsMicrosoftAccount)
            {
                return "Microsoft 账户的密码是微软账户密码，本机改不了；请填你在微软那边的当前密码。";
            }

            if (!CanRemoteLogon && !HasLoggedOn)
            {
                return "它从未登录过，也没有远程桌面登录权限 —— 这是给服务 / 沙箱用的运行身份，"
                       + "不建议当通道账户（选中也连不上远程桌面）。";
            }

            if (!IsAdministrator)
            {
                return "它不是本机管理员：保存凭据时会问要不要把它加进 Administrators。";
            }

            return string.Empty;
        }
    }
}

/// <summary>一次扫描的结果。</summary>
public sealed class LocalUserScan
{
    public IReadOnlyList<LocalUserEntry> All { get; init; } = Array.Empty<LocalUserEntry>();

    /// <summary>能当目标账户用的（已启用、非系统内置）。</summary>
    public IReadOnlyList<LocalUserEntry> Usable { get; init; } = Array.Empty<LocalUserEntry>();

    /// <summary>一句话概况，直接用于界面提示与运行日志。</summary>
    public string Summary { get; init; } = "";

    /// <summary>NetUserEnum 的返回码；非 0 表示这轮扫描不可信。</summary>
    public int Code { get; init; }

    public string Error { get; init; } = "";
}

/// <summary>
/// 扫描本机账户，供「通道管理」的目标账户选择器使用。<b>全程只读</b>：不建、不删、不改密码、不改组。
/// </summary>
/// <remarks>
/// <para>为什么自己走 netapi32 而不是解析 <c>net user</c>：那条命令的输出全是本地化文本，
/// 账户名里还可能有空格与中文，"启用/禁用"要靠人眼认；<c>NetUserEnum</c> 给的是一手标志位。</para>
///
/// <para>判别依据（三个能力各用各的 API，都实测过）：
/// ①「启用」看 <c>usri3_flags</c> 的 UF_ACCOUNTDISABLE；
/// ②「Microsoft 账户」看 <c>NetUserGetInfo(level 24)</c> 的第一个 BOOL（usri24_internet_identity）——
///   本机实测：MSA 账户全为 1、纯本地账户全为 0，与 <c>Get-LocalUser</c> 的 PrincipalSource 一一对应；
/// ③「管理员 / 能远程登录」把 Administrators 与 Remote Desktop Users 的成员按 SID 取成集合再比对
///   （两条 <c>NetLocalGroupGetMembers</c> 就够，不必逐账户问一遍）。</para>
///
/// <para>系统内置账户按名字识别（Administrator / Guest / DefaultAccount / WDAGUtilityAccount /
/// WsiAccount）。这些名字在 Windows 上**不随语言本地化**，只有描述文字会变。</para>
///
/// <para>纯 .NET + Win32，不引用 Microsoft.UI.Xaml；代理工程里显式排除（asInvoker 用不到账户枚举）。</para>
/// </remarks>
public static class LocalUserDirectory
{
    /// <summary>每次最多取多少条（-1 = 让系统一次给全）。</summary>
    private const int PreferMaxLength = -1;

    private const int FilterNormalAccount = 0x0002;

    private const int UfAccountDisable = 0x0002;
    private const int UfDontExpirePasswd = 0x0001_0000;

    private const int LevelUserInfo1 = 1;
    private const int LevelUserInfo3 = 3;
    private const int LevelUserInfo24 = 24;
    private const int NerrSuccess = 0;
    private const int ErrorMoreData = 234;

    /// <summary>系统内置账户名（不随语言本地化）。</summary>
    private static readonly string[] BuiltInNames =
    {
        "Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount", "WsiAccount",
    };

    /// <summary>
    /// 扫描本机全部本地账户（含已禁用与系统内置），并挑出可用的那些。
    /// </summary>
    /// <param name="machineName">当前机器名，用于标记「当前登录账户」。</param>
    public static LocalUserScan Scan(string? machineName = null)
    {
        var names = new List<(string Name, uint Flags)>();
        var resumeHandle = IntPtr.Zero;

        try
        {
            while (true)
            {
                var buffer = IntPtr.Zero;
                try
                {
                    var code = Native.NetUserEnum(
                        null, LevelUserInfo1, FilterNormalAccount, out buffer,
                        PreferMaxLength, out var read, out _, ref resumeHandle);

                    if (code != NerrSuccess && code != ErrorMoreData)
                    {
                        return new LocalUserScan
                        {
                            Code = code,
                            Error = $"枚举本机账户失败：{DescribeCode(code)}",
                            Summary = $"枚举失败（{DescribeCode(code)}）",
                        };
                    }

                    var size = Marshal.SizeOf<Native.USER_INFO_1>();
                    for (var i = 0; i < read; i++)
                    {
                        var info = Marshal.PtrToStructure<Native.USER_INFO_1>(buffer + (i * size));
                        var name = Marshal.PtrToStringUni(info.usri1_name);
                        if (!string.IsNullOrEmpty(name))
                        {
                            names.Add((name, info.usri1_flags));
                        }
                    }

                    if (code != ErrorMoreData)
                    {
                        break;
                    }
                }
                finally
                {
                    if (buffer != IntPtr.Zero)
                    {
                        _ = Native.NetApiBufferFree(buffer);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new LocalUserScan
            {
                Code = -1,
                Error = $"枚举本机账户出错：{ex.GetType().Name}：{ex.Message}",
                Summary = "枚举出错",
            };
        }
        finally
        {
            if (resumeHandle != IntPtr.Zero)
            {
                _ = Native.NetApiBufferFree(resumeHandle);
            }
        }

        var admins = GroupMemberSids(RdpEditions.AdministratorsSid);
        var rdpUsers = GroupMemberSids(RdpEditions.RemoteDesktopUsersSid);
        // 登录界面可见性：整张 UserList 一次读全，别逐账户开注册表
        var userList = LocalUserVisibility.ReadUserList();
        var currentUser = Environment.UserName;

        var all = new List<LocalUserEntry>(names.Count);
        foreach (var (name, enumFlags) in names)
        {
            all.Add(ReadEntry(name, enumFlags, admins, rdpUsers, userList, currentUser));
        }

        all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var usable = all.FindAll(static e => e.Usable);
        var hidden = all.Count - usable.Count;

        return new LocalUserScan
        {
            All = all,
            Usable = usable,
            Code = NerrSuccess,
            Summary = hidden == 0
                ? $"扫到 {all.Count} 个本机账户，全部可用。"
                : $"扫到 {all.Count} 个本机账户，其中 {usable.Count} 个可用"
                  + $"（其余 {hidden} 个：{DescribeExclusions(all)} —— 已从列表里隐去）。",
        };
    }

    /// <summary>
    /// 把被隐去的账户按原因归类说清楚（按「已禁用 → 系统内置 → 服务/沙箱身份」的优先级各算一次）。
    /// 只用于概况文案，不参与可用性判定。
    /// </summary>
    private static string DescribeExclusions(IReadOnlyList<LocalUserEntry> all)
    {
        var disabled = 0;
        var builtIn = 0;
        var service = 0;

        foreach (var entry in all)
        {
            if (entry.Usable)
            {
                continue;
            }

            if (!entry.Enabled)
            {
                disabled++;
            }
            else if (entry.IsBuiltIn)
            {
                builtIn++;
            }
            else
            {
                service++;
            }
        }

        var parts = new List<string>(3);
        if (disabled > 0)
        {
            parts.Add($"已禁用 {disabled}");
        }

        if (builtIn > 0)
        {
            parts.Add($"系统内置 {builtIn}");
        }

        if (service > 0)
        {
            parts.Add($"服务/沙箱身份 {service}");
        }

        return string.Join("、", parts);
    }

    /// <summary>单个账户的详情（只读）。账户不存在或读不出来时返回 <c>null</c>。</summary>
    public static LocalUserEntry? Read(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        // 复用建账户那条链路的查重（同样是只读的 NetUserGetInfo，三态语义一致）
        if (!LocalUserAccountService.QueryExists(text).Found)
        {
            return null;
        }

        return ReadEntry(
            text, 0, GroupMemberSids(RdpEditions.AdministratorsSid),
            GroupMemberSids(RdpEditions.RemoteDesktopUsersSid),
            LocalUserVisibility.ReadUserList(), Environment.UserName);
    }

    /// <summary>把账户名解析成 SID 字符串（失败返回 null）。给「凭据归属」这类判断用。</summary>
    public static string? ResolveSid(string? name) => LocalGroupMembershipService.ResolveAccountSid(name);

    private static LocalUserEntry ReadEntry(
        string name, uint enumFlags, HashSet<string> admins, HashSet<string> rdpUsers,
        IReadOnlyDictionary<string, int> userList, string currentUser)
    {
        var info = ReadInfo(name);
        var flags = info.Ok ? info.Flags : enumFlags;
        var sid = LocalGroupMembershipService.ResolveAccountSid(name);

        return new LocalUserEntry
        {
            Name = name,
            Comment = info.Ok ? info.Comment : string.Empty,
            Enabled = (flags & UfAccountDisable) == 0,
            IsBuiltIn = IsBuiltInName(name),
            IsMicrosoftAccount = info.Ok && info.MicrosoftAccount,
            IsAdministrator = sid is not null && admins.Contains(sid),
            CanRemoteLogon = sid is not null && (admins.Contains(sid) || rdpUsers.Contains(sid)),
            PasswordNeverExpires = (flags & UfDontExpirePasswd) != 0,
            PasswordAgeDays = info.Ok && info.PasswordAgeSeconds >= 0
                ? (int)(info.PasswordAgeSeconds / 86400)
                : -1,
            LastLogon = info.Ok && info.LastLogonSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(info.LastLogonSeconds).ToLocalTime()
                : null,
            IsCurrentUser = string.Equals(name, currentUser, StringComparison.OrdinalIgnoreCase),
            HiddenFromLogon = LocalUserVisibility.IsHidden(userList, name),
        };
    }

    private static bool IsBuiltInName(string name)
    {
        foreach (var builtIn in BuiltInNames)
        {
            if (string.Equals(name, builtIn, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>读 USER_INFO_3（标志位 / 密码年龄 / 最近登录）+ level 24（是否 Microsoft 账户）。</summary>
    private static (bool Ok, uint Flags, string Comment, long PasswordAgeSeconds,
        uint LastLogonSeconds, bool MicrosoftAccount) ReadInfo(string name)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var code = Native.NetUserGetInfo(null, name, LevelUserInfo3, out buffer);
            if (code != NerrSuccess || buffer == IntPtr.Zero)
            {
                return (false, 0, string.Empty, -1, 0, false);
            }

            var info = Marshal.PtrToStructure<Native.USER_INFO_3>(buffer);
            return (true, info.usri3_flags,
                Marshal.PtrToStringUni(info.usri3_comment) ?? string.Empty,
                info.usri3_password_age,
                info.usri3_last_logon,
                ReadMicrosoftIdentity(name));
        }
        catch
        {
            return (false, 0, string.Empty, -1, 0, false);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = Native.NetApiBufferFree(buffer);
            }
        }
    }

    /// <summary>
    /// 是否 Microsoft 账户。<c>USER_INFO_24</c> 只读第一个 BOOL（<c>usri24_internet_identity</c>）——
    /// 结构体后半段各版本有差异，只读首字段永远不会越界。系统不支持 level 24 时按"否"处理。
    /// </summary>
    private static bool ReadMicrosoftIdentity(string name)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var code = Native.NetUserGetInfo(null, name, LevelUserInfo24, out buffer);
            return code == NerrSuccess && buffer != IntPtr.Zero && Marshal.ReadInt32(buffer) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = Native.NetApiBufferFree(buffer);
            }
        }
    }

    /// <summary>取内置组成员，统一成 SID 字符串集合。组不存在 / 读不出来时返回空集合。</summary>
    private static HashSet<string> GroupMemberSids(string groupSid)
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var groupName = LocalGroupMembershipService.ResolveGroupName(groupSid);
        if (string.IsNullOrEmpty(groupName))
        {
            return sids;
        }

        var resumeHandle = IntPtr.Zero;
        var examined = 0;
        try
        {
            while (true)
            {
                var buffer = IntPtr.Zero;
                try
                {
                    var code = Native.NetLocalGroupGetMembers(
                        null, groupName, 0, out buffer, PreferMaxLength,
                        out var read, out var total, ref resumeHandle);

                    if (code != NerrSuccess && code != ErrorMoreData)
                    {
                        // 读不全就当空集合：调用方只是根据它标"是不是管理员"，不能因为读不到就崩
                        return sids;
                    }

                    var size = Marshal.SizeOf<Native.LOCALGROUP_MEMBERS_INFO_0>();
                    for (var i = 0; i < read; i++)
                    {
                        var item = Marshal.PtrToStructure<Native.LOCALGROUP_MEMBERS_INFO_0>(buffer + (i * size));
                        if (item.pSid == IntPtr.Zero)
                        {
                            continue;
                        }

                        try
                        {
                            sids.Add(new SecurityIdentifier(item.pSid).Value);
                        }
                        catch
                        {
                            // 单条 SID 解析失败不影响其它成员
                        }
                    }

                    examined += read;

                    if (code != ErrorMoreData || read <= 0)
                    {
                        return sids;
                    }

                    // 说要继续却一条没给 / 已经数满却还没拿到结束信号 → 收手，别死循环
                    if (total > 0 && examined >= total)
                    {
                        return sids;
                    }
                }
                finally
                {
                    if (buffer != IntPtr.Zero)
                    {
                        _ = Native.NetApiBufferFree(buffer);
                    }
                }
            }
        }
        finally
        {
            if (resumeHandle != IntPtr.Zero)
            {
                _ = Native.NetApiBufferFree(resumeHandle);
            }
        }
    }

    private static string DescribeCode(int code) => code switch
    {
        5 => "拒绝访问（需要管理员权限）",
        87 => "参数错误（ERROR_INVALID_PARAMETER）",
        1727 => "本次调用失败，稍后重试即可（RPC 调用中继）",
        _ => $"错误码 {code.ToString(CultureInfo.InvariantCulture)}",
    };

    private static class Native
    {
        /// <summary>USER_INFO_1：只需要名字与标志位。字符串一律 IntPtr，缓冲区归 netapi32。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct USER_INFO_1
        {
            public IntPtr usri1_name;
            public IntPtr usri1_password;
            public uint usri1_password_age;
            public uint usri1_priv;
            public IntPtr usri1_home_dir;
            public IntPtr usri1_comment;
            public uint usri1_flags;
            public IntPtr usri1_script_path;
        }

        /// <summary>USER_INFO_3：与 <c>LocalUserPasswordPolicy</c> 里那份逐字一致（字段顺序不能动）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct USER_INFO_3
        {
            public IntPtr usri3_name;
            public IntPtr usri3_password;
            public uint usri3_password_age;
            public uint usri3_priv;
            public IntPtr usri3_home_dir;
            public IntPtr usri3_comment;
            public uint usri3_flags;
            public IntPtr usri3_script_path;
            public uint usri3_auth_flags;
            public IntPtr usri3_full_name;
            public IntPtr usri3_usr_comment;
            public IntPtr usri3_parms;
            public IntPtr usri3_workstations;
            public uint usri3_last_logon;
            public uint usri3_last_logoff;
            public uint usri3_acct_expires;
            public uint usri3_max_storage;
            public uint usri3_units_per_week;
            public IntPtr usri3_logon_hours;
            public uint usri3_bad_pw_count;
            public uint usri3_num_logons;
            public IntPtr usri3_logon_server;
            public uint usri3_country_code;
            public uint usri3_code_page;
            public uint usri3_user_id;
            public uint usri3_primary_group_id;
            public IntPtr usri3_profile;
            public IntPtr usri3_home_dir_drive;
            public uint usri3_password_expired;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LOCALGROUP_MEMBERS_INFO_0
        {
            public IntPtr pSid;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetUserEnum(
            string? serverName, int level, int filter, out IntPtr buffer,
            int preferredMaximumLength, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetUserGetInfo(
            string? serverName, string userName, int level, out IntPtr buffer);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetLocalGroupGetMembers(
            string? serverName, string groupName, int level, out IntPtr buffer,
            int preferredMaximumLength, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

        [DllImport("netapi32.dll")]
        public static extern int NetApiBufferFree(IntPtr buffer);
    }
}
