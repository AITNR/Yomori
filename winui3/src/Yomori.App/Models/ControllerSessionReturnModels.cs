// -*- coding: utf-8 -*-
namespace Yomori.Models;

/// <summary>
/// 自动切回主控会话的结果分类。
///
/// <para>刻意区分「已达成」与「刚执行成功」：用户可能本来就在控制台，
/// 那不该报一次切换；而命令退出码为 0 也不代表桌面真的回来了 ——
/// 唯一判据是复核时控制台会话 ID 等于主控会话 ID。</para>
/// </summary>
public enum ControllerSessionReturnOutcome
{
    /// <summary>功能开关关闭，未尝试。</summary>
    Disabled,

    /// <summary>本次没有任何本机通道，不产生切回意图。</summary>
    NoLocalChannel,

    /// <summary>已经在主控会话的控制台上，无需动作。</summary>
    AlreadyConsole,

    /// <summary>判定需要执行切回（仅表示「该做」，不代表已做）。</summary>
    Needed,

    /// <summary>执行后复核通过：控制台已回到主控会话。</summary>
    Succeeded,

    /// <summary>tscon 进程返回非零退出码，且复核未通过。</summary>
    CommandFailed,

    /// <summary>tscon 在限定时间内没有结束，结果不确定。</summary>
    Timeout,

    /// <summary>命令已返回但没有切到目标会话（或控制台暂未附着）。</summary>
    VerifyFailed,

    /// <summary>主控身份或控制台会话查询失败，不做猜测。</summary>
    QueryFailed,

    /// <summary>用户停止、退出或关闭开关，请求在提交前被取消。</summary>
    Cancelled,

    /// <summary>已有一次切回在执行，本次合并丢弃。</summary>
    Busy,
}

/// <summary>一次自动切回请求的完整结果，用于日志与界面提示。</summary>
public sealed class ControllerSessionReturnResult
{
    public ControllerSessionReturnOutcome Outcome { get; init; } = ControllerSessionReturnOutcome.Disabled;

    /// <summary>来源标签，例如「任务启动」「手动连接」，只用于日志。</summary>
    public string Source { get; init; } = string.Empty;

    public uint ControllerSessionId { get; init; }

    public uint ConsoleSessionIdBefore { get; init; }

    public uint ConsoleSessionIdAfter { get; init; }

    /// <summary>面向用户的一句话说明；失败时包含可定位原因。</summary>
    public string Detail { get; init; } = string.Empty;

    public bool IsSuccess =>
        Outcome is ControllerSessionReturnOutcome.Succeeded or ControllerSessionReturnOutcome.AlreadyConsole;
}

/// <summary>
/// 切回策略的纯逻辑判定（无 IO、无 UI 依赖），便于冒烟直接覆盖。
/// </summary>
public static class ControllerSessionReturnPolicy
{
    /// <summary>
    /// 决定是否真的需要执行系统命令。返回 <see cref="ControllerSessionReturnOutcome.AlreadyConsole"/>
    /// 表示目标已达成，调用方只记一条日志即可。
    /// </summary>
    public static ControllerSessionReturnOutcome Decide(
        bool enabled,
        bool hasLocalChannel,
        uint controllerSessionId,
        bool consoleKnown,
        uint consoleSessionId)
    {
        if (!enabled)
        {
            return ControllerSessionReturnOutcome.Disabled;
        }

        if (!hasLocalChannel)
        {
            return ControllerSessionReturnOutcome.NoLocalChannel;
        }

        // 0 不是合法交互会话；查询不到时也不猜。
        if (controllerSessionId == 0)
        {
            return ControllerSessionReturnOutcome.QueryFailed;
        }

        if (!consoleKnown)
        {
            return ControllerSessionReturnOutcome.QueryFailed;
        }

        return consoleSessionId == controllerSessionId
            ? ControllerSessionReturnOutcome.AlreadyConsole
            : ControllerSessionReturnOutcome.Needed;
    }
}
