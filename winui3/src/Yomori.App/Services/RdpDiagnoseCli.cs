// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// <c>Yomori.exe --rdp-diagnose</c>：不开窗口，把 RDP 解锁状态与事务账本打出来。
/// </summary>
/// <remarks>
/// <para>计划书 M4 要求「补偿及无界面诊断」。为什么必须有这条路：</para>
/// <list type="bullet">
/// <item>隔离 VM 里验解锁流程时，界面截图没法自动化，退出码 + 文本才是可断言的东西。</item>
/// <item>用户报「解锁失败」时，让他跑这一条命令就能拿到完整现场，比问十个问题快。</item>
/// </list>
/// <para>本命令**只读**：跑检测、读账本，不改任何键、不启动服务、不下载。</para>
/// <para>退出码：0 = 四项全通过；1 = 检测完成但有项没通过；2 = 检测本身失败。</para>
/// </remarks>
public static class RdpDiagnoseCli
{
    public const string SwitchName = "--rdp-diagnose";

    public static bool IsInvocation(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, SwitchName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static int Run()
    {
        var text = new StringBuilder();

        try
        {
            text.AppendLine("Yomori RDP 解锁诊断");
            text.AppendLine("时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            text.AppendLine();

            var snapshot = RdpReadOnlyDetector.Detect();
            text.AppendLine(RdpUnlockDiagnostics.FormatDetail(snapshot));
            text.AppendLine();

            AppendTransactions(text);
            AppendEditionAndGrants(text);

            Emit(text.ToString());
            return snapshot.AllPass ? 0 : 1;
        }
        catch (Exception ex)
        {
            text.AppendLine();
            text.AppendLine($"诊断过程出错：{ex.GetType().Name}: {ex.Message}");
            Emit(text.ToString());
            return 2;
        }
    }

    private static void AppendTransactions(StringBuilder text)
    {
        text.AppendLine("--- 事务账本 ---");
        text.AppendLine("目录：" + RdpTransactionStore.RootDir);

        var list = RdpTransactionStore.List();
        if (list.Count == 0)
        {
            text.AppendLine("（还没有任何事务记录）");
            return;
        }

        foreach (var info in list)
        {
            text.AppendLine($"{info.SummaryText}　id={info.Id}"
                            + (info.ReleaseTag.Length > 0 ? $"　发布={info.ReleaseTag}" : "")
                            + (info.Failure.Length > 0 ? $"　原因={info.Failure}" : ""));
        }

        var incomplete = RdpTransactionStore.ListIncomplete();
        if (incomplete.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"⚠ 有 {incomplete.Count} 个事务没有收尾（崩溃或强杀）。"
                            + "解锁/还原在它被处理掉之前会拒绝开始。");
        }
    }

    /// <summary>
    /// 账户授权现场：策略、目标账户的实际管理员身份、历史授权记录。
    /// </summary>
    /// <remarks>
    /// 用户报「保存账户后没变成管理员」时，按顺序看三件事就够：
    /// ① 该账户在本机是否存在、SID 是多少；② 它实际在不在 Administrators；
    /// ③ 授权记录里那一轮是 added / already / failed / unconfirmed。
    /// 系统版本与 Remote Desktop Users 组只作附加诊断 —— 它们**不参与**授权决策。
    /// </remarks>
    private static void AppendEditionAndGrants(StringBuilder text)
    {
        text.AppendLine();
        text.AppendLine("--- 账户授权（统一管理员策略）---");
        text.AppendLine($"策略版本：{RdpGrantPlanner.PolicyVersion}　目标组恒定 SID={RdpEditions.AdministratorsSid}");

        var (edition, editionId, productName) = LocalGroupMembershipService.DetectEdition();
        var adminGroup = LocalGroupMembershipService.ResolveGroupName(RdpEditions.AdministratorsSid);
        var rdpGroup = LocalGroupMembershipService.ResolveGroupName(RdpEditions.RemoteDesktopUsersSid);

        text.AppendLine($"Administrators 组：{(adminGroup is null ? "不存在（授权会直接失败）" : adminGroup)}"
                        + $"（{RdpEditions.AdministratorsSid}）");
        text.AppendLine($"本机版本：{RdpEditions.Label(edition)}"
                        + $"（EditionID={editionId}，ProductName={productName}）—— 仅诊断，不影响授权");
        text.AppendLine($"Remote Desktop Users 组：{(rdpGroup is null ? "不存在" : rdpGroup)}"
                        + "　（仅诊断：新策略不再往这里加人）");

        AppendTargetAccounts(text, adminGroup);

        var grants = RdpGrantStore.List();
        if (grants.Count == 0)
        {
            text.AppendLine("授权记录：（还没由 Yomori 加过组成员）");
            return;
        }

        text.AppendLine($"授权记录（{RdpGrantStore.FilePath}）：");
        foreach (var grant in grants)
        {
            var action = grant.Action is { Length: > 0 } ? grant.Action : "add";

            // 旧行只有 action=add；新行是 intent/result 两段式，靠 op_id 串起来
            var tail = new StringBuilder();
            if (grant.OpId is { Length: > 0 })
            {
                tail.Append($"　op={grant.OpId}");
            }

            if (grant.BeforeMember is { Length: > 0 })
            {
                tail.Append($"　前={grant.BeforeMember}");
            }

            if (grant.Request is { Length: > 0 })
            {
                tail.Append($"　请求={grant.Request}");
            }

            if (grant.Readback is { Length: > 0 })
            {
                tail.Append($"　回读={grant.Readback}");
            }

            if (grant.Result is { Length: > 0 })
            {
                tail.Append($"　结论={grant.Result}");
            }

            // 组 SID 决定这行是老账（RDP 组）还是新账（管理员组），历史事实不改写
            var groupTag = grant.GroupSid == RdpEditions.AdministratorsSid
                ? "管理员"
                : grant.GroupSid == RdpEditions.RemoteDesktopUsersSid ? "RDP组（历史）" : "其它组";

            text.AppendLine($"· {grant.At}　[{action}]　{grant.AccountName}（{grant.AccountSid}）"
                            + $" → {grant.GroupName}（{grant.GroupSid}，{groupTag}）{tail}");
        }
    }

    /// <summary>逐条通道核对目标账户的实际管理员身份。只读。</summary>
    private static void AppendTargetAccounts(StringBuilder text, string? adminGroup)
    {
        text.AppendLine();
        text.AppendLine("--- 通道目标账户 ---");

        List<RdpChannel> channels;
        try
        {
            var path = AppPaths.ConfigPath;
            if (!File.Exists(path))
            {
                text.AppendLine("（还没有配置文件，无法核对通道账户）");
                return;
            }

            var config = JsonSerializer.Deserialize<AppConfig>(
                File.ReadAllText(path, Encoding.UTF8),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            channels = config?.Rdp?.Channels ?? new List<RdpChannel>();
        }
        catch (Exception ex)
        {
            text.AppendLine($"（配置读不出来：{ex.GetType().Name}）");
            return;
        }

        if (channels.Count == 0)
        {
            text.AppendLine("（配置里没有通道）");
            return;
        }

        foreach (var channel in channels)
        {
            var label = channel.DisplayName;

            if (!RdpTargets.IsLocal(channel.Host))
            {
                text.AppendLine($"· {label}：远程目标（{RdpTargets.Normalize(channel.Host)}），"
                                + "本机不判断、不修改 —— 需在目标计算机上设置管理员");
                continue;
            }

            var plan = RdpGrantPlanner.Plan(channel.User, Environment.MachineName, adminGroup is not null);
            if (!plan.Allowed)
            {
                text.AppendLine($"· {label}：账户「{channel.User}」不可授权（{plan.Reason}）");
                continue;
            }

            var sid = LocalGroupMembershipService.ResolveAccountSid(plan.AccountName);
            if (sid is null)
            {
                text.AppendLine($"· {label}：本机没有账户「{plan.AccountName}」");
                continue;
            }

            var member = LocalGroupMembershipService.IsMember(adminGroup!, sid);
            var state = member switch
            {
                true => "已是管理员",
                false => "不是管理员",
                _ => "成员关系读不出来",
            };

            text.AppendLine($"· {label}：{RdpGrantPlanner.QualifiedName(Environment.MachineName, plan.AccountName)}"
                            + $"（{sid}）→ {state}");
        }
    }

    /// <summary>
    /// 把文本交出去：能挂到父控制台就写到控制台，挂不上就落文件。
    /// </summary>
    /// <remarks>
    /// 本程序是 WinExe，<c>Console.Out</c> 在启动时指向一个无效句柄，必须先
    /// <c>AttachConsole(父进程)</c> 再重新打开标准输出，否则所有输出静默丢弃 ——
    /// 「命令跑了、什么都没打」这种体验比报错更难查。
    /// </remarks>
    private static void Emit(string text)
    {
        try
        {
            if (AttachConsole(AttachParentProcess))
            {
                var stdout = Console.OpenStandardOutput();
                using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
                writer.Write(text);
                writer.WriteLine();
                FreeConsole();
                return;
            }
        }
        catch
        {
            // 落到下面的文件路径
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Yomori", "rdp", "last-diagnose.txt");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch
        {
            // 连文件都写不了就只剩退出码了
        }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
