// -*- coding: utf-8 -*-
using System;
using System.IO;

namespace Yomori.Services;

/// <summary>
/// 「开始执行」前的会话代理闸门判据。
///
/// <para>与 <see cref="RdpSessionService.IsAgentDeployed"/> 的区别：那个只查公共启动目录的
/// 快捷方式在不在，等于把「代理副本已部署」和「登录自启已开启」压成了同一件事。
/// 这里拆成两个独立判据，缺哪一样就执行对应的那一个动作。</para>
///
/// <para>⚠️ 纯 BCL：Services 目录会被 Yomori.Agent 工程「链接 + 排除」复用编译，
/// 本文件不能出现 Microsoft.UI.Xaml / WinUI 任何类型。</para>
/// </summary>
public static class AgentStartGate
{
    public sealed class Result
    {
        /// <summary>公共副本目录里有没有代理主程序（agent / agent_b 任一槽）。</summary>
        public bool PayloadPresent { get; init; }

        /// <summary>公共启动目录里有没有登录自启快捷方式。</summary>
        public bool AutoStartEnabled { get; init; }

        public bool Ready => PayloadPresent && AutoStartEnabled;
    }

    public enum Action
    {
        None,
        Deploy,
        EnableAutoStart,
    }

    /// <summary>
    /// 判据与真实路径解耦：生产走 <see cref="EvaluateDefault"/>，冒烟用临时目录注入。
    /// 副本候选槽与 RdpSessionService.PayloadSlotDirs() 保持一致（agent / agent_b）。
    /// 只读文件系统，不写盘、不查注册表 —— 任何时候都不该因为闸门把「开始执行」掀掉。
    /// </summary>
    public static Result Evaluate(string payloadRoot, string shortcutPath, string agentExeName, string? legacyExeName = null)
    {
        var payloadPresent = false;
        if (!string.IsNullOrWhiteSpace(payloadRoot) && !string.IsNullOrWhiteSpace(agentExeName))
        {
            foreach (var slot in new[]
                     {
                         Path.Combine(payloadRoot, "agent"),
                         Path.Combine(payloadRoot, "agent_b"),
                     })
            {
                try
                {
                    // 新形态（精简代理）优先；没有随包 agent\ 时 DeployAgent 会回退成
                    // 整目录复制的旧形态（Yomori.exe），那同样是「已部署」，
                    // 闸门不能每次都把人拦下来。
                    if (File.Exists(Path.Combine(slot, agentExeName))
                        || (!string.IsNullOrWhiteSpace(legacyExeName)
                            && File.Exists(Path.Combine(slot, legacyExeName))))
                    {
                        payloadPresent = true;
                        break;
                    }
                }
                catch
                {
                    // 路径非法 / 权限异常：当作这个槽没有
                }
            }
        }

        var autoStart = false;
        try
        {
            autoStart = !string.IsNullOrWhiteSpace(shortcutPath) && File.Exists(shortcutPath);
        }
        catch
        {
            // 同上
        }

        return new Result { PayloadPresent = payloadPresent, AutoStartEnabled = autoStart };
    }

    public static Result EvaluateDefault() => Evaluate(
        RdpSessionService.AgentPayloadRoot,
        RdpSessionService.AgentShortcutPath,
        RdpSessionService.AgentExeName,
        RdpSessionService.LegacyAgentExeName);

    /// <summary>
    /// 缺哪样做哪样：缺副本时一步到位走「部署」（部署本身会连自启项一起建好），
    /// 只缺自启时走轻量的「开启自启」，不重铺 73MB 副本。
    /// </summary>
    public static Action ChooseAction(Result gate)
    {
        if (gate is null || gate.Ready)
        {
            return Action.None;
        }

        return gate.PayloadPresent ? Action.EnableAutoStart : Action.Deploy;
    }

    /// <summary>弹窗标题（纯函数，冒烟按文字锚点断言）。</summary>
    public static string Title(Result gate) => ChooseAction(gate) switch
    {
        Action.Deploy => "需要先部署会话代理",
        Action.EnableAutoStart => "会话代理未开启登录自启",
        _ => "会话代理已就绪",
    };

    /// <summary>弹窗主按钮（纯函数）。</summary>
    public static string PrimaryText(Result gate) => ChooseAction(gate) switch
    {
        Action.Deploy => "部署会话代理",
        Action.EnableAutoStart => "开启登录自启",
        _ => "知道了",
    };

    /// <summary>弹窗正文（纯函数）。</summary>
    public static string Body(Result gate)
    {
        var head = ChooseAction(gate) switch
        {
            Action.Deploy =>
                "还没有部署会话代理。点「部署会话代理」会把程序副本铺到所有用户都能访问的公共目录，并同时写入登录自启项。",
            Action.EnableAutoStart =>
                "代理程序副本已就位，但公共启动目录里没有登录自启项。点「开启登录自启」只补这一项，不会重铺程序副本。",
            _ => "代理已部署且已开启登录自启。",
        };

        return head
            + "\n\n处理完成后才会开始连接通道。"
            + "\n\n注意：代理只在目标账户登录那一刻由启动项拉起；若目标账户已经处于活跃会话，"
            + "仍可能需要先注销目标账户再重新连接，代理才会上线。";
    }
}
