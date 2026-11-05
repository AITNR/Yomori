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
/// 适配器注册表 —— 白名单式识别。
///
/// <para>没登记过的脚本一律返回 null，即使内容探针"看起来像"，也不启用精简：
/// 误判的代价是用户看不懂自己的日志，比不精简严重得多。</para>
/// </summary>
public static class ExternalLogAdapterRegistry
{
    /// <summary>March7thAssistant / March7th Launcher 的 id。</summary>
    public const string March7thId = "m7a";

    /// <summary>两个入口同属一个项目，实测日志语法一致。</summary>
    private static readonly string[] March7thEntries =
    {
        "March7th Launcher.exe",
        "March7th Assistant.exe",
    };

    /// <summary>实测预设里的模式：`Presets/launch_presets.json` → `projects[id=m7a].log.pattern`。</summary>
    private static readonly Regex March7thLogPattern = new(
        @"^20\?\?-\?\?-\?\?\.log$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>按线索选适配器；都不命中返回 null（调用方走粗略兜底）。</summary>
    public static IExternalLogAdapter? Select(ExternalLogAdapterHint? hint)
    {
        if (hint is null) { return null; }

        if (LooksLikeMarch7thPreset(hint.PresetId)) { return new March7thLogAdapter(); }
        if (LooksLikeMarch7thEntry(hint.EntryName)) { return new March7thLogAdapter(); }
        if (LooksLikeMarch7thLogPath(hint.LogPath, hint.LogPattern)) { return new March7thLogAdapter(); }
        return null;
    }

    /// <summary>
    /// 内容探针（文件刚建立、显式线索缺失时的兜底）。
    ///
    /// <para>必须**同时**满足：至少一条 March7th 标准行 + 至少一条框线/框内标题/完成线，
    /// 且整段里没有别的族的标记。单看标准行会把一堆 Python 脚本误判成 March7th。</para>
    /// </summary>
    public static IExternalLogAdapter? DetectByContent(IReadOnlyList<string> lines)
    {
        if (lines is null || lines.Count == 0) { return null; }

        var hasStandard = false;
        var hasFrame = false;
        foreach (var line in lines)
        {
            if (line is null) { continue; }
            if (March7thGrammar.IsForeignFamily(line)) { return null; }

            if (!hasStandard && March7thGrammar.StandardLine.IsMatch(line)) { hasStandard = true; }
            if (!hasFrame &&
                (March7thGrammar.BoxRule.IsMatch(line) ||
                 March7thGrammar.BoxTitle.IsMatch(line) ||
                 March7thGrammar.DoneRule.IsMatch(line)))
            {
                hasFrame = true;
            }
        }

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

    /// <summary>取文件名（同时认 `\` 与 `/`，远程日志路径是 Linux 风格时也不吃亏）。</summary>
    private static string FileNameOf(string path)
    {
        if (path.Length == 0) { return string.Empty; }
        var cut = path.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 ? path[(cut + 1)..].Trim() : path;
    }
}
