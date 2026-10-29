// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yomori.Models;

/// <summary>一行日志经过投影之后的结果。</summary>
public sealed class ExternalLogProjection
{
    /// <summary>false = 整行丢弃。</summary>
    public bool Emit { get; private init; }

    /// <summary>true = 没有可读的投影文本，按原始行显示（fail-open）。</summary>
    public bool UseRaw { get; private init; }

    /// <summary>面板档文本（可能为空 —— 表示"面板档不显示这一行"）。</summary>
    public string PanelText { get; private init; } = string.Empty;

    /// <summary>节点档文本（归档即用它；可能为空 —— 表示只在面板档显示）。</summary>
    public string NodeText { get; private init; } = string.Empty;

    /// <summary>语义类型，界面按它上色。</summary>
    public ExternalLogKind Kind { get; private init; }

    /// <summary>丢弃一条（噪声、重复、中间层自检）。</summary>
    public static readonly ExternalLogProjection Drop = new() { Emit = false };

    /// <summary>原样放行：格式不认识、解析失败、非 MaaFramework 族。</summary>
    public static readonly ExternalLogProjection Pass = new() { Emit = true, UseRaw = true };

    internal static ExternalLogProjection New(string panel, string node, ExternalLogKind kind) => new()
    {
        Emit = true,
        PanelText = panel,
        NodeText = node,
        Kind = kind,
    };

    /// <summary>只有面板档显示（例如 focus 人话：归档已经有节点级事实，不必再记一遍）。</summary>
    internal static ExternalLogProjection PanelOnly(string text, ExternalLogKind kind) =>
        New(text, string.Empty, kind);

    /// <summary>只有节点档显示（例如中间层的 WRN：实时面板上没信息量，排障时要看）。</summary>
    internal static ExternalLogProjection NodeOnly(string text, ExternalLogKind kind) =>
        New(string.Empty, text, kind);
}

/// <summary>
/// MaaFramework 事件行里的 `[details={…}]`。
///
/// <para>必须**按 key 取**，不能"取第一个 name"：`PipelineNode.Succeeded` 的 payload 以
/// `action_details` 打头，首个 `"name"` 是动作名（`DoNothing`）而不是节点名
/// —— 实测 8,573 条里用"第一个 name"只命中 37 条。</para>
/// </summary>
public sealed class MaafwDetails
{
    private static readonly Regex EntryKey = new(
        "\"entry\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    private static readonly Regex NameKey = new(
        "\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    private static readonly Regex FocusPair = new(
        "\"focus\"\\s*:\\s*\\{\\s*(?:\"(?<k>(?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\")",
        RegexOptions.Compiled);

    private static readonly Regex FocusPlainPattern = new(
        "\"focus\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    /// <summary>任务 entry（`Tasker.Task.*`）。</summary>
    public string Entry { get; private set; } = string.Empty;

    /// <summary>节点名（`Node.PipelineNode.*`）。</summary>
    public string Node { get; private set; } = string.Empty;

    /// <summary>focus 直接是字符串时（少见）的内容。</summary>
    public string FocusPlain { get; private set; } = string.Empty;

    /// <summary>focus 是 `{事件名: 文本}` 时，取**键等于当前事件**的那一条。</summary>
    public string FocusFor(string eventName)
    {
        if (FocusPlain.Length > 0) { return FocusPlain; }
        if (_focusMap is null) { return string.Empty; }
        if (eventName.Length > 0 && _focusMap.TryGetValue(eventName, out var hit)) { return hit; }
        return _focusMap.TryGetValue("*", out var any) ? any : string.Empty;
    }

    private Dictionary<string, string>? _focusMap;

    /// <summary>解析失败一律返回空对象（调用方按"没有字段"处理，不抛）。</summary>
    public static MaafwDetails Parse(string rest)
    {
        var details = new MaafwDetails();
        var match = ExternalLogGrammar.DetailsStart.Match(rest);
        if (!match.Success) { return details; }

        var raw = match.Groups[1].Value.Trim().TrimEnd('\u2026');
        if (raw.Length == 0) { return details; }

        if (TryReadJson(details, raw)) { return details; }
        ReadByKey(details, raw);
        return details;
    }

    private static bool TryReadJson(MaafwDetails details, string raw)
    {
        foreach (var candidate in new[] { raw, Repair(raw) })
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { continue; }
                Fill(details, doc.RootElement);
                return true;
            }
            catch (JsonException)
            {
                // 换下一个候选
            }
        }

