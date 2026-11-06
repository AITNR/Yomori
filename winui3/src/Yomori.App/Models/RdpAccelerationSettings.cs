// -*- coding: utf-8 -*-
namespace Yomori.Models;

/// <summary>
/// RDP 解锁下载链路的 GitHub 加速设置快照。
/// </summary>
/// <remarks>
/// <para>与 <c>update_proxy_url</c>（HTTP 代理 / WebProxy）是两回事：这里是 URL 前缀重写，
/// 两者可以叠加，但语义不同，不能共用一个配置项。</para>
/// <para>传值而不是传 <see cref="AppConfig"/>，避免后台线程长时间持有可变对象。</para>
/// </remarks>
public readonly record struct RdpAccelerationSettings(bool Enabled, string CustomPrefix)
{
    /// <summary>未启用 / 无自定义前缀时的默认快照。</summary>
    public static RdpAccelerationSettings Disabled => new(false, string.Empty);

    public static RdpAccelerationSettings FromConfig(AppConfig config) =>
        new(config is not null && config.RdpAccelerationEnabled,
            config?.RdpAccelerationPrefix ?? string.Empty);
}
