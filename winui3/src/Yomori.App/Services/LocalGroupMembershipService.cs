// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 本机账户的组授权（计划书 §8）。**按 SID 操作，按 SID 比对。**
/// </summary>
/// <remarks>
/// <para>为什么不用 <c>net localgroup</c> 命令行：它的输出要按本地化文本解析，
/// 组名和成员名都可能带空格与中文，还得处理"命令成功但成员没加上"这种情形。
/// 直接用 netapi32：加/删成员都以 SID 传参，回读也按 SID 比，绕开所有本地化问题。</para>
/// <para>安全边界（计划书 §8 逐条）：不创建用户；不把 <c>DOMAIN\user</c> 截成 <c>user</c>；
/// 版本未知不授权；专业版缺 RDP 组只报异常、不改授管理员；家庭版缺 RDP 组才加 Administrators，
/// 且界面必须显式提示"授予管理员权限"。</para>
/// </remarks>
public static class LocalGroupMembershipService
{
    /// <summary>读本机版本。返回 (分类, EditionID 原文, ProductName 原文)，后两者用于诊断。</summary>
    public static (RdpWindowsEdition Edition, string EditionId, string ProductName) DetectEdition()
    {
        var editionId = "";
        var productName = "";

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            if (key is not null)
            {
                editionId = key.GetValue("EditionID") as string ?? "";
                productName = key.GetValue("ProductName") as string ?? "";
            }
        }
        catch
        {
            // 读不到就是 Unknown，交给 Plan 拒绝
        }

