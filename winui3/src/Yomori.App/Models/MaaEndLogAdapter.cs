// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Yomori.Models;

/// <summary>
/// MaaEnd（MaaFramework 5.x 内核日志）专属深度适配器（`maaend` 族）。
///
/// <para>与 March7th / BetterGI 都不同：一条日志自带时间戳、级别、源文件、函数与正文，
/// 但可读语义不在正文里，而在 <c>[msg=…]</c> 事件与 <c>[details={…}]</c> 的
/// <c>entry</c> / <c>name</c> / <c>focus</c>，且任务名与节点名必须借
/// <see cref="LogLabelMap"/> 才能翻成中文。所以它走单行族骨架，但复用
/// <see cref="MaafwDetails"/> 与 <see cref="ExternalLogGrammar"/> 的解析层。</para>
///
/// <para>相比通用 <see cref="ExternalLogProjector"/> 的三点差异：① 产出
/// <c>Compact=true</c> 的单行形态；② 不再为 45+ 个 MaaFW 项目做保守取舍（可读节点
/// 全部可出、告警保留原文进归档）；③ focus 是单键对象且键不等于当前事件时也能取到。</para>
///
/// <para>状态按实例持有；解析永不抛异常；不认识的行 <c>Pass</c> 原样放行；
/// <c>NodeText</c> 一律传原始行，归档因此逐字等于脚本自身日志。</para>
/// </summary>
public sealed class MaaEndLogAdapter : IExternalLogAdapter, IExternalLogLabelAware
{
    /// <summary>可读节点名的最小间隔（与 <c>ExternalLogProjector.NodeThrottleMs</c> 同量级）。</summary>
    private const long NodeThrottleMs = 30_000;

    /// <summary>同类 WRN 每隔多少条再出一条（与通用投影器一致）。</summary>
    private const int WarnRepeatEvery = 100;

    /// <summary>第一次心跳的等待时间，随后按运行时长放宽。</summary>
    private const long HeartbeatEarlyMs = 60_000;
    private const long HeartbeatLateMs = 300_000;
    private const long HeartbeatIdleMs = 600_000;

    /// <summary>同一任务最多补几条心跳。</summary>
    private const int MaxHeartbeats = 6;

    /// <summary>超过这个空档就单独说明一次"日志中断"。</summary>
    private const long GapMs = 300_000;

    /// <summary>同一节点失败常被两个线程各打一遍，2 秒内同名只出一次。</summary>
    private const long FailDuplicateWindowMs = 2_000;

    private readonly Queue<string> _inputs = new();
    private readonly Queue<ExternalLogProjection> _outputs = new();

    /// <summary>
    /// 由 <see cref="SetLabels"/> 注入的项目资源映射（异步装载，装好之前用空表）。
    /// 不可变引用，读线程无需加锁。
    /// </summary>
    private LogLabelMap _labels = LogLabelMap.Empty;

    private readonly List<string> _openTasks = new();
    private readonly Dictionary<string, long> _taskStartedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _warnCounts = new(StringComparer.Ordinal);

    private string _lastHms = string.Empty;
    private string _lastInternal = string.Empty;
    private string _lastNotice = string.Empty;
    private string _lastNode = string.Empty;
    private string _printedNode = string.Empty;
    private string _lastFailNode = string.Empty;
    private long _lastFailAtMs = long.MinValue / 2;
    private long _lastNodeAtMs = long.MinValue / 2;
    private long _lastOwnLineMs = -1;
    private long _lastOutMs = long.MinValue / 2;
    private long _runStartedAtMs;
    private long _pendingGapMs;
    private int _heartbeats;
    private bool _gapNoted;

    /// <inheritdoc />
    public string Id => ExternalLogAdapterRegistry.MaaEndId;

    /// <inheritdoc />
    public void SetLabels(LogLabelMap labels) => _labels = labels ?? LogLabelMap.Empty;

    /// <inheritdoc />
    public ExternalLogProjection Project(string rawLine, long nowMs)
    {
        var raw = rawLine ?? string.Empty;
        if (raw.Trim().Length == 0) { return ExternalLogProjection.Drop; }

        // 幂等：主控端可能对老代理发来的行再投一次。
        if (MaaEndGrammar.LooksCompact(raw)) { return ExternalLogProjection.Pass; }

        _inputs.Enqueue(raw);
        Drain(nowMs);
        return _outputs.Count > 0 ? _outputs.Dequeue() : ExternalLogProjection.Drop;
    }