        return false;
    }

    private static void Fill(MaafwDetails details, JsonElement root)
    {
        details.Entry = GetString(root, "entry");
        details.Node = GetString(root, "name");
        if (!root.TryGetProperty("focus", out var focus)) { return; }

        if (focus.ValueKind == JsonValueKind.String)
        {
            details.FocusPlain = focus.GetString() ?? string.Empty;
            return;
        }

        if (focus.ValueKind != JsonValueKind.Object) { return; }
        foreach (var property in focus.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                details.Focus(property.Name, property.Value.GetString() ?? string.Empty);
            }
        }
    }

    private void Focus(string key, string text)
    {
        _focusMap ??= new Dictionary<string, string>(StringComparer.Ordinal);
        _focusMap[key] = text;
    }

    /// <summary>截断的 JSON：按 key 正则逐个抓。取到哪个算哪个，不追求结构完整。</summary>
    private static void ReadByKey(MaafwDetails details, string raw)
    {
        var entry = EntryKey.Match(raw);
        if (entry.Success) { details.Entry = Unescape(entry.Groups[1].Value); }

        var name = NameKey.Match(raw);
        if (name.Success) { details.Node = Unescape(name.Groups[1].Value); }

        var plain = FocusPlainPattern.Match(raw);
        if (plain.Success) { details.FocusPlain = Unescape(plain.Groups[1].Value); }

        foreach (Match pair in FocusPair.Matches(raw))
        {
            details.Focus(Unescape(pair.Groups["k"].Value), Unescape(pair.Groups["v"].Value));
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;

    private static string Unescape(string text) =>
        text.Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\n", " ", StringComparison.Ordinal)
            .Replace("\\t", " ", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);

    /// <summary>补上被截断时缺的引号与括号（只为让 `JsonDocument` 能读，不求语义正确）。</summary>
    internal static string Repair(string raw)
    {
        var buffer = new System.Text.StringBuilder(raw.Length + 8);
        var depth = 0;
        var inString = false;
        var escaped = false;
        foreach (var ch in raw)
        {
            buffer.Append(ch);
            if (inString)
            {
                if (escaped) { escaped = false; }
                else if (ch == '\\') { escaped = true; }
                else if (ch == '"') { inString = false; }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    if (depth == 0) { return buffer.ToString(); }
                    break;
            }
        }

        if (inString) { buffer.Append('"'); }
        var stack = new Stack<char>();
        foreach (var ch in buffer.ToString())
        {
            if (ch == '{') { stack.Push('}'); }
            else if (ch == '[') { stack.Push(']'); }
            else if ((ch == '}' || ch == ']') && stack.Count > 0) { stack.Pop(); }
        }

        while (stack.Count > 0) { buffer.Append(stack.Pop()); }
        return buffer.ToString();
    }
}

/// <summary>
/// 把 MaaFramework 日志投影成「在干什么」—— 默认视图（面板档）的直接数据源。
///
/// <para><b>为什么不是"过滤"</b>：有用的信息（任务名、节点名、项目作者写的提示）
/// 全在 `details` 的 JSON 里，整行丢掉就把信息一起丢了；整行保留就是现在这堆代码。
/// 正确做法是解析出字段、重写成一句话。</para>
///
/// <para><b>三条纪律</b>：① 永不抛异常（解析失败原样放行）；② 丢弃必须能解释（每条规则都能指向实测行）；
/// ③ 状态按 tail 实例持有（多开 / 多任务互不串台）。</para>
///
/// <para>无文件 IO、无 WinUI 依赖：整个类可以直接在冒烟工程里重放归档文件。</para>
/// </summary>
public sealed class ExternalLogProjector
{
    /// <summary>节点名在面板上的最小间隔。</summary>
    public const int NodeThrottleMs = 30_000;

    /// <summary>第一次心跳的等待时间。</summary>
    private const long HeartbeatEarlyMs = 60_000;

    /// <summary>3 分钟后放宽到 5 分钟。</summary>
    private const long HeartbeatLateMs = 300_000;

    /// <summary>30 分钟后放宽到 10 分钟。</summary>
    private const long HeartbeatIdleMs = 600_000;

    /// <summary>同一任务最多补几条心跳 —— 2 小时空档刷 120 条是噪声，不是进度。</summary>
    private const int MaxHeartbeats = 6;

    /// <summary>超过这个空档就单独说明一次。</summary>
    private const long GapMs = 300_000;

    /// <summary>同类告警每隔多少次再出一条。</summary>
    private const int WarnRepeatEvery = 100;

    private static readonly Regex HostLevel = new(@"\]\[(WRN|ERR)\]", RegexOptions.Compiled);

    private readonly List<string> _openTasks = new();
    private readonly Dictionary<string, long> _taskStartedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _warnCounts = new(StringComparer.Ordinal);

    private volatile LogLabelMap _labels;
    private bool? _maafw;

    private bool _hasFocus;
    private string _lastNode = string.Empty;
    private string _printedNode = string.Empty;
    private string _lastInternal = string.Empty;
    private string _lastNotice = string.Empty;
    private string _lastFailNode = string.Empty;
    private long _lastNodeAtMs = long.MinValue / 2;
    private long _lastFailAtMs = long.MinValue / 2;
    private long _lastOutMs = long.MinValue / 2;
    private long _lastOwnLineMs = -1;
    private long _runStartedAtMs;
    private long _pendingGapMs;
    private int _heartbeats;
    private bool _gapNoted;

    /// <param name="labels">项目资源里的 entry → 中文名映射；装载完成前用 <see cref="LogLabelMap.Empty"/>。</param>
    /// <param name="maafw">
    /// 是否为 MaaFramework 内核日志：<c>true</c> 强制开、<c>false</c> 强制关、
    /// <c>null</c>（默认）按前几十行自动判定。
    ///
    /// <para>自动判定是必需的：同一套采集代码要同时喂 MaaFW 系与三月七 / BetterGI / AUTO-MAS，
    /// 后者没有 `[msg=…]` 事件，投影规则一条都不适用 —— 必须原样放行，
    /// 否则会把整份日志的行全判成"噪声"丢光。</para>
    /// </param>
    public ExternalLogProjector(LogLabelMap? labels = null, bool? maafw = null)
    {
        _labels = labels ?? LogLabelMap.Empty;
        _maafw = maafw;
    }

    /// <summary>已经确定为 MaaFramework 内核日志（界面据此显示/隐藏"视图"选项）。</summary>
    public bool IsMaafw => _maafw == true;

    /// <summary>当前视图（面板 / 节点）用来决定回退行为。</summary>
    public string View { get; set; } = ExternalLogViews.Panel;

    /// <summary>当前任务的中文名（顶部状态条用）；没有正在跑的任务时为空。</summary>
    public string CurrentTaskLabel =>
        _openTasks.Count == 0 ? string.Empty : _labels.Resolve(_openTasks[^1]);

    /// <summary>本次运行已经进入过多少个节点（状态条用）。</summary>
    public int NodeCount { get; private set; }

    /// <summary>本次运行累计的"任务失败"次数（状态条用）。</summary>
    public int FailedTasks { get; private set; }

    /// <summary>标签表异步装好后整体替换（不可变，读线程无需加锁）。</summary>
    public void SetLabels(LogLabelMap labels) => _labels = labels ?? LogLabelMap.Empty;

    /// <summary>新一次运行开始：清空全部状态。</summary>
    public void Reset()
    {
        _openTasks.Clear();
        _taskStartedAt.Clear();
        _warnCounts.Clear();
        _hasFocus = false;
        _lastNode = string.Empty;
        _printedNode = string.Empty;
        _lastInternal = string.Empty;
        _lastNotice = string.Empty;
        _lastFailNode = string.Empty;
        _lastNodeAtMs = long.MinValue / 2;
        _lastFailAtMs = long.MinValue / 2;
        _lastOutMs = long.MinValue / 2;
        _lastOwnLineMs = -1;
        _runStartedAtMs = 0;
        _pendingGapMs = 0;
        _heartbeats = 0;
        _gapNoted = false;
        NodeCount = 0;
        FailedTasks = 0;
    }

    /// <summary>投影一行。**永不抛异常**；不认识的东西原样放行。</summary>
    public ExternalLogProjection Project(string? rawLine) => Project(rawLine, Environment.TickCount64);

    /// <summary>投影一行（可注入时钟，便于重放归档做断言）。</summary>
    public ExternalLogProjection Project(string? rawLine, long nowMs)
    {
        var raw = rawLine ?? string.Empty;
        if (raw.Length == 0) { return ExternalLogProjection.Drop; }

        if (_maafw is null && !Detect(raw)) { return ExternalLogProjection.Pass; }
        if (_maafw == false) { return ExternalLogProjection.Pass; }

        // 已经是投影后的样子就别再加工（幂等：主控端对老代理的原始行再投一次也安全）。
        if (ExternalLogGrammar.LooksProjected(raw)) { return ExternalLogProjection.Pass; }

        try
        {
            // 两种形状都必须认：
            //   ① 归档行：`[13:03:56] [警告] [2026-10-29 …][WARNING]…`（重放 / 归档时）
            //   ② 源文件行：`[2026-10-29 18:31:45.556][DBG][Px…][Logger] …`（**实时采集时就是这个**）
            //
            // 早期版本只认 ①，于是实时链路里 `[msg=…]` 永远匹配不上 → 全部原样放行，
            // 面板照旧刷一堆英文。重放与端到端用例当时喂的都是带前缀的归档行，
            // 所以测试全绿而线上不生效 —— 这是本次最贵的一个坑。
            var prefix = ExternalLogGrammar.ArchivePrefix.Match(raw);
            var rest = (prefix.Success ? prefix.Groups["rest"].Value : raw).Trim();
            if (rest.Length == 0) { return ExternalLogProjection.Drop; }

            var projection = ProjectCore(rest, nowMs, out var ownLine);
            if (ownLine)
            {
                // 空档在**更新基准之前**算：否则 Tick 永远看不到"刚才断了多久"。
                if (_lastOwnLineMs >= 0 && nowMs - _lastOwnLineMs >= GapMs && _openTasks.Count > 0)
                {
                    _pendingGapMs = nowMs - _lastOwnLineMs;
                }

                _lastOwnLineMs = nowMs;
                _gapNoted = false;
            }

            return projection;
        }
        catch (Exception)
        {
            // 投影是"锦上添花"：它坏掉时绝不能让整条实时日志停摆。
            return ExternalLogProjection.Pass;
        }
    }

    /// <summary>
    /// 一直探到第一条 MaaFW 痕迹为止，探不到就一直原样放行。
    ///
    /// <para>为什么**不能**"前 N 行没探到就判定不是"：实测 `bh3` 归档的头 40 行是
    /// MXU 外壳的启动日志（`[INFO][mxu_lib::commands::…]`），第 41 行才是内核事件。
    /// 早期版本用"40 行封顶"，结果整份 18 万行全被当成"不认识"原样放行 ——
    /// 面板一行都没减。</para>
    ///
    /// <para>代价可忽略：判不出来时每行只是几次 <c>Contains</c>，
    /// 而真正非 MaaFW 的日志（三月七 / BetterGI）本来就该原样放行。</para>
    /// </summary>
    private bool Detect(string raw)
    {
        if (raw.Contains("!!!OnEventNotify!!!", StringComparison.Ordinal) ||
            raw.Contains("[msg=", StringComparison.Ordinal) ||
            raw.Contains("MaaNS::", StringComparison.Ordinal) ||
            raw.Contains("][INF][Px", StringComparison.Ordinal))
        {
            _maafw = true;
            return true;
        }

        return false;
    }

    private ExternalLogProjection ProjectCore(string rest, long nowMs, out bool ownLine)
    {
        ownLine = false;

        // ① 事件行
        var eventMatch = ExternalLogGrammar.EventName.Match(rest);
        if (eventMatch.Success)
        {
            ownLine = true;
            return ProjectEvent(eventMatch.Groups[1].Value, rest, nowMs);
        }

        // ② 内核非事件行：ERR 留、WRN 只作失败原因、其余丢
        if (HostLevel.IsMatch(rest))
        {
            ownLine = true;
            return ProjectKernelNonEvent(rest);
        }

        // ③ 外壳行（MFW 的 python 侧）：只留警告与错误
        var shell = ExternalLogGrammar.ShellLine.Match(rest);
        if (shell.Success)
        {
            ownLine = true;
            return ProjectShell(shell);
        }

        // ④ 别的进程混进来的行（只有类名），以及其余中间层噪声
        if (ExternalLogGrammar.ForeignLine.IsMatch(rest)) { return ExternalLogProjection.Drop; }
        return ExternalLogProjection.Drop;
    }

    private ExternalLogProjection ProjectEvent(string eventName, string rest, long nowMs)
    {
        var details = MaafwDetails.Parse(rest);

        if (eventName == "Tasker.Task.Starting")
        {
            var entry = details.Entry;
            if (entry.Length == 0) { return ExternalLogProjection.Drop; }
            if (_openTasks.Contains(entry)) { return ExternalLogProjection.Drop; }   // 未结束就重发

            if (_openTasks.Count == 0)
            {
                _runStartedAtMs = nowMs;
                _heartbeats = 0;
                NodeCount = 0;
                FailedTasks = 0;
            }

            _openTasks.Add(entry);
            _taskStartedAt[entry] = nowMs;
            _hasFocus = false;
            _lastNode = string.Empty;
            _printedNode = string.Empty;
            _lastInternal = string.Empty;
            _lastNotice = string.Empty;
            _lastNodeAtMs = long.MinValue / 2;
            _gapNoted = false;

            Touch(nowMs);
            return ExternalLogProjection.New(
                $"▶ 任务开始：{_labels.Resolve(entry)}",
                $"▶ 任务 {entry}",
                ExternalLogKind.TaskStart);
        }

        if (eventName is "Tasker.Task.Succeeded" or "Tasker.Task.Failed")
        {
            var entry = details.Entry;
            if (entry.Length == 0 && _openTasks.Count > 0) { entry = _openTasks[^1]; }
            if (entry.Length == 0 || !_openTasks.Remove(entry)) { return ExternalLogProjection.Drop; }

            var ok = eventName.EndsWith("Succeeded", StringComparison.Ordinal);
            if (!ok) { FailedTasks++; }
            var elapsed = _taskStartedAt.TryGetValue(entry, out var startedAt) ? nowMs - startedAt : 0;
            _taskStartedAt.Remove(entry);

            var reason = ok || _lastInternal.Length == 0 ? string.Empty : $" · {_lastInternal}";
            _lastInternal = string.Empty;
            _hasFocus = false;
            _lastNotice = string.Empty;
            _printedNode = string.Empty;
            _lastFailNode = string.Empty;
            if (_openTasks.Count == 0) { _lastNode = string.Empty; }

            Touch(nowMs);
            var mark = ok ? "✔ 任务完成" : "✘ 任务失败";
            var label = _labels.Resolve(entry);
            return ExternalLogProjection.New(
                $"{mark}：{label}（{Duration(elapsed)}）{reason}",
                ok ? $"✓ 任务 {entry}（{Duration(elapsed)}）" : $"✗ 任务 {entry} 失败",
                ok ? ExternalLogKind.TaskDone : ExternalLogKind.TaskFail);
        }

        // focus：项目作者写的提示语，只有"键等于当前事件"时才显示
        var focusText = LogTextSanitizer.Clean(details.FocusFor(eventName), _labels);
        if (focusText.Length > 0)
        {
            _hasFocus = true;
            if (string.Equals(focusText, _lastNotice, StringComparison.Ordinal))
            {
                // 连续同一条提示（双线程各打一遍 / 同一节点反复触发）不再重复上屏
                return ExternalLogProjection.Drop;
            }

            _lastNotice = focusText;
            Touch(nowMs);
            return ExternalLogProjection.New(focusText, focusText, ExternalLogKind.Notice);
        }

        if (eventName == "Node.PipelineNode.Starting")
        {
            var name = details.Node;
            if (name.Length == 0) { return ExternalLogProjection.Drop; }
            NodeCount++;
            _lastNode = name;

            // 节点档永远记原名（排障要能对上 pipeline 里的 key）。
            var nodeText = $"  ▸ {name}";
            if (_hasFocus || ExternalLogGrammar.WaitLike.IsMatch(name) || name.StartsWith("__", StringComparison.Ordinal))
            {
                return ExternalLogProjection.New(string.Empty, nodeText, ExternalLogKind.Node);
            }

            var current = _openTasks.Count > 0 ? _openTasks[^1] : string.Empty;
            if (string.Equals(name, current, StringComparison.Ordinal) ||
                string.Equals(name, _printedNode, StringComparison.Ordinal) ||
                nowMs - _lastNodeAtMs < NodeThrottleMs)
            {
                return ExternalLogProjection.New(string.Empty, nodeText, ExternalLogKind.Node);
            }

            // 面板只显示"人话"节点名：有中文 desc 用 desc，名字本身是中文就直接用。
            // 两者都不满足的（`Stack.AutoCollectRouteValleyWukinddoTask1Assertion1` 这类
            // 动态注册节点）不上屏 —— 它占一整行只会把真正的中文提示挤掉。
            var display = NodeDisplay(name);
            if (display.Length == 0)
            {
                return ExternalLogProjection.New(string.Empty, nodeText, ExternalLogKind.Node);
            }

            _printedNode = name;
            _lastNodeAtMs = nowMs;
            Touch(nowMs);
            return ExternalLogProjection.New($"▸ {display}", nodeText, ExternalLogKind.Node);
        }

        if (eventName == "Node.PipelineNode.Failed")
        {
            var name = details.Node;
            if (name.Length == 0) { name = _lastNode; }
            if (name.Length == 0) { return ExternalLogProjection.Drop; }

            // 失败是少数且必须可见：查不到中文就退回原名，绝不因为"不好读"就丢掉。
            // 同一节点失败常被两个线程各打一遍，2 秒内同名字只出一次。
            if (string.Equals(name, _lastFailNode, StringComparison.Ordinal) && nowMs - _lastFailAtMs < 2_000)
            {
                return ExternalLogProjection.Drop;
            }

            _lastFailNode = name;
            _lastFailAtMs = nowMs;
            var display = NodeDisplay(name);
            if (display.Length == 0) { display = name; }

            Touch(nowMs);
            return ExternalLogProjection.New($"✗ 节点失败：{display}", $"  ✗ {name} 失败", ExternalLogKind.NodeFail);
        }

        // 识别失败 / 动作 / 下一节点列表：计数即可，逐条上屏没有信息量
        return ExternalLogProjection.Drop;
    }

    private ExternalLogProjection ProjectKernelNonEvent(string rest)
    {
        if (ExternalLogGrammar.Noise.IsMatch(rest)) { return ExternalLogProjection.Drop; }

        var line = ExternalLogGrammar.KernelLine.Match(rest);
        if (!line.Success) { return ExternalLogProjection.Drop; }

        var file = line.Groups["file"].Value;
        var level = line.Groups["lvl"].Value;
        var body = Shorten(line.Groups["body"].Value);

        if (ExternalLogGrammar.InternalFiles.Contains(file))
        {
            // 中间层自检：单独看没有意义，留作"任务失败"的原因
            if (level == "WRN" || level == "ERR") { _lastInternal = body; }
            return ExternalLogProjection.Drop;
        }

        if (ExternalLogGrammar.NoiseFiles.Contains(file)) { return ExternalLogProjection.Drop; }

        var folded = Fold($"⚠ {file}: {body}");
        if (folded.Length == 0) { return ExternalLogProjection.Drop; }

        return level == "WRN"
            // 面板档不收：这些是"这次没认出来"的正常流程；节点档保留（排障要看）
            ? ExternalLogProjection.NodeOnly(folded, ExternalLogKind.Warn)
            : ExternalLogProjection.New(folded, folded, ExternalLogKind.Warn);
    }

    private ExternalLogProjection ProjectShell(Match shell)
    {
        var level = shell.Groups["lvl"].Value;
        if (level is not ("WARNING" or "ERROR")) { return ExternalLogProjection.Drop; }
        var message = LogTextSanitizer.Clean(shell.Groups["msg"].Value, _labels);
        if (message.Length == 0) { return ExternalLogProjection.Drop; }
        var text = Fold(message);
        return text.Length == 0
            ? ExternalLogProjection.Drop
            : ExternalLogProjection.New(text, text, ExternalLogKind.Warn);
    }

    /// <summary>同类文本每满 100 次才再出一条，首条必出。</summary>
    private string Fold(string text)
    {
        var key = Regex.Replace(text, @"\d+", "N");
        if (key.Length > 60) { key = key.Substring(0, 60); }
        _warnCounts.TryGetValue(key, out var count);
        count++;
        _warnCounts[key] = count;
        return count == 1 || count % WarnRepeatEvery == 0
            ? (count == 1 ? text : $"{Trim(text, 110)}（同类第 {count} 次）")
            : string.Empty;
    }

    /// <summary>
    /// 定时检查：心脏跳一下，或者报告一次"日志中断"。
    ///
    /// <para>必须由采集循环按固定节拍调用 —— 没有新行的时候才更需要它，
    /// 否则长任务在面板上看起来就是卡死。</para>
    /// </summary>
    public ExternalLogProjection? Tick(long nowMs)
    {
        if (_maafw != true || _openTasks.Count == 0) { return null; }

        // ① 刚恢复：把"上一条与本条之间断了多久"补出来（重放 / 长任务都会遇到）
        if (_pendingGapMs > 0)
        {
            var broken = _pendingGapMs;
            _pendingGapMs = 0;
            _gapNoted = true;
            var note = $"⋯ 日志中断 {Duration(broken)}（脚本未写日志）";
            Touch(nowMs);
            return ExternalLogProjection.New(note, note, ExternalLogKind.Gap);
        }

        // ② 现场等待中：一直没有新行，超过阈值就报一次（此后保持安静，直到恢复）
        if (_lastOwnLineMs >= 0 && nowMs - _lastOwnLineMs >= GapMs)
        {
            if (_gapNoted) { return null; }
            _gapNoted = true;
            var gap = $"⋯ 日志中断 {Duration(nowMs - _lastOwnLineMs)}（脚本未写日志）";
            Touch(nowMs);
            return ExternalLogProjection.New(gap, gap, ExternalLogKind.Gap);
        }

        if (_gapNoted) { return null; }

        var age = nowMs - _runStartedAtMs;
        var limit = age < 180_000 ? HeartbeatEarlyMs : age < 1_800_000 ? HeartbeatLateMs : HeartbeatIdleMs;
        if (nowMs - _lastOutMs < limit || _heartbeats >= MaxHeartbeats) { return null; }

        _heartbeats++;
        var lastDisplay = NodeDisplay(_lastNode);
        var tail = lastDisplay.Length > 0 ? $"，最近：{lastDisplay}" : string.Empty;
        var text = $"⋯ 进行中（本次运行已 {Duration(age)}{tail}）";
        Touch(nowMs);
        return ExternalLogProjection.New(text, text, ExternalLogKind.Heartbeat);
    }

    private void Touch(long nowMs) => _lastOutMs = nowMs;

    /// <summary>
    /// 节点名的“面板显示形态”：有中文 `desc` 用 `desc`，名字本身是中文用它自己，
    /// 两者都不满足返回空串（调用方据此决定不上屏）。
    ///
    /// <para>为什么必须拦：MaaEnd 那类项目节点名是英文（`AncientTreeQuickTeleport`），
    /// agent 动态注册的更是 `Stack.AutoCollectRouteValleyWukinddoTask1Assertion1` 这种长 ID。
    /// 面板上每一行都是这种名字，等于把“在干什么”重新变回“一堆代码”。</para>
    /// </summary>
    private string NodeDisplay(string name)
    {
        var desc = _labels.ResolveNode(name);
        if (desc.Length > 0) { return desc; }
        return _labels.IsNodeReadable(name) ? name : string.Empty;
    }

    private static string Shorten(string body)
    {
        var text = body.Trim();
        text = Regex.Replace(text, @"^\S*::\S*", string.Empty).Trim();          // 去 MaaNS::A::b
        text = Regex.Replace(text, @"(\s*\[[^\]]*=\S*\])+\s*$", string.Empty);   // 去尾部参数
        if (text.Length == 0) { text = body.Trim(); }
        return Trim(text, 150);
    }

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "\u2026";

    /// <summary>毫秒 → `42s` / `1m01s` / `2h04m`。</summary>
    public static string Duration(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds / 1000);
        if (seconds >= 3600)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}h{seconds % 3600 / 60:00}m");
        }

        if (seconds >= 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m{seconds % 60:00}s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seconds}s");
    }
}
