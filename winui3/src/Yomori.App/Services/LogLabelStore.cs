// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 读项目资源，建「entry → 中文任务名」映射（面板档的 `任务开始：🤝拜访好友` 就靠它）。
///
/// <para><b>为什么要单独一个类</b>：`interface.json` 只是一张索引，真正的任务名在
/// `import[]` 指向的 `tasks/*.json` 里，而 `label` 又常常是 `$task.Xxx.label` 这样的 i18n 键，
/// 要去 `locales/**/zh_cn.json` 再查一次。三跳都读错一个地方，界面上只是显示英文 entry，
/// **没有任何异常**——所以解析逻辑放 Models 做纯函数（可断言），这里只负责 IO 与缓存。</para>
///
/// <para><b>成本</b>（实测 MaaEnd）：80 个任务文件 / 5.3 MB + 3 个词条文件 / 262 KB。
/// 因此必须异步装载、带预算闸，读不到就退化成 entry 名。</para>
/// </summary>
public static class LogLabelStore
{
    /// <summary>总读取上限：超过就停手（极端情况：几十个项目目录塞在一起）。</summary>
    private const long MaxTotalBytes = 20L * 1024 * 1024;

    /// <summary>总耗时上限。</summary>
    private const int MaxElapsedMs = 8_000;

