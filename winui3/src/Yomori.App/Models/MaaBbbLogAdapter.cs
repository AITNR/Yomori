// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>
/// MAA_bbb（识宝小助手，<c>projects[id=maabbb]</c>，MFW-PyQt6 前端）的日志适配器 ——
/// 第四族「MFW Python 侧混合单行族」，采集目标是预设默认的 <c>debug/gui.log</c>。
///
/// <para>这份日志一条一行，自带完整日期、毫秒、大写级别、源文件、行号与函数名；
/// 可读的业务正文集中在 <c>logoutput_widget.py</c>，正文再套一层 <c>[INFO] </c> /
/// <c>[WARNING] </c> / <c>[ERROR] </c> 前缀，本身已是中文短句。约 85% 的行是
/// 启动、配置、回调、GPU、更新与监控期的框架噪声；任务结束时 <c>notice.py</c> 会把整段
/// <c>[HH:mm:ss][信息]…</c> 通知正文一次性写回文件，与前面的业务行逐条重复。</para>
///
/// <para>本适配器只做排版规整与去噪，不做语义翻译，也不需要 <c>LogLabelMap</c>：
/// 中文任务名与提示本就在正文里。通知块按「面板不上屏、原文逐字进归档」处理，
/// 用 <c>CompactLine("", 原文, …)</c> 而不是 <c>Drop</c> —— 已适配分支的
/// <c>Drop</c> 会被按 Warn/Error 级别强制原样放行。</para>
///
/// <para>状态全部按实例持有；解析永不抛异常；不认识的行 <c>Pass</c> 原样放行；
/// <c>NodeText</c> 一律传原始行，归档因此逐字等于脚本自身日志。
/// 绑定适配器后采集侧不再调用投影器的 <c>Tick</c>，心跳与「日志中断」由本类自补。</para>
/// </summary>
public sealed class MaaBbbLogAdapter : IExternalLogAdapter
{
    /// <summary>第一次心跳的等待时间，随后按运行时长放宽。</summary>
    private const long HeartbeatEarlyMs = 60_000;
    private const long HeartbeatLateMs = 300_000;
    private const long HeartbeatIdleMs = 600_000;

    /// <summary>同一次运行最多补几条心跳。</summary>
    private const int MaxHeartbeats = 6;

    /// <summary>超过这个空档就单独说明一次「日志中断」。</summary>
    private const long GapMs = 300_000;

    /// <summary>业务行来自这个模块；其它模块一律按框架噪声处理（Warn/Error 必出除外）。</summary>
    private const string BusinessModule = "logoutput_widget.py";

    private readonly Queue<string> _inputs = new();
    private readonly Queue<ExternalLogProjection> _outputs = new();

    private string _lastHms = string.Empty;
    private string _lastBody = string.Empty;
    private bool _hasLastBody;
    private string _lastTask = string.Empty;

    private bool _runOpen;
    private long _runStartedAtMs;
    private long _lastOwnLineMs = -1;
    private long _lastOutMs = long.MinValue / 2;
    private long _pendingGapMs;
    private int _heartbeats;
    private bool _gapNoted;

    /// <inheritdoc />
    public string Id => ExternalLogAdapterRegistry.MaaBbbId;

    /// <inheritdoc />
    public ExternalLogProjection Project(string rawLine, long nowMs)
    {
        var raw = rawLine ?? string.Empty;
        if (raw.Trim().Length == 0) { return ExternalLogProjection.Drop; }

        // 幂等：主控端可能对老代理发来的行再投一次。
        if (MaaBbbGrammar.LooksCompact(raw)) { return ExternalLogProjection.Pass; }

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
        _lastHms = string.Empty;
        _lastBody = string.Empty;
        _hasLastBody = false;
        _lastTask = string.Empty;
        _runOpen = false;
        _runStartedAtMs = 0;
        _lastOwnLineMs = -1;
        _lastOutMs = long.MinValue / 2;
        _pendingGapMs = 0;
        _heartbeats = 0;
        _gapNoted = false;
    }

