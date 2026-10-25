// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>一份外部日志文件的摘要（设置页「占用」列表用）。</summary>
public sealed class ExternalLogFileInfo
{
    public string FilePath { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    /// <summary>通道标识（本地执行组为 <c>local</c>）。</summary>
    public string ChannelTag { get; init; } = string.Empty;

    /// <summary>
    /// 这份日志是哪一处写出来的（见 <see cref="ExternalLogRoot.Label"/>）。
    ///
    /// 必须有：同一个通道标识可能在两个位置各有一份 —— 目标账户会话里跑的那份由代理写在
    /// <c>%ProgramData%\Yomori\agent\external_logs</c>，关掉远程执行时由主控端写在
    /// 程序目录的 <c>external_logs</c>。只显示通道名会让两行长得一模一样。
    /// </summary>
    public string SourceLabel { get; init; } = string.Empty;

    public long Bytes { get; init; }

    public DateTime LastWriteTime { get; init; }

    /// <summary>给人看的体积文本，例如「312 KB」。</summary>
    public string SizeText => ExternalLogStore.FormatBytes(Bytes);
}

/// <summary>外部日志目录的占用概览。</summary>
public sealed class ExternalLogUsage
{
    public int FileCount { get; init; }

    public long TotalBytes { get; init; }

    public string TotalText => ExternalLogStore.FormatBytes(TotalBytes);

    public List<ExternalLogFileInfo> Files { get; init; } = new();
}

/// <summary>一处外部日志落盘位置（主控端查看 / 清理时要覆盖的那些）。</summary>
public sealed class ExternalLogRoot
{
    /// <summary>根目录绝对路径（<c>...\external_logs</c>）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>这处日志是谁写的 —— 界面上必须标出来。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>是不是「目标账户会话里的代理」写的。</summary>
    public bool IsAgent { get; init; }
}

/// <summary>
/// 外部脚本日志的**独立**落盘 —— 与Yomori自己的会话日志（<c>logs/YYYY-MM-DD.log</c>）完全分开。
///
/// <para>目录结构：<c>external_logs/&lt;通道标识&gt;/&lt;通道标识&gt;-YYYY-MM-DD.log</c>
/// —— 也就是「每个通道每天一个文件」。</para>
///
/// <para>为什么独立：会话日志是"Yomori做了什么"，外部日志是"脚本说了什么"，
/// 两者节奏差几个数量级（BetterGI 一天几千行）。混在一起会把调度信息淹掉。</para>
///
/// <para>为什么按通道分目录：多通道并行时不同通道可能在跑**同一个**脚本
/// （比如两条通道都跑 BetterGI），共用一个文件就会互相穿插、看不出哪行属于哪条通道。</para>
///
/// <para>【两个落盘位置 —— 这是查"日志哪去了"时最容易搞错的一点】</para>
/// <list type="bullet">
///   <item><b>目标账户会话里</b>（远程执行，绝大多数场景）：日志是**代理**写的，
///         而代理的程序目录是 <c>%ProgramData%\Yomori\agent</c>，所以落在
///         <c>%ProgramData%\Yomori\agent\external_logs\</c>（副本被占用过则可能是 <c>agent_b</c>）。</item>
///   <item><b>当前进程本地执行</b>（关掉「在目标账户会话中执行任务」）：日志是主控端写的，
///         落在主控端程序目录的 <c>external_logs\local\</c>。</item>
/// </list>
/// <para>所以 <see cref="RootDir"/> 是"**本进程**的根"，而主控端要看全部日志时用
/// <see cref="ViewRoots"/>（本机 + 代理副本）。之前只看本进程那一个根，
/// 结果目标账户跑出来的日志在界面上永远是"暂无文件"、也永远清不掉。</para>
/// </summary>
public static class ExternalLogStore
{
    /// <summary>外部日志根目录名（程序根目录下）。</summary>
    public const string RootDirName = "external_logs";

    /// <summary>本地执行组的通道标识。</summary>
    public const string LocalChannelTag = "local";

    /// <summary>
    /// 本进程的外部日志根目录：<c>&lt;BaseDir&gt;\external_logs</c>。
    ///
    /// <para>注意语义是"**写日志的那个进程**自己的根"：主控端进程里它是安装目录，
    /// 代理进程里它就是 <c>%ProgramData%\Yomori\agent</c>。写入侧（<see cref="TodayFilePath"/>）
    /// 依赖这个语义，别改成"主控端视角"。</para>
    /// </summary>
    public static string RootDir => Path.Combine(AppPaths.BaseDir, RootDirName);

    /// <summary>
    /// 主控端视角下的所有外部日志根目录 —— 查看占用、清理、打开目录都要覆盖这些。
    ///
    /// 代理副本目录不存在时不列出来（还没部署代理），免得占用明细里凭空多一处空来源；
    /// 但 <see cref="PreferredRoot"/> 在"已部署但还没产生日志"时仍然会指向它。
    /// </summary>
    public static IReadOnlyList<ExternalLogRoot> ViewRoots()
    {
        var roots = new List<ExternalLogRoot>
        {
            new() { Path = RootDir, Label = "本机执行", IsAgent = false },
        };

        foreach (var dir in AgentRootDirs())
        {
            roots.Add(new ExternalLogRoot { Path = dir, Label = "目标账户", IsAgent = true });
        }

        return roots;
    }

