// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 安装时自动部署会话代理：安装程序把文件铺完后调 <c>Yomori.exe --deploy-agent</c>。
///
/// <para>【为什么要这一步】</para>
/// 会话代理是"目标账户登录后自己跑起来"的常驻进程，装完程序还得有人把它铺到公共目录
/// （<c>%ProgramData%\Yomori\agent</c>）并登记登录自启，远程通道才可能工作。以前这一步只存在于
/// 界面上的「部署会话代理」按钮里 —— 用户装完程序、配好通道、点「连接」，第一个报错就是
/// "代理未部署"，而那句提示并不足以让人意识到要先去点另一个按钮。安装时顺手做掉它，
/// 首次连接的成功率就从"取决于用户有没有读文档"变成"取决于装没装"。
///
/// <para>【这个入口和界面上的部署按钮是什么关系】</para>
/// 同一个部署逻辑（<see cref="RdpSessionService.AutoDeployForInstall"/>），只是去掉交互：
/// 不弹窗、不等用户确认、不参与单实例、退出码就是结论（安装器的日志里能看见）。
/// 界面上的手动部署仍然保留且必要 —— 换目标账户、改桥目录、装完才配通道，都要靠它。
///
/// <para>【出厂设置：不打断正在跑的任务】</para>
/// 安装常常是"覆盖升级"，而这时目标账户里的代理可能正在执行任务。部署要覆盖它的程序文件，
/// 硬来会把人家的活打断（甚至留下半套文件）。所以这里先扫一遍所有桥的状态文件，
/// 只要有活跃任务就整轮跳过 —— 宁可这一次不部署（用户之后可以在程序里点一次），
/// 也不做"装个新版把任务搞砸"这种事。
/// </summary>
public static class AgentAutoDeploy
{
    /// <summary>安装器传给主程序的开关。</summary>
    public const string Switch = "--deploy-agent";

    /// <summary>部署结论写在这个文件里（追加），安装器日志只记退出码。</summary>
    public const string LogFileName = "agent_deploy.log";

    // 退出码约定。安装器的 [Code] 里按这几个值写日志，改动时两处要一起改。
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitAgentBusy = 2;
    public const int ExitNotElevated = 3;

    /// <summary>命令行里有没有 <see cref="Switch"/>。</summary>
    public static bool IsInvocation(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return false;
        }

