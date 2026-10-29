using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Yomori.Models;
using Yomori.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length > 0 && string.Equals(args[0], "--write-log", StringComparison.OrdinalIgnoreCase))
{
    return WriteSyntheticLog(args);
}

if (args.Length > 0 && string.Equals(args[0], "--observe", StringComparison.OrdinalIgnoreCase))
{
    return ObserveBridge(args);
}

if (args.Length > 0 && string.Equals(args[0], "--replay", StringComparison.OrdinalIgnoreCase))
{
    return ReplayExternalLog(args);
}

var tests = new (string Name, Action Run)[]
{
    ("Agent payload identity ignores patched EXE timestamp", TestAgentPayloadIdentity),
    ("Agent capability placeholder and takeover", TestAgentCapabilities),
    ("RdpBridge normalized For cache and bounded BOM status", TestRdpBridgeCacheAndStatus),
    ("RdpBridge live-log NDJSON protocol", TestRdpBridgeLiveLogs),
    ("RdpChannelSession isolation and bounded collections", TestRdpChannelSession),
    ("ExternalLogFilter normalization and classification", TestExternalLogFilter),
    ("ExternalLogTail incremental, rotation, partial lines and encoding", TestExternalLogTail),
    ("ExternalLogTail large backlog drains in order across polls", TestExternalLogTailLargeBacklog),
    ("ExternalLogTail multi-pattern, recursion and carriage-return progress",
        TestExternalLogTailPatternsRecursionAndCarriageReturn),
    ("ExternalLogProjector panel projection and label map", TestExternalLogProjector),
    ("ExternalLogTail projects real lines end to end", TestExternalLogTailProjectsRealLines),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} test groups passed.");
if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

return 0;

static int WriteSyntheticLog(string[] args)
{
    if (args.Length != 3 || string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2]))
    {
        Console.Error.WriteLine("Usage: --write-log <path> <label>");
        return 2;
    }

    var path = Path.GetFullPath(args[1]);
    var label = args[2];
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    using var writer = new StreamWriter(
        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
        new UTF8Encoding(false));
    for (var i = 1; i <= 15; i++)
    {
        writer.WriteLine($"[INFO] {label}-{i} 中文正文");
        writer.Flush();
        Thread.Sleep(200);
    }

    writer.WriteLine($"[INFO] {label}-FINAL");
    writer.Flush();
    Console.WriteLine($"WROTE path={path} label={label} rows=16");
    return 0;
}

static int ObserveBridge(string[] args)
{
    if (args.Length != 4 || !int.TryParse(args[3], out var expectedTaskCount) || expectedTaskCount < 0)
    {
        Console.Error.WriteLine("Usage: --observe <bridgeDir> <commandId> <expected task count>");
        return 2;
    }

    var bridge = new RdpBridge(args[1]);
    var session = new RdpChannelSession(null, bridge);
    session.Begin(args[2], expectedTaskCount, DateTimeOffset.Now);
    var observed = new List<RdpLiveLog>();
    var taskDoneCount = 0;
    var finishedCount = 0;
    var extLogEventCount = 0;
    var seenEventSeq = new HashSet<long>();
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
    var completed = false;

    while (DateTime.UtcNow < deadline)
    {
        var status = bridge.TryReadStatus();
        if (status is not null && string.Equals(status.CommandId, args[2], StringComparison.Ordinal))
        {
            var tick = session.Tick(status, DateTimeOffset.Now, 90);
            foreach (var ev in tick.NewEvents)
            {
                if (ev.Kind == RdpEventKinds.TaskDone)
                {
                    taskDoneCount++;
                }
                else if (ev.Kind == RdpEventKinds.Finished)
                {
                    finishedCount++;
                }
            }

            foreach (var ev in status.Events)
            {
                if (ev.Kind == RdpEventKinds.ExternalLog && seenEventSeq.Add(ev.Seq))
                {
                    extLogEventCount++;
                }
            }

            if (tick.SawFinished || status.Phase is "done" or "stopped" or "error")
            {
                completed = true;
            }
        }

        observed.AddRange(session.ConsumeLiveLogs());
        if (completed)
        {
            break;
        }

        Thread.Sleep(200);
    }

    var finalSeen = observed.Any(log => log.Message.Contains("FINAL", StringComparison.Ordinal));
    var ordinaryEventsOk = taskDoneCount == expectedTaskCount && finishedCount == 1;
    var success = completed && observed.Count > 0 && finalSeen && ordinaryEventsOk && extLogEventCount == 0;
    Console.WriteLine(
        $"OBSERVE completed={completed} observed={observed.Count} final={finalSeen} " +
        $"task_done={taskDoneCount}/{expectedTaskCount} finished={finishedCount}/1 ext_log={extLogEventCount} " +
        $"result={(success ? "PASS" : "FAIL")}");
    return success ? 0 : 1;
}

