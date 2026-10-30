// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Yomori.Models;

/// <summary>
/// 任务类型（写进 config.json 的 <c>task_kind</c>，见《任务类型与收尾计划书》§3 D2）。
///
/// <para>三种类型共用同一张 <see cref="TaskConfig"/> 表，由这个字段决定"看哪些列、跑什么行为"：
/// <c>script</c> 起一个外部程序并等它结束；<c>wait</c> 只是等一段时间；
/// <c>finish</c> 对通道做处置（注销 / 断开 / 无操作）。</para>
///
/// <para>⚠️ <b>两套表不许混用</b>：界面绑 <see cref="Selectable"/>（只有脚本与等待），
/// <see cref="Items"/>（含收尾）只用于展示与校验。收尾任务由系统固定维护 ——
/// 每通道恰好一条、恒在末位、不可添加/删除/改类型，所以它不该出现在任何"可选"列表里。</para>
///
/// <para>纯逻辑，不引任何界面类型（<c>_smoke</c> 直接编译本文件）。</para>
/// </summary>
public static class TaskKinds
{
    public const string Script = "script";
    public const string Wait = "wait";
    public const string Finish = "finish";

    /// <summary>完整表（三项）。给"这条配置是什么类型"的展示与校验用。</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items =
        new List<KeyValuePair<string, string>>
        {
            new(Script, "脚本"),
            new(Wait, "等待时间"),
            new(Finish, "收尾"),
        };

    /// <summary>界面可选项（两项）：类型单选只绑这一份，收尾不出现。</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Selectable =
        new List<KeyValuePair<string, string>>
        {
            new(Script, "脚本"),
            new(Wait, "等待时间"),
        };

    /// <summary>
    /// 归一成合法值，未知 / 空一律回落 <see cref="Script"/>。
    ///
    /// <para>回落脚本而不是别的：老配置没有这个键，读出来必须是"现在的项目脚本任务"，
    /// 否则升级一次所有任务都变味。</para>
    /// </summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            Wait => Wait,
            Finish => Finish,
            _ => Script,
        };
    }

    public static string Label(string? value) => Normalize(value) switch
    {
        Wait => "等待时间",
        Finish => "收尾",
        _ => "脚本",
    };

    public static bool IsFinish(string? value) =>
        string.Equals(Normalize(value), Finish, StringComparison.Ordinal);

    public static bool IsWait(string? value) =>
        string.Equals(Normalize(value), Wait, StringComparison.Ordinal);

    public static bool IsScript(string? value) =>
        string.Equals(Normalize(value), Script, StringComparison.Ordinal);
}

/// <summary>
/// 收尾动作（写进 <c>post_task_action</c>）。
///
/// <para>原先挂在通道上（<c>session_finish</c>），现在归任务级的收尾任务所有 ——
/// 值名保持不变，老配置的键仍能读进来做迁移；只把标签改成任务语义：
/// 原来叫"保持连接"，现在叫"无操作"（在任务语境里它的含义就是"不处置"）。</para>
/// </summary>
public static class SessionFinishModes
{
    public const string Keep = "keep";
    public const string Disconnect = "disconnect";
    public const string Logoff = "logoff";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items =
        new List<KeyValuePair<string, string>>
        {
            new(Keep, "无操作（保持现状）"),
            new(Disconnect, "断开通道（会话保留，进程继续运行）"),
            new(Logoff, "注销通道（结束会话，释放资源）"),
        };

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            Disconnect => Disconnect,
            Logoff => Logoff,
            _ => Keep,
        };
    }

    public static string Label(string? value) => Normalize(value) switch
    {
        Disconnect => "断开通道",
        Logoff => "注销通道",
        _ => "无操作",
    };

    /// <summary>给日志用的一句话（比 <see cref="Label"/> 多一句后果说明）。</summary>
    public static string Describe(string? value) => Normalize(value) switch
    {
        Disconnect => "断开会话（进程继续在后台跑）",
        Logoff => "注销会话（会话内进程一并结束）",
        _ => "不做处置",
    };
}

/// <summary>等待任务的执行时机（写进 <c>wait_timing</c>）。</summary>
public static class WaitTimings
{
    /// <summary>在建立通道连接之前等 —— 只有本通道队列里的第一条任务能选。</summary>
    public const string BeforeConnect = "before_connect";

