// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Yomori.Services;

/// <summary>
/// 把 GitHub 发布说明（Markdown）压成通知里能读的几行「更新内容」。
///
/// <para>为什么要单独做一层：两条通道的 <c>body</c> 都是给人看发布页写的，不是给通知写的。</para>
/// <list type="bullet">
///   <item>正式版走 <c>--generate-notes</c>：<c>## What's Changed</c> +
///     <c>* fix(x): … by @user in https://…</c> 一长串 + <c>**Full Changelog**</c> 链接；</item>
///   <item>测试版是 CI 手写的模板：元信息表格、<c>### 本次提交</c> 下的代码块、
///     <c>### 怎么用</c> 下的整段下载说明和两行引用。</item>
/// </list>
///
/// <para>直接把这些塞进通知，占满一屏的还是「怎么用」「Full Changelog」，
/// 真正想知道的那几条变更反而被挤到看不见。所以这里只留**要点**：</para>
/// <list type="bullet">
///   <item>列表项（<c>- / * / + / 1.</c>）与代码块里的行（测试版就是提交信息）当正文；</item>
///   <item>表格、引用、HTML、分隔线、结构性标题（<c>##</c>）一律丢掉；</item>
///   <item>顺手剥掉 <c>**加粗**</c>、反引号、<c>[文字](链接)</c>、<c>by @user in 链接</c>、
///     行首短哈希 —— 那些是 GitHub 网页上的排版残留，在通知里只会占宽度。</item>
/// </list>
///
/// <para>一条要点都没有时（有人把说明写成纯散文）退化成取头两句，
/// 总比「更新内容：」下面空着好；仍然为空时调用方就不画那一段。</para>
///
/// <para>⚠️ 纯函数、不碰任何 XAML / 网络：冒烟工程直接把本文件编进纯 .NET 目标验行为。</para>
/// </summary>
public static class UpdateNotes
{
    /// <summary>通知里最多列几条。再多就没人读了，铃铛也给不了那么高。</summary>
    public const int DefaultMaxLines = 6;

    /// <summary>全部要点的字符预算（380px 宽的通知里大约 8 行）。</summary>
    public const int DefaultMaxChars = 240;

    /// <summary>单条要点最长字符数。一条 commit 标题再长也不该独占六行。</summary>
    private const int MaxLineChars = 72;

    /// <summary>GitHub 自动生成的那行收尾链接，对用户没有信息量。</summary>
    private const string FullChangelogMarker = "Full Changelog";