    /// <summary>缓存条目上限（多开时每个实例一个目录）。</summary>
    private const int MaxEntries = 8;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string Stamp, LogLabelMap Map)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取某个脚本目录的任务名映射。同步、带缓存；读不到返回 <see cref="LogLabelMap.Empty"/>。
    /// **不要**在 UI 线程上直接调用 —— 用 <see cref="GetAsync"/>。
    /// </summary>
    public static LogLabelMap Get(string? scriptPath)
    {
        var directory = ResolveDirectory(scriptPath);
        if (directory.Length == 0) { return LogLabelMap.Empty; }

        var stamp = BuildStamp(directory);
        lock (Gate)
        {
            if (Cache.TryGetValue(directory, out var hit) && hit.Stamp == stamp) { return hit.Map; }
        }

        var map = Load(directory);
        lock (Gate)
        {
            if (Cache.Count >= MaxEntries)
            {
                var oldest = Cache.Keys.FirstOrDefault();
                if (oldest is not null) { Cache.Remove(oldest); }
            }

            Cache[directory] = (stamp, map);
        }

        return map;
    }

    /// <summary>后台装载（调用方拿到结果后自己 <c>SetLabels</c> 给投影器）。</summary>
    public static Task<LogLabelMap> GetAsync(string? scriptPath) =>
        Task.Run(() => Get(scriptPath));

    private static string ResolveDirectory(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.Length == 0) { return string.Empty; }
        try
        {
            if (File.Exists(text)) { return Path.GetDirectoryName(text) ?? string.Empty; }
            return Directory.Exists(text) ? text : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>缓存戳：三处资源的最后写入时间。项目一更新就重建。</summary>
    private static string BuildStamp(string directory)
    {
        static long Stamp(string path)
        {
            try
            {
                if (File.Exists(path)) { return File.GetLastWriteTimeUtc(path).Ticks; }
                return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        return $"{Stamp(Path.Combine(directory, "interface.json"))}|" +
               $"{Stamp(Path.Combine(directory, "tasks"))}|" +
               $"{Stamp(Path.Combine(directory, "locales"))}|" +
               $"{Stamp(Path.Combine(directory, "resource", "pipeline"))}";
    }

    private static LogLabelMap Load(string directory)
    {
        var watch = Stopwatch.StartNew();
        long bytes = 0;

        try
        {
            var interfacePath = Path.Combine(directory, "interface.json");
            if (!File.Exists(interfacePath)) { return LogLabelMap.Empty; }

            var interfaceText = ReadText(interfacePath, ref bytes, watch);
            if (interfaceText is null) { return LogLabelMap.Empty; }

            // import[] 指向的任务文件；没有 import 就退化为扫 tasks 目录
            var taskTexts = new List<string>();
            foreach (var relative in ReadImports(interfaceText))
            {
                if (watch.ElapsedMilliseconds > MaxElapsedMs || bytes > MaxTotalBytes) { break; }
                var full = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) { continue; }
                var text = ReadText(full, ref bytes, watch);
                if (text is not null) { taskTexts.Add(text); }
            }

            if (taskTexts.Count == 0)
            {
                foreach (var full in Enumerate(directory, "tasks", "*.json"))
                {
                    if (watch.ElapsedMilliseconds > MaxElapsedMs || bytes > MaxTotalBytes) { break; }
                    var text = ReadText(full, ref bytes, watch);
                    if (text is not null) { taskTexts.Add(text); }
                }
            }

            var localeTexts = new List<string>();
            foreach (var full in Enumerate(directory, "locales", "zh_cn.json"))
            {
                if (watch.ElapsedMilliseconds > MaxElapsedMs || bytes > MaxTotalBytes) { break; }
                var text = ReadText(full, ref bytes, watch);
                if (text is not null) { localeTexts.Add(text); }
            }

            // pipeline：节点名的唯一中文来源。MaaEnd 的 nodes.json 单个 4.3 MB，
            // 所以在 tasks 之后读，超预算就停（任务名比节点名更关键）。
            var pipelineTexts = new List<string>();
            foreach (var full in EnumeratePipelines(directory, interfaceText))
            {
                if (watch.ElapsedMilliseconds > MaxElapsedMs || bytes > MaxTotalBytes) { break; }
                var text = ReadText(full, ref bytes, watch);
                if (text is not null) { pipelineTexts.Add(text); }
            }

            return LogLabelMap.Build(interfaceText, taskTexts, localeTexts, pipelineTexts);
        }
        catch (Exception)
        {
            // 读项目资源失败不该影响日志采集本身：退回 entry 名即可。
            return LogLabelMap.Empty;
        }
    }

    private static IEnumerable<string> ReadImports(string interfaceText)
    {
        using var doc = LenientJson.Parse(interfaceText);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) { yield break; }
        if (!doc.RootElement.TryGetProperty("import", out var imports) ||
            imports.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in imports.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) { continue; }
            var value = (item.GetString() ?? string.Empty).Trim();
            if (value.Length > 0) { yield return value; }
        }
    }

    /// <summary>
    /// 找出 pipeline 目录下的 json 文件。
    ///
    /// <para>路径优先取 `interface.json` 的 `resource[].path`（可能是字符串或数组），
    /// 取不到再试 `<项目>/resource/pipeline`、`<项目>/pipeline`。
    /// **只取第一个 resource**：MaaEnd 有 resource / resource_adb / resource_linux 等 7 套，
    /// 内容相同，全读一次就是 30 MB。</para>
    /// </summary>
    private static IEnumerable<string> EnumeratePipelines(string directory, string interfaceText)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ResourceRoots(directory, interfaceText))
        {
            var pipeline = Path.Combine(root, "pipeline");
            if (!Directory.Exists(pipeline)) { continue; }
            if (!seen.Add(pipeline)) { continue; }

            string[] files;
            try
            {
                files = Directory.GetFiles(pipeline, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                continue;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files) { yield return file; }
            yield break;      // 第一个存在的 resource 就是它，别把 7 套副本全读一遍
        }
    }

    private static IEnumerable<string> ResourceRoots(string directory, string interfaceText)
    {
        using var doc = LenientJson.Parse(interfaceText);
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("resource", out var resources) &&
            resources.ValueKind == JsonValueKind.Array)
        {
            foreach (var resource in resources.EnumerateArray())
            {
                if (resource.ValueKind != JsonValueKind.Object) { continue; }
                if (!resource.TryGetProperty("path", out var paths)) { continue; }
                foreach (var relative in Flatten(paths))
                {
                    var full = FullPath(directory, relative);
                    if (full.Length > 0) { yield return full; }
                }
            }
        }

        yield return Path.Combine(directory, "resource");
        yield return directory;
    }

    /// <summary>`"path": "./resource"` 与 `"path": ["./resource", "./x"]` 两种写法都认。</summary>
    private static IEnumerable<string> Flatten(JsonElement paths)
    {
        if (paths.ValueKind == JsonValueKind.String)
        {
            var single = (paths.GetString() ?? string.Empty).Trim();
            if (single.Length > 0) { yield return single; }
            yield break;
        }

        if (paths.ValueKind != JsonValueKind.Array) { yield break; }
        foreach (var item in paths.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) { continue; }
            var value = (item.GetString() ?? string.Empty).Trim();
            if (value.Length > 0) { yield return value; }
        }
    }

    private static string FullPath(string baseDirectory, string relative)
    {
        try
        {
            var trimmed = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart('.', '\\', '/');
            var full = Path.GetFullPath(Path.Combine(baseDirectory, trimmed));

            // 不允许跑到项目目录外面去（项目文件里写 `../..` 的情况不是没有）
            if (!full.StartsWith(Path.GetFullPath(baseDirectory), StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return Directory.Exists(full) ? full : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> Enumerate(string directory, string sub, string pattern)
    {
        var root = Path.Combine(directory, sub);
        if (!Directory.Exists(root)) { yield break; }

        string[] files;
        try
        {
            files = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var file in files) { yield return file; }
    }

    private static string? ReadText(string path, ref long bytes, Stopwatch watch)
    {
        try
        {
            if (watch.ElapsedMilliseconds > MaxElapsedMs || bytes > MaxTotalBytes) { return null; }
            var info = new FileInfo(path);
            if (info.Length > MaxTotalBytes) { return null; }
            bytes += info.Length;
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