    /// <inheritdoc />
    public ExternalLogProjection? Tick(long nowMs)
    {
        EmitHeartbeatIfDue(nowMs);
        if (_outputs.Count == 0 && _inputs.Count > 0) { Drain(nowMs); }
        return _outputs.Count > 0 ? _outputs.Dequeue() : null;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _inputs.Clear();
        _outputs.Clear();
        _openTasks.Clear();
        _taskStartedAt.Clear();
        _warnCounts.Clear();
        _lastHms = string.Empty;
        _lastInternal = string.Empty;
        _lastNotice = string.Empty;
        _lastNode = string.Empty;
        _printedNode = string.Empty;
        _lastFailNode = string.Empty;
        _lastFailAtMs = long.MinValue / 2;
        _lastNodeAtMs = long.MinValue / 2;
        _lastOwnLineMs = -1;
        _lastOutMs = long.MinValue / 2;
        _runStartedAtMs = 0;
        _pendingGapMs = 0;
        _heartbeats = 0;
        _gapNoted = false;
        // _labels 刻意不清：它是 SetLabels 注入的项目配置，不是会话状态。
        // 清空会让 Reset 之后的中文任务名永久退化成英文 entry，且不会再注入一次。
    }

    private void Drain(long nowMs)
    {
        while (_outputs.Count < 3 && _inputs.Count > 0)
        {
            var line = _inputs.Dequeue();
            var produced = _outputs.Count;
            try { ProcessLine(line, nowMs); }
            catch (Exception)
            {
                // 解析坏掉绝不吞行：原样放行交给粗略链路。
                if (_outputs.Count == produced) { _outputs.Enqueue(ExternalLogProjection.Pass); }
            }
        }
    }

    private void ProcessLine(string raw, long nowMs)
    {
        var text = raw.TrimEnd();
        var kernel = MaaEndGrammar.KernelLine.Match(text);
        if (!kernel.Success)
        {
            _outputs.Enqueue(ExternalLogProjection.Pass);
            return;
        }

        var hms = kernel.Groups["hms"].Value;
        var lvl = kernel.Groups["lvl"].Value;
        var file = kernel.Groups["file"].Value;
        var rest = kernel.Groups["rest"].Value.Trim();
        var level = MaaEndGrammar.LevelOf(lvl);
        _lastHms = hms;

        // ① 内核事件行：语义全在 [msg=] 与 [details={…}] 里。
        var ev = ExternalLogGrammar.EventName.Match(rest);
        if (rest.IndexOf("!!!OnEventNotify!!!", StringComparison.Ordinal) >= 0 && ev.Success)
        {
            NoteOwnLine(nowMs);
            ProjectEvent(raw, ev.Groups[1].Value, rest, hms, level, nowMs);
            return;
        }

        // ② AgentClient 的 [message=] 转发行与 TRC/DBG 函数级噪声整类不上屏。
        //    关键字命中由分派层无条件强制放行，不会丢用户配置的关键字。
        if (MaaEndGrammar.AgentForward.IsMatch(rest) || lvl is "TRC" or "DBG") { return; }

        // ③ 内核 WRN / ERR：必出。被折叠的同类行只进归档、不上面板，
        //    但绝不返回 Drop —— 已适配分支会按 Warn/Error 级别把 Drop 强制原样放行。
        if (lvl is "WRN" or "ERR")
        {
            NoteOwnLine(nowMs);
            ProjectKernelNonEvent(raw, rest, file, lvl, hms, level, nowMs);
            return;
        }

        // ④ 其余 INF 非事件行（reco / action / node done / 启动横幅）：整类丢弃。
        //    这些是 INF 级，不会触发分派层的级别强制放行。
    }