static void TestRdpBridgeLiveLogs()
{
    using var temp = new TempDirectory();
    var bridge = new RdpBridge(temp.Path);
    const string commandId = "cmd-中文-1";
    var first = new RdpLiveLog
    {
        Seq = 1,
        Time = "12:00:01",
        TaskName = "BetterGI",
        Message = "中文第一行\n第二行",
        Level = "info",
    };
    var second = new RdpLiveLog
    {
        Seq = 2,
        Time = "12:00:02",
        TaskName = "MaaEnd",
        Message = "任务完成",
        Level = "warn",
        IsAbnormal = true,
    };

    Assert(bridge.AppendLiveLogBatch(commandId, new[] { first, second }), "append initial batch");
    var offset = 0L;
    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var read), "read initial batch");
    Equal(2, read.Count, "initial row count");
    Equal(commandId, read[0].CommandId, "command id is assigned on append");
    Equal(first.Message, read[0].Message, "newline-containing message survives JSON round trip");
    Equal(second.Message, read[1].Message, "Chinese message survives JSON round trip");

    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var duplicateRead), "repeat read");
    Equal(0, duplicateRead.Count, "incremental read does not duplicate rows");

    var path = bridge.LiveLogPath(commandId);
    var half = new RdpLiveLog { CommandId = commandId, Seq = 3, Message = "半行恢复" };
    var halfJson = JsonSerializer.Serialize(half);
    File.AppendAllText(path, halfJson[..(halfJson.Length / 2)], new UTF8Encoding(false));
    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var incomplete), "read incomplete row");
    Equal(0, incomplete.Count, "incomplete JSON row is held at the byte offset");
    File.AppendAllText(path, halfJson[(halfJson.Length / 2)..] + "\n", new UTF8Encoding(false));
    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var recovered), "read completed row");
    Equal(1, recovered.Count, "completed half row is recovered");
    Equal("半行恢复", recovered[0].Message, "recovered message");

    File.AppendAllText(path, "{not-json}\n", new UTF8Encoding(false));
    var wrongCommand = JsonSerializer.Serialize(new RdpLiveLog
    {
        CommandId = "other-command",
        Seq = 99,
        Message = "不应出现",
    });
    File.AppendAllText(path, wrongCommand + "\n", new UTF8Encoding(false));
    File.AppendAllText(path, JsonSerializer.Serialize(new RdpLiveLog
    {
        CommandId = commandId,
        Seq = 4,
        Message = "坏行之后仍可读",
    }) + "\n", new UTF8Encoding(false));
    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var afterBadRows), "read after malformed rows");
    Equal(1, afterBadRows.Count, "malformed and wrong-command rows are filtered without blocking");
    Equal("坏行之后仍可读", afterBadRows[0].Message, "valid row after malformed rows");

    File.WriteAllText(path, JsonSerializer.Serialize(new RdpLiveLog
    {
        CommandId = commandId,
        Seq = 5,
        Message = "截断后重新开始",
    }) + "\n", new UTF8Encoding(false));
    Assert(bridge.TryReadLiveLogs(commandId, ref offset, out var afterTruncate), "read after truncation");
    Equal(1, afterTruncate.Count, "file truncation resets the byte cursor");
    Equal("截断后重新开始", afterTruncate[0].Message, "row after truncation");

    Assert(!bridge.TryReadLiveLogs("missing", ref offset, out var missing), "missing command file reports unavailable input");
    Equal(0, missing.Count, "missing command file yields no rows");
}

