// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>「会话通道」子菜单里一条**配置通道**的输入快照（不含任何界面类型）。</summary>
public readonly record struct ChannelMenuChannel(
    string Id, string Name, string Host, string User, bool Enabled);

/// <summary>「会话通道」子菜单里一条**本轮会话**的输入快照（不含任何界面类型）。</summary>
public readonly record struct ChannelMenuSession(
    string ChannelId, string DisplayName, string TargetHost, string TargetUser,
    string StatusText, bool IsRemote, bool CompletionReported);

/// <summary>
/// 「会话通道」父项下该列哪些通道 —— 纯函数，冒烟可直接编译跑行为断言。
///
/// <para>两路来源：
/// ① 配置里启用中的通道（不跑任务也能点进去手动连画面看）；
/// ② 本轮**还没收尾**的远程会话 —— 正在连接 / 执行 / 停止 / 注销中的通道即使配置里
///    已停用或删除，也必须看得见，否则用户会丢失正在跑的画面入口。</para>
///
/// <para>🔑 ② 的判据不能只用 <c>IsRemote</c>：<c>RdpChannelSession.IsRemote</c> 只是
/// <c>Channel is not null</c>，与「是否在跑」毫无关系；而 <c>_sessions</c> 只在
/// 「开始执行」的 <c>ResetSessions()</c> 里清空，一轮跑完会话对象会一直留到下次执行。
/// 用 <c>IsRemote</c> 当「在跑」，跑过任务的旧通道就会被永久加回菜单 —— 停用也关不掉
/// （2026-11-09 用户实测：新加的通道停用后能消失，原来已有的关不掉）。</para>
///
/// <para>收尾标记 <c>CompletionReported</c> 由主控端自己在
/// <c>ReportSessionCompletion</c> 里打，不依赖代理上报的 phase 字符串，
/// 比 <c>IsActive</c>（只看 <c>Phase == "running"</c>）更可靠：连接中、正在注销
/// 的会话都还没收尾，仍应保留可见。</para>
/// </summary>
public static class ChannelMenuPolicy
{
    /// <summary>
    /// 按「配置顺序 → 未收尾会话补位」算出子菜单项，id 大小写不敏感去重。
    /// 返回值与窗口里 <c>BuildChannelMenuSignature</c> 的入参同形，调用方不必再转换。
    /// </summary>
    public static List<(string Id, string Name, string Tooltip)> BuildEntries(
        IEnumerable<ChannelMenuChannel> configured,
        IEnumerable<ChannelMenuSession> sessions)
    {
        var entries = new List<(string Id, string Name, string Tooltip)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in configured)
        {
            // 停用 / id 空白 / 重复 id 都不进菜单。
            if (!channel.Enabled || string.IsNullOrWhiteSpace(channel.Id) || !seen.Add(channel.Id))
            {
                continue;
            }

            entries.Add((channel.Id, channel.Name, $"{channel.Host} / {channel.User}"));
        }

        foreach (var session in sessions)
        {
            // 本地执行组不进菜单；已收尾的会话不再常驻 —— 这一条是「停用后菜单项不消失」的修复点。
            if (!session.IsRemote || session.CompletionReported)
            {
                continue;
            }

            if (!seen.Add(session.ChannelId))
            {
                continue;
            }

            entries.Add((
                session.ChannelId,
                session.DisplayName,
                $"{session.TargetHost} / {session.TargetUser} · {session.StatusText}"));
        }

        return entries;
    }
}
