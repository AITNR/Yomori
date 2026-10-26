// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Yomori.Services;

/// <summary>单账户单会话策略的四种（加上失败共五种）状态。</summary>
public enum SingleSessionPolicyStatus
{
    /// <summary>读失败 —— 什么都不做，也不许据此推断。</summary>
    ReadFailed = 0,

    /// <summary>本机键不存在（Windows 默认行为：允许同账户多会话，只要服务端版本支持）。</summary>
    Absent = 1,

    /// <summary>显式设为 1：同账户只允许一条会话。</summary>
    Enabled = 2,

    /// <summary>显式设为 0：允许同账户多会话。</summary>
    Disabled = 3,

    /// <summary>组策略已托管这个值 —— 本程序不覆盖、不绕过。</summary>
    ManagedByPolicy = 4,
}

/// <summary>策略读取结果。</summary>
public sealed class SingleSessionPolicyState
{
    public SingleSessionPolicyStatus Status { get; set; } = SingleSessionPolicyStatus.ReadFailed;

    /// <summary>本机键（HKLM\SYSTEM\...\Terminal Server）的原始值：null = 不存在，否则 0 / 1。</summary>
    public int? LocalValue { get; set; }

    /// <summary>策略键（HKLM\SOFTWARE\Policies\...）的原始值：null = 不存在。</summary>
    public int? PolicyValue { get; set; }

    public string Error { get; set; } = string.Empty;

    /// <summary>最终生效值（策略优先）。null 表示"没有配置，取系统默认"。</summary>
    public int? Effective => PolicyValue ?? LocalValue;

    /// <summary>能不能据此认为「重连一定回到同一条会话」。</summary>
    public bool Proven => Status == SingleSessionPolicyStatus.Enabled;

    public string Describe() => Status switch
    {
        SingleSessionPolicyStatus.Enabled => "已开启（同账户只允许一条会话）",
        SingleSessionPolicyStatus.Disabled => "已显式关闭（允许同账户多会话）",
        SingleSessionPolicyStatus.Absent => "未配置（取系统默认；同账户多会话的可能性无法排除）",
        SingleSessionPolicyStatus.ManagedByPolicy => $"由组策略托管（当前值 {PolicyValue?.ToString() ?? "未设置"}），本程序不会覆盖",
        _ => $"读取失败：{Error}",
    };

    /// <summary>给用户看的一行：当前值 + 两处来源。</summary>
    public string Detail =>
        $"本机值：{(LocalValue is int local ? local.ToString() : "不存在")}；" +
        $"策略值：{(PolicyValue is int policy ? policy.ToString() : "不存在")}。";
}

/// <summary>策略写入的结果。</summary>
public sealed class SingleSessionPolicyChange
{
    public bool Ok { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>写入前的状态（回滚用）。</summary>
    public SingleSessionPolicyState? Before { get; set; }

    /// <summary>写入后回读到的状态。</summary>
    public SingleSessionPolicyState? After { get; set; }

    /// <summary>是否因为组策略冲突而拒绝写入。</summary>
    public bool Conflict { get; set; }
}

/// <summary>
/// 「限制远程桌面服务用户只使用一个会话」（<c>fSingleSessionPerUser</c>）的**独立**配置入口。
///
/// <para>【为什么单独立一个类，而不是加进 <see cref="RdpRegistryService"/> 的白名单】</para>
/// <para>那个白名单是「RDP 解锁」流程的写入边界，冒烟里有硬断言锁着它只含
/// <c>fDenyTSConnections</c>。计划书 P1 明确要求：策略变更必须是独立、显式、可回滚的操作，
/// 不并入既有解锁写白名单。所以这里自己读写，白名单一行没动。</para>
///
/// <para>【行为约束】（计划书 P1 / §七）</para>
/// <list type="bullet">
/// <item>只有用户显式点「开启」才会写；应用启动、连接、任务执行任何路径都不会顺手改它。</item>
/// <item>写之前读原值（含"原先不存在"），写之后回读确认；不存在就删值，不写 0 冒充"恢复"。</item>
/// <item>组策略键里已有该值 → 直接停止并报告冲突，不覆盖、不静默重试。</item>
/// <item>绝不重启 TermService，绝不改其它值。</item>
/// <item>每次变更都落一行账本（主机、账户、SID、原值、新值、时刻、结果）。</item>
/// </list>
/// </summary>
public static class RdpSingleSessionPolicy
{
    /// <summary>本机键（Windows 自己读的那一处）。</summary>
    public const string LocalKeyPath = @"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server";

