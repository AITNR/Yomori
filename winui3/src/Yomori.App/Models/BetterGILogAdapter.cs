// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Yomori.Models;

/// <summary>
/// BetterGI（更好的原神）日志的逐脚本深度适配器。
///
/// <para>这个脚本的日志是 Serilog 风格的**三行一组**：头行只有
/// `[HH:mm:ss.fff] [LVL] [Primary:S…:P…:T…] BetterGenshinImpact.&lt;模块&gt;`，
/// 中文正文在下一行，空行收尾。而采集端 `ExternalLogTail.AddLine` 会把空行
/// 在进适配器之前丢掉，所以适配器看到的流是「头行 / 正文 / 头行 / 正文 …」，
/// 必须用**头行做组边界**做跨行合成，不能按单行解析。</para>
///
/// <para>状态全部按实例持有：多个通道同时跟同一份 BetterGI 日志时，pending 头、
/// 组续行与折叠计数不能互相污染。规则表与实测统计见
/// 《外部脚本实时日志深度适配计划书-BetterGI.md》§3.3 / §3.4 / §3.5。</para>
/// </summary>
public sealed class BetterGILogAdapter : IExternalLogAdapter
{
    /// <summary>头行等到正文的上限；超时把头行原文放行，绝不静默丢行。</summary>
    private const long HeaderWindowMs = 2_000;

    /// <summary>高频过程行的折叠窗口（与 March7th / MaaFW 同量级）。</summary>
    private const long ThrottleWindowMs = 30_000;

    /// <summary>完全相同正文的去重窗口。</summary>
    private const long DuplicateWindowMs = 2_000;

    /// <summary>等待处理的原始行。用队列而不是「暂存一行」：一次调用可能产出多条输出。</summary>
    private readonly Queue<string> _inputs = new();

    /// <summary>已经算好、还没交出去的投影。</summary>
    private readonly Queue<ExternalLogProjection> _outputs = new();

    // ---- 刚读到、还没等到正文的头行 ----
    private string _pendingHms = string.Empty;
    private string _pendingLvl = string.Empty;
    private string _pendingRaw = string.Empty;
    private long _pendingAtMs;
    private bool _hasPending;

    // ---- 当前组（主行已出，可能还有堆栈续行）----
    private bool _groupOpen;
    private string _groupLvl = string.Empty;
    private int _groupExtra;
    private long _groupAtMs;
    private string _lastHms = string.Empty;

    /// <summary>按类名独立计数的折叠窗口。</summary>
    private sealed class FoldState
    {
        public bool Open;
        public long SinceMs;
        public int Count;
        public string Last = string.Empty;
        public string Hms = string.Empty;
    }

    private readonly Dictionary<string, FoldState> _folds = new(StringComparer.Ordinal);

    private string _lastBody = string.Empty;
    private long _lastBodyAtMs;
    private bool _hasLastBody;

    /// <inheritdoc />
    public string Id => ExternalLogAdapterRegistry.BetterGIId;

    /// <inheritdoc />
    public ExternalLogProjection Project(string rawLine, long nowMs)
    {
        var raw = rawLine ?? string.Empty;

        // 纯空行不上屏（正常情况下采集层已经丢掉了，这里兜住重放场景）。
        if (raw.Trim().Length == 0) { return ExternalLogProjection.Drop; }

        // 幂等：已经是精简形态的行不再加工（主控端可能对老代理的行再投一次）。
        // 只在「不在组内」时生效：组内带缩进的堆栈续行不是精简文本，必须留给
        // ProcessLine 处理，否则会被短路成 fail-open、续行计数也会少算。
        if (!_hasPending && !_groupOpen && BetterGIGrammar.LooksCompact(raw))
        {
            return ExternalLogProjection.Pass;
        }

        var header = BetterGIGrammar.HeaderLine.Match(raw);
        if (header.Success)
        {
            OnHeader(raw, header, nowMs);
            // 头行本身不单独上屏：MetaDrop 让分派层跳过 Warn/Error 的强制原样放行，
            // 否则「只有类名、没有消息体」的元数据行会占掉一条两行旧形态。
            return _outputs.Count > 0 ? _outputs.Dequeue() : ExternalLogProjection.MetaDrop;
        }

        _inputs.Enqueue(raw);
        Drain(nowMs);
        return _outputs.Count > 0 ? _outputs.Dequeue() : ExternalLogProjection.Drop;
    }