        return (RdpEditions.Classify(editionId, productName), editionId, productName);
    }

    /// <summary>
    /// 由内置组的 SID 取本机上的**本地化组名**；组不存在返回 null。
    /// </summary>
    public static string? ResolveGroupName(string groupSid)
    {
        try
        {
            var sid = new SecurityIdentifier(groupSid);
            var account = sid.Translate(typeof(NTAccount)).Value;

            // 形如 "BUILTIN\Remote Desktop Users"
            var slash = account.IndexOf('\\');
            return slash > 0 ? account.Substring(slash + 1) : account;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把本机账户名解析成 SID。账户不存在返回 null。</summary>
    public static string? ResolveAccountSid(string? accountName)
    {
        var bare = RdpGrantPlanner.BareAccountName(accountName);
        if (bare.Length == 0)
        {
            return null;
        }

        try
        {
            var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, bare)
                .Translate(typeof(SecurityIdentifier));
            return sid.Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 查账户是否在组里。<c>null</c> 表示查不出来（组不存在 / API 失败），
    /// 与"不在组里"必须区分：后者可以直接加，前者加之前得先查清原因。
    /// </summary>
    public static bool? IsMember(string groupName, string accountSid)
    {
        var buffer = IntPtr.Zero;
        var handle = IntPtr.Zero;

        try
        {
            var code = Native.NetLocalGroupGetMembers(
                null, groupName, 0, out buffer, -1, out var read, out _, ref handle);

            if (code != 0)
            {
                return null;
            }

            var size = Marshal.SizeOf<Native.LOCALGROUP_MEMBERS_INFO_0>();
            for (var i = 0; i < read; i++)
            {
                var item = Marshal.PtrToStructure<Native.LOCALGROUP_MEMBERS_INFO_0>(buffer + (i * size));
                if (item.pSid == IntPtr.Zero)
                {
                    continue;
                }

                var sid = new SecurityIdentifier(item.pSid);
                if (string.Equals(sid.Value, accountSid, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = Native.NetApiBufferFree(buffer);
            }
        }
    }

    /// <summary>把账户加入组。已存在返回 (true, 1378, ...)，按幂等处理。</summary>
    public static (bool Ok, int Code, string Error) AddMember(string groupName, string accountSid)
    {
        try
        {
            var sid = new SecurityIdentifier(accountSid);

            // 加成员要以 SID 二进制形式传进去：LOCALGROUP_MEMBERS_INFO_0 的 pSid 指向原始 SID 字节
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);

            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var info = new Native.LOCALGROUP_MEMBERS_INFO_0 { pSid = pinned.AddrOfPinnedObject() };
                var code = Native.NetLocalGroupAddMembers(null, groupName, 0, ref info, 1);
                return code switch
                {
                    0 => (true, 0, ""),
                    1378 => (true, 1378, "已经是该组成员"),
                    _ => (false, code, DescribeCode(code)),
                };
            }
            finally
            {
                pinned.Free();
            }
        }
        catch (Exception ex)
        {
            return (false, -1, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把账户从组里移除。**只有本轮由 Yomori 加入的成员才该走到这里**（计划书 §8），
    /// 本版本没有移除入口，保留实现是为了让授权记录可核销。
    /// </summary>
    public static (bool Ok, int Code, string Error) RemoveMember(string groupName, string accountSid)
    {
        try
        {
            var sid = new SecurityIdentifier(accountSid);
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);

            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var info = new Native.LOCALGROUP_MEMBERS_INFO_0 { pSid = pinned.AddrOfPinnedObject() };
                var code = Native.NetLocalGroupDelMembers(null, groupName, 0, ref info, 1);
                return code switch
                {
                    0 => (true, 0, ""),
                    1377 => (true, 1377, "本来就不在该组"),
                    _ => (false, code, DescribeCode(code)),
                };
            }
            finally
            {
                pinned.Free();
            }
        }
        catch (Exception ex)
        {
            return (false, -1, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 给本机账户授权。调用方必须已经确认「目标就是本机」（见 <c>RdpTargets.IsLocal</c>）——
    /// 远程通道不该走到这里。
    /// </summary>
    public static RdpGrantResult Authorize(string? accountName, RdpWindowsEdition edition)
    {
        var rdpGroupName = ResolveGroupName(RdpEditions.RemoteDesktopUsersSid);
        var adminGroupName = ResolveGroupName(RdpEditions.AdministratorsSid);

        var plan = RdpGrantPlanner.Plan(
            accountName,
            Environment.MachineName,
            edition,
            rdpGroupExists: rdpGroupName is not null,
            adminGroupExists: adminGroupName is not null);

        if (!plan.Allowed)
        {
            return RdpGrantResult.Refused(plan.Reason);
        }

        var bare = RdpGrantPlanner.BareAccountName(accountName);
        var accountSid = ResolveAccountSid(accountName);

        if (accountSid is null)
        {
            // 不创建用户：计划书 §8 第一条。用户得自己在「设置 → 账户」里建
            return RdpGrantResult.Failed(
                $"本机没有账户「{bare}」。Yomori 不创建 Windows 用户，请先在「设置 → 账户 → 其他用户」里创建。");
        }

        var groupName = plan.GroupSid == RdpEditions.AdministratorsSid ? adminGroupName : rdpGroupName;
        if (string.IsNullOrEmpty(groupName))
        {
            return RdpGrantResult.Failed("解析不出目标组的本地名称，可能组已被删除。");
        }

        var before = IsMember(groupName, accountSid);
        if (before is null)
        {
            return RdpGrantResult.Failed($"查不出「{bare}」在「{groupName}」里的现有成员关系，未做修改。");
        }

        if (before == true)
        {
            return new RdpGrantResult
            {
                Outcome = RdpGrantOutcome.AlreadyMember,
                GroupName = groupName,
                GroupSid = plan.GroupSid,
                AccountSid = accountSid,
                Message = $"「{bare}」已经在「{groupName}」里，无需改动。",
                Detail = "执行前成员关系：已在组内；本轮未新增。",
            };
        }

        var (ok, code, error) = AddMember(groupName, accountSid);
        if (!ok)
        {
            return RdpGrantResult.Failed($"加入「{groupName}」失败：{error}", $"Win32/NERR {code}");
        }

        // 回读：加了不等于加上了
        var after = IsMember(groupName, accountSid);
        if (after != true)
        {
            return RdpGrantResult.Failed(
                $"已发出加入请求，但回读不到「{bare}」在「{groupName}」里的成员资格。",
                "回读结果为否或查不出来。");
        }

        var outcome = plan.GroupSid == RdpEditions.AdministratorsSid
            ? RdpGrantOutcome.AddedAdministrators
            : RdpGrantOutcome.AddedRemoteDesktopUsers;

        return new RdpGrantResult
        {
            Outcome = outcome,
            GroupName = groupName,
            GroupSid = plan.GroupSid,
            AccountSid = accountSid,
            WasAddedThisRound = true,
            Message = plan.NeedsAdminWarning
                ? $"家庭版缺少远程桌面用户组：已把「{bare}」加入「{groupName}」（授予管理员权限）。"
                : $"已把「{bare}」加入「{groupName}」。",
            Detail = $"执行前成员关系：不在组内；本轮新增；回读结果：已在组内（{accountSid}）。",
        };
    }

    private static string DescribeCode(int code) => code switch
    {
        5 => "拒绝访问（需要管理员权限）",
        1378 => "已经是该组成员",
        1377 => "本来就不在该组",
        2220 => "找不到指定的本地组",
        2221 => "找不到指定的账户",
        _ => $"错误码 {code}",
    };

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct LOCALGROUP_MEMBERS_INFO_0
        {
            public IntPtr pSid;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetLocalGroupAddMembers(
            string? serverName, string groupName, int level, ref LOCALGROUP_MEMBERS_INFO_0 buffer, int totalEntries);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetLocalGroupDelMembers(
            string? serverName, string groupName, int level, ref LOCALGROUP_MEMBERS_INFO_0 buffer, int totalEntries);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetLocalGroupGetMembers(
            string? serverName,
            string groupName,
            int level,
            out IntPtr buffer,
            int preferredMaximumLength,
            out int entriesRead,
            out int totalEntries,
            ref IntPtr resumeHandle);

        [DllImport("netapi32.dll")]
        public static extern int NetApiBufferFree(IntPtr buffer);
    }
}

/// <summary>
/// 「本轮由 Yomori 新加的组成员」记录。
/// </summary>
/// <remarks>
/// 计划书 §8 要求「只有本轮新增的成员允许通过专门授权恢复记录移除」——
/// 那就必须把"本轮"这件事落到盘上，否则下次启动就分不清谁是我们加的。
/// 本版本没有移除入口，这里只负责记；移除时的核销逻辑留给后续需求。
/// </remarks>
public static class RdpGrantStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Yomori", "rdp", "grants.jsonl");

    public sealed class GrantRecord
    {
        [JsonPropertyName("at")]
        public string At { get; set; } = "";

        [JsonPropertyName("account_sid")]
        public string AccountSid { get; set; } = "";

        [JsonPropertyName("account_name")]
        public string AccountName { get; set; } = "";

        [JsonPropertyName("group_sid")]
        public string GroupSid { get; set; } = "";

        [JsonPropertyName("group_name")]
        public string GroupName { get; set; } = "";

        [JsonPropertyName("action")]
        public string Action { get; set; } = "add";
    }

    /// <summary>追加一条记录。失败静默（记账不该阻断授权本身，但会写进运行日志）。</summary>
    public static bool Append(string accountName, RdpGrantResult result)
    {
        if (!result.WasAddedThisRound || result.AccountSid.Length == 0)
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var record = new GrantRecord
            {
                At = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                AccountSid = result.AccountSid,
                AccountName = RdpGrantPlanner.BareAccountName(accountName),
                GroupSid = result.GroupSid,
                GroupName = result.GroupName,
                Action = "add",
            };

            File.AppendAllText(
                FilePath,
                JsonSerializer.Serialize(record) + Environment.NewLine,
                System.Text.Encoding.UTF8);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读出全部记录（供后续的核销/展示用）。</summary>
    public static IReadOnlyList<GrantRecord> List()
    {
        var records = new List<GrantRecord>();

        try
        {
            if (!File.Exists(FilePath))
            {
                return records;
            }

            foreach (var line in File.ReadLines(FilePath))
            {
                var text = line.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                try
                {
                    var record = JsonSerializer.Deserialize<GrantRecord>(text);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch
                {
                    // 单行损坏不影响其它行
                }
            }
        }
        catch
        {
            // 读不出来就当没有
        }

        return records;
    }
}
