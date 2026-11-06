// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 通道账户的「密码永不过期」标志位（UF_DONT_EXPIRE_PASSWD）维护。
///
/// <para>Windows 本地账户默认受 <c>net accounts</c> 的「密码最长使用期限」（本机 42 天）约束。
/// 密码一到期，NLA 会要求改密，而本程序走的是凭据管理器里的固定密码 + 自动登录，
/// 没有交互式改密流程 —— 表现就是通道用了几十天后突然连不上，且凭据管理器里的旧密码
/// 不会自己更新。<c>net user &lt;user&gt; /expires:never</c> 管的是「账户有效期」，不是这件事。</para>
///
/// <para>本服务只改 <c>USER_INFO_1008.usri1008_flags</c> 的单个标志位，**不设置密码、
/// 不创建用户、不改全局密码策略**。因此凭据管理器里已保存的密码继续有效，
/// 也不需要用户重新「保存凭据」。</para>
///
/// <para>作用范围由调用方通过 <see cref="CollectChannelAccounts"/> 限定：只处理
/// config.json 里 <c>rdp.channels[].user</c> 出现的、指向本机且账户边界清楚的本地账户。
/// 本服务自身不会枚举本机所有账户。</para>
///
/// <para>纯 .NET + Win32，不引用 Microsoft.UI.Xaml —— 本文件也会被链接进会话代理工程，
/// 但代理以 asInvoker 运行，不承载账户策略修改，已在该工程的 Exclude 中排除。</para>
/// </summary>
public static class LocalUserPasswordPolicy
{
    /// <summary>UF_DONT_EXPIRE_PASSWD：密码不受最长使用期限约束。</summary>
    public const uint UfDontExpirePasswd = 0x0001_0000;

    private const int LevelUserInfo3 = 3;
    private const int LevelUserInfo1008 = 1008;
    private const int LevelUserModals0 = 0;
    private const int NerrSuccess = 0;

    /// <summary>USER_MODALS_INFO_0.usrmod0_force_logoff / max_passwd_age 的「永不」哨兵值。</summary>
    private const uint Unlimited = uint.MaxValue;

    /// <summary>
    /// 从通道列表里挑出允许维护的账户名：去重、限定本机、限定本地账户边界。
    ///
    /// <para>刻意**不**按 <c>RdpChannel.Enabled</c> 过滤：那是「任务行要不要用这条通道」的开关，
    /// 与「这个账户是不是已经添加进 Yomori」无关。停用的通道账户同样会在 42 天后过期。</para>
    /// </summary>
    /// <returns>去掉域/机器名前缀后的裸账户名，大小写不敏感去重。</returns>
    public static IReadOnlyList<string> CollectChannelAccounts(
        IEnumerable<RdpChannel>? channels, string? machineName)
    {
        var result = new List<string>();
        if (channels is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            if (channel is null || !RdpTargets.IsLocal(channel.Host))
            {
                continue;
            }

            // 复用授权链的账户边界判定：含 @ 的 UPN / 非本机的 DOMAIN\user 一律拒绝。
            // adminGroupExists 传 true —— 这里只借它的「名字边界」结论，不做加组。
            var plan = RdpGrantPlanner.Plan(channel.User, machineName ?? string.Empty, adminGroupExists: true);
            if (!plan.Allowed)
            {
                continue;
            }

            var bare = RdpGrantPlanner.BareAccountName(plan.AccountName);
            if (bare.Length > 0 && seen.Add(bare))
            {
                result.Add(bare);
            }
        }

        return result;
    }