static void TestRdpBridgeCacheAndStatus()
{
    using var temp = new TempDirectory();
    var equivalentPath = Path.Combine(temp.Path, "bridge", "..", "bridge") + "   ";
    var first = RdpBridge.For(equivalentPath);
    var second = RdpBridge.For(Path.GetFullPath(Path.Combine(temp.Path, "bridge")));
    Assert(ReferenceEquals(first, second), "normalized equivalent bridge paths reuse the active cached instance");
    Directory.CreateDirectory(first.BridgeDir);

    var statusJson = JsonSerializer.Serialize(new RdpStatus
    {
        CommandId = "bom-status",
        Phase = "running",
        StatusText = "BOM 可读",
    });
    File.WriteAllText(first.StatusPath, statusJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    var status = first.TryReadStatus();
    Assert(status is not null, "UTF-8 BOM status is readable");
    Equal("bom-status", status!.CommandId, "BOM status command id survives read");
    Equal("BOM 可读", status.StatusText, "BOM status text survives read");

    File.WriteAllBytes(first.StatusPath, Encoding.UTF8.GetBytes("{\"CommandId\":\"oversized\",\"Padding\":\"" +
        new string('x', 1024 * 1024) + "\"}"));
    Assert(new FileInfo(first.StatusPath).Length > 1024 * 1024, "oversized status fixture exceeds the one MiB boundary");
    Assert(first.TryReadStatus() is null, "status over one MiB is rejected");
}
static void TestRdpChannelSession()
{
    using var temp = new TempDirectory();
    var bridge = new RdpBridge(temp.Path);
    var session = new RdpChannelSession(null, bridge);
    session.Begin("session-a", total: 1, DateTimeOffset.UtcNow);
    session.SetEventLineLimit(20);

    for (var i = 1; i <= RdpChannelSession.MaxExternalLogLines + 20; i++)
    {
        Assert(bridge.AppendLiveLogBatch("session-a", new[]
        {
            new RdpLiveLog { CommandId = "session-a", Seq = i, Message = $"日志-{i}" },
        }), $"append live row {i}");
    }

    var ordinaryEvents = new List<RdpTaskEvent>();
    for (var i = 1; i <= 23; i++)
    {
        ordinaryEvents.Add(new RdpTaskEvent
        {
            Seq = i,
            Kind = RdpEventKinds.Log,
            Message = $"ordinary-{i}",
        });
    }

    ordinaryEvents.Add(new RdpTaskEvent { Seq = 24, Kind = RdpEventKinds.TaskDone, Message = "task_done" });
    ordinaryEvents.Add(new RdpTaskEvent { Seq = 25, Kind = RdpEventKinds.Finished, Message = "finished" });
    var ordinary = new RdpStatus
    {
        CommandId = "session-a",
        Phase = "done",
        UpdatedAt = DateTimeOffset.UtcNow.ToString("o"),
        Events = ordinaryEvents,
    };
    var tick = session.Tick(ordinary, DateTimeOffset.UtcNow, 30);
    var consumed = session.ConsumeLiveLogs();
    Equal(RdpChannelSession.MaxExternalLogLines, session.ExternalLogs.Count, "live-log collection is bounded");
    Equal(20, session.Events.Count, "ordinary event collection has its independent capacity");
    Equal(25, tick.NewEvents.Count, "ordinary events remain independent of live-log volume");
    Equal(RdpEventKinds.TaskDone, tick.NewEvents[^2].Kind, "task_done remains an ordinary event");
    Equal(RdpEventKinds.Finished, tick.NewEvents[^1].Kind, "finished event survives live-log volume");
    Equal(220, consumed.Count, "all new live rows are consumed in one pass");
    Equal("日志-21", session.ExternalLogs[0].Message, "old live rows are evicted only from live collection");
    Equal("日志-220", session.ExternalLogs[^1].Message, "newest live row is retained");
    Equal(0, session.ConsumeLiveLogs().Count, "session incremental consumption does not duplicate rows");

    session.Begin("session-b", total: 1, DateTimeOffset.UtcNow);
    Assert(bridge.AppendLiveLogBatch("session-a", new[]
    {
        new RdpLiveLog { CommandId = "session-a", Seq = 220, Message = "旧指令日志" },
    }), "append old command after reset");
    Assert(bridge.AppendLiveLogBatch("session-b", new[]
    {
        new RdpLiveLog { CommandId = "session-b", Seq = 1, Message = "新指令日志" },
    }), "append new command after reset");
    var isolated = session.ConsumeLiveLogs();
    Equal(1, isolated.Count, "new command consumes only its own stream");
    Equal("新指令日志", isolated[0].Message, "command reset prevents old stream leakage");
}

static void TestExternalLogFilter()
{
    var parsed = ExternalLogFilter.Parse("2026-10-17 12:00:00 [ERROR] 中文\u001b[31m故障\u001b[0m", new[] { "故障" });
    Equal("2026-10-17 12:00:00 [ERROR] 中文故障", parsed.Text, "ANSI/control characters are removed");
    Equal(ExternalLogLevel.Error, parsed.Level, "bracket error level is classified");
    Assert(parsed.KeywordHit, "keyword hit is detected");

    var warning = ExternalLogFilter.Parse("12:00:01 | WARN | 连接超时", Array.Empty<string>());
    Equal(ExternalLogLevel.Warn, warning.Level, "pipe warning level is classified");
    var info = ExternalLogFilter.Parse("2026-10-17 12:00:02 INFO ready", Array.Empty<string>());
    Equal(ExternalLogLevel.Info, info.Level, "timestamp bare info level is classified");
    var bodyOnlyError = ExternalLogFilter.Parse("normal message: error-like text", Array.Empty<string>());
    Equal(ExternalLogLevel.Unknown, bodyOnlyError.Level, "body text does not become a level");
    Assert(ExternalLogFilter.NormalizeLine("a\n\r  b") == "a b", "line breaks and whitespace are normalized");
}

static void TestExternalLogTail()
{
    using var temp = new TempDirectory();
    var failures = new List<string>();

    void Check(string name, Action assertion)
    {
        try
        {
            assertion();
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }

    var tailDirectory = Path.Combine(temp.Path, "tail");
    Directory.CreateDirectory(tailDirectory);
    var file = Path.Combine(tailDirectory, "current.log");
    File.WriteAllText(file, "旧内容\n", new UTF8Encoding(false));

    using var tail = new ExternalLogTail(tailDirectory, "*.log", ExternalLogEncodings.Utf8, followTail: true);
    Check("follow-tail starts at current end", () => Equal(0, tail.Poll(Array.Empty<string>()).Count, "follow-tail starts at current end"));

    AppendUtf8InChunks(file, "第一条中文\n第二条\n", chunkSize: 2);
    Check("UTF-8 byte chunks produce complete incremental lines", () =>
    {
        var first = tail.Poll(Array.Empty<string>());
        Equal(2, first.Count, "UTF-8 byte chunks produce complete incremental lines");
        Equal("第一条中文", first[0].Text, "UTF-8 Chinese text is decoded");
    });

    var bytewiseFile = Path.Combine(temp.Path, "bytewise.log");
    File.WriteAllText(bytewiseFile, string.Empty, new UTF8Encoding(false));
    using var bytewiseTail = new ExternalLogTail(bytewiseFile, "*.log", ExternalLogEncodings.Utf8, followTail: false);
    var bytewiseText = Encoding.UTF8.GetBytes("跨Poll中文\n");
    for (var i = 0; i < bytewiseText.Length; i++)
    {
        using (var stream = new FileStream(bytewiseFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.WriteByte(bytewiseText[i]);
            stream.Flush();
        }

        var rows = bytewiseTail.Poll(Array.Empty<string>());
        if (i < bytewiseText.Length - 1)
        {
            Equal(0, rows.Count, $"bytewise UTF-8 poll {i + 1} waits for newline");
        }
        else
        {
            Equal(1, rows.Count, "bytewise UTF-8 polling emits one complete line");
            Equal("跨Poll中文", rows[0].Text, "UTF-8 decoder preserves characters across Poll calls");
        }
    }

    var halfFile = Path.Combine(temp.Path, "half.log");
    File.WriteAllText(halfFile, "半行", new UTF8Encoding(false));
    using var halfTail = new ExternalLogTail(halfFile, "*.log", ExternalLogEncodings.Utf8, followTail: false);
    Check("tail waits for a complete line", () => Equal(0, halfTail.Poll(Array.Empty<string>()).Count, "tail waits for a complete line"));
    File.AppendAllText(halfFile, "完成\n", new UTF8Encoding(false));
    Check("half line is emitted after newline arrives", () =>
    {
        var completed = halfTail.Poll(Array.Empty<string>());
        Equal(1, completed.Count, "half line is emitted after newline arrives");
        Equal("半行完成", completed[0].Text, "half-line text is preserved");
    });

    var rotationDir = Path.Combine(temp.Path, "rotation");
    Directory.CreateDirectory(rotationDir);
    var oldRotated = Path.Combine(rotationDir, "old.log");
    File.WriteAllText(oldRotated, "旧轮转内容\n", new UTF8Encoding(false));
    using var rotationTail = new ExternalLogTail(rotationDir, "*.log", ExternalLogEncodings.Utf8, followTail: true);
    var rotated = Path.Combine(rotationDir, "rotated.log");
    File.WriteAllText(rotated, "轮转后\n", new UTF8Encoding(false));
    Touch(rotated, DateTime.UtcNow.AddMinutes(1));
    Check("newest matching file is followed after rotation", () =>
    {
        var afterRotation = rotationTail.Poll(Array.Empty<string>());
        Equal(1, afterRotation.Count, "newest matching file is followed after rotation");
        Equal("轮转后", afterRotation[0].Text, "rotated file content is read");
    });

    File.WriteAllText(rotated, "重写\n", new UTF8Encoding(false));
    Touch(rotated, DateTime.UtcNow.AddMinutes(2));
    Check("truncated/recreated content is reread", () =>
    {
        var afterRewrite = rotationTail.Poll(Array.Empty<string>());
        Equal(1, afterRewrite.Count, "truncated/recreated content is reread");
        Equal("重写", afterRewrite[0].Text, "rewritten file content is read");
    });

    var gbkFile = Path.Combine(temp.Path, "gbk.log");
    File.WriteAllText(gbkFile, "启动中文\n", Encoding.GetEncoding("GBK"));
    using var gbkTail = new ExternalLogTail(gbkFile, "*.log", ExternalLogEncodings.Gbk, followTail: false);
    Check("explicit GBK file is read", () =>
    {
        var gbk = gbkTail.Poll(Array.Empty<string>());
        Equal(1, gbk.Count, "explicit GBK file is read");
        Equal("启动中文", gbk[0].Text, "GBK Chinese text is decoded");
    });

    if (failures.Count > 0)
    {
        throw new InvalidOperationException(string.Join(" | ", failures));
    }
}

static void TestExternalLogTailPatternsRecursionAndCarriageReturn()
{
    using var temp = new TempDirectory();
    var failures = new List<string>();

    void Check(string name, Action assertion)
    {
        try
        {
            assertion();
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }

    var root = Path.Combine(temp.Path, "logs");
    var runDir = Path.Combine(root, "20261029_120000");
    var deepDir = Path.Combine(root, "a", "b", "c", "d");
    Directory.CreateDirectory(runDir);
    Directory.CreateDirectory(deepDir);

    var utf8 = new UTF8Encoding(false);
    File.WriteAllText(Path.Combine(root, "top.log"), "顶层一行\n", utf8);
    File.WriteAllText(Path.Combine(runDir, "run.log"), "子目录一行\n", utf8);
    File.WriteAllText(Path.Combine(deepDir, "deep.log"), "太深的一行\n", utf8);
    File.WriteAllText(Path.Combine(root, "progress.log"), "10%\r20%\r30%\n完成\r\n", utf8);

    // 多模式：分号分隔逐个匹配；不存在的模式不该让整次采集失败
    using (var multi = new ExternalLogTail(root, "top.log;missing.log", ExternalLogEncodings.Utf8, followTail: false))
    {
        var lines = multi.Poll(Array.Empty<string>());
        Check("multi-pattern collects matched file", () => Equal(1, lines.Count, "multi-pattern count"));
        Check("multi-pattern content", () => Equal("顶层一行", lines[0].Text, "multi-pattern text"));
    }

    // 递归关（默认）：子目录里的 run.log 采不到
    using (var flat = new ExternalLogTail(root, "run.log", ExternalLogEncodings.Utf8, followTail: false))
    {
        Check("recursive off skips subdirectories", () => Equal(0, flat.Poll(Array.Empty<string>()).Count, "flat"));
    }

    // 递归开：进一层能找到；超过 3 层的丢掉
    using (var recursed = new ExternalLogTail(root, "run.log;deep.log", ExternalLogEncodings.Utf8,
               followTail: false, baseDirectory: null, recursive: true))
    {
        var lines = recursed.Poll(Array.Empty<string>());
        Check("recursive on finds nested file", () => Equal(1, lines.Count, "recursive count"));
        Check("recursive on content", () => Equal("子目录一行", lines[0].Text, "recursive text"));
    }

    // \r 进度行：只保留最后一段；\r\n 当普通换行
    using (var progress = new ExternalLogTail(Path.Combine(root, "progress.log"), null,
               ExternalLogEncodings.Utf8, followTail: false))
    {
        var lines = progress.Poll(Array.Empty<string>());
        Check("progress segments collapse", () =>
            Equal(2, lines.Count, "progress count: " + string.Join("|", lines.Select(l => l.Text))));
        Check("progress keeps last segment", () => Equal("30%", lines[0].Text, "progress first"));
        Check("crlf line stays intact", () => Equal("完成", lines[1].Text, "progress second"));
    }

    // 收尾时还没换行的进度段要补出来（否则最后一步的进度永远看不到）
    var tailFile = Path.Combine(root, "tailprogress.log");
    File.WriteAllText(tailFile, "building\r50%", utf8);
    using (var partial = new ExternalLogTail(tailFile, null, ExternalLogEncodings.Utf8, followTail: false))
    {
        partial.Poll(Array.Empty<string>());
        var drained = partial.Drain(Array.Empty<string>());
        Check("pending progress flushed on drain",
            () => Assert(drained.Any(l => l.Text == "50%"),
                "drain: " + string.Join("|", drained.Select(l => l.Text))));
    }

    if (failures.Count > 0)
    {
        throw new InvalidOperationException(string.Join(" | ", failures));
    }
}

static void AppendUtf8InChunks(string path, string text, int chunkSize)
{
    var bytes = Encoding.UTF8.GetBytes(text);
    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    for (var offset = 0; offset < bytes.Length; offset += chunkSize)
    {
        var count = Math.Min(chunkSize, bytes.Length - offset);
        stream.Write(bytes, offset, count);
        stream.Flush();
    }
}

static void Touch(string path, DateTime utc)
{
    File.SetLastWriteTimeUtc(path, utc);
}

// ------------------------------------------------------------------ 投影器（面板档）

/// <summary>项目资源（含注释的任务文件 + i18n 词条）→ entry 中文名的解析。</summary>
const string InterfaceJson = """
{
  "name": "Demo",
  // 顶层的 task 直接写在这里，import 指向的再补充
  "task": [ { "entry": "GameLauncherWakeMain", "name": "GameLauncherWakeMain" } ],
  "import": [ "tasks/visit.json" ]
}
""";

/// <summary>实测 MAA_bbb 的 tasks/游戏启动.json 里就有这种 `//` 注释与尾逗号。</summary>
const string TaskJsonWithComments = """
{
  "task": [
    {
      "name": "VisitFriends",
      "label": "$task.VisitFriends.label",
      "entry": "VisitFriendsMain",   // 尾逗号 + 行内注释
    }
  ]
}
""";

const string LocaleJson = """{ "task.VisitFriends.label": "🤝拜访好友" }""";

const string TaskStartLine =
    "[13:17:13] [2026-10-29 13:17:13.190][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
    "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Tasker.Task.Starting] " +
    "[details={\"entry\":\"VisitFriendsMain\",\"task_id\":200000001}]";

const string TaskDoneLine =
    "[13:19:44] [2026-10-29 13:19:44.100][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
    "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Tasker.Task.Succeeded] " +
    "[details={\"entry\":\"VisitFriendsMain\",\"task_id\":200000001}]";

/// <summary>focus 的键等于当前事件 —— 唯一会显示的那种。</summary>
const string FocusLine =
    "[13:18:00] [2026-10-29 13:18:00.190][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
    "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Node.Action.Succeeded] " +
    "[details={\"focus\":{\"Node.Action.Succeeded\":\"<div style=\\\"color:#00bfff\\\">获得</div>\"}," +
    "\"name\":\"VisitFriendsQuickVisitNext\"}]";

/// <summary>同一行换个事件名：focus 的键不匹配，一个字符都不该输出。</summary>
const string FocusMismatchedLine =
    "[13:18:00] [2026-10-29 13:18:00.190][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
    "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Node.Recognition.Starting] " +
    "[details={\"focus\":{\"Node.Action.Succeeded\":\"获得\"},\"name\":\"VisitFriendsQuickVisitNext\"}]";

const string NodeLine =
    "[13:18:22] [2026-10-29 13:18:22.190][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
    "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Node.PipelineNode.Starting] " +
    "[details={\"focus\":null,\"name\":\"VisitFriendsMenuAssistControlNexus\",\"node_id\":300000001}]";

/// <summary>`Succeeded` 的 payload 以 action_details 打头：首个 name 是动作名，不是节点名。</summary>
const string NodeSucceededLine =
    "[13:19:44] [2026-10-29 13:19:44.190][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
    "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Node.PipelineNode.Succeeded] " +
    "[details={\"action_details\":{\"action\":\"DoNothing\",\"name\":\"DoNothing\"}," +
    "\"focus\":null,\"name\":\"VisitFriendsMain\"}]";

const string InternalWarnLine =
    "[13:22:41] [2026-10-29 13:22:41.190][WRN][Px82844][Tx54350][PipelineTask.cpp][L155]" +
    "[MaaNS::TaskNS::PipelineTask::run_next] node not completed, handle error " +
    "[node_name=DeliveryJobsContinueFillWulingAfterPriority1]";

const string NoiseLine =
    "[13:04:40] [2026-10-29 13:04:40.190][WRN][Px82844][Tx8661][OCRer.cpp][L139]" +
    "[MaaNS::VisionNS::OCRer::handle_cached] cache is empty [name=x]";

/// <summary>同一份归档里混进来的别的进程的行：只有类名、没有消息体。</summary>
const string ForeignLine =
    "[13:12:47] [13:12:46.935] [INF] [Primary:S3:P75124:T1793250766610] BetterGenshinImpact.Service.Instance";

/// <summary>外壳告警：带 Yomori 自己的 `[警告]` 前缀，剥完才是消息。</summary>
const string ShellWarnLine =
    "[13:03:56] [警告] [2026-10-29 13:03:56,142][WARNING][i18n_service.py][L80]" +
    "[load_translations_from_interface] | 未找到语言 zh_cn 的翻译文件配置";

/// <summary>三月七那种"本来就是人话"的日志：一条都不许投影掉。</summary>
const string March7thLine = "[13:04:50] 当前界面：手机菜单";

/// <summary>断言辅助（参数顺序：说明在前）。本地函数套一层，避免和上面的 Equal 混淆。</summary>
static void Same(string message, object? expected, object? actual)
{
    if (!Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}; expected '{expected}', actual '{actual}'");
    }
}

void TestExternalLogProjector()
{
    // ---- 1. 项目资源：注释 / 尾逗号 / i18n 键
    var labels = LogLabelMap.Build(InterfaceJson, new[] { TaskJsonWithComments }, new[] { LocaleJson });
    Same("label：i18n 键被解析成中文", "🤝拜访好友", labels.Resolve("VisitFriendsMain"));
    Same("label：无 label 时用 name", "GameLauncherWakeMain", labels.Resolve("GameLauncherWakeMain"));
    Same("label：查不到就原样返回 entry", "UnknownEntry", labels.Resolve("UnknownEntry"));
    Same("label：注释与尾逗号不影响解析", 2, labels.Count);

    // ---- 2. 任务边界
    var projector = new ExternalLogProjector(labels, maafw: true);
    long now = 1_000_000;
    var start = projector.Project(TaskStartLine, now);
    Assert(start.Emit, "任务开始：不该被丢弃");
    Same("任务开始：面板用中文名", "▶ 任务开始：🤝拜访好友", start.PanelText);
    Same("任务开始：节点档用 entry", "▶ 任务 VisitFriendsMain", start.NodeText);
    Same("任务开始：类型", ExternalLogKind.TaskStart, start.Kind);

    // 同一 entry 未结束就重发（MaaFW 会重发 Starting）→ 丢弃
    var duplicate = projector.Project(TaskStartLine, now + 200);
    Assert(!duplicate.Emit, "任务开始：未结束的重发应丢弃");

    // ---- 3. focus 只有"键等于当前事件"才显示
    Assert(!projector.Project(FocusMismatchedLine, now + 300).Emit, "focus：键不匹配时不该输出");
    var focus = projector.Project(FocusLine, now + 400);
    Assert(focus.Emit, "focus：命中时应输出");
    Same("focus：剥掉 HTML 标签", "获得", focus.PanelText);
    Same("focus：类型", ExternalLogKind.Notice, focus.Kind);

    // 连续同一条 focus（双线程各打一遍）→ 只留一条
    Assert(!projector.Project(FocusLine, now + 500).Emit, "focus：连续重复应丢弃");

    // ---- 4. 已经有 focus 的任务里，节点名不再上屏（否则行数翻倍）
    var nodeInFocusTask = projector.Project(NodeLine, now + 600);
    Assert(nodeInFocusTask.Emit, "节点：节点档必须保留");
    Same("节点：面板档在有 focus 后不显示", "", nodeInFocusTask.PanelText);
    Same("节点：节点档缩进两格", "  ▸ VisitFriendsMenuAssistControlNexus", nodeInFocusTask.NodeText);

    // ---- 5. `Succeeded` 按 key 取节点名，不能取首个 name
    var succeeded = projector.Project(NodeSucceededLine, now + 700);
    Assert(!succeeded.Emit, "节点完成：当前实现只计数，不上屏");

    // ---- 6. 中间层 WRN：不单独出，作为失败原因
    var internalWarn = projector.Project(InternalWarnLine, now + 800);
    Assert(!internalWarn.Emit, "中间层 WRN：不该单独出行");
    var failed = new ExternalLogProjector(labels, maafw: true);
    failed.Project(TaskStartLine, now);
    failed.Project(InternalWarnLine, now + 10);
    var failLine =
        "[13:22:42] [2026-10-29 13:22:42.100][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
        "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Tasker.Task.Failed] " +
        "[details={\"entry\":\"VisitFriendsMain\"}]";
    var failure = failed.Project(failLine, now + 1000);
    Same("任务失败：带原因", "✘ 任务失败：🤝拜访好友（1s） · node not completed, handle error",
        failure.PanelText);
    Same("任务失败：类型", ExternalLogKind.TaskFail, failure.Kind);

    // ---- 7. 噪声与异种进程行直接丢
    Assert(!projector.Project(NoiseLine, now + 900).Emit, "噪声：cache is empty 应丢弃");
    Assert(!projector.Project(ForeignLine, now + 910).Emit, "异种进程行：应丢弃");

    // ---- 8. 外壳告警：剥掉 Yomori 的级别前缀，只留消息
    var shell = projector.Project(ShellWarnLine, now + 920);
    Assert(shell.Emit, "外壳告警：应保留");
    Same("外壳告警：去掉前缀", "未找到语言 zh_cn 的翻译文件配置", shell.PanelText);

    // ---- 9. 没有任何 focus 的任务里，节点名按 30 秒节流上屏
    var flat = new ExternalLogProjector(labels, maafw: true);
    flat.Project(TaskStartLine, now);
    var firstNode = flat.Project(NodeLine, now + 1000);
    Same("节点（无 focus）：面板显示节点名", "▸ VisitFriendsMenuAssistControlNexus", firstNode.PanelText);
    var nextNode =
        "[13:18:40] [2026-10-29 13:18:40.190][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
        "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Node.PipelineNode.Starting] " +
        "[details={\"focus\":null,\"name\":\"SceneEnterMenuFriendsList\"}]";
    var throttled = flat.Project(nextNode, now + 2000);
    Same("节点（无 focus）：30 秒内的第二个节点不再上屏", "", throttled.PanelText);
    Assert(throttled.Emit, "节点（无 focus）：节流只影响面板档，节点档仍保留");
    var reopened = flat.Project(nextNode, now + 40_000);
    Same("节点（无 focus）：过了节流窗口再出一行", "▸ SceneEnterMenuFriendsList", reopened.PanelText);

    // ---- 10. 内部节点（`__` 开头）不进面板
    var internalNode =
        "[13:18:50] [2026-10-29 13:18:50.190][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
        "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Node.PipelineNode.Starting] " +
        "[details={\"focus\":null,\"name\":\"__ScenePrivateMapTeleportConfirm\"}]";
    var internalProjection = flat.Project(internalNode, now + 80_000);
    Same("内部节点：面板档不显示", "", internalProjection.PanelText);
    Assert(internalProjection.Emit, "内部节点：节点档仍保留");

    // ---- 11. 心跳：60 秒起跳，最多 6 条
    var beating = new ExternalLogProjector(labels, maafw: true);
    beating.Project(TaskStartLine, now);
    Assert(beating.Tick(now + 30_000) is null, "心跳：不足 60 秒不该出");
    var beat = beating.Tick(now + 61_000);
    Assert(beat is not null, "心跳：超过 60 秒应出一条");
    Same("心跳：类型", ExternalLogKind.Heartbeat, beat!.Kind);
    for (var i = 0; i < 20; i++) { beating.Tick(now + 62_000 + i * 40_000); }
    Assert(beating.Tick(now + 3_000_000) is null, "心跳：单任务最多 6 条，之后保持安静");

    // ---- 12. 非 MaaFW 日志：一条都不许丢（三月七 / BetterGI / AUTO-MAS 全靠这条）
    var passthrough = new ExternalLogProjector(labels);
    var plain = passthrough.Project(March7thLine, now);
    Assert(plain.Emit, "非 MaaFW：不能丢行");
    Assert(plain.UseRaw, "非 MaaFW：原样放行");
    Assert(!passthrough.IsMaafw, "非 MaaFW：判定为不认识");

    // ---- 13. 幂等：把面板档的输出再喂一次，必须原样放行（主控端二次投影的前提）
    var again = new ExternalLogProjector(labels, maafw: true);
    var twice = again.Project(start.PanelText, now + 2000);
    Assert(twice.Emit && twice.UseRaw, "幂等：已投影的行再投影应原样放行");

    // ---- 14. 视图取值归一
    Same("视图：非法值回落面板", ExternalLogViews.Panel, ExternalLogViews.Normalize("nonsense"));
    Same("视图：大小写无关", ExternalLogViews.Node, ExternalLogViews.Normalize("NODE"));
}

/// <summary>
/// 采集器接线检查：真实文件 → <see cref="ExternalLogTail"/> → 投影 → 行。
///
/// <para>投影器自身的断言在上一组；这里要钉的是"它到底有没有被接进去"——
/// 单元测试全绿但 Tail 忘了调投影，界面照样是一堆英文，这类缺口只能靠端到端用例抓。</para>
/// </summary>
void TestExternalLogTailProjectsRealLines()
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "maafw.log");

    // ⚠ 这三行是**源文件形状**（没有 [HH:mm:ss] 前缀）。实时采集读到的就是它；
    // 加上前缀反而是归档形状。早期版本只认带前缀的那种，于是线上一条都没投影。
    const string startLine =
        "[2026-10-29 13:17:13.190][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
        "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Tasker.Task.Starting] " +
        "[details={\"entry\":\"VisitFriendsMain\"}]";
    const string noiseLine =
        "[2026-10-29 13:17:14.190][WRN][Px82844][Tx8661][OCRer.cpp][L139]" +
        "[MaaNS::VisionNS::OCRer::handle_cached] cache is empty [name=x]";
    const string focusLine =
        "[2026-10-29 13:17:15.190][INF][Px82844][Tx54350][Utils/EventDispatcher.hpp][L65]" +
        "[MaaNS::EventDispatcher::notify] !!!OnEventNotify!!! [handle=true] [msg=Node.Action.Succeeded] " +
        "[details={\"focus\":{\"Node.Action.Succeeded\":\"画面检查通过\"},\"name\":\"X\"}]";

    File.WriteAllLines(path, new[] { startLine, noiseLine, focusLine }, new UTF8Encoding(false));

    using var tail = new ExternalLogTail(path, "*.log", ExternalLogEncodings.Utf8, followTail: false,
        baseDirectory: temp.Path);
    var lines = tail.Poll(Array.Empty<string>());

    Same("接线：两行有效（任务开始 + 提示），噪声被丢", 2, lines.Count);
    // 本用例的临时目录里没有 interface.json，所以任务名只能是 entry —— 这里钉"投影确实生效"
    Same("接线：任务开始被投影", "▶ 任务开始：VisitFriendsMain", lines[0].Text);
    Same("接线：focus 变成人话", "画面检查通过", lines[1].Text);
    Same("接线：类型", ExternalLogKind.Notice, lines[1].Kind);
    // 归档按设计写**节点档**（"跑到哪一步断了"），所以这里必须是 entry 而不是中文任务名
    Same("接线：归档走节点档", "▶ 任务 VisitFriendsMain", lines[0].ArchiveText);
}

/// <summary>
/// 拿真实归档重放一遍投影（P0 验收判据 1 / 3 的可执行版本）。
///
/// <para>用法：<c>--replay &lt;日志&gt; &lt;项目目录|-&gt; [输出]</c>。
/// 时间取每行自带的 `[HH:mm:ss]` 前缀 —— 节流与心跳都按真实时间推进，
/// 而不是"每行加 1 秒"那种假时钟。</para>
/// </summary>
static int ReplayExternalLog(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: --replay <logPath> <projectDir|-> [outPath]");
        return 2;
    }

    var logPath = args[1];
    var projectDir = args[2];
    var outPath = args.Length > 3 ? args[3] : logPath + ".panel.log";
    if (!File.Exists(logPath))
    {
        Console.Error.WriteLine($"log not found: {logPath}");
        return 2;
    }

    var labels = projectDir == "-" ? LogLabelMap.Empty : LogLabelStore.Get(projectDir);

    // 与采集器同一套判定：先看文件开头，再逐行兜底
    using var head = File.OpenRead(logPath);
    var probe = new byte[512 * 1024];
    var read = head.Read(probe, 0, probe.Length);
    var maafw = ExternalLogGrammar.DetectMaafw(Encoding.Latin1.GetString(probe, 0, read));
    Console.WriteLine($"labels={labels.Count} maafw={maafw}");

    var projector = new ExternalLogProjector(labels, maafw);
    var output = new List<string>(4096);
    long lastHms = 0;
    var dropped = 0;
    var raw = 0;
    var panel = 0;

    // 两种输入都要能喂：归档行（带 [HH:mm:ss] 前缀）与源文件行（MaaFW 原生形状）。
    // 源文件行的形状是 `[2026-10-29 18:31:45.556][INF]…`，时间从行首的日期时间里取。
    var sourceShape = false;
    foreach (var line in File.ReadLines(logPath))
    {
        var match = ExternalLogGrammar.ArchivePrefix.Match(line);
        if (!match.Success)
        {
            if (!sourceShape) { sourceShape = true; }
            var stamp = System.Text.RegularExpressions.Regex.Match(line,
                @"^\[\d{4}-\d{2}-\d{2} (\d{2}):(\d{2}):(\d{2})");
            if (!stamp.Success) { dropped++; continue; }

            var sourceNow = long.Parse(stamp.Groups[1].Value) * 3600_000 +
                            long.Parse(stamp.Groups[2].Value) * 60_000 +
                            long.Parse(stamp.Groups[3].Value) * 1000;
            if (sourceNow < lastHms) { sourceNow += 24 * 3600_000; }
            lastHms = sourceNow;

            var projected = projector.Project(line, sourceNow);
            if (!projected.Emit) { dropped++; continue; }
            if (projected.UseRaw) { raw++; output.Add(line); continue; }
            if (projected.PanelText.Length == 0) { dropped++; continue; }
            panel++;
            output.Add($"{stamp.Groups[1].Value}:{stamp.Groups[2].Value}:{stamp.Groups[3].Value}  {projected.PanelText}");
            continue;
        }

        var hms = match.Groups["hms"].Value;
        var now = hms.Length == 8
            ? long.Parse(hms[..2]) * 3600_000 +
              long.Parse(hms.Substring(3, 2)) * 60_000 + long.Parse(hms.Substring(6, 2)) * 1000
            : lastHms + 1000;
        if (now < lastHms) { now += 24 * 3600_000; }   // 跨零点
        lastHms = now;

        var projection = projector.Project(line, now);
        if (!projection.Emit) { dropped++; continue; }
        if (projection.UseRaw)
        {
            raw++;
            output.Add(line);
            continue;
        }

        if (projection.PanelText.Length == 0) { dropped++; continue; }
        panel++;
        output.Add($"{hms}  {projection.PanelText}");

        // 心跳 / 空档判定：采集器每拍都会调，重放时按行时间补上
        var beat = projector.Tick(now);
        if (beat is not null && beat.PanelText.Length > 0)
        {
            panel++;
            output.Add($"{hms}  {beat.PanelText}");
        }
    }

    File.WriteAllLines(outPath, output, new UTF8Encoding(false));
    Console.WriteLine($"shape={(sourceShape ? "source" : "archive")} lines={File.ReadLines(logPath).Count()} " +
                      $"panel={panel} raw={raw} dropped={dropped} out={output.Count}");
    Console.WriteLine($"written: {outPath}");
    return 0;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}; expected '{expected}', actual '{actual}'");
    }
}

