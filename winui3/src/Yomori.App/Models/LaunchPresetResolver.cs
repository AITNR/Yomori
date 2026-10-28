// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Yomori.Models;

/// <summary>识别结果的来源，界面上要如实显示（用户才知道哪一环是猜的）。</summary>
public enum LaunchConfidence
{
    /// <summary>四级都没命中，只剩通用模板。</summary>
    None = 0,

    /// <summary>只认出"用的是哪个通用前端"（MFAAvalonia / MaaPiCli …），不知道具体项目。</summary>
    Frontend = 1,

    /// <summary>按可执行文件名命中项目。</summary>
    FileName = 2,

    /// <summary>按 interface.json 里的 mirrorchyan_rid / name 命中项目（最可靠）。</summary>
    InterfaceJson = 3,
}

/// <summary>识别结果。</summary>
public sealed class LaunchResolution
{
    public LaunchProject? Project { get; init; }

    public LaunchFrontend? Frontend { get; init; }

    public LaunchConfidence Confidence { get; init; }

    /// <summary>给界面显示的一句话依据，如「已识别：M9A（依据 interface.json）」。</summary>
    public string Basis { get; init; } = string.Empty;

    public string ProjectId => Project?.Id ?? string.Empty;
}

/// <summary>
/// 识别 + 候选合并 + 命中反查 + 写字段。
///
/// **全部是纯函数**（输入只有预设库、脚本路径字符串、目录画像），不读盘、不碰 XAML ——
/// 这样冒烟工程能直接把 <c>Models/*.cs</c> 链过去跑断言（见计划书 §7.2）。
/// </summary>
public static class LaunchPresetResolver
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>参数归一：折叠空白、去首尾。大小写不合并（返回值仍可用于忽略大小写的比较）。</summary>
    public static string NormalizeArgs(string? args) =>
        Whitespace.Replace((args ?? string.Empty).Trim(), " ");

    /// <summary>两个参数串是否等价（多一个空格、大小写不同都算同一条）。</summary>
    public static bool SameArgs(string? a, string? b) =>
        string.Equals(NormalizeArgs(a), NormalizeArgs(b), StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- 识别

    /// <summary>
    /// 四级识别（优先级从高到低）：
    /// L2 目录画像（interface.json 的 rid / name，最可靠）→ L1 文件名 → L3 通用前端 → L4 兜底。
    ///
    /// L2 排在 L1 前面是有意的：文件名可能被用户改过，而 rid 是项目自己写死的。
    /// </summary>
    public static LaunchResolution Resolve(LaunchPresetLibrary library, string? scriptPath, LauncherProfile? profile)
    {
        if (library is null)
        {
            return new LaunchResolution { Confidence = LaunchConfidence.None, Basis = "预设库为空" };
        }

        var exeName = FileName(scriptPath);

        // L2：interface.json 的 rid / name
        if (profile is not null && (profile.MirrorchyanRid.Length > 0 || profile.InterfaceName.Length > 0))
        {
            foreach (var project in library.Projects)
            {
                if (project.Id.Length > 0 && SameProjectKey(project.Id, profile.MirrorchyanRid))
                {
                    return new LaunchResolution
                    {
                        Project = project,
                        Frontend = PickFrontend(library, exeName, project),
                        Confidence = LaunchConfidence.InterfaceJson,
                        Basis = $"已识别：{project.Name}（依据 interface.json 的 mirrorchyan_rid）",
                    };
                }
            }

            foreach (var project in library.Projects)
            {
                if (SameProjectKey(project.Name, profile.InterfaceName))
                {
                    return new LaunchResolution
                    {
                        Project = project,
                        Frontend = PickFrontend(library, exeName, project),
                        Confidence = LaunchConfidence.InterfaceJson,
                        Basis = $"已识别：{project.Name}（依据 interface.json 的 name）",
                    };
                }
            }
        }

        // L1：可执行文件名
        if (exeName.Length > 0)
        {
            foreach (var project in library.Projects)
            {
                foreach (var entry in project.Entries)
                {
                    if (FileName(entry).Equals(exeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return new LaunchResolution
                        {
                            Project = project,
                            Frontend = PickFrontend(library, exeName, project),
                            Confidence = LaunchConfidence.FileName,
                            Basis = $"已识别：{project.Name}（依据文件名）",
                        };
                    }
                }
            }
        }

        // L3：通用前端的 exe（MFAAvalonia.exe / MaaPiCli.exe / mxu.exe / MFW.exe）
        if (exeName.Length > 0)
        {
            foreach (var frontend in library.Frontends)
            {
                foreach (var entry in frontend.Entries)
                {
                    if (FileName(entry).Equals(exeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return new LaunchResolution
                        {
                            Frontend = frontend,
                            Confidence = LaunchConfidence.Frontend,
                            Basis = $"未收录项目，使用通用前端参数（{frontend.Name}）",
                        };
                    }
                }
            }
        }

        // L4：兜底 —— 只有"不带参数启动"
        return new LaunchResolution
        {
            Frontend = FindFrontend(library, "generic") ?? (library.Frontends.Count > 0 ? library.Frontends[0] : null),
            Confidence = LaunchConfidence.None,
            Basis = "未收录这个脚本，参数可手动填写",
        };
    }

    public static LaunchFrontend? FindFrontend(LaunchPresetLibrary library, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        foreach (var frontend in library.Frontends)
        {
            if (string.Equals(frontend.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return frontend;
            }
        }

        return null;
    }

    /// <summary>
    /// 决定用哪套前端参数：**先看用户选的那个 exe 本身是谁**，再回落到项目声明的前端。
    ///
    /// 为什么必须是这个顺序：同一个 MaaFramework 项目里可能同时带 <c>M9A.exe</c>（MFAAvalonia 壳）
    /// 和 <c>MaaPiCli.exe</c>（框架命令行）。用户选的是后者时，项目声明的 mfaa 参数
    /// （<c>--autostart</c>）对它完全无效 —— 它只认 <c>-d</c>。
    /// </summary>
    private static LaunchFrontend? PickFrontend(LaunchPresetLibrary library, string exeName, LaunchProject project)
    {
        if (exeName.Length > 0)
        {
            foreach (var frontend in library.Frontends)
            {
                foreach (var entry in frontend.Entries)
                {
                    if (FileName(entry).Equals(exeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return frontend;
                    }
                }
            }
        }

        return FindFrontend(library, project.Frontend);
    }

    // ---------------------------------------------------------------- 候选

    /// <summary>
    /// 合并候选：项目自带参数在前（更具体），前端通用参数在后；按归一后的参数去重。
    /// 只保留"能直接跑"的（不含占位符、所需文件存在）。
    /// </summary>
    public static List<LaunchChoice> BuildChoices(
        LaunchResolution resolution, LauncherProfile? profile, out string manualHint)
    {
        var choices = new List<LaunchChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        manualHint = string.Empty;

        if (resolution?.Project is { } project)
        {
            AppendCommandList(choices, seen, project.Commands, project.Id, project.Name, profile);
            if (manualHint.Length == 0)
            {
                manualHint = project.ManualHint;
            }
        }

        if (resolution?.Frontend is { } frontend)
        {
            AppendCommandList(choices, seen, frontend.Commands, resolution.ProjectId, frontend.Name, profile);
            if (manualHint.Length == 0)
            {
                manualHint = frontend.ManualHint;
            }
        }

        return choices;
    }

    private static void AppendCommandList(
        List<LaunchChoice> choices, HashSet<string> seen, List<LaunchCommand>? commands,
        string projectId, string projectName, LauncherProfile? profile)
    {
        if (commands is null)
        {
            return;
        }

        foreach (var command in commands)
        {
            if (!command.CanSelect)
            {
                continue;   // 含占位符：v2.1 不进下拉，靠说明行给手写模板
            }

            if (command.RequiresFile.Length > 0 && profile is not null && !profile.HasFile(command.RequiresFile))
            {
                continue;   // 依赖的文件不在（如 MaaPiCli 没配置过），这条必然失败
            }

            var key = NormalizeArgs(command.Args);
            if (!seen.Add(key))
            {
                continue;
            }

            choices.Add(new LaunchChoice
            {
                ProjectId = projectId,
                ProjectName = projectName,
                CommandId = command.Id,
                Args = command.Args,
                Detail = string.IsNullOrWhiteSpace(command.Summary)
                    ? command.Name
                    : $"{command.Name} · {command.Summary}",
                Summary = command.Summary,
                Precondition = command.Precondition,
                Primary = command.Primary,
                Volatile = command.Volatile,
                Verified = command.Verified,
                TimeoutMinutes = command.TimeoutMinutes,
                Monitor = command.Monitor,
            });
        }
    }

    /// <summary>主推项（<see cref="LaunchChoice.Primary"/>），没有就返回第一项。</summary>
    public static LaunchChoice? Primary(List<LaunchChoice>? choices)
    {
        if (choices is null || choices.Count == 0)
        {
            return null;
        }

        foreach (var choice in choices)
        {
            if (choice.Primary)
            {
                return choice;
            }
        }

        return choices[0];
    }

    /// <summary>按文本反查命中的候选（**不用 SelectedItem**：手打时它永远是 null）。</summary>
    public static LaunchChoice? Match(List<LaunchChoice>? choices, string? args)
    {
        if (choices is null || choices.Count == 0)
        {
            return null;
        }

        var normalized = NormalizeArgs(args);
        foreach (var choice in choices)
        {
            if (SameArgs(choice.Args, normalized))
            {
                return choice;
            }
        }

        return null;
    }

    /// <summary>
    /// 最接近的一条（编辑距离），只用于"你可能想写的是…"的提示，**绝不自动替换**。
    /// 找不到足够接近的就返回 null。
    /// </summary>
    public static LaunchChoice? Nearest(List<LaunchChoice>? choices, string? args)
    {
        if (choices is null || choices.Count == 0)
        {
            return null;
        }

        var target = NormalizeArgs(args).ToLowerInvariant();
        if (target.Length == 0)
        {
            return null;
        }

        LaunchChoice? best = null;
        var bestDistance = int.MaxValue;
        foreach (var choice in choices)
        {
            var candidate = NormalizeArgs(choice.Args).ToLowerInvariant();
            if (candidate.Length == 0)
            {
                continue;
            }

            var distance = Distance(target, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = choice;
            }
        }

        if (best is null)
        {
            return null;
        }

        var limit = Math.Max(2, target.Length / 3);
        return bestDistance <= limit ? best : null;
    }

    // ---------------------------------------------------------------- 应用

    /// <summary>
    /// 把一条候选写进任务。
    ///
    /// <paramref name="applyRecommended"/> = 界面上的「同时套用推荐设置」：
    /// 打开时顺带写超时、完成判断（三选一，绝不两种同时写）、外部日志目录。
    /// **永不覆盖**通道归属、并发、清理进程名、任务名 —— 那些是"这台机器"的信息，与启动参数无关。
    /// </summary>
    public static void Apply(
        LaunchChoice choice, TaskConfig task, bool applyRecommended, string? logDirectory, int rev)
    {
        if (choice is null || task is null)
        {
            return;
        }

        task.UseArgs = true;
        task.Args = choice.Args;
        task.LaunchPresetId = choice.Key;
        task.LaunchPresetRev = rev;

        if (!applyRecommended)
        {
            return;
        }

        if (choice.TimeoutMinutes > 0)
        {
            task.TimeoutMinutes = choice.TimeoutMinutes;
        }

        var monitor = choice.Monitor;
        if (monitor is not null)
        {
            switch (monitor.Kind)
            {
                case MonitorKinds.Window:
                    task.WindowKeywords = new List<string>(monitor.Keywords);
                    task.ProcessKeywords = new List<string>();
                    break;

                case MonitorKinds.Process:
                    task.ProcessKeywords = new List<string>(monitor.Keywords);
                    task.WindowKeywords = new List<string>();
                    break;

                default:
                    // direct：只看本程序直接启动的进程；关键词必须清空，
                    // 否则 BuildMonitorSpec 会按 window/process 覆盖掉"等直接进程"。
                    task.WaitMode = WaitModes.DirectProcess;
                    task.ProcessKeywords = new List<string>();
                    task.WindowKeywords = new List<string>();
                    break;
            }
        }

        // 日志目录：探测不到就**不启用**（写一个不存在的路径会静默采集不到，比不启用更难查）
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            task.ExternalLogEnabled = true;
            task.ExternalLogPath = logDirectory;
        }
    }

    // ---------------------------------------------------------------- 内部

    private static string FileName(string? pathOrName)
    {
        var text = (pathOrName ?? string.Empty).Trim().Trim('"');
        if (text.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileName(text);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private static bool SameProjectKey(string? a, string? b)
    {
        var left = (a ?? string.Empty).Replace(" ", string.Empty).Trim();
        var right = (b ?? string.Empty).Replace(" ", string.Empty).Trim();
        return left.Length > 0 && right.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Levenshtein 距离（只用于"最接近的一条"提示，字符串都很短，不优化也能跑）。</summary>
    private static int Distance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
