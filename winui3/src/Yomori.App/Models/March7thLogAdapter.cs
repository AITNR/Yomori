// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>
/// March7thAssistant / March7th Launcher 的日志适配器。
///
/// <para>这个脚本的日志"本来就是人话"，乱的只是三样东西：宽控制台的 ASCII 边框、
/// 每行都带一遍的日期与级别、以及 `切换到：…` 这类一秒一条的过程行。
/// 所以这里不做语义翻译，只做排版规整（计划书 §3.3 / §3.4）。</para>
///
/// <para>状态全部按实例持有：两个通道同时跟同一份日志时，阶段名与节流计数不能互相污染。</para>
/// </summary>
public sealed class March7thLogAdapter : IExternalLogAdapter
{
    /// <summary>界面状态节流窗口（与 `ExternalLogProjector.NodeThrottleMs` 同量级）。</summary>
    private const long UiWindowMs = 30_000;

    /// <summary>识别失败告警的折叠窗口。</summary>
    private const long WarnWindowMs = 30_000;

    /// <summary>合并组等待续行的上限：超过这个时间没有新续行就收尾。</summary>
    private const long GroupWindowMs = 2_000;

    /// <summary>完全相同正文的去重窗口（双线程重复打印）。</summary>
    private const long DuplicateWindowMs = 2_000;

    /// <summary>等待处理的原始行。用队列而不是"暂存一行"：一次调用可能产出两条输出，
    /// 剩下的那条必须留在原地等下一次调用，绝不丢行。</summary>
    private readonly Queue<string> _inputs = new();

    /// <summary>已经算好、还没交出去的投影。</summary>
    private readonly Queue<ExternalLogProjection> _outputs = new();

    /// <summary>上一条标准行的 `HH:mm:ss`；没有自带时间的行沿用它的时间。</summary>
    private string _lastHms = string.Empty;

    /// <summary>最近一次框内标题（`✔ 完成：<阶段名>` 用它）。</summary>
    private string _stage = string.Empty;

    /// <summary>正在等续行的合并组（以冒号结尾的那条正文，已清洗）。</summary>
    private string? _groupText;

    /// <summary>合并组的原始行（NodeText 要能对上脚本日志）。</summary>
    private string _groupRaw = string.Empty;

    private string _groupHms = string.Empty;
    private long _groupAtMs;

    private bool _uiOpen;
    private long _uiWindowAtMs;
    private int _uiFolded;
    private string _uiLast = string.Empty;

    private bool _warnOpen;
    private long _warnWindowAtMs;
    private int _warnFolded;

    private string _lastVersion = string.Empty;

    private string _lastBody = string.Empty;
    private long _lastBodyAtMs;
    private bool _hasLastBody;

    /// <summary>状态行的上次取值（`开拓力` / `沉浸器` …），同值不出第二条。</summary>
    private readonly Dictionary<string, string> _resourceValues = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => ExternalLogAdapterRegistry.March7thId;

    /// <inheritdoc />
    public ExternalLogProjection Project(string rawLine, long nowMs)
    {
        var raw = rawLine ?? string.Empty;

        // 纯空行不上屏，也不占用队列（它没有内容可以丢）。
        if (raw.Trim().Length == 0) { return ExternalLogProjection.Drop; }

        // 幂等：已经是精简形态的行不再加工（主控端可能对老代理的行再投一次）。
        if (March7thGrammar.LooksCompact(raw)) { return ExternalLogProjection.Pass; }

        _inputs.Enqueue(raw);
        Drain(nowMs);
        return _outputs.Count > 0 ? _outputs.Dequeue() : ExternalLogProjection.Drop;
    }

    /// <inheritdoc />
    public ExternalLogProjection? Tick(long nowMs)
    {
        FlushGroupIfStale(nowMs);
        CloseUiWindowIfExpired(nowMs);
        CloseWarnWindowIfExpired(nowMs);

        if (_outputs.Count == 0 && _inputs.Count > 0) { Drain(nowMs); }
        return _outputs.Count > 0 ? _outputs.Dequeue() : null;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _inputs.Clear();
        _outputs.Clear();
        _lastHms = string.Empty;
        _stage = string.Empty;
        _groupText = null;
        _groupRaw = string.Empty;
        _groupHms = string.Empty;
        _groupAtMs = 0;
        _uiOpen = false;
        _uiWindowAtMs = 0;
        _uiFolded = 0;
        _uiLast = string.Empty;
        _warnOpen = false;
        _warnWindowAtMs = 0;
        _warnFolded = 0;
        _lastVersion = string.Empty;
        _lastBody = string.Empty;
        _lastBodyAtMs = 0;
        _hasLastBody = false;
        _resourceValues.Clear();
    }

