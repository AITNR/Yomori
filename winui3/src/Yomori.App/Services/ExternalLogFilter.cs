// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 外部日志行的解析 —— **全部是纯函数**，不碰文件、不碰 WinUI，
/// 这样整条逻辑能脱离界面直接跑断言。
///
/// 级别（<see cref="Classify"/>）有两个用途：
/// <list type="number">
///   <item>**标注** —— 界面上区分普通行 / 告警 / 错误；</item>
///   <item>**归档取舍** —— 见 <see cref="ShouldArchive"/>：TRACE/DEBUG 级不进归档文件。</item>
/// </list>
///
/// <para>为什么现在要按级别丢弃一部分：见 <see cref="ShouldArchive"/> 的说明。</para>
/// </summary>
public static class ExternalLogFilter
{
    /// <summary>
    /// 归档冗余行的逃生口：设为 <c>1</c> / <c>true</c> 时恢复「一条不丢」。
    ///
    /// 只在排障需要 TRACE/DEBUG 明细时用。改环境变量后主控端与代理都要重启才生效
    /// （主控端靠它过滤本地执行的归档，代理靠它过滤目标账户会话里的归档）。
    /// </summary>
    public const string VerboseEnvironmentVariable = "YOMORI_EXTERNAL_LOG_VERBOSE";

    private static readonly bool ArchiveVerbose = ReadVerboseFlag();

