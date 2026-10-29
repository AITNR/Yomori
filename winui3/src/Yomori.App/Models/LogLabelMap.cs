// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yomori.Models;

/// <summary>
/// 项目资源（`interface.json` → `tasks/*.json` → `locales/**/zh_cn.json`）里的
/// **entry → 中文任务名** 映射。
///
/// <para>面板档要显示「任务开始：🤝拜访好友」而不是「ParseProblemMain」，靠的就是这张表。
/// 它是不可变的：装载完成后整体替换，读线程不加锁。</para>
///
/// <para>为什么不在 Services 里读文件：这张表是纯数据 + 纯函数，冒烟工程只链 `Models/*.cs`
/// 就能断言（真实项目资源里带 `//` 注释，光这一点就值得专门钉一条断言）。</para>
/// </summary>
public sealed class LogLabelMap
{
    /// <summary>空表：读不到项目资源时的兜底，`Resolve` 原样返回 entry。</summary>
    public static readonly LogLabelMap Empty = new(new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly Dictionary<string, string> _labels;

    private LogLabelMap(Dictionary<string, string> labels) => _labels = labels;

    /// <summary>条目数（0 表示这份项目没有可用的 label）。</summary>
    public int Count => _labels.Count;

    /// <summary>按 entry 取中文名；取不到就原样返回 entry（fail-open，绝不返回空串丢信息）。</summary>
    public string Resolve(string? entry)
    {
        var key = (entry ?? string.Empty).Trim();
        if (key.Length == 0) { return string.Empty; }
        return _labels.TryGetValue(key, out var label) && label.Length > 0 ? label : key;
    }

    /// <summary>
    /// 用项目的三份资源建表。任何一份解析失败都只跳过它，不影响其余文件。
    /// </summary>
    /// <param name="interfaceJson">`interface.json` 原文。</param>
    /// <param name="taskJsonTexts">`import[]` 指向的 `tasks/*.json` 原文。</param>
    /// <param name="localeTexts">`locales/**/zh_cn.json` 原文（可多份，后读的覆盖先读的）。</param>
    public static LogLabelMap Build(
        string? interfaceJson,
        IEnumerable<string>? taskJsonTexts,
        IEnumerable<string>? localeTexts)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var locale = new Dictionary<string, string>(StringComparer.Ordinal);

        if (localeTexts is not null)
        {
            foreach (var text in localeTexts)
            {
                using var doc = LenientJson.Parse(text);
                if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) { continue; }
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        locale[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }

        void Put(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) { return; }
            var entry = Str(item, "entry");
            if (entry.Length == 0) { entry = Str(item, "name"); }
            if (entry.Length == 0) { return; }

            var label = ResolveKey(Str(item, "label"), locale);
            if (label.Length == 0) { label = Str(item, "name"); }
            if (label.Length == 0) { label = entry; }
            labels[entry] = label;
        }

        using (var doc = LenientJson.Parse(interfaceJson))
        {
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("group", out var groups) &&
                    groups.ValueKind == JsonValueKind.Array)
                {
                    foreach (var group in groups.EnumerateArray())
                    {
                        if (group.ValueKind == JsonValueKind.Object &&
                            group.TryGetProperty("task", out var tasks) &&
                            tasks.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in tasks.EnumerateArray()) { Put(item); }
                        }
                    }
                }

                if (doc.RootElement.TryGetProperty("task", out var direct) &&
                    direct.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in direct.EnumerateArray()) { Put(item); }
                }
            }
        }

        if (taskJsonTexts is not null)
        {
            foreach (var text in taskJsonTexts)
            {
                using var doc = LenientJson.Parse(text);
                if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) { continue; }
                if (!doc.RootElement.TryGetProperty("task", out var tasks) ||
                    tasks.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in tasks.EnumerateArray()) { Put(item); }
            }
        }

        return labels.Count == 0 ? Empty : new LogLabelMap(labels);
    }

    /// <summary>`"$task.X.label"` → 查词条；查不到返回去掉 `$` 的键名（可读、可定位）。</summary>
    internal static string ResolveKey(string key, IReadOnlyDictionary<string, string> locale)
    {
        if (key.Length == 0) { return string.Empty; }
        if (key[0] != '$') { return key; }
        var name = key.Substring(1);
        return locale.TryGetValue(name, out var value) && value.Length > 0 ? value : name;
    }

    private static string Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;
}

