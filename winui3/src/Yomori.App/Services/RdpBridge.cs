// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 主控端与目标会话 Agent 之间的通信桥（**每通道一个实例**）。
///
/// 两个 Windows 用户会话之间不能直接通信，因此统一走共享目录下的一组文件：
///   command.json  主控端写、Agent 读（要执行的任务快照）
///   status.json   Agent 写、主控端读（进度 + 事件流）
///   stop.json     主控端写、Agent 读（请求停止 / 紧急停止）
///   stats.json    Agent 写、主控端读（本轮跑完后发布的耗时统计快照）
/// 状态文件写入走"临时文件 + 原子替换"；实时日志使用独立追加式 NDJSON，
/// 读取容忍并发 IOException 并重试，
/// 避免主控端正在读时 Agent 正好重写导致解析到半个文件。
///
/// 为什么改成实例类：多会话通道并行时，每个通道必须有自己的一座桥
/// （同一个 command.json 会被多个代理抢读，任务就串了）。
/// 目录由 <see cref="RdpChannelPaths"/> 按通道/用户名派生，两端各自推算出同一个路径。
/// </summary>
public sealed class RdpBridge
{
    private const int MaxEvents = 500;
    private const int ReadRetry = 5;
    private const int MaxLiveLogReadBytes = 512 * 1024;
    // Keep the historical session contract: a normal 220-row burst is consumed in one tick.
    // The 512 KiB byte limit remains the primary backlog/allocation boundary.
    private const int MaxLiveLogLinesPerRead = 500;
    private const int MaxStatusReadBytes = 1024 * 1024;
    private const int CacheScavengeInterval = 64;

    /// <summary>耗时统计快照的文件名（Agent 写、主控端读）。</summary>
    public const string StatsFileName = "stats.json";
    public const string LiveLogFilePrefix = "live-log-";
    public const string LiveLogFileSuffix = ".ndjson";

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, WeakReference<RdpBridge>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static int _cacheAccesses;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _liveLogWriteGate = new();
    private readonly object _statusWriteGate = new();
    private static readonly JsonSerializerOptions LiveLogOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>建一座指向指定目录的桥。目录选取请优先用 <see cref="For"/> / <see cref="ForChannel"/>。</summary>
    public RdpBridge(string directory)
    {
        BridgeDir = NormalizeBridgeDirectory(directory);
    }

    /// <summary>本座桥的共享目录。</summary>
    public string BridgeDir { get; }

    /// <summary>是否用了自定义目录（不是按当前账户派生的默认目录）。</summary>
    public bool IsCustomized =>
        !string.Equals(
            BridgeDir,
            NormalizeBridgeDirectory(null),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>最近一次写入失败的原因，用于界面提示与排障。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// 本桥所属通道的标识，用于外部日志分目录。
    ///
    /// 取桥目录的最后一段 —— 它要么是按账户名派生的（<c>zdh</c>），
    /// 要么是自定义桥目录的末段，两端算出同一个名字，天然一致。
    /// 取不到就回落 <c>default</c>，保证路径拼接永远有值。
    /// </summary>
    public string ChannelTag
    {
        get
        {
            try
            {
                var name = new DirectoryInfo(BridgeDir).Name;
                return string.IsNullOrWhiteSpace(name) ? "default" : name;
            }
            catch
            {
                return "default";
            }
        }
    }

    public string CommandPath => Path.Combine(BridgeDir, "command.json");

    public string StatusPath => Path.Combine(BridgeDir, "status.json");

    /// <summary>停止请求文件：主控端写、Agent 读。老版本 Agent 不认识它，写了也不会响应（无害）。</summary>
    public string StopPath => Path.Combine(BridgeDir, "stop.json");

    /// <summary>耗时统计快照文件：Agent 写、主控端读。老版本 Agent 不写它，主控端读不到就退回复本目录兜底。</summary>
    public string StatsPath => Path.Combine(BridgeDir, StatsFileName);

    public string LiveLogPath(string commandId) =>
        Path.Combine(BridgeDir, $"{LiveLogFilePrefix}{SafeFilePart(commandId)}{LiveLogFileSuffix}");

    private static string SafeFilePart(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        return new string(text.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    // ---------------- 桥实例的选取 ----------------

    /// <summary>
    /// 默认桥目录：<c>&lt;ProgramData&gt;\Yomori\rdp\&lt;当前账户&gt;</c>。
    /// 代理侧按"自己在哪个账户"算，主控端按"通道配的账户"算，两边自然对齐。
    /// </summary>
    public static string DefaultBridgeDir() => RdpChannelPaths.AgentBridgeDir(Environment.UserName);

    /// <summary>按目录取桥实例（同目录复用同一实例）。传空 = 默认目录。</summary>
    public static RdpBridge For(string? directory)
    {
        var key = NormalizeBridgeDirectory(directory);

        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var reference) && reference.TryGetTarget(out var bridge))
            {
                ScavengeDeadCacheEntries();
                return bridge;
            }

            bridge = new RdpBridge(key);
            Cache[key] = new WeakReference<RdpBridge>(bridge);
            ScavengeDeadCacheEntries();
            return bridge;
        }
    }