    private void ProjectEvent(string raw, string eventName, string rest, string hms,
        ExternalLogLevel level, long nowMs)
    {
        var details = MaafwDetails.Parse(rest);

        if (eventName == "Tasker.Task.Starting")
        {
            var entry = details.Entry;
            if (entry.Length == 0 || _openTasks.Contains(entry)) { return; }

            if (_openTasks.Count == 0)
            {
                _runStartedAtMs = nowMs;
                _heartbeats = 0;
                _pendingGapMs = 0;
                _gapNoted = false;
            }

            _openTasks.Add(entry);
            _taskStartedAt[entry] = nowMs;
            _lastInternal = string.Empty;
            _lastNotice = string.Empty;
            _lastNode = string.Empty;
            _printedNode = string.Empty;
            _lastNodeAtMs = long.MinValue / 2;

            Emit("▶ 任务开始：" + Label(entry), raw, ExternalLogKind.TaskStart, hms, level, nowMs);
            return;
        }

        if (eventName is "Tasker.Task.Succeeded" or "Tasker.Task.Failed")
        {
            var entry = details.Entry;
            if (entry.Length == 0 && _openTasks.Count > 0) { entry = _openTasks[^1]; }
            if (entry.Length == 0 || !_openTasks.Remove(entry)) { return; }

            var ok = eventName.EndsWith("Succeeded", StringComparison.Ordinal);
            var elapsed = _taskStartedAt.TryGetValue(entry, out var startedAt) ? nowMs - startedAt : 0;
            _taskStartedAt.Remove(entry);

            var reason = ok || _lastInternal.Length == 0 ? string.Empty : " · " + _lastInternal;
            var cost = "（" + ExternalLogProjector.Duration(elapsed) + "）";
            var label = Label(entry);
            _lastInternal = string.Empty;
            _lastNotice = string.Empty;
            _printedNode = string.Empty;
            if (_openTasks.Count == 0) { _lastNode = string.Empty; }

            Emit(ok ? "✔ 任务完成：" + label + cost
                    : "✘ 任务失败：" + label + cost + reason,
                raw, ok ? ExternalLogKind.TaskDone : ExternalLogKind.TaskFail,
                hms, ok ? level : ExternalLogLevel.Error, nowMs);
            return;
        }

        // focus 人话：先按当前事件名精确匹配；落空时若 focus 是单键对象也取它
        // （MaaEnd 实测 [msg=Node.Recognition.Starting] 配 {"focus":{"Node.Recognition.Succeeded":"…"}}）。
        var focusText = Clean(details.FocusFor(eventName));
        if (focusText.Length == 0) { focusText = Clean(details.SingleFocus()); }
        if (focusText.Length == 0) { focusText = Clean(details.FocusPlain); }
        if (focusText.Length > 0)
        {
            if (string.Equals(focusText, _lastNotice, StringComparison.Ordinal)) { return; }
            _lastNotice = focusText;
            Emit("  " + focusText, raw, ExternalLogKind.Notice, hms, level, nowMs);
            return;
        }

        if (eventName == "Node.PipelineNode.Failed")
        {
            var name = details.Node;
            if (name.Length == 0) { name = _lastNode; }
            if (name.Length == 0) { return; }

            if (string.Equals(name, _lastFailNode, StringComparison.Ordinal) &&
                nowMs - _lastFailAtMs < FailDuplicateWindowMs) { return; }
            _lastFailNode = name;
            _lastFailAtMs = nowMs;

            // 失败是少数且必须可见：查不到中文就退回原名，绝不因为"不好读"丢掉。
            var display = NodeDisplay(name);
            if (display.Length == 0) { display = name; }
            Emit("✗ 节点失败：" + display, raw, ExternalLogKind.NodeFail, hms,
                ExternalLogLevel.Error, nowMs);
            return;
        }

        if (eventName == "Node.PipelineNode.Starting")
        {
            var name = details.Node;
            if (name.Length == 0) { return; }
            _lastNode = name;

            var display = NodeDisplay(name);
            if (display.Length == 0) { return; }   // 动态注册的英文长 ID 不上屏
            if (string.Equals(name, _printedNode, StringComparison.Ordinal) ||
                nowMs - _lastNodeAtMs < NodeThrottleMs) { return; }

            _printedNode = name;
            _lastNodeAtMs = nowMs;
            Emit("▸ " + display, raw, ExternalLogKind.Node, hms, level, nowMs);
            return;
        }

        // 其余 Node.* / Controller.* 事件：逐条上屏没有信息量，计数即可。
    }

    private void ProjectKernelNonEvent(string raw, string rest, string file, string lvl,
        string hms, ExternalLogLevel level, long nowMs)
    {
        var body = Shorten(rest);

        // 不套 ExternalLogGrammar.Noise：那张通用噪声表里含 "Wrong ocr_result size" 等
        // MaaEnd 真实 WRN 正文，照搬会让这类告警一条都不上面板。通用投影器走未适配分支
        // （Drop 即丢）可以这么做；专属适配器按计划书要求「WRN 必出」，只按文件分类折叠。

        if (ExternalLogGrammar.InternalFiles.Contains(file))
        {
            // 中间层自检单独看没意义，留作"任务失败"的原因。
            if (lvl is "WRN" or "ERR") { _lastInternal = body; }
            EmitArchivedOnly(raw, hms, level, nowMs);
            return;
        }

        if (ExternalLogGrammar.NoiseFiles.Contains(file)) { EmitArchivedOnly(raw, hms, level, nowMs); return; }

        if (lvl == "ERR")
        {
            Emit("✗ " + file + ": " + body, raw, ExternalLogKind.TaskFail, hms,
                ExternalLogLevel.Error, nowMs);
            return;
        }

        // WRN：同类按归一 key 计数，首条与每满 100 条出面板行，其余只进归档。
        var key = Regex.Replace("⚠ " + file + ": " + body, @"\d+", "N");
        if (key.Length > 60) { key = key.Substring(0, 60); }
        _warnCounts.TryGetValue(key, out var count);
        count++;
        _warnCounts[key] = count;
        if (count > 1 && count % WarnRepeatEvery != 0)
        {
            EmitArchivedOnly(raw, hms, level, nowMs);
            return;
        }

        var panel = count == 1
            ? "⚠ " + file + ": " + body
            : "⚠ " + file + ": " + body + "（同类第 " + count.ToString() + " 次）";
        Emit(panel, raw, ExternalLogKind.Warn, hms, ExternalLogLevel.Warn, nowMs);
    }

