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
///   <item><b>列表项</b>（<c>- / * / + / 1.</c>）与**它的续行**当正文（markdown 允许一条跨行写，
///     不合并就会切在半句）；</item>
///   <item><b>代码块里的行</b>（测试版「本次提交」就在代码块里）；</item>
///   <item><b><c>###</c> 小节标题 + 其下第一句</b>，合成「标题：首句」一条。
///     正式版的手写说明主干全在散文里（RDP 解锁、UU 修复、授权统一都是这种），
///     只认列表项会让最重要的几条整段消失，只剩边角料；</item>
///   <item>表格、引用、HTML、分隔线、<c>#</c> / <c>##</c> 大标题、流程性小节标题
///     （「怎么用」「本次提交」）与 <c>Full Changelog</c> 一律丢掉；</item>
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

    /// <summary>全部要点的字符预算（380px 宽的通知里大约 10 行）。</summary>
    public const int DefaultMaxChars = 320;

    /// <summary>
    /// 一条要点至少要有这么多字符才值得出现。
    ///
    /// <para>预算见底时宁可少列一条，也不要留「授权规则的统一：本机被控账…」这种残句 ——
    /// 残句既不说明问题，又把下一条挤掉。</para>
    /// </summary>
    private const int MinLineChars = 24;

    /// <summary>单条要点最长字符数。一条 commit 标题再长也不该独占六行。</summary>
    private const int MaxLineChars = 72;

    /// <summary>GitHub 自动生成的那行收尾链接，对用户没有信息量。</summary>
    private const string FullChangelogMarker = "Full Changelog";

    /// <summary>句末标点：一条要点只取第一句，截断也优先断在这里。</summary>
    private static readonly char[] SentenceMarks = { '。', '！', '？', '；' };

    /// <summary>句中的停顿标点（断在这里要补省略号，表示话没说完）。</summary>
    private static readonly char[] PauseMarks = { '，', '、', ',', ';' };

    /// <summary>
    /// 流程性小节标题（装、下、提交了什么 …）。判据是「关键词命中」，宁可漏判也不要误判：
    /// 漏判顶多多一条「怎么用：两种下载方式」，误判会把真正的小节（如「修复」）吞掉。
    /// </summary>
    private static readonly string[] FlowHeadingWords =
    {
        "怎么用", "如何使用", "用法", "安装说明", "下载方式", "下载地址", "使用说明",
        "本次提交", "更新日志", "变更记录", "changelog", "usage", "install", "how to",
        "what's changed", "whats changed",
    };

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

        // 已读到 `###` 小节标题、正等它下面第一段散文（headingConsumed = 已经用掉）
        string? heading = null;
        var headingConsumed = false;

        // 先把跨行的段落合并成逻辑行：markdown 里一句可以用好几行写，
        // 按物理行判断会把句子切在半句（"…减少远控软件"就是这么来的）。
        foreach (var line in MergeContinuationLines(markdown))
        {
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
                heading = null;
                continue;
            }

            if (line.Length == 0)
            {
                // 空行 = 段落结束：标题不再等它下面那段
                continue;
            }

            if (inFence)
            {
                // 代码块里的行就是测试版真正的「本次提交」
                AppendUnique(bullets, CleanCommitLine(line));
                heading = null;
                continue;
            }

            // 表格 / 引用 / HTML / 分隔线 / 收尾链接：都不是要点
            if (line[0] is '|' or '>' or '<' or '=' ||
                IsHorizontalRule(line) ||
                line.Contains(FullChangelogMarker, StringComparison.OrdinalIgnoreCase))
            {
                heading = null;
                continue;
            }

            if (line[0] == '#')
            {
                // `#` / `##` 是整份说明的大标题（通常就是版本号本身），丢掉；
                // `###` 以下才是「这一版改了什么」的分节，记下来等它的第一段散文。
                heading = ReadSectionHeading(line);
                headingConsumed = false;
                continue;
            }

            var item = StripListMarker(line);
            if (item is not null)
            {
                // 列表项自己就是要点，前面那个标题不再重复采用
                heading = null;
                AppendUnique(bullets, CleanInline(item));
                continue;
            }

            var body = CleanInline(line);
            if (body.Length == 0)
            {
                continue;
            }

            if (heading is { Length: > 0 } section && !headingConsumed)
            {
                // 「### 小节标题 + 首句」合成一条要点。
                // 正式版手写说明的主干全在散文里（RDP 解锁、UU 修复、授权统一都是这种），
                // 只认列表项会让最重要的几条整段消失，只剩边角料。
                if (AppendUnique(bullets, section + "：" + FirstSentence(body)))
                {
                    headingConsumed = true;
                }

                continue;
            }

            heading = null;
            AppendUnique(prose, body);
        }

        // 长度上限放到最后统一处理：合并完整段才有上下文，
        // 也才有机会在标点处断开而不是硬切。
        for (var i = 0; i < bullets.Count; i++)
        {
            bullets[i] = TrimTo(bullets[i], MaxLineChars, preferBreak: true);
        }

        return Fit(bullets.Count > 0 ? bullets : prose, maxLines, maxChars);
    }

    /// <summary>
    /// 把 markdown 的软换行合并成逻辑行。
    ///
    /// <para>markdown 里「一句话」可以跨任意多行写，只有空行才分段。按物理行处理会有两个后果：
    /// 句子被切在半句，以及「标题 + 其下第一句」只吃到第一行（v2.4.0 的说明就是这样，
    /// 「…根因是本机 wtsapi32.dll」后面那半句直接被丢）。</para>
    ///
    /// <para>代码块内的行**不合并**（每行是独立的一条提交）；标题、表格、引用、
    /// HTML、分隔线这类结构性行既不与上一行合并，也不让下一行并进来。</para>
    /// </summary>
    private static List<string> MergeContinuationLines(string markdown)
    {
        var output = new List<string>();
        var inFence = false;

        // 代码块整体只留第一个非空行 —— 见下面 inFence 分支的说明
        var fenceHasLine = false;

        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("```", StringComparison.Ordinal) ||
                line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                fenceHasLine = false;
                output.Add(line);
                continue;
            }

            if (inFence)
            {
                // CI 的「本次提交」代码块放的是**整条提交信息**（标题 + 空行 + 正文段落）。
                // 逐行当成独立要点，通知里会出现六条半句碎片，全是同一条提交的正文；
                // 只取标题那一行才是「这一版改了什么」。
                if (line.Length > 0 && !fenceHasLine)
                {
                    output.Add(line);
                    fenceHasLine = true;
                }

                continue;
            }

            if (line.Length == 0)
            {
                output.Add(string.Empty);
                continue;
            }

            var indented = raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t');

            // 标题 / 表格 / 引用 / HTML / 分隔线，以及**顶格写的列表项**：都自成一块。
            // 列表项必须在这里拦下：`* fix: a` 下一行是 `* feat: b`，不拦会合并成一条。
            if (IsStructuralLine(line) || (!indented && StripListMarker(line) is not null))
            {
                output.Add(line);
                continue;
            }

            // 缩进的续行（markdown 里列表项的第二行必须缩进）与普通散文行接在上一块后面
            if (output.Count > 0 && output[^1].Length > 0 && !IsStructuralLine(output[^1]))
            {
                output[^1] = Join(output[^1], line);
                continue;
            }

            output.Add(line);
        }

        return output;
    }

    /// <summary>标题 / 表格 / 引用 / HTML / 分隔线：这些行自成一块，不与相邻行合并。</summary>
    private static bool IsStructuralLine(string line) =>
        line.Length > 0
        && (line[0] is '#' or '|' or '>' or '<' or '=' || IsHorizontalRule(line));

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

    /// <summary>
    /// 读出 `###` 及更深的小节标题；`#` / `##` 与流程性标题（「怎么用」「本次提交」）返回 null。
    /// </summary>
    private static string? ReadSectionHeading(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level <= 2)
        {
            return null;
        }

        var text = CleanInline(line[level..].Trim());
        if (text.Length == 0 || IsFlowHeading(text))
        {
            return null;
        }

        return text;
    }

    /// <summary>
    /// 流程性小节标题：它们是「怎么装、怎么下、本次提交了什么」这类说明，
    /// 不是「这一版改了什么」。当作要点会让通知里出现「怎么用：两种下载方式，任选其一：」这种废话。
    /// </summary>
    private static bool IsFlowHeading(string text)
    {
        foreach (var word in FlowHeadingWords)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>取一句话（到第一个句末标点）。整段塞进一条要点会立刻超长。</summary>
    private static string FirstSentence(string text)
    {
        var end = -1;
        foreach (var mark in SentenceMarks)
        {
            var index = text.IndexOf(mark);
            if (index >= 0 && (end < 0 || index < end))
            {
                end = index;
            }
        }

        return end >= 0 ? text[..(end + 1)] : text;
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

        // 这里**不截断**：列表项跨行写的时候，续行要接到完整句子上才截，
        // 提前切会让「合并续行」拿到的已经是半句（v2.4.0 说明就栽在这）。
        return text;
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

    /// <summary>
    /// 接续一行文字。中文之间**不加空格** —— markdown 的软换行是排版，不是词间隔；
    /// 补一个空格会变成「减少远控软件 进到其他账户桌面」这种夹生句。
    /// </summary>
    private static string Join(string head, string tail)
    {
        if (head.Length == 0)
        {
            return tail;
        }

        if (tail.Length == 0)
        {
            return head;
        }

        return IsWideChar(head[^1]) || IsWideChar(tail[0])
            ? head + tail
            : head + " " + tail;
    }

    /// <summary>CJK 汉字、中文标点、全角字符（这些字符之间按中文习惯不空格）。</summary>
    private static bool IsWideChar(char c) =>
        c is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3000' and <= '\u303F'
            or >= '\uFF00' and <= '\uFFEF';

    /// <summary>
    /// 去重追加，返回是否真的加进去了。
    /// 同一句重复出现（有人把同一条列两遍）只留一次。
    /// </summary>
    private static bool AppendUnique(List<string> list, string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var existing in list)
        {
            if (string.Equals(existing, text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        list.Add(text);
        return true;
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

            // 预算见底时宁可少一条，也不要留半截残句（残句既不说明问题，还占掉一条的位置）
            if (result.Count > 0 && budget < MinLineChars)
            {
                break;
            }

            // 还剩预算就按剩余长度截断，别整条丢掉 —— 半句也比一片空白有用
            var text = line.Length <= budget ? line : TrimTo(line, budget, preferBreak: true);
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

    /// <summary>
    /// 截到 <paramref name="max"/> 字符以内。
    /// </summary>
    /// <param name="preferBreak">
    /// 是否尽量断在标点上。中文句子硬切会切在词中间（「…减少远控软件」）；
    /// 断在逗号后要补省略号（话没说完），断在句号后不补（本来就是完整一句）。
    /// </param>
    private static string TrimTo(string text, int max, bool preferBreak = false)
    {
        if (max <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        var cut = max - 1;

        if (preferBreak)
        {
            // 断得太早（不到三分之一处就找到标点）比硬切更难看，宁可继续往下找
            var floor = max / 3;

            // ① 句末标点：本身就是一句话的终点，不必再补省略号
            var end = text.LastIndexOfAny(SentenceMarks, cut);
            if (end >= floor)
            {
                return text[..(end + 1)];
            }

            // ② 句中停顿：断在这里补省略号，读者才知道后面还有
            var pause = text.LastIndexOfAny(PauseMarks, cut);
            if (pause >= floor)
            {
                return text[..(pause + 1)] + "…";
            }
        }

        return text[..cut] + "…";
    }
}
