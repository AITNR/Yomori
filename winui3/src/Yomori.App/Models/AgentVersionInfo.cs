// -*- coding: utf-8 -*-
using System;
using System.Reflection;
using System.Text;

namespace Yomori.Models;

/// <summary>
/// 代理端版本号。
///
/// <para>【它是什么】</para>
/// 代理载荷（<c>Yomori.Agent.exe</c> 及其依赖）自己的版本，与主程序版本**无关**。
/// 主程序每次发版号都变，代理绝大多数时候一个字节都没动 —— 用主程序版本判断
/// 「要不要重铺代理」会把每次安装都变成一次 73MB 的无谓搬运。
///
/// <para>【单一来源】</para>
/// 数字只写在 <c>Yomori.Agent.csproj</c> 的 <c>&lt;AgentVersion&gt;</c>。构建时它被写进
/// 两处：程序集元数据 <c>AssemblyMetadata("AgentVersion")</c>（本类运行时读这里）与
/// 文件版本资源（安装器 / 部署逻辑读 exe 属性）。所以这里**没有**常量，
/// 免得出现「元数据里 2.0.0、代码里 2.0.1」这种没法对账的状态。
///
/// <para>【为什么主控端读它得到空串】</para>
/// 本文件被链接编译进两个程序集：主控端 <c>Yomori.App.dll</c> 与代理 <c>Yomori.Agent.dll</c>。
/// <c>Current</c> 读的是**当前程序集**的元数据，主控端没有这条元数据，因此返回空串 ——
/// 这正好可以用来回答「我是不是代理进程」，不需要额外的开关或环境变量。
/// </summary>
public static class AgentVersionInfo
{
    /// <summary>程序集元数据里用的键名，与 <c>Yomori.Agent.csproj</c> 的条目必须一致。</summary>
    public const string MetadataKey = "AgentVersion";

    /// <summary>
    /// 当前程序集声明的代理版本（形如 <c>2.0.0</c>）。主控端程序集恒为空串。
    /// 读取失败也返回空串 —— 版本号读不出来时调用方应判「未知」，而不是猜一个。
    /// </summary>
    public static string Current { get; } = ReadCurrent();

    private static string ReadCurrent()
    {
        try
        {
            var assembly = typeof(AgentVersionInfo).Assembly;
            foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(attribute.Key, MetadataKey, StringComparison.OrdinalIgnoreCase))
                {
                    return Normalize(attribute.Value);
                }
            }
        }
        catch
        {
            // 反射取元数据在裁剪/单文件等场景下可能失败：当作没有版本号
        }

        return string.Empty;
    }

    /// <summary>
    /// 把 <c>" 2.0.0.0 "</c> / <c>"2.0.0"</c> 这类写法统一成四段以内的数字串，去掉多余的尾零。
    /// 非数字段一律按 0 计（<c>"2.0.0-rc1"</c> → <c>"2.0.0"</c>），保证来自不同读取途径
    /// （元数据 / 文件版本资源 / 配置）的同一个版本能比较。
    /// </summary>
    public static string Normalize(string? text)
    {
        var parts = Parse(text);
        if (parts is null)
        {
            return string.Empty;
        }

        // 去掉尾部的 0，但至少保留三段：2.0.0 不该显示成 2，2.0.0.0 也不该显示成 2.0.0.0 之外的东西
        var length = 4;
        while (length > 3 && parts[length - 1] == 0)
        {
            length--;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            if (i > 0)
            {
                builder.Append('.');
            }

            builder.Append(parts[i]);
        }

        return builder.ToString();
    }

    /// <summary>两个版本号是不是同一个版本。<b>任一侧读不出来就判「不同」</b>。</summary>
    /// <remarks>
    /// 判「不同」而不是「相同」，是因为调用方用它决定「要不要替换代理」：
    /// 读不出来时宁可多替换一次，也不要静默保留一个来路不明的旧代理。
    /// </remarks>
    public static bool Same(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);
        if (a is null || b is null)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>解析成四段数字（缺位补 0）；完全不像版本号时返回 <c>null</c>。</summary>
    private static int[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0 || !char.IsDigit(trimmed[0]))
        {
            return null;
        }

        var parts = new int[4];
        var index = 0;
        foreach (var segment in trimmed.Split('.'))
        {
            if (index >= 4)
            {
                break;
            }

            // 只取前导数字：'0-rc1' 视作 0，'184' 视作 184
            var digits = 0;
            while (digits < segment.Length && char.IsDigit(segment[digits]))
            {
                digits++;
            }

            parts[index++] = digits == 0
                ? 0
                : ParsePart(segment, digits);
        }

        return parts;
    }

    /// <summary>把一段前导数字转成 int；超过 9 位时只取前 9 位（版本号用不到那么长，防溢出）。</summary>
    private static int ParsePart(string segment, int digits)
    {
        var length = Math.Min(digits, 9);
        return int.TryParse(segment.AsSpan(0, length), out var value) ? value : 0;
    }
}
