// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>
/// 脚本所在目录的"画像" —— 识别逻辑的输入。
///
/// 为什么不在 Models 里直接读盘：冒烟工程把 <c>Models/*.cs</c> 直接链进纯 .NET 工程编译，
/// 判定必须是纯函数才好断言；读文件、解析 JSON 这些副作用统一放
/// <c>Services/LaunchPresetStore.cs</c>，填好这个对象再交给判定层。
/// </summary>
public sealed class LauncherProfile
{
    /// <summary>脚本所在目录（绝对路径）；空表示没算出来。</summary>
    public string ExeDirectory { get; set; } = string.Empty;

    /// <summary>目录里有没有 <c>interface.json</c>（MaaFramework 项目的标志）。</summary>
    public bool HasInterfaceJson { get; set; }

    /// <summary>
    /// <c>interface.json</c> 里的 <c>mirrorchyan_rid</c> —— **就是 Mirror酱的项目 id**，
    /// 与预设库的 <see cref="LaunchProject.Id"/> 同源。这是最可靠的识别依据：
    /// <c>MFAAvalonia.exe</c> 被十几个项目复用，只有它能区分"这个 exe 属于哪个项目"。
    /// </summary>
    public string MirrorchyanRid { get; set; } = string.Empty;

    /// <summary><c>interface.json</c> 里的 <c>name</c>，rid 缺失时用它兜底匹配。</summary>
    public string InterfaceName { get; set; } = string.Empty;

    /// <summary>有没有 <c>config/maa_pi_config.json</c>：决定 <c>MaaPiCli -d</c> 能不能用。</summary>
    public bool HasPiConfig { get; set; }

    /// <summary>相对路径的存在性索引（正斜杠、小写），给 <see cref="LaunchCommand.RequiresFile"/> 用。</summary>
    public HashSet<string> PresentRelativeFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>探测到的日志目录（绝对路径，按候选顺序）。都探测不到就是空。</summary>
    public List<string> LogDirectories { get; set; } = new();

    /// <summary>
    /// 「相对目录名 → 是否存在」的索引（正斜杠、小写，如 <c>debug</c> / <c>runtime/logs</c>）。
    ///
    /// <para>为什么需要它而不是直接用 <see cref="LogDirectories"/>：后者只按固定的
    /// <c>debug → logs → log</c> 顺序留了绝对路径，而日志预设要按**自己的顺序**挑目录
    /// （MFAAvalonia 先 logs 后 debug，三月七只要 logs）。索引在 Services 层一次探测生成，
    /// 判定层只做纯查表 + 字符串拼接，仍然可以脱离文件系统断言。</para>
    /// </summary>
    public HashSet<string> ExistingRelativeDirs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>某个相对目录是否存在（容忍 <c>\</c>、<c>./</c>、首尾斜杠的写法差异）。</summary>
    public bool HasRelativeDir(string? relativePath) =>
        ExistingRelativeDirs.Contains(NormalizeRelative(relativePath));

    public bool HasFile(string relativePath)
    {
        var key = NormalizeRelative(relativePath);
        return key.Length > 0 && PresentRelativeFiles.Contains(key);
    }

    public static string NormalizeRelative(string path) =>
        (path ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}