    private static bool ReadVerboseFlag()
    {
        var value = Environment.GetEnvironmentVariable(VerboseEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                          || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 这一行是否要落进归档文件 / 实时日志流。
    ///
    /// <para>TRACE 与 DEBUG 一律丢弃，其余（含识别不出级别的）全部保留。</para>
    ///
    /// <para>【为什么】实测 March7thAssistant 跑崩坏3（MaaFramework 侧）一天能吐
    /// 100 万行以上、200MB 以上：MaaFW 把每个函数的进入/离开、每个 Pipeline 节点的解析、
    /// GPU 适配器探测都以 TRC/DBG 级别打到 stdout，经 MaaEnd 的 maafw.log 被采集。
    /// 按级别统计，TRC 占 44%、DBG 占 28.5%，而两者对排障几乎没有价值
    /// （真正有用的是 INF 的识别结果与任务流转、WRN/ERR 的告警）。丢掉这两级能省 72% 体积。</para>
    ///
    /// <para>【为什么 Unknown 保留】识别不出级别的行可能是用户脚本自己 printf 的正文，
    /// 丢了就等于把最该留的东西筛掉。宁可多留，不可误杀 —— 这是 fail-open。</para>
    /// </summary>
    public static bool ShouldArchive(ExternalLogLevel level)
        => ArchiveVerbose || level is not (ExternalLogLevel.Trace or ExternalLogLevel.Debug);

    /// <summary>同 <see cref="ShouldArchive(ExternalLogLevel)"/>，直接用已解析的行（null 视为不归档）。</summary>
    public static bool ShouldArchive(ExternalLogLine? line)
        => line is not null && ShouldArchive(line.Level);
    /// <summary>ANSI 转义序列（颜色码 / 光标控制），日志里常见但显示出来是一坨乱码。</summary>
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;?]*[a-zA-Z]", RegexOptions.Compiled);

    /// <summary>连续空白（不含换行）压缩成单个空格。</summary>
    private static readonly Regex WhitespaceRun = new(@"[\t ]{2,}", RegexOptions.Compiled);

    /// <summary>控制字符（除制表符外）一律去掉。</summary>
    private static readonly Regex ControlChars = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

    private static readonly string[] ErrorTokens =
    {
        "ERROR", "ERR", "FATAL", "CRITICAL", "PANIC", "EXCEPTION", "UNHANDLED",
        "失败", "错误", "异常", "崩溃", "致命",
    };

    private static readonly string[] WarnTokens =
    {
        "WARN", "WARNING", "WRN", "警告", "重试", "超时",
    };

    private static readonly string[] InfoTokens =
    {
        "INFO", "INF", "INFORMATION", "信息", "提示",
    };

    private static readonly string[] DebugTokens =
    {
        "DEBUG", "DBG", "调试",
    };

    private static readonly string[] TraceTokens =
    {
        "TRACE", "TRC", "VERBOSE",
    };

    /// <summary>
    /// python logging 的默认格式：<c>时间 - [logger 名] - 级别 - 正文</c>。
    ///
    /// 出现在 MAA_bbb 的 <c>debug\agent.log</c>（它自己的 <c>Logger.py</c> 用
    /// <c>"%(asctime)s - [%(name)s] - %(levelname)s - %(message)s"</c> 建的 formatter）。
    /// 这条不认，整份 agent.log 就全是 Unknown —— 行会照常显示，
    /// 但 WARNING/ERROR 不被标红，排障时最该跳出来的那几行反而最不显眼。
    ///
    /// 锚死在行首的时间戳与两个连字符上：正文里出现的 <c>- ERROR -</c> 不算级别。
    /// </summary>
    private static readonly Regex PythonLoggingLevel = new(
        @"^\s*\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,6})?\s*-\s*" +
        @"(?:\[[^\]]*\]\s*-\s*)?(?<v>[A-Za-z]{3,11})\s*-",
        RegexOptions.Compiled);

    /// <summary>
    /// 把原始行规整成能安全落盘 / 展示的形态。
    ///
    /// 顺序不能换：先去转义 -> 再去控制字符 -> 再压空白 -> 最后截断。
    /// 若先压空白，夹在中间的控制字符会挡住压缩，留下大段空白。
    /// </summary>
    public static string NormalizeLine(string? raw)
    {
        var text = (raw ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        text = AnsiEscape.Replace(text, string.Empty);
        text = ControlChars.Replace(text, string.Empty);
        text = WhitespaceRun.Replace(text, " ");
        text = text.Trim();

        return text.Length <= ExternalLogDefaults.MaxLineLength
            ? text
            : text.Substring(0, ExternalLogDefaults.MaxLineLength) + "\u2026";
    }

    /// <summary>
    /// 识别行级别，仅用于界面标注。
    ///
    /// 只在**级别字段**里比对，正文里的 "error" / "超时" 不会被误判。
    ///
    /// <para>四种写法（顺序即优先级）：</para>
    /// <list type="number">
    ///   <item><c>[INF]</c> / <c>[ERROR]</c> —— BetterGI、MaaEnd、MaaFramework（maafw.log）</item>
    ///   <item><c>| INFO |</c> —— March7thAssistant</item>
    ///   <item>时间戳后紧跟大写级别 —— MaaEnd 的 <c>2026-10-16 12:26:08 INFO</c></item>
    ///   <item><c>- INFO -</c> —— python logging 默认格式（MAA_bbb 的 agent.log）</item>
    /// </list>
    /// </summary>
    public static ExternalLogLevel Classify(string? raw)
    {
        var text = raw ?? string.Empty;
        if (text.Length == 0)
        {
            return ExternalLogLevel.Unknown;
        }

        // 三种格式的时间戳 + 级别都落在前 60 字符内，再往后是正文
        var head = text.Length > 60 ? text.Substring(0, 60) : text;

        // ① [BRACKET] 写法（BetterGI / MaaEnd）
        var bracket = Regex.Match(head, @"\[(?<v>[A-Z]{3,11})\]");
        if (bracket.Success)
        {
            return MatchLevel(bracket.Groups["v"].Value);
        }

        // ② | PIPE | 写法（March7thAssistant）
        var pipe = Regex.Match(head, @"\|\s*(?<v>[A-Za-z]{3,11})\s*\|");
        if (pipe.Success)
        {
            return MatchLevel(pipe.Groups["v"].Value);
        }

        // ③ 时间戳后紧跟独立大写词（MaaEnd 的另一种写法）
        var bare = Regex.Match(
            head,
            @"^\s*\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}\s+(?<v>[A-Z]{3,11})\b");
        if (bare.Success)
        {
            return MatchLevel(bare.Groups["v"].Value);
        }

        // ④ python logging 默认格式（MAA_bbb 的 agent.log）。
        //    组件名可能很长（"custom.往世乐土周常流程"），60 字符的 head 会截掉级别，
        //    所以这一条单独取更宽的前缀，且用带时间戳锚定的正则。
        var wide = text.Length > 200 ? text.Substring(0, 200) : text;
        var python = PythonLoggingLevel.Match(wide);
        if (python.Success)
        {
            return MatchLevel(python.Groups["v"].Value);
        }

        return ExternalLogLevel.Unknown;
    }

    /// <summary>是否命中用户配置的关键字（全行不区分大小写匹配）。</summary>
    public static bool MatchesKeyword(string? raw, IReadOnlyList<string>? keywords)
    {
        var text = raw ?? string.Empty;
        if (text.Length == 0 || keywords is null || keywords.Count == 0)
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            var needle = (keyword ?? string.Empty).Trim();
            if (needle.Length == 0)
        {
            continue;
        }

            if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把原始行解析成结构化的一行（规整 + 级别 + 关键字）。</summary>
    public static ExternalLogLine Parse(string? raw, IReadOnlyList<string>? keywords)
        => Parse(raw, keywords, wasTruncated: false);

    /// <summary>
    /// 把已在原始字节层被截断的行标出，保留行首以维持级别识别。
    /// </summary>
    public static ExternalLogLine Parse(string? raw, IReadOnlyList<string>? keywords, bool wasTruncated)
    {
        var text = NormalizeLine(raw);
        if (wasTruncated && text.Length > 0)
        {
            const string suffix = " [日志行原始字节超限，已截断]";
            var keep = Math.Max(0, ExternalLogDefaults.MaxLineLength - suffix.Length);
            text = text.Length <= keep ? text + suffix : text.Substring(0, keep) + suffix;
        }

        return new ExternalLogLine
        {
            Text = text,
            Level = Classify(text),
            KeywordHit = MatchesKeyword(text, keywords),
        };
    }

    private static ExternalLogLevel MatchLevel(string field)
    {
        if (ContainsAny(field, ErrorTokens))
        {
            return ExternalLogLevel.Error;
        }

        if (ContainsAny(field, WarnTokens))
        {
            return ExternalLogLevel.Warn;
        }

        if (ContainsAny(field, InfoTokens))
        {
            return ExternalLogLevel.Info;
        }

        if (ContainsAny(field, DebugTokens))
        {
            return ExternalLogLevel.Debug;
        }

        if (ContainsAny(field, TraceTokens))
        {
            return ExternalLogLevel.Trace;
        }

        return ExternalLogLevel.Unknown;
    }

    private static bool ContainsAny(string text, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