        foreach (var argument in args)
        {
            if (string.Equals(argument, Switch, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 执行部署，返回进程退出码。**不抛异常**，也不弹任何界面 —— 任何失败都变成
    /// 退出码 + <see cref="LogFileName"/> 里的一段说明，因为调用方在安装向导里，
    /// 没人会去看对话框（且静默安装时弹窗会把安装卡住）。
    /// </summary>
    public static int Run()
    {
        var lines = new List<string>();
        var code = RunCore(lines);
        lines.Add($"结论：{DescribeCode(code)}（退出码 {code}）");
        WriteLog(lines);
        return code;
    }

    private static int RunCore(List<string> lines)
    {
        lines.Add("账户：" + Environment.UserName);
        lines.Add("程序目录：" + AppPaths.BaseDir);
        var shippedExe = Path.Combine(RdpSessionService.ShippedAgentDir, RdpSessionService.AgentExeName);
        lines.Add("代理载荷：" + shippedExe);

        // 版本号是「要不要替换代理」的唯一判据（2026-10-30）：代理端自己有版本，
        // 没动过就不该因为主程序发版而重铺一遍。判据读不出来时按「需要替换」处理。
        var shippedVersion = RdpSessionService.ReadAgentExeVersion(shippedExe);
        var deployedVersion = ReadDeployedVersion();
        lines.Add("代理版本（随包）：" + (shippedVersion.Length > 0 ? shippedVersion : "未知（载荷缺失或无版本号）"));
        if (deployedVersion.Length > 0)
        {
            lines.Add("代理版本（已部署）：" + deployedVersion);
        }

        // 写 ProgramData 与公共启动目录都要管理员。安装器本身是提权的，
        // 走到这里说不是管理员，只可能是有人手动跑了这个开关 —— 说清楚就好，别去弹 UAC。
        if (!RdpSessionService.IsElevated)
        {
            lines.Add("当前进程不是管理员，无法写入 ProgramData 与公共启动目录。");
            return ExitNotElevated;
        }

        if (RdpSessionService.IsAgentDeployed() && RdpSessionService.IsAgentPayloadCurrent())
        {
            lines.Add($"已部署且代理版本一致（{shippedVersion}），不替换。");
            return ExitOk;
        }

        if (shippedVersion.Length > 0)
        {
            lines.Add(deployedVersion.Length > 0
                ? $"代理版本 {deployedVersion} → {shippedVersion}，需要替换。"
                : "已部署副本读不到版本号（改动前的老载荷，或文件正被占用），按需要替换处理。");
        }

        if (TryFindBusyAgent(out var busy))
        {
            lines.Add($"检测到会话代理正在执行任务：{busy}");
            lines.Add("为避免打断任务，本次不做部署（安装完成后可在程序里点「部署会话代理」）。");
            return ExitAgentBusy;
        }

        if (!RdpSessionService.AutoDeployForInstall(out var message))
        {
            foreach (var line in Split(message))
            {
                lines.Add(line);
            }

            return ExitFailed;
        }

        foreach (var line in Split(message))
        {
            lines.Add(line);
        }

        return ExitOk;
    }

    /// <summary>
    /// 已部署副本的代理版本（两个槽里谁先读出来就用谁）。都没部署或都读不到时返回空串。
    ///
    /// 只用于日志里说清「从哪个版本换到哪个版本」，<b>不参与判定</b> ——
    /// 判定统一走 <see cref="RdpSessionService.IsAgentPayloadCurrent"/>，
    /// 两套口径迟早会一个说"要更新"一个说"不用"，那种矛盾最难查。
    /// </summary>
    private static string ReadDeployedVersion()
    {
        foreach (var slot in RdpSessionService.PayloadSlotDirs())
        {
            var version = RdpSessionService.ReadAgentExeVersion(
                Path.Combine(slot, RdpSessionService.AgentExeName));
            if (version.Length > 0)
            {
                return version;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 有没有哪一路桥上正跑着任务。扫的是 <see cref="RdpChannelPaths.BridgeRoot"/> 下
    /// 每个账户目录的 status.json —— 安装时不知道目标是哪个账户，所以不能只查一路。
    ///
    /// 判"在跑"的条件与 <c>RdpSessionService.StopAgentProcess</c> 一致：阶段是 running/stopping
    /// **且**心跳新鲜。只看阶段不行 —— 注销账户会把代理一起杀掉，status.json 会永远停在
    /// 最后一帧的 running，那之后所有安装都会因此跳过部署。
    /// </summary>
    private static bool TryFindBusyAgent(out string description)
    {
        description = string.Empty;

        try
        {
            var root = RdpChannelPaths.BridgeRoot();
            if (!Directory.Exists(root))
            {
                return false;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                RdpStatus? status;
                try
                {
                    status = RdpBridge.For(dir).TryReadStatus();
                }
                catch
                {
                    // 单个桥读不出来（ACL / 临时文件）不代表别的桥空闲，继续看下一个
                    continue;
                }

                if (status?.Phase is not ("running" or "stopping"))
                {
                    continue;
                }

                var heartbeat = RdpHeartbeat.Evaluate(status.UpdatedAt, status.Phase, DateTimeOffset.Now);
                if (!heartbeat.Parsed || heartbeat.IsStale)
                {
                    continue;
                }

                var phase = status.Phase == "stopping" ? "正在按停止请求收尾" : "正在执行任务";
                description = $"{Path.GetFileName(dir)}（{phase}）";
                return true;
            }
        }
        catch
        {
            // 桥目录整个访问不了：当作没有活跃任务，让部署自己报具体的错
        }

        return false;
    }

    private static string DescribeCode(int code) => code switch
    {
        ExitOk => "已完成",
        ExitAgentBusy => "已跳过：代理正在执行任务",
        ExitNotElevated => "未部署：进程没有管理员权限",
        _ => "失败",
    };

    /// <summary>把多行报文拆成若干行（日志按行写，带换行的字符串会把缩进弄乱）。</summary>
    private static IEnumerable<string> Split(string text)
    {
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    /// <summary>
    /// 追加一段日志到 <c>logs\agent_deploy.log</c>。写不进去就算了 —— 部署本身已经做完，
    /// 诊断信息丢失不该变成一个失败。
    /// </summary>
    private static void WriteLog(IReadOnlyList<string> lines)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);

            var builder = new StringBuilder();
            builder.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .AppendLine("] 安装时自动部署会话代理");
            foreach (var line in lines)
            {
                builder.AppendLine("  " + line);
            }

            File.AppendAllText(
                Path.Combine(AppPaths.LogDir, LogFileName),
                builder.ToString(),
                new UTF8Encoding(false));
        }
        catch
        {
            // 日志失败不影响部署结论
        }
    }
}
