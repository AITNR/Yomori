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

/// <summary>
/// March7thAssistant（三月七小助手 / March7th Launcher）日志的文法常量。
///
/// <para>和 <see cref="ExternalLogGrammar"/> 分开放的原因：那一套是 MaaFramework 内核日志的格式事实，
/// 这一套全是三月七自己 print 出来的中文行 + ASCII 边框，两者的判据没有一条能复用。
/// 混在一起会让"到底是谁的格式"变得看不出来。</para>
///
/// <para>每条正则都注明了出处行（`2026-11-05.log` / `2026-10-29.log`，
/// 位于 `C:\Program Files\March7thAssistant_v2.7.0\logs\`），
/// 统计口径见《外部脚本实时日志深度适配计划书》§1.4。</para>
/// </summary>
public static class March7thGrammar
{
    /// <summary>
    /// 标准行：`2026-11-05 21:32:05,502 | INFO | 游戏启动：…`。
    ///
    /// <para>全天 389/497 行都是它（78.3%）。级别字段实测只出现 `INFO / WARNING / ERROR` 三种，
    /// 这里仍用 `[A-Z]+` 兜住将来新增的级别 —— 认不出来只会掉进 INFO 分支，不会漏行。</para>
    /// </summary>
    public static readonly Regex StandardLine = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}) (?<hms>\d{2}:\d{2}:\d{2}),(?<ms>\d{3}) \| (?<lvl>[A-Z]+) \| (?<body>.*)$",
        RegexOptions.Compiled);

    /// <summary>完整框线：`+-------------------------------------------------------------------+`（42 行，8.5%）。</summary>
    public static readonly Regex BoxRule = new(@"^\+-+\+$", RegexOptions.Compiled);

    /// <summary>
    /// 窄栏折行后的左半截框线：`+-----------------------------`。
    ///
    /// <para>不是脚本写出来的，是宽控制台的行在 360px 栏宽下被截断后的形态；
    /// 归档里不会有，但实时读取时如果用户把 Yomori 自己的归档当源，就会遇到。</para>
    /// </summary>
    public static readonly Regex BoxRuleLeft = new(@"^\+-*$", RegexOptions.Compiled);

    /// <summary>窄栏折行后的右半截框线：`-----------------------------+`。</summary>
    public static readonly Regex BoxRuleRight = new(@"^-*\+$", RegexOptions.Compiled);

    /// <summary>框内标题：`|                            开始运行                                |`（21 行）。</summary>
    public static readonly Regex BoxTitle = new(@"^\|\s*(?<title>.+?)\s*\|$", RegexOptions.Compiled);

    /// <summary>完成线：`----------------------- 完成 -----------------------`（19 行，全部无时间戳）。</summary>
    public static readonly Regex DoneRule = new(@"^-+\s*完成\s*-+$", RegexOptions.Compiled);

    /// <summary>等号小标题：`============= 开始查询日常任务完成情况 =============`（6 行）。</summary>
    public static readonly Regex EqualsTitle = new(@"^=+\s*(?<title>.*?)\s*=+$", RegexOptions.Compiled);

    /// <summary>
    /// 横线子步骤：`------------ 准备分解四星及以下遗器 ------------`（19 行）。
    ///
    /// <para>纯分隔符也会命中（标题为空），调用方按"标题为空即丢弃"处理。</para>
    /// </summary>
    public static readonly Regex DashTitle = new(@"^-+\s*(?<title>.*?)\s*-+$", RegexOptions.Compiled);

    /// <summary>只由分隔符组成的行（纯 `---` / `===` / `+++`）：绝不能上屏。</summary>
    public static readonly Regex SeparatorOnly = new(@"^[+\-=_*~\s]+$", RegexOptions.Compiled);

    /// <summary>
    /// 批量刷的横线标题：`开始刷饰品提取 - 浴火钢心，总计1轮，每轮包含6次`
    /// （`2026-11-05.log:108`）。压成 `刷 饰品提取 - 浴火钢心（1轮×6次）`。
    /// </summary>
    public static readonly Regex BatchStart = new(
        @"^开始刷\s*(?<what>.+?)\s*[，,]\s*总计\s*(?<rounds>\d+)\s*轮[，,]\s*每轮包含\s*(?<per>\d+)\s*次$",
        RegexOptions.Compiled);

    /// <summary>游戏启动：`游戏启动：D:\mhy\Star Rail Game\StarRail.exe`（`2026-11-05.log:13`）。</summary>
    public static readonly Regex GameLaunch = new(@"^游戏启动\s*[:：]\s*(?<path>.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Windows 绝对路径（允许路径段里有空格 —— 实测 `D:\mhy\Star Rail Game\` 就带空格）。
    /// 其它正文里的路径压成文件名，避免窄栏折成 2～3 行。
    /// </summary>
    public static readonly Regex WinPath = new(
        @"[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]*\\)*(?<path>[^\\/:*?""<>|\r\n]*)",
        RegexOptions.Compiled);

    /// <summary>新版本提示：`GitHub 发现新版本: v2026.10.31`（`2026-11-05.log:1`）。</summary>
    public static readonly Regex NewVersion = new(
        @"^GitHub\s*发现新版本\s*[:：]\s*(?<ver>v?[0-9][\w.\-]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 界面状态：`切换到：手机菜单` / `当前界面：月卡`（全天 100+ 行，占四分之一）。
    /// 两者同类节流 —— 对用户来说都是"现在在哪个界面"。
    /// </summary>
    public static readonly Regex UiSwitch = new(
        @"^(?:切换到|当前界面)\s*[:：]\s*(?<name>.+?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// 识别失败告警：`未识别出任何界面，请确保游戏画面干净，按ESC后重试`（13 行）。
    /// 实测是 WARNING 级，但少数版本会打成 INFO，两条路径都要认。
    /// </summary>
    public static readonly Regex UnrecognizedUi = new(@"^未识别出任何界面", RegexOptions.Compiled);

    /// <summary>
    /// 资源状态：`开拓力: 300/300` / `沉浸器: 0/12`（`2026-11-05.log:61,100`）。
    ///
    /// <para>只认 `N/N` 这种纯数值形态 —— `开拓力: 300 + 沉浸器: 0 = 7 次挑战` 是算式，
    /// 每次内容不同，压成状态去重反而会吞掉有用信息。</para>
    /// </summary>
    public static readonly Regex ResourceStatus = new(
        @"^(?<key>开拓力|沉浸器|体力|星琼|信用点)\s*[:：]\s*(?<value>\d+\s*/\s*\d+)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// 列表型续行：`侵蚀隧洞 - 密伶之径, 霜风之径`（`2026-11-05.log:33`）。
    ///
    /// <para>实测这几行**带**时间戳（旧版本里不带），所以续行不能只按"没有时间戳"判断；
    /// 这里用"短前缀 + 空格连字符 + 空格"的列表形状补一条判据。</para>
    /// </summary>
    public static readonly Regex ContinuationItem = new(@"^\S[^:：]{0,24}\s-\s\S", RegexOptions.Compiled);

    /// <summary>
    /// 内部机制噪声：`准备发送 winotify 通知（…）`（22 行）、`winotify 通知发送完成`、
    /// `[config-watch] …` / `[config-save] …`（10 行）。用户视角零信息量。
    /// </summary>
    public static readonly string[] NoisePrefixes =
    {
        "准备发送 winotify",
        "winotify 通知发送完成",
        "[config-watch]",
        "[config-save]",
    };

    /// <summary>别的族混进同一份日志时的标记 —— 命中任意一条就不许判定为 March7th。</summary>
    private static readonly string[] ForeignMarkers =
    {
        "!!!OnEventNotify!!!",
        "[msg=",
        "MaaNS::",
        "][INF][Px",
    };

    /// <summary>是不是内部机制噪声（按前缀判定，前缀之后可能带参数）。</summary>
    public static bool IsNoiseBody(string body)
    {
        if (body.Length == 0) { return false; }
        foreach (var prefix in NoisePrefixes)
        {
            if (body.StartsWith(prefix, StringComparison.Ordinal)) { return true; }
        }

        return false;
    }

    /// <summary>这一行是不是别的脚本族（MaaFramework）的标记行。</summary>
    public static bool IsForeignFamily(string line)
    {
        foreach (var marker in ForeignMarkers)
        {
            if (line.Contains(marker, StringComparison.Ordinal)) { return true; }
        }

        return false;
    }

    /// <summary>
    /// 这一行看起来已经是**精简形态**（`▶ …` / `  正文` / `⚠ …`）。
    ///
    /// <para>和 <see cref="ExternalLogGrammar.LooksProjected"/> 是同一份纪律：投影必须幂等，
    /// 否则主控端对老代理发来的行再投一次就会二次加工。区别是这里还要认 `■`（停止运行）
    /// 与两空格缩进（March7th 的普通正文前缀）。</para>
    /// </summary>
    public static bool LooksCompact(string line)
    {
        if (line.Length == 0) { return false; }
        if (StandardLine.IsMatch(line) ||
            BoxRule.IsMatch(line) ||
            BoxRuleLeft.IsMatch(line) ||
            BoxRuleRight.IsMatch(line) ||
            BoxTitle.IsMatch(line) ||
            DoneRule.IsMatch(line) ||
            EqualsTitle.IsMatch(line) ||
            DashTitle.IsMatch(line))
        {
            return false;
        }

        if (line.StartsWith("  ", StringComparison.Ordinal)) { return true; }

        var text = line.TrimStart();
        if (text.Length == 0 || text.Length != line.Length) { return false; }
        return text.StartsWith("▶", StringComparison.Ordinal) ||
               text.StartsWith("✔", StringComparison.Ordinal) ||
               text.StartsWith("✘", StringComparison.Ordinal) ||
               text.StartsWith("■", StringComparison.Ordinal) ||
               text.StartsWith("▸", StringComparison.Ordinal) ||
               text.StartsWith("⚠", StringComparison.Ordinal) ||
               text.StartsWith("✗", StringComparison.Ordinal) ||
               text.StartsWith("⋯", StringComparison.Ordinal) ||
               text.StartsWith("✓", StringComparison.Ordinal);
    }
}
