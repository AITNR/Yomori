// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>检查更新的结论。</summary>
public enum UpdateCheckStatus
{
    /// <summary>远端最新版与当前版本一致。</summary>
    UpToDate,

    /// <summary>远端有比当前更新的版本。</summary>
    UpdateAvailable,

    /// <summary>当前是本机自编译的开发版，和发布通道上的版本没法直接比。</summary>
    LocalBuild,

    /// <summary>当前版本比远端公开的还新（远端那份被回退过，或本机提前跑了自编译包）。</summary>
    LocalNewer,

    /// <summary>该通道还没有发布过（正式版常见 —— 还没发过正式版时就是这个）。</summary>
    NoRelease,

    /// <summary>没查成（网络、限流、解析失败）。</summary>
    Failed,
}

/// <summary>一次检查更新的结果。界面只负责把它画出来，文案在服务里统一造。</summary>
public sealed class UpdateCheckResult
{
    public required UpdateCheckStatus Status { get; init; }

    /// <summary>给用户看的结论（一句话，含两边版本号）。</summary>
    public required string Message { get; init; }

    /// <summary>写进运行日志的一行（含通道、远端 tag 等排障信息）。</summary>
    public required string LogLine { get; init; }

    /// <summary>远端那份发布的详情；没查到时为 null。</summary>
    public GitHubRelease? Latest { get; init; }
}

/// <summary>GitHub 上的一份发布（只留本程序用得上的字段）。</summary>
public sealed class GitHubRelease
{
    public required string Tag { get; init; }

    /// <summary>发布标题（形如「测试版（滚动更新）· 2.4.1-dev.42」）。</summary>
    public required string Title { get; init; }

    /// <summary>版本号（从 tag 或资产文件名里解析出来；解析不出时为空串）。</summary>
    public required string Version { get; init; }

    /// <summary>发布页面地址（浏览器打开用）。</summary>
    public required string PageUrl { get; init; }

    /// <summary>
    /// 发布说明原文（GitHub 的 <c>body</c>，Markdown）。可能为空。
    ///
    /// <para>不是 required：老的调用点（冒烟里的夹具）不关心它，
    /// 而且它只影响通知里多出的那几行「更新内容」，缺了也不该编不过。</para>
    ///
    /// <para>怎么读成人话见 <see cref="UpdateNotes"/>（正文里带表格、代码块、下载说明，
    /// 直接往通知里塞会把有用的那句挤没）。</para>
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>安装版直链（<c>*-setup.exe</c>），没有就是 null。</summary>
    public string? SetupUrl { get; init; }

    /// <summary>绿色版直链（<c>*.zip</c>），没有就是 null。</summary>
    public string? ZipUrl { get; init; }