    /// <summary><c>* fix: … by @AITNR in https://…</c> 的尾巴。</summary>
    private static readonly Regex ByAuthorTail = new(
        @"\s+by\s+@[A-Za-z0-9_.\-]+\s+in\s+\S+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>行首的提交短哈希（测试版的提交块是 <c>c5776bd 标题</c>）。</summary>
    private static readonly Regex LeadingSha = new(
        @"^[0-9a-f]{7,40}\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary><c>[文字](链接)</c> → 文字。</summary>
    private static readonly Regex MarkdownLink = new(
        @"\[([^\]]*)\]\([^)]*\)",
        RegexOptions.CultureInvariant);

    /// <summary>连续空白压成一个空格。</summary>
    private static readonly Regex Whitespace = new(
        @"\s+",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 取要点。返回值不带项目符号，调用方自己决定怎么排（通知里是「· 」开头）。
    /// 永不抛异常，永不返回 null。
    /// </summary>
    /// <param name="markdown">发布说明原文。</param>
    /// <param name="maxLines">最多几条。</param>
    /// <param name="maxChars">全部要点共同的字符预算。</param>
    public static IReadOnlyList<string> Summarize(
        string? markdown,
        int maxLines = DefaultMaxLines,
        int maxChars = DefaultMaxChars)
    {
        var bullets = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown) || maxLines <= 0 || maxChars <= 0)
        {
            return bullets;
        }

        // 纯散文的兜底候选（只在一条要点都没有时才用）
        var prose = new List<string>();
        var inFence = false;
        var inHtmlComment = false;

        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();

            if (inHtmlComment)
            {
                if (line.Contains("-->", StringComparison.Ordinal))
                {
                    inHtmlComment = false;
                }

                continue;
            }

            if (line.StartsWith("<!--", StringComparison.Ordinal))
            {
                inHtmlComment = !line.Contains("-->", StringComparison.Ordinal);
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal) ||
                line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (inFence)
            {
                // 代码块里的行就是测试版真正的「本次提交」
                Append(bullets, CleanCommitLine(line));
                continue;
            }

            // 表格 / 引用 / HTML / 分隔线 / 结构性标题 / 收尾链接：都不是要点
            if (line[0] is '|' or '>' or '<' or '#' or '=' ||
                IsHorizontalRule(line) ||
                line.Contains(FullChangelogMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var item = StripListMarker(line);
            if (item is not null)
            {
                Append(bullets, CleanInline(item));
                continue;
            }

            Append(prose, CleanInline(line));
        }

        var result = bullets.Count > 0 ? bullets : prose;
        return Fit(result, maxLines, maxChars);
    }

    /// <summary>
    /// 造一条「发现新版本」通知的正文：版本对比 + 更新内容 + 下一步该做什么。
    ///
    /// <para>三段都要有：只有版本号用户不知道改了什么，只有更新内容用户不知道相对哪一版；
    /// 没有第三段，用户会以为点开只是去网页看看。</para>
    /// </summary>
    /// <param name="fromVersion">当前版本。</param>
    /// <param name="toVersion">远端版本。</param>
    /// <param name="channelLabel">通道名（正式版 / 测试版）。</param>
    /// <param name="publishedAt">发布日期；未知时传 null。</param>
    /// <param name="releaseNotes">发布说明原文。</param>
    /// <param name="canDownload">这份发布有没有可下载的安装包（决定第三段怎么说）。</param>
    public static string BuildBody(
        string fromVersion,
        string toVersion,
        string channelLabel,
        DateTimeOffset? publishedAt,
        string? releaseNotes,
        bool canDownload,
        int maxLines = DefaultMaxLines,
        int maxChars = DefaultMaxChars)
    {
        var text = new StringBuilder();

        text.Append("当前 ").Append(fromVersion).Append(" → ").Append(toVersion)
            .Append('（').Append(channelLabel).Append('）');

        if (publishedAt is { } published)
        {
            text.Append(" · 发布于 ")
                .Append(published.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        text.Append('\n');

        var notes = Summarize(releaseNotes, maxLines, maxChars);
        if (notes.Count > 0)
        {
            text.Append("更新内容：\n");
            foreach (var note in notes)
            {
                text.Append("· ").Append(note).Append('\n');
            }
        }

        text.Append(canDownload
            ? "点这条直接下载安装包，下完会再问你要不要装。"
            : "这份发布里没有可下载的安装包，点这条去发布页看看。");

        return text.ToString();
    }

    // ---------------- 内部 ----------------

    /// <summary>代码块里的行：去掉行首短哈希与尾巴，别把 commit 的元数据当正文。</summary>
    private static string CleanCommitLine(string line)
    {
        var sha = LeadingSha.Match(line);
        if (sha.Success)
        {
            line = line[sha.Length..];
        }

        return CleanInline(line);
    }

    /// <summary>剥掉 markdown 的内联排版残留。</summary>
    private static string CleanInline(string line)
    {
        var text = line.Trim();

        var tail = ByAuthorTail.Match(text);
        if (tail.Success)
        {
            text = text[..tail.Index];
        }

        text = MarkdownLink.Replace(text, "$1");
        text = text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);

        // 链接的尖括号形式 <https://…> 只剩 URL 时没有价值
        if (text.StartsWith('<') && text.EndsWith('>') && text.Contains("://", StringComparison.Ordinal))
        {
            text = string.Empty;
        }

        text = Whitespace.Replace(text, " ").Trim();
        return TrimTo(text, MaxLineChars);
    }

    /// <summary>是不是列表项；是则返回去掉标记后的内容。</summary>
    private static string? StripListMarker(string line)
    {
        if (line.Length >= 2 && line[0] is '-' or '*' or '+' or '•' && char.IsWhiteSpace(line[1]))
        {
            return line[2..];
        }

        // 1. / 12) 这类有序列表
        var digits = 0;
        while (digits < line.Length && char.IsDigit(line[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < line.Length && line[digits] is '.' or ')' && char.IsWhiteSpace(line[digits + 1]))
        {
            return line[(digits + 2)..];
        }

        return null;
    }

    private static bool IsHorizontalRule(string line)
    {
        if (line.Length < 3)
        {
            return false;
        }

        var symbol = line[0];
        if (symbol is not ('-' or '*' or '_'))
        {
            return false;
        }

        foreach (var ch in line)
        {
            if (ch != symbol && !char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static void Append(List<string> list, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        // 同一句重复出现（有人把同一条列两遍）只留一次
        foreach (var existing in list)
        {
            if (string.Equals(existing, text, StringComparison.Ordinal))
            {
                return;
            }
        }

        list.Add(text);
    }

    /// <summary>按条数上限与总字符预算收敛。</summary>
    private static IReadOnlyList<string> Fit(List<string> lines, int maxLines, int maxChars)
    {
        var result = new List<string>();
        var budget = maxChars;

        foreach (var line in lines)
        {
            if (result.Count >= maxLines)
            {
                break;
            }

            // 还剩预算就按剩余长度截断，别整条丢掉 —— 半句也比一片空白有用
            var text = line.Length <= budget ? line : TrimTo(line, budget);
            if (text.Length == 0)
            {
                break;
            }

            result.Add(text);
            budget -= text.Length;

            if (budget <= 0)
            {
                break;
            }
        }

        return result;
    }

    private static string TrimTo(string text, int max)
    {
        if (max <= 0)
        {
            return string.Empty;
        }

        return text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
    }
}
