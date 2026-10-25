// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 注册表值的无损读写。**只服务白名单里的键值**（计划书 5.1）。
/// </summary>
/// <remarks>
/// <para>这个类属于「修改模块」，与 <see cref="RdpReadOnlyDetector"/> 严格分开：
/// 检测模块不许引用它，冒烟工程对检测模块做了导入白名单断言。</para>
/// <para>几个必须做对的点（计划书 5.2）：</para>
/// <list type="bullet">
/// <item>显式 <c>RegistryView.Registry64</c> —— x64 系统上 32 位视图会把 SYSTEM 键重定向到 Wow6432Node。</item>
/// <item><c>EXPAND_SZ</c> 取未展开原文，写回也写未展开原文，否则
/// <c>%SystemRoot%\...</c> 会被固化成本机绝对路径，换机器/换系统盘就不对了。</item>
/// <item>「原值不存在」是一种状态，恢复时表现为**删除该值**，不是写空串。</item>
/// <item>MULTI_SZ 的空数组与「不存在」不是一回事，读的时候不能把前者折叠成后者。</item>
/// </list>
/// </remarks>
public static class RdpRegistryService
{
    private const string HivePrefix = "HKLM";

    /// <summary>
    /// 最小写入候选集（计划书 5.1）。除这两项之外，本功能不碰任何注册表值。
    /// </summary>
    /// <remarks>
    /// 只收 <c>fDenyTSConnections</c>：它才是「关掉远程桌面」的总开关。
    /// <c>fSingleSessionPerUser</c>、NLA、SecurityLayer、端口、时限等只检查不写 ——
    /// 计划书要求「除非实测证明是必要条件并在确认清单中明确」，本轮没有这样的证据。
    /// </remarks>
    public static readonly IReadOnlyList<(string KeyPath, string Name)> Whitelist = new[]
    {
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections"),
        (@"HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", "fDenyTSConnections"),
    };

    /// <summary>
    /// 明确禁止写入的值（计划书 5.1）。它们会扩大攻击面或与本次目的无关：
    /// 空密码远程登录、本地账户远程令牌过滤、隐藏账户都在这条线上。
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenNames = new[]
    {
        "LimitBlankPasswordUse",
        "LocalAccountTokenFilterPolicy",
        "HideUser",
    };

