// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>新建本机账户的结果。**成功与否、成到什么程度，都要说清** —— 见 <see cref="Detail"/>。</summary>
public sealed class LocalUserCreateResult
{
    /// <summary>账户本身是否建成（组关系可能另说）。</summary>
    public bool Ok { get; init; }

    /// <summary>建成的账户名（去掉机器名前缀）。未建成时为空串。</summary>
    public string AccountName { get; init; } = "";

    /// <summary>建成后解析到的账户 SID。解析不到为空串。</summary>
    public string AccountSid { get; init; } = "";

    /// <summary>目标组名（本地化后的名字，例如 Administrators / 远程桌面用户）。</summary>
    public string GroupName { get; init; } = "";

    /// <summary>本轮是否真的把它加进了 <see cref="GroupName"/>。</summary>
    public bool GroupAdded { get; init; }

    /// <summary>它本来就在那个组里（并发场景，本轮不算新增）。</summary>
    public bool AlreadyMember { get; init; }

    /// <summary>本轮是否真的把它从登录界面隐藏了（只影响交互式登录界面的磁贴列表）。</summary>
    public bool HiddenApplied { get; init; }

    /// <summary>Win32 / NERR 错误码；0 表示账户创建这一步成功，-1 表示在托管侧就被拦下了。</summary>
    public int Code { get; init; }

    /// <summary>一句话结论，可直接交界面显示。</summary>
    public string Message { get; init; } = "";

    /// <summary>逐项细节（账户、组、标志位各自的结果），供运行日志与排障。</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// 新建 Windows <b>本机</b>账户，并按通道策略决定把它放进哪个组。
/// </summary>
/// <remarks>
/// <para>为什么需要它：通道要一个「目标账户」，而 Windows 客户端版没有现成的命令行能可靠地
/// 建本地账户（<c>net user</c> 要解析本地化输出，密码走命令行参数还会留在事件日志里）。
/// 直接走 netapi32 可以只传密码、只按 SID 加组，且每一步都能读回校验。</para>
///
/// <para>⛔ <b>USER_INFO_1 的字段顺序不能凭印象写</b>。原生结构是
/// <c>name, password, password_age, priv, home_dir, comment, flags, script_path</c> ——
/// 字符串与 DWORD 交替出现。若按「所有字符串排前面」写，封送器会把 home_dir 指针塞进
/// <c>usri1_password_age</c> 的位置、把 comment 指针塞进 <c>usri1_home_dir</c>，
/// 而 <c>usri1_priv</c> 会读到指针的高 32 位：<c>NetUserAdd</c> 要么返回
/// ERROR_INVALID_PARAMETER，要么**按垃圾标志位建出一个看不出异常的账户**（可能是停用、
/// 也可能是密码永不过期）。冒烟里用 <see cref="FieldOffsets"/> 把六个偏移钉死。</para>
///
/// <para>策略（与通道的既有约定一致）：默认设为 <b>Administrators</b> 成员 ——
/// 通道的「管理员授权」就是这么做的（见 <see cref="LocalGroupMembershipService"/>）；
/// 不勾选管理员时退而加入 <b>Remote Desktop Users</b>，否则账户建出来也登不进远程桌面。
/// 密码永不过期标志位默认跟随配置里的通道账户策略（<c>channel_password_never_expires</c>），
/// 免得账户建完几十天后因为密码到期把通道顶掉。</para>
///
/// <para>纯 .NET + Win32，不引用 Microsoft.UI.Xaml；本文件在代理工程里显式排除 ——
/// 代理以 asInvoker 运行，不该持有「建账户」这种能力。</para>
/// </remarks>
public static class LocalUserAccountService
{
    /// <summary>USER_PRIV_USER：普通用户（不是 Guest，也不是 Administrator 特例账户）。</summary>
    public const uint UserPrivUser = 1;

    /// <summary>UF_NORMAL_ACCOUNT：普通账户（0x0200）。</summary>
    public const uint UfNormalAccount = 0x0000_0200;