    /// <summary>策略键（域 / 本地组策略下发的那一处）。</summary>
    public const string PolicyKeyPath = @"HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    public const string ValueName = "fSingleSessionPerUser";

    /// <summary>账本位置：与桥目录同级，便于事后按时间线对齐。</summary>
    public static string LedgerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Yomori",
        "rdp",
        "policy-ledger.ndjson");

    /// <summary>读两处值并给出合并结论。<b>不写任何东西</b>。</summary>
    public static SingleSessionPolicyState Read()
    {
        var state = new SingleSessionPolicyState();

        try
        {
            state.LocalValue = ReadDword(LocalKeyPath);
            state.PolicyValue = ReadDword(PolicyKeyPath);
        }
        catch (Exception ex)
        {
            state.Status = SingleSessionPolicyStatus.ReadFailed;
            state.Error = $"{ex.GetType().Name}: {ex.Message}";
            return state;
        }

        if (state.PolicyValue is int policy)
        {
            // 组策略有这个值就等于"已经托管"：不论它是 0 还是 1，我们都不碰。
            state.Status = SingleSessionPolicyStatus.ManagedByPolicy;
            return state;
        }

        state.Status = state.LocalValue switch
        {
            1 => SingleSessionPolicyStatus.Enabled,
            0 => SingleSessionPolicyStatus.Disabled,
            _ => SingleSessionPolicyStatus.Absent,
        };
        return state;
    }