    private void Drain(long nowMs)
    {
        // 上限 3 条：一行最多产出 1 条，留一点余量给心跳，又不会把输入队列堆起来。
        while (_outputs.Count < 3 && _inputs.Count > 0)
        {
            var line = _inputs.Dequeue();
            var produced = _outputs.Count;
            try
            {
                ProcessLine(line, nowMs);
            }
            catch (Exception)
            {
                // 解析坏掉绝不吞行：原样放行交给粗略链路。
                if (_outputs.Count == produced) { _outputs.Enqueue(ExternalLogProjection.Pass); }
            }
        }
    }

    private void ProcessLine(string raw, long nowMs)
    {
        // 框架噪声虽然不上屏，仍证明脚本在写日志 —— 活跃基准按收到的原始行更新。
        NoteOwnLine(nowMs);
        var text = raw.TrimEnd();

        // 任务完成通知的正文块：面板不上屏，原文逐字进归档（node 视图与排障可回看）。
        var notice = MaaBbbGrammar.NoticeBlockLine.Match(text);
        if (notice.Success)
        {
            var hms = notice.Groups["hms"].Value;
            _lastHms = hms;
            Emit(string.Empty, raw, ExternalLogKind.Plain, hms,
                MaaBbbGrammar.NoticeLevelOf(notice.Groups["lvl"].Value), nowMs);
            return;
        }

        var py = MaaBbbGrammar.PyLine.Match(text);
        if (!py.Success)
        {
            // 真不认识的行：fail-open，交回粗略链路。
            _outputs.Enqueue(ExternalLogProjection.Pass);
            return;
        }

        var file = py.Groups["file"].Value;
        var hms2 = py.Groups["hms"].Value;
        var level = MaaBbbGrammar.LevelOf(py.Groups["lvl"].Value);
        var body = py.Groups["body"].Value.Trim();
        _lastHms = hms2;
        if (body.Length == 0) { return; }

        if (string.Equals(file, BusinessModule, StringComparison.OrdinalIgnoreCase))
        {
            ProcessBusiness(raw, body, level, hms2, nowMs);
            return;
        }

        // 其它模块：框架噪声一律丢；WARN / ERROR 必出（分派层也会按级别强制放行）。
        if (level is ExternalLogLevel.Warn or ExternalLogLevel.Error)
        {
            var isError = level == ExternalLogLevel.Error;
            var prefix = isError ? "✗ " : "⚠ ";
            Emit(prefix + file + ": " + MaaBbbGrammar.CompressPaths(body), raw,
                isError ? ExternalLogKind.TaskFail : ExternalLogKind.Warn, hms2, level, nowMs);
        }
    }

    /// <summary><c>logoutput_widget.py</c> 正文的全部清洗与降噪规则（计划书 §5.3）。</summary>
    private void ProcessBusiness(string raw, string body, ExternalLogLevel pyLevel,
        string hms, long nowMs)
    {
        if (MaaBbbGrammar.LogImageFrame.IsMatch(body) ||
            MaaBbbGrammar.PureSeparator.IsMatch(body))
        {
            return;
        }

        var biz = MaaBbbGrammar.BusinessPrefix.Match(body);
        // 正文里的级别优先：实测 py 行级别恒 INFO，而正文写 [WARNING] / [ERROR]。
        var level = biz.Success ? MaaBbbGrammar.LevelOf(biz.Groups["lvl"].Value) : pyLevel;
        var text = (biz.Success ? biz.Groups["body"].Value : body).Trim();
        if (text.Length == 0) { return; }

        if (!_runOpen)
        {
            _runOpen = true;
            _runStartedAtMs = nowMs;
            _heartbeats = 0;
            _pendingGapMs = 0;
        }

        var clean = MaaBbbGrammar.CompressPaths(text);

        // 任务边界：每条都出，不参与去重与节流。
        var task = MaaBbbGrammar.TaskStart.Match(clean);
        if (task.Success)
        {
            _lastTask = task.Groups["name"].Value.Trim();
            Emit("▸ 执行任务：" + _lastTask, raw, ExternalLogKind.Node, hms, level, nowMs);
            RememberBody(clean);
            return;
        }

        if (MaaBbbGrammar.AllDone.IsMatch(clean))
        {
            Emit("✔ 所有任务都已完成", raw, ExternalLogKind.TaskDone, hms, level, nowMs);
            RememberBody(clean);
            CloseRun();
            return;
        }

        if (MaaBbbGrammar.Stopping.IsMatch(clean))
        {
            Emit("■ 停止任务中", raw, ExternalLogKind.TaskStart, hms, level, nowMs);
            RememberBody(clean);
            CloseRun();
            return;
        }

        // 错误 / 告警必出：不参与去重与节流。
        if (level == ExternalLogLevel.Error)
        {
            Emit("✗ " + clean, raw, ExternalLogKind.TaskFail, hms, ExternalLogLevel.Error, nowMs);
            RememberBody(clean);
            return;
        }

        if (level == ExternalLogLevel.Warn)
        {
            Emit("⚠ " + clean, raw, ExternalLogKind.Warn, hms, ExternalLogLevel.Warn, nowMs);
            RememberBody(clean);
            return;
        }

        // 紧邻重复去重：实测「设备连接成功」等会连续打印两遍；
        // 不做跨轮次的强节流，避免丢掉不同任务轮次里的同名提示。
        if (_hasLastBody && string.Equals(_lastBody, clean, StringComparison.Ordinal)) { return; }
        Emit("  " + clean, raw, ExternalLogKind.Notice, hms, level, nowMs);
        RememberBody(clean);
    }

