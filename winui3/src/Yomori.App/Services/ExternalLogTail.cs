// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 构造时建立文件 baseline；每个文件保留独立字节位置与未完成行。
/// 所有读取与释放串行化，日志文件可以在脚本启动后才出现。
/// </summary>
public sealed class ExternalLogTail : IDisposable
{
    private sealed class Cursor
    {
        public long Offset;
        public long Creation;
        public byte[] Anchor = Array.Empty<byte>();
        public readonly List<byte> Pending = new();
        public readonly List<byte> DiscardProbe = new();

        /// <summary>
        /// 用 <c>\r</c> 刷新同一行的进度条内容（<c>10%\r20%\r…</c>）。
        ///
        /// <para>为什么不直接当普通行发出去：pip / uv / 解压器每几百毫秒刷一次，
        /// 一次下载就是上千行；面板只有 200 行、归档又会全留（Unknown 级别不丢）。
        /// 这里只保留**最后一段**，遇到换行或收尾时才输出一次，语义与终端里看到的一致。</para>
        /// </summary>
        public readonly List<byte> Progress = new();

        public Encoding? BomEncoding;
        public bool HeaderChecked;
        public bool DiscardingOversizeLine;
    }

    private readonly object _sync = new();
    private readonly string _rootPath;
    private readonly List<string> _patterns;
    private readonly bool _recursive;
    private readonly string _encodingMode;
    private readonly Dictionary<string, Cursor> _files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private const int MaxReadBytesPerPoll = 256 * 1024;
    private const int MaxOutputLinesPerPoll = 200;
    private const int MaxPendingBytes = 64 * 1024;

    /// <summary>递归时最多进几层子目录（LALC 的 logs/&lt;run_id&gt;/ 只有一层）。</summary>
    private const int MaxRecursiveDepth = 3;

    /// <summary>递归时最多收多少个匹配文件；超了只留前 N 个并在诊断里说明。</summary>
    private const int MaxRecursiveFiles = 500;

    /// <summary>内容探针最多看多少行；到量仍未命中就放弃（显式线索缺失时才会走到这里）。</summary>
    private const int AdapterProbeLimit = 32;

    private readonly ExternalLogProjector _projector;
    private readonly string _view;

    /// <summary>
    /// 逐脚本深度适配器；为 <c>null</c> 时整条链路与改造前逐行等价。
    ///
    /// <para>识别只在构造时做一次（显式线索），或在头 32 行内容探针命中时绑定一次；
    /// 绑定后整份文件生命周期内不再切换 —— 通道里前后两种日志形态混着出现比不精简更难读。</para>
    /// </summary>
    private IExternalLogAdapter? _adapter;

    /// <summary>内容探针累积的原始行（最多 <see cref="AdapterProbeLimit"/> 条）。</summary>
    private readonly List<string> _adapterProbeLines = new();

    /// <summary>探针已结束（命中或到量）：不再累积行，也不再反复尝试识别。</summary>
    private bool _adapterProbeDone;

    private int _labelsLoading;
    private string? _currentFile;
    private string _lastError = string.Empty;
    private string _note = string.Empty;
    private bool _disposed;