    public required bool IsPrerelease { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// 「检查更新」：拿 GitHub Releases 和本机版本号比一比。
///
/// 为什么直接查 GitHub Releases 而不是另外搭一个版本接口：
///   程序本来就是从 GitHub 分发的（正式版由 release.yml 手动发 vX.Y.Z，测试版由 ci.yml
///   每次 main 提交推一份滚动预发布），Releases 里已经同时写着「谁是正式版」「谁是测试版」
///   「下载在哪」这三件事。再维护一份是多余的，还会跟发布流程漂。
///
/// 两条通道各查什么：
///   · <see cref="UpdateChannels.Stable"/>（正式版）→ <c>/releases/latest</c>。
///     GitHub 对它的定义就是「最新的非草稿、非预发布发布」，正好等于「最新正式版」。
///     ⚠️ 一份正式版都没发过时这个接口返回 404 —— 那不是错误，是「还没发过正式版」。
///   · <see cref="UpdateChannels.Beta"/>（测试版）→ <c>/releases</c> 列表里挑**版本号最大**的一份
///     （含预发布）。滚动 tag <c>test-build</c> 与它对应的固定归档 <c>build-N</c> 版本号相同，
///     同版本时优先取 <c>test-build</c>（那是给「只想拿最新版」的人准备的固定入口）。
///
/// 版本号从哪来（顺序是固定的，因为滚动预发布的 tag 里**没有**版本号）：
///   1) tag 本身就是版本号（正式版 <c>v2.1.0</c>）→ 直接用；
///   2) 否则从资产文件名里抠：<c>Yomori-winui3-&lt;版本&gt;-x64.zip</c> /
///      <c>Yomori-&lt;版本&gt;-setup.exe</c>。版本号自己带 '-'（2.4.1-dev.42），所以靠前后缀锚定。
///
/// 网络与限流：GitHub 匿名接口每小时 60 次（按出口 IP 计）。设置页填了 GitHub 访问令牌就
/// 自动带上 —— 两条通道（手动「检查更新」与启动时自动检查）都走这里，配额提到每小时 5000 次。
/// 令牌被拒（401，过期或被撤销）时**不**让整次检查作废：退回匿名再试一次并在日志里写明；
/// 真被限流（403 且额度为 0）也会如实说明，绝不装作「已是最新」。
/// </summary>
public static class UpdateService
{
    /// <summary>仓库归属。README / 安装程序里的下载入口与这里必须一致。</summary>
    public const string Owner = "AITNR";

    /// <summary>
    /// 仓库名。
    ///
    /// ⚠️ 2026-10 产品从「Anigame-script-manager」改名为「Yomori」，GitHub 侧跟着改了：
    /// 旧名会 301 跳过来，但每跳一次都多一回重定向，而且接口地址写旧名迟早会被清理。
    /// 这里写的是**改后的**名字 —— 与 installer.iss 的 <c>MyAppURL</c>、
    /// 设置页「关于」里的 GitHub 按钮保持一致。
    /// </summary>
    public const string Repo = "Yomori";

    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";

    /// <summary>本机自编译标记（与 csproj 里 Version 的兜底值一致）。</summary>
    public const string LocalBuildMarker = "-dev.local";

    /// <summary>默认网络设置（跟随系统代理）。界面上的「检查更新」按钮没传设置时用它。</summary>
    public static readonly UpdateNetworkSettings DefaultNetwork =
        new(UpdateProxyModes.System, string.Empty);

    /// <summary>
    /// 按「代理模式 + 地址」缓存 HttpClient。
    ///
    /// 为什么缓存而不是每次新建：HttpClient 每次 new 都会带走一个连接池，检查更新虽然不频繁，
    /// 但下载完再检查一次就会漏一个；而且代理设置一变（用户在设置里改地址）只该换 client，
    /// 不该影响正在跑的那次请求。
    /// </summary>
    private static readonly Dictionary<string, HttpClient> Clients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 代理设置的"代次"：地址或认证一变就 +1，缓存键跟着变，下一次请求按新设置重建 client。
    ///
    /// 为什么需要它：认证密码不写在地址里（它进凭据管理器），所以「只改了密码」这件事
    /// 从地址上看不出来 —— 光靠 <c>mode|url</c> 当键会一直拿回带旧密码的那个 client，
    /// 表现是「改了密码还是 407」。
    /// </summary>
    private static int _clientGeneration;

    /// <summary>代理地址 / 认证改了：让下一次请求用新设置重建 client。见 <see cref="_clientGeneration"/>。</summary>
    public static void InvalidateClients() => Interlocked.Increment(ref _clientGeneration);

    /// <summary>默认请求超时。<c>ResponseHeadersRead</c> 也按整体计时，所以它同时是下载的总预算。</summary>
    private static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(20);

    /// <param name="timeout">
    /// 本次请求的超时；传 null 用 <see cref="DefaultHttpTimeout"/>。
    /// ⚠️ 超时是 <see cref="HttpClient"/> 级属性，不能按请求改 —— 所以它必须进缓存键，
    /// 否则「先按 20 秒建了 client，再要 180 秒」会拿回那个短的那个（表现为大文件总在 20 秒断流）。
    /// </param>
    private static HttpClient GetClient(UpdateNetworkSettings network, TimeSpan? timeout = null)
    {
        var mode = UpdateProxyModes.Normalize(network.ProxyMode);
        var proxyUrl = (network.ProxyUrl ?? string.Empty).Trim();
        var budget = timeout ?? DefaultHttpTimeout;
        var key = mode + "|" + proxyUrl + "|" + budget.TotalSeconds.ToString("0") + "|"
                  + Volatile.Read(ref _clientGeneration);

        lock (Clients)
        {
            if (Clients.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var created = CreateClient(mode, proxyUrl, budget);
            Clients[key] = created;
            return created;
        }
    }

    private static HttpClient CreateClient(string proxyMode, string proxyUrl, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();

        switch (proxyMode)
        {
            case UpdateProxyModes.None:
                // 直连：系统代理配错（或走了不通的梯子）时用它绕开
                handler.UseProxy = false;
                break;

            case UpdateProxyModes.Custom:
                if (proxyUrl.Length == 0)
                {
                    // 空 → 不使用代理。用户要的语义就是"填了才走代理"，
                    // 而不是"选了自定义却没填，于是又偷偷走系统代理"。
                    handler.UseProxy = false;
                    break;
                }

                var endpoint = ResolveProxy(proxyUrl);
                if (endpoint is null)
                {
                    // 填了但解析不出来 → 退回系统代理，而不是"静默不用代理"。
                    // 检查更新照常能走；用户会从结果里看到是网络问题还是没更新。
                    handler.UseProxy = true;
                    handler.Proxy = HttpClient.DefaultProxy;
                    break;
                }

                var proxy = new WebProxy(endpoint.Value.Address);
                if (endpoint.Value.HasUser)
                {
                    // 用户名有、密码为空也照样设：有的代理只认用户名当口令。
                    // 401 时底层会按 Basic / Digest 协商自动带上这份凭据。
                    proxy.Credentials = new NetworkCredential(endpoint.Value.User, endpoint.Value.Password);
                }

                handler.Proxy = proxy;
                handler.UseProxy = true;
                break;

            default:
                handler.UseProxy = true;
                handler.Proxy = HttpClient.DefaultProxy;
                break;
        }

        var client = new HttpClient(handler) { Timeout = timeout };

        // GitHub 接口要求带 User-Agent，缺了直接 403；Accept 用官方推荐的那个
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Yomori", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    /// <summary>
    /// 一次检查更新里「令牌用得怎么样」的记录本：用没用上、有没有被拒。
    /// 最后拼进日志行 —— 启动时的检查只在日志里说话，令牌被拒这件事必须留痕，
    /// 否则用户只会看到「查不到更新」，永远不知道是令牌过期了。
    /// </summary>
    private sealed class GitHubCallContext
    {
        /// <summary>本次请求真的带上了访问令牌。</summary>
        public bool UsedToken;

        /// <summary>需要写进日志的异常情况（目前只有「令牌被拒、已退回匿名」）。</summary>
        public string? Note;
    }

    /// <summary>
    /// 发一个 GitHub REST GET 请求：配了访问令牌就带 <c>Authorization: Bearer</c>。
    ///
    /// <para><b>为什么按请求加头，而不是挂在 <see cref="HttpClient"/> 的默认头上</b>：
    /// client 是按代理设置缓存共享的，同一个 client 还要下载安装包（
    /// <c>objects.githubusercontent.com</c>）。令牌跟着默认头走就会跨主机发出去 ——
    /// 那是把凭据递给第三方主机，不能接受。</para>
    ///
    /// <para><b>令牌被拒要退回匿名</b>：令牌过期/撤销时 GitHub 返回 401，如果直接把它当失败，
    /// 这台机器上的更新检查就静默瘫痪了（启动检查只在日志里写一行，用户根本不会去看）。
    /// 所以退回匿名再试一次，结果照常返回，日志里补一句「令牌被拒」。</para>
    /// </summary>
    private static async Task<HttpResponseMessage> GetGitHubAsync(
        HttpClient client,
        string url,
        GitHubCallContext call,
        CancellationToken cancellationToken)
    {
        // 每发一次读一次：令牌在设置页保存/清除后不需要重启，下一次检查就按新的来。
        var token = GitHubTokenStore.Read();

        call.UsedToken = token is not null;
        var response = await SendOnceAsync(client, url, token, cancellationToken).ConfigureAwait(false);

        if (token is null || response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        call.UsedToken = false;
        call.Note = "配置的 GitHub 访问令牌被 GitHub 拒绝（401），本次已退回匿名请求；"
            + "请到「设置 → 开发者工具」重新保存或清除令牌";

        return await SendOnceAsync(client, url, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(
        HttpClient client,
        string url,
        string? token,
        CancellationToken cancellationToken)
    {
        var request = BuildGitHubRequest(url, token);

        try
        {
            // 默认按 ResponseContentRead 收完内容，所以这里释放 request 不会碰到已缓冲的响应体。
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>
    /// 造一个查 GitHub 接口的 GET 请求：给了令牌才带 <c>Authorization: Bearer</c>。
    ///
    /// <para>公开给冒烟断言用（同 <see cref="PickNewest"/> 的理由）：这个函数决定「令牌发不发出去」，
    /// 而它一旦写错，表现是配额没提上去或者凭据漏到不该去的主机 —— 两种都不会当场报错。</para>
    /// </summary>
    internal static HttpRequestMessage BuildGitHubRequest(string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return request;
    }

    /// <summary>把「令牌被拒」这类旁注拼到日志行后面；没事就是原样。</summary>
    private static string WithTokenNote(string logLine, GitHubCallContext? call) =>
        call?.Note is { Length: > 0 } note ? logLine + "　[" + note + "]" : logLine;

    /// <summary>
    /// 解析代理地址；地址里没带密码时从 Windows 凭据管理器补上
    /// （密码不落 config.json，见 <see cref="UpdateProxySecretStore"/>）。
    /// 返回 <c>null</c> 表示地址本身解析不出来。
    /// </summary>
    private static ProxyAddress? ResolveProxy(string proxyUrl)
    {
        if (!UpdateProxyModes.TryParse(proxyUrl, out var address))
        {
            return null;
        }

        if (address.HasUser && address.Password.Length == 0)
        {
            address = address with { Password = UpdateProxySecretStore.Read() };
        }

        return address;
    }

    /// <summary>
    /// 当前版本号（形如 <c>2.4.1-dev.42</c>）。
    ///
    /// 来源是程序集信息版本：CI 用 <c>-p:Version=</c> 写进去，本地编译用 csproj 里的兜底值。
    /// 信息版本默认形如 <c>2.4.1-dev.42+7f3a1c2</c>（'+' 后面是提交哈希），显示与比较时都要去掉。
    /// </summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>提交哈希（信息版本里 '+' 之后那段，取前 7 位）；拿不到就是空串。</summary>
    public static string CurrentCommit { get; } = ReadCurrentCommit();

    /// <summary>是不是本机自编译的开发版（没有对应的发布可比）。</summary>
    public static bool IsLocalBuild { get; } =
        CurrentVersion.EndsWith(LocalBuildMarker, StringComparison.OrdinalIgnoreCase);

    private static string ReadCurrentVersion()
    {
        var raw = ReadInformationalVersion();

        // 去掉 '+' 后面的提交哈希（MSBuild 默认会带上 SourceRevisionId）
        var plus = raw.IndexOf('+');
        if (plus >= 0)
        {
            raw = raw[..plus];
        }

        raw = raw.Trim();
        return raw.Length > 0 ? raw : "(未知版本)";
    }

    private static string ReadCurrentCommit()
    {
        var raw = ReadInformationalVersion();
        var plus = raw.IndexOf('+');
        if (plus < 0 || plus + 1 >= raw.Length)
        {
            return string.Empty;
        }

        var sha = raw[(plus + 1)..].Trim();
        return sha.Length > 7 ? sha[..7] : sha;
    }

    private static string ReadInformationalVersion()
    {
        try
        {
            var assembly = typeof(UpdateService).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational;
            }

            // 兜底：信息版本属性缺了就用程序集版本（形如 2.0.0.0）
            return assembly.GetName().Version?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 按给定通道查一次更新。**不抛异常** —— 失败也会变成一条 <see cref="UpdateCheckStatus.Failed"/> 结果，
    /// 让调用方（界面）只有一个分支要写。
    /// </summary>
    public static Task<UpdateCheckResult> CheckAsync(string channel, CancellationToken cancellationToken = default)
        => CheckAsync(channel, DefaultNetwork, cancellationToken);

    /// <summary>
    /// 带网络设置的重载：启动时自动检查与手动「检查更新」都走这里，
    /// 代理模式由设置页决定（系统 / 自定义 / 不使用）。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(
        string channel,
        UpdateNetworkSettings network,
        CancellationToken cancellationToken = default)
    {
        var normalized = UpdateChannels.Normalize(channel);
        var channelLabel = UpdateChannels.Label(normalized);

        // 这一次请求用没用上令牌、令牌有没有被拒，都记在这里，最后拼进日志行。
        var call = new GitHubCallContext();

        try
        {
            var client = GetClient(network);

            var release = normalized == UpdateChannels.Beta
                ? await FetchNewestPrereleaseAsync(client, call, cancellationToken).ConfigureAwait(false)
                : await FetchLatestStableAsync(client, call, cancellationToken).ConfigureAwait(false);

            if (release is null)
            {
                // 两条通道"什么都没有"的含义不同，文案必须分开：
                //   · 正式版：/releases/latest 返回 404 = 一份正式版都还没发过（正常状态）；
                //   · 测试版：列表里没有被标成预发布的 = CI 没在跑，或发布时漏了 prerelease 标记。
                return normalized == UpdateChannels.Beta
                    ? new UpdateCheckResult
                    {
                        Status = UpdateCheckStatus.NoRelease,
                        Message = $"测试版通道上还没有构建（当前 {CurrentVersion}）。"
                            + "CI 每推一次 main 才会发一份；想看正式发布请把更新渠道切到「正式版」。",
                        LogLine = WithTokenNote(
                            $"[更新检查 · {channelLabel}] 发布列表里没有标记为预发布的构建。", call),
                    }
                    : new UpdateCheckResult
                    {
                        Status = UpdateCheckStatus.NoRelease,
                        Message = $"还没有发布过正式版（当前 {CurrentVersion}）。"
                            + "想看自动构建的测试版，把更新渠道切到「测试版」。",
                        LogLine = WithTokenNote(
                            $"[更新检查 · {channelLabel}] 正式版通道暂无发布（GitHub 返回 404）。", call),
                    };
            }

            if (release.Version.Length == 0)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Failed,
                    Message = $"查到最新发布是 {release.Tag}，但里面没有可识别的版本号，没法比较。",
                    LogLine = WithTokenNote(
                        $"[更新检查 · {channelLabel}] 发布 {release.Tag} 的 tag 与资产名里都没有版本号。", call),
                    Latest = release,
                };
            }

            // 本机自编译的开发版（-dev.local）没有对应的发布可比 —— 版本号前缀一样大，
            // 拿它去比只会得出"比远端新"这种误导结论，所以单列一个状态，只把远端版本报出来。
            if (IsLocalBuild)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.LocalBuild,
                    Message = $"当前是本机自编译的开发版（{CurrentVersion}）。"
                        + $"{channelLabel}通道上最新的是 {release.Version}（{FormatPublishedAt(release)}）——"
                        + "开发版和发布版没法直接比大小，想跟官方版本对齐请到「前往下载」换正式包。",
                    LogLine = WithTokenNote(
                        $"[更新检查 · {channelLabel}] 本机为自编译开发版 {CurrentVersion}，远端 {release.Version}"
                        + $"（tag {release.Tag}）→ LocalBuild", call),
                    Latest = release,
                };
            }

            var comparison = CompareVersions(release.Version, CurrentVersion);
            var status = comparison > 0
                ? UpdateCheckStatus.UpdateAvailable
                : comparison == 0
                    ? UpdateCheckStatus.UpToDate
                    : UpdateCheckStatus.LocalNewer;

            var message = status switch
            {
                UpdateCheckStatus.UpdateAvailable =>
                    $"发现新版本 {release.Version}（{channelLabel}）—— 当前是 {CurrentVersion}。"
                    + $"发布日期：{FormatPublishedAt(release)}。",

                UpdateCheckStatus.UpToDate =>
                    $"已是最新版本（{CurrentVersion}，{channelLabel}）。",

                _ => normalized == UpdateChannels.Beta
                    // 测试版的号取「最新正式版的次版本 +1」（v2.3.2 → 2.4.0-dev.N），本该永远压正式版一头；
                    // 真落到这里，通常是刚发过正式版、CI 还没来得及按新号重新构建一轮，
                    // 或者远端那份被回退过。说清缘由，别让用户以为自己的版本坏了。
                    ? $"测试版通道上最新的是 {release.Version}（{FormatPublishedAt(release)}），"
                        + $"比当前 {CurrentVersion} 的版本号小 —— 多半是刚发过正式版、测试版还没重新构建。"
                        + "想回到正式版请把更新渠道切到「正式版」。"
                    : $"当前版本（{CurrentVersion}）比{channelLabel}通道上最新的 {release.Version} 还要新 ——"
                        + "多半是远端那份被回退过，或者本地在跑自己编译的包。",
            };

            return new UpdateCheckResult
            {
                Status = status,
                Message = message,
                LogLine = WithTokenNote(
                    $"[更新检查 · {channelLabel}] 本机 {CurrentVersion}，远端 {release.Version}"
                    + $"（tag {release.Tag}，{(release.IsPrerelease ? "预发布" : "正式发布")}）"
                    + $"→ {status}（{(call.UsedToken ? "已认证" : "匿名")}请求）", call),
                Latest = release,
            };
        }
        catch (OperationCanceledException)
        {
            return Failed(channelLabel, "请求被取消或超时", "检查更新被取消 / 超时了，稍后再试。", call);
        }
        catch (HttpRequestException ex)
        {
            return Failed(channelLabel, ex.Message, $"连不上 GitHub：{ex.Message}", call);
        }
        catch (RateLimitedException ex)
        {
            // 带上令牌还撞限流，说明 5000 次/小时也用完了 —— 文案要跟匿名时分开，
            // 否则用户会去配一个本来就已经在用的令牌。
            return Failed(
                channelLabel,
                ex.Message,
                call.UsedToken
                    ? $"GitHub 接口请求太频繁（已用访问令牌，配额每小时 5000 次，{ex.ResetHint}）"
                        + "—— 过一会儿再点「检查更新」。"
                    : $"GitHub 接口请求太频繁（匿名调用每小时 60 次，{ex.ResetHint}）"
                        + "—— 在「设置 → 开发者工具」配一个 GitHub 访问令牌可以提到每小时 5000 次，"
                        + "或者过一会儿再点「检查更新」。",
                call);
        }
        catch (Exception ex)
        {
            // 解析/反序列化之类：如实报出来，别让用户以为"已经是最新"
            return Failed(
                channelLabel,
                $"{ex.GetType().Name}: {ex.Message}",
                $"检查更新失败：{ex.Message}",
                call);
        }
    }

    private static UpdateCheckResult Failed(
        string channelLabel,
        string detail,
        string message,
        GitHubCallContext? call = null) =>
        new()
        {
            Status = UpdateCheckStatus.Failed,
            Message = message,
            LogLine = WithTokenNote($"[更新检查 · {channelLabel}] 失败：{detail}", call),
        };

    /// <summary>正式版通道：GitHub 定义的「最新正式版」。</summary>
    private static async Task<GitHubRelease?> FetchLatestStableAsync(
        HttpClient client,
        GitHubCallContext call,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        using var response = await GetGitHubAsync(client, url, call, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // 一份正式版都没发过 —— 正常状态，不是错误
            return null;
        }

        await ThrowIfNotSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return ParseRelease(document.RootElement);
    }

    /// <summary>
    /// 测试版通道：**只在预发布里**挑版本号最大的一份。
    ///
    /// <para>为什么不把正式版也放进来比：渠道是两条独立的线，编号规则也不同 —— 正式版发 <c>v2.4.0</c>，
    /// 测试版是 CI 每推一次 main 就发的 <c>2.4.1-dev.N</c>（修订号 = 最新正式版 +1）。
    /// 而且发布列表里还留着 2026-10 之前按 <c>2.0.0-dev.N</c> 编号的旧预发布，混在一起按大小取最大
    /// 会被正式版压住（2.4.0 &gt; 2.0.0-dev.105），于是「选了测试版却拿到正式版」——
    /// 2026-10-25 用户实测反馈的就是这个。</para>
    ///
    /// <para>为什么不用「列表第一个」：列表按创建时间排，而 CI 每推一次 main 就发一份，
    /// 时间顺序不等于版本大小（重跑、回退、手改 tag 都会打乱）。按版本号挑才是稳的。</para>
    /// </summary>
    private static async Task<GitHubRelease?> FetchNewestPrereleaseAsync(
        HttpClient client,
        GitHubCallContext call,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=50";

        using var response = await GetGitHubAsync(client, url, call, cancellationToken).ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("发布列表的返回不是数组");
        }

        var releases = new List<GitHubRelease>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            // 草稿不算「已发布」
            if (element.ValueKind != JsonValueKind.Object ||
                element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var release = ParseRelease(element);
            if (release is not null)
            {
                releases.Add(release);
            }
        }

        // 筛候选与排序都在 PickNewest 里 —— 那是纯函数，冒烟能直接验
        // 「正式版 v2.2.1 + 预发布 2.0.0-dev.105 同时在列表里该选谁」这个真机踩过的组合。
        return PickNewest(releases, UpdateChannels.Beta);
    }

    /// <summary>
    /// 从一批发布里挑出该通道的「最新」。
    ///
    /// <para><b>渠道决定候选集，版本号决定先后</b> —— 这两件事必须分开。正式版 <c>v2.4.0</c>
    /// 与测试版 <c>2.4.1-dev.N</c> 的编号现在已经对齐（测试版压正式版一头），但发布列表里
    /// 仍留着 <c>2.0.0-dev.N</c> 那套旧号的预发布，混着比大小永远被正式版压住，
    /// 「选了测试版却拿到正式版」就是这么来的（2026-10-25 实测）。</para>
    ///
    /// <para>同类里按版本号取最大而不是按发布时间：CI 每推一次 main 就发一份，
    /// 时间顺序不等于版本大小（重跑、回退、手改 tag 都会打乱）。</para>
    /// </summary>
    /// <param name="releases">待挑选的发布（可以混着正式版与预发布）。</param>
    /// <param name="channel">更新渠道，决定只看哪一类。</param>
    public static GitHubRelease? PickNewest(IEnumerable<GitHubRelease> releases, string channel)
    {
        var wantPrerelease = UpdateChannels.Normalize(channel) == UpdateChannels.Beta;
        GitHubRelease? best = null;

        foreach (var release in releases)
        {
            if (release is null
                || release.Version.Length == 0
                || release.IsPrerelease != wantPrerelease)
            {
                continue;
            }

            if (best is null)
            {
                best = release;
                continue;
            }

            var comparison = CompareVersions(release.Version, best.Version);
            if (comparison > 0)
            {
                best = release;
            }
            else if (comparison == 0
                     && !string.Equals(release.Tag, best.Tag, StringComparison.OrdinalIgnoreCase)
                     && IsRollingTag(release.Tag))
            {
                // 同版本时优先滚动入口 test-build：它才是「只想拿最新版」的固定地址
                best = release;
            }
        }

        return best;
    }

    private static bool IsRollingTag(string tag) =>
        string.Equals(tag, "test-build", StringComparison.OrdinalIgnoreCase);

    /// <summary>把接口返回的一组发布、资产解析成 <see cref="GitHubRelease"/>；结构不对就是 null。</summary>
    private static GitHubRelease? ParseRelease(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tag = GetString(element, "tag_name");
        if (tag.Length == 0)
        {
            return null;
        }

        var title = GetString(element, "name");
        if (title.Length == 0)
        {
            title = tag;
        }

        var pageUrl = GetString(element, "html_url");
        if (pageUrl.Length == 0)
        {
            pageUrl = $"{RepoUrl}/releases/tag/{tag}";
        }

        string? setupUrl = null;
        string? zipUrl = null;
        var assetNames = new List<string>();

        if (element.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                if (name.Length == 0)
                {
                    continue;
                }

                assetNames.Add(name);
                var downloadUrl = GetString(asset, "browser_download_url");

                if (setupUrl is null && name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    setupUrl = downloadUrl;
                }
                else if (zipUrl is null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = downloadUrl;
                }
            }
        }

        var isPrerelease = element.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;

        DateTimeOffset? publishedAt = null;
        var publishedRaw = GetString(element, "published_at");
        if (publishedRaw.Length > 0 &&
            DateTimeOffset.TryParse(publishedRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            publishedAt = parsed;
        }

        return new GitHubRelease
        {
            Tag = tag,
            Title = title,
            Version = ExtractVersion(tag, assetNames),
            PageUrl = pageUrl,
            Body = GetString(element, "body"),
            SetupUrl = setupUrl,
            ZipUrl = zipUrl,
            IsPrerelease = isPrerelease,
            PublishedAt = publishedAt,
        };
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// 从 tag / 资产文件名里抠版本号。
    ///
    /// 滚动预发布的 tag 是 <c>test-build</c>，里面根本没有版本号，所以必须回落到资产名：
    ///   <c>Yomori-winui3-2.4.1-dev.42-x64.zip</c> / <c>Yomori-2.4.1-dev.42-setup.exe</c>。
    /// 版本号自己带 '-'（2.4.1-dev.42），所以用前后缀锚定，避免把 dev.42 之类的片段切错。
    /// </summary>
    internal static string ExtractVersion(string tag, IReadOnlyList<string> assetNames)
    {
        // tag 本身就是版本号（正式版 v2.1.0，或手打的 2.1.0）
        var fromTag = tag.Trim().TrimStart('v', 'V');
        if (LooksLikeVersion(fromTag))
        {
            return fromTag;
        }

        foreach (var name in assetNames)
        {
            var fromZip = MatchGroup(name, @"^Yomori-winui3-(.+)-x64\.zip$");
            if (fromZip is not null)
            {
                return fromZip;
            }

            var fromSetup = MatchGroup(name, @"^Yomori-(.+)-setup\.exe$");
            if (fromSetup is not null)
            {
                return fromSetup;
            }
        }

        return string.Empty;
    }

    /// <summary>抠出正则第一个捕获组；没匹配上就是 null。</summary>
    private static string? MatchGroup(string input, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            input, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>是不是形如 <c>2.1.0</c> / <c>2.4.1-dev.42</c> 的版本号（宽松判断，只用来挡住明显的 tag 名）。</summary>
    internal static bool LooksLikeVersion(string text)
    {
        if (text.Length == 0 || !char.IsDigit(text[0]))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or '+')
            {
                continue;
            }

            return false;
        }

        return text.Contains('.');
    }

    /// <summary>
    /// 版本号比较（照 SemVer 的规矩，但对不规整的版本号要能容错，**绝不抛异常**）。
    ///
    ///   · 主版本段按数字比（<c>2.10.0 &gt; 2.9.0</c>，不是按字符串比）；
    ///   · 有预发布后缀的 &lt; 没有的（<c>2.1.0-rc1 &lt; 2.1.0</c>）；
    ///   · 两边都有后缀时逐段比：纯数字段按数字比且 &lt; 非数字段，其余按序号比。
    ///
    /// 返回值：left &gt; right 为正，相等为 0，小于为负。
    /// </summary>
    public static int CompareVersions(string left, string right)
    {
        var (leftMain, leftPre) = SplitVersion(left);
        var (rightMain, rightPre) = SplitVersion(right);

        var mainComparison = CompareNumericSegments(leftMain, rightMain);
        if (mainComparison != 0)
        {
            return mainComparison;
        }

        if (leftPre.Count == 0 && rightPre.Count == 0)
        {
            return 0;
        }

        // 正式版 > 预发布版
        if (leftPre.Count == 0)
        {
            return 1;
        }

        if (rightPre.Count == 0)
        {
            return -1;
        }

        var count = Math.Max(leftPre.Count, rightPre.Count);
        for (var i = 0; i < count; i++)
        {
            // 段数不够时按「缺的那段更小」处理（semver：1.0.0-alpha < 1.0.0-alpha.1）
            if (i >= leftPre.Count)
            {
                return -1;
            }

            if (i >= rightPre.Count)
            {
                return 1;
            }

            var comparison = CompareIdentifier(leftPre[i], rightPre[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static (List<string> Main, List<string> Pre) SplitVersion(string version)
    {
        var text = (version ?? string.Empty).Trim();

        // 去掉 v 前缀与 '+build' 后缀
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var dash = text.IndexOf('-');
        var main = dash >= 0 ? text[..dash] : text;
        var pre = dash >= 0 ? text[(dash + 1)..] : string.Empty;

        return (
            main.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            pre.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
    }

    private static int CompareNumericSegments(List<string> left, List<string> right)
    {
        var count = Math.Max(left.Count, right.Count);
        for (var i = 0; i < count; i++)
        {
            var leftValue = i < left.Count ? ParseLeadingNumber(left[i]) : 0;
            var rightValue = i < right.Count ? ParseLeadingNumber(right[i]) : 0;

            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        return 0;
    }

    /// <summary>取字符串最前面的数字（"42" → 42，"1a" → 1）；没有数字就是 0。</summary>
    private static int ParseLeadingNumber(string segment)
    {
        var end = 0;
        while (end < segment.Length && char.IsDigit(segment[end]))
        {
            end++;
        }

        if (end == 0)
        {
            return 0;
        }

        return int.TryParse(segment[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsDigitsOnly(left);
        var rightNumeric = IsDigitsOnly(right);

        if (leftNumeric && rightNumeric)
        {
            return CompareNumericSegments(new List<string> { left }, new List<string> { right });
        }

        // 数字段 < 字母段（semver：1.0.0-1 < 1.0.0-alpha）
        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDigitsOnly(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatPublishedAt(GitHubRelease release) =>
        release.PublishedAt is { } published
            ? published.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "未知";

    /// <summary>非 2xx 时抛错；限流（403 且额度为 0）单独抛 <see cref="RateLimitedException"/> 以便给出人话。</summary>
    private static async Task ThrowIfNotSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 读不到响应体不影响判断
        }

        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            remaining.FirstOrDefault() == "0")
        {
            var resetHint = "额度已用完";
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) &&
                long.TryParse(reset.FirstOrDefault(), out var epoch))
            {
                resetHint = $"大约 {DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime():HH:mm} 后恢复";
            }

            throw new RateLimitedException(resetHint);
        }

        throw new HttpRequestException(
            $"GitHub 接口返回 {(int)response.StatusCode} {response.ReasonPhrase}"
            + (body.Length > 0 ? $"（{Truncate(body, 200)}）" : string.Empty));
    }

    private static string Truncate(string text, int max)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>用系统默认程序打开链接（下载页 / 发布页）。</summary>
    public static bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>被限流（匿名额度用尽）。</summary>
    private sealed class RateLimitedException : Exception
    {
        public RateLimitedException(string resetHint)
            : base(resetHint) => ResetHint = resetHint;

        public string ResetHint { get; }
    }

    // ---------------- 下载安装包 ----------------

    /// <summary>
    /// 这份发布里该下载哪个文件：优先安装包（<c>*-setup.exe</c>），其次是绿色包。
    ///
    /// 优先安装包是因为"自动下载"的下一步是"要不要现在装" —— 绿色包解压到哪、
    /// 要不要覆盖当前目录，那些问题没有安装包替我们回答。
    /// </summary>
    /// <returns>URL 与建议的文件名；这份发布没有可下载资产时为 null。</returns>
    public static (string Url, string FileName)? PickDownload(GitHubRelease? release)
    {
        if (release is null)
        {
            return null;
        }

        var url = !string.IsNullOrWhiteSpace(release.SetupUrl) ? release.SetupUrl : release.ZipUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return (url, FileNameFromUrl(url));
    }

    /// <summary>
    /// 从资产直链里取文件名；取不到就给个兜底名。
    ///
    /// 公开是因为通知里只存了 URL（点"下载"时手上没有 GitHubRelease 对象），
    /// 落盘文件名得从同一个规则来，否则和 <see cref="PickDownload"/> 会不一致。
    /// </summary>
    public static string FileNameFromUrl(string? url)
    {
        const string fallback = "Yomori-update.bin";

        if (string.IsNullOrWhiteSpace(url))
        {
            return fallback;
        }

        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// 把发布资产下载到指定路径，返回（是否成功，给用户看的一句话）。
    ///
    /// <para>先写 <c>*.part</c> 再改名：中途断网会留下半个 exe，双击报"安装数据损坏"——
    /// 那比干脆没下载更糟。失败时把临时的半成品删掉。</para>
    ///
    /// <para>⚠️ 不抛异常：调用方在后台线程，失败要变成通知里的一句话，而不是一个未捕获异常。</para>
    /// </summary>
    /// <param name="url">资产直链。</param>
    /// <param name="targetPath">落盘路径（目录不存在会建）。</param>
    /// <param name="network">代理设置。</param>
    /// <param name="progress">进度 0~1（服务器不给 Content-Length 时不会回调）。</param>
    /// <param name="timeout">
    /// 整次下载的超时预算；null 用默认 20 秒。
    /// 下载体积大、又必须走慢速代理时（RDP 解锁要取 577KB 的 rdpwrap.ini）用它放宽，
    /// 否则会在「正在下」的时候被超时掐掉，报出来的却是「下载被取消或超时」，看不出是预算不够。
    /// </param>
    /// <summary>
    /// 按代理设置取一个共享的 <see cref="HttpClient"/>。
    /// 对外开放是为了让别的模块查 GitHub 时复用同一套代理语义（RDP 解锁要查上游工具发布），
    /// 而不是各自 new 一个 client、各自漏一个连接池。
    /// </summary>
    public static HttpClient NetworkClient(UpdateNetworkSettings network) => GetClient(network);

    public static async Task<(bool Ok, string Message)> DownloadAsync(
        string url,
        string targetPath,
        UpdateNetworkSettings network,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var partPath = targetPath + ".part";

        try
        {
            var client = GetClient(network, timeout);

            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            await ThrowIfNotSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            var total = response.Content.Headers.ContentLength ?? 0;

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var source = await response.Content
                             .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var sink = new FileStream(
                             partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    if (total > 0)
                    {
                        progress?.Report((double)received / total);
                    }
                }
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            File.Move(partPath, targetPath);
            return (true, $"已下载 {Path.GetFileName(targetPath)}");
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            return (false, "下载被取消或超时（安装包没有下完）。");
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            return (false, $"下载失败：{ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就留着，反正 .part 不会被当成安装包
        }
    }
}

/// <summary>
/// 检查更新 / 下载安装包时用的网络设置。值来自 <see cref="AppConfig"/>，
/// 语义见 <see cref="UpdateProxyModes"/>。
/// </summary>
/// <param name="ProxyMode">system / custom / none。</param>
/// <param name="ProxyUrl">
/// 自定义代理地址（仅 custom 用），形如 http://127.0.0.1:7890。
/// 可带认证 <c>http://用户:密码@主机:端口</c>；落盘的那份**不带密码**（密码在凭据管理器里，
/// 见 <see cref="UpdateProxySecretStore"/>）。留空 = 直连，不退回系统代理。
/// </param>
public readonly record struct UpdateNetworkSettings(string ProxyMode, string ProxyUrl)
{
    /// <summary>从配置里取一份快照。传值而不是传 AppConfig，避免后台线程长时间持有可变对象。</summary>
    public static UpdateNetworkSettings FromConfig(AppConfig config) =>
        new(config.UpdateProxyMode, config.UpdateProxyUrl);
}
