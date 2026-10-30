// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

namespace Yomori.Models;

/// <summary>
/// 「按执行通道编排任务」的纯函数集合（计划书 <c>docs/tasks-by-channel-plan.md</c> §3）。
///
/// 为什么单独抽一层：**任务仍然只有一份扁平表**（<c>config.tasks[]</c> + <c>channel_id</c>），
/// "通道 → 任务"只是它的一种读法。放到不碰界面、不碰文件的静态类里，
/// 既能让 <c>MainViewModel</c> 只做薄壳，也能在 <c>_smoke</c> 里脱离 WinUI 直接测。
///
/// ⚠️ 与执行顺序的关系（§3.2 等价性）：<see cref="RdpChannelPlanner.Plan"/> 是
/// 「全局 order 升序 → 稳定分桶 → 按通道配置顺序启动」。本类只做两件事：
///   1. 通道内上移/下移**只在同通道内**交换 order；
///   2. 拖放排序（<see cref="ApplyScopeSequence"/>）只重排该组已占用的 order 值；
///   3. 重编 order 时保持（本地 → 通道配置序 → 孤儿）的分组次序与组内相对次序。
/// 于是每条通道拿到的任务集合与顺序与改造前**逐一相同**，执行链路一行都不用改。
/// </summary>
public static class TaskScopePlanner
{
    /// <summary>本地执行（与 <see cref="TaskConfig.ChannelId"/> 空串同一语义）。</summary>
    // 注意：string.Empty 是 static readonly，不能赋给 const —— 这里必须写字面量
    public const string LocalScopeId = "";

    /// <summary>删除通道时「保持未知」的哨兵值 —— 只在对话框里传递，不落盘。</summary>
    public const string KeepOrphan = "__keep__";

    public static string NormalizeId(string? id) => (id ?? string.Empty).Trim();

    private static bool SameId(string? a, string? b) =>
        string.Equals(NormalizeId(a), NormalizeId(b), StringComparison.OrdinalIgnoreCase);

    // ---------------- 分组读取 ----------------

    /// <summary>
    /// 某条通道下的任务，按 <see cref="TaskConfig.Order"/> 升序，**收尾任务恒排最后**。
    ///
    /// <para>收尾是通道里固定的一步（每通道恰好一条、不可增删改），它的语义就是"队尾"，
    /// 所以排序键里直接带上它 —— 追加、移动、导入、改类型都靠这一次排序收敛，
    /// 不需要在每个操作里各写一遍"记得把它挪到最后"。</para>
    /// </summary>
    public static List<TaskConfig> TasksForScope(IEnumerable<TaskConfig>? tasks, string? scopeId)
    {
        return (tasks ?? Enumerable.Empty<TaskConfig>())
            .Where(t => t is not null && SameId(t.ChannelId, scopeId))
            .OrderBy(t => TaskTypeResolver.IsFinish(t) ? 1 : 0)
            .ThenBy(t => t.Order)
            .ToList();
    }

    public static int CountForScope(IEnumerable<TaskConfig>? tasks, string? scopeId) =>
        TasksForScope(tasks, scopeId).Count;

