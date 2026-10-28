// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Yomori.Models;

/// <summary>
/// 启动方式预设库（<c>Presets/launch_presets.json</c> 的反序列化目标）。
///
/// 为什么拆成「frontends + projects」两层：MaaFramework 生态里四十多个助手的命令行参数
/// 完全一样（<c>--autostart / -q / -f</c>），它们只是把通用 GUI 换个 exe 名重新打包。
/// 参数按前端存一份、项目用 <see cref="LaunchProject.Frontend"/> 引用，
/// 否则每加一个助手就要复制五条命令，改一处漏一处。
/// </summary>
public sealed class LaunchPresetLibrary
{
    [JsonPropertyName("rev")]
    public int Rev { get; set; }

    [JsonPropertyName("updatedOn")]
    public string UpdatedOn { get; set; } = string.Empty;

    [JsonPropertyName("frontends")]
    public List<LaunchFrontend> Frontends { get; set; } = new();

    [JsonPropertyName("projects")]
    public List<LaunchProject> Projects { get; set; } = new();
}

/// <summary>一套参数（属于某个通用前端，如 MFAAvalonia / MaaPiCli / ok-script）。</summary>
public sealed class LaunchFrontend
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>这个前端自己的可执行文件名（<c>MFAAvalonia.exe</c> 之类），用于 L3 识别。</summary>
    [JsonPropertyName("entries")]
    public List<string> Entries { get; set; } = new();

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("checkedOn")]
    public string CheckedOn { get; set; } = string.Empty;

    [JsonPropertyName("commands")]
    public List<LaunchCommand> Commands { get; set; } = new();

    /// <summary>需要用户自己补名字/组名时的示例（下拉里放不下，只能写在说明行）。</summary>
    [JsonPropertyName("manualHint")]
    public string ManualHint { get; set; } = string.Empty;
}

/// <summary>一个上游项目（三月七 / BetterGI / M9A …）。</summary>
public sealed class LaunchProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("game")]
    public string Game { get; set; } = string.Empty;

    /// <summary>该项目的启动文件候选（不含路径），用于 L1 识别。</summary>
    [JsonPropertyName("entries")]
    public List<string> Entries { get; set; } = new();

    /// <summary>引用的 <see cref="LaunchFrontend.Id"/>；为空表示该项目自带参数。</summary>
    [JsonPropertyName("frontend")]
    public string Frontend { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("checkedOn")]
    public string CheckedOn { get; set; } = string.Empty;

    [JsonPropertyName("commands")]
    public List<LaunchCommand> Commands { get; set; } = new();

    [JsonPropertyName("manualHint")]
    public string ManualHint { get; set; } = string.Empty;
}

/// <summary>一条「启动方式」= 一组参数 + 它对用户意味着什么。</summary>
public sealed class LaunchCommand
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>只放参数，绝不放整条命令 —— 否则 <c>CommandLine.BuildTaskCommand</c>
    /// 会把它当成"完整命令"另走一套分支，绕过脚本路径。</summary>
    [JsonPropertyName("args")]
    public string Args { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>界面上原样展示的前置条件（必须先在界面勾任务、需要管理员…）。</summary>
    [JsonPropertyName("precondition")]
    public string Precondition { get; set; } = string.Empty;

    /// <summary>Args 为空时预填它（每个命令族最多一个）。</summary>
    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    /// <summary>建议的运行时长（分钟）；勾了「同时套用推荐设置」才写。</summary>
    [JsonPropertyName("timeoutMinutes")]
    public int TimeoutMinutes { get; set; }

    /// <summary>参数里的序号/名字随版本变化（ok-script 的 <c>-t N</c>），选中时提醒。</summary>
    [JsonPropertyName("volatile")]
    public bool Volatile { get; set; }

    /// <summary>false = 只在项目 README 里出现过框架名，参数未在该项目文档中确认。</summary>
    [JsonPropertyName("verified")]
    public bool Verified { get; set; } = true;

    /// <summary>必须是目录里存在这个文件，这条参数才有意义（如 MaaPiCli 的 <c>-d</c>）。</summary>
    [JsonPropertyName("requiresFile")]
    public string RequiresFile { get; set; } = string.Empty;

    [JsonPropertyName("monitor")]
    public LaunchMonitor? Monitor { get; set; }

    [JsonPropertyName("placeholders")]
    public List<LaunchPlaceholder> Placeholders { get; set; } = new();

    /// <summary>含 <c>{...}</c> 的参数不进下拉（v2.1 不做占位符替换，见计划书 §1.5）。</summary>
    [JsonIgnore]
    public bool HasPlaceholder => Args.IndexOf('{') >= 0;

    [JsonIgnore]
    public bool CanSelect => !HasPlaceholder;
}

/// <summary>完成判定建议。<see cref="Kind"/> 三选一，绝不同时给两种。</summary>
public sealed class LaunchMonitor
{
    /// <summary><c>process</c> / <c>window</c> / <c>direct</c>，取自 <see cref="MonitorKinds"/>。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = MonitorKinds.Direct;

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();
}

/// <summary>参数里的占位符声明（目前只登记，不替换）。</summary>
public sealed class LaunchPlaceholder
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

/// <summary>
/// 下拉里的一项（界面专用，纯数据）。
///
/// ⚠️ 可编辑 <c>ComboBox</c> 在文本框里显示的是 <see cref="ToString()"/> 的结果，
/// 不是 <c>DisplayMemberPath</c>。所以这里必须重写 ToString 返回"参数本身"，
/// 否则用户看到的会是 <c>Yomori.Models.LaunchChoice</c>。
/// </summary>
public sealed class LaunchChoice
{
    public string ProjectId { get; init; } = string.Empty;

    public string ProjectName { get; init; } = string.Empty;

    public string CommandId { get; init; } = string.Empty;

    public string Args { get; init; } = string.Empty;

    /// <summary>下拉项的第二行：动作名 + 一句话作用。</summary>
    public string Detail { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Precondition { get; init; } = string.Empty;

    public bool Primary { get; init; }

    public bool Volatile { get; init; }

    public bool Verified { get; init; } = true;

    public int TimeoutMinutes { get; init; }

    public LaunchMonitor? Monitor { get; init; }

    /// <summary>来源标记，写进 <c>TaskConfig.LaunchPresetId</c>：<c>m7a:daily</c>。</summary>
    public string Key => string.IsNullOrEmpty(ProjectId) ? CommandId : $"{ProjectId}:{CommandId}";

    public override string ToString() => Args;
}