    /// <summary>UF_SCRIPT：本地账户的约定标志位，与 <c>net user /add</c> 建出来的账户一致。</summary>
    public const uint UfScript = 0x0000_0001;

    /// <summary>Windows 本地账户名的长度上限。</summary>
    public const int MaxNameLength = 20;

    /// <summary>本程序要求的最短密码长度（这些账户会远程登录，且默认还是本机管理员）。</summary>
    public const int MinPasswordLength = 8;

    private const int LevelUserInfo1 = 1;
    private const int NerrSuccess = 0;

    /// <summary><c>NERR_UserNotFound</c>：查不到这个账户 —— 可以建。</summary>
    public const int NerrUserNotFound = 2221;

    /// <summary>Windows 账户名里不允许出现的字符。</summary>
    private static readonly char[] InvalidNameChars = { '"', '\\', '/', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '@' };

    /// <summary>设备名 / 通配名，Windows 一律拒绝。</summary>
    private static readonly string[] ReservedNames =
    {
        ".", "..",
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>USER_INFO_1 的托管结构大小（只给冒烟做形状回归）。</summary>
    public static int StructSize => Marshal.SizeOf<Native.USER_INFO_1>();

    /// <summary>
    /// 各字段在 USER_INFO_1 里的字节偏移，按「字段名 → 偏移」给出。
    ///
    /// <para>这是本文件唯一能证明「结构没写错」的机械判据：结构体的 Size 一样而字段顺序不同
    /// 也能通过，只有偏移能抓住「所有字符串排前面」这种错法。</para>
    /// </summary>
    public static IReadOnlyDictionary<string, int> FieldOffsets()
    {
        var fields = new[]
        {
            "usri1_name", "usri1_password", "usri1_password_age", "usri1_priv",
            "usri1_home_dir", "usri1_comment", "usri1_flags", "usri1_script_path",
        };

        var map = new Dictionary<string, int>(fields.Length, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            map[field] = (int)Marshal.OffsetOf<Native.USER_INFO_1>(field);
        }

        return map;
    }

    /// <summary>
    /// 校验账户名。<b>纯逻辑</b>：不碰系统，可在冒烟里任意跑。
    /// </summary>
    /// <param name="machineName">本机计算机名；与它同名会被 Windows 拒绝，传了就先拦下。</param>
    public static (bool Ok, string Reason) ValidateName(string? name, string? machineName = null)
    {
        var text = (name ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return (false, "用户名不能为空。");
        }

        if (text.Length > MaxNameLength)
        {
            return (false, $"Windows 本地账户名最长 {MaxNameLength} 个字符，当前 {text.Length} 个。");
        }

        var bad = text.IndexOfAny(InvalidNameChars);
        if (bad >= 0)
        {
            return (false, $"用户名里不能有「{text[bad]}」。Windows 不允许这些字符：\" \\ / [ ] : ; | = , + * ? < > @");
        }

        foreach (var ch in text)
        {
            if (char.IsControl(ch))
            {
                return (false, "用户名里不能有控制字符。");
            }
        }

        if (text.EndsWith('.'))
        {
            return (false, "用户名不能以句点结尾（Windows 会把它当成设备名语法）。");
        }

        foreach (var reserved in ReservedNames)
        {
            if (string.Equals(text, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"「{text}」是 Windows 的保留名，换一个。");
            }
        }

        if (!string.IsNullOrEmpty(machineName)
            && string.Equals(text, machineName, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "用户名不能与计算机名相同。");
        }

        return (true, string.Empty);
    }

    /// <summary>
    /// 校验密码。<b>纯逻辑</b>；<paramref name="confirm"/> 传 null 表示不做二次确认比对。
    /// </summary>
    /// <remarks>
    /// 复杂度规则交给 Windows 自己判（<c>NetUserAdd</c> 会返回 NERR_PasswordTooShort 等），
    /// 这里只拦两件本程序在意的事：空密码、以及一眼就能猜到的短密码 ——
    /// 这些账户默认是本机管理员，还能远程登录。
    /// </remarks>
    public static (bool Ok, string Reason) ValidatePassword(string? password, string? confirm = null)
    {
        var text = password ?? string.Empty;

        if (text.Length == 0)
        {
            return (false, "密码不能为空（空密码账户等于把远程桌面敞开）。");
        }

        if (text.Length < MinPasswordLength)
        {
            return (false, $"密码至少 {MinPasswordLength} 位。"
                           + "这个账户会被用来远程登录，并且默认是本机管理员。");
        }

        if (confirm is not null && !string.Equals(text, confirm, StringComparison.Ordinal))
        {
            return (false, "两次输入的密码不一致。");
        }

        return (true, string.Empty);
    }

    /// <summary>
    /// 只读查询本机有没有这个账户。
    /// </summary>
    /// <remarks>
    /// 三态：<c>Found=true</c> 已存在；<c>Found=false &amp;&amp; Code==2221</c> 确定不存在；
    /// 其它 <c>Code</c> 表示查不清 —— 调用方必须停手，不能把「查不出来」当成「可以建」。
    /// </remarks>
    public static (bool Found, int Code, string Error) QueryExists(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return (false, NerrSuccess, "账户名为空。");
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            // ⚠️ NetUserGetInfo 的字符串字段全部用 IntPtr 接：netapi32 的返回缓冲区由
            // NetApiBufferFree 释放，若声明成 string 会让封送器擅自去释放内部指针。
            var code = Native.NetUserGetInfo(null, text, LevelUserInfo1, out buffer);
            if (code == NerrSuccess)
            {
                return (true, NerrSuccess, string.Empty);
            }

            return (false, code, DescribeCode(code));
        }
        catch (Exception ex)
        {
            return (false, -1, $"{ex.GetType().Name}：{ex.Message}");
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
    /// 创建账户，并按需加入组。<b>会真的改本机账户</b> —— 冒烟里绝不调用。
    /// </summary>
    /// <param name="makeAdmin">
    /// true = 加入 Administrators（与通道的管理员授权策略一致）；
    /// false = 加入 Remote Desktop Users（否则账户建出来也登不进远程桌面）。
    /// </param>
    /// <param name="neverExpires">是否置 UF_DONT_EXPIRE_PASSWD（跟随通道密码策略开关）。</param>
    /// <param name="hiddenFromLogon">
    /// 是否把这个账户从 Windows 登录界面隐藏（<see cref="LocalUserVisibility"/>）。
    /// 只影响交互式登录界面的磁贴列表，远程桌面照样能用凭据登录。
    /// </param>
    public static LocalUserCreateResult Create(
        string? name, string? password, string comment, bool makeAdmin, bool neverExpires,
        bool hiddenFromLogon = false)
    {
        var nameCheck = ValidateName(name, Environment.MachineName);
        if (!nameCheck.Ok)
        {
            return Failure(string.Empty, -1, nameCheck.Reason);
        }

        var bare = name!.Trim();

        var passwordCheck = ValidatePassword(password);
        if (!passwordCheck.Ok)
        {
            return Failure(bare, -1, passwordCheck.Reason);
        }

        // 先只读确认「确实没有」，再动手。查不清就不建 —— 半个账户比没账户更难收场。
        var query = QueryExists(bare);
        if (query.Found)
        {
            return Failure(bare, -1, $"本机已有账户「{bare}」，换一个名字（或直接用现有账户建通道）。");
        }

        if (query.Code != NerrUserNotFound)
        {
            return Failure(bare, query.Code, $"查不清本机是否已有账户「{bare}」（{query.Error}），未做任何修改。");
        }

        var flags = UfScript | UfNormalAccount
                    | (neverExpires ? LocalUserPasswordPolicy.UfDontExpirePasswd : 0u);

        var info = new Native.USER_INFO_1
        {
            usri1_name = bare,
            usri1_password = password,

            // ⚠️ 这四个字段的顺序就是原生顺序，别为了"好看"重排（见类注释）。
            usri1_password_age = 0,
            usri1_priv = UserPrivUser,
            usri1_home_dir = null,
            usri1_comment = comment,

            usri1_flags = flags,
            usri1_script_path = null,
        };

        int code;
        uint parmErr;
        try
        {
            code = Native.NetUserAdd(null, LevelUserInfo1, ref info, out parmErr);
        }
        catch (Exception ex)
        {
            return Failure(bare, -1, $"调用 NetUserAdd 时出错：{ex.GetType().Name}：{ex.Message}");
        }

        if (code != NerrSuccess)
        {
            // parmErr 指明是第几个字段被拒 —— 结构写错时它会指向一个毫不相干的字段，
            // 这条信息在排障时比错误码本身有用。
            var field = parmErr == 0 && code == 87 ? string.Empty : $"（被拒字段：{FieldName(parmErr)}）";
            return Failure(bare, code, $"创建账户失败：{DescribeCode(code)}{field}");
        }

        // ---- 账户已经建成。后面每一步都必须如实回报，不能因为"账户建好了"就把组关系 /
        //      登录界面可见性的失败也算成功 —— 用户拿一个非管理员、也不在远程桌面组里的账户
        //      去建通道，会在连接阶段报一个看起来毫不相干的错。 ----

        // 隐藏放在加组之前：它只动注册表，与组关系无关，先做完能让后面几条提前返回都带上结论
        var hidden = hiddenFromLogon
            ? LocalUserVisibility.SetHidden(bare, true)
            : (Ok: true, Detail: "未勾选隐藏，登录界面照常显示该账户。");

        var hiddenNote = $";登录界面：{(hidden.Ok ? "已按选择处理" : $"隐藏失败：{hidden.Detail}")}";

        var sid = LocalGroupMembershipService.ResolveAccountSid(bare);
        if (sid is null)
        {
            return new LocalUserCreateResult
            {
                Ok = true,
                AccountName = bare,
                HiddenApplied = hidden.Ok && hiddenFromLogon,
                Message = $"已创建账户「{bare}」，但解析不到它的 SID，未能加入任何组。",
                Detail = $"NetUserAdd 成功；SID 解析失败。请到「计算机管理 → 本地用户和组」确认账户状态。"
                         + hiddenNote,
                Code = NerrSuccess,
            };
        }

        var groupSid = makeAdmin ? RdpEditions.AdministratorsSid : RdpEditions.RemoteDesktopUsersSid;
        var groupName = LocalGroupMembershipService.ResolveGroupName(groupSid);
        if (groupName is null)
        {
            return new LocalUserCreateResult
            {
                Ok = true,
                AccountName = bare,
                AccountSid = sid,
                HiddenApplied = hidden.Ok && hiddenFromLogon,
                Message = $"已创建账户「{bare}」，但本机找不到{(makeAdmin ? "管理员组" : "远程桌面用户组")}，未能加组。",
                Detail = $"NetUserAdd 成功；SID={sid}；目标组 SID={groupSid} 在本机不存在。{hiddenNote}",
                Code = NerrSuccess,
            };
        }

        var (memberOk, memberCode, memberError) = LocalGroupMembershipService.AddMember(groupName, sid);

        var detail =
            $"NetUserAdd 成功；SID={sid}；标志位=0x{flags:X8}"
            + $"（{(neverExpires ? "密码永不过期" : "密码按系统期限")}）；"
            + $"目标组「{groupName}」（{groupSid}）："
            + (memberOk ? (memberCode == 1378 ? "本来就在组内" : "本轮已加入") : $"加入失败：{memberError}（{memberCode}）")
            + ";默认组 Users 由 NetUserAdd 自动加入。"
            + hiddenNote;

        if (!memberOk)
        {
            return new LocalUserCreateResult
            {
                Ok = true,
                AccountName = bare,
                AccountSid = sid,
                HiddenApplied = hidden.Ok && hiddenFromLogon,
                GroupName = groupName,
                Message = $"已创建账户「{bare}」，但加入「{groupName}」失败：{memberError}",
                Detail = detail,
                Code = memberCode,
            };
        }

        return new LocalUserCreateResult
        {
            Ok = true,
            AccountName = bare,
            AccountSid = sid,
            GroupName = groupName,
            GroupAdded = memberCode != 1378,
            AlreadyMember = memberCode == 1378,
            HiddenApplied = hidden.Ok && hiddenFromLogon,
            Message = makeAdmin
                ? $"已创建账户「{bare}」，并设为本机管理员。"
                : $"已创建账户「{bare}」，已加入「{groupName}」（本机管理员未勾选）。",
            Detail = detail,
            Code = NerrSuccess,
        };
    }

    /// <summary>把 NERR / Win32 错误码翻成人话。</summary>
    public static string DescribeCode(int code) => code switch
    {
        0 => "成功",
        5 => "拒绝访问（需要以管理员身份运行）",
        87 => "参数错误（ERROR_INVALID_PARAMETER）",
        2202 => "用户名不合法（NERR_BadUsername）",
        2203 => "密码不合法（NERR_BadPassword）",
        2220 => "找不到指定的本地组（NERR_GroupNotFound）",
        2221 => "找不到指定的账户（NERR_UserNotFound）",
        2224 => "该账户已存在（NERR_UserExists）",
        2236 => "该账户已在组内（NERR_UserInGroup）",
        2245 => "密码不满足本机密码策略（NERR_PasswordTooShort：太短或不够复杂）",
        2246 => "密码太长（NERR_PasswordTooLong）",
        2248 => "密码与历史记录冲突，换一个新密码（NERR_PasswordHistConflict）",
        1378 => "指定的账户名已是该组成员",
        1377 => "指定的账户不是该组成员",
        -1 => "本程序在调用系统接口之前就拦下了",
        _ => $"错误码 {code.ToString(CultureInfo.InvariantCulture)}",
    };

    /// <summary>USER_INFO_1 的字段序号（<c>NetUserAdd</c> 的 parm_err 输出）。</summary>
    private static string FieldName(uint index) => index switch
    {
        0 => "usri1_name",
        1 => "usri1_password",
        2 => "usri1_password_age",
        3 => "usri1_priv",
        4 => "usri1_home_dir",
        5 => "usri1_comment",
        6 => "usri1_flags",
        7 => "usri1_script_path",
        _ => $"第 {index.ToString(CultureInfo.InvariantCulture)} 项（超出结构范围，结构布局可能写错了）",
    };

    private static LocalUserCreateResult Failure(string account, int code, string message) => new()
    {
        Ok = false,
        AccountName = account,
        Code = code,
        Message = message,
        Detail = message,
    };

    private static class Native
    {
        /// <summary>
        /// USER_INFO_1 —— <b>字段顺序与原生结构逐字对齐</b>：
        /// name, password, password_age, priv, home_dir, comment, flags, script_path。
        /// 字符串用 <c>string</c> 靠 CharSet.Unicode 封送成 LPWSTR 是安全的：这是**入参**结构，
        /// 缓冲区由封送器分配、调用返回后由它自己释放（与 NetUserGetInfo 的出参结构相反）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct USER_INFO_1
        {
            public string? usri1_name;
            public string? usri1_password;
            public uint usri1_password_age;
            public uint usri1_priv;
            public string? usri1_home_dir;
            public string? usri1_comment;
            public uint usri1_flags;
            public string? usri1_script_path;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetUserAdd(
            string? serverName, int level, ref USER_INFO_1 buffer, out uint parmError);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetUserGetInfo(
            string? serverName, string userName, int level, out IntPtr buffer);

        [DllImport("netapi32.dll")]
        public static extern int NetApiBufferFree(IntPtr buffer);
    }
}
