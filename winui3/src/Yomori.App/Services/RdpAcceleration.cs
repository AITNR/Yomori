// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>一条候选下载地址：URL、给用户看的来源、是否经过加速前缀重写。</summary>
public readonly record struct RdpAccelCandidate(string Url, string Source, bool Accelerated);

/// <summary>
/// RDP 解锁链路专用的 GitHub 加速前缀重写。
/// </summary>
/// <remarks>
/// <para>只在 RDP 解锁链路使用，不触碰主程序「检查更新」；重写发生在发起请求之前，
/// <c>UpdateService.DownloadAsync</c> 内部不变。</para>
/// <para>本文件会被 Yomori.Agent 工程链接，因此不得引用 Microsoft.UI.Xaml 等界面类型。</para>
/// </remarks>
public static class RdpAcceleration
{
    /// <summary>内置候选前缀，按顺序轮换。不落配置，便于随版本更新而无需迁移配置。</summary>
    public static readonly IReadOnlyList<string> BuiltInPrefixes = new[]
    {
        "https://gh-proxy.com/",
        "https://ghfast.top/",
    };

    /// <summary>
    /// INI 的国内 CDN 直连。指向上游仓库的纯文本配置文件，
    /// 不经过反代、不涉及二进制，与 exe 通道隔离。
    /// </summary>
    public const string IniCdnUrl =
        "https://cdn.jsdelivr.net/gh/sebaxakerhtc/rdpwrap.ini@master/rdpwrap.ini";

    /// <summary>
    /// 规范化用户输入的前缀。非法或为空返回空串（视为「未配置」）。
    /// 只接受 HTTPS、无 userinfo、无 query / fragment 的绝对地址。
    /// </summary>
    public static string NormalizePrefix(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0
            || !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Host.Length == 0
            || uri.UserInfo.Length > 0
            || text.Contains('?')
            || text.Contains('#'))
        {
            return string.Empty;
        }

        return text.TrimEnd('/') + "/";
    }

    /// <summary>
    /// 按优先级列出完整候选 URL。自定义前缀优先，其次内置候选，
    /// <b>官方直连恒为最后一项</b>。未启用时只返回原地址。
    /// </summary>
    public static IReadOnlyList<RdpAccelCandidate> Candidates(
        string originalUrl, RdpAccelerationSettings settings)
    {
        if (string.IsNullOrEmpty(originalUrl))
        {
            return Array.Empty<RdpAccelCandidate>();
        }

        if (!settings.Enabled)
        {
            return new[] { new RdpAccelCandidate(originalUrl, "官方直连", false) };
        }

        var result = new List<RdpAccelCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string prefix, string label)
        {
            var url = prefix + originalUrl;
            if (seen.Add(url))
            {
                result.Add(new RdpAccelCandidate(url, label, true));
            }
        }

        var custom = NormalizePrefix(settings.CustomPrefix);
        if (custom.Length > 0)
        {
            Add(custom, "自定义前缀 " + HostOf(custom));
        }

        foreach (var builtIn in BuiltInPrefixes)
        {
            Add(builtIn, HostOf(builtIn));
        }

        if (seen.Add(originalUrl))
        {
            result.Add(new RdpAccelCandidate(originalUrl, "官方直连（加速全部失败后的回退）", false));
        }

        return result;
    }

    /// <summary>
    /// 是否允许把 GitHub 访问令牌附到这个请求上。
    /// 经加速前缀重写的请求一律不带令牌（D1）；未重写时也只有 <c>api.github.com</c> 该带。
    /// </summary>
    public static bool ShouldSendToken(string url, bool accelerated)
    {
        return !accelerated
               && Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// INI 专用候选：启用加速时先走 jsDelivr CDN（上游 INI 的纯文本镜像），
    /// 之后是 raw 的反代候选与官方直连。
    /// </summary>
    public static IReadOnlyList<RdpAccelCandidate> IniCandidates(
        string originalUrl, RdpAccelerationSettings settings)
    {
        var result = new List<RdpAccelCandidate>();

        if (settings.Enabled)
        {
            result.Add(new RdpAccelCandidate(IniCdnUrl, "jsDelivr CDN（上游 INI 镜像）", false));
        }

        result.AddRange(Candidates(originalUrl, settings));
        return result;
    }

    /// <summary>取前缀的主机名，用于给用户看的节点名。</summary>
    public static string HostOf(string prefix) =>
        Uri.TryCreate(prefix, UriKind.Absolute, out var uri) ? uri.Host : prefix;
}
