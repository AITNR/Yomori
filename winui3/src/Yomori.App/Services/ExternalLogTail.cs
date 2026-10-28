// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        public Encoding? BomEncoding;
        public bool HeaderChecked;
        public bool DiscardingOversizeLine;
    }

    private readonly object _sync = new();
    private readonly string _rootPath;
    private readonly string _pattern;
    private readonly string _encodingMode;
    private readonly Dictionary<string, Cursor> _files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private const int MaxReadBytesPerPoll = 256 * 1024;
    private const int MaxOutputLinesPerPoll = 200;
    private const int MaxPendingBytes = 64 * 1024;
    private string? _currentFile;
    private string _lastError = string.Empty;
    private bool _disposed;

    static ExternalLogTail() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public ExternalLogTail(string path, string? pattern, string? encodingName, bool followTail,
        string? baseDirectory = null)
    {
        var expanded = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim());
        _rootPath = expanded.Length == 0 ? string.Empty : Path.GetFullPath(expanded,
            Path.GetFullPath(baseDirectory ?? Environment.CurrentDirectory));
        _pattern = ExternalLogDefaults.NormalizePattern(pattern);
        _encodingMode = ExternalLogEncodings.Normalize(encodingName);
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

    public string? CurrentFile { get { lock (_sync) { return _currentFile; } } }
    /// <summary>最近一次诊断；成功读取后清空。由 worker 在变化/恢复时报告。</summary>
    public string LastError { get { lock (_sync) { return _lastError; } } }

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

    private IReadOnlyList<ExternalLogLine> Read(IReadOnlyList<string>? keywords, bool final)
    {
        lock (_sync)
        {
            var result = new List<ExternalLogLine>();
            if (_disposed) { return result; }
            try
            {
                var candidates = Candidates().OrderByDescending(File.GetLastWriteTimeUtc).ToList();
                if (candidates.Count == 0)
                {
                    // 不再有匹配文件时，所有现存 cursor 都属于已轮转或已删除的文件。
                    // 清理其 Pending，防止 final drain 因 HasUnreadData 重复空转。
                    _files.Clear();

                    _lastError = $"等待日志文件：{_rootPath}（匹配 {_pattern}）";
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
                            ReadFile(file, result, keywords, final, ref bytesRemaining);
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
        return (attributes & FileAttributes.Directory) != 0
            ? Directory.GetFiles(_rootPath, _pattern)
            : new[] { _rootPath };
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
            if (!final && cursor.Pending.Count < 4 && !cursor.Pending.Contains((byte)'\n')) { return; }
            var bytes = cursor.Pending.ToArray();
            var bomLength = FindBom(bytes, out var encoding);
            cursor.BomEncoding = encoding;
            cursor.HeaderChecked = true;
            if (bomLength > 0) { cursor.Pending.RemoveRange(0, bomLength); }
        }
        var width = cursor.BomEncoding?.CodePage is 1200 or 1201 ? 2 :
            cursor.BomEncoding?.CodePage is 12000 or 12001 ? 4 : 1;
        var bigEndian = cursor.BomEncoding?.CodePage is 1201 or 12001;
        var start = 0;
        for (var i = 0; i + width <= cursor.Pending.Count; i += width)
        {
            var lf = true;
            for (var b = 0; b < width; b++)
            {
                var expected = b == (bigEndian ? width - 1 : 0) ? 10 : 0;
                if (cursor.Pending[i + b] != expected) { lf = false; break; }
            }
            if (!lf) { continue; }
            AddLine(cursor, cursor.Pending.GetRange(start, i - start).ToArray(), result, keywords);
            start = i + width;
            if (result.Count >= MaxOutputLinesPerPoll) { break; }
        }
        if (start > 0) { cursor.Pending.RemoveRange(0, start); }
        // 达到批次行数上限时保留完整尾行给下一轮 Drain，不能把它们合并成一行。
        if (final && result.Count < MaxOutputLinesPerPoll && cursor.Pending.Count > 0)
        {
            AddLine(cursor, cursor.Pending.ToArray(), result, keywords);
            cursor.Pending.Clear();
        }
    }

    private void ConsumeDiscardedByte(Cursor cursor, byte value, List<ExternalLogLine> result,
        IReadOnlyList<string>? keywords)
    {
        var width = GetEncodingWidth(cursor);
        cursor.DiscardProbe.Add(value);
        if (cursor.DiscardProbe.Count < width) { return; }

        if (IsLineFeed(cursor.DiscardProbe, width, IsBigEndian(cursor)))
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
        var line = ExternalLogFilter.Parse(text.TrimEnd('\r'), keywords, wasTruncated);
        if (line.Text.Length > 0) { result.Add(line); }
    }

    private static int GetEncodingWidth(Cursor cursor) => cursor.BomEncoding?.CodePage is 1200 or 1201 ? 2 :
        cursor.BomEncoding?.CodePage is 12000 or 12001 ? 4 : 1;

    private static bool IsBigEndian(Cursor cursor) => cursor.BomEncoding?.CodePage is 1201 or 12001;

    private static bool IsLineFeed(IReadOnlyList<byte> bytes, int width, bool bigEndian)
    {
        for (var i = 0; i < width; i++)
        {
            var expected = i == (bigEndian ? width - 1 : 0) ? 10 : 0;
            if (bytes[i] != expected) { return false; }
        }

        return true;
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
