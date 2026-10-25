// -*- coding: utf-8 -*-
using System;
using System.Threading;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 「解锁 / 卸载 / 还原注册表」三个修改动作共用的互斥锁（计划书 3.1、10）。
/// </summary>
/// <remarks>
/// <para>两层锁各有分工：</para>
/// <list type="bullet">
/// <item><b>进程内</b> <see cref="SemaphoreSlim"/>：挡连点。三个按钮都可能被连点，
/// 而每次操作都要停服务、改注册表，撞在一起就是两个流程同时改一台机器。</item>
/// <item><b>跨进程</b> 命名互斥量：挡「主控端开着、用户又双击了一次 exe」或多账户各开一份的情形。
/// 跨进程锁必须放在进程内锁**之后**获取，否则两个进程互等时先拿到的那一方可能被饿死。</item>
/// </list>
/// <para>为什么失败要返回理由而不只是 false：界面需要说清「另一个操作正在跑」还是
/// 「上一次事务没收尾」，这两种情况的处理完全不同。</para>
/// </remarks>
public static class RdpOperationLock
{
    private static readonly SemaphoreSlim InProcess = new(1, 1);

    /// <summary>计划书要求三个修改动作共享的一把锁。带 <c>Global\</c> 前缀以覆盖所有会话。</summary>
    private const string GlobalName = @"Global\Yomori.RdpUnlock.Operation";
    private const string LocalName = @"Local\Yomori.RdpUnlock.Operation";

    /// <summary>当前是否有修改操作在跑（只反映本进程）。</summary>
    public static bool IsBusy => InProcess.CurrentCount == 0;

    /// <summary>
    /// 尝试进入临界区。成功时返回的租约 Dispose 即释放；失败时 <paramref name="reason"/> 说明原因。
    /// </summary>
    /// <remarks>
    /// ⚠️ 跨进程这一层必须用 <see cref="Semaphore"/> 而不是命名 <c>Mutex</c>：
    /// Mutex 是**线程亲和**的，ReleaseMutex 只能由 WaitOne 的那个线程调用。
    /// 而整个解锁流程跨了很多 await —— 续体落在哪个线程由线程池决定，几乎必然换线程，
    /// 那时 ReleaseMutex 会抛 ApplicationException("未同步的代码块")，
    /// 而且是在 using 的 Dispose 里抛，异常会盖掉真正的结果。
    /// 信号量没有线程亲和，可以在任何线程 Release。
    /// <para>崩溃安全也成立：命名内核对象在所有句柄关闭时销毁，
    /// 进程被杀之后下一次启动会拿到一个全新的、计数为 1 的对象，
    /// 不会像 Mutex 那样留下 AbandonedMutex 需要特殊处理。</para>
    /// </remarks>
    public static bool TryAcquire(TimeSpan timeout, out RdpOperationLease? lease, out string reason)
    {
        lease = null;
        reason = "";

        if (!InProcess.Wait(timeout))
        {
            reason = "已有另一个解锁操作正在进行";
            return false;
        }

        Semaphore? semaphore = null;

        try
        {
            semaphore = OpenSemaphore(GlobalName) ?? OpenSemaphore(LocalName);

            if (semaphore is null)
            {
                InProcess.Release();
                reason = "无法创建跨进程互斥量（权限不足）";
                return false;
            }

            if (!semaphore.WaitOne(timeout))
            {
                semaphore.Dispose();
                InProcess.Release();
                reason = "另一个 Yomori 进程正在执行解锁或还原操作";
                return false;
            }

            lease = new RdpOperationLease(GlobalName, () =>
            {
                try
                {
                    semaphore.Release();
                }
                catch (SemaphoreFullException)
                {
                    // 重复释放：忽略，别让 Dispose 抛出去
                }
                finally
                {
                    semaphore.Dispose();
                    InProcess.Release();
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            semaphore?.Dispose();
            InProcess.Release();
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 打开（必要时创建）计数为 1 的命名信号量。
    /// </summary>
    /// <remarks>
    /// <c>Global\</c> 需要 SeCreateGlobalPrivilege（管理员才有）。本程序是 requireAdministrator，
    /// 正常路径不会失败；但为了在非管理员环境下仍能挡住同会话的并发，失败时退到 <c>Local\</c>。
    /// </remarks>
    private static Semaphore? OpenSemaphore(string name)
    {
        try
        {
            return new Semaphore(initialCount: 1, maximumCount: 1, name);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
