// -*- coding: utf-8 -*-
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Yomori.Services;

/// <summary>
/// 同账户会话唯一性的<b>准入门闸</b>。
///
/// <para>它把三件事绑在一起（计划书 P0 第 2、3、4 条）：</para>
/// <list type="number">
/// <item><b>按「主机 + 账户 SID」串行化</b>所有连接入口 —— 内嵌、mstsc、自动重连、
/// 多通道并行都要先经过同一个闸。否则两条通道同时判定"没有会话"、同时新建，
/// 就凭空多出两条会话，而每一步单看都"合法"。</item>
/// <item><b>准入判定</b>交给纯函数 <see cref="RdpSessionAdmissionPlanner"/>：
/// 查询失败、多会话、SID 未知一律阻断，并留下可审计清单。</item>
/// <item><b>连接后复核</b>：把连接前的快照和连接后的实际会话比对，
/// 出现预期外的新会话就只断开<b>本次</b>客户端，不碰任何别的会话。</item>
/// </list>
///
/// <para>为什么闸门的键里必须有 SID：同名账户可以属于不同域 / 不同主体。
/// 只用用户名加锁，两个不同主体的连接会互相排队甚至互相"证明"对方已经存在。</para>
/// </summary>
public static class RdpSessionGuard
{
    /// <summary>串行化等待上限。超过就放弃本次连接，而不是无限期占着闸门。</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(60);

    /// <summary>审计环形缓冲的条数上限（界面 / 诊断命令读它，不读文件）。</summary>
    private const int MaxAuditLines = 200;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Queue<string> AuditRing = new();
    private static readonly object AuditGate = new();

    /// <summary>审计落盘位置：桥目录根下的单一文件，两端都读得到，便于事后对齐时间线。</summary>
    public static string AuditPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Yomori",
        "rdp",
        "session-audit.ndjson");

    /// <summary>
    /// 闸门键：主机 + 账户 SID。<b>拿不到 SID 时不允许退化成用户名</b> ——
    /// 退化的那一路正是「同名不同域串台」的入口。调用方在这种情况下应当先阻断。
    /// </summary>
    public static string GateKey(string? host, string? accountSid, string? user)
    {
        var h = (host ?? string.Empty).Trim().ToLowerInvariant();
        var sid = (accountSid ?? string.Empty).Trim();
        if (sid.Length > 0)
        {
            return "SID|" + h + "|" + sid;
        }

        // 仅用于「还没有 SID 的早期阶段」（例如账户尚未创建）的排他，
        // 与 SID 键分成两套，避免两种语义混在一个锁上。
        return "ACCOUNT|" + h + "|" + SessionQueryResult.BareName(user).ToLowerInvariant();
    }

    /// <summary>
    /// 取到闸门（按主机 + SID 串行）。用 <c>await using</c> / <c>using</c> 释放。
    /// 拿不到（超时 / 取消）返回 null —— 调用方必须把 null 当成"不许连接"。
    /// </summary>
    public static async Task<IDisposable?> TryAcquireAsync(
        string? host, string? user, CancellationToken token = default)
    {
        var sid = ResolveSid(host, user);
        var key = GateKey(host, sid, user);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(GateTimeout, token).ConfigureAwait(false))
        {
            Record("闸门超时", $"账户「{SessionQueryResult.BareName(user)}」的连接入口在 {GateTimeout.TotalSeconds:0} 秒内没有让出闸门。");
            return null;
        }

        return new GateLease(gate);
    }

    private sealed class GateLease : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _released;

        public GateLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            try
            {
                _gate.Release();
            }
            catch (SemaphoreFullException)
            {
                // 重复释放：忽略
            }

            // 【故意不把信号量从字典里摘掉、也不 Dispose】
            // 只要还有线程持有 / 等待这个实例，摘掉字典项就会让第三个调用方
            // 拿到一个**新建的**信号量，互斥当场失效；Dispose 则会抛在等待者身上。
            // 字典项的总数等于「出现过的账户数」，量级极小，留着比抢着清理安全得多。
        }
    }

    /// <summary>
    /// 准入判定（同步）。<b>所有连接入口都必须先调它</b>，返回 <c>Allowed == false</c> 就不许连。
    /// </summary>
    /// <param name="host">目标主机（已归一）。</param>
    /// <param name="user">目标账户，允许 <c>DOMAIN\user</c>。</param>
    public static RdpSessionAdmissionResult Evaluate(string? host, string? user)
    {
        var sid = ResolveSid(host, user);
        var query = RdpSessionService.QuerySessions(host);
        var result = RdpSessionAdmissionPlanner.Plan(query, host, user, sid);

        foreach (var line in result.AuditLines)
        {
            Record("准入", line);
        }

        if (!result.Allowed)
        {
            Record("阻断", $"[{result.Code}] {result.Reason}");
        }
        else
        {
            foreach (var warning in result.Warnings)
            {
                Record("警告", warning);
            }
        }

        return result;
    }

    /// <summary>
    /// 连接后的会话身份复核。返回结论 + 完整观测快照，调用方据此决定是否断开本次客户端。
    /// </summary>
    public static RdpSessionVerificationResult VerifyAfterConnect(
        RdpSessionAdmissionResult admission, string? host, string? user)
    {
        var after = RdpSessionService.QuerySessions(host);
        var verification = RdpSessionAdmissionPlanner.VerifyAfterConnect(admission, after, user);

        Record("复核", $"{verification.Verdict}：{verification.Reason}");
        foreach (var line in after.DescribeAll())
        {
            Record("复核快照", line);
        }

        return verification;
    }

    /// <summary>当前可展示的会话清单（「历史会话」只展示不处置，见计划书 P2）。</summary>
    public static List<string> DescribeSessions(string? host)
    {
        var query = RdpSessionService.QuerySessions(host);
        var lines = query.DescribeAll();
        lines.Add($"查询状态：{query.Status}（{(query.Usable ? "可用于准入判断" : "不可用，只能展示")}）");
        return lines;
    }

    /// <summary>最近的审计行（进程内环形缓冲）。</summary>
    public static List<string> RecentAudit(int count = 50)
    {
        lock (AuditGate)
        {
            return AuditRing.Skip(Math.Max(0, AuditRing.Count - Math.Max(1, count))).ToList();
        }
    }

    /// <summary>记一条审计：环形缓冲 + 追加落盘。落盘失败绝不影响主流程。</summary>
    public static void Record(string kind, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz}\t{kind}\t{message}";

        lock (AuditGate)
        {
            AuditRing.Enqueue(line);
            while (AuditRing.Count > MaxAuditLines)
            {
                AuditRing.Dequeue();
            }
        }

        try
        {
            var dir = Path.GetDirectoryName(AuditPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var record = new Dictionary<string, string>
            {
                ["at"] = DateTimeOffset.Now.ToString("o"),
                ["kind"] = kind,
                ["host"] = Environment.MachineName,
                ["pid"] = Environment.ProcessId.ToString(),
                ["session"] = RdpSessionService.TryGetCurrentSessionId()?.ToString() ?? "?",
                ["message"] = message,
            };
            File.AppendAllText(
                AuditPath,
                JsonSerializer.Serialize(record) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            // 审计写不进去不影响连接本身
        }
    }

    /// <summary>把「域\账户」翻成 SID（已带缓存）。取不到返回空串。</summary>
    public static string ResolveSid(string? host, string? user)
    {
        var text = (user ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var slash = text.IndexOf('\\');
        return slash > 0
            ? RdpSessionService.TryResolveAccountSid(text[..slash], text[(slash + 1)..])
            : RdpSessionService.TryResolveAccountSid(string.Empty, text);
    }
}
