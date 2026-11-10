// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Yomori.Models;

/// <summary>
/// 组装外部日志适配器时能拿到的全部线索。
///
/// <para>为什么四个字段都要：脚本可能换入口（`Launcher.exe` / `Assistant.exe`）、
/// 可能装在带版本号的目录里（`March7thAssistant_v2.7.0`）、用户也可能改了日志文件名模式。
/// 任何一条命中就足以认定，不必全中。</para>
/// </summary>
public sealed class ExternalLogAdapterHint
{
    /// <summary>启动预设的项目段，例如 `m7a:main`。</summary>
    public string PresetId { get; init; } = string.Empty;

    /// <summary>入口可执行文件名，例如 `March7th Launcher.exe`。</summary>
    public string EntryName { get; init; } = string.Empty;

    /// <summary>要跟的日志文件绝对路径。</summary>
    public string LogPath { get; init; } = string.Empty;

    /// <summary>目录模式下配置的文件名模式，例如 `20??-??-??.log`。</summary>
    public string LogPattern { get; init; } = string.Empty;
}

/// <summary>
/// 单个脚本的日志适配器。
///
/// <para>三层职责（见《外部脚本实时日志深度适配计划书》§4.1）：注册表负责"是谁"，
/// 适配器负责"这一行在这个脚本里是什么意思"，采集端负责把结果落成 <see cref="ExternalLogLine"/>。
/// 适配器一律不碰文件 IO、不碰界面。</para>
///
/// <para>状态必须按实例持有：远程通道可能有多个 tail 同时跑同一脚本，串台就会互相吞行。</para>
/// </summary>
public interface IExternalLogAdapter
{
    /// <summary>注册表里的 id（例如 `m7a`）。</summary>
    string Id { get; }

    /// <summary>
    /// 吃一行原始日志，吐一条投影结果。
    ///
    /// <para>可以返回 <see cref="ExternalLogProjection.Drop"/>（这一行不上屏），
    /// 也可以返回 <see cref="ExternalLogProjection.Pass"/>（不认识，fail-open 交给粗略链路）。
    /// 被缓存的行走 <see cref="Tick"/> 或下一次 <see cref="Project"/> 收尾，绝不静默丢行。</para>
    /// </summary>
    ExternalLogProjection Project(string rawLine, long nowMs);

    /// <summary>采集节拍上的收尾：超时未追加的合并组、到期的节流窗口。没有可出的返回 null。</summary>
    ExternalLogProjection? Tick(long nowMs);

    /// <summary>清空全部会话状态（换文件、重连、用户清空日志时调用）。</summary>
    void Reset();
}

/// <summary>
/// 可选能力：适配器需要项目资源里的中文任务名 / 节点名时实现它。
///
/// <para><see cref="LogLabelMap"/> 由 <c>LogLabelStore</c> 异步装载，而适配器在 tail 构造时
/// 就已绑定。因此由 <c>ExternalLogTail.BeginLoadLabels</c> 装载完成后主动注入，
/// 而不是把标签塞进 <see cref="IExternalLogAdapter"/> 的构造签名 —— 那样每加一个族
/// 都要改动另外两个适配器。目前只有 MaaEnd（MaaFW 内核日志）需要它。</para>
/// </summary>
public interface IExternalLogLabelAware
{
    /// <summary>注入（或整体替换）项目资源映射；实现方必须容忍空表与多次调用。</summary>
    void SetLabels(LogLabelMap labels);
}

/// <summary>
/// 适配器注册表 —— 白名单式识别。
///
/// <para>没登记过的脚本一律返回 null，即使内容探针"看起来像"，也不启用精简：
/// 误判的代价是用户看不懂自己的日志，比不精简严重得多。</para>
/// </summary>
public static class ExternalLogAdapterRegistry
{
    /// <summary>March7thAssistant / March7th Launcher 的 id。</summary>
    public const string March7thId = "m7a";

    /// <summary>BetterGI（更好的原神）的 id，与 `launch_presets.json` 的 `projects[id=bgi]` 对齐。</summary>
    public const string BetterGIId = "bgi";

    /// <summary>MaaEnd 的 id，与 `launch_presets.json` 的 `projects[id=maaend]` 对齐。</summary>
    public const string MaaEndId = "maaend";

    /// <summary>两个入口同属一个项目，实测日志语法一致。</summary>
    private static readonly string[] March7thEntries =
    {
        "March7th Launcher.exe",
        "March7th Assistant.exe",
    };