    private static string NormalizeBridgeDirectory(string? directory)
    {
        var text = (directory ?? string.Empty).Trim();
        var expanded = text.Length == 0 ? DefaultBridgeDir() : Environment.ExpandEnvironmentVariables(text);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    private static void ScavengeDeadCacheEntries()
    {
        if ((++_cacheAccesses & (CacheScavengeInterval - 1)) != 0)
        {
            return;
        }

        foreach (var key in Cache.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray())
        {
            Cache.Remove(key);
        }
    }

    /// <summary>按通道取桥实例：配了 bridge_path 用它，否则按通道账户派生。</summary>
    public static RdpBridge ForChannel(RdpChannel? channel) =>
        For(RdpChannelPaths.BridgeDirFor(channel));

    /// <summary>
    /// Agent 进程用的桥。
    /// 启动时由 <see cref="ConfigureAgent"/> 设定（命令行 --bridge: 优先，否则按当前账户派生）。
    /// </summary>
    public static RdpBridge Agent { get; private set; } = For(null);

    /// <summary>Agent 启动时确定自己该读写哪座桥。</summary>
    public static void ConfigureAgent(string? customPath)
    {
        Agent = For(customPath);
    }

    // ---------------- 共享目录 ----------------

    public void EnsureDirectory()
    {
        try
        {
            Directory.CreateDirectory(BridgeDir);
        }
        catch
        {
            // 目录创建失败会在后续写入时明确报错，这里不阻断启动
        }
    }

    /// <summary>在资源管理器里打开共享目录（排障用）。</summary>
    public void OpenBridgeFolder()
    {
        EnsureDirectory();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"\"{BridgeDir}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------- 主控端：下发指令 ----------------

    public bool WriteCommand(RdpCommand command)
    {
        EnsureDirectory();
        try
        {
            LastError = string.Empty;
            if (!WriteAtomic(CommandPath, JsonSerializer.Serialize(command, WriteOptions)))
            {
                return false;
            }

            // 下发新指令即清场：上一轮没被消费的 stop.json 不该带进新一轮。
            // （不靠 CommandId 兜底就够了的说法是错的 —— 排障时那份残留文件会让人
            //  误判"停止请求压根没写进去"，而且老版本代理的归属校验更弱。）
            ClearStop();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>清空上一次的状态，并写入一个 idle 占位，让主控端立刻知道 Agent 还没接手。</summary>
    public void ResetStatus(string commandId)
    {
        EnsureDirectory();
        var status = new RdpStatus
        {
            CommandId = commandId,
            Phase = "idle",
            StatusText = "等待目标会话中的Yomori接管…",
        };
        try
        {
            LastError = string.Empty;
            WriteAtomic(StatusPath, JsonSerializer.Serialize(status, WriteOptions));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    // ---------------- 主控端：请求停止 ----------------

    /// <summary>请求目标会话停止当前指令（Agent 每秒检查一次）。</summary>
    public bool WriteStop(string commandId, bool emergency)
    {
        EnsureDirectory();
        try
        {
            LastError = string.Empty;
            var request = new RdpStopRequest { CommandId = commandId ?? string.Empty, Emergency = emergency };
            return WriteAtomic(StopPath, JsonSerializer.Serialize(request, WriteOptions));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>读停止请求（Agent 侧）。读不到或解析失败都返回 null。</summary>
    public RdpStopRequest? TryReadStop()
    {
        try
        {
            if (!File.Exists(StopPath))
            {
                return null;
            }

            var text = ReadTextWithRetry(StopPath);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : JsonSerializer.Deserialize<RdpStopRequest>(text, ReadOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Agent 侧判定：是否有人请求停止**当前这条指令**。
    /// 归属校验不可省 —— 桥里可能残留上一轮指令的 stop.json，
    /// 不校验的话新指令一启动就会被它立刻停掉。
    /// </summary>
    public bool IsStopRequested(string currentCommandId, out bool emergency)
    {
        emergency = false;
        var request = TryReadStop();
        if (request is null || string.IsNullOrEmpty(currentCommandId))
        {
            return false;
        }

        if (!string.Equals(request.CommandId, currentCommandId, StringComparison.Ordinal))
        {
            return false;
        }

        emergency = request.Emergency;
        return true;
    }

    /// <summary>处理完停止请求后删掉它，避免重复触发。</summary>
    public void ClearStop()
    {
        TryDeleteQuietly(StopPath);
    }

    // ---------------- Agent：回写状态 ----------------

    /// <summary>Agent 的进度、完成与诊断回调可能并行；串行化状态读改写。</summary>
    public bool UpdateStatus(Func<RdpStatus, RdpStatus> mutator)
    {
        lock (_statusWriteGate) return UpdateStatusCore(mutator);
    }

    private bool UpdateStatusCore(Func<RdpStatus, RdpStatus> mutator)
    {
        EnsureDirectory();
        try
        {
            LastError = string.Empty;
            RdpStatus status;
            try
            {
                var existing = ReadTextWithRetry(StatusPath, MaxStatusReadBytes);
                status = string.IsNullOrWhiteSpace(existing)
                    ? new RdpStatus()
                    : JsonSerializer.Deserialize<RdpStatus>(existing, ReadOptions) ?? new RdpStatus();
            }
            catch
            {
                status = new RdpStatus();
            }

            status = mutator(status);
            status.UpdatedAt = DateTimeOffset.Now.ToString("o");
            status.Events = TrimEvents(status.Events);

            return WriteAtomic(StatusPath, JsonSerializer.Serialize(status, WriteOptions));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Agent 追加一条事件，序号自增。</summary>
    public static void AppendEvent(RdpStatus current, RdpTaskEvent ev)
    {
        current.Events.Add(ev);
    }

    /// <summary>把事件列表裁剪到上限，只保留最新的若干条（纯逻辑，不碰文件）。</summary>
    public static List<RdpTaskEvent> TrimEvents(List<RdpTaskEvent>? events)
    {
        if (events is null)
        {
            return new List<RdpTaskEvent>();
        }

        return events.Count <= MaxEvents
            ? events
            : events.GetRange(events.Count - MaxEvents, MaxEvents);
    }

    // ---------------- Agent / 主控：独立实时日志 ----------------

    /// <summary>Agent 批量追加实时日志；不会读改写 status.json。</summary>
    public bool AppendLiveLogBatch(string commandId, IReadOnlyList<RdpLiveLog> logs)
    {
        if (logs.Count == 0 || string.IsNullOrWhiteSpace(commandId)) return true;
        lock (_liveLogWriteGate)
        {
            EnsureDirectory();
            try
            {
                var path = LiveLogPath(commandId);
                GrantUsersFullControl(path);
                // Each record must be a single physical line, including messages with newlines.
                var buffer = new StringBuilder();
                foreach (var log in logs)
                {
                    if (log is null || string.IsNullOrWhiteSpace(log.Message)) continue;
                    log.CommandId = commandId;
                    buffer.Append(JsonSerializer.Serialize(log, LiveLogOptions)).Append('\n');
                }

                var bytes = Encoding.UTF8.GetBytes(buffer.ToString());
                using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                {
                    var start = stream.Length;
                    stream.Position = start;
                    try
                    {
                        stream.Write(bytes);
                        stream.Flush();
                    }
                    catch
                    {
                        // A retry must not leave a partial JSON row ahead of the next batch.
                        try { stream.SetLength(start); } catch { }
                        throw;
                    }
                }
                GrantUsersFullControl(path);
                LastError = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Only delete this feature's old per-command transport files; keep the current run.</summary>
    public void PruneLiveLogs(string currentCommandId, int keepFiles = 30)
    {
        try
        {
            var current = LiveLogPath(currentCommandId);
            foreach (var file in Directory.EnumerateFiles(BridgeDir, "live-log-*.ndjson")
                .Where(f => !string.Equals(f, current, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc).Skip(Math.Max(1, keepFiles) - 1))
            {
                TryDeleteQuietly(file);
            }
        }
        catch { /* Retention failures cannot affect execution. */ }
    }

    /// <summary>主控从字节偏移读取完整 NDJSON 行；半行保留到下一次读取。</summary>
    public bool TryReadLiveLogs(string commandId, ref long byteOffset, out List<RdpLiveLog> logs)
    {
        logs = new List<RdpLiveLog>();
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return false;
        }

        try
        {
            var path = LiveLogPath(commandId);
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (byteOffset < 0 || byteOffset > stream.Length)
            {
                byteOffset = 0;
            }

            stream.Position = byteOffset;
            // Bound each poll so a backlog cannot allocate an entire run on the UI thread.
            var count = (int)Math.Min(MaxLiveLogReadBytes, stream.Length - byteOffset);
            var bytes = new byte[count];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            var completeEnd = 0;
            for (var i = 0; i < read; i++)
            {
                if (bytes[i] != (byte)'\n')
                {
                    continue;
                }

                var length = i - completeEnd;
                if (length > 0 && bytes[completeEnd + length - 1] == (byte)'\r')
                {
                    length--;
                }

                if (length > 0)
                {
                    var line = Encoding.UTF8.GetString(bytes, completeEnd, length);
                    try
                    {
                        var item = JsonSerializer.Deserialize<RdpLiveLog>(line, ReadOptions);
                        if (item is not null && string.Equals(item.CommandId, commandId, StringComparison.Ordinal) && !string.IsNullOrEmpty(item.Message))
                        {
                            logs.Add(item);
                            if (logs.Count >= MaxLiveLogLinesPerRead)
                            {
                                completeEnd = i + 1;
                                break;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Consume malformed rows so one bad line cannot block later logs.
                    }
                }

                completeEnd = i + 1;
            }

            byteOffset += completeEnd;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- Agent：发布耗时统计快照 ----------------

    /// <summary>
    /// Agent 侧：把本账户累积的耗时统计（已合并的历史平均值）发布到桥目录。
    ///
    /// 为什么非要走这一步：耗时统计写在**代理自己那份副本**的 runtime_stats\&lt;账户&gt; 下
    /// （见 AppPaths.AgentMode）。主控端既不知道副本落在了 agent 还是 agent_b 槽，
    /// 目标是远程主机时更是根本读不到对方的 ProgramData —— 于是"耗时统计"页永远空白。
    /// 桥是两端唯一约定的共享位置，发布到这里主控端才有得可读。
    ///
    /// 发布的是**合并后的全量历史**而不是本次会话：这样重发同一份内容的多次写入是幂等的，
    /// 主控端也不必自己去重。
    /// </summary>
    public bool WriteStatsSnapshot(Dictionary<string, RuntimeHistoryItem> history)
    {
        EnsureDirectory();
        try
        {
            LastError = string.Empty;
            return WriteAtomic(StatsPath, JsonSerializer.Serialize(history, WriteOptions));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>主控端侧：读本桥的统计快照。文件不存在 / 空内容 / 解析失败一律返回 null。</summary>
    public Dictionary<string, RuntimeHistoryItem>? TryReadStatsSnapshot()
    {
        try
        {
            if (!File.Exists(StatsPath))
            {
                return null;
            }

            var text = ReadTextWithRetry(StatsPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var history = JsonSerializer.Deserialize<Dictionary<string, RuntimeHistoryItem>>(text, ReadOptions);
            return history is { Count: > 0 } ? history : null;
        }
        catch
        {
            return null;
        }
    }

    public static RdpTaskEvent MakeEvent(RdpStatus current, string kind, string message, string taskName = "",
        int index = 0, int total = 0, int elapsedSeconds = 0, bool abnormal = false)
    {
        var nextSeq = current.Events.Count == 0 ? 1 : current.Events[^1].Seq + 1;
        return new RdpTaskEvent
        {
            Seq = nextSeq,
            Kind = kind,
            Time = DateTime.Now.ToString("HH:mm:ss"),
            TaskName = taskName,
            Message = message,
            Index = index,
            Total = total,
            ElapsedSeconds = elapsedSeconds,
            IsAbnormal = abnormal,
        };
    }

    // ---------------- 双方通用读取 ----------------

    public bool TryReadCommand(out RdpCommand? command)
    {
        command = null;
        try
        {
            if (!File.Exists(CommandPath))
            {
                return false;
            }

            var text = ReadTextWithRetry(CommandPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            command = JsonSerializer.Deserialize<RdpCommand>(text, ReadOptions);
            return command is not null;
        }
        catch
        {
            return false;
        }
    }

    public RdpStatus? TryReadStatus()
    {
        try
        {
            if (!File.Exists(StatusPath))
            {
                return null;
            }

            var text = ReadTextWithRetry(StatusPath, MaxStatusReadBytes);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : JsonSerializer.Deserialize<RdpStatus>(text, ReadOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 清掉指令文件（Agent 读到并接管后调用，防止代理被再次拉起时重复执行同一批任务）。
    ///
    /// 首选直接删。跨账户场景下删除可能被 ACL 拦（指令文件是主控端账户创建的），
    /// 这时退化为清空内容 —— 效果一样：读取方看到空内容会当作"没有指令"。
    /// </summary>
    public void ConsumeCommand()
    {
        var path = CommandPath;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 落到下面清空内容
        }
        catch (IOException)
        {
            // 文件被占用等情况，同样清空内容
        }

        try
        {
            if (File.Exists(path))
            {
                File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 彻底没辙就留着，但读取方会因为内容为空而当作"没有指令"
        }
    }

    // ---------------- Agent：原子认领指令 ----------------

    /// <summary>
    /// 认领文件的路径（每个代理实例一个，认领后指令就从 <c>command.json</c> 消失）。
    /// </summary>
    public string ClaimPath(string instanceId) =>
        Path.Combine(BridgeDir, $"command.claim-{SafeFilePart(instanceId)}.json");

    /// <summary>
    /// <b>原子认领</b>：把当前指令搬成"我的"，只有搬成功的那个代理才执行。
    ///
    /// <para>旧做法是「先读 command.json，再删掉它」（<see cref="TryReadCommand"/> +
    /// <see cref="ConsumeCommand"/>）。读和删之间有一整段窗口，同账户多会话时
    /// 两个代理可以同时读到同一份指令、各自删一次（第二次删的是空），然后各跑一遍任务。
    /// 改名（<c>File.Move</c>）在 NTFS 上是原子的：同一个源文件只有一个调用能成功。</para>
    ///
    /// <para>跨账户时改名可能被 ACL 拦（文件是主控端账户建的）。这时<b>退化为旧的
    /// 读 + 清空</b>，而不是直接放弃 —— 放弃等于让远程 / 跨账户场景彻底不能执行任务。
    /// 退化路径会在 <see cref="LastError"/> 里留痕，便于排障时知道"这一轮没有原子保障"。</para>
    /// </summary>
    /// <returns>true = 本实例拿到了指令（<paramref name="command"/> 非空）。</returns>
    public bool TryClaimCommand(string instanceId, out RdpCommand? command)
    {
        command = null;
        var claimPath = ClaimPath(instanceId);

        try
        {
            if (!File.Exists(CommandPath))
            {
                return false;
            }

            try
            {
                // 覆盖式改名：上一次异常退出留下的认领文件不会挡住新一轮
                File.Move(CommandPath, claimPath, overwrite: true);
                LastError = string.Empty;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 退化路径：读出来再清空源文件
                LastError = $"原子认领失败，退化为读取后清空：{ex.GetType().Name}";
                if (!TryReadCommand(out var fallback) || fallback is null)
                {
                    return false;
                }

                ConsumeCommand();
                command = fallback;
                return true;
            }

            var text = ReadTextWithRetry(claimPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            command = JsonSerializer.Deserialize<RdpCommand>(text, ReadOptions);
            return command is not null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>释放认领（执行完或拒收时调用）：删掉本实例的认领文件。</summary>
    public void ReleaseClaim(string instanceId) => TryDeleteQuietly(ClaimPath(instanceId));

    // ---------------- Agent：带实例条件的心跳 ----------------

    /// <summary>心跳写入的三种结局 —— 必须分开，否则"拒绝覆盖"会被当成"写盘失败"而误退出。</summary>
    public enum HeartbeatOutcome
    {
        /// <summary>写成功。</summary>
        Written = 0,

        /// <summary>桥里已有另一个实例写的 running 状态，本次<b>故意不写</b>。</summary>
        Skipped = 1,

        /// <summary>写盘真的失败了（共享目录不可用）。</summary>
        Failed = 2,
    }

    /// <summary>
    /// 写 idle 心跳，但**不覆盖另一个实例正在执行的状态**。
    ///
    /// <para>为什么这一条必须做：<c>UpdateStatus</c> 是「读回来 → 改字段 → 写回去」。
    /// 同账户两个代理时，A 正在写 running，B 的 idle 心跳读到的可能是 A 那句，
    /// 改成 idle 再写回 —— 主控端立刻看到"任务结束/没在跑"，而实际上 A 还在跑。
    /// 判定只看 <c>Phase</c> 与 <c>AgentInstanceId</c>，不猜时间戳。</para>
    /// </summary>
    public HeartbeatOutcome WriteIdleHeartbeat(string instanceId, Func<RdpStatus, RdpStatus> mutator)
    {
        lock (_statusWriteGate)
        {
            try
            {
                var existing = TryReadStatus();
                if (existing is not null
                    && existing.AgentInstanceId.Length > 0
                    && !string.Equals(existing.AgentInstanceId, instanceId, StringComparison.Ordinal)
                    && string.Equals(existing.Phase, "running", StringComparison.OrdinalIgnoreCase))
                {
                    return HeartbeatOutcome.Skipped;
                }
            }
            catch
            {
                // 读不到就按"可以写"处理：读失败本身会在写的时候暴露
            }

            return UpdateStatusCore(status =>
            {
                var updated = mutator(status);
                updated.AgentInstanceId = instanceId;
                return updated;
            })
                ? HeartbeatOutcome.Written
                : HeartbeatOutcome.Failed;
        }
    }

    // ---------------- 内部实现 ----------------

    /// <summary>
    /// 写文件。按可靠性从高到低尝试三种方式：
    ///   1. 临时文件 + 原子替换（读取方永远看不到半截内容）
    ///   2. 删掉目标再重命名（目标文件不允许被覆盖时）
    ///   3. 直接覆写（前面都不行时的兜底）
    /// 跨账户场景下目标文件是另一个账户创建的，ACL 常常不允许删除，
    /// 所以必须有兜底 —— 写不进去会让整个任务链路断掉，比"可能读到半截"严重得多。
    /// </summary>
    private bool WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先放开目标文件的权限。必须放在替换之前：
        // 否则别的账户创建的文件会一直挡住后续所有写入。
        GrantUsersFullControl(path);

        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            GrantUsersFullControl(temp);
        }
        catch
        {
            // 临时文件都写不出来，直接试覆写
            TryDeleteQuietly(temp);
            return OverwriteDirectly(path, content);
        }

        // 方式 1：原子替换
        try
        {
            File.Move(temp, path, overwrite: true);
            GrantUsersFullControl(path);
            return true;
        }
        catch
        {
            // 落到方式 2
        }

        // 方式 2：先删目标再重命名
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
            GrantUsersFullControl(path);
            return true;
        }
        catch
        {
            // 落到方式 3
        }

        // 方式 3：直接覆写
        TryDeleteQuietly(temp);
        return OverwriteDirectly(path, content);
    }

    /// <summary>兜底写入：直接覆写目标文件。失败时抛异常，由调用方记录 LastError。</summary>
    private bool OverwriteDirectly(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            GrantUsersFullControl(path);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
    }

    /// <summary>删文件，失败就算了 —— 只是清理临时文件，不该影响主流程。</summary>
    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 给文件授予 BUILTIN\Users 完全控制。
    /// 桥目录是跨账户通信用的，两个账户都要能读写对方的文件，默认 ACL 不够。
    /// 授权失败不抛异常 —— 同账户场景本来就不需要，不该因此阻断写入。
    /// </summary>
    internal static void GrantUsersFullControl(string path)    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));

            new FileInfo(path).SetAccessControl(security);
        }
        catch
        {
            // 权限设置失败不影响函数返回值；跨账户场景会在状态回写时暴露出来
        }
    }

    private static string ReadTextWithRetry(string path, int? maxBytes = null)
    {
        for (var attempt = 0; attempt < ReadRetry; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (maxBytes is int limit)
                {
                    if (stream.Length > limit)
                    {
                        throw new IOException($"文件超过读取上限（{limit} 字节）：{path}");
                    }

                    var bytes = new byte[(int)stream.Length];
                    var read = 0;
                    while (read < bytes.Length)
                    {
                        var count = stream.Read(bytes, read, bytes.Length - read);
                        if (count == 0) { break; }
                        read += count;
                    }

                    return DecodeBoundedText(bytes, read);
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < ReadRetry - 1)
            {
                System.Threading.Thread.Sleep(40);
            }
        }

        return string.Empty;
    }

    private static string DecodeBoundedText(byte[] bytes, int count)
    {
        using var memory = new MemoryStream(bytes, 0, count, writable: false);
        using var reader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
