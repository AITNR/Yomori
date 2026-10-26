// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Yomori.Services;

/// <summary>
/// 代理领导者选举 —— 用「桥目录里的一个独占文件句柄」实现。
///
/// <para>【为什么需要】同账户可能同时存在多条会话（历史遗留、策略未生效、Wrapper 差异），
/// 每条会话登录时都会由公共启动目录拉起一份代理。它们盯着同一座桥：
/// 同一份 command.json 会被多个代理抢读，任务就执行了两遍甚至三遍；
/// 空闲代理的心跳还会把别人正在写的 running 状态覆写成 idle。
/// 领导者选举把「谁能读指令、谁能写心跳」收敛到一个进程。</para>
///
/// <para>【为什么不用命名互斥体】<c>Global\</c> 命名空间需要
/// <c>SeCreateGlobalPrivilege</c>，普通用户账户没有这个权限 —— 而那正是代理运行的身份。
/// 用 <c>Local\</c> 又只在本会话内有效，跨会话就等于没有锁。
/// 文件独占句柄不需要任何特权，跨会话有效，而且进程一死句柄就由内核释放，
/// 不存在"锁文件残留导致再也起不来"的经典问题。</para>
///
/// <para>【代价】文件所在的目录必须对所有可能登录的账户可写（桥目录本来就是这样的），
/// 否则非领导者连尝试打开都做不到，只能一直当跟随者。这一点在
/// <see cref="TryAcquire"/> 里用一次 ACL 修正兜住。</para>
/// </summary>
public sealed class AgentLeadership : IDisposable
{
    private const string LeaderFileName = "agent-leader.lock";

    private FileStream? _handle;
    private int _disposed;

    private AgentLeadership(FileStream handle, string path)
    {
        _handle = handle;
        LockPath = path;
    }

    /// <summary>锁文件路径（审计 / 排障用）。</summary>
    public string LockPath { get; } = string.Empty;

    /// <summary>当前进程是否持有领导权。</summary>
    public bool IsLeader => _handle is not null && Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// 尝试成为领导者。<b>拿不到返回 null（= 跟随者），不抛异常</b> ——
    /// 抢不到锁是完全正常的状态，不该让代理崩掉。
    /// </summary>
    public static AgentLeadership? TryAcquire(string? bridgeDir)
    {
        var dir = (bridgeDir ?? string.Empty).Trim();
        if (dir.Length == 0)
        {
            return null;
        }

        var path = Path.Combine(dir, LeaderFileName);

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                // FileShare.None = 独占。第二个进程会在这一步收到 IOException(共享冲突)。
                var stream = new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                // 记下是谁占着，方便排障时知道锁属于哪个会话 / 进程。
                var text = Encoding.UTF8.GetBytes(
                    $"machine={Environment.MachineName}\tuser={Environment.UserName}\t" +
                    $"session={RdpSessionService.TryGetCurrentSessionId()?.ToString() ?? "?"}\t" +
                    $"pid={Environment.ProcessId}\tacquired={DateTimeOffset.Now:o}{Environment.NewLine}");
                stream.SetLength(0);
                stream.Write(text);
                stream.Flush();

                return new AgentLeadership(stream, path);
            }
            catch (UnauthorizedAccessException) when (attempt == 0)
            {
                // 锁文件是上一个领导者（别的账户）创建的，当前账户没有写权限。
                // 放开一次 ACL 再试 —— 只在第一次失败时做，避免无谓地反复动权限。
                RdpBridge.GrantUsersFullControl(path);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // 不删文件：删掉会和其他会话的 TryAcquire 抢同一个路径，
            // 句柄一关就等于让别人可以立刻接手，这正是我们要的效果。
            _handle?.Dispose();
        }
        catch
        {
            // 忽略
        }
        finally
        {
            _handle = null;
        }
    }
}
