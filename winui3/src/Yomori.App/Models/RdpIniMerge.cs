// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Yomori.Models;

/// <summary>用户提交的 INI 配置文本的校验结果。</summary>
/// <param name="Ok">文本能不能用。</param>
/// <param name="Message">不能用时的原因（能用时为空串）。</param>
/// <param name="SectionNames">提交文本里的段名，按出现顺序。</param>
/// <param name="VersionMatches">段名里有没有本机 termsrv.dll 的构建号 —— 决定要不要重启服务。</param>
public readonly record struct RdpIniSubmission(
    bool Ok,
    string Message,
    IReadOnlyList<string> SectionNames,
    bool VersionMatches)
{
    public static RdpIniSubmission Reject(string message) =>
        new(false, message, Array.Empty<string>(), false);
}

/// <summary>把提交文本合并进现有 INI 的结果。</summary>
public readonly record struct RdpIniMergeOutcome(
    bool Ok,
    string Message,
    string MergedText,
    int ReplacedSections,
    int AddedSections);

/// <summary>
/// rdpwrap.ini 的「按段合并」纯逻辑。界面提交的是社区针对某个系统版本贴出的补丁段
/// （例如 <c>[10.0.26100.9818]</c> 与 <c>[10.0.26100.9818-SLInit]</c>），不是完整 INI。
/// </summary>
/// <remarks>
/// <para>为什么不整份替换：完整的 rdpwrap.ini 还包含 <c>[Main]</c> / <c>Updated=</c> /
/// <c>[SLInit]</c> / <c>[PatchCodes]</c> 与上千个历史版本段。把两三段补丁当成整份文件写回去，
/// 会直接让 TermService 加载不出配置。所以只按段名 upsert：同名段整段替换、新段追加到末尾，
/// 其余行（含注释与空行）逐行原样保留。</para>
/// <para>本类刻意放在 Models 层、只依赖 BCL：冒烟工程直接编译 Models/*.cs，
/// 出现 <c>Microsoft.UI.Xaml</c> 类型就编译不过。</para>
/// </remarks>
public static class RdpIniMerge
{
    private sealed class Section
    {
        public string Name = "";
        public int HeaderIndex;
        public List<string> Lines = new();
    }

    /// <summary>
    /// 校验提交文本。<b>版本不匹配时直接判不可用</b>：段名就是 termsrv.dll 构建号，
    /// 贴错版本写进去也不会生效，还会在「是否正确版本号才重启」这道门槛上骗过自己。
    /// </summary>
    public static RdpIniSubmission ValidateSubmitted(string text, string expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return RdpIniSubmission.Reject("配置文本是空的。");
        }

        if (LooksLikeWebPage(text))
        {
            return RdpIniSubmission.Reject("这看起来是网页内容，不是 INI 配置；请只复制 [版本] 段及其下面的键值行。");
        }

        var sections = LocateSections(SplitLines(text));
        if (sections.Count == 0)
        {
            return RdpIniSubmission.Reject("没找到 [段名]；请粘贴形如 [10.0.26100.9818] 的补丁段。");
        }

        foreach (var section in sections)
        {
            if (section.Name.Length == 0 || section.Name.Contains('[') || section.Name.Contains(']'))
            {
                return RdpIniSubmission.Reject($"段名不合法：{section.Lines[0]}");
            }
        }

        if (!sections.Any(s => s.Lines.Skip(1).Any(IsKeyValueLine)))
        {
            return RdpIniSubmission.Reject("段里没有 key=value 配置行，这不是有效的补丁配置。");
        }

        var names = sections.Select(s => s.Name).ToList();

