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
/// 本机账户的组授权。**按 SID 操作，按 SID 比对。**
/// </summary>
/// <remarks>
/// <para>为什么不用 <c>net localgroup</c> 命令行：它的输出要按本地化文本解析，
/// 组名和成员名都可能带空格与中文，还得处理"命令成功但成员没加上"这种情形。
/// 直接用 netapi32：加/删成员都以 SID 传参，回读也按 SID 比，绕开所有本地化问题。</para>
/// <para>策略（2026-10-26 增量计划书）：本机被控账户**一律设为管理员**，目标组恒为
/// Administrators；不创建用户；不把 <c>DOMAIN\user</c> 截成 <c>user</c>；
/// 管理员组不可用就失败，不"降级"去加 Remote Desktop Users；
/// 不执行「移除原组 → 加入管理员组」的搬迁链路（只追加，不删任何组关系）。</para>
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
    /// 查账户是否在组里。<c>null</c> 表示查不出来（组不存在 / API 失败 / 分页读不完整），
    /// 与"不在组里"必须区分：后者可以直接加，前者先得查清原因，绝不能把读取不完整
    /// 当成"不是成员"——那会导出一个"我加的"假结论。
    /// </summary>
    /// <remarks>
    /// <c>NetLocalGroupGetMembers</c> 在成员多到一次给不全时返回 <c>ERROR_MORE_DATA</c>(234)，
    /// 此时必须带 <c>resume_handle</c> 继续读，读全了才能断言"不在组里"。
    /// </remarks>
    public static bool? IsMember(string groupName, string accountSid)
    {
        if (string.IsNullOrEmpty(groupName) || string.IsNullOrEmpty(accountSid))
        {
            return null;
        }

        var resumeHandle = IntPtr.Zero;
        var size = Marshal.SizeOf<Native.LOCALGROUP_MEMBERS_INFO_0>();
        var examined = 0;

        try
        {
            while (true)
            {
                var buffer = IntPtr.Zero;
                try
                {
                    var code = Native.NetLocalGroupGetMembers(
                        null, groupName, 0, out buffer, -1, out var read, out var total, ref resumeHandle);

                    if (code != 0 && code != Native.ErrorMoreData)
                    {
                        return null;
                    }

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

                    examined += read;

                    if (code != Native.ErrorMoreData)
                    {
                        // 读完了：到这里才能断言"不在组里"
                        return false;
                    }

                    // 说要继续却一条没给 / 已经数满却还没拿到结束信号 → 读取不完整
                    if (read <= 0)
                    {
                        return null;
                    }

                    if (total > 0 && examined >= total)
                    {
                        return false;
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
        catch
        {
            return null;
        }
        finally
        {
            // 续读句柄由调用方负责释放（MSDN：resume_handle 用完要交给 NetApiBufferFree）
            if (resumeHandle != IntPtr.Zero)
            {
                _ = Native.NetApiBufferFree(resumeHandle);
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
    /// 授权前的只读核对：身份边界、账户 SID、是否已经是管理员。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="AuthorizeAsAdministrator"/> 分开，是为了先看清再决定要不要弹确认框：
    /// 已经是管理员的账户不该弹框，账户不存在 / 身份不清的账户更不该弹框。
    /// 本方法只读，不改任何组成员关系。
    /// </remarks>
    public static RdpGrantPrecheck Inspect(string? accountName)
    {
        var adminGroupName = ResolveGroupName(RdpEditions.AdministratorsSid);
        var plan = RdpGrantPlanner.Plan(accountName, Environment.MachineName, adminGroupName is not null);

        if (!plan.Allowed)
        {
            return new RdpGrantPrecheck
            {
                Plan = plan,
                GroupName = adminGroupName ?? "",
                Failure = plan.Reason,
            };
        }

        var accountSid = ResolveAccountSid(plan.AccountName);
        if (accountSid is null)
        {
            // 不创建用户：用户得自己在「设置 → 账户」里建
            return new RdpGrantPrecheck
            {
                Plan = plan,
                GroupName = adminGroupName!,
                Failure = $"本机没有账户「{plan.AccountName}」。Yomori 不创建 Windows 用户，"
                          + "请先在「设置 → 账户 → 其他用户」里创建。",
            };
        }

        var member = IsMember(adminGroupName!, accountSid);
        return new RdpGrantPrecheck
        {
            Plan = plan,
            GroupName = adminGroupName!,
            AccountSid = accountSid,
            IsAlreadyMember = member,
            Failure = member is null
                ? $"查不出「{plan.AccountName}」在「{adminGroupName}」里的现有成员关系，未做任何修改。"
                : "",
        };
    }

    /// <summary>
    /// 把本机账户设为本机管理员。调用方必须已经确认「目标就是本机」（见 <c>RdpTargets.IsLocal</c>）——
    /// 远程通道不该走到这里。
    /// </summary>
    /// <param name="accountName">通道里填的账户名。</param>
    /// <param name="confirmedAccountSid">
    /// 确认对话框之前解析到的账户 SID。传入后会在执行前再复解析一次并比对：
    /// 同名账户在确认期间被删除重建时，SID 会变，此时必须停下来，不能把管理员身份
    /// 授给"另一个同名的账户"。
    /// </param>
    public static RdpGrantResult AuthorizeAsAdministrator(string? accountName, string? confirmedAccountSid = null)
    {
        var adminGroupName = ResolveGroupName(RdpEditions.AdministratorsSid);
        var plan = RdpGrantPlanner.Plan(accountName, Environment.MachineName, adminGroupName is not null);

        if (!plan.Allowed)
        {
            return RdpGrantResult.Refused(plan.Reason);
        }

        var accountSid = ResolveAccountSid(plan.AccountName);
        if (accountSid is null)
        {
            return RdpGrantResult.Failed(
                $"本机已经找不到账户「{plan.AccountName}」（可能在确认后被删除）。Yomori 不创建 Windows 用户。");
        }

        if (confirmedAccountSid is { Length: > 0 }
            && !string.Equals(confirmedAccountSid, accountSid, StringComparison.OrdinalIgnoreCase))
        {
            return RdpGrantResult.Refused(
                $"账户「{plan.AccountName}」的身份在确认之后变了（{confirmedAccountSid} → {accountSid}），未做任何修改。",
                "确认时解析到的 SID 与执行前复解析的 SID 不一致，按计划书 §4.1 停止授权。");
        }

        var groupName = adminGroupName!;
        var groupSid = plan.GroupSid;
        var bare = plan.AccountName;

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
                AccountName = bare,
                AccountSid = accountSid,
                GroupName = groupName,
                GroupSid = groupSid,
                ReadbackMember = true,
                Message = $"账户「{bare}」已是本机管理员，无需修改。",
                Detail = "执行前成员关系：已在 Administrators 内；本轮未新增。",
            };
        }

        // 先落盘 intent 再改权限：进程在写的中间被崩溃/强杀时，仍能分辨"打算做什么"（计划书 §6）
        var (opId, intentSaved) = RdpGrantStore.AppendIntent(bare, accountSid, groupSid, groupName, "false");

        var (ok, code, error) = AddMember(groupName, accountSid);
        if (!ok)
        {
            _ = RdpGrantStore.AppendOutcome(
                bare, opId, accountSid, groupSid, groupName,
                before: "false", request: $"failed({code})", readback: "not-read", final: "failed");

            return RdpGrantResult.Failed(
                $"加入「{groupName}」失败：{error}", $"Win32/NERR {code}", code);
        }

        // 回读：加了不等于加上了
        var after = IsMember(groupName, accountSid);
        if (after != true)
        {
            _ = RdpGrantStore.AppendOutcome(
                bare, opId, accountSid, groupSid, groupName,
                before: "false", request: code == 0 ? "ok" : code.ToString(CultureInfo.InvariantCulture),
                readback: after is null ? "unreadable" : "absent", final: "unconfirmed");

            return RdpGrantResult.Unconfirmed(
                $"已发出加入「{groupName}」的请求，但回读确认不了「{bare}」的成员资格。管理员授权结果未确认，请重新检查。",
                after is null ? "回读：查不出来。" : "回读：确认不在组内。",
                code);
        }

        if (code == 1378)
        {
            // 并发场景：确认前它还不是成员，但发出请求时别的操作已经加了 —— 不能记成本轮新增
            _ = RdpGrantStore.AppendOutcome(
                bare, opId, accountSid, groupSid, groupName,
                before: "false", request: "already(1378)", readback: "present", final: "already");

            return new RdpGrantResult
            {
                Outcome = RdpGrantOutcome.AlreadyMember,
                AccountName = bare,
                AccountSid = accountSid,
                GroupName = groupName,
                GroupSid = groupSid,
                RequestAccepted = true,
                RequestCode = code,
                ReadbackMember = true,
                Message = $"账户「{bare}」已是本机管理员，无需修改。",
                Detail = "执行前成员关系：不在组内；加入请求返回 1378（已在组内，可能由其它操作并发加入）；"
                         + "回读：已在组内。本轮不算新增。",
            };
        }

        var resultSaved = RdpGrantStore.AppendOutcome(
            bare, opId, accountSid, groupSid, groupName,
            before: "false", request: code == 0 ? "ok" : code.ToString(CultureInfo.InvariantCulture),
            readback: "present", final: "added");

        return new RdpGrantResult
        {
            Outcome = RdpGrantOutcome.AddedAdministrators,
            AccountName = bare,
            AccountSid = accountSid,
            GroupName = groupName,
            GroupSid = groupSid,
            RequestAccepted = true,
            RequestCode = code,
            ReadbackMember = true,
            WasAddedThisRound = true,
            Message = $"已将账户「{bare}」设为本机管理员。",
            Detail = $"执行前成员关系：不在组内；本轮新增；回读结果：已在 Administrators 内（{accountSid}）。",
            RecordSaved = intentSaved && resultSaved,
        };
    }

    private static string DescribeCode(int code) => code switch
    {
        5 => "拒绝访问（需要管理员权限）",
        234 => "成员列表没读完（ERROR_MORE_DATA）",
        1378 => "已经是该组成员",
        1377 => "本来就不在该组",
        2220 => "找不到指定的本地组",
        2221 => "找不到指定的账户",
        _ => $"错误码 {code}",
    };

    private static class Native
    {
        /// <summary><c>ERROR_MORE_DATA</c>：还有成员没返回，要带 resume_handle 继续读。</summary>
        public const int ErrorMoreData = 234;

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
/// 本机账户授权的事实记录（<c>grants.jsonl</c>，一行一条 JSON）。
/// </summary>
/// <remarks>
/// <para>为什么必须落到盘上：授权记录是"哪些管理员身份是我们授的"的唯一证据。没有它，
/// 日后既不敢撤销，也说不清是谁加的。</para>
/// <para>计划书 §6 要求「先持久化 intent 再改权限，防止进程崩溃后无法分辨是否已授予」，
/// 所以一次授权写两行：<c>action=intent</c>（执行前）与 <c>action=result</c>（执行后），
/// 两行靠 <c>op_id</c> 串起来。只有 intent 没有 result = 执行阶段崩了，需要人工按 SID 复核。</para>
/// <para>历史行（旧策略写下的 <c>action=add</c>、只有 account/group 四件套）继续可读，
/// <b>不重写</b>：旧记录里 <c>group_sid=S-1-5-32-555</c> 就是当时真实发生的事。</para>
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

        /// <summary><c>add</c>=旧策略的事实行；<c>intent</c>/<c>result</c>=本策略的两段式记录。</summary>
        [JsonPropertyName("action")]
        public string Action { get; set; } = "add";

        /// <summary>一次授权的操作 ID，intent 与 result 两行共用。旧行没有这个字段。</summary>
        [JsonPropertyName("op_id")]
        public string OpId { get; set; } = "";

        /// <summary>策略版本（<see cref="RdpGrantPlanner.PolicyVersion"/>）。</summary>
        [JsonPropertyName("policy")]
        public string Policy { get; set; } = "";

        [JsonPropertyName("machine")]
        public string Machine { get; set; } = "";

        /// <summary>执行前是否已是成员。</summary>
        [JsonPropertyName("before_member")]
        public string BeforeMember { get; set; } = "";

        /// <summary>加入请求的结果：<c>ok</c> / <c>already(1378)</c> / <c>failed(码)</c>。</summary>
        [JsonPropertyName("request")]
        public string Request { get; set; } = "";

        /// <summary>回读结果：<c>present</c> / <c>absent</c> / <c>unreadable</c> / <c>not-read</c>。</summary>
        [JsonPropertyName("readback")]
        public string Readback { get; set; } = "";

        /// <summary>最终结论：<c>added</c> / <c>already</c> / <c>failed</c> / <c>unconfirmed</c>。</summary>
        [JsonPropertyName("result")]
        public string Result { get; set; } = "";
    }

    /// <summary>
    /// 执行前写下"打算做什么"。返回操作 ID 与是否真的落盘。
    /// </summary>
    public static (string OpId, bool Saved) AppendIntent(
        string accountName, string accountSid, string groupSid, string groupName, string beforeMember)
    {
        var opId = Guid.NewGuid().ToString("N")[..12];

        var saved = Write(new GrantRecord
        {
            AccountSid = accountSid,
            AccountName = accountName,
            GroupSid = groupSid,
            GroupName = groupName,
            Action = "intent",
            OpId = opId,
            Policy = RdpGrantPlanner.PolicyVersion,
            Machine = Environment.MachineName,
            BeforeMember = beforeMember,
            Request = "pending",
            Readback = "pending",
            Result = "pending",
        });

        return (opId, saved);
    }

    /// <summary>执行后补写结论行，与 intent 靠 <paramref name="opId"/> 串起来。</summary>
    public static bool AppendOutcome(
        string accountName,
        string opId,
        string accountSid,
        string groupSid,
        string groupName,
        string before,
        string request,
        string readback,
        string final)
    {
        return Write(new GrantRecord
        {
            AccountSid = accountSid,
            AccountName = accountName,
            GroupSid = groupSid,
            GroupName = groupName,
            Action = "result",
            OpId = opId,
            Policy = RdpGrantPlanner.PolicyVersion,
            Machine = Environment.MachineName,
            BeforeMember = before,
            Request = request,
            Readback = readback,
            Result = final,
        });
    }

    /// <summary>
    /// 旧入口：只记"本轮新增"这一种事实。保留给兼容调用方与冒烟断言；
    /// 新流程用 <see cref="AppendIntent"/> + <see cref="AppendOutcome"/>。
    /// </summary>
    public static bool Append(string accountName, RdpGrantResult result)
    {
        if (!result.WasAddedThisRound || result.AccountSid.Length == 0)
        {
            return false;
        }

        return Write(new GrantRecord
        {
            AccountSid = result.AccountSid,
            AccountName = RdpGrantPlanner.BareAccountName(accountName),
            GroupSid = result.GroupSid,
            GroupName = result.GroupName,
            Action = "add",
            Policy = RdpGrantPlanner.PolicyVersion,
            Machine = Environment.MachineName,
            BeforeMember = "false",
            Request = "ok",
            Readback = "present",
            Result = "added",
        });
    }

    /// <summary>追加一行。失败返回 false —— 记账失败不能吞掉，调用方要把"记录保存失败"讲给用户。</summary>
    private static bool Write(GrantRecord record)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            record.At = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);

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