    /// <summary>实测预设里的模式：`Presets/launch_presets.json` → `projects[id=m7a].log.pattern`。</summary>
    private static readonly Regex March7thLogPattern = new(
        @"^20\?\?-\?\?-\?\?\.log$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>BetterGI 只有一个入口（`projects[id=bgi].entries`）。</summary>
    private static readonly string[] BetterGIEntries =
    {
        "BetterGI.exe",
    };

    /// <summary>MaaEnd 只有一个入口（`projects[id=maaend].entries`）。</summary>
    private static readonly string[] MaaEndEntries =
    {
        "MaaEnd.exe",
    };

    /// <summary>按线索选适配器；都不命中返回 null（调用方走粗略兜底）。</summary>
    public static IExternalLogAdapter? Select(ExternalLogAdapterHint? hint)
    {
        if (hint is null) { return null; }

        if (LooksLikeMarch7thPreset(hint.PresetId)) { return new March7thLogAdapter(); }
        if (LooksLikeMarch7thEntry(hint.EntryName)) { return new March7thLogAdapter(); }
        if (LooksLikeMarch7thLogPath(hint.LogPath, hint.LogPattern)) { return new March7thLogAdapter(); }

        if (LooksLikeBetterGIPreset(hint.PresetId)) { return new BetterGILogAdapter(); }
        if (LooksLikeBetterGIEntry(hint.EntryName)) { return new BetterGILogAdapter(); }
        if (LooksLikeBetterGILogPath(hint.LogPath, hint.LogPattern)) { return new BetterGILogAdapter(); }

        if (LooksLikeMaaEndPreset(hint.PresetId)) { return new MaaEndLogAdapter(); }
        if (LooksLikeMaaEndEntry(hint.EntryName)) { return new MaaEndLogAdapter(); }
        if (LooksLikeMaaEndLogPath(hint.LogPath)) { return new MaaEndLogAdapter(); }
        return null;
    }

    /// <summary>
    /// 内容探针（文件刚建立、显式线索缺失时的兜底）。
    ///
    /// <para>March7th 必须**同时**满足：至少一条标准行 + 至少一条框线/框内标题/完成线，
    /// 且整段里没有别的族的标记。单看标准行会把一堆 Python 脚本误判成 March7th。</para>
    ///
    /// <para>BetterGI 必须满足：至少两条带 `BetterGenshinImpact.` 模块名的头行 +
    /// 至少一条非分隔的正文行，且没有 March7th 标准行。注意
    /// `ExternalLogGrammar.ForeignLine` 与 BetterGI 头行同形，**不能**拿它当排除项。</para>
    ///
    /// <para>**MaaEnd 故意不接入内容探针**：MaaFW 内核日志跨项目同形（45+ 个项目共用一套
    /// `[msg=…]` / `[details=…]` 事件格式），没有 MaaEnd 专属标记；探针一命中就会把
    /// 其它 MaaFW 项目一起劫持到单行形态。它只靠三类显式线索（见
    /// 《外部脚本实时日志深度适配计划书-MaaEnd.md》§5.1）。探针里的 MaaFW 外族标记
    /// 已使内核日志天然不会命中前两族，这里保持白名单为加法，不新增分支。</para>
    /// </summary>
    public static IExternalLogAdapter? DetectByContent(IReadOnlyList<string> lines)
    {
        if (lines is null || lines.Count == 0) { return null; }

        var hasStandard = false;
        var hasFrame = false;
        var bgiHeaders = 0;
        var bgiBodies = 0;
        foreach (var line in lines)
        {
            if (line is null) { continue; }
            if (March7thGrammar.IsForeignFamily(line) || BetterGIGrammar.IsForeignFamily(line)) { return null; }

            if (!hasStandard && March7thGrammar.StandardLine.IsMatch(line)) { hasStandard = true; }
            if (!hasFrame &&
                (March7thGrammar.BoxRule.IsMatch(line) ||
                 March7thGrammar.BoxTitle.IsMatch(line) ||
                 March7thGrammar.DoneRule.IsMatch(line)))
            {
                hasFrame = true;
            }

            var bgiHeader = BetterGIGrammar.HeaderLine.Match(line);
            if (bgiHeader.Success &&
                bgiHeader.Groups["mod"].Value.IndexOf(BetterGIGrammar.ModulePrefix,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bgiHeaders++;
            }
            else if (line.Trim().Length > 0 && !BetterGIGrammar.IsSeparator(line))
            {
                bgiBodies++;
            }
        }

        if (bgiHeaders >= 2 && bgiBodies >= 1 && !hasStandard) { return new BetterGILogAdapter(); }
        return hasStandard && hasFrame ? new March7thLogAdapter() : null;
    }

    /// <summary>`m7a:main` → 前段是 `m7a`。</summary>
    private static bool LooksLikeMarch7thPreset(string? presetId)
    {
        var text = (presetId ?? string.Empty).Trim();
        if (text.Length == 0) { return false; }
        var head = text.Split(':')[0].Trim();
        return string.Equals(head, March7thId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`D:\…\March7th Launcher.exe` → 只看文件名。</summary>
    private static bool LooksLikeMarch7thEntry(string? entryName)
    {
        var name = FileNameOf((entryName ?? string.Empty).Trim());
        if (name.Length == 0) { return false; }
        foreach (var candidate in March7thEntries)
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)) { return true; }
        }

        return false;
    }

    /// <summary>路径里带项目名（允许 `March7thAssistant_v2.7.0` 这类版本目录），且模式为空或就是实测的那个。</summary>
    private static bool LooksLikeMarch7thLogPath(string? logPath, string? logPattern)
    {
        var path = (logPath ?? string.Empty).Trim();
        if (path.Length == 0) { return false; }
        if (path.IndexOf("March7thAssistant", StringComparison.OrdinalIgnoreCase) < 0) { return false; }

        var pattern = (logPattern ?? string.Empty).Trim();
        return pattern.Length == 0 || March7thLogPattern.IsMatch(pattern);
    }

    /// <summary>`bgi:oneDragon` / `bgi:start` / `bgi:groups` → 前段是 `bgi`。</summary>
    private static bool LooksLikeBetterGIPreset(string? presetId)
    {
        var text = (presetId ?? string.Empty).Trim();
        if (text.Length == 0) { return false; }
        var head = text.Split(':')[0].Trim();
        return string.Equals(head, BetterGIId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`C:\Program Files\BetterGI\BetterGI.exe` → 只看文件名。</summary>
    private static bool LooksLikeBetterGIEntry(string? entryName)
    {
        var name = FileNameOf((entryName ?? string.Empty).Trim());
        if (name.Length == 0) { return false; }
        foreach (var candidate in BetterGIEntries)
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)) { return true; }
        }

        return false;
    }