    /// <summary>
    /// 「打开日志目录」该打开哪一处。
    ///
    /// 判断顺序按"用户最可能想看什么"来：
    ///   ① 已经有文件的那一处（同一个位置可能有多份日志、代理副本还可能有 agent_b 两份，
    ///      取文件最多的一处）；
    ///   ② 有代理副本目录但还没产生日志 → 打开代理那一处 —— 任务绝大多数跑在目标账户里，
    ///      这才是"接下来会写文件"的正确位置（以前这里打开的是主控端安装目录下的同名空目录，
    ///      用户看到的是个永远不长的空文件夹）；
    ///   ③ 代理都没部署过（本地执行）→ 本机那份。
    /// </summary>
    public static ExternalLogRoot PreferredRoot()
    {
        var roots = ViewRoots();

        var withFiles = roots
            .Select(root => (Root: root, Count: EnumerateLogFiles(root.Path).Count()))
            .Where(item => item.Count > 0)
            .OrderByDescending(item => item.Count)
            .ToList();

        if (withFiles.Count > 0)
        {
            return withFiles[0].Root;
        }

        foreach (var slot in PayloadSlotDirs())
        {
            if (!Directory.Exists(slot))
            {
                continue;
            }

            return new ExternalLogRoot
            {
                Path = Path.Combine(slot, RootDirName),
                Label = "目标账户",
                IsAgent = true,
            };
        }

        return roots[0];
    }

    /// <summary>代理副本可能存在的几个目录（首选 + 备用），顺序即优先级。</summary>
    private static IEnumerable<string> PayloadSlotDirs()
    {
        yield return RdpSessionService.AgentProgramDir;
        yield return RdpSessionService.AgentProgramDirSpare;
    }

    /// <summary>代理那几处日志根（只返回目录确实存在的）。</summary>
    private static IEnumerable<string> AgentRootDirs()
    {
        foreach (var slot in PayloadSlotDirs())
        {
            var dir = Path.Combine(slot, RootDirName);
            if (Directory.Exists(dir))
            {
                yield return dir;
            }
        }
    }

    /// <summary>
    /// 枚举一个根目录下所有通道的日志文件（连通道目录名一起返回）。
    ///
    /// 全程 try/catch：<c>%ProgramData%\Yomori\agent</c> 是多个账户共用的，
    /// 别的账户建的通道目录当前账户可能读不进去 —— 那不该让整份统计变成"暂无"。
    /// </summary>
    private static List<(FileInfo File, string Tag)> EnumerateLogFiles(string rootDir)
    {
        var files = new List<(FileInfo File, string Tag)>();

        try
        {
            var root = new DirectoryInfo(rootDir);
            if (!root.Exists)
            {
                return files;
            }

            // 先物化成列表：懒枚举的异常会在 foreach 里抛，那样连一层 catch 都套不住
            foreach (var dir in root.EnumerateDirectories().ToList())
            {
                try
                {
                    foreach (var file in dir.EnumerateFiles("*.log"))
                    {
                        files.Add((file, dir.Name));
                    }
                }
                catch
                {
                    // 单个通道目录读不进去，继续下一个
                }
            }
        }
        catch
        {
            // 整个根目录访问不了，当空处理
        }

        return files;
    }

    /// <summary>某通道的目录：<c>&lt;RootDir&gt;\&lt;通道标识&gt;</c>。</summary>
    public static string ChannelDir(string channelTag) =>
        Path.Combine(RootDir, SanitizeTag(channelTag));

    /// <summary>
    /// 某通道今天的文件：<c>&lt;通道目录&gt;\&lt;通道标识&gt;-YYYY-MM-DD.log</c>。
    /// 一天一份，跨天自动换文件。
    /// </summary>
    public static string TodayFilePath(string channelTag, DateTime now)
    {
        var tag = SanitizeTag(channelTag);
        return Path.Combine(ChannelDir(tag), tag + "-" + now.ToString("yyyy-MM-dd") + ".log");
    }

    /// <summary>
    /// 追加一行。UTF-8 无 BOM；文件不存在就建；**失败一律吞掉** ——
    /// 日志落盘绝不能影响任务执行。
    /// </summary>
    public static void Append(string filePath, string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(filePath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // 目录不可写 / 文件被占用 / 盘满 —— 都静默，主流程不受影响
        }
    }

