// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Yomori.Models;

/// <summary>
/// 一条日志行的语义类型 —— 界面按它上色、归档按它取舍。
///
/// 为什么不用字符串：这些值会跨进程（代理 → 主控）与跨视图（面板 / 节点 / 原文）使用，
/// 一旦写错一个中文字符就会静默变成"普通行"，没有任何编译期保护。
/// </summary>
public enum ExternalLogKind
{
    /// <summary>未识别的普通行。</summary>
    Plain,

    /// <summary>任务开始（`▶`）。</summary>
    TaskStart,

    /// <summary>任务完成（`✔`）。</summary>
    TaskDone,

    /// <summary>任务失败（`✘`）。</summary>
    TaskFail,

    /// <summary>节点进出（`▸`）。</summary>
    Node,

    /// <summary>项目作者写的人话提示（`details.focus`）。</summary>
    Notice,

    /// <summary>节点失败（`✗`）。</summary>
    NodeFail,

    /// <summary>告警（`⚠`）。</summary>
    Warn,

    /// <summary>心跳（`⋯`）。</summary>
    Heartbeat,

    /// <summary>日志空档（`⋯ 日志中断`）。</summary>
    Gap,
}

/// <summary>外部日志视图取值（面板 / 节点 / 原文）。</summary>
public static class ExternalLogViews
{
    /// <summary>默认：任务边界 + 人话提示 + 心跳。</summary>
    public const string Panel = "panel";

    /// <summary>节点进出、识别失败折叠、告警 —— 排障用。</summary>
    public const string Node = "node";

    /// <summary>未投影的原始行。</summary>
    public const string Raw = "raw";

    /// <summary>未知值回落面板 —— 面板最不容易让人误以为"脚本没输出"。</summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            Node => Node,
            Raw => Raw,
            _ => Panel,
        };
    }
}

/// <summary>
/// MaaFramework 日志的文法常量。
///
/// <para>集中放在这里的原因：这些都是"格式事实"（4.x 与 5.x 都成立，但 MFW / BetterGI / 三月七
/// 都不成立），散落到投影器里就没法单独断言，也没法一眼看出哪些是实测得来的。</para>
///
/// <para>每一条都来自 `bh3-2026-10-29.log`（5.x）与 `zdh-2026-10-29.log`（4.x）的真实行，
/// 出处见《外部脚本实时日志降噪计划书》§3.7。</para>
/// </summary>
public static class ExternalLogGrammar
{
    /// <summary>
    /// 归档行自带的前缀：`[13:03:56] [警告] [2026-10-29 …`。
    ///
    /// <para>`[警告]` / `[错误]` 是 Yomori 自己加的级别提示（`ExternalLogText`），
    /// 不是脚本写的。不先剥掉，整条原始行会被当成"消息"再输出一遍。</para>
    /// </summary>
    public static readonly Regex ArchivePrefix = new(
        @"^\[(?<hms>\d{2}:\d{2}:\d{2})\] (?:\[(?<tag>警告|错误|信息)\])? ?(?<rest>.*)$",
        RegexOptions.Compiled);

    /// <summary>事件名：`[msg=Tasker.Task.Starting]`。</summary>
    public static readonly Regex EventName = new(@"\[msg=([^\]]+)\]", RegexOptions.Compiled);

    /// <summary>payload 起点：`[details={…}`（可能被 300 字符截断）。</summary>
    public static readonly Regex DetailsStart = new(@"\[details=(.*)$", RegexOptions.Compiled);

    /// <summary>内核行：`[时间][INF][Px…][Tx…][文件][Lnn][函数] 正文`。</summary>
    public static readonly Regex KernelLine = new(
        @"^\[(?<ts>[^\]]+)\]\[(?<lvl>TRC|DBG|INF|WRN|ERR)\]\[\w+\]\[\w+\]\[(?<file>[^\]]+)\]\[L\d+\]\[[^\]]*\](?<body>.*)$",
        RegexOptions.Compiled);

