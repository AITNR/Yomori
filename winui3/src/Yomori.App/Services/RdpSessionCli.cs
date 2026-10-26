// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text;

namespace Yomori.Services;

/// <summary>
/// <c>Yomori.exe --rdp-sessions</c>：不开窗口，打印会话清单与单账户单会话策略状态。
///
/// <para>【为什么需要这条路】</para>
/// <list type="bullet">
/// <item>「同账户重复会话」的现场是"这台机器上到底有几条会话、分别属于哪个 SID"，
/// 界面里只能看到摘要，用户在电话里也描述不清楚 —— 一条命令拿到全部证据最快。</item>
/// <item>计划书 §六 的验收要求「每轮保存连接前后会话快照」。快照就是这个命令的输出。</item>
/// <item>策略变更（<c>--enable-single-session</c>）必须是**显式**动作，
/// 不能是应用启动的副作用；命令行是最直白的"显式"。</item>
/// </list>
///
/// <para>默认<b>只读</b>；只有带 <c>--enable-single-session</c> /
/// <c>--restore-single-session</c> 才会写注册表。</para>
/// <para>退出码：0 = 正常；1 = 策略操作失败或被组策略挡下；2 = 执行出错。</para>
/// </summary>
public static class RdpSessionCli
{
    public const string SwitchName = "--rdp-sessions";
    public const string EnableSwitch = "--enable-single-session";
    public const string RestoreSwitch = "--restore-single-session";
    public const string AuditSwitch = "--audit";

    public static bool IsInvocation(IReadOnlyList<string> args) => Has(args, SwitchName);

    public static int Run(IReadOnlyList<string> args)
    {
        var text = new StringBuilder();
        var exitCode = 0;

        try
        {
            text.AppendLine("Yomori 会话诊断");
            text.AppendLine("时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            text.AppendLine("主机：" + Environment.MachineName +
                            "　账户：" + Environment.UserName +
                            "　本进程会话：" + (RdpSessionService.TryGetCurrentSessionId()?.ToString() ?? "?"));
            text.AppendLine();

            AppendSessions(text);
            AppendPolicy(text);

            if (Has(args, EnableSwitch))
            {
                var change = RdpSingleSessionPolicy.Enable();
                text.AppendLine();
                text.AppendLine("--- 开启单账户单会话策略 ---");
                text.AppendLine(change.Message);
                if (!change.Ok)
                {
                    exitCode = 1;
                }
            }

            if (Has(args, RestoreSwitch))
            {
                var original = RdpSingleSessionPolicy.ReadOriginalFromLedger();
                var change = RdpSingleSessionPolicy.Restore(original);
                text.AppendLine();
                text.AppendLine("--- 回滚单账户单会话策略 ---");
                text.AppendLine(original is null
                    ? "账本里没有找到可回滚的原始状态。"
                    : change.Message);
                if (!change.Ok)
                {
                    exitCode = 1;
                }
            }

            if (Has(args, AuditSwitch))
            {
                text.AppendLine();
                text.AppendLine("--- 最近审计 ---");
                foreach (var line in RdpSessionGuard.RecentAudit(40))
                {
                    text.AppendLine(line);
                }
            }

            Emit(text.ToString());
            return exitCode;
        }
        catch (Exception ex)
        {
            text.AppendLine();
            text.AppendLine($"执行出错：{ex.GetType().Name}: {ex.Message}");
            Emit(text.ToString());
            return 2;
        }
    }

    private static void AppendSessions(StringBuilder text)
    {
        text.AppendLine("--- 会话清单 ---");
        foreach (var line in RdpSessionGuard.DescribeSessions(null))
        {
            text.AppendLine(line);
        }
    }

    private static void AppendPolicy(StringBuilder text)
    {
        var state = RdpSingleSessionPolicy.Read();
        text.AppendLine();
        text.AppendLine("--- 单账户单会话策略（fSingleSessionPerUser） ---");
        text.AppendLine("状态：" + state.Describe());
        text.AppendLine(state.Detail);
        text.AppendLine("账本：" + RdpSingleSessionPolicy.LedgerPath);
        if (!state.Proven)
        {
            text.AppendLine(
                "说明：该值未确认为「已开启」时，重连能否回到同一条会话无法保证；" +
                "本程序仍会在连接前后核对会话集合，发现预期外的新会话就断开本次客户端。");
        }
    }

    private static bool Has(IReadOnlyList<string> args, string name)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 输出。命令行诊断与安装器都要求"没有窗口也能拿到文本"，
    /// 所以直接写控制台；被重定向时 Console 自己会处理。
    /// </summary>
    private static void Emit(string text)
    {
        try
        {
            Console.Out.Write(text);
            Console.Out.Flush();
        }
        catch
        {
            // 没有控制台（GUI 子系统）时忽略
        }
    }
}