static void TestExternalLogTailLargeBacklog()
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "backlog.log");
    const int lineCount = 4501;
    using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(false)))
    {
        for (var i = 0; i < lineCount; i++)
        {
            writer.WriteLine($"[INFO] backlog-{i:D4}");
        }
    }

    using var tail = new ExternalLogTail(path, "*.log", ExternalLogEncodings.Utf8, followTail: false);
    var seen = new List<string>(lineCount);
    for (var poll = 0; poll < 100 && seen.Count < lineCount; poll++)
    {
        var rows = tail.Poll(Array.Empty<string>());
        Assert(rows.Count <= 200, $"poll {poll + 1} respects the bounded output batch");
        seen.AddRange(rows.Select(row => row.Text));
    }

    Equal(lineCount, seen.Count, "large backlog is completely drained across multiple polls");
    for (var i = 0; i < lineCount; i++)
    {
        Equal($"[INFO] backlog-{i:D4}", seen[i], $"backlog row {i} remains ordered and is not duplicated");
    }

    Equal(0, tail.Poll(Array.Empty<string>()).Count, "poll after complete backlog drain does not duplicate rows");
}
static void TestExternalLogTailOversizeAndUtf16Utf32()
{
    using var temp = new TempDirectory();
    var oversizePath = Path.Combine(temp.Path, "oversize.log");
    var prefix = "[ERROR] " + new string('x', 120_000);
    File.WriteAllText(oversizePath, prefix, new UTF8Encoding(false));
    using var oversizeTail = new ExternalLogTail(oversizePath, "*.log", ExternalLogEncodings.Utf8, followTail: false);
    var first = oversizeTail.Poll(Array.Empty<string>());
    Equal(0, first.Count, "an incomplete oversized line remains pending until its newline");
    Assert(prefix.Length > 64 * 1024, "oversized line fixture exceeds the pending bound");

    File.AppendAllText(oversizePath, "\n[INFO] after-oversize\n", new UTF8Encoding(false));
    var recovered = oversizeTail.Poll(Array.Empty<string>());
    Equal(2, recovered.Count, "oversized line and its following normal line are both recovered");
    Assert(recovered[0].Text.Contains("日志行原始字节超限", StringComparison.Ordinal), "oversized line is explicitly marked as truncated");
    Equal("[INFO] after-oversize", recovered[1].Text, "normal line after oversized input is not swallowed");

    AssertEncodingInSingleByteChunks(temp.Path, "utf16le.log", new UnicodeEncoding(false, true), "utf16le-一", "utf16le-二");
    AssertEncodingInSingleByteChunks(temp.Path, "utf16be.log", new UnicodeEncoding(true, true), "utf16be-一", "utf16be-二");
    AssertEncodingInSingleByteChunks(temp.Path, "utf32le.log", new UTF32Encoding(false, true), "utf32le-一", "utf32le-二");
    AssertEncodingInSingleByteChunks(temp.Path, "utf32be.log", new UTF32Encoding(true, true), "utf32be-一", "utf32be-二");
}