    /// <summary>
    /// 显式开启单账户单会话。仅由用户点按钮触发。
    /// </summary>
    /// <param name="force">true 时即使已开启也会重写一遍（用于修复可疑状态）。</param>
    public static SingleSessionPolicyChange Enable(bool force = false)
    {
        var change = new SingleSessionPolicyChange { Before = Read() };
        var before = change.Before;

        if (before.Status == SingleSessionPolicyStatus.ReadFailed)
        {
            change.Message = $"无法读取当前策略值，已停止（不做无把握的写入）：{before.Error}";
            LogLedger("enable", before, null, false, change.Message);
            return change;
        }

        if (before.Status == SingleSessionPolicyStatus.ManagedByPolicy)
        {
            change.Conflict = true;
            change.Message =
                "该值由组策略托管（" + before.Detail + "）。本程序不会覆盖域策略 —— " +
                "请让域管理员在组策略里调整「限制远程桌面服务用户只使用一个会话」，或从域中移出该策略。";
            LogLedger("enable", before, null, false, change.Message);
            return change;
        }

        if (before.Status == SingleSessionPolicyStatus.Enabled && !force)
        {
            change.Ok = true;
            change.After = before;
            change.Message = "已经是开启状态，无需改动。";
            return change;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(LocalKeyPath, writable: true);
            if (key is null)
            {
                change.Message = $"键不存在或无权写入：{LocalKeyPath}（通常需要管理员权限）。";
                LogLedger("enable", before, null, false, change.Message);
                return change;
            }

            key.SetValue(ValueName, 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            change.Message = $"写入失败：{ex.GetType().Name}: {ex.Message}";
            LogLedger("enable", before, null, false, change.Message);
            return change;
        }

        var after = Read();
        change.After = after;

        if (after.LocalValue == 1 && after.Status == SingleSessionPolicyStatus.Enabled)
        {
            change.Ok = true;
            change.Message = "已开启并回读确认：" + after.Describe() + "。" + after.Detail;
        }
        else
        {
            change.Message = $"写入后回读没有得到预期值（当前 {after.Describe()}），请检查权限或是否有其它程序在改同一项。";
        }

        LogLedger("enable", before, after, change.Ok, change.Message);
        return change;
    }

    /// <summary>
    /// 按账本里的原始状态回滚：原本是"不存在"就<b>删除</b>该值，原本有值就写回原值。
    /// </summary>
    public static SingleSessionPolicyChange Restore(SingleSessionPolicyState? before)
    {
        var change = new SingleSessionPolicyChange { Before = Read() };

        if (before is null)
        {
            change.Message = "没有可用的原始状态快照，未做任何改动。";
            return change;
        }

        if (change.Before!.Status == SingleSessionPolicyStatus.ManagedByPolicy)
        {
            change.Conflict = true;
            change.Message = "该值现在由组策略托管，回滚会与域策略冲突，已停止：" + change.Before.Detail;
            LogLedger("restore", change.Before, null, false, change.Message);
            return change;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(LocalKeyPath, writable: true);
            if (key is null)
            {
                change.Message = $"键不存在或无权写入：{LocalKeyPath}（通常需要管理员权限）。";
                LogLedger("restore", change.Before, null, false, change.Message);
                return change;
            }

            if (before.LocalValue is int original)
            {
                key.SetValue(ValueName, original, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            change.Message = $"回滚失败：{ex.GetType().Name}: {ex.Message}";
            LogLedger("restore", change.Before, null, false, change.Message);
            return change;
        }

        var after = Read();
        change.After = after;

        var expected = before.LocalValue;
        change.Ok = after.LocalValue == expected && after.Status != SingleSessionPolicyStatus.ManagedByPolicy;
        change.Message = change.Ok
            ? $"已回滚到原状态（本机值 {(expected is int v ? v.ToString() : "不存在")}）。" + after.Detail
            : $"回滚后回读不一致（期望 {(expected is int e ? e.ToString() : "不存在")}，实际 {after.Detail}）。";

        LogLedger("restore", change.Before, after, change.Ok, change.Message);
        return change;
    }

    /// <summary>
    /// 从账本里取最近一次 enable 之前的原始值，供「回滚」使用。
    ///
    /// <para>为什么不直接"把值删掉"当回滚：原值可能是 0（显式允许），也可能是 1，
    /// 也可能是"原先不存在" —— 三种情况的正确恢复动作不一样，
    /// 一律删除会悄悄改变用户的系统配置，而不是恢复它。</para>
    /// </summary>
    public static SingleSessionPolicyState? ReadOriginalFromLedger()
    {
        try
        {
            if (!File.Exists(LedgerPath))
            {
                return null;
            }

            string? lastEnable = null;
            foreach (var line in File.ReadLines(LedgerPath))
            {
                if (line.Contains("\"action\":\"enable\"", StringComparison.Ordinal))
                {
                    lastEnable = line;
                }
            }

            if (lastEnable is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(lastEnable);
            var root = document.RootElement;

            var state = new SingleSessionPolicyState();
            if (root.TryGetProperty("beforeLocal", out var beforeLocal)
                && beforeLocal.ValueKind == JsonValueKind.Number)
            {
                state.LocalValue = beforeLocal.GetInt32();
            }

            if (root.TryGetProperty("beforePolicy", out var beforePolicy)
                && beforePolicy.ValueKind == JsonValueKind.Number)
            {
                state.PolicyValue = beforePolicy.GetInt32();
            }

            return state;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读一个 DWORD。值不存在返回 null；读失败抛异常（由调用方统一处理）。</summary>
    private static int? ReadDword(string keyPath)
    {
        var (hive, subKey) = SplitKeyPath(keyPath);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        if (key is null)
        {
            return null;
        }

        var raw = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return raw switch
        {
            null => null,
            int number => number,
            _ => Convert.ToInt32(raw),
        };
    }

    private static (RegistryHive Hive, string SubKey) SplitKeyPath(string keyPath)
    {
        var text = (keyPath ?? string.Empty).Trim();
        if (!text.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"只支持 HKLM 下的键：{keyPath}", nameof(keyPath));
        }

        var rest = text.Length > 4 ? text[4..].TrimStart('\\') : string.Empty;
        if (rest.Length == 0)
        {
            throw new ArgumentException($"键路径缺少子键：{keyPath}", nameof(keyPath));
        }

        return (RegistryHive.LocalMachine, rest);
    }

    /// <summary>记一行账本。写不进去不影响结果（但要能让排障的人看到原因）。</summary>
    private static void LogLedger(
        string action,
        SingleSessionPolicyState? before,
        SingleSessionPolicyState? after,
        bool ok,
        string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LedgerPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var entry = new Dictionary<string, object?>
            {
                ["at"] = DateTimeOffset.Now.ToString("o"),
                ["action"] = action,
                ["host"] = Environment.MachineName,
                ["user"] = Environment.UserName,
                ["sid"] = RdpSessionService.TryResolveAccountSid(string.Empty, Environment.UserName),
                ["value"] = ValueName,
                ["beforeLocal"] = before?.LocalValue,
                ["beforePolicy"] = before?.PolicyValue,
                ["afterLocal"] = after?.LocalValue,
                ["afterPolicy"] = after?.PolicyValue,
                ["ok"] = ok,
                ["message"] = message,
                ["version"] = typeof(RdpSingleSessionPolicy).Assembly.GetName().Version?.ToString() ?? "unknown",
            };

            File.AppendAllText(
                LedgerPath,
                JsonSerializer.Serialize(entry) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            // 账本写不进去不应该改变"策略到底改没改"这个事实
        }
    }
}
