// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace Yomori.Services;

/// <summary>
/// 两个上游 exe 的内置 SHA-256 基线。
/// </summary>
/// <remarks>
/// <para>上游 Release 的 <c>digest</c> 字段为 <c>null</c>，官方没有提供可比的摘要。
/// 这里的基线于 2026-11-06 从两条独立反代路径分别下载、大小与哈希均一致后采信，
/// 对应 <c>v1.8.9.9</c> 的两个附件；采集过程只下载计算、未执行文件。</para>
/// <para>命中基线时强制比对；未命中（上游发布新版）时校验环节须如实标注「未按内置基线校验」，
/// 不阻断安装，也不假装验过。INI 会滚动更新，因此不设基线。</para>
/// </remarks>
public static class RdpToolHashes
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["v1.8.9.9|RDPW_Installer.exe"] =
            "932bcf6c68e34fb99ffafb5ae62a1473fe761d961034cb5630dc3a9ba9155ccb",
        ["v1.8.9.9|RDPW_Uninstaller.exe"] =
            "ab66ac087a6f0c825cd4c49b282de4ae77c362c73d7664611c995de8fea8f0f6",
    };

    /// <summary>返回该 tag + 文件名的期望 SHA-256；没有收录返回空串。</summary>
    public static string Find(string tag, string fileName)
    {
        if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        return Table.TryGetValue(tag + "|" + fileName, out var hash) ? hash : string.Empty;
    }
}
