// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 通知中心：主窗口右上角铃铛里的那串消息。
///
/// <para>存在 <c>&lt;程序目录&gt;\notifications.json</c>（最多 <see cref="MaxItems"/> 条，
/// 已读状态一起存）。为什么落盘：用户可能关掉程序才想起来"刚才好像提示有更新"，
/// 而启动时检查更新的结果如果只在内存里，关一次就没了 —— 那就等于每次都得盯着看。</para>
///
/// <para>⚠️ 线程纪律：本类**不做线程调度**，Add / MarkRead / Load 等访问
/// <see cref="Items"/> 的操作必须在 UI 线程调用（<see cref="Items"/> 是直接绑到界面的
/// ObservableCollection）。后台线程（更新检查、下载）要加通知，先在 VM 里 Post 回 UI 线程。</para>
///
/// <para>高频 Add / MarkRead / Remove / Clear 不再逐次同步写盘：它们在 UI 线程完成内存变更后，
/// 只生成一份很快的深拷贝快照，并由**至多一个、短生命周期**的后台任务在合并窗口结束后写最新快照。
/// 正常退出和 <see cref="Flush"/> 仍会同步写出最后状态；后台任务只碰快照，不碰 ObservableCollection。</para>
/// </summary>
public sealed class NotificationCenter
{
    /// <summary>最多保留多少条。再多的价值低于"翻不到最新那条"的代价。</summary>
    public const int MaxItems = 50;

    /// <summary>第一次产生变更后最多等这么久再开始后台写，用来合并连续操作。</summary>
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object ProcessExitGate = new();
    private static readonly List<WeakReference<NotificationCenter>> ProcessExitCenters = new();
    private static bool _processExitHooked;

    private readonly string _path;
    private readonly TimeSpan _writeDelay;

    /// <summary>保护最新快照与修订号；锁内不做文件 I/O。</summary>
    private readonly object _saveStateGate = new();

    /// <summary>串行化实际文件写入和启动删除，避免旧快照覆盖新快照。</summary>
    private readonly object _diskGate = new();

    private List<AppNotification>? _latestSnapshot;
    private long _generation;
    private long _mutationRevision;
    private long _attemptedRevision;
    private long _persistedRevision;
    private bool _writeTaskActive;
    private int _persistenceWriteCount;

    public NotificationCenter(string? path = null)
        : this(path, CoalesceDelay)
    {
    }