    /// <summary>
    /// 孤儿 id 列表：任务指向的通道已经不在配置里了。
    /// 按 id 排序 —— 分组顺序必须稳定，否则界面上的行会来回跳。
    /// </summary>
    public static List<string> OrphanIds(IEnumerable<TaskConfig>? tasks, IReadOnlyList<RdpChannel>? channels)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels ?? Array.Empty<RdpChannel>())
        {
            if (!string.IsNullOrWhiteSpace(channel.Id))
            {
                known.Add(channel.Id.Trim());
            }
        }

        return (tasks ?? Enumerable.Empty<TaskConfig>())
            .Where(t => t is not null)
            .Select(t => NormalizeId(t.ChannelId))
            .Where(id => id.Length > 0 && !known.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 左侧「执行通道」列表：本地执行（恒第一项）→ 各通道（配置顺序）→ 未知通道（孤儿）。
    /// 顺带把每个 scope 的任务数算好。
    /// </summary>
    public static List<ChannelScope> BuildScopes(
        IEnumerable<TaskConfig>? tasks,
        IReadOnlyList<RdpChannel>? channels,
        bool rdpEnabled)
    {
        var list = (tasks ?? Enumerable.Empty<TaskConfig>()).Where(t => t is not null).ToList();
        var channelList = channels ?? Array.Empty<RdpChannel>();
        var scopes = new List<ChannelScope>();

        scopes.Add(new ChannelScope
        {
            Id = LocalScopeId,
            Name = "本地执行（当前会话）",
            Subtitle = "主控端当前账户",
            IsLocal = true,
            IsUsable = true,
            TaskCount = CountForScope(list, LocalScopeId),
        });

        foreach (var channel in channelList)
        {
            if (string.IsNullOrWhiteSpace(channel.Id))
            {
                continue;
            }

            scopes.Add(new ChannelScope
            {
                Id = channel.Id,
                Name = channel.DisplayName,
                Subtitle = channel.ListSubtitle,
                Enabled = channel.Enabled,
                IsUsable = IsScopeUsable(channel, rdpEnabled),
                HintText = HintFor(channel, rdpEnabled),
                TaskCount = CountForScope(list, channel.Id),
            });
        }

        foreach (var id in OrphanIds(list, channelList))
        {
            scopes.Add(new ChannelScope
            {
                Id = id,
                Name = $"未知通道（{id}）",
                Subtitle = "这条通道已经被删除了",
                IsMissing = true,
                IsUsable = false,
                HintText = "里面的任务不会被下发 —— 请移到别的通道或本地执行",
                TaskCount = CountForScope(list, id),
            });
        }

        return scopes;
    }

    /// <summary>
    /// 通道这一行能不能用（能不能点开排任务、进不进入本轮执行）。
    ///
    /// 抽出来是为了让界面上的开关能**就地**刷新一行，而不必重建整个
    /// <c>ChannelScopes</c> 集合 —— 开关是在 <c>Toggled</c> 事件里改的，
    /// 此刻整体替换集合正好踩中本项目反复出现的「集合整体替换 × 绑定源失效」形状。
    /// </summary>
    public static bool IsScopeUsable(RdpChannel? channel, bool rdpEnabled) =>
        channel is not null && rdpEnabled && channel.Enabled && channel.IsConfigured;

    /// <summary>通道这一行不可用的原因（可用时为空串）。</summary>
    public static string HintFor(RdpChannel? channel, bool rdpEnabled)
    {
        if (channel is null)
        {
            return string.Empty;
        }

        if (!rdpEnabled)
        {
            return "总开关已关闭 —— 开始执行时这些任务会按本地跑";
        }

        if (!channel.Enabled)
        {
            return "通道已停用 —— 本轮不会下发";
        }

        return channel.IsConfigured ? string.Empty : "通道未配置目标账户 —— 本轮不会下发";
    }

    /// <summary>写通道内显示序号（1..N，运行期属性，不落盘）。</summary>
    public static void RenumberScope(IReadOnlyList<TaskConfig> scopeTasks)
    {
        for (var i = 0; i < scopeTasks.Count; i++)
        {
            scopeTasks[i].ScopeOrder = i + 1;
        }
    }

    // ---------------- 编排操作 ----------------

    /// <summary>
    /// 把 order 重编成 1..N 连续唯一：先按（本地 → 通道配置序 → 孤儿）分组，
    /// 组内**先普通任务、后收尾任务**，再按现有 order 升序。
    /// **只改 order 的值，不改集合的物理顺序** —— 执行侧只看 order。
    /// </summary>
    public static void NormalizeOrdersByChannel(IList<TaskConfig> tasks, IReadOnlyList<RdpChannel>? channels)
    {
        if (tasks is null)
        {
            return;
        }

        var rank = BuildRankMap(channels, tasks);

        var ordered = tasks
            .Where(t => t is not null)
            .OrderBy(t => RankOf(t.ChannelId, rank))
            .ThenBy(t => TaskTypeResolver.IsFinish(t) ? 1 : 0)
            .ThenBy(t => t.Order)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i + 1;
        }
    }

    /// <summary>
    /// 通道内上移/下移（delta = -1 / +1）：**只与同通道内相邻的一项交换 order**。
    /// 别的通道的顺序不受影响 —— 这正是"执行顺序等价"的关键。
    ///
    /// <para>收尾任务不参与：它恒在末位、位置由系统保证，所以只要涉及它（自己动、或被挤到它后面）
    /// 就返回 false。界面据此把按钮置灰，方法内再拒一次 —— 置灰只说明"按钮该灰"，
    /// 不是闸门。</para>
    /// </summary>
    public static bool TryMoveInScope(
        IList<TaskConfig> tasks,
        TaskConfig? task,
        int delta,
        IReadOnlyList<RdpChannel>? channels)
    {
        if (tasks is null || task is null || delta == 0)
        {
            return false;
        }

        if (TaskTypeResolver.IsFinish(task))
        {
            return false;
        }

        var channelList = channels ?? Array.Empty<RdpChannel>();

        // 先重编一次：万一 order 有重复（旧配置 / 手工改过），交换两个相同值等于没动
        NormalizeOrdersByChannel(tasks, channelList);

        var scope = TasksForScope(tasks, task.ChannelId);
        var index = scope.IndexOf(task);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= scope.Count)
        {
            return false;
        }

        // 不许把普通任务挤到收尾之后
        if (TaskTypeResolver.IsFinish(scope[target]))
        {
            return false;
        }

        (scope[index].Order, scope[target].Order) = (scope[target].Order, scope[index].Order);
        NormalizeOrdersByChannel(tasks, channelList);
        return true;
    }

    /// <summary>
    /// 拖放排序后的收尾：把「通道内的新次序」翻译成 <see cref="TaskConfig.Order"/>。
    ///
    /// 做法是**只重排这一组已经占用的 order 值**（取出来升序排好，再按新次序发回去）——
    /// order 的多重集合一点没变，所以既不会跟别的通道撞号，也不用先重编全局序号。
    /// 之后任何一次 <see cref="NormalizeOrdersByChannel"/>（`SaveConfig()` 里就会调）
    /// 都会把它收敛成 1..N 连续值，且保持这里定下的相对次序。
    /// </summary>
    public static void ApplyScopeSequence(IReadOnlyList<TaskConfig>? orderedInScope)
    {
        if (orderedInScope is null || orderedInScope.Count == 0)
        {
            return;
        }

        var slots = orderedInScope.Select(t => t.Order).OrderBy(v => v).ToList();
        for (var i = 0; i < orderedInScope.Count; i++)
        {
            orderedInScope[i].Order = slots[i];
        }
    }

    /// <summary>改任务归属：换到目标通道并落到它的末尾，然后统一重编 order。</summary>
    public static void MoveToScope(
        IList<TaskConfig> tasks,
        TaskConfig? task,
        string? targetScopeId,
        IReadOnlyList<RdpChannel>? channels)
    {
        if (tasks is null || task is null)
        {
            return;
        }

        // 收尾任务与通道绑定：它属于"这条通道跑完做什么"，换通道没有意义，
        // 而且会让源通道少一条、目标通道多一条（两边都破坏"恰好一条"）。
        if (TaskTypeResolver.IsFinish(task))
        {
            return;
        }

        var id = NormalizeId(targetScopeId);
        if (SameId(task.ChannelId, id))
        {
            return;
        }

        task.ChannelId = id;

        var max = tasks.Where(t => t is not null).Select(t => t.Order).DefaultIfEmpty(0).Max();
        task.Order = max + 1;

        // 目标通道已有收尾时，重编会把它排到收尾之前（排序键带 IsFinish）
        NormalizeOrdersByChannel(tasks, channels ?? Array.Empty<RdpChannel>());
    }

    // ---------------- 收尾任务：保底、类型闸门、拖放夹取 ----------------

    /// <summary>
    /// 保证某条通道**恰好一条**收尾任务：一条都没有就补一条（排在末位），
    /// 出现两条以上就收敛成 order 最大的那条。
    ///
    /// <para>返回新补的那一条（没有补则 null），调用方可据此写日志。</para>
    ///
    /// <para>空通道不动 —— 没任务时显示"无任务"比显示一条幽灵收尾任务更符合直觉；
    /// 收尾行是在"这条通道第一次有任务"时补上的。</para>
    /// </summary>
    public static TaskConfig? EnsureFinishTask(
        IList<TaskConfig> tasks,
        string? scopeId,
        IReadOnlyList<RdpChannel>? channels,
        out List<TaskConfig> removed)
    {
        removed = new List<TaskConfig>();

        if (tasks is null)
        {
            return null;
        }

        var id = NormalizeId(scopeId);
        var inScope = tasks
            .Where(t => t is not null && SameId(t.ChannelId, id))
            .ToList();

        if (inScope.Count == 0)
        {
            return null;
        }

        var finishes = inScope
            .Where(TaskTypeResolver.IsFinish)
            .OrderBy(t => t.Order)
            .ToList();

        if (finishes.Count == 1)
        {
            return null;
        }

        if (finishes.Count > 1)
        {
            // 收敛：留下 order 最大的那条，其余移除（导入的脏配置 / 手工改坏）
            var keep = finishes[^1];
            foreach (var extra in finishes.Take(finishes.Count - 1))
            {
                tasks.Remove(extra);
                removed.Add(extra);
            }

            NormalizeOrdersByChannel(tasks, channels);
            return null;
        }

        var created = TaskConfig.CreateFinish(id, tasks.Select(t => t.Order).DefaultIfEmpty(0).Max() + 1);
        tasks.Add(created);
        NormalizeOrdersByChannel(tasks, channels);
        return created;
    }

    /// <summary><see cref="EnsureFinishTask"/> 的简化重载（不关心被收敛掉的副本）。</summary>
    public static TaskConfig? EnsureFinishTask(
        IList<TaskConfig> tasks,
        string? scopeId,
        IReadOnlyList<RdpChannel>? channels) =>
        EnsureFinishTask(tasks, scopeId, channels, out _);

    /// <summary>
    /// 所有执行单元都过一遍收尾保底：本地组 + 每个已配置通道。
    /// 配置加载后调一次（<c>AppConfig.Sanitize</c>），保证任何一条通道都有那条收尾行。
    /// </summary>
    public static void EnsureFinishForAllScopes(
        IList<TaskConfig> tasks,
        IReadOnlyList<RdpChannel>? channels,
        Action<string, TaskConfig>? onCreated = null)
    {
        if (tasks is null)
        {
            return;
        }

        var created = EnsureFinishTask(tasks, LocalScopeId, channels);
        if (created is not null)
        {
            onCreated?.Invoke(LocalScopeId, created);
        }

        foreach (var channel in channels ?? Array.Empty<RdpChannel>())
        {
            if (channel is null || string.IsNullOrWhiteSpace(channel.Id))
            {
                continue;
            }

            created = EnsureFinishTask(tasks, channel.Id, channels);
            if (created is not null)
            {
                onCreated?.Invoke(channel.Id, created);
            }
        }
    }

    /// <summary>
    /// 改任务类型的**唯一闸门**。    ///
    /// <para>拒绝两种改动：改成 <c>finish</c>（收尾身份唯一且由系统维护，界面上根本没有这个选项），
    /// 以及把收尾任务改成别的类型。界面那边连入口都没有，这一层是给导入配置与将来的代码路径兜底的
    /// —— 少一层就是"某天一个批量操作把收尾改没了"。</para>
    /// </summary>
    public static bool SetTaskKind(
        IList<TaskConfig>? tasks,
        TaskConfig? task,
        string? kind,
        IReadOnlyList<RdpChannel>? channels)
    {
        if (tasks is null || task is null)
        {
            return false;
        }

        var target = TaskKinds.Normalize(kind);

        if (TaskKinds.IsFinish(target) || TaskTypeResolver.IsFinish(task))
        {
            return false;
        }

        if (task.TaskKind == target)
        {
            return true;
        }

        task.TaskKind = target;

        // 名称还停在默认值（"新任务3"/"等待"）时顺手改成新类型的默认名，用户改过就别动。
        if (string.IsNullOrWhiteSpace(task.Name)
            || task.Name.StartsWith("新任务", StringComparison.Ordinal)
            || task.Name == "等待")
        {
            task.Name = TaskKinds.IsWait(target) ? "等待" : $"新任务{task.Order}";
        }

        NormalizeOrdersByChannel(tasks, channels ?? Array.Empty<RdpChannel>());
        return true;
    }

    /// <summary>
    /// 拖放的落点夹取：插入位置不能越过收尾任务（它恒在末位）。
    ///
    /// <para>拖放（<see cref="ApplyScopeSequence"/>）只重排已占用的 order 槽位、**不调**
    /// <see cref="NormalizeOrdersByChannel"/>，所以这里必须自己夹一次 —— 否则收尾会被拖到中间，
    /// 而"收尾恒在末位"是所有收尾动作成立的前提。</para>
    /// </summary>
    public static int ClampInsertIndex(IReadOnlyList<TaskConfig>? scopeTasks, int index)
    {
        if (scopeTasks is null || scopeTasks.Count == 0)
        {
            return Math.Max(0, index);
        }

        var finishIndex = -1;
        for (var i = 0; i < scopeTasks.Count; i++)
        {
            if (TaskTypeResolver.IsFinish(scopeTasks[i]))
            {
                finishIndex = i;
                break;
            }
        }

        var clamped = Math.Max(0, Math.Min(index, scopeTasks.Count));

        if (finishIndex >= 0 && clamped > finishIndex)
        {
            clamped = finishIndex;
        }

        return clamped;
    }

    /// <summary>某通道里某类任务的数量（冒烟与界面计数用）。</summary>
    public static int CountOf(IEnumerable<TaskConfig>? tasks, string? scopeId, string kind)
    {
        var normalized = TaskKinds.Normalize(kind);
        return (tasks ?? Enumerable.Empty<TaskConfig>())
            .Count(t => t is not null && SameId(t.ChannelId, scopeId) && TaskKinds.Normalize(t.TaskKind) == normalized);
    }

    // ---------------- 并发组（只在通道内生效） ----------------

    /// <summary>
    /// 跨执行单元的并发组名：同一个组名出现在 ≥2 个通道（本地组也算一个）。
    /// <see cref="Services.ScriptRunner"/> 只在**本批次任务列表**里扫描相邻同组任务，
    /// 所以跨通道同名组根本不会并在一起 —— 这里只用来给一行日志提示，不阻断。
    /// </summary>
    public static List<string> CrossChannelGroups(IEnumerable<TaskConfig>? tasks)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks ?? Enumerable.Empty<TaskConfig>())
        {
            if (task is null || !task.Enabled)
            {
                continue;
            }

            var group = (task.ConcurrentGroup ?? string.Empty).Trim();
            if (group.Length == 0)
            {
                continue;
            }

            if (!owners.TryGetValue(group, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                owners[group] = set;
            }

            set.Add(NormalizeId(task.ChannelId));
        }

        return owners
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => kv.Key)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------- 内部：分组次序 ----------------

    private static Dictionary<string, int> BuildRankMap(
        IReadOnlyList<RdpChannel>? channels,
        IEnumerable<TaskConfig> tasks)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [LocalScopeId] = 0,
        };

        var channelList = channels ?? Array.Empty<RdpChannel>();
        for (var i = 0; i < channelList.Count; i++)
        {
            var id = NormalizeId(channelList[i].Id);
            if (id.Length > 0)
            {
                rank[id] = i + 1;
            }
        }

        var orphanBase = channelList.Count + 1;
        var orphans = OrphanIds(tasks, channelList);
        for (var i = 0; i < orphans.Count; i++)
        {
            rank[orphans[i]] = orphanBase + i;
        }

        return rank;
    }

    private static int RankOf(string? channelId, Dictionary<string, int> rank) =>
        rank.TryGetValue(NormalizeId(channelId), out var value) ? value : int.MaxValue;
}