    private void RememberBody(string clean)
    {
        _lastBody = clean;
        _hasLastBody = true;
    }

    private void CloseRun()
    {
        _runOpen = false;
        _heartbeats = 0;
        _pendingGapMs = 0;
        _gapNoted = false;
    }

    private void EmitHeartbeatIfDue(long nowMs)
    {
        if (!_runOpen) { return; }

        if (_pendingGapMs > 0)
        {
            var broken = _pendingGapMs;
            _pendingGapMs = 0;
            _gapNoted = true;
            EmitHeartbeat("⋯ 日志中断 " + ExternalLogProjector.Duration(broken) + "（脚本未写日志）",
                ExternalLogKind.Gap, nowMs);
            return;
        }

        if (_lastOwnLineMs >= 0 && nowMs - _lastOwnLineMs >= GapMs)
        {
            if (_gapNoted) { return; }
            _gapNoted = true;
            EmitHeartbeat("⋯ 日志中断 " + ExternalLogProjector.Duration(nowMs - _lastOwnLineMs) +
                "（脚本未写日志）", ExternalLogKind.Gap, nowMs);
            return;
        }

        if (_gapNoted) { return; }

        var age = nowMs - _runStartedAtMs;
        var limit = age < 180_000 ? HeartbeatEarlyMs : age < 1_800_000 ? HeartbeatLateMs : HeartbeatIdleMs;
        if (nowMs - _lastOutMs < limit || _heartbeats >= MaxHeartbeats) { return; }

        _heartbeats++;
        var tail = _lastTask.Length > 0 ? "，最近：" + _lastTask : string.Empty;
        EmitHeartbeat("⋯ 进行中（本次运行已 " + ExternalLogProjector.Duration(age) + tail + "）",
            ExternalLogKind.Heartbeat, nowMs);
    }

    private void EmitHeartbeat(string text, ExternalLogKind kind, long nowMs) =>
        Emit(text, text, kind, _lastHms, ExternalLogLevel.Info, nowMs);

    private void NoteOwnLine(long nowMs)
    {
        // 空档在更新基准之前算：否则 Tick 永远看不到「刚才断了多久」。
        if (_lastOwnLineMs >= 0 && nowMs - _lastOwnLineMs >= GapMs && _runOpen)
        {
            _pendingGapMs = nowMs - _lastOwnLineMs;
        }

        _lastOwnLineMs = nowMs;
        _gapNoted = false;
    }

    private void Emit(string panel, string node, ExternalLogKind kind, string sourceTime,
        ExternalLogLevel level, long nowMs)
    {
        _lastOutMs = nowMs;
        _outputs.Enqueue(ExternalLogProjection.CompactLine(panel, node, kind, sourceTime, level));
    }
}