    /// <summary>
    /// 逐行消费队列，直到手上有一条可以交出去的投影。
    ///
    /// <para>一行可能产出 0 条（丢弃）、1 条，或 2 条（先收尾上一组、再出自己的）。
    /// 多出来的留在 <see cref="_outputs"/> 里，由下一次调用取走。</para>
    /// </summary>
    private void Drain(long nowMs)
    {
        // 上限 3 条：一行最多产出 2 条（先收尾上一组、再出自己的），留一点余量就够，
        // 又不会因为"每行产出两条"把输入队列堆起来。
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
                // 解析永不抛到上层：认不出来的行原样放行（fail-open）。
                if (_outputs.Count == produced) { _outputs.Enqueue(ExternalLogProjection.Pass); }
            }
        }
    }

    private void ProcessLine(string raw, long nowMs)
    {
        var text = raw.TrimEnd();

        var standard = March7thGrammar.StandardLine.Match(text);
        if (standard.Success)
        {
            var level = standard.Groups["lvl"].Value;
            var body = standard.Groups["body"].Value.Trim();

            // 列表型续行：实测这一版日志的续行**带**时间戳，所以要在标准行分支里也认一次。
            if (IsInfo(level) && March7thGrammar.ContinuationItem.IsMatch(body) && TryAppendGroup(raw, body, nowMs))
            {
                _lastHms = standard.Groups["hms"].Value;
                return;
            }

            CloseGroup();
            _lastHms = standard.Groups["hms"].Value;
            if (body.Length > 0) { EmitStandard(raw, level, body, nowMs); }
            return;
        }

        // 没有时间戳的行：可能是续行（实测 `2026-11-05.log:336` 的 `预计今天8时5分回满`）。
        if (TryAppendGroup(raw, text, nowMs)) { return; }

        CloseGroup();
        EmitShape(raw, text, nowMs);
    }

    private void EmitStandard(string raw, string level, string body, long nowMs)
    {
        if (string.Equals(level, "ERROR", StringComparison.Ordinal) ||
            string.Equals(level, "CRITICAL", StringComparison.Ordinal))
        {
            // 错误必出：不参与去重、不参与节流。
            Emit("✗ " + CompressPaths(body), raw, ExternalLogKind.TaskFail, _lastHms);
            return;
        }

        if (string.Equals(level, "WARNING", StringComparison.Ordinal) ||
            string.Equals(level, "WARN", StringComparison.Ordinal))
        {
            EmitWarning(raw, body, nowMs);
            return;
        }

        EmitInfo(raw, body, nowMs);
    }

    private void EmitWarning(string raw, string body, long nowMs)
    {
        if (March7thGrammar.UnrecognizedUi.IsMatch(body))
        {
            FoldUnrecognizedUi(raw, nowMs);
            return;
        }

        Emit("⚠ " + CompressPaths(body), raw, ExternalLogKind.Warn, _lastHms);
    }

    /// <summary>INFO 正文的全部清洗与降噪规则（计划书 §3.4）。</summary>
    private void EmitInfo(string raw, string body, long nowMs)
    {
        if (March7thGrammar.IsNoiseBody(body)) { return; }

        if (March7thGrammar.UnrecognizedUi.IsMatch(body))
        {
            FoldUnrecognizedUi(raw, nowMs);
            return;
        }

        var launch = March7thGrammar.GameLaunch.Match(body);
        if (launch.Success)
        {
            EmitNotice(raw, "启动游戏 " + FileNameOf(launch.Groups["path"].Value), nowMs);
            return;
        }

        body = CompressPaths(body);

        var version = March7thGrammar.NewVersion.Match(body);
        if (version.Success)
        {
            var ver = version.Groups["ver"].Value.Trim();
            if (_lastVersion.Length > 0 && string.Equals(_lastVersion, ver, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lastVersion = ver;
            EmitNotice(raw, "发现新版本 " + ver, nowMs);
            return;
        }

        var ui = March7thGrammar.UiSwitch.Match(body);
        if (ui.Success)
        {
            EmitUiSwitch(raw, body, ui.Groups["name"].Value.Trim(), nowMs);
            return;
        }

        var status = March7thGrammar.ResourceStatus.Match(body);
        if (status.Success)
        {
            var key = status.Groups["key"].Value;
            var value = status.Groups["value"].Value.Trim();
            if (_resourceValues.TryGetValue(key, out var previous) && previous == value) { return; }
            _resourceValues[key] = value;
            EmitNotice(raw, body, nowMs);
            return;
        }

        // 以冒号结尾：后面 1～2 行是列表续行，先扣住，等续行或超时再合并出。
        if (EndsWithColon(body))
        {
            _groupText = body;
            _groupRaw = raw;
            _groupHms = _lastHms;
            _groupAtMs = nowMs;
            return;
        }

        EmitNotice(raw, body, nowMs);
    }

    /// <summary>界面状态节流：窗口内首条出，其余累计，窗口到期由 <see cref="Tick"/> 补一行摘要。</summary>
    private void EmitUiSwitch(string raw, string body, string name, long nowMs)
    {
        CloseUiWindowIfExpired(nowMs);
        if (!_uiOpen)
        {
            _uiOpen = true;
            _uiWindowAtMs = nowMs;
            _uiFolded = 0;
            _uiLast = name;
            EmitNotice(raw, body, nowMs);
            return;
        }

        _uiFolded++;
        _uiLast = name;
    }

    /// <summary>识别失败告警折叠：首条出人话，后续累计成 `⚠ 未识别出界面 ×N`。</summary>
    private void FoldUnrecognizedUi(string raw, long nowMs)
    {
        CloseWarnWindowIfExpired(nowMs);
        if (!_warnOpen)
        {
            _warnOpen = true;
            _warnWindowAtMs = nowMs;
            _warnFolded = 0;
            Emit("⚠ 未识别出界面，按 ESC 重试", raw, ExternalLogKind.Warn, _lastHms);
            return;
        }

        _warnFolded++;
    }

    /// <summary>普通正文：两空格缩进 + 2 秒内同文去重。</summary>
    private void EmitNotice(string raw, string body, long nowMs)
    {
        if (body.Length == 0) { return; }

        if (_hasLastBody &&
            string.Equals(body, _lastBody, StringComparison.Ordinal) &&
            nowMs - _lastBodyAtMs <= DuplicateWindowMs)
        {
            return;
        }

        _lastBody = body;
        _lastBodyAtMs = nowMs;
        _hasLastBody = true;
        Emit("  " + body, raw, ExternalLogKind.Notice, _lastHms);
    }

    /// <summary>非标准行（边框、标题、完成线、孤立正文）。</summary>
    private void EmitShape(string raw, string text, long nowMs)
    {
        if (text.Trim().Length == 0) { return; }

        var title = March7thGrammar.BoxTitle.Match(text);
        if (title.Success)
        {
            var name = title.Groups["title"].Value.Trim();
            if (name.Length == 0) { return; }

            _stage = name;
            Emit(name == "停止运行" ? "■ 停止运行" : "▶ " + name, raw, ExternalLogKind.TaskStart, _lastHms);
            return;
        }

        if (March7thGrammar.BoxRule.IsMatch(text) ||
            March7thGrammar.BoxRuleLeft.IsMatch(text) ||
            March7thGrammar.BoxRuleRight.IsMatch(text))
        {
            return;
        }

        if (March7thGrammar.DoneRule.IsMatch(text))
        {
            Emit(_stage.Length > 0 ? "✔ 完成：" + _stage : "✔ 完成", raw, ExternalLogKind.TaskDone, _lastHms);
            return;
        }

        var equals = March7thGrammar.EqualsTitle.Match(text);
        if (equals.Success)
        {
            var name = equals.Groups["title"].Value.Trim();
            if (name.Length == 0) { return; }
            Emit("▸ " + NormalizeStep(name), raw, ExternalLogKind.Node, _lastHms);
            return;
        }

        var dash = March7thGrammar.DashTitle.Match(text);
        if (dash.Success)
        {
            var name = dash.Groups["title"].Value.Trim();
            if (name.Length == 0) { return; }
            Emit("▸ " + NormalizeStep(name), raw, ExternalLogKind.Node, _lastHms);
            return;
        }

        // 剩下的分隔符残渣（`-----` / `=====`）绝不能上屏。
        if (March7thGrammar.SeparatorOnly.IsMatch(text)) { return; }

        // 真不认识的行：原样放行，交给粗略链路（fail-open）。
        _outputs.Enqueue(ExternalLogProjection.Pass);
    }

    /// <summary>横线/等号标题的规整：`开始刷 X，总计1轮，每轮包含6次` → `刷 X（1轮×6次）`。</summary>
    private static string NormalizeStep(string title)
    {
        var match = March7thGrammar.BatchStart.Match(title);
        if (!match.Success) { return title; }

        var what = match.Groups["what"].Value.Trim();
        var rounds = match.Groups["rounds"].Value;
        var per = match.Groups["per"].Value;
        return "刷 " + what + "（" + rounds + "轮×" + per + "次）";
    }

    /// <summary>把路径压成文件名：窄栏里一条 `D:\mhy\Star Rail Game\StarRail.exe` 要折三行。</summary>
    private static string CompressPaths(string body) =>
        March7thGrammar.WinPath.Replace(body, match => FileNameOf(match.Groups["path"].Value));

    private static string FileNameOf(string path)
    {
        var text = path.Trim().Trim('"');
        if (text.Length == 0) { return string.Empty; }
        var cut = text.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 ? text[(cut + 1)..].Trim() : text;
    }

    private static bool EndsWithColon(string body) =>
        body.Length > 0 && (body[^1] == ':' || body[^1] == '：');

    private static bool IsInfo(string level) =>
        level.Length == 0 ||
        string.Equals(level, "INFO", StringComparison.Ordinal) ||
        string.Equals(level, "DEBUG", StringComparison.Ordinal) ||
        string.Equals(level, "TRACE", StringComparison.Ordinal);

    /// <summary>这一行是不是结构行（边框/标题/标准行）—— 结构行永远不能当续行吃掉。</summary>
    private static bool IsFrameShape(string text) =>
        March7thGrammar.StandardLine.IsMatch(text) ||
        March7thGrammar.BoxRule.IsMatch(text) ||
        March7thGrammar.BoxRuleLeft.IsMatch(text) ||
        March7thGrammar.BoxRuleRight.IsMatch(text) ||
        March7thGrammar.BoxTitle.IsMatch(text) ||
        March7thGrammar.DoneRule.IsMatch(text) ||
        March7thGrammar.EqualsTitle.IsMatch(text) ||
        March7thGrammar.DashTitle.IsMatch(text);

    private bool TryAppendGroup(string raw, string body, long nowMs)
    {
        if (_groupText is null) { return false; }
        if (IsFrameShape(body)) { return false; }

        _groupText = _groupText + " " + body;
        _groupRaw = _groupRaw.Length > 0 ? _groupRaw + " " + raw : raw;
        _groupAtMs = nowMs;
        return true;
    }

    /// <summary>把合并组收尾成一行。没有组时什么也不做。</summary>
    private void CloseGroup()
    {
        var text = _groupText;
        if (text is null) { return; }

        var node = _groupRaw.Length > 0 ? _groupRaw : text;
        var hms = _groupHms;
        _groupText = null;
        _groupRaw = string.Empty;
        _groupHms = string.Empty;
        _groupAtMs = 0;

        _outputs.Enqueue(ExternalLogProjection.CompactLine("  " + text, node, ExternalLogKind.Notice, hms));
    }

    /// <summary>合并组超过 2 秒没有续行：立即收尾，不能让它一直扣着。</summary>
    private void FlushGroupIfStale(long nowMs)
    {
        if (_groupText is null) { return; }
        if (nowMs - _groupAtMs < GroupWindowMs) { return; }
        CloseGroup();
    }

    private void CloseUiWindowIfExpired(long nowMs)
    {
        if (!_uiOpen) { return; }
        if (nowMs - _uiWindowAtMs < UiWindowMs) { return; }
        _uiOpen = false;
        if (_uiFolded <= 0) { return; }

        var count = _uiFolded;
        _uiFolded = 0;
        var text = "⋯ 界面切换 ×" + count.ToString();
        if (_uiLast.Length > 0) { text = text + "（最近：" + _uiLast + "）"; }
        _outputs.Enqueue(ExternalLogProjection.CompactLine(text, text, ExternalLogKind.Heartbeat, _lastHms));
    }

    private void CloseWarnWindowIfExpired(long nowMs)
    {
        if (!_warnOpen) { return; }
        if (nowMs - _warnWindowAtMs < WarnWindowMs) { return; }
        _warnOpen = false;
        if (_warnFolded <= 0) { return; }

        var count = _warnFolded;
        _warnFolded = 0;
        var text = "⚠ 未识别出界面 ×" + count.ToString();
        _outputs.Enqueue(ExternalLogProjection.CompactLine(text, text, ExternalLogKind.Warn, _lastHms));
    }

    private void Emit(string panel, string node, ExternalLogKind kind, string sourceTime) =>
        _outputs.Enqueue(ExternalLogProjection.CompactLine(panel, node, kind, sourceTime));
}
