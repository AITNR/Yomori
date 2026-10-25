// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 解析 RDP Wrapper 官方 Releases 的最新正式版。
/// </summary>
/// <remarks>
/// <para>计划书 4.1 的硬要求：每次真正需要下载时**重新解析**一次 latest，
/// 不写死 <c>v1.8.9.9</c> 之类的历史版本，也不自己比较字符串挑「最高版本」。
/// GitHub 的 <c>/releases/latest</c> 定义就是「最新的非草稿、非预发布发布」，
/// 正好等于「最新正式版」；草稿与预发布在接口层已经排除了。</para>
/// <para>为什么不用官方的 <c>releases</c> 列表自己排序：那就要处理版本号比较、tag 命名漂移、
/// 预发布混入三种情况，每一种都能把用户送到测试版上。能少一层判断就少一层。</para>
/// <para>⚠️ 只在「用户已经人工确认关闭安全软件」之后调用（计划书 3.2：确认前不预取组件、
/// 连 metadata 都不查）。这个约束由 <see cref="RdpUnlockService"/> 把守。</para>
/// </remarks>
public static class RdpReleaseResolver
{
    public const string Owner = "sebaxakerhtc";
    public const string Repo = "rdpwrap";

    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>
    /// 查最新正式版。失败返回 <c>(null, 原因)</c>，原因直接给用户看。
    /// </summary>
    public static async Task<(RdpReleaseInfo? Release, string Error)> ResolveLatestAsync(
        UpdateNetworkSettings network,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = UpdateService.NetworkClient(network);

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
            // GitHub 对匿名请求要求 User-Agent；Accept 固定版本化媒体类型，避免字段语义漂移
            request.Headers.UserAgent.ParseAdd("Yomori-RdpUnlock");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, "上游还没有正式发布（releases/latest 返回 404）。");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == (HttpStatusCode)429)
            {
                return (null, "GitHub 接口被限流（403/429）。稍后再试，或先在「关于 → 检查更新」里确认网络可用。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"查询上游发布失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(json);
        }
        catch (OperationCanceledException)
        {
            return (null, "查询上游发布被取消或超时。");
        }
        catch (Exception ex)
        {
            // 断网/TLS/代理配错都到这里。不自动换镜像、不重试到别的源（计划书 4.1）
            return (null, $"查询上游发布失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析 releases/latest 的 JSON。公开是为了让冒烟能用固定样本断言字段映射，
    /// 不必联网。
    /// </summary>
    public static (RdpReleaseInfo? Release, string Error) Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "上游返回的内容不是预期的发布对象。");
            }

            var assets = new List<RdpAsset>();

            if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in assetsElement.EnumerateArray())
                {
                    assets.Add(new RdpAsset
                    {
                        Id = ReadLong(item, "id"),
                        Name = ReadString(item, "name"),

                        // GitHub 对部分资产返回 null 的 digest：这里保持空串，
                        // 让校验环节如实说「官方没给摘要」，而不是编一个通过
                        Digest = ReadString(item, "digest"),
                        Size = ReadLong(item, "size"),
                        DownloadUrl = ReadString(item, "browser_download_url"),
                    });
                }
            }

            var release = new RdpReleaseInfo
            {
                Id = ReadLong(root, "id"),
                Tag = ReadString(root, "tag_name"),
                Name = ReadString(root, "name"),
                PageUrl = ReadString(root, "html_url"),
                IsDraft = ReadBool(root, "draft"),
                IsPrerelease = ReadBool(root, "prerelease"),
                PublishedAt = ReadDate(root, "published_at"),
                Assets = assets,
            };

            if (release.Id <= 0 || release.Tag.Length == 0)
            {
                return (null, "上游返回的发布缺少 id 或 tag，无法锁定版本。");
            }

            return (release, "");
        }
        catch (JsonException ex)
        {
            return (null, $"上游发布 JSON 解析失败：{ex.Message}");
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        var text = ReadString(element, name);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }
}