    /// <summary>策略键：它存在就说明这台机器可能被组策略托管，冲突时只报告不绕过。</summary>
    public const string PolicyKeyPath = @"HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    public static bool IsAllowed(string keyPath, string name)
    {
        var normalized = NormalizeKeyPath(keyPath);
        foreach (var (allowedKey, allowedName) in Whitelist)
        {
            if (string.Equals(NormalizeKeyPath(allowedKey), normalized, StringComparison.OrdinalIgnoreCase)
                && string.Equals(allowedName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsForbidden(string name)
    {
        foreach (var forbidden in ForbiddenNames)
        {
            if (string.Equals(forbidden, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>读一个值。值不存在返回 <see cref="RdpRegistryKind.NotFound"/>；读失败抛给调用方处理。</summary>
    public static RdpRegistryValueState Read(string keyPath, string name)
    {
        var (hive, subKey) = SplitKeyPath(keyPath);

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKey, writable: false);

        if (key is null)
        {
            return new RdpRegistryValueState
            {
                KeyPath = NormalizeKeyPath(keyPath),
                Name = name,
                Kind = RdpRegistryKind.NotFound,
            };
        }

        // DoNotExpandEnvironmentNames 是这里的关键：REG_EXPAND_SZ 要留原文
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
        {
            return new RdpRegistryValueState
            {
                KeyPath = NormalizeKeyPath(keyPath),
                Name = name,
                Kind = RdpRegistryKind.NotFound,
            };
        }

        var kind = key.GetValueKind(name);
        var normalizedPath = NormalizeKeyPath(keyPath);

        return kind switch
        {
            RegistryValueKind.String => new RdpRegistryValueState
            {
                KeyPath = normalizedPath, Name = name, Kind = RdpRegistryKind.String, StringValue = raw as string ?? "",
            },
            RegistryValueKind.ExpandString => new RdpRegistryValueState
            {
                KeyPath = normalizedPath, Name = name, Kind = RdpRegistryKind.ExpandString, StringValue = raw as string ?? "",
            },
            RegistryValueKind.MultiString => new RdpRegistryValueState
            {
                KeyPath = normalizedPath, Name = name, Kind = RdpRegistryKind.MultiString,
                MultiValues = raw as string[] ?? Array.Empty<string>(),
            },
            RegistryValueKind.DWord => new RdpRegistryValueState
            {
                KeyPath = normalizedPath, Name = name, Kind = RdpRegistryKind.Dword,
                NumberValue = unchecked((int)(Convert.ToInt32(raw))),
            },
            RegistryValueKind.QWord => new RdpRegistryValueState
            {
                KeyPath = normalizedPath, Name = name, Kind = RdpRegistryKind.Qword,
                NumberValue = Convert.ToInt64(raw),
            },
            RegistryValueKind.Binary => new RdpRegistryValueState
            {
                KeyPath = normalizedPath, Name = name, Kind = RdpRegistryKind.Binary,
                BinaryBase64 = Convert.ToBase64String(raw as byte[] ?? Array.Empty<byte>()),
            },
            _ => new RdpRegistryValueState
            {
                KeyPath = normalizedPath, Name = name, Kind = RdpRegistryKind.NotFound,
            },
        };
    }

    /// <summary>
    /// 把一个值写成快照描述的样子。快照是 <see cref="RdpRegistryKind.NotFound"/> 时**删除该值**。
    /// </summary>
    /// <returns>(是否成功, 错误说明)</returns>
    public static (bool Ok, string Error) Write(RdpRegistryValueState state)
    {
        if (!IsAllowed(state.KeyPath, state.Name))
        {
            // 越界写入在这里断掉：白名单之外一律拒绝，不给「顺手改一下」的机会
            return (false, $"不在写入白名单内：{state.KeyPath}\\{state.Name}");
        }

        if (IsForbidden(state.Name))
        {
            return (false, $"禁止写入的值：{state.Name}");
        }

        try
        {
            var (hive, subKey) = SplitKeyPath(state.KeyPath);
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, writable: true);

            if (key is null)
            {
                return (false, $"键不存在或无权写入：{state.KeyPath}");
            }

            switch (state.Kind)
            {
                case RdpRegistryKind.NotFound:
                    key.DeleteValue(state.Name, throwOnMissingValue: false);
                    return (true, "");

                case RdpRegistryKind.String:
                    key.SetValue(state.Name, state.StringValue, RegistryValueKind.String);
                    return (true, "");

                case RdpRegistryKind.ExpandString:
                    key.SetValue(state.Name, state.StringValue, RegistryValueKind.ExpandString);
                    return (true, "");

                case RdpRegistryKind.MultiString:
                    key.SetValue(state.Name, state.MultiValues.ToArray(), RegistryValueKind.MultiString);
                    return (true, "");

                case RdpRegistryKind.Dword:
                    key.SetValue(state.Name, unchecked((int)state.NumberValue), RegistryValueKind.DWord);
                    return (true, "");

                case RdpRegistryKind.Qword:
                    key.SetValue(state.Name, state.NumberValue, RegistryValueKind.QWord);
                    return (true, "");

                case RdpRegistryKind.Binary:
                    key.SetValue(state.Name, Convert.FromBase64String(state.BinaryBase64), RegistryValueKind.Binary);
                    return (true, "");

                default:
                    return (false, $"不支持的值类型：{state.Kind}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>两个快照是否等价（类型与内容都要一致）。</summary>
    public static bool Matches(RdpRegistryValueState left, RdpRegistryValueState right)
    {
        if (left.Kind != right.Kind)
        {
            return false;
        }

        if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return left.Kind switch
        {
            RdpRegistryKind.NotFound => true,
            RdpRegistryKind.Dword or RdpRegistryKind.Qword => left.NumberValue == right.NumberValue,
            RdpRegistryKind.String or RdpRegistryKind.ExpandString =>
                string.Equals(left.StringValue, right.StringValue, StringComparison.Ordinal),
            RdpRegistryKind.MultiString => SequenceEqual(left.MultiValues, right.MultiValues),
            RdpRegistryKind.Binary => string.Equals(left.BinaryBase64, right.BinaryBase64, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// 目标值与当前值是否已经一致 —— 用来在写入前短路（已经在目标状态就别动系统）。
    /// </summary>
    public static bool AlreadyAtTarget(RdpRegistryValueState current, RdpRegistryValueState target) =>
        Matches(current, target);

    /// <summary>
    /// 恢复前的一致性检查（计划书 5.2）：第三方或组策略在我们之后改过第三值时要展示冲突，
    /// 不覆盖后续合法修改。
    /// </summary>
    public static bool HasConflict(RdpRegistryValueState original, RdpRegistryValueState current) =>
        !Matches(original, current);

    /// <summary>策略是否托管了远程桌面开关。有值就报告，不绕过。</summary>
    public static RdpRegistryValueState ReadPolicyValue() =>
        Read(PolicyKeyPath, "fDenyTSConnections");

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeKeyPath(string keyPath) =>
        (keyPath ?? string.Empty).Trim().TrimEnd('\\');

    /// <summary>拆成 (hive, 子键路径)。只认 HKLM —— 白名单里没有别的蜂巢。</summary>
    private static (RegistryHive Hive, string SubKey) SplitKeyPath(string keyPath)
    {
        var text = NormalizeKeyPath(keyPath);
        if (!text.StartsWith(HivePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"只支持 HKLM 下的键：{keyPath}", nameof(keyPath));
        }

        var rest = text.Length > HivePrefix.Length ? text.Substring(HivePrefix.Length).TrimStart('\\') : "";
        if (rest.Length == 0)
        {
            throw new ArgumentException($"键路径缺少子键：{keyPath}", nameof(keyPath));
        }

        return (RegistryHive.LocalMachine, rest);
    }
}