    static ExternalLogTail() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <param name="recursive">
    /// 目录模式是否递归子目录。默认 false —— 只在预设明确要求时打开（LALC 每次运行一个
    /// <c>logs\&lt;run_id&gt;\</c>、ok-Onmyoji 多开按实例分目录）。打开时受深度与文件数两道闸限制。
    /// </param>
    /// <param name="view">
    /// 显示视图（<c>panel</c> / <c>node</c> / <c>raw</c>）。原文视图直接跳过投影 ——
    /// 用户要看原文时再套一层"重写"就自相矛盾了。
    /// </param>
    /// <param name="adapterHint">
    /// 深度适配的识别线索（预设 id / 入口名 / 日志路径与模式）。命中就整份文件走适配器；
    /// 为 <c>null</c> 或都没命中时按原样走粗略链路，行为与改造前完全一致。
    /// </param>
    public ExternalLogTail(string path, string? pattern, string? encodingName, bool followTail,
        string? baseDirectory = null, bool recursive = false, string? view = null,
        ExternalLogAdapterHint? adapterHint = null)
    {
        var expanded = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim());
        _rootPath = expanded.Length == 0 ? string.Empty : Path.GetFullPath(expanded,
            Path.GetFullPath(baseDirectory ?? Environment.CurrentDirectory));
        _patterns = ParsePatterns(pattern);
        _recursive = recursive;
        _view = ExternalLogViews.Normalize(view);
        _encodingMode = ExternalLogEncodings.Normalize(encodingName);
        // 显式线索命中就直接绑定，不再做内容探针（探针只是线索缺失时的兜底）。
        _adapter = ExternalLogAdapterRegistry.Select(adapterHint);
        _adapterProbeDone = _adapter is not null;
        // 族判定在"打开文件之前"做一次：MaaFW 归档的头几十行往往是外壳启动日志
        // （`[INFO][mxu_lib::commands::…]`），只在逐行流里判会把这些行当"不认识"原样放出来。
        _projector = new ExternalLogProjector(LogLabelMap.Empty, ProbeMaafw()) { View = _view };
        BeginLoadLabels(baseDirectory);
        try
        {
            foreach (var file in Candidates())
            {
                using var stream = Open(file);
                var cursor = new Cursor { Creation = File.GetCreationTimeUtc(file).Ticks };
                cursor.Offset = followTail ? stream.Length : 0;
                cursor.Anchor = ReadAnchor(stream, cursor.Offset);
                DetectExistingBom(stream, cursor);
                _files[file] = cursor;
            }
        }
        catch (Exception ex) { SetError(ex); }
    }

    /// <summary>
    /// 读文件开头 512 KB 判断是不是 MaaFramework 内核日志。
    ///
    /// <para>按 **ASCII 字节**找标记（`!!!OnEventNotify!!!` / `[msg=Tasker.` / `MaaNS::`），
    /// 因此不受编码影响 —— GBK 日志里这些标记同样是单字节。找不到任何标记但文件有内容时，
    /// 判定为"不是内核日志"（原样放行）；文件还不存在时返回 <c>null</c>，交给逐行判定兜底。</para>
    /// </summary>
    private bool? ProbeMaafw()
    {
        try
        {
            foreach (var file in Candidates())
            {
                using var stream = Open(file);
                var buffer = new byte[512 * 1024];
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) { continue; }

                var detected = ExternalLogGrammar.DetectMaafw(Encoding.Latin1.GetString(buffer, 0, read));
                if (detected is not null) { return detected; }
            }
        }
        catch (Exception)
        {
            // 探测失败不是错误：交给逐行判定。
        }

        return null;
    }

    public string? CurrentFile { get { lock (_sync) { return _currentFile; } } }
    /// <summary>最近一次诊断；成功读取后清空。由 worker 在变化/恢复时报告。</summary>
    /// <summary>最近一次诊断。读取成功后清空；构造期的提示（<see cref="_note"/>）常驻。</summary>
    public string LastError { get { lock (_sync) { return _lastError.Length > 0 ? _lastError : _note; } } }

    /// <summary>是否仍有已缓存或尚未读取的日志字节；用于有界的收尾 drain 循环。</summary>
    public bool HasUnreadData
    {
        get
        {
            lock (_sync)
            {
                if (_disposed) { return false; }
                if (_files.Values.Any(cursor => cursor.Pending.Count > 0 || cursor.DiscardProbe.Count > 0 ||
                                                 cursor.DiscardingOversizeLine))
                {
                    return true;
                }

                try
                {
                    foreach (var file in Candidates())
                    {
                        if (!_files.TryGetValue(file, out var cursor))
                        {
                            return true;
                        }

                        using var stream = Open(file);
                        if (stream.Length != cursor.Offset ||
                            !ReadAnchor(stream, cursor.Offset).SequenceEqual(cursor.Anchor))
                        {
                            return true;
                        }
                    }

                    return false;
                }
                catch
                {
                    // 无法可靠检查时保守地要求调用方再做一次有界 drain。
                    return true;
                }
            }
        }
    }

    public IReadOnlyList<ExternalLogLine> Poll(IReadOnlyList<string>? keywords) => Read(keywords, false);

    /// <summary>停止时读取最后新增字节并发送最后一条未换行文本；应在 Dispose 前调用。</summary>
    public IReadOnlyList<ExternalLogLine> Drain(IReadOnlyList<string>? keywords) => Read(keywords, true);

    /// <summary>
    /// 已适配脚本的关键字预处理：剔除**恰好是级别词**的关键字。
    ///
    /// <para>第三期实测：MaaFW 原始内核行里 `save_on_error` / `CloseErrorDialog` 会命中
    /// 裸 `ERROR`，而「关键字命中绝不吞」会把整条英文长行以两行旧形态推上屏。
    /// 已适配脚本的 ERR / WRN 由适配器自己必出，不依赖关键字；用户自定义的业务关键字
    /// （`TaskChainError` / `SubTaskError` 等）一律保留。未适配族不受影响。</para>
    /// </summary>
    private IReadOnlyList<string>? EffectiveKeywords(IReadOnlyList<string>? keywords)
    {
        if (_adapter is null || keywords is null || keywords.Count == 0) { return keywords; }
        var kept = new List<string>(keywords.Count);
        foreach (var keyword in keywords)
        {
            var word = (keyword ?? string.Empty).Trim();
            if (word.Length == 0 || ExternalLogFilter.IsBareLevelWord(word)) { continue; }
            kept.Add(keyword ?? string.Empty);
        }

        return kept;
    }

    private IReadOnlyList<ExternalLogLine> Read(IReadOnlyList<string>? keywords, bool final)
    {
        lock (_sync)
        {
            var result = new List<ExternalLogLine>();
            if (_disposed) { return result; }
            var effective = EffectiveKeywords(keywords);
            try
            {
                var candidates = Candidates().OrderByDescending(File.GetLastWriteTimeUtc).ToList();
                if (candidates.Count == 0)
                {
                    // 不再有匹配文件时，所有现存 cursor 都属于已轮转或已删除的文件。
                    // 清理其 Pending，防止 final drain 因 HasUnreadData 重复空转。
                    _files.Clear();

                    _lastError = $"等待日志文件：{_rootPath}（匹配 {string.Join(";", _patterns)}）";
                }
                else
                {
                    _currentFile = candidates[0];
                    var candidateSet = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
                    var retired = _files.Keys.Where(file => !candidateSet.Contains(file)).ToArray();
                    foreach (var file in _files.Keys.Where(file => !File.Exists(file)).ToArray())
                    {
                        _files.Remove(file);
                    }

                    var bytesRemaining = MaxReadBytesPerPoll;
                    foreach (var file in _files.Keys.Concat(new[] { _currentFile })
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                    {
                        if (bytesRemaining <= 0 || result.Count >= MaxOutputLinesPerPoll) { break; }
                        if (File.Exists(file))
                        {
                            ReadFile(file, result, effective, final, ref bytesRemaining);
                        }

                        // 重命名后不再匹配的旧文件最多再收一拍；删除文件则直接淘汰。
                        if (retired.Contains(file, StringComparer.OrdinalIgnoreCase) || !File.Exists(file))
                        {
                            _files.Remove(file);
                        }
                    }
                    _lastError = string.Empty;
                }
            }
            catch (Exception ex) { SetError(ex); }
            return result;
        }
    }

    private IEnumerable<string> Candidates()
    {
        if (_rootPath.Length == 0) { throw new IOException("日志路径为空"); }
        // GetAttributes 保留权限错误；File.Exists/Directory.Exists 会把它们掩盖为不存在。
        FileAttributes attributes;
        try { attributes = File.GetAttributes(_rootPath); }
        catch (FileNotFoundException) { return Array.Empty<string>(); }
        catch (DirectoryNotFoundException) { throw new DirectoryNotFoundException($"日志目录不存在：{_rootPath}"); }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            // 直接指定了文件：模式与递归都不参与（用户已经说清楚要哪一份）
            return new[] { _rootPath };
        }

        return EnumerateMatches();
    }

    /// <summary>
    /// 多模式 + 可选递归地列出匹配文件。
    ///
    /// <para>模式用 <c>;</c> 分隔（如 <c>maafw.log;maa.log</c> 覆盖两代内核的文件名）。
    /// 递归时加两道闸：深度 ≤ <see cref="MaxRecursiveDepth"/>、总数 ≤ <see cref="MaxRecursiveFiles"/>。
    /// 顺序上先按深度筛再计数，避免在 <c>logs\</c> 这种每天一个子目录的地方把枚举跑穿。</para>
    /// </summary>
    private List<string> EnumerateMatches()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var option = _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var overflow = false;

        foreach (var pattern in _patterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(_rootPath, pattern, option);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;   // 单个模式出错不该让其它模式一起失效
            }

            foreach (var file in files)
            {
                if (result.Count >= MaxRecursiveFiles) { overflow = true; break; }
                if (_recursive && DepthOf(file) > MaxRecursiveDepth) { continue; }
                if (seen.Add(file)) { result.Add(file); }
            }

            if (overflow) { break; }
        }

        if (overflow && _note.Length == 0)
        {
            _note = $"日志目录里的匹配文件超过 {MaxRecursiveFiles} 个，只跟了前 {MaxRecursiveFiles} 个：" +
                    $"{_rootPath}（建议缩小目录或模式）";
        }

        return result;
    }

    /// <summary>文件相对日志根目录的层数（根下为 0，<c>run_id\run.log</c> 为 1）。</summary>
    private int DepthOf(string file)
    {
        try
        {
            var relative = Path.GetRelativePath(_rootPath, Path.GetDirectoryName(file) ?? _rootPath);
            if (relative.Length == 0 || relative == ".") { return 0; }
            return relative.Count(ch => ch == Path.DirectorySeparatorChar) + 1;
        }
        catch (Exception)
        {
            return 0;   // 算不出层数就当作在根下，宁可多跟一个也不整段丢掉
        }
    }

    /// <summary>把 <c>a.log;b.log</c> 拆成模式列表；空项丢弃，全空时回落默认 <c>*.log</c>。</summary>
    private static List<string> ParsePatterns(string? pattern)
    {
        var result = new List<string>();
        foreach (var part in (pattern ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = part.Trim();
            if (text.Length > 0) { result.Add(text); }
        }

        if (result.Count == 0)
        {
            result.Add(ExternalLogDefaults.Pattern);
        }

        return result;
    }

    private void ReadFile(string path, List<ExternalLogLine> result, IReadOnlyList<string>? keywords, bool final,
        ref int bytesRemaining)
    {
        using var stream = Open(path);
        var creation = File.GetCreationTimeUtc(path).Ticks;
        if (!_files.TryGetValue(path, out var cursor))
        {
            cursor = new Cursor { Creation = creation };
            _files[path] = cursor;
        }
        else if (creation != cursor.Creation || stream.Length < cursor.Offset ||
                 !ReadAnchor(stream, cursor.Offset).SequenceEqual(cursor.Anchor))
        {
            // baseline 之后的截断/重建也必须从零开始，不能 Math.Min(baseline, length)。
            cursor = new Cursor { Creation = creation };
            _files[path] = cursor;
        }

        stream.Position = cursor.Offset;
        var end = stream.Length; // 本拍快照，持续写入不应阻塞取消。
        // 上一拍可能在输出上限处停止，但完整行已经在 Pending 中。
        // 必须先消费这些字节，否则文件 EOF 时不会进入下面的读取循环。
        ExtractLines(cursor, result, keywords, false);
        var buffer = new byte[4096];
        while (stream.Position < end && bytesRemaining > 0 && result.Count < MaxOutputLinesPerPoll)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(Math.Min(buffer.Length, bytesRemaining),
                end - stream.Position));
            if (read == 0) { break; }
            var consumed = AppendBytes(cursor, buffer, read, result, keywords);
            // 只有已保存到 cursor 或已转换为输出的字节才提交 offset。若行数上限
            // 截断了本次 buffer，下一拍会从这个 offset 重新读取未提交的尾部。
            cursor.Offset += consumed;
            bytesRemaining -= consumed;
            if (consumed < read) { break; }
        }
        cursor.Anchor = ReadAnchor(stream, cursor.Offset);
        if (final && cursor.Offset >= end && result.Count < MaxOutputLinesPerPoll)
        {
            FlushFinal(cursor, result, keywords);
        }
    }

    private int AppendBytes(Cursor cursor, byte[] buffer, int count, List<ExternalLogLine> result,
        IReadOnlyList<string>? keywords)
    {
        if (!cursor.DiscardingOversizeLine && cursor.Pending.Count >= MaxPendingBytes &&
            result.Count < MaxOutputLinesPerPoll)
        {
            ExtractLines(cursor, result, keywords, false);
            if (cursor.Pending.Count >= MaxPendingBytes && result.Count < MaxOutputLinesPerPoll)
            {
                cursor.DiscardingOversizeLine = true;
                cursor.DiscardProbe.Clear();
            }
        }

        var i = 0;
        for (; i < count && result.Count < MaxOutputLinesPerPoll; i++)
        {
            if (cursor.DiscardingOversizeLine)
            {
                ConsumeDiscardedByte(cursor, buffer[i], result, keywords);
                continue;
            }

            cursor.Pending.Add(buffer[i]);
            if (cursor.Pending.Count < MaxPendingBytes || result.Count >= MaxOutputLinesPerPoll) { continue; }

            ExtractLines(cursor, result, keywords, false);
            if (result.Count < MaxOutputLinesPerPoll && cursor.Pending.Count >= MaxPendingBytes)
            {
                // 没有换行的行只保留固定大小前缀；后续字节只扫描行结束符。
                cursor.DiscardingOversizeLine = true;
                cursor.DiscardProbe.Clear();
            }
        }

        if (!cursor.DiscardingOversizeLine && result.Count < MaxOutputLinesPerPoll)
        {
            ExtractLines(cursor, result, keywords, false);
        }

        return i;
    }

    private void ExtractLines(Cursor cursor, List<ExternalLogLine> result,
        IReadOnlyList<string>? keywords, bool final)
    {
        if (!cursor.HeaderChecked)
        {
            // 跨 Poll 的 BOM 和多字节字符暂存原始字节，完整行到达后才解码。
            // \r 也算行界：只有进度条的程序可能整段都没有 \n。
            if (!final && cursor.Pending.Count < 4 && !HasLineBreak(cursor.Pending)) { return; }
            var bytes = cursor.Pending.ToArray();
            var bomLength = FindBom(bytes, out var encoding);
            cursor.BomEncoding = encoding;
            cursor.HeaderChecked = true;
            if (bomLength > 0) { cursor.Pending.RemoveRange(0, bomLength); }
        }
        var width = GetEncodingWidth(cursor);
        var bigEndian = IsBigEndian(cursor);
        var start = 0;
        var i = 0;
        while (i + width <= cursor.Pending.Count)
        {
            if (IsLineFeed(cursor.Pending, i, width, bigEndian))
            {
                EmitSegment(cursor, start, i - start, result, keywords);
                start = i + width;
                i = start;
                if (result.Count >= MaxOutputLinesPerPoll) { break; }
                continue;
            }

            if (IsCarriageReturn(cursor.Pending, i, width, bigEndian))
            {
                var next = i + width;
                if (next + width <= cursor.Pending.Count && IsLineFeed(cursor.Pending, next, width, bigEndian))
                {
                    // \r\n：先把进度段补出来，再当一次普通换行（Windows 文本 / 带 \r 的日志）
                    EmitSegment(cursor, start, i - start, result, keywords);
                    start = next + width;
                    i = start;
                    if (result.Count >= MaxOutputLinesPerPoll) { break; }
                    continue;
                }

                if (next + width > cursor.Pending.Count && !final)
                {
                    // 末字节可能是 \r\n 的前半：等下一拍再判，别把 \r\n 切成两行
                    break;
                }

                // 孤立的 \r：进度刷新，整段覆盖进 Progress，不直接产出
                cursor.Progress.Clear();
                if (i > start) { cursor.Progress.AddRange(cursor.Pending.GetRange(start, i - start)); }
                start = next;
                i = start;
                continue;
            }

            i += width;
        }

        if (start > 0) { cursor.Pending.RemoveRange(0, start); }

        // 达到批次行数上限时保留完整尾行给下一轮 Drain，不能把它们合并成一行。
        if (final && result.Count < MaxOutputLinesPerPoll)
        {
            if (cursor.Pending.Count > 0)
            {
                // 收尾时还有半行：它同样覆盖了之前那一段进度，直接发它
                cursor.Progress.Clear();
                AddLine(cursor, cursor.Pending.ToArray(), result, keywords);
                cursor.Pending.Clear();
            }
            else
            {
                FlushProgress(cursor, result, keywords);
            }
        }
    }

    /// <summary>
    /// 输出一段到换行为止的内容。
    ///
    /// <para>与挂起的进度段的关系按终端语义处理：<c>\r</c> 只是回到行首，
    /// 之后写进来的字符会把旧内容覆盖掉。所以本段非空时直接丢弃进度段（旧的那一行在
    /// 屏幕上已经看不见了），只有本段为空（<c>\r\n</c> 或末拍）才把进度段补出来。</para>
    /// </summary>
    private void EmitSegment(Cursor cursor, int start, int count, List<ExternalLogLine> result,
        IReadOnlyList<string>? keywords)
    {
        if (count > 0)
        {
            cursor.Progress.Clear();
            AddLine(cursor, cursor.Pending.GetRange(start, count).ToArray(), result, keywords);
            return;
        }

        FlushProgress(cursor, result, keywords);
    }

    /// <summary>
    /// 输出进度条的最后一段。批次数满时不输出也不清空 —— 这些字节已经出了 Pending，
    /// 清掉就真丢了，留给下一拍。
    /// </summary>
    private void FlushProgress(Cursor cursor, List<ExternalLogLine> result, IReadOnlyList<string>? keywords)
    {
        if (cursor.Progress.Count == 0 || result.Count >= MaxOutputLinesPerPoll) { return; }
        AddLine(cursor, cursor.Progress.ToArray(), result, keywords);
        cursor.Progress.Clear();
    }

    private void ConsumeDiscardedByte(Cursor cursor, byte value, List<ExternalLogLine> result,
        IReadOnlyList<string>? keywords)
    {
        var width = GetEncodingWidth(cursor);
        cursor.DiscardProbe.Add(value);
        if (cursor.DiscardProbe.Count < width) { return; }

        var bigEndian = IsBigEndian(cursor);
        if (IsLineFeed(cursor.DiscardProbe, 0, width, bigEndian) ||
            IsCarriageReturn(cursor.DiscardProbe, 0, width, bigEndian))
        {
            AddLine(cursor, cursor.Pending.ToArray(), result, keywords, wasTruncated: true);
            cursor.Pending.Clear();
            cursor.DiscardingOversizeLine = false;
        }

        cursor.DiscardProbe.Clear();
    }

    private void FlushFinal(Cursor cursor, List<ExternalLogLine> result, IReadOnlyList<string>? keywords)
    {
        if (cursor.DiscardingOversizeLine)
        {
            AddLine(cursor, cursor.Pending.ToArray(), result, keywords, wasTruncated: true);
            cursor.Pending.Clear();
            cursor.DiscardProbe.Clear();
            cursor.DiscardingOversizeLine = false;
            return;
        }

        ExtractLines(cursor, result, keywords, true);
    }

    private void AddLine(Cursor cursor, byte[] bytes, List<ExternalLogLine> result, IReadOnlyList<string>? keywords,
        bool wasTruncated = false)
    {
        string text;
        if (cursor.BomEncoding is not null) { text = cursor.BomEncoding.GetString(bytes); }
        else if (_encodingMode == ExternalLogEncodings.Gbk) { text = Encoding.GetEncoding(936).GetString(bytes); }
        else if (_encodingMode == ExternalLogEncodings.Utf8) { text = Encoding.UTF8.GetString(bytes); }
        else
        {
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { text = Encoding.GetEncoding(936).GetString(bytes); }
        }

        var raw = text.TrimEnd('\r');
        var line = ExternalLogFilter.Parse(raw, keywords, wasTruncated);
        if (line.Text.Length == 0) { return; }

        // 原文视图不投影：用户要看原文时再套一层"重写"就自相矛盾了。
        if (_view == ExternalLogViews.Raw)
        {
            result.Add(line);
            return;
        }

        // 显式线索缺失时，用前 32 行做一次内容探针（字节时代就知道是哪家的日志最好，
        // 但预设缺省也得能用）。探针期间的行已按粗略链路上屏，不做回溯改写 ——
        // 前面那几行留在面板上不影响阅读，反倒是"重排历史"会让人找不到自己刚才看到的那条。
        if (_adapter is null && !_adapterProbeDone)
        {
            _adapterProbeLines.Add(raw);
            _adapter = ExternalLogAdapterRegistry.DetectByContent(_adapterProbeLines);
            if (_adapter is not null || _adapterProbeLines.Count >= AdapterProbeLimit)
            {
                _adapterProbeDone = true;
            }
        }

        // 未识别脚本：**这条链路一个字符都不改**，保证改造前后逐行一致。
        if (_adapter is null)
        {
            // 投影必须发生在外部日志过滤器之后、截断之前 —— Parse 只做级别识别与规整，
            // 真正的截断在 NormalizeLine 里，而它作用在投影文本上（投影文本本来就很短）。
            var projection = _projector.Project(raw);
            if (!projection.Emit) { return; }
            if (projection.UseRaw)
            {
                result.Add(line);
                return;
            }

            if (projection.PanelText.Length == 0 && projection.NodeText.Length == 0) { return; }

            // Text 是"这个视图该显示的那一行"：节点视图下节点名才是内容，面板视图下 focus 才是内容。
            var display = _view == ExternalLogViews.Node
                ? (projection.NodeText.Length > 0 ? projection.NodeText : projection.PanelText)
                : projection.PanelText;
            result.Add(new ExternalLogLine
            {
                Text = display,
                NodeText = projection.NodeText,
                Kind = projection.Kind,
                Level = line.Level,
                KeywordHit = line.KeywordHit,
            });
            return;
        }

        // 已识别脚本：交给适配器决定这一行的形态。
        var adapted = _adapter.Project(raw, Environment.TickCount64);

        // fail-open：适配器不认识这一行，原样交给粗略链路（Compact 保持 false）。
        if (adapted.UseRaw)
        {
            result.Add(line);
            return;
        }

        if (!adapted.Emit)
        {
            // 关键字命中**绝不吞**，但已适配脚本不回到两行旧模板：MaaFW 内核原始行里
            // `save_on_error` / `CloseErrorDialog` 这类技术词会命中宽泛的 `ERROR` 关键字，
            // 第三期实测 124/124 条 Compact:false 行都含 error，整条英文长行以两行形态刷屏。
            // 改用一条单行短句承载命中事实，原始行仍逐字进归档。
            if (line.KeywordHit)
            {
                result.Add(new ExternalLogLine
                {
                    Text = "⚠ 关键字命中：" + ExternalLogFilter.FirstKeyword(raw, keywords),
                    NodeText = raw,
                    Raw = raw,
                    Kind = ExternalLogKind.Warn,
                    Level = line.Level is ExternalLogLevel.Warn or ExternalLogLevel.Error
                        ? line.Level : ExternalLogLevel.Warn,
                    KeywordHit = true,
                    Compact = true,
                    SourceTime = adapted.SourceTime,
                });
                return;
            }

            // 警告/错误行绝不吞：适配器的降噪规则针对的是过程噪声。
            // 例外：适配器明确标记 MetaOnly 的结构 / 元数据行（BetterGI 头行只有类名、
            // 没有消息体），它的语义已并入紧随的业务行，按级别强制放行只会推上一条空壳行。
            if ((line.Level is ExternalLogLevel.Warn or ExternalLogLevel.Error) && !adapted.MetaOnly)
            {
                result.Add(line);
            }

            return;
        }

        if (adapted.PanelText.Length == 0 && adapted.NodeText.Length == 0) { return; }

        // 面板档可能为空（节点档独有的行）：与现有投影链路保持一致，照样交出 ——
        // 上屏侧按"Text 为空"决定不显示，但归档仍要拿到 NodeText，不能在这里就把行丢掉。
        var adaptedDisplay = _view == ExternalLogViews.Node
            ? (adapted.NodeText.Length > 0 ? adapted.NodeText : adapted.PanelText)
            : adapted.PanelText;

        // 级别前缀（[错误] / [警告]）不在这里加：深度适配的正文已经自带 ✗ / ⚠ 符号，
        // 再加一层级别文字就重复了。NodeText 填原始行，归档因此仍与脚本自己的日志一致。
        result.Add(new ExternalLogLine
        {
            Text = adaptedDisplay,
            NodeText = adapted.NodeText,
            Raw = raw,
            Kind = adapted.Kind,
            // BetterGI 这类脚本把级别写在头行、正文在下一行：适配器跨行合成后，
            // 当前正文行的 Classify 恒为 Unknown。优先用适配器给出的真实级别，
            // 否则界面的「只看错误」筛选器与 TRACE/DEBUG 归档闸都会错配。
            Level = adapted.Level ?? line.Level,
            KeywordHit = line.KeywordHit,
            Compact = adapted.Compact,
            SourceTime = adapted.SourceTime,
        });
    }

    /// <summary>
    /// 采集节拍上的一次检查：任务在跑却没有输出时补心跳，长时间空档补一条"日志中断"。
    ///
    /// <para>必须由采集循环调用 —— 没有新行的时候才更需要它，否则长任务在面板上看起来就是卡死。</para>
    ///
    /// <para>返回**一批**而不是一条：适配器内部会积压（一行产两条、合并组收尾、节流窗口到期），
    /// 每拍只取一条会让队列越堆越长，用户看到的日志越来越滞后。这里一次排空，
    /// 但受 <see cref="MaxOutputLinesPerPoll"/> 约束，排不完的留到下一拍。</para>
    /// </summary>
    public IReadOnlyList<ExternalLogLine> Tick()
    {
        var result = new List<ExternalLogLine>();
        if (_disposed) { return result; }

        var now = Environment.TickCount64;

        if (_adapter is not null)
        {
            while (result.Count < MaxOutputLinesPerPoll)
            {
                var projection = _adapter.Tick(now);
                if (projection is null) { break; }
                if (projection.PanelText.Length == 0 && projection.NodeText.Length == 0) { continue; }

                result.Add(new ExternalLogLine
                {
                    Text = projection.PanelText,
                    NodeText = projection.NodeText,
                    Kind = projection.Kind,
                    Level = projection.Level ?? ExternalLogLevel.Info,
                    Compact = true,
                    SourceTime = projection.SourceTime,
                });
            }

            return result;
        }

        var tick = _projector.Tick(now);
        if (tick is null) { return result; }
        result.Add(new ExternalLogLine
        {
            Text = tick.PanelText,
            NodeText = tick.NodeText,
            Kind = tick.Kind,
            Level = ExternalLogLevel.Info,
        });
        return result;
    }

    /// <summary>投影器的会话状态（顶部状态条：当前任务 / 已进节点数 / 失败数）。</summary>
    public ExternalLogProjector Projector => _projector;

    /// <summary>
    /// 后台装项目资源（entry → 中文任务名）：5 MB 级 JSON，绝不能挡在采集线程的启动路径上。
    /// 装好之前任务名显示 entry 原文，装好后从下一行开始生效。
    /// </summary>
    private void BeginLoadLabels(string? baseDirectory)
    {
        if (Interlocked.Exchange(ref _labelsLoading, 1) == 1) { return; }
        var directory = baseDirectory;
        // 构造时捕获一次：适配器在整份文件生命周期内只绑定一次，
        // 这里拿局部引用即可，不必与内容探针期的 _adapter 写入抢锁。
        var labelAware = _adapter as IExternalLogLabelAware;
        _ = Task.Run(() =>
        {
            try
            {
                var map = LogLabelStore.Get(directory);
                _projector.SetLabels(map);
                // 已适配族（目前只有 MaaEnd）也要拿中文任务名 / 节点名：
                // 它不再走投影器，缺了这张表界面上就只剩英文 entry。
                labelAware?.SetLabels(map);
            }
            catch (Exception)
            {
                // 读不到项目资源只是"任务名显示英文"，不该影响日志采集本身。
            }
        });
    }

    private static int GetEncodingWidth(Cursor cursor) => cursor.BomEncoding?.CodePage is 1200 or 1201 ? 2 :
        cursor.BomEncoding?.CodePage is 12000 or 12001 ? 4 : 1;

    private static bool IsBigEndian(Cursor cursor) => cursor.BomEncoding?.CodePage is 1201 or 12001;

    /// <summary>从 <paramref name="offset"/> 起是否是一个换行符（按编码宽度对齐）。</summary>
    private static bool IsLineFeed(IReadOnlyList<byte> bytes, int offset, int width, bool bigEndian) =>
        MatchesCodeUnit(bytes, offset, width, bigEndian, 10);

    /// <summary>从 <paramref name="offset"/> 起是否是一个回车符。</summary>
    private static bool IsCarriageReturn(IReadOnlyList<byte> bytes, int offset, int width, bool bigEndian) =>
        MatchesCodeUnit(bytes, offset, width, bigEndian, 13);

    /// <summary>
    /// 比较一个码元。UTF-16 / UTF-32 下有效字节在低位，高位补 0；
    /// UTF-8 与 GBK 下 0x0A / 0x0D 不可能出现在多字节字符的后续字节里，按字节比对即可。
    /// </summary>
    private static bool MatchesCodeUnit(IReadOnlyList<byte> bytes, int offset, int width, bool bigEndian, byte value)
    {
        if (offset < 0 || offset + width > bytes.Count) { return false; }
        for (var i = 0; i < width; i++)
        {
            var expected = i == (bigEndian ? width - 1 : 0) ? value : (byte)0;
            if (bytes[offset + i] != expected) { return false; }
        }

        return true;
    }

    /// <summary>这段待解码字节里有没有行界（<c>\n</c> 或 <c>\r</c>）。</summary>
    private static bool HasLineBreak(List<byte> bytes)
    {
        for (var i = 0; i < bytes.Count; i++)
        {
            if (bytes[i] is 10 or 13) { return true; }
        }

        return false;
    }

    private static void DetectExistingBom(FileStream stream, Cursor cursor)
    {
        var prefix = new byte[(int)Math.Min(4, stream.Length)];
        stream.Position = 0;
        var read = stream.Read(prefix, 0, prefix.Length);
        if (read != prefix.Length) { Array.Resize(ref prefix, read); }
        FindBom(prefix, out var encoding);
        cursor.BomEncoding = encoding;
        cursor.HeaderChecked = cursor.Offset > 0;
    }

    private static int FindBom(byte[] b, out Encoding? encoding)
    {
        encoding = null;
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
        { encoding = new UTF32Encoding(false, false); return 4; }
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)
        { encoding = new UTF32Encoding(true, false); return 4; }
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        { encoding = Encoding.UTF8; return 3; }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        { encoding = Encoding.Unicode; return 2; }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        { encoding = Encoding.BigEndianUnicode; return 2; }
        return 0;
    }

    private static byte[] ReadAnchor(FileStream stream, long offset)
    {
        if (offset > stream.Length) { return Array.Empty<byte>(); }
        var bytes = new byte[(int)Math.Min(64, offset)];
        stream.Position = offset - bytes.Length;
        var read = stream.Read(bytes, 0, bytes.Length);
        if (read != bytes.Length) { Array.Resize(ref bytes, read); }
        return bytes;
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private void SetError(Exception ex) => _lastError =
        $"日志读取失败：{_rootPath}：{ex.Message.Replace("异常", "错误", StringComparison.Ordinal)}";

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _files.Clear(); // FileStream 只在每拍读取期间持有，均由 using 释放。
        }
    }
}