    /// <inheritdoc />
    public ExternalLogProjection? Tick(long nowMs)
    {
        // 头行超时仍未等到正文：把原文放行，绝不静默丢行。
        if (_hasPending && nowMs - _pendingAtMs >= HeaderWindowMs) { FlushPendingHeader(); }

        // 多行组长时间没有新行：补一条续行摘要。
        if (_groupOpen && _groupExtra > 0 && nowMs - _groupAtMs >= HeaderWindowMs) { CloseGroup(); }

        CloseExpiredFolds(nowMs);

        if (_outputs.Count == 0 && _inputs.Count > 0) { Drain(nowMs); }
        return _outputs.Count > 0 ? _outputs.Dequeue() : null;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _inputs.Clear();
        _outputs.Clear();
        _pendingHms = string.Empty;
        _pendingLvl = string.Empty;
        _pendingRaw = string.Empty;
        _pendingAtMs = 0;
        _hasPending = false;
        _groupOpen = false;
        _groupLvl = string.Empty;
        _groupExtra = 0;
        _groupAtMs = 0;
        _lastHms = string.Empty;
        _folds.Clear();
        _lastBody = string.Empty;
        _lastBodyAtMs = 0;
        _hasLastBody = false;
    }

    /// <summary>
    /// 读到一条头行：收尾上一组、排空到期折叠窗口、把元数据挂起等正文。
    /// 组边界只认头行 —— 空行在采集层已被过滤，适配器永远收不到。
    /// </summary>
    private void OnHeader(string raw, Match header, long nowMs)
    {
        // 上一个头行还没等到正文（异常形态）：先把它原文放行。
        if (_hasPending) { FlushPendingHeader(); }

        CloseGroup();
        CloseExpiredFolds(nowMs);

        _pendingHms = header.Groups["hms"].Value;
        _pendingLvl = header.Groups["lvl"].Value;
        _pendingRaw = raw;
        _pendingAtMs = nowMs;
        _hasPending = true;
        _lastHms = _pendingHms;
    }

    /// <summary>超时未配对的头行：以原文出一条，保证任何被缓存的行最终都有输出。</summary>
    private void FlushPendingHeader()
    {
        if (!_hasPending) { return; }
        var raw = _pendingRaw;
        var hms = _pendingHms;
        var level = BetterGIGrammar.LevelOf(_pendingLvl);
        _hasPending = false;
        _pendingRaw = string.Empty;
        Emit(raw, raw, ExternalLogKind.Plain, hms, level);
    }

    /// <summary>逐行消费队列，直到手上有一条可以交出去的投影。</summary>
    private void Drain(long nowMs)
    {
        while (_outputs.Count < 4 && _inputs.Count > 0)
        {
            var line = _inputs.Dequeue();
            var produced = _outputs.Count;
            try
            {
                ProcessLine(line, nowMs);
            }
            catch (Exception)
            {
                // 解析永不抛到上层：认不出来的行原样放行（fail-open）。
                if (_outputs.Count == produced) { _outputs.Enqueue(ExternalLogProjection.Pass); }
            }
        }
    }