    /// <summary>
    /// 统计外部日志的占用情况。
    ///
    /// 只认本功能自己的命名（<c>&lt;tag&gt;-YYYY-MM-DD.log</c>），
    /// 跟 <c>FileLogger.PruneOldSessions</c> 同样的三条纪律：只认命名、按写入时间排序、全程 try/catch。
    /// 乱删别的日志文件比不清理严重得多。
    /// </summary>
    public static ExternalLogUsage GetUsage()
    {
        var result = new List<ExternalLogFileInfo>();

        // ⚠️ 必须遍历 ViewRoots，不能只看本进程的 RootDir ——
        //    目标账户会话里跑出来的日志躺在 %ProgramData%\Yomori\agent\external_logs，
        //    之前只看本进程根目录，界面永远显示"暂无文件"。
        foreach (var root in ViewRoots())
        {
            foreach (var (file, tag) in EnumerateLogFiles(root.Path))
            {
                result.Add(new ExternalLogFileInfo
                {
                    FilePath = file.FullName,
                    FileName = file.Name,
                    ChannelTag = tag,
                    SourceLabel = root.Label,
                    Bytes = file.Length,
                    LastWriteTime = file.LastWriteTime,
                });
            }
        }

        result = result.OrderByDescending(f => f.LastWriteTime).ToList();

        return new ExternalLogUsage
        {
            FileCount = result.Count,
            TotalBytes = result.Sum(f => f.Bytes),
            Files = result,
        };
    }

    /// <summary>
    /// 删掉超出份数的日志文件。
    ///
    /// <para>**按通道各自计数**：保留 30 份 = 每条通道各留最近 30 天，
    /// 不是全部通道加起来 30 份 —— 否则通道一多，老通道的记录会先被挤掉。</para>
    ///
    /// <para>返回（删掉的文件数，释放的字节数）。失败不抛异常。</para>
    /// </summary>
    public static (int Deleted, long FreedBytes) Prune(int keepFiles)
    {
        var keep = Math.Max(1, keepFiles);
        var deleted = 0;
        var freed = 0L;

        // 两处落盘都要清。只清本机那份的话，目标账户里代理写的那份会一直涨
        // （脚本一天几千行），而且用户在界面上根本看不见它有多大 —— 报"占用 0"却占着几个 G。
        foreach (var root in ViewRoots())
        {
            foreach (var group in EnumerateLogFiles(root.Path).GroupBy(item => item.Tag))
            {
                var sessions = group.Select(item => item.File).ToList();
                if (sessions.Count <= keep)
                {
                    continue;
                }

                // 按最后写入时间排序，而不是按文件名：字典序与真实新旧不总是一致
                foreach (var file in sessions
                             .OrderByDescending(f => f.LastWriteTimeUtc)
                             .Skip(keep))
                {
                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deleted++;
                        freed += size;
                    }
                    catch
                    {
                        // 单个文件删不掉（被同步 / 占用）不影响其余清理
                    }
                }
            }
        }

        return (deleted, freed);
    }

    /// <summary>清空某个通道（或全部通道）的外部日志。返回（删掉文件数，释放字节数）。</summary>
    /// <param name="channelTag">传 null / 空 = 清空全部通道。</param>
    public static (int Deleted, long FreedBytes) Clear(string? channelTag)
    {
        var deleted = 0;
        var freed = 0L;
        var filter = string.IsNullOrWhiteSpace(channelTag) ? null : SanitizeTag(channelTag);

        // 同样要覆盖两处落盘，否则"清空"之后代理那份还在，用户会以为按钮没生效
        foreach (var root in ViewRoots())
        {
            foreach (var (file, tag) in EnumerateLogFiles(root.Path))
            {
                if (filter is not null && !string.Equals(tag, filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var size = file.Length;
                    file.Delete();
                    deleted++;
                    freed += size;
                }
                catch
                {
                    // 忽略
                }
            }
        }

        return (deleted, freed);
    }

    /// <summary>
    /// 把通道标识规整成合法的目录名片段。
    ///
    /// 通道 id 可能是 8 位十六进制（<c>5c24fe85</c>）、也可能是 <c>default</c>／中文名，
    /// 这里统一：去路径分隔符、折叠非法字符、截到 32 字符、空则回落 <c>default</c>。
    /// 目标只有一个 —— 绝不能让配置里的字符串拼出 <c>..\..</c> 这种越界路径。
    /// </summary>
    public static string SanitizeTag(string? channelTag)
    {
        var text = (channelTag ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return LocalChannelTag;
        }

        var builder = new StringBuilder(text.Length);
        var lastSeparator = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                builder.Append(ch);
                lastSeparator = false;
            }
            else if (!lastSeparator)
            {
                builder.Append('_');
                lastSeparator = true;
            }
        }

        var tag = builder.ToString().Trim('_', '.', ' ');
        if (tag.Length > 32)
        {
            tag = tag.Substring(0, 32).Trim('_', '.', ' ');
        }

        return tag.Length == 0 ? LocalChannelTag : tag;
    }

    /// <summary>字节数 → 给人看的体积文本。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        string[] units = { "B", "KB", "MB", "GB" };
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{(long)size} {units[unit]}"
            : $"{size:0.0} {units[unit]}";
    }
}
