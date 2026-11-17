// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace Yomori.Services;

/// <summary>
/// 账户在<b>登录界面</b>的可见性（隐藏 / 显示）。
/// </summary>
/// <remarks>
/// <para>Windows <b>没有</b>"隐藏用户"的正规 API。可行且被广泛使用的机制是
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList</c>
/// 下的一个 DWORD：值名 = 账户名，数据 <c>0</c> = 从登录界面隐藏，<c>1</c> = 显示。
/// 本服务只做这一件事。</para>
///
/// <para><b>作用范围要说准</b>：只影响<b>交互式登录界面</b>（欢迎屏幕的账户磁贴、「切换用户」列表）。
/// 不影响手机远程桌面登录、不影响 <c>net user</c>、不影响「计算机管理 → 本地用户和组」、
/// 也不影响本程序自己的账户列表 —— 账户照样能用凭据登录，只是登录界面上看不到它。
/// 隐藏对已登录的会话即时生效性也有限：下一次渲染登录界面时才看不到。</para>
///
/// <para>纯 .NET（Microsoft.Win32.Registry），不引用 Microsoft.UI.Xaml；代理工程里显式排除 ——
/// 这是需要管理员权限的写操作，不该出现在常驻代理里。</para>
/// </remarks>
public static class LocalUserVisibility
{
    /// <summary>存放"登录界面可见性"的注册表路径（相对 HKLM）。</summary>
    public const string UserListSubKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    /// <summary>完整路径，供界面与日志显示。</summary>
    public const string UserListFullPath =
        @"HKEY_LOCAL_MACHINE\" + UserListSubKey;

    /// <summary>
    /// 把整张 UserList 表读出来（值名 → 数据）。键不存在时返回空表。
    /// </summary>
    /// <remarks>
    /// 一次读全，供扫描时给每个账户标"是不是藏起来了"——逐账户 OpenSubKey 会多花几十次注册表调用。
    /// </remarks>
    public static IReadOnlyDictionary<string, int> ReadUserList()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(UserListSubKey);

            if (key is null)
            {
                return result;
            }

            foreach (var name in key.GetValueNames())
            {
                // 非 DWORD 的值一律忽略：那不是我们写的，也不该按我们的语义解释
                if (key.GetValue(name) is int value)
                {
                    result[name] = value;
                }
            }
        }
        catch
        {
            // 读不出来就当"没隐藏任何人"：这只影响显示，不该让整轮扫描失败
        }

        return result;
    }

    /// <summary>按已读出的表判断某个账户是否被隐藏。表里没有它 = 没隐藏。</summary>
    public static bool IsHidden(IReadOnlyDictionary<string, int>? userList, string? account)
    {
        var name = (account ?? string.Empty).Trim();
        if (name.Length == 0 || userList is null || !userList.TryGetValue(name, out var value))
        {
            return false;
        }

        return value == 0;
    }

    /// <summary>
    /// 设置账户在登录界面的可见性。<b>需要管理员权限</b>，写后读回校验。
    /// </summary>
    /// <param name="hidden">true = 隐藏（写 0）；false = 显示（写 1）。</param>
    public static (bool Ok, string Detail) SetHidden(string? account, bool hidden)
    {
        var name = (account ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return (false, "账户名为空，未做任何修改。");
        }

        var target = hidden ? 0 : 1;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(UserListSubKey, writable: true);
            if (key is null)
            {
                return (false, $"打不开注册表键「{UserListSubKey}」（可能需要管理员权限）。");
            }

            key.SetValue(name, target, RegistryValueKind.DWord);

            // 写完必须读回：SetValue 不抛异常不代表值真的落下了（权限/重定向都可能静默失败）
            var readback = key.GetValue(name);
            if (readback is not int actual)
            {
                return (false, $"写入后读回失败（取到的是 {readback?.GetType().Name ?? "空值"}）。");
            }

            if (actual != target)
            {
                return (false,
                    $"写入后读回不一致：期望 {target.ToString(CultureInfo.InvariantCulture)}，"
                    + $"实得 {actual.ToString(CultureInfo.InvariantCulture)}。");
            }

            return (true, hidden
                ? $"已隐藏：登录界面与「切换用户」列表里看不到「{name}」。"
                : $"已恢复显示：「{name}」会重新出现在登录界面。");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}：{ex.Message}");
        }
    }
}
