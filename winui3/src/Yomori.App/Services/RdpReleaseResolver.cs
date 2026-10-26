// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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


    /// <summary>缓存有效期。1 小时：既能挡住「连点几次」把配额烧光，也仍然接近「最新正式版」。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    /// <summary>缓存文件：<c>%ProgramData%\Yomori\rdp\release-cache.json</c>。</summary>
    public static string CachePath => Path.Combine(
        Path.GetDirectoryName(RdpTransactionStore.RootDir) ?? RdpTransactionStore.RootDir,
        "release-cache.json");

    /// <summary>
    /// 查最新正式版。失败返回 <c>(null, 原因)</c>，原因直接给用户看。
    /// </summary>
    /// <remarks>
    /// <para>三层降级，为的是不在 GitHub 的匿名配额（60 次/小时）上撞墙：</para>
    /// <list type="number">
    /// <item>本地缓存还新鲜（<see cref="CacheTtl"/> 内）→ 直接用，不发请求
    /// （<paramref name="forceRefresh"/> 为真时跳过这一层）；</item>
    /// <item>调官方接口，成功则刷新缓存；</item>
    /// <item>接口被限流或不可用 → 过期缓存可用就用它（标明「离线降级」），否则去发布页读 tag 再拼附件地址。</item>
    /// </list>
    /// <para><b>要下载东西时必须传 <c>forceRefresh: true</c></b>：缓存命中意味着「可能落后于官方最新」，
    /// 而计划书 4.1 要求每次真正需要下载时重新解析 latest（2026-10-26 检查记录 P1）。</para>
    /// <para>第三层的「拼地址」不是猜名称：<c>RDPW_Installer.exe</c> /
    /// <c>RDPW_Uninstaller.exe</c> 是上游固定的两个附件名，拼错会在下载时报 404 而不是静默装错东西。
    /// 而且这条路径拿不到大小与官方摘要，<see cref="RdpAsset.Size"/> 为 0、
    /// digest 为空 —— 校验环节会如实说明，不会假装验过。</para>
    /// </remarks>
    public static async Task<(RdpReleaseInfo? Release, string Error)> ResolveLatestAsync(
        UpdateNetworkSettings network,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        // ---- 1. 新鲜缓存：连请求都不发 ----
        // ⚠️ forceRefresh=true 时**跳过**这一层：真正要下载东西的时候，
        // 一小时内的缓存可能已经落后于官方发布（2026-10-26 检查记录 P1）。
        // 缓存只保留给「用户点重新检测 / 诊断」这类不下载的场合，
        // 以及接口彻底不可用时的离线降级（下面第 2 层）。
        var cached = TryReadCache();
        if (!forceRefresh && cached.Json.Length > 0 && cached.Age <= CacheTtl)
        {
            var (cachedRelease, cachedError) = Parse(cached.Json);
            if (cachedRelease is not null)
            {
                cachedRelease.Source = $"本地缓存（{cached.Age.TotalMinutes:0} 分钟前解析）";
                return (cachedRelease, "");
            }

            _ = cachedError;   // 缓存坏了就当没有，往下走接口
        }

        string apiFailure;

        try
        {
            var client = UpdateService.NetworkClient(network);

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
            // GitHub 对匿名请求要求 User-Agent；Accept 固定版本化媒体类型，避免字段语义漂移
            request.Headers.UserAgent.ParseAdd("Yomori-RdpUnlock");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            // 配了令牌就用：匿名 60 次/小时 → 认证 5000 次/小时（见 GitHubTokenStore）
            var token = GitHubTokenStore.Read();
            if (token is not null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, "上游还没有正式发布（releases/latest 返回 404）。");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == (HttpStatusCode)429)
            {
                apiFailure = DescribeRateLimit(response);
            }
            else if (!response.IsSuccessStatusCode)
            {
                apiFailure = $"查询上游发布失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            }
            else
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var (release, error) = Parse(json);

                if (release is not null)
                {
                    WriteCache(json);
                    release.Source = "官方接口";
                }

                return (release, error);
            }
        }
        catch (OperationCanceledException)
        {
            apiFailure = "查询上游发布被取消或超时。";
        }
        catch (Exception ex)
        {
            // 断网/TLS/代理配错都到这里。不自动换镜像、不重试到别的源（计划书 4.1）
            apiFailure = $"查询上游发布失败：{ex.GetType().Name}: {ex.Message}";
        }

        // ---- 2. 接口不通：过期缓存也比没有强 ----
        if (cached.Json.Length > 0)
        {
            var (staleRelease, _) = Parse(cached.Json);
            if (staleRelease is not null)
            {
                // 离线降级必须说清楚：这不是「最新」，是「上一次解析到的」。
                // 界面与步骤里会把它原样显示出来（2026-10-26 检查记录 P1）。
                staleRelease.Source =
                    $"离线降级：本地缓存（{cached.Age.TotalHours:0.#} 小时前的解析，"
                    + $"官方接口暂不可用：{apiFailure}）";
                return (staleRelease, "");
            }
        }

        // ---- 3. 最后一条路：不碰 api.github.com，直接读发布页跳转 ----
        var (tag, tagError) = await TryResolveTagFromReleasePageAsync(network, cancellationToken)
            .ConfigureAwait(false);

        if (tag.Length > 0)
        {
            var built = BuildReleaseFromTag(tag);
            built.Source = $"发布页重定向（接口暂不可用：{apiFailure}）";
            return (built, "");
        }

        return (null, apiFailure + "　也没能从发布页取到版本号：" + tagError
                      + "　可以稍等到限流恢复后重试，或用浏览器手动下载安装器再点「指定安装包」。");
    }

    /// <summary>
    /// 把 403/429 翻译成有用的信息：剩余配额、恢复时间、以及两条可走的路。
    /// </summary>
    private static string DescribeRateLimit(HttpResponseMessage response)
    {
        var text = "GitHub 接口限流（匿名每小时 60 次）";

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
        {
            text += $"，剩余 {remaining.FirstOrDefault() ?? "0"} 次";
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out var seconds)
            && seconds > 0)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
            text += $"，{when:HH:mm} 左右恢复";
        }

        return text + "。可以在本页填一个 GitHub 访问令牌（配额提到每小时 5000 次），"
                      + "或等到恢复后再试，或用浏览器手动下载安装器后点「指定安装包」。";
    }

    /// <summary>
    /// 走 <c>/releases/latest</c> 的 HTTP 重定向取 tag，**不经过 api.github.com**。
    /// </summary>
    /// <remarks>
    /// 发布页是普通网页请求，不吃接口配额。HttpClient 默认跟随重定向，
    /// 最终的请求地址形如 <c>/releases/tag/v1.8.9.9</c>，从里面取最后一段即可。
    /// </remarks>
    private static async Task<(string Tag, string Error)> TryResolveTagFromReleasePageAsync(
        UpdateNetworkSettings network,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = UpdateService.NetworkClient(network);
            var url = $"https://github.com/{Owner}/{Repo}/releases/latest";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 不要求 HTML：只要跳转后的地址
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return ("", $"HTTP {(int)response.StatusCode}");
            }

            var finalUri = response.RequestMessage?.RequestUri?.ToString() ?? "";
            var tag = TagFromUrl(finalUrl: finalUri);
            return tag.Length > 0 ? (tag, "") : ("", "跳转后的地址里没有 tag：" + finalUri);
        }
        catch (Exception ex)
        {
            return ("", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>从 <c>.../releases/tag/&lt;tag&gt;</c> 里取 tag。纯函数，供冒烟断言。</summary>
    public static string TagFromUrl(string finalUrl)
    {
        const string Marker = "/releases/tag/";
        var index = finalUrl.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "";
        }

        var tag = finalUrl.Substring(index + Marker.Length).Trim().Trim('/');
        var cut = tag.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
        {
            tag = tag.Substring(0, cut);
        }

        return tag;
    }

    /// <summary>
    /// 只凭 tag 构造一份发布信息（拿不到大小与官方摘要）。
    /// </summary>
    /// <remarks>
    /// 两个附件名是上游固定的，不是从候选里猜出来的；拼错会在下载阶段 404。
    /// <see cref="RdpAsset.Size"/> 保持 0，于是「大小核对」这一步会被跳过 ——
    /// 这是真实的能力损失，所以 <see cref="RdpReleaseInfo.Source"/> 必须如实标注来源。
    /// </remarks>
    public static RdpReleaseInfo BuildReleaseFromTag(string tag)
    {
        var baseUrl = $"https://github.com/{Owner}/{Repo}/releases/download/{tag}";

        return new RdpReleaseInfo
        {
            Id = 0,
            Tag = tag,
            Name = tag,
            PageUrl = $"https://github.com/{Owner}/{Repo}/releases/tag/{tag}",
            Assets = new List<RdpAsset>
            {
                new() { Name = InstallerFileName, DownloadUrl = $"{baseUrl}/{InstallerFileName}" },
                new() { Name = UninstallerFileName, DownloadUrl = $"{baseUrl}/{UninstallerFileName}" },
            },
        };
    }

    /// <summary>上游固定的安装器附件名。</summary>
    public const string InstallerFileName = "RDPW_Installer.exe";

    /// <summary>上游固定的卸载器附件名。</summary>
    public const string UninstallerFileName = "RDPW_Uninstaller.exe";

    private readonly record struct CachedRelease(string Json, TimeSpan Age);

    private static CachedRelease TryReadCache()
    {
        try
        {
            var path = CachePath;
            if (!File.Exists(path))
            {
                return new CachedRelease("", TimeSpan.MaxValue);
            }

            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var raw = document.RootElement.TryGetProperty("json", out var rawElement) ? rawElement.GetString() ?? "" : "";
            var at = document.RootElement.TryGetProperty("cached_at", out var atElement)
                     && DateTimeOffset.TryParse(atElement.GetString(), out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            var age = DateTimeOffset.Now - at;
            return new CachedRelease(raw, age < TimeSpan.Zero ? TimeSpan.MaxValue : age);
        }
        catch
        {
            return new CachedRelease("", TimeSpan.MaxValue);
        }
    }

    private static void WriteCache(string rawJson)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["cached_at"] = DateTimeOffset.Now.ToString("O"),
                ["json"] = rawJson,
            });

            File.WriteAllText(CachePath, payload);
        }
        catch
        {
            // 缓存写不进去只是下次多消耗一次配额，不该影响这次解锁
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