static void AssertEncodingInSingleByteChunks(string directory, string fileName, Encoding encoding,
    string firstExpected, string secondExpected)
{
    var path = Path.Combine(directory, fileName);
    var bytes = encoding.GetBytes(firstExpected + "\n" + secondExpected + "\n");
    File.WriteAllBytes(path, Array.Empty<byte>());
    using var tail = new ExternalLogTail(path, "*.log", ExternalLogEncodings.Auto, followTail: false);
    var seen = new List<string>();
    for (var i = 0; i < bytes.Length; i++)
    {
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.WriteByte(bytes[i]);
            stream.Flush();
        }
        var rows = tail.Poll(Array.Empty<string>());
        seen.AddRange(rows.Select(row => row.Text));
    }

    Equal(2, seen.Count, $"{fileName} emits both complete lines from bytewise chunks");
    Equal(firstExpected, seen[0], $"{fileName} decodes first line");
    Equal(secondExpected, seen[1], $"{fileName} decodes second line");
}
static void TestAgentPayloadIdentity()
{
    using var temp = new TempDirectory();
    var source = Path.Combine(temp.Path, "source");
    var target = Path.Combine(temp.Path, "target");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(target);
    var sourceExe = Path.Combine(source, "Yomori.exe");
    var targetExe = Path.Combine(target, "Yomori.exe");
    File.WriteAllText(sourceExe, "original manifest");
    File.WriteAllText(targetExe, "asInvoker patched manifest");
    File.SetLastWriteTimeUtc(targetExe, DateTime.UtcNow.AddDays(1));
    foreach (var file in new[] { "Yomori.dll", "Yomori.deps.json", "Yomori.runtimeconfig.json", "Yomori.pri" })
    {
        File.WriteAllText(Path.Combine(source, file), "new build");
        File.WriteAllText(Path.Combine(target, file), "new build");
    }
    Assert(AgentPayloadIdentity.Matches(sourceExe, targetExe), "manifest differences do not prevent reuse of identical managed payload");
    File.WriteAllText(Path.Combine(target, "Yomori.dll"), "old build");
    Assert(!AgentPayloadIdentity.Matches(sourceExe, targetExe), "newer patched EXE cannot hide same-size stale DLL");
    File.WriteAllText(Path.Combine(target, "Yomori.dll"), "new build");
    File.WriteAllText(Path.Combine(target, "Yomori.pri"), "old build");
    Assert(!AgentPayloadIdentity.Matches(sourceExe, targetExe), "stale PRI prevents reuse");
    File.Delete(Path.Combine(target, "Yomori.pri"));
    Assert(!AgentPayloadIdentity.Matches(sourceExe, targetExe), "missing payload prevents reuse");
}

static void TestAgentCapabilities()
{
    using var temp = new TempDirectory();
    var bridge = new RdpBridge(temp.Path);
    var session = new RdpChannelSession(null, bridge);
    var now = DateTimeOffset.Now;
    session.Begin("capability", 1, now);
    bridge.ResetStatus("capability");
    session.Tick(bridge.TryReadStatus()!, now, 90);
    Assert(session.LiveLogStatusText.Contains("等待代理"), "controller placeholder is not an old-agent verdict");
    var status = bridge.TryReadStatus()!;
    status.AgentUser = "test-agent";
    status.LiveLogProtocol = RdpLiveLogProtocol.Current;
    session.Tick(status, now, 90);
    Assert(session.LiveLogStatusText.Contains("正在接收"), "new agent capability is accepted in idle state");
    status.LiveLogProtocol = 0;
    session.Tick(status, now, 90);
    Assert(session.LiveLogStatusText.Contains("未声明"), "legacy agent remains distinguishable");
}


sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Yomori.LogChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Keep test cleanup best-effort; assertion failures remain the signal.
        }
    }
}