    private void ProcessLine(string raw, long nowMs)
    {
        if (_hasPending)
        {
            // 本组第一条正文：借头行的时间、级别与模块信息合成一条。
            _hasPending = false;
            var hms = _pendingHms;
            var lvl = _pendingLvl;
            _pendingRaw = string.Empty;
            _groupOpen = true;
            _groupLvl = lvl;
            _groupExtra = 0;
            _groupAtMs = nowMs;
            EmitBody(raw, lvl, hms, nowMs);
            return;
        }

        if (_groupOpen)
        {
            // 多行正文组（异常堆栈，实测最多 12 行）：后续行只进归档、不上面板，
            // 面板由「首行 + 一条摘要」承载，归档因此仍逐行等于脚本自身日志。
            _groupAtMs = nowMs;
            Emit(string.Empty, raw, ExternalLogKind.Plain, _lastHms,
                BetterGIGrammar.LevelOf(_groupLvl));
            _groupExtra++;
            return;
        }

        // 没有 pending 头、也不在组里：不认识的孤立行，fail-open。
        _outputs.Enqueue(ExternalLogProjection.Pass);
    }

    /// <summary>本组第一条正文的全部规则（顺序不可换）。</summary>
    private void EmitBody(string raw, string lvl, string hms, long nowMs)
    {
        var body = raw.TrimEnd();
        var level = BetterGIGrammar.LevelOf(lvl);

        // ① 纯分隔符绝不上屏。
        if (BetterGIGrammar.IsSeparator(body)) { return; }

        // ② 任务边界行（结构行优先于级别）。
        if (TryEmitBoundary(raw, body, hms, level)) { return; }

        // ③ ERR / WRN 必出：不参与去重、不参与节流。
        if (BetterGIGrammar.IsErrorOrWarn(lvl))
        {
            var prefix = lvl == "ERR" ? "✗ " : "⚠ ";
            var kind = lvl == "ERR" ? ExternalLogKind.TaskFail : ExternalLogKind.Warn;
            Emit(prefix + Clean(body), raw, kind, hms, level);
            return;
        }

        // ④ 一次性启动期噪声。
        if (BetterGIGrammar.IsNoiseBody(body)) { return; }

        // ⑤ 高频过程行按类折叠；DBG 的零散业务行同样折叠（默认类）。
        var cls = BetterGIGrammar.ProcedureClassOf(body);
        if (cls.Length == 0 && lvl == "DBG") { cls = "调试"; }
        if (cls.Length > 0)
        {
            Fold(raw, Clean(body), cls, hms, level, nowMs);
            return;
        }

        // ⑥ 普通 INF 业务正文：两空格缩进 + 2 秒内同文去重。
        EmitNotice(raw, body, hms, level, nowMs);
    }

    /// <summary>任务边界 → `▶ / ✔ / ▸`。每条都指向计划书 §1.3 的实测行。</summary>
    private bool TryEmitBoundary(string raw, string body, string hms, ExternalLogLevel level)
    {
        if (BetterGIGrammar.TaskRunnerStart.IsMatch(body))
        {
            Emit("▶ 任务启动", raw, ExternalLogKind.TaskStart, hms, level);
            return true;
        }

        if (BetterGIGrammar.TaskRunnerDone.IsMatch(body))
        {
            Emit("✔ 任务结束", raw, ExternalLogKind.TaskDone, hms, level);
            return true;
        }

        var match = BetterGIGrammar.OneDragonProgress.Match(body);
        if (match.Success)
        {
            Emit("▶ 一条龙任务执行 " + match.Groups["cur"].Value + "/" + match.Groups["total"].Value,
                raw, ExternalLogKind.TaskStart, hms, level);
            return true;
        }

        match = BetterGIGrammar.GroupProgress.Match(body);
        if (match.Success)
        {
            Emit("▸ 配置组任务执行 " + match.Groups["cur"].Value + "/" + match.Groups["total"].Value,
                raw, ExternalLogKind.Node, hms, level);
            return true;
        }

        match = BetterGIGrammar.GroupStart.Match(body);
        if (match.Success)
        {
            Emit("▸ 配置组「" + match.Groups["name"].Value + "」加载完成，共 " +
                 match.Groups["count"].Value + " 个脚本",
                raw, ExternalLogKind.Node, hms, level);
            return true;
        }

        match = BetterGIGrammar.GroupDone.Match(body);
        if (match.Success)
        {
            Emit("✔ 配置组「" + match.Groups["name"].Value + "」执行结束",
                raw, ExternalLogKind.TaskDone, hms, level);
            return true;
        }

        match = BetterGIGrammar.JsStart.Match(body);
        if (match.Success)
        {
            Emit("▶ 开始执行 JS 脚本：" + Clean(match.Groups["name"].Value),
                raw, ExternalLogKind.TaskStart, hms, level);
            return true;
        }

        match = BetterGIGrammar.MapStart.Match(body);
        if (match.Success)
        {
            Emit("▶ 开始执行地图追踪任务：" + Clean(match.Groups["name"].Value),
                raw, ExternalLogKind.TaskStart, hms, level);
            return true;
        }

        match = BetterGIGrammar.ScriptEnd.Match(body);
        if (match.Success)
        {
            Emit("✔ 脚本执行结束：" + Clean(match.Groups["name"].Value) +
                 "（" + match.Groups["cost"].Value.Trim() + "）",
                raw, ExternalLogKind.TaskDone, hms, level);
            return true;
        }

        return false;
    }

