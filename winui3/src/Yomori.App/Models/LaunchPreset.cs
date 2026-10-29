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

    /// <summary>
    /// 「按可执行文件名兜底」的日志预设，只服务于不在 <see cref="Projects"/> 里的工具
    /// （AUTO-MAS / LALC / ArkLights 之类）。不参与启动参数，不与项目条目争夺识别优先级。
    /// </summary>
    [JsonPropertyName("logsByEntry")]
    public List<LaunchLogEntry> LogsByEntry { get; set; } = new();
}

/// <summary>
/// 一条按入口文件名匹配的日志预设（兜底用）。
///
/// 为什么需要第三层：`projects` 只收录了有启动参数可配的 55 个项目，
/// 而第二大类基础设施（AUTO-MAS、千机链、ModAVD）与部分自研工具同样有日志要看，
/// 又不该为了"给它配日志"而硬塞一条空的 `commands`。
/// </summary>
public sealed class LaunchLogEntry
{
    /// <summary>可执行文件名候选（不含路径）。</summary>
    [JsonPropertyName("entries")]
    public List<string> Entries { get; set; } = new();

    [JsonPropertyName("log")]
    public LaunchLogPreset? Log { get; set; }
}

/// <summary>
/// 外部脚本实时日志的推荐值（可被用户全量改写的"初值"）。
///
/// <para>为什么挂在 frontend / project 两层而不是逐项目写 76 份：45 个 MaaFramework 项目的
/// 日志都落在 `debug\` 或 `logs\`，形态一致；逐项写既臃肿又必然写串。project 上的 `log`
/// 用于覆盖"与壳默认相反"的项目（三月七的 debug 只有截图，业务日志在 logs）。</para>
///
/// <para>合并规则是<b>整块替换</b>，不做字段级合并 —— 否则会出现"目录取项目的、模式取壳的"
/// 这种半对半错的组合，排查时无法解释。</para>
/// </summary>
public sealed class LaunchLogPreset
{
    /// <summary>
    /// 日志目录候选，**相对 exe 目录**，按序探测取第一个存在的；允许嵌套（`runtime/logs`）。
    /// 相对路径是有意的：项目升版只改版本目录名，相对路径不受影响。
    /// </summary>
    [JsonPropertyName("dirs")]
    public List<string> Dirs { get; set; } = new() { "debug", "logs", "log" };

    /// <summary>文件名匹配模式；P0 只用单个模式（多模式 `;` 需 P2 的采集器扩展）。</summary>
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = ExternalLogDefaults.Pattern;

    /// <summary>auto / utf8 / gbk。默认 auto：BOM → 严格 UTF-8 → 回退 GBK，硬写 gbk 会把 UTF-8 读成乱码。</summary>
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = ExternalLogEncodings.Auto;

    [JsonPropertyName("followTail")]
    public bool FollowTail { get; set; } = ExternalLogDefaults.FollowTail;

    /// <summary>
    /// 递归子目录。P0 采集器还不支持，带这个标记的预设一律 `enabled=false`，
    /// 等 P2（多模式 + 递归 + `\r` 行界）一起打开。
    /// </summary>
    [JsonPropertyName("recursive")]
    public bool Recursive { get; set; }

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();

    /// <summary>false = 该项目不落盘日志（或不值得采集），套用后开关必须保持关闭。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>界面灰字：为什么这么配。</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>源码/文档锚点，便于核对（与 xlsx「依据」列同源）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("checkedOn")]
    public string CheckedOn { get; set; } = string.Empty;

    /// <summary>measured（本机实测）/ source（源码核实）/ framework（框架规则）/ guess（推测）。</summary>
    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = LaunchLogConfidences.Guess;
}

/// <summary>日志预设的置信度取值与界面文案。</summary>
public static class LaunchLogConfidences
{
    public const string Measured = "measured";
    public const string Source = "source";
    public const string Framework = "framework";
    public const string Guess = "guess";

    /// <summary>给界面用的中文说明；未知值按「推测」处理，不抛。</summary>
    public static string Describe(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Measured => "日志位置来自本机实测",
        Source => "日志位置来自源码核实",
        Framework => "日志位置来自框架规则",
        _ => "日志位置为推测",
    };

    /// <summary>归一：未知值回落 guess。</summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            Measured => Measured,
            Source => Source,
            Framework => Framework,
            _ => Guess,
        };
    }
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

    /// <summary>这一套壳的日志默认值（`MFAAvalonia` 的 `logs\` + `debug\`）。项目上的 `log` 覆盖它。</summary>
    [JsonPropertyName("log")]
    public LaunchLogPreset? Log { get; set; }

    /// <summary>这套壳的默认清理登记；项目自己的 <see cref="LaunchProject.Cleanup"/> 整块覆盖它。</summary>
    [JsonPropertyName("cleanup")]
    public LaunchCleanup? Cleanup { get; set; }
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

    /// <summary>
    /// 覆盖前端默认的日志位置。整块替换（不按字段合并）：三月七的 `debug\` 只有异常截图，
    /// 必须改指 `logs\`；混着取会得到"目录对、模式错"的组合。
    /// </summary>
    [JsonPropertyName("log")]
    public LaunchLogPreset? Log { get; set; }

    /// <summary>
    /// 本项目的清理登记建议。整块替换前端的那份（不按字段合并）——
    /// 字段级合并会出现"launcher 来自项目、main 来自前端"这种半对半错的组合，
    /// 出问题时无法解释名单里那个名字是谁给的。
    /// </summary>
    [JsonPropertyName("cleanup")]
    public LaunchCleanup? Cleanup { get; set; }
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