    /// <summary>
    /// 只进归档、不上面板：面板文本为空时上屏侧会跳过，但 <c>NodeText</c> 让原文进归档。
    /// 这也是 WRN 折叠不返回 Drop 的原因（Drop 会被分派层按 Warn 级别强制原样放行）。
    /// </summary>
    private void EmitArchivedOnly(string raw, string hms, ExternalLogLevel level, long nowMs) =>
        Emit(string.Empty, raw, ExternalLogKind.Plain, hms, level, nowMs);

    private void EmitHeartbeatIfDue(long nowMs)
    {
        if (_openTasks.Count == 0) { return; }

        if (_pendingGapMs > 0)
        {
            var broken = _pendingGapMs;
            _pendingGapMs = 0;
            _gapNoted = true;
            var note = "⋯ 日志中断 " + ExternalLogProjector.Duration(broken) + "（脚本未写日志）";
            EmitHeartbeat(note, ExternalLogKind.Gap, nowMs);
            return;
        }

        if (_lastOwnLineMs >= 0 && nowMs - _lastOwnLineMs >= GapMs)
        {
            if (_gapNoted) { return; }
            _gapNoted = true;
            var note = "⋯ 日志中断 " + ExternalLogProjector.Duration(nowMs - _lastOwnLineMs) + "（脚本未写日志）";
            EmitHeartbeat(note, ExternalLogKind.Gap, nowMs);
            return;
        }

        if (_gapNoted) { return; }

        var age = nowMs - _runStartedAtMs;
        var limit = age < 180_000 ? HeartbeatEarlyMs : age < 1_800_000 ? HeartbeatLateMs : HeartbeatIdleMs;
        if (nowMs - _lastOutMs < limit || _heartbeats >= MaxHeartbeats) { return; }

        _heartbeats++;
        var display = NodeDisplay(_lastNode);
        var tail = display.Length > 0 ? "，最近：" + display : string.Empty;
        var note2 = "⋯ 进行中（本次运行已 " + ExternalLogProjector.Duration(age) + tail + "）";
        EmitHeartbeat(note2, ExternalLogKind.Heartbeat, nowMs);
    }

    private void EmitHeartbeat(string text, ExternalLogKind kind, long nowMs) =>
        Emit(text, text, kind, _lastHms, ExternalLogLevel.Info, nowMs);

    private void NoteOwnLine(long nowMs)
    {
        // 空档在更新基准之前算：否则 Tick 永远看不到"刚才断了多久"。
        if (_lastOwnLineMs >= 0 && nowMs - _lastOwnLineMs >= GapMs && _openTasks.Count > 0)
        {
            _pendingGapMs = nowMs - _lastOwnLineMs;
        }

        _lastOwnLineMs = nowMs;
        _gapNoted = false;
    }

    private void Emit(string panel, string node, ExternalLogKind kind, string sourceTime,
        ExternalLogLevel? level, long nowMs)
    {
        _lastOutMs = nowMs;
        _outputs.Enqueue(ExternalLogProjection.CompactLine(panel, node, kind, sourceTime, level));
    }

    private string Label(string entry) => _labels.Resolve(entry);

    private string Clean(string? text) => LogTextSanitizer.Clean(text, _labels);

    private string NodeDisplay(string name)
    {
        var desc = _labels.ResolveNode(name);
        if (desc.Length > 0) { return desc; }
        return _labels.IsNodeReadable(name) ? name : string.Empty;
    }

    /// <summary>剥函数名前缀与尾部参数、压路径、截断到 150 字符。</summary>
    private static string Shorten(string body)
    {
        var text = body.Trim();
        text = Regex.Replace(text, @"^\S*::\S*", string.Empty).Trim();
        text = Regex.Replace(text, @"(\s*\[[^\]]*=\S*\])+\s*$", string.Empty);
        if (text.Length == 0) { text = body.Trim(); }
        text = MaaEndGrammar.CompressPaths(text);
        return text.Length <= 150 ? text : text.Substring(0, 150) + "\u2026";
    }
}