    /// <summary>测试可缩短/延长合并窗口；生产只走上面的默认构造。</summary>
    internal NotificationCenter(string? path, TimeSpan writeDelay)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(AppPaths.BaseDir, "notifications.json")
            : path;
        _writeDelay = writeDelay < TimeSpan.Zero ? TimeSpan.Zero : writeDelay;
        RegisterForProcessExit(this);
    }

    /// <summary>通知列表，新的在前面（直接绑 ListView，别整体替换集合）。</summary>
    public ObservableCollection<AppNotification> Items { get; } = new();

    public int UnreadCount => Items.Count(item => !item.IsRead);

    public bool HasUnread => Items.Any(item => !item.IsRead);

    /// <summary>有新通知进来（用于摇铃铛）。</summary>
    public event EventHandler<AppNotification>? Added;

    /// <summary>未读数或已读状态变了（用于刷新红点、停止摇动）。</summary>
    public event EventHandler? Changed;

    public string FilePath => _path;

    /// <summary>仅供同程序集冒烟测试确认多次高频变更合并成了一次实际写入。</summary>
    internal int PersistenceWriteCount => Volatile.Read(ref _persistenceWriteCount);

    /// <summary>从磁盘读回来。文件不存在 / 内容坏了都当成"没有通知"，不抛。</summary>
    public void Load()
    {
        Items.Clear();
        InvalidatePendingPersistence();

        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            string json;
            lock (_diskGate)
            {
                if (!File.Exists(_path))
                {
                    return;
                }

                json = File.ReadAllText(_path, Encoding.UTF8);
            }

            var loaded = JsonSerializer.Deserialize<List<AppNotification>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (loaded is null)
            {
                return;
            }

            foreach (var item in loaded
                         .Where(item => item is not null)
                         .OrderByDescending(item => item.CreatedAt)
                         .Take(MaxItems))
            {
                Items.Add(item);
            }
        }
        catch
        {
            // 读坏了就当空的：通知是锦上添花，不能因为它起不来
        }
    }

    /// <summary>
    /// 启动时清空：内存里的历史清掉，落盘文件一并删除。
    ///
    /// <para>为什么不再"开机还在"：铃铛里的更新提醒、下载回执都是**当下这一刻**的状态 ——
    /// 上次开机会话里那条「安装包已下载」，这次启动时安装包可能早就装完删了，
    /// 点它只会报"文件不存在"。启动检查更新本来就会在 6 秒后再查一遍并按需重新挂一条，
    /// 留着旧的那条只会变成误导。</para>
    ///
    /// <para>⚠️ 与 <see cref="Load"/> 是互斥的两种启动策略，产品里只用这一个；
    /// <see cref="Load"/> 留着是因为「落盘/读回/损坏容错」这套能力自身还要能被冒烟验证。</para>
    /// </summary>
    public void ResetOnStartup()
    {
        Items.Clear();
        InvalidatePendingPersistence();

        try
        {
            lock (_diskGate)
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
        }
        catch
        {
            // 删不掉（只读目录 / 被占用）也不影响本次会话：反正启动不再回读它
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>加一条并排队写盘，返回新条目。</summary>
    public AppNotification Add(
        string title,
        string body,
        string kind,
        string actionKind = AppNotificationActions.None,
        string actionTarget = "")
    {
        var item = new AppNotification
        {
            Title = title ?? string.Empty,
            Body = body ?? string.Empty,
            Kind = kind,
            ActionKind = actionKind,
            ActionTarget = actionTarget ?? string.Empty,
        };

        Items.Insert(0, item);
        Trim();
        QueueSave();
        Added?.Invoke(this, item);
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    /// <summary>
    /// 同标题的不重复堆叠，而是**就地换成新的一条**（已读状态保留）。
    ///
    /// 专门给启动时检查更新用：每次启动都会查一次，而"发现新版本 2.3.0"这条
    /// 在用户升级之前会一直成立 —— 不去重的话，一天开十次就攒十条一样的。
    ///
    /// 换而不是跳过：同一条更新提醒的动作可能变（例如从"去网页下载"改成"直接下载"），
    /// 跳过就会让用户点着车上留下来的旧条目、又跳回浏览器。
    /// </summary>
    /// <returns>新加的那条；已有同标题时返回 null（但列表里那条已经被换掉了）。</returns>
    public AppNotification? AddIfNew(
        string title,
        string body,
        string kind,
        string actionKind = AppNotificationActions.None,
        string actionTarget = "")
    {
        var existing = Items.FirstOrDefault(item => string.Equals(item.Title, title, StringComparison.Ordinal));

        if (existing is null)
        {
            return Add(title, body, kind, actionKind, actionTarget);
        }

        var wasRead = existing.IsRead;
        Items.Remove(existing);

        var replaced = Add(title, body, kind, actionKind, actionTarget);
        if (wasRead)
        {
            MarkRead(replaced);
        }

        return null;
    }

    /// <summary>把某条标成已读（已读的不重复通知界面）。</summary>
    public void MarkRead(AppNotification? item)
    {
        if (item is null || item.IsRead)
        {
            return;
        }

        item.IsRead = true;
        QueueSave();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>全部标为已读。</summary>
    public void MarkAllRead()
    {
        var changed = false;
        foreach (var item in Items.Where(item => !item.IsRead))
        {
            item.IsRead = true;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        QueueSave();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>删掉一条。</summary>
    public void Remove(AppNotification? item)
    {
        if (item is null || !Items.Remove(item))
        {
            return;
        }

        QueueSave();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空全部（界面上有确认）。</summary>
    public void Clear()
    {
        if (Items.Count == 0)
        {
            return;
        }

        Items.Clear();
        QueueSave();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 立即把当前最新快照同步写到磁盘。
    ///
    /// <para>正常退出由 <see cref="AppDomain.ProcessExit"/> 自动调用；测试或其他显式收尾点也可直接调用。
    /// 它只读后台写者持有的快照，不访问 <see cref="Items"/>，因此退出回调线程也可以安全调用。</para>
    /// </summary>
    public void Flush()
    {
        List<AppNotification>? snapshot;
        long generation;
        long revision;
        lock (_saveStateGate)
        {
            snapshot = _latestSnapshot;
            generation = _generation;
            revision = _mutationRevision;
        }

        if (snapshot is null)
        {
            return;
        }

        TryPersistSnapshot(generation, revision, snapshot);
    }

    private void Trim()
    {
        while (Items.Count > MaxItems)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }

    /// <summary>
    /// 在 UI 线程取一份不可变（就本类而言）的值快照，随后至多启动一个后台写者。
    /// 后面的变更只替换"最新快照"，不会形成无界队列。
    /// </summary>
    private void QueueSave()
    {
        var snapshot = SnapshotItems();
        lock (_saveStateGate)
        {
            _mutationRevision++;
            _latestSnapshot = snapshot;

            if (!_writeTaskActive)
            {
                StartWriteTaskLocked();
            }
        }
    }

    private void StartWriteTaskLocked()
    {
        _writeTaskActive = true;
        var generation = _generation;

        try
        {
            _ = Task.Run(() => WriteAfterDelayAsync(generation));
        }
        catch
        {
            _writeTaskActive = false;
        }
    }

    private async Task WriteAfterDelayAsync(long generation)
    {
        try
        {
            if (_writeDelay > TimeSpan.Zero)
            {
                await Task.Delay(_writeDelay).ConfigureAwait(false);
            }

            List<AppNotification>? snapshot;
            long revision;
            lock (_saveStateGate)
            {
                if (generation != _generation ||
                    _latestSnapshot is null ||
                    _mutationRevision <= _attemptedRevision)
                {
                    _writeTaskActive = false;
                    StartWriteTaskIfNeededLocked();
                    return;
                }

                snapshot = _latestSnapshot;
                revision = _mutationRevision;
            }

            TryPersistSnapshot(generation, revision, snapshot);

            lock (_saveStateGate)
            {
                _writeTaskActive = false;

                if (generation != _generation || _mutationRevision > _attemptedRevision)
                {
                    StartWriteTaskIfNeededLocked();
                }
            }
        }
        catch
        {
            lock (_saveStateGate)
            {
                _writeTaskActive = false;
                StartWriteTaskIfNeededLocked();
            }
        }
    }

    /// <summary>调用方必须持有 <see cref="_saveStateGate"/>。</summary>
    private void StartWriteTaskIfNeededLocked()
    {
        if (_writeTaskActive ||
            _latestSnapshot is null ||
            _mutationRevision <= _attemptedRevision)
        {
            return;
        }

        StartWriteTaskLocked();
    }

    /// <summary>
    /// 写一份快照；修订号与代次检查保证 Reset/Load/Flush 之后旧任务不会把旧内容写回来。
    /// 失败只吞掉 —— 通知丢了不影响主功能，下次变更或显式 Flush 还会重试最新快照。
    /// </summary>
    private bool TryPersistSnapshot(long generation, long revision, List<AppNotification> snapshot)
    {
        lock (_diskGate)
        {
            lock (_saveStateGate)
            {
                if (generation != _generation)
                {
                    return false;
                }

                if (revision <= _persistedRevision)
                {
                    if (revision > _attemptedRevision)
                    {
                        _attemptedRevision = revision;
                    }

                    return true;
                }
            }

            Interlocked.Increment(ref _persistenceWriteCount);
            var success = TryWriteSnapshotToDisk(snapshot);

            lock (_saveStateGate)
            {
                if (generation == _generation)
                {
                    if (revision > _attemptedRevision)
                    {
                        _attemptedRevision = revision;
                    }

                    if (success && revision > _persistedRevision)
                    {
                        _persistedRevision = revision;
                    }
                }
            }

            return success;
        }
    }

    private bool TryWriteSnapshotToDisk(List<AppNotification> snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, WriteOptions), new UTF8Encoding(false));
            return true;
        }
        catch
        {
            // 盘满 / 只读目录 / 文件被占用：都静默；界面上的那份还在
            return false;
        }
    }

    private List<AppNotification> SnapshotItems() => Items.Select(CloneForPersistence).ToList();

    /// <summary>
    /// 只复制真正落盘的字段。进度是瞬时 UI 状态（<see cref="AppNotification.Progress"/> 本身也不序列化），
    /// 后台线程因此不会读到仍在 UI 线程上变化的通知对象。
    /// </summary>
    private static AppNotification CloneForPersistence(AppNotification item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Body = item.Body,
        Kind = item.Kind,
        CreatedAt = item.CreatedAt,
        IsRead = item.IsRead,
        ActionKind = item.ActionKind,
        ActionTarget = item.ActionTarget,
    };

    /// <summary>Load / Reset 使在途快照失效；下一次 QueueSave 会使用新的代次。</summary>
    private void InvalidatePendingPersistence()
    {
        lock (_saveStateGate)
        {
            _generation++;
            _latestSnapshot = null;
            _mutationRevision++;
            _attemptedRevision = _mutationRevision;
        }
    }

    private static void RegisterForProcessExit(NotificationCenter center)
    {
        lock (ProcessExitGate)
        {
            for (var i = ProcessExitCenters.Count - 1; i >= 0; i--)
            {
                if (!ProcessExitCenters[i].TryGetTarget(out _))
                {
                    ProcessExitCenters.RemoveAt(i);
                }
            }

            ProcessExitCenters.Add(new WeakReference<NotificationCenter>(center));
            if (_processExitHooked)
            {
                return;
            }

            _processExitHooked = true;
            AppDomain.CurrentDomain.ProcessExit += FlushAllOnProcessExit;
        }
    }

    private static void FlushAllOnProcessExit(object? sender, EventArgs e)
    {
        NotificationCenter[] centers;
        lock (ProcessExitGate)
        {
            var live = new List<NotificationCenter>(ProcessExitCenters.Count);
            foreach (var reference in ProcessExitCenters)
            {
                if (reference.TryGetTarget(out var center))
                {
                    live.Add(center);
                }
            }

            centers = live.ToArray();
        }

        foreach (var center in centers)
        {
            try
            {
                center.Flush();
            }
            catch
            {
                // 退出路径不能让通知落盘失败拖垮进程收尾
            }
        }
    }
}
