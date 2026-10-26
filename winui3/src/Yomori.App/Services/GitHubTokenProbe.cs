// -*- coding: utf-8 -*-
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 用已保存的令牌验证一次，并读回当前配额。
/// </summary>
/// <remarks>
/// 与 <see cref="GitHubTokenStore"/> 分开的原因是依赖不同：这里要发 HTTP（依赖 UpdateService），
/// 存取那边只碰凭据管理器。分开之后存取部分能被离线断言，
/// 联网部分能被替换或跳过 —— 否则「令牌存得对不对」这种纯逻辑要跟着网络一起测。
/// </remarks>
internal static class GitHubTokenProbeRunner
{
    /// <summary>
    /// 用令牌查一次配额，确认它真的有效。
    /// </summary>
    /// <remarks>
    /// 打的是 <c>/rate_limit</c>：这个端点**不消耗核心配额**，
    /// 又恰好能同时回答「令牌有效吗」和「现在还剩多少」。
    /// 保存时先落盘再验证 —— 断网时用户仍然可以先把令牌存好，
    /// 只是界面会如实说「暂时无法验证」。
    /// </remarks>
    public static async Task<(GitHubTokenProbe State, string Message)> ProbeAsync(
        UpdateNetworkSettings network,
        CancellationToken cancellationToken = default)
    {
        var token = GitHubTokenStore.Read();
        if (token is null)
        {
            return (GitHubTokenProbe.Invalid, "还没有配置令牌。");
        }

        try
        {
            var client = UpdateService.NetworkClient(network);

            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://api.github.com/rate_limit");
            request.Headers.UserAgent.ParseAdd("Yomori-RdpUnlock");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return (GitHubTokenProbe.Invalid, "令牌被 GitHub 拒绝（401）。请确认没有过期、没有被撤销，且复制完整。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (GitHubTokenProbe.Unverified, $"验证请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            var core = document.RootElement.TryGetProperty("resources", out var resources)
                       && resources.TryGetProperty("core", out var coreElement)
                ? coreElement
                : default;

            var limit = core.ValueKind == JsonValueKind.Object && core.TryGetProperty("limit", out var limitElement)
                ? limitElement.GetInt32()
                : 0;
            var remaining = core.ValueKind == JsonValueKind.Object && core.TryGetProperty("remaining", out var remainingElement)
                ? remainingElement.GetInt32()
                : 0;

            // 匿名是 60/小时，认证后是 5000/小时：用这个差值确认令牌真的被认了
            if (limit <= 60)
            {
                return (GitHubTokenProbe.Invalid,
                    $"请求带上了令牌，但配额仍是 {limit}/小时（认证未生效）。请确认令牌属于 github.com，且没有过期。");
            }

            return (GitHubTokenProbe.Valid, $"令牌有效：配额 {limit}/小时，当前剩余 {remaining} 次。");
        }
        catch (OperationCanceledException)
        {
            return (GitHubTokenProbe.Unverified, "验证被取消或超时。");
        }
        catch (Exception ex)
        {
            return (GitHubTokenProbe.Unverified, $"暂时无法验证（{ex.GetType().Name}: {ex.Message}）。令牌已保存，网络恢复后会生效。");
        }
    }
}