        // 同名段出现两次会让「整段替换」的区间互相踩（两个替换都指向同一个原段），
        // 合并结果不可预期。社区贴配置时偶尔会复制重了，这里当场拦住。
        var duplicate = names
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return RdpIniSubmission.Reject($"配置里有重复的 [{duplicate.Key}] 段，请只保留一份。");
        }

        var versionMatches = expectedVersion.Length > 0
            && names.Any(n => string.Equals(n, expectedVersion, StringComparison.OrdinalIgnoreCase));

        if (expectedVersion.Length > 0 && !versionMatches)
        {
            return RdpIniSubmission.Reject(
                $"配置里没有 [{expectedVersion}] 段，与本机 termsrv.dll 版本不符；已拒绝写入，也没有重启服务。");
        }

        return new RdpIniSubmission(true, "", names, versionMatches);
    }

    /// <summary>
    /// 把提交文本里的段合并进现有 INI 正文，返回合并后的全文（不碰磁盘）。
    /// 同名段按第一次出现的位置整段替换，其余段追加到末尾；换行风格沿用现有文件。
    /// </summary>
    public static RdpIniMergeOutcome Merge(string existing, string submitted)
    {
        var submittedSections = LocateSections(SplitLines(submitted));
        if (submittedSections.Count == 0)
        {
            return new RdpIniMergeOutcome(false, "提交文本里没有 [段名]。", "", 0, 0);
        }

        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(existing);
        var located = LocateSections(lines);

        // 先把所有替换区间算好（都基于原始 lines 的下标），再从后往前改，
        // 这样前面的下标不会因前面的删除而失效。
        var replacements = new List<(int Start, int End, List<string> Replacement)>();
        var pending = new List<Section>(submittedSections);
        var replaced = 0;

        foreach (var submittedSection in submittedSections)
        {
            var hit = located.FirstOrDefault(
                s => string.Equals(s.Name, submittedSection.Name, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                continue;
            }

            var next = located.FirstOrDefault(s => s.HeaderIndex > hit.HeaderIndex);
            var end = next?.HeaderIndex ?? lines.Count;
            replacements.Add((hit.HeaderIndex, end, Normalize(submittedSection.Lines)));
            pending.Remove(submittedSection);
            replaced++;
        }

        foreach (var replacement in replacements.OrderByDescending(r => r.Start))
        {
            lines.RemoveRange(replacement.Start, replacement.End - replacement.Start);
            lines.InsertRange(replacement.Start, replacement.Replacement);
        }

        var added = pending.Count;
        foreach (var section in pending)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add("");
            }

            lines.AddRange(Normalize(section.Lines));
        }

        // 去掉尾部多余空行，再补一个换行收尾：INI 不需要，但 diff 与人工编辑都干净
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var merged = string.Join(newline, lines) + newline;
        return new RdpIniMergeOutcome(true, "", merged, replaced, added);
    }

    /// <summary>段名列表（供结果文案列出写了哪些段）。</summary>
    public static string DescribeSections(IReadOnlyList<string> names) =>
        names.Count == 0 ? "（无）" : string.Join("、", names.Select(n => $"[{n}]"));

    private static bool LooksLikeWebPage(string text)
    {
        var head = text.TrimStart();
        return head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    private static List<Section> LocateSections(List<string> lines)
    {
        var result = new List<Section>();
        Section? current = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var name = SectionName(lines[i]);
            if (name is not null)
            {
                current = new Section { Name = name, HeaderIndex = i };
                current.Lines.Add(lines[i]);
                result.Add(current);
            }
            else
            {
                // 第一个段头之前的内容（注释 / [Main] 之前的抬头）不归任何段，合并时原样保留
                current?.Lines.Add(lines[i]);
            }
        }

        return result;
    }

    private static string? SectionName(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            return null;
        }

        return trimmed.Substring(1, trimmed.Length - 2).Trim();
    }

    private static bool IsKeyValueLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#')
        {
            return false;
        }

        return trimmed.IndexOf('=') > 0;
    }

    /// <summary>规范化一个段：去掉尾部空行、补一个空行与下一段分隔。</summary>
    private static List<string> Normalize(List<string> lines)
    {
        var result = lines.Select(l => l.TrimEnd('\r')).ToList();
        while (result.Count > 0 && result[^1].Trim().Length == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        result.Add("");
        return result;
    }
}