    /// <summary>
    /// 日志路径带 `BetterGI` / `better-genshin-impact`，或文件模式就是
    /// `better-genshin-impact*.log`（实测 `projects[id=bgi].log.pattern`）。
    /// 任一命中即认定，不必全中 —— 用户可能把日志目录改到别处。
    /// </summary>
    private static bool LooksLikeBetterGILogPath(string? logPath, string? logPattern)
    {
        var path = (logPath ?? string.Empty).Trim();
        var pattern = (logPattern ?? string.Empty).Trim();
        return path.IndexOf("BetterGI", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("better-genshin-impact", StringComparison.OrdinalIgnoreCase) >= 0 ||
               pattern.IndexOf("better-genshin-impact", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>`maaend:auto-on-launch` / `maaend:autostart` → 前段是 `maaend`。</summary>
    private static bool LooksLikeMaaEndPreset(string? presetId)
    {
        var text = (presetId ?? string.Empty).Trim();
        if (text.Length == 0) { return false; }
        var head = text.Split(':')[0].Trim();
        return string.Equals(head, MaaEndId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`D:\Program Files\MaaEnd\MaaEnd.exe` → 只看文件名。</summary>
    private static bool LooksLikeMaaEndEntry(string? entryName)
    {
        var name = FileNameOf((entryName ?? string.Empty).Trim());
        if (name.Length == 0) { return false; }
        foreach (var candidate in MaaEndEntries)
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)) { return true; }
        }

        return false;
    }

    /// <summary>
    /// 日志路径里带 `MaaEnd`（实测 `D:\Program Files\MaaEnd\debug`）。
    ///
    /// <para>**不把 `maafw.log` 当独立信号**：它是 MaaFramework 通用文件名，
    /// 单独命中会把同一套内核日志的其它项目一起判成 MaaEnd。只有路径已含项目名时才认定。</para>
    /// </summary>
    private static bool LooksLikeMaaEndLogPath(string? logPath)
    {
        var path = (logPath ?? string.Empty).Trim();
        return path.Length > 0 && path.IndexOf("MaaEnd", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>取文件名（同时认 `\` 与 `/`，远程日志路径是 Linux 风格时也不吃亏）。</summary>
    private static string FileNameOf(string path)
    {
        if (path.Length == 0) { return string.Empty; }
        var cut = path.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 ? path[(cut + 1)..].Trim() : path;
    }
}
