// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Yomori.Models;

/// <summary>
/// 原生内嵌会话上限，必须与 <c>Yomori.RdpNative/ycn_rdp.c</c> 的
/// <c>YCN_MAX_SESSIONS</c> 保持一致（那份是数组长度，超了会直接连不上）。
/// 改一处就要改两处。
/// </summary>
public static class RdpNativeLimits
{
    public const int MaxSessions = 8;
}

/// <summary>一个通道本轮要跑的任务（通道内部按 order 串行，与现状 ScriptRunner 一致）。</summary>
public sealed record ChannelPlan(RdpChannel Channel, IReadOnlyList<TaskConfig> Tasks);

/// <summary>本轮没有启动的任务及其原因（通道停用 / 没配账户 / 超过并发上限 / 引用了不存在的通道）。</summary>
public sealed record SkippedChannelPlan(RdpChannel? Channel, string Reason, IReadOnlyList<TaskConfig> Tasks)
{
    /// <summary>给运行日志用的一行说明。</summary>
    public string Describe()
    {
        var owner = Channel is not null ? $"通道「{Channel.DisplayName}」" : "未归属任何通道";
        var names = string.Join("、", Tasks.Take(3).Select(t => t.DisplayName));
        if (Tasks.Count > 3)
        {
            names += $" 等 {Tasks.Count} 个";
        }

        return $"{owner}的 {Tasks.Count} 个任务未启动（{Reason}）：{names}";
    }
}

/// <summary>本轮执行规划的结果。</summary>
public sealed record ExecutionPlan(
    IReadOnlyList<TaskConfig> LocalTasks,
    IReadOnlyList<ChannelPlan> ToLaunch,
    IReadOnlyList<SkippedChannelPlan> Skipped)
{
    /// <summary>本轮启用的任务总数（含本地与未启动的）。</summary>
    public int EnabledTaskCount =>
        LocalTasks.Count + ToLaunch.Sum(p => p.Tasks.Count) + Skipped.Sum(s => s.Tasks.Count);

    public bool IsEmpty => EnabledTaskCount == 0;
}

/// <summary>
/// 执行规划：把启用的任务按「执行通道」分组，并决定本轮真正启动哪些通道。
/// 全部是纯函数 —— 不碰文件、不碰界面，方便脱离 WinUI 单测（见 _smoke/RdpChannelCheck.cs）。
///
/// 分组规则（与计划书 §3.2 / D6 一致）：
///   · channel_id 为空            → 本地执行（跑在主控端当前会话里）；
///   · channel_id 指向启用通道    → 归该通道；
///   · 通道被停用 / 没配目标账户   → 不启动，给出原因（不静默改跑本地）；
///   · channel_id 指向不存在的通道 → 不启动，给出原因（宁可不跑，也不猜着跑到别处）；
///   · 需要启动的通道数超过 maxParallel → 按通道配置顺序取前 N 个，其余给出原因。
/// </summary>
public static class RdpChannelPlanner
{
    public static ExecutionPlan Plan(
        IEnumerable<TaskConfig>? tasks,
        IReadOnlyList<RdpChannel>? channels,
        bool rdpEnabled,
        int maxParallel = RdpNativeLimits.MaxSessions,
        bool localEnabled = true)
    {
        var enabledTasks = (tasks ?? Enumerable.Empty<TaskConfig>())
            .Where(t => t is not null && t.Enabled)
            .OrderBy(t => t.Order)
            .ToList();

        // RDP 总开关关掉：所有任务都在本地跑，行为与改造前一致
        // （例外：本地执行组自己也被关掉 → 本轮什么都不跑，按"跳过"如实说清）
        if (!rdpEnabled)
        {
            if (!localEnabled && enabledTasks.Count > 0)
            {
                return new ExecutionPlan(
                    Array.Empty<TaskConfig>(),
                    Array.Empty<ChannelPlan>(),
                    new[] { new SkippedChannelPlan(null, "本地执行已停用", enabledTasks) });
            }

            return new ExecutionPlan(
                enabledTasks,
                Array.Empty<ChannelPlan>(),
                Array.Empty<SkippedChannelPlan>());
        }

        // 通道按 id 建索引，同时记住它们在配置里的顺序（决定谁先占用并发名额）
        var byId = new Dictionary<string, RdpChannel>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<RdpChannel>();
        foreach (var channel in channels ?? Array.Empty<RdpChannel>())
        {
            if (channel is null || string.IsNullOrWhiteSpace(channel.Id) || byId.ContainsKey(channel.Id))
            {
                continue;
            }

            byId[channel.Id] = channel;
            ordered.Add(channel);
        }

        var local = new List<TaskConfig>();
        var buckets = new Dictionary<string, List<TaskConfig>>(StringComparer.OrdinalIgnoreCase);
        var unknown = new Dictionary<string, List<TaskConfig>>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in enabledTasks)
        {
            var id = (task.ChannelId ?? string.Empty).Trim();

            if (id.Length == 0)
            {
                local.Add(task);
                continue;
            }

            if (byId.TryGetValue(id, out var known))
            {
                if (!buckets.TryGetValue(known.Id, out var list))
                {
                    list = new List<TaskConfig>();
                    buckets[known.Id] = list;
                }

                list.Add(task);
                continue;
            }

            if (!unknown.TryGetValue(id, out var orphans))
            {
                orphans = new List<TaskConfig>();
                unknown[id] = orphans;
            }

            orphans.Add(task);
        }

        var skipped = new List<SkippedChannelPlan>();

        // 本地执行组被关掉：本机那批任务本轮不下发，但要如实说清"它们去哪了"。
        // 不能静默清空 —— 用户看到进度总数变小会以为是任务丢了。
        if (!localEnabled && local.Count > 0)
        {
            skipped.Add(new SkippedChannelPlan(null, "本地执行已停用", local));
            local.Clear();
        }

        foreach (var (id, orphans) in unknown)
        {
            skipped.Add(new SkippedChannelPlan(null, $"找不到通道「{id}」", orphans));
        }

        var candidates = new List<ChannelPlan>();
        foreach (var channel in ordered)
        {
            if (!buckets.TryGetValue(channel.Id, out var channelTasks) || channelTasks.Count == 0)
            {
                continue;
            }

            if (!channel.Enabled)
            {
                skipped.Add(new SkippedChannelPlan(channel, "通道已停用", channelTasks));
                continue;
            }

            if (!channel.IsConfigured)
            {
                skipped.Add(new SkippedChannelPlan(channel, "通道未配置目标账户", channelTasks));
                continue;
            }

            candidates.Add(new ChannelPlan(channel, channelTasks));
        }

        var limit = Math.Max(1, maxParallel);
        var launch = new List<ChannelPlan>();
        foreach (var candidate in candidates)
        {
            if (launch.Count >= limit)
            {
                skipped.Add(new SkippedChannelPlan(
                    candidate.Channel,
                    $"超过并发上限 {limit}",
                    candidate.Tasks));
                continue;
            }

            launch.Add(candidate);
        }

        return new ExecutionPlan(local, launch, skipped);
    }
}
