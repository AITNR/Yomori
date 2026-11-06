// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>一个待测速的下载节点。<c>Prefix</c> 为空且 <c>Direct</c> 为真时表示官方直连。</summary>
public readonly record struct RdpProbeNode(string Name, string Prefix, bool Direct)
{
    public static RdpProbeNode DirectNode => new("官方直连", "", true);
}

/// <summary>单个节点的测速结果。<c>LatencyMs</c> 为空表示不可达。</summary>
public readonly record struct RdpNodeMeasurement(
    string Name,
    string Prefix,
    bool Direct,
    double? LatencyMs,
    string Error)
{
    public bool Ok => LatencyMs.HasValue;

    public string Display => LatencyMs.HasValue
        ? $"{LatencyMs.Value:0} ms"
        : (Error.Length > 0 ? Error : "不可达");
}

/// <summary>
/// 测速选节点：并行测各镜像节点到响应首字节的耗时（TTFB），按延迟升序排序、不可达置底。
/// </summary>
/// <remarks>
/// <para><b>代理语义必须与实际下载一致</b>：这里用 <see cref="UpdateService.NetworkClient"/>
/// 建 client，与 <c>UpdateService.DownloadAsync</c> 共用同一份 <see cref="UpdateNetworkSettings"/>
/// 与缓存键，所以测出来的延迟就是「走设置里那个代理之后」的延迟。若用裸 <c>HttpClient</c>，
/// 在系统代理 / 自定义代理环境下测出来的排名会与实际下载相反。</para>
/// <para>本文件位于 Services，会被 Yomori.Agent 工程链接，因此不得引用 Microsoft.UI.Xaml。</para>
/// </remarks>
public static class RdpAccelerationProbe
{
    /// <summary>单节点超时。测的是 TTFB，4 秒足够判定不可达。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>探针目标：用实际会下载的 INI 地址，Range 只取 1 字节，不下整份 577KB。</summary>
    public const string ProbeTarget =
        "https://raw.githubusercontent.com/sebaxakerhtc/rdpwrap.ini/master/rdpwrap.ini";

    /// <summary>
    /// 候选节点：配置过的自定义节点（若有）→ 两个内置反代 → 官方直连。
    /// 自定义节点与内置重复时跳过，避免列表里出现两行一样的地址。
    /// </summary>
    public static IReadOnlyList<RdpProbeNode> Nodes(string? customPrefix)
    {
        var result = new List<RdpProbeNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var custom = RdpAcceleration.NormalizePrefix(customPrefix);
        if (custom.Length > 0 && seen.Add(custom))
        {
            result.Add(new RdpProbeNode("自定义 " + RdpAcceleration.HostOf(custom), custom, false));
        }

        foreach (var builtIn in RdpAcceleration.BuiltInPrefixes)
        {
            if (seen.Add(builtIn))
            {
                result.Add(new RdpProbeNode(RdpAcceleration.HostOf(builtIn), builtIn, false));
            }
        }

        result.Add(RdpProbeNode.DirectNode);
        return result;
    }

    /// <summary>把测速结果转成 RDP 解锁链路用的加速设置快照。</summary>
    /// <remarks>
    /// 选中镜像节点时把它写成 <c>CustomPrefix</c>：<see cref="RdpAcceleration.Candidates"/>
    /// 里自定义前缀排第一，其余内置候选仍作兜底；选中官方直连时返回 Disabled。
    /// </remarks>
    public static RdpAccelerationSettings ToSettings(RdpNodeMeasurement measurement) =>
        measurement.Direct || measurement.Prefix.Length == 0
            ? RdpAccelerationSettings.Disabled
            : new RdpAccelerationSettings(true, measurement.Prefix);

    /// <summary>排序纯函数：可达按延迟升序，不可达置底，同值按名称稳定排序。供冒烟断言。</summary>
    public static IReadOnlyList<RdpNodeMeasurement> Rank(IEnumerable<RdpNodeMeasurement> results) =>
        results
            .OrderBy(r => r.Ok ? 0 : 1)
            .ThenBy(r => r.LatencyMs ?? double.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 并行测所有节点。任一节点失败不影响其它节点；取消令牌会同时停掉全部请求。
    /// </summary>
    public static async Task<IReadOnlyList<RdpNodeMeasurement>> ProbeAsync(
        IReadOnlyList<RdpProbeNode> nodes,
        UpdateNetworkSettings network,
        IProgress<RdpNodeMeasurement>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (nodes.Count == 0)
        {
            return Array.Empty<RdpNodeMeasurement>();
        }

        var tasks = nodes
            .Select(node => ProbeOneAsync(node, network, progress, cancellationToken))
            .ToArray();

        var measured = await Task.WhenAll(tasks).ConfigureAwait(false);
        return Rank(measured);
    }

    private static async Task<RdpNodeMeasurement> ProbeOneAsync(
        RdpProbeNode node,
        UpdateNetworkSettings network,
        IProgress<RdpNodeMeasurement>? progress,
        CancellationToken cancellationToken)
    {
        var url = node.Direct ? ProbeTarget : node.Prefix + ProbeTarget;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);

        // 与实际下载共用同一个 client 缓存：代理模式、认证、超时语义完全一致
        var client = UpdateService.NetworkClient(network);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            watch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return Fail(node, $"HTTP {(int)response.StatusCode}");
            }

            var ok = new RdpNodeMeasurement(
                node.Name, node.Prefix, node.Direct, watch.Elapsed.TotalMilliseconds, "");
            progress?.Report(ok);
            return ok;
        }
        catch (OperationCanceledException)
        {
            return Fail(node, cancellationToken.IsCancellationRequested ? "测速已取消" : "测速超时");
        }
        catch (Exception ex)
        {
            return Fail(node, ex.GetType().Name);
        }
    }

    private static RdpNodeMeasurement Fail(RdpProbeNode node, string error) =>
        new(node.Name, node.Prefix, node.Direct, null, error);
}