/// <summary>
/// 宽容的 JSON 解析：MaaFramework 的 `meojson` 接受 `//` 注释与尾逗号，标准 JSON 不接受。
///
/// <para>实测 `MAA_bbb` 的 `tasks/游戏启动.json` 第 223 行就有 `"若误触进了剧情": { //未测试`。
/// 不剥注释，这一份文件整体解析失败 → 该项目的任务名全部退化成英文 entry，
/// 而界面上又看不出任何异常（面板只是显示 `ParseProblemMain`）。</para>
/// </summary>
public static class LenientJson
{
    /// <summary>先按原样解析；失败再剥注释与尾逗号重试。都失败返回 null。</summary>
    public static JsonDocument? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        foreach (var candidate in new[] { text, StripCommentsAndTrailingCommas(text) })
        {
            try
            {
                return JsonDocument.Parse(candidate);
            }
            catch (JsonException)
            {
                // 换下一个候选
            }
        }

        return null;
    }

    /// <summary>去掉 `//`、`/* */` 注释与对象/数组尾逗号。字符串字面量内的内容一律不动。</summary>
    public static string StripCommentsAndTrailingCommas(string text)
    {
        var output = new StringBuilder(text.Length);
        var inString = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (inString)
            {
                output.Append(ch);
                if (ch == '\\' && i + 1 < text.Length)
                {
                    output.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                if (ch == '"') { inString = false; }
                i++;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                output.Append(ch);
                i++;
                continue;
            }

            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var next = text.IndexOf('\n', i);
                i = next < 0 ? text.Length : next;
                continue;
            }

            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var next = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = next < 0 ? text.Length : next + 2;
                continue;
            }

            output.Append(ch);
            i++;
        }

        // 尾逗号：`,` 后面只有空白再接 `}` 或 `]`
        return Regex.Replace(output.ToString(), @",(\s*[}\]])", "$1");
    }
}

/// <summary>把日志里带富文本 / HTML 实体 / i18n 键的文本洗成能直接看的一句话。</summary>
public static class LogTextSanitizer
{
    /// <summary>
    /// 只剥**已知的 HTML 标签**，不剥"看起来像标签"的一切。
    ///
    /// <para>反例：MaaFW 的外壳日志里有 `actual_type=&lt;class 'dict'&gt;`，
    /// 用 `&lt;[^&gt;]+&gt;` 一刀切会把它变成 `actual_type=` —— 一条本来有用的配置错误被洗成残句。</para>
    /// </summary>
    private static readonly Regex Tag = new(
        @"</?(?:div|span|b|i|u|s|em|strong|font|color|size|br|p|a|img|sub|sup|small|big)(?:\s[^>]{0,300})?/?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Whitespace = new(@"\s{2,}", RegexOptions.Compiled);

    private static readonly (string From, string To)[] Entities =
    {
        ("&lt;", "<"), ("&gt;", ">"), ("&amp;", "&"), ("&quot;", "\""), ("&#39;", "'"), ("&nbsp;", " "),
    };

    /// <summary>
    /// 剥标签 → 反转义 → 压空白 → 解析 `$i18n` → 截断。
    ///
    /// <para>剥完为空时**返回原文**：有的项目拿纯标签做排版，
    /// 把它变成空行等于把这一行变成不可见（面板上表现为"少了几行"）。</para>
    /// </summary>
    public static string Clean(string? text, LogLabelMap? labels)
    {
        var raw = text ?? string.Empty;
        var value = Tag.Replace(raw, string.Empty);
        foreach (var (from, to) in Entities)
        {
            value = value.Replace(from, to, StringComparison.Ordinal);
        }

        // 单个换行也算行界：focus 里带 `\n` 时不能把它带进一行文本（会撑成两条并错位时间戳）。
        value = value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        value = Whitespace.Replace(value, " ").Trim();
        if (value.Length == 0) { return string.Empty; }

        if (value[0] == '$' && labels is not null)
        {
            value = labels.Resolve(value);
            // 单个换行也算行界：focus 里带 `\n` 时不能把它带进一行文本（会撑成两条并错位时间戳）。
        value = value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        value = Whitespace.Replace(value, " ").Trim();
        }

        if (raw.Length > 0 && value.Length == 0) { return Whitespace.Replace(raw, " ").Trim(); }

        return value.Length <= ExternalLogDefaults.MaxLineLength
            ? value
            : value.Substring(0, ExternalLogDefaults.MaxLineLength) + "\u2026";
    }
}