    /// <summary>外壳行（MFW 的 python 侧）：`[时间][INFO][文件][Lnn][函数] | 消息`。</summary>
    public static readonly Regex ShellLine = new(
        @"^\[(?<ts>[^\]]+)\]\[(?<lvl>INFO|WARNING|ERROR|DEBUG|TRACE)\]\[(?<mod>[^\]]+)\]\[L\d+\]\[(?<fn>[^\]]*)\] \| ?(?<msg>.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// 别的进程混进同一份归档的行：`[13:12:47.123] [INF] [Primary:S3:P75124:T…] 类名`。
    ///
    /// <para>实测 `zdh` 里有 4,227 行 BetterGI 的这类行：只有类名、没有消息体，
    /// 而且**不能**拿它当"脚本还活着"的证据。</para>
    /// </summary>
    public static readonly Regex ForeignLine = new(
        @"^\[[\d:.]+\] \[[A-Z]{3,5}\] \[[A-Za-z]+:[^\]]*\] [\w.]+$",
        RegexOptions.Compiled);

    /// <summary>等待类节点：一次任务里会重复几百遍，面板上留着只会淹掉真进度。</summary>
    public static readonly Regex WaitLike = new(
        "(Wait|Loading|Sleep|Delay|Retry|Loop|等待|检测|轮询)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 启动期固定噪声：GPU 适配器探测、窗口移动、截图尺寸、OCR 缓存空。
    ///
    /// <para>实测 `zdh` 一天 1,218 条 GPU 适配器 + 1,000+ 条 `cache is empty`。
    /// 这类行**不进折叠计数** —— 否则面板会被「同类第 900 次」占满。</para>
    /// </summary>
    public static readonly Regex Noise = new(
        "virtual adapters|adapter has less than|driver date is too old|software adapter|" +
        "Moving/resizing window|prefetch_batch_ocr|Set draw quality|" +
        "cache is empty|roi is out of range|custom action target rect is empty|" +
        "Wrong ocr_result size|entry is not \\*\\.json|Window no longer visible",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>GPU / 窗口 / 截图类文件：整类行连告警都不必看。</summary>
    public static readonly HashSet<string> NoiseFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "GpuOptionWin32.cpp", "HwndUtils.hpp", "PseudoMinimizeHelper.cpp", "SeizeInput.cpp",
    };

    /// <summary>
    /// 中间层告警：`invalid node id` / `Task timeout` / `node not completed` 这类。
    ///
    /// <para>单独看对人没有意义，但拼在"任务失败"后面正好回答「为什么失败」。所以只记原因，不出行。</para>
    /// </summary>
    public static readonly HashSet<string> InternalFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "PipelineTask.cpp", "TaskBase.cpp", "Context.cpp", "PipelineBase.cpp",
        "MaaTasker.cpp", "Tasker.cpp",
        // 识别/动作层的自检：`failed to get target rect for wait_freezes` 之类。
        // 它意味着"这一次没认出来"，而项目本身会继续重试 —— 上屏只会把中文提示挤走。
        "ActionHelper.cpp", "ControllerAgent.cpp", "Recognizer.cpp",
    };

    /// <summary>
    /// 按文本里的 ASCII 标记判断是不是 MaaFramework 内核日志。
    /// <para>调用方按 **Latin1** 解码即可（标记全是单字节，不受源文件编码影响）。
    /// 返回 <c>null</c> 表示"没有内容可判断"。</para>
    /// </summary>
    public static bool? DetectMaafw(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return null; }
        if (text.Contains("!!!OnEventNotify!!!", StringComparison.Ordinal) ||
            text.Contains("[msg=Tasker.", StringComparison.Ordinal) ||
            text.Contains("MaaNS::", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 这一行看起来已经是**投影后的输出**（`▶ 任务开始：…` / `  ▸ 节点` / `⋯ 进行中`）。
    ///
    /// <para>用途只有一个：保证"再投影一次还是它自己"。这样主控端可以对老代理发来的原始行
    /// 再投影一次，而新代理已经投影过的行不会被二次加工；用户把日志目录指到 Yomori 自己的
    /// 归档目录时也不会把面板文本再洗一遍。</para>
    /// </summary>
    public static bool LooksProjected(string line)
    {
        if (line.Length == 0) { return false; }
        var text = line.TrimStart();
        return text.StartsWith("▶", StringComparison.Ordinal) ||
               text.StartsWith("✔", StringComparison.Ordinal) ||
               text.StartsWith("✘", StringComparison.Ordinal) ||
               text.StartsWith("▸", StringComparison.Ordinal) ||
               text.StartsWith("✓", StringComparison.Ordinal) ||
               text.StartsWith("✗", StringComparison.Ordinal) ||
               text.StartsWith("⚠", StringComparison.Ordinal) ||
               text.StartsWith("⋯", StringComparison.Ordinal);
    }

    /// <summary>是不是一份 MaaFramework 内核日志（含事件行的那一类）。</summary>
    public static bool LooksLikeMaafw(IReadOnlyList<string> firstLines)
    {
        foreach (var line in firstLines)
        {
            if (line is null) { continue; }
            if (line.Contains("!!!OnEventNotify!!!", StringComparison.Ordinal) ||
                line.Contains("[msg=Tasker.", StringComparison.Ordinal) ||
                line.Contains("MaaNS::", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
