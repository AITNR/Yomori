// -*- coding: utf-8 -*-
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// GitHub 访问令牌（可选）的存取与校验。
/// </summary>
/// <remarks>
/// <para>用途：GitHub 的匿名接口每小时 60 次（按出口 IP 计），而 Yomori 每次真正需要下载
/// RDP 解锁组件时都会查一次 <c>releases/latest</c>，每次检查更新（手动点、以及每次启动时的自动检查）
/// 也都要查一遍公开接口。配上个人访问令牌后额度是**每小时 5000 次**，基本不会再撞限流。</para>
/// <para>谁在用这个令牌：<see cref="UpdateService"/> 的检查更新（两条通道）、
/// <see cref="RdpReleaseResolver"/> 查上游工具发布、以及这里保存时的验证。
/// 而下载安装包**不带**令牌 —— 那是 <c>objects.githubusercontent.com</c>，
/// 不该把凭据发到另一个主机（见 <c>UpdateService.GetGitHubAsync</c>）。</para>
/// <para><b>令牌只进 Windows 凭据管理器，不写 config.json。</b>
/// config.json 会被导出、备份、贴给别人看；凭据不会。这一条与「通道密码只进凭据管理器」
/// 是同一条规则，不因为是令牌就放松。</para>
/// <para>需要什么权限：只读公开仓库的经典令牌**一个 scope 都不用勾**；
/// 细粒度令牌也不需要任何仓库权限。它唯一的作用是把请求的配额从匿名提升到已认证。</para>
/// </remarks>
/// <summary>令牌验证的三种结果：无效要清掉，验证不了就留着等网络恢复。</summary>
internal enum GitHubTokenProbe
{
    Valid = 0,
    Invalid = 1,
    Unverified = 2,
}

internal static class GitHubTokenStore
{
    /// <summary>凭据管理器里的键。带前缀，避免和在存的 RDP 密码条目混在一起。</summary>
    private const string Target = "Yomori/GitHub/ApiToken";

    /// <summary>凭据条目里记的账户名（仅用于显示，不参与鉴权）。</summary>
    private const string AccountLabel = "github-token";

    /// <summary>读取令牌；没有配置返回 null。</summary>
    public static string? Read()
    {
        var token = RdpCredentialStore.ReadSecret(Target);
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    /// <summary>是否已配置。</summary>
    public static bool HasToken => Read() is not null;

    /// <summary>清除令牌。</summary>
    public static void Clear() => RdpCredentialStore.DeleteSecret(Target);

    /// <summary>保存令牌。返回是否成功；失败时给出原因。</summary>
    public static (bool Ok, string Message) Save(string raw)
    {
        var (token, error) = GitHubTokenFormat.Sanitize(raw);
        if (token.Length == 0)
        {
            return (false, error);
        }

        if (!RdpCredentialStore.WriteSecret(Target, AccountLabel, token))
        {
            return (false, "写入 Windows 凭据管理器失败（可能没有权限）。");
        }

        return (true, "");
    }

    /// <summary>给界面看的一行状态。</summary>
    public static string DescribeState()
    {
        var token = Read();
        return token is null
            ? "未配置令牌：GitHub 接口每小时只有 60 次，连点几次可能被限流。"
            : $"已配置（{GitHubTokenFormat.Mask(token)}）。GitHub 接口配额提升到每小时 5000 次。";
    }
}
