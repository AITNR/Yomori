// -*- coding: utf-8 -*-
using System;

namespace Yomori.Services;

/// <summary>
/// 配置自动保存的节流状态机。
///
/// <para><b>它只回答"什么时候该写盘"，不写盘、不持有时钟。</b>计时器在宿主
/// （<c>MainViewModel</c> 的 <c>DispatcherQueueTimer</c>）里，这里只提供纯逻辑：
/// 去抖窗口内的多次改动合并成一次、落盘期间到达的通知一律吞掉。</para>
///
/// <para><b>为什么必须有"落盘期间吞掉"这一条：</b>落盘内核自己就会改动被订阅的对象
/// —— 它重排 <c>Order</c>、给 <c>Config.Tasks</c> 赋值、调 <c>Sanitize()</c>。
/// 这些通知与用户操作走同一条链路，不吞掉就会形成"落盘 → 触发通知 → 再排一次落盘"
/// 的空转：每 600 毫秒写一次 config.json，永远停不下来。
/// 落盘在 UI 线程上同步执行，期间用户不可能改任何东西，所以吞掉不会丢改动。</para>
///
/// <para>不依赖 WinUI，冒烟工程可以直接链接做行为断言（见 <c>_smoke/AutoSaveCheck.cs</c>）。</para>
/// </summary>
public sealed class ConfigAutoSaver
{
    /// <summary>
    /// 去抖窗口（毫秒）。取值理由：任务字段大多是失焦才写回，一次性连改几个框的间隔
    /// 通常在此之内；再长会让"改完马上关程序"的窗口变大，再短会把一次编辑拆成多次写盘。
    /// </summary>
    public const int DefaultDelayMs = 600;

    private bool _pending;
    private bool _applying;

    public ConfigAutoSaver(int delayMs = DefaultDelayMs)
    {
        DelayMs = Math.Max(1, delayMs);
    }

    /// <summary>去抖窗口长度（毫秒），宿主按它设置计时器间隔。</summary>
    public int DelayMs { get; }

    /// <summary>有没有尚未落盘的改动。</summary>
    public bool HasPending => _pending;

    /// <summary>是否正处在落盘过程中。</summary>
    public bool IsApplying => _applying;

    /// <summary>
    /// 标记一次改动。
    /// </summary>
    /// <returns>
    /// true = 调用方应当（重新）启动延迟计时器；false = 这次标记被忽略
    /// （未启用 / 正在落盘）。去抖语义要求"每次标记都重新计时"，
    /// 因此第二次及以后的标记同样返回 true。
    /// </returns>
    public bool MarkDirty()
    {
        if (_applying)
        {
            return false;
        }

        _pending = true;
        return true;
    }

    /// <summary>
    /// 进入落盘。已经在落盘中、或根本没有待存改动时返回 false（调用方直接返回即可）。
    /// 返回值只表示"这次轮到你写了"，落盘本身成功与否由宿主处理。
    /// </summary>
    public bool BeginApply()
    {
        if (_applying || !_pending)
        {
            return false;
        }

        _applying = true;
        _pending = false;
        return true;
    }

    /// <summary>退出落盘。之后到达的改动才会重新变成"待存"。</summary>
    public void EndApply() => _applying = false;

    /// <summary>丢弃待存标记（不落盘）。只在明确知道改动不该保存时用。</summary>
    public void Clear()
    {
        _pending = false;
        _applying = false;
    }
}
