// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 通知中心：主窗口右上角铃铛里的那串消息。
///
/// <para>存在 <c>&lt;程序目录&gt;\notifications.json</c>（最多 <see cref="MaxItems"/> 条，
/// 已读状态一起存）。为什么落盘：用户可能关掉程序才想起来"刚才好像提示有更新"，
/// 而启动时检查更新的结果如果只在内存里，关一次就没了 —— 那就等于每次都得盯着看。</para>
///
/// <para>⚠️ 线程纪律：本类**不做线程调度**，Add / MarkRead / Load 必须在 UI 线程调用
/// （<see cref="Items"/> 是直接绑到界面的 ObservableCollection）。后台线程（更新检查、
/// 下载）要加通知，先在 VM 里 Post 回 UI 线程。</para>
/// </summary>
public sealed class NotificationCenter
{
    /// <summary>最多保留多少条。再多的价值低于"翻不到最新那条"的代价。</summary>
    public const int MaxItems = 50;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public NotificationCenter(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(AppPaths.BaseDir, "notifications.json")
            : path;
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

    /// <summary>从磁盘读回来。文件不存在 / 内容坏了都当成"没有通知"，不抛。</summary>
    public void Load()
    {
        Items.Clear();

        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path, Encoding.UTF8);
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

        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // 删不掉（只读目录 / 被占用）也不影响本次会话：反正启动不再回读它
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>加一条并落盘，返回新条目。</summary>
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
        Save();
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
        Save();
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

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>删掉一条。</summary>
    public void Remove(AppNotification? item)
    {
        if (item is null || !Items.Remove(item))
        {
            return;
        }

        Save();
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
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Trim()
    {
        while (Items.Count > MaxItems)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }

    /// <summary>写盘。失败只吞掉 —— 通知丢了不影响主功能，但界面上的那份还在。</summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(Items.ToList(), WriteOptions), new UTF8Encoding(false));
        }
        catch
        {
            // 盘满 / 只读目录 / 文件被占用：都静默
        }
    }
}
