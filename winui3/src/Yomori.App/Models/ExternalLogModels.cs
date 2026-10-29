// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>
/// 外部脚本实时日志的默认值与常量。
///
/// 为什么要有它：BetterGI / March7thAssistant / MaaEnd 的日志位置与格式各不相同，
/// 而且同一软件跨版本还会变（MaaEnd 一个 debug 目录里就有两种行格式）。
/// 所以这里不做逐家格式解析，只提供通用默认值 + 用户可配的关键字。
/// </summary>
public static class ExternalLogDefaults
{
    /// <summary>目录模式下默认只看 .log。</summary>
    public const string Pattern = "*.log";

    /// <summary>默认从文件末尾开始跟（只关心本次运行产生的行）。</summary>
    public const bool FollowTail = true;

    /// <summary>
    /// 保留多少份「每通道每天」的外部日志文件。
    /// 与"事件条数"无关 —— 日志一律全部保留，这里只管落盘文件的份数。
    /// </summary>
    public const int KeepFiles = 30;

    /// <summary>单行最大长度（超出截断），限制界面单行文本长度。</summary>
    public const int MaxLineLength = 300;

    /// <summary>跟踪循环的默认节拍（毫秒）。</summary>
    public const int PollIntervalMs = 250;
    /// <summary>通道事件列表保留条数的**默认值**。</summary>
    public const int EventLineLimit = 200;

    /// <summary>通道事件列表保留条数的允许范围。</summary>
    public const int MinEventLines = 20;
    public const int MaxEventLinesLimit = 5000;

    /// <summary>把任意输入规整到合法的保留条数范围内。</summary>
    public static int NormalizeEventLineLimit(int value) =>
        value < MinEventLines ? MinEventLines : value > MaxEventLinesLimit ? MaxEventLinesLimit : value;

    /// <summary>模式为空/空白时回落到默认值。</summary>
    public static string NormalizePattern(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? Pattern : text;
    }
}

/// <summary>外部日志文件的编码取值。</summary>
public static class ExternalLogEncodings
{
    /// <summary>BOM 优先；无 BOM 时按完整行严格校验 UTF-8，失败后回退 GBK。</summary>
    public const string Auto = "auto";

    public const string Utf8 = "utf8";

    /// <summary>GBK / GB2312 一族，中文软件日志常见。</summary>
    public const string Gbk = "gbk";

    /// <summary>未知值回落 Auto —— 猜错编码总比直接崩好。</summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            Utf8 => Utf8,
            Gbk => Gbk,
            _ => Auto,
        };
    }
}

/// <summary>从外部日志文件里读出来的一行。</summary>
public sealed class ExternalLogLine
{
    /// <summary>行正文（已规整：去转义、压空白、截断）。面板档/节点档按视图取用。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// 节点档文本（所有节点进出、告警、折叠）。
    ///
    /// <para>归档写它：事后排障要的是"跑到哪一步断了"，而不是面板上那几行人话。
    /// 为空时表示这一行只在面板档出现（例如 focus 提示），归档退回 <see cref="Text"/>。</para>
    /// </summary>
    public string NodeText { get; init; } = string.Empty;

    /// <summary>原始行（未投影）。只有编译期就知道要原文视图时才填，其余为空省内存。</summary>
    public string Raw { get; init; } = string.Empty;

    /// <summary>语义类型（任务 / 节点 / 人话 / 告警 / 心跳 …），界面据此上色。</summary>
    public ExternalLogKind Kind { get; init; } = ExternalLogKind.Plain;

    /// <summary>识别出的级别。</summary>
    public ExternalLogLevel Level { get; init; }

    /// <summary>是否命中了用户配置的关键字。</summary>
    public bool KeywordHit { get; init; }

    /// <summary>按视图取显示文本（原文视图没有 <see cref="Raw"/> 时退回投影文本，绝不返回空）。</summary>
    public string TextFor(string? view)
    {
        var mode = ExternalLogViews.Normalize(view);
        if (mode == ExternalLogViews.Raw)
        {
            return Raw.Length > 0 ? Raw : Text.Length > 0 ? Text : NodeText;
        }

        if (mode == ExternalLogViews.Node)
        {
            return NodeText.Length > 0 ? NodeText : Text;
        }

        return Text.Length > 0 ? Text : NodeText;
    }

    /// <summary>归档用文本：优先节点档（含全部节点事实），没有再退回面板档。</summary>
    public string ArchiveText => NodeText.Length > 0 ? NodeText : Text;
}

/// <summary>日志行级别（从粗到细）。</summary>
public enum ExternalLogLevel
{
    /// <summary>识别不出来，按普通信息对待。</summary>
    Unknown,

    Trace,
    Debug,
    Info,
    Warn,

    /// <summary>错误日志。</summary>
    Error,
}