    /// <summary>在队列执行到这一项时等（默认）。</summary>
    public const string AfterConnect = "after_connect";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items =
        new List<KeyValuePair<string, string>>
        {
            new(AfterConnect, "通道连接后（队列执行到这里时等待）"),
            new(BeforeConnect, "通道连接前（建立连接之前先等）"),
        };

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text == BeforeConnect ? BeforeConnect : AfterConnect;
    }

    public static string Label(string? value) => Normalize(value) == BeforeConnect ? "连接前" : "连接后";

    public static bool IsBeforeConnect(string? value) =>
        Normalize(value) == BeforeConnect;
}

/// <summary>
/// 任务类型的**全部判定**都收在这里：执行侧（主控端 / 代理）、界面、冒烟共用一套纯函数。
///
/// <para>分散写会立刻分叉：主控端算一次"这条是不是收尾"、代理再算一次，两边口径一漂，
/// 日志与行为就对不上（本项目在"会话处理方式"上已经踩过这种分裂）。</para>
/// </summary>
public static class TaskTypeResolver
{
    public static bool IsFinish(TaskConfig? task) => task is not null && TaskKinds.IsFinish(task.TaskKind);

    public static bool IsWait(TaskConfig? task) => task is not null && TaskKinds.IsWait(task.TaskKind);

    public static bool IsScript(TaskConfig? task) => task is null || TaskKinds.IsScript(task.TaskKind);

    /// <summary>
    /// 本通道有没有"能真跑起来"的脚本任务 —— 替换原先 <c>HasUsableTaskConfig</c> 的内核。
    ///
    /// <para>为什么不能沿用"任意一条路径非空"：等待/收尾任务没有脚本路径，
    /// 继续用旧判据会把"只有等待和收尾"的通道判成"没有可用配置"，直接不让开始执行。</para>
    /// </summary>
    public static bool HasRunnableScript(IEnumerable<TaskConfig>? tasks)
    {
        foreach (var task in tasks ?? Enumerable.Empty<TaskConfig>())
        {
            if (task is not null && task.Enabled && IsScript(task) && !string.IsNullOrWhiteSpace(task.ScriptPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 本通道队列里排在第一位的启用任务是不是"连接前等待"。
    ///
    /// <para>一条通道只连一次，"连接前"只有首条任务兑现得了。判断必须与实际执行顺序同源：
    /// 传进来的 <paramref name="scopeTasks"/> 必须已经按 <see cref="TaskScopePlanner.TasksForScope"/>
    /// 排好（收尾在末位）。</para>
    /// </summary>
    public static bool ShouldWaitBeforeConnect(IReadOnlyList<TaskConfig>? scopeTasks)
    {
        var first = (scopeTasks ?? Array.Empty<TaskConfig>())
            .FirstOrDefault(t => t is not null && t.Enabled);

        if (first is null || !IsWait(first) || !WaitTimings.IsBeforeConnect(first.WaitTiming))
        {
            return false;
        }

        return first.WaitSeconds > 0;
    }

    /// <summary>
    /// 本通道跑完之后该对会话做什么 —— 取**最后一条收尾任务**的动作，没有收尾任务就是"无操作"。
    ///
    /// <para>配置正常时收尾只有一条；导入的脏配置里可能出现两条，此时按 order 取最后一条
    /// （与执行顺序一致：后执行的那条说了算）。</para>
    /// </summary>
    public static string ResolveSessionFinish(IEnumerable<TaskConfig>? scopeTasks)
    {
        TaskConfig? last = null;
        foreach (var task in scopeTasks ?? Enumerable.Empty<TaskConfig>())
        {
            if (IsFinish(task) && (last is null || task!.Order >= last.Order))
            {
                last = task;
            }
        }

        return SessionFinishModes.Normalize(last?.PostTaskAction);
    }

    /// <summary>列表行第二行要显示的文本（按类型换内容）。</summary>
    public static string Describe(TaskConfig? task)
    {
        if (task is null)
        {
            return string.Empty;
        }

        if (IsFinish(task))
        {
            return SessionFinishModes.Label(task.PostTaskAction);
        }

        if (IsWait(task))
        {
            var seconds = Math.Max(0, task.WaitSeconds);
            var timing = WaitTimings.IsBeforeConnect(task.WaitTiming) ? "连接前" : "队列内";
            return seconds > 0 ? $"等待 {seconds} 秒 · {timing}" : "未设置时长";
        }

        return task.ScriptPath;
    }

    /// <summary>列表行左侧的类型标签：脚本不显示（它是默认），等待/收尾各一个词。</summary>
    public static string TagOf(TaskConfig? task) => task is null ? string.Empty : TaskKinds.Normalize(task.TaskKind) switch
    {
        TaskKinds.Wait => "等待",
        TaskKinds.Finish => "收尾",
        _ => string.Empty,
    };
}