    /// <summary>过程行折叠：窗口内首条出，其余累计，到期由汇总行补。</summary>
    private void Fold(string raw, string clean, string cls, string hms, ExternalLogLevel level, long nowMs)
    {
        if (!_folds.TryGetValue(cls, out var state))
        {
            state = new FoldState();
            _folds[cls] = state;
        }

        if (!state.Open || nowMs - state.SinceMs >= ThrottleWindowMs)
        {
            if (state.Open && state.Count > 0) { EmitFoldSummary(cls, state); }
            state.Open = true;
            state.SinceMs = nowMs;
            state.Count = 0;
            state.Last = clean;
            state.Hms = hms;
            Emit("  " + clean, raw, ExternalLogKind.Notice, hms, level);
            return;
        }

        state.Count++;
        state.Last = clean;
    }

    private void EmitFoldSummary(string cls, FoldState state)
    {
        if (state.Count <= 0) { state.Open = false; return; }
        var text = "⋯ " + cls + " ×" + state.Count.ToString() +
                   "（最近：" + state.Last + "）";
        Emit(text, text, ExternalLogKind.Heartbeat, state.Hms, ExternalLogLevel.Info);
        state.Count = 0;
        state.Open = false;
    }

    private void CloseExpiredFolds(long nowMs)
    {
        foreach (var pair in _folds)
        {
            var state = pair.Value;
            if (state.Open && state.Count > 0 && nowMs - state.SinceMs >= ThrottleWindowMs)
            {
                EmitFoldSummary(pair.Key, state);
            }
        }
    }

    /// <summary>多行组收尾：补一条续行摘要（原文已经逐行进归档）。</summary>
    private void CloseGroup()
    {
        if (!_groupOpen) { return; }
        _groupOpen = false;
        if (_groupExtra <= 0) { return; }

        var count = _groupExtra;
        _groupExtra = 0;
        var text = "⋯ 后续 " + count.ToString() + " 行（原文见归档）";
        Emit(text, text, ExternalLogKind.Heartbeat, _lastHms, ExternalLogLevel.Info);
    }

    private void EmitNotice(string raw, string body, string hms, ExternalLogLevel level, long nowMs)
    {
        var clean = Clean(body);
        if (clean.Length == 0) { return; }

        if (_hasLastBody &&
            string.Equals(clean, _lastBody, StringComparison.Ordinal) &&
            nowMs - _lastBodyAtMs <= DuplicateWindowMs)
        {
            return;
        }

        _lastBody = clean;
        _lastBodyAtMs = nowMs;
        _hasLastBody = true;
        Emit("  " + clean, raw, ExternalLogKind.Notice, hms, level);
    }

    /// <summary>路径压缩 + 去掉正文首尾空白。</summary>
    private static string Clean(string body) => BetterGIGrammar.CompressPaths(body.Trim());

    private void Emit(string panel, string node, ExternalLogKind kind, string hms, ExternalLogLevel level) =>
        _outputs.Enqueue(ExternalLogProjection.CompactLine(panel, node, kind, hms, level));
}