/// <summary>
/// 「进程清理登记」建议：三栏分别落到 <c>TaskConfig</c> 的
/// <c>LauncherProcess</c> / <c>MainProcess</c> / <c>GameProcess</c>。
///
/// <para>【为什么必须有它】清理名单的名字全部来自任务配置，而预设原先只写「监控关键词」。</para>
/// <para>监控关键词确实会经 <c>BuildCleanupPlan</c> 并入②「目标进程」，但这只对
/// `monitor.kind=process` 且关键词**就是游戏本体**的项目成立（m7a/bgi/zzz）。
/// 另外两类仍然缺：关键词写的是工具自己（maaend → MaaEnd），或根本没有 monitor（51 个 direct 项目）。
/// 终末地两条正落在缺口里 —— 名单里只有工具，游戏本体一层都没有。</para>
///
/// <para>【为什么不给 game 栏批量填】写错的代价是误杀：`QtWebEngineProcess.exe` / `uninst.exe`
/// 这类通用名一旦进名单，会杀掉无关程序。所以只填有实证的，「宁可留空，不许猜」。</para>
/// </summary>
public sealed class LaunchCleanup
{
    /// <summary>落到①「启动脚本进程」。放壳 / 启动器 / 提权重启后的另一个入口名。</summary>
    [JsonPropertyName("launcher")]
    public List<string> Launcher { get; set; } = new();

    /// <summary>落到②「目标进程」。放真正执行任务的引擎进程，**禁止**放游戏本体。</summary>
    [JsonPropertyName("main")]
    public List<string> Main { get; set; } = new();

    /// <summary>落到③「游戏/扩展进程」。只在「monitor 没覆盖到游戏本体」时填。</summary>
    [JsonPropertyName("game")]
    public List<string> Game { get; set; } = new();

    /// <summary>取自 <see cref="LaunchCleanupConfidences"/>；<c>guess</c> 一律不写。</summary>
    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = LaunchCleanupConfidences.Source;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("checkedOn")]
    public string CheckedOn { get; set; } = string.Empty;

    /// <summary>有名字可写、且不是纯推测。<c>guess</c> 保留在 JSON 里供核对，但不写进任务。</summary>
    [JsonIgnore]
    public bool IsUsable =>
        !string.Equals(Confidence, LaunchCleanupConfidences.Guess, StringComparison.OrdinalIgnoreCase)
        && (Launcher.Count > 0 || Main.Count > 0 || Game.Count > 0);
}

/// <summary>清理登记的可信度。<c>guess</c> 与其它三档的区别是"会不会写进任务"。</summary>
public static class LaunchCleanupConfidences
{
    /// <summary>本机实跑观测到的真实镜像名。</summary>
    public const string Measured = "measured";

    /// <summary>官方文档 / 源码写明。</summary>
    public const string Source = "source";

    /// <summary>按框架规则推（MaaFramework 系引擎是 DLL，没有独立进程）。</summary>
    public const string Framework = "framework";

    /// <summary>推测：只登记不落库。</summary>
    public const string Guess = "guess";
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

    /// <summary>该项目的清理登记建议（项目自己的优先，否则用前端的）。</summary>
    public LaunchCleanup? Cleanup { get; init; }

    /// <summary>来源标记，写进 <c>TaskConfig.LaunchPresetId</c>：<c>m7a:daily</c>。</summary>
    public string Key => string.IsNullOrEmpty(ProjectId) ? CommandId : $"{ProjectId}:{CommandId}";

    public override string ToString() => Args;
}

/// <summary>
/// 一次「日志推荐值」的解析结果（纯数据，界面据此填五个字段 + 显示一行来源）。
///
/// <para>注意这里只有一个目录而不是列表：<c>ExternalLogTail</c> 一次只跟一个根路径，
/// 同时跟两个目录会让"当前文件"在两者之间来回跳、日志顺序错乱。
/// 多候选只在**选择阶段**用（按序探测第一个存在的）。</para>
/// </summary>
public sealed class LaunchLogPlan
{
    /// <summary>预设来源 id：项目 id / 前端 id / <c>entry:auto-mas.exe</c>。</summary>
    public string PresetId { get; init; } = string.Empty;

    public string PresetName { get; init; } = string.Empty;

    /// <summary>该项目是否值得采集；false 时开关必须保持关闭。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>探测到的日志目录（绝对路径，已被执行侧解析）；空表示没找到。</summary>
    public string Directory { get; init; } = string.Empty;

    public string Pattern { get; init; } = ExternalLogDefaults.Pattern;

    public string Encoding { get; init; } = ExternalLogEncodings.Auto;

    public bool FollowTail { get; init; } = ExternalLogDefaults.FollowTail;

    /// <summary>需要递归子目录（P0 采集器尚不支持，此类预设一律 enabled=false）。</summary>
    public bool Recursive { get; init; }

    public List<string> Keywords { get; init; } = new();

    public string Summary { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    /// <summary>measured / source / framework / guess。</summary>
    public string Confidence { get; init; } = LaunchLogConfidences.Guess;

    /// <summary>给界面显示的一句话，如「日志位置来自源码核实」。</summary>
    public string Basis { get; init; } = string.Empty;

    public bool HasDirectory => Directory.Length > 0;
}