    /// <summary>
    /// 把一组账户的标志位幂等地对齐到目标状态。
    ///
    /// <para>取消置位（<paramref name="neverExpires"/> = false）时多一道保护：若账户密码的
    /// 实际年龄已经达到或超过系统策略的最大期限，清掉标志位会让 Windows **立刻**把该账户
    /// 判成「密码已过期」，通道当场断连。这种情况跳过该账户并回报失败，由调用方把开关拨回。</para>
    /// </summary>
    /// <returns>AllOk = 所有账户都处理成功；Summary = 可直接写进运行日志的逐账户结果；Changed = 实际改动的账户数。</returns>
    public static (bool AllOk, string Summary, int Changed) Apply(
        IReadOnlyList<string> accounts, bool neverExpires)
    {
        if (accounts is null || accounts.Count == 0)
        {
            return (true, "没有需要维护的通道账户。", 0);
        }

        var lines = new List<string>(accounts.Count);
        var allOk = true;
        var changed = 0;

        // 两阶段：先只读地把整批账户算一遍，再统一写入。
        // 取消置位时尤其要这样 —— 不能出现「A 已还原、B 因密码超期被跳过」的半状态，
        // 那样开关回拨后账户状态反而是撕裂的。
        var pending = new List<KeyValuePair<string, uint>>();
        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account))
            {
                continue;
            }

            var read = ReadFlags(account);
            if (!read.Ok)
            {
                allOk = false;
                lines.Add($"「{account}」读取失败：{read.Detail}");
                continue;
            }

            var hasFlag = (read.Flags & UfDontExpirePasswd) != 0;
            if (hasFlag == neverExpires)
            {
                lines.Add($"「{account}」已是目标状态。");
                continue;
            }

            if (!neverExpires)
            {
                var guard = WouldExpireIfCleared(account);
                if (guard.WouldExpire)
                {
                    // 有一个账户还原不得，整批都不动：调用方据此把开关拨回原位。
                    lines.Add($"「{account}」不能还原：{guard.Detail}");
                    return (false, string.Join(" ", lines) + " 整批未做任何修改。", 0);
                }
            }

            var target = neverExpires
                ? read.Flags | UfDontExpirePasswd
                : read.Flags & ~UfDontExpirePasswd;
            pending.Add(new KeyValuePair<string, uint>(account, target));
        }

        // 读取阶段已有账户查不动时，也别写入：宁可整批跳过，等下次巡检重试。
        if (!allOk)
        {
            return (false, string.Join(" ", lines) + " 整批未做任何修改。", 0);
        }

        foreach (var pair in pending)
        {
            var write = WriteFlags(pair.Key, pair.Value);
            if (!write.Ok)
            {
                allOk = false;
                lines.Add($"「{pair.Key}」写入失败：{write.Detail}");
                continue;
            }

            changed++;
            lines.Add($"「{pair.Key}」已{(neverExpires ? "设为密码永不过期" : "还原为系统默认期限")}。");
        }

        return (allOk, string.Join(" ", lines), changed);
    }

    /// <summary>只读检查单个账户当前的标志位，供诊断与冒烟使用。</summary>
    public static (bool Ok, bool NeverExpires, string Detail) Inspect(string account)
    {
        var read = ReadFlags(account);
        return read.Ok
            ? (true, (read.Flags & UfDontExpirePasswd) != 0, string.Empty)
            : (false, false, read.Detail);
    }

    /// <summary>
    /// 只读诊断：把标志位、密码年龄、系统最大期限一次读全。
    ///
    /// <para>冒烟用它真跑一遍 <c>NetUserGetInfo(3)</c> 与 <c>NetUserModalsGet(0)</c> ——
    /// USER_INFO_3 的字段顺序一旦写错，<c>usri3_password_age</c> 会读到垃圾值，
    /// 取消勾选时的「会不会立刻过期」保护就形同虚设。这里不做任何写入。</para>
    /// </summary>
    public static (bool Ok, bool NeverExpires, long PasswordAgeSeconds,
        long MaxPasswordAgeSeconds, string Detail) ReadPolicyState(string account)
    {
        var flags = ReadFlags(account);
        if (!flags.Ok)
        {
            return (false, false, 0, 0, flags.Detail);
        }

        var age = ReadPasswordAge(account);
        if (!age.Ok)
        {
            return (false, false, 0, 0, age.Detail);
        }

        var policy = ReadMaxPasswordAge();
        if (!policy.Ok)
        {
            return (false, false, 0, 0, policy.Detail);
        }

        return (true, (flags.Flags & UfDontExpirePasswd) != 0,
            age.Seconds, policy.Seconds, string.Empty);
    }

    private static (bool Ok, uint Flags, string Detail) ReadFlags(string account)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            // ⚠️ NetUserGetInfo 不支持 level 1008 —— USER_INFO_1008 是 NetUserSetInfo
            // 专用的写入结构。用 1008 去读会返回 ERROR_INVALID_LEVEL(124)，
            // 表现为启动巡检每个账户都「读取失败」，标志位永远置不上。
            // 读标志位走 level 3 的 USER_INFO_3.usri3_flags。
            var code = Native.NetUserGetInfo(null, account, LevelUserInfo3, out buffer);
            if (code != NerrSuccess)
            {
                return (false, 0, DescribeCode(code));
            }

            var info = Marshal.PtrToStructure<Native.USER_INFO_3>(buffer);
            return (true, info.usri3_flags, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Native.NetApiBufferFree(buffer);
            }
        }
    }

    private static (bool Ok, string Detail) WriteFlags(string account, uint flags)
    {
        try
        {
            var info = new Native.USER_INFO_1008 { usri1008_flags = flags };
            var code = Native.NetUserSetInfo(null, account, LevelUserInfo1008, ref info, out var parmErr);
            if (code != NerrSuccess)
            {
                return (false, $"{DescribeCode(code)}（参数索引 {parmErr.ToString(CultureInfo.InvariantCulture)}）");
            }

            // 写完必须读回：NetUserSetInfo 返回 0 不代表标志位真的落下了（组策略覆盖等）。
            var readback = ReadFlags(account);
            if (!readback.Ok)
            {
                return (false, "写入后无法读回：" + readback.Detail);
            }

            if (readback.Flags != flags)
            {
                return (false, $"写入后读回不一致：期望 0x{flags:X8}，实得 0x{readback.Flags:X8}");
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + "：" + ex.Message);
        }
    }

    /// <summary>判断清掉标志位后，该账户会不会立刻被判成密码过期。读不到就保守地当作「会」。</summary>
    private static (bool WouldExpire, string Detail) WouldExpireIfCleared(string account)
    {
        var age = ReadPasswordAge(account);
        if (!age.Ok)
        {
            return (true, "读不到密码年龄（" + age.Detail + "），为防断连跳过还原。");
        }

        var policy = ReadMaxPasswordAge();
        if (!policy.Ok)
        {
            return (true, "读不到系统密码策略（" + policy.Detail + "），为防断连跳过还原。");
        }

        if (policy.Seconds == 0 || policy.Seconds == Unlimited)
        {
            return (false, "系统全局已设为密码永不过期。");
        }

        if (age.Seconds < policy.Seconds)
        {
            return (false, $"密码年龄 {age.Seconds} 秒，未达策略上限 {policy.Seconds} 秒。");
        }

        return (true,
            $"密码年龄 {age.Seconds.ToString(CultureInfo.InvariantCulture)} 秒已达策略上限 "
            + $"{policy.Seconds.ToString(CultureInfo.InvariantCulture)} 秒；"
            + "请先在 Windows 里修改密码并重新保存凭据，再关闭本开关。");
    }

    private static (bool Ok, long Seconds, string Detail) ReadPasswordAge(string account)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var code = Native.NetUserGetInfo(null, account, LevelUserInfo3, out buffer);
            if (code != NerrSuccess)
            {
                return (false, 0, DescribeCode(code));
            }

            // 用 PtrToStructure 读整个结构，绝不手算字段偏移（对齐在不同位数进程下会变）。
            var info = Marshal.PtrToStructure<Native.USER_INFO_3>(buffer);
            return (true, info.usri3_password_age, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Native.NetApiBufferFree(buffer);
            }
        }
    }

    private static (bool Ok, long Seconds, string Detail) ReadMaxPasswordAge()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var code = Native.NetUserModalsGet(null, LevelUserModals0, out buffer);
            if (code != NerrSuccess)
            {
                return (false, 0, DescribeCode(code));
            }

            var info = Marshal.PtrToStructure<Native.USER_MODALS_INFO_0>(buffer);
            return (true, info.usrmod0_max_passwd_age, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.GetType().Name + "：" + ex.Message);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Native.NetApiBufferFree(buffer);
            }
        }
    }

    private static string DescribeCode(int code) => code switch
    {
        5 => "拒绝访问（需要管理员权限）",
        87 => "参数错误（ERROR_INVALID_PARAMETER）",
        124 => "不支持该信息级别（ERROR_INVALID_LEVEL）",
        2221 => "找不到指定的账户（NERR_UserNotFound）",
        _ => $"错误码 {code.ToString(CultureInfo.InvariantCulture)}",
    };

    private static class Native
    {
        /// <summary>USER_INFO_1008：只有一个标志位字段。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct USER_INFO_1008
        {
            public uint usri1008_flags;
        }

        /// <summary>USER_MODALS_INFO_0：本机密码策略摘要。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct USER_MODALS_INFO_0
        {
            public uint usrmod0_min_passwd_len;
            public uint usrmod0_max_passwd_age;
            public uint usrmod0_min_passwd_age;
            public uint usrmod0_force_logoff;
            public uint usrmod0_password_hist_len;
        }

        /// <summary>
        /// USER_INFO_3：只需要 usri3_password_age，但结构必须写全，
        /// PtrToStructure 才能按平台默认对齐把第三个字段放对位置。
        /// 字符串一律用 IntPtr（LPWSTR），避免封送器擅自释放 netapi32 的缓冲区。
        /// </summary>
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

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetUserGetInfo(
            string? serverName, string userName, int level, out IntPtr bufPtr);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetUserSetInfo(
            string? serverName, string userName, int level, ref USER_INFO_1008 buf, out uint parmErr);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetUserModalsGet(
            string? serverName, int level, out IntPtr bufPtr);

        [DllImport("netapi32.dll")]
        public static extern int NetApiBufferFree(IntPtr buffer);
    }
}
