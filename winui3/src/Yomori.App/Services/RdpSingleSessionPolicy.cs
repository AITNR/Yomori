// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Yomori.Services;

/// <summary>单账户单会话策略的状态。</summary>
public enum SingleSessionPolicyStatus
{
    /// <summary>读失败 —— 什么都不做，也不许据此推断。</summary>
    ReadFailed = 0,

    /// <summary>两处都没有这个值（取系统默认）。</summary>
    Absent = 1,

    /// <summary>生效值为 1：同账户只允许一条会话。</summary>
    Enabled = 2,

    /// <summary>生效值为 0：允许同账户多会话。</summary>
    Disabled = 3,

    /// <summary>策略键里有值，且它覆盖了本机键（值见 <see cref="SingleSessionPolicyState.PolicyValue"/>）。</summary>
    OverriddenByPolicyKey = 4,
}

/// <summary>一次读取的结果：两处原始值 + 合并结论。</summary>
public sealed class SingleSessionPolicyState
{
    /// <summary>本机键（HKLM\SYSTEM\...\Terminal Server）的值：null = 不存在。</summary>
    public int? LocalValue { get; set; }

    /// <summary>策略键（HKLM\SOFTWARE\Policies\...）的值：null = 不存在。</summary>
    public int? PolicyValue { get; set; }

    public SingleSessionPolicyStatus Status { get; set; } = SingleSessionPolicyStatus.ReadFailed;

    public string Error { get; set; } = string.Empty;

    /// <summary>最终生效值：策略键优先。null 表示两处都没有。</summary>
    public int? Effective => PolicyValue ?? LocalValue;

    /// <summary>能不能据此认为「同一账户只会有一条会话」。只有生效值等于 1 才算。</summary>
    public bool Proven => Status != SingleSessionPolicyStatus.ReadFailed && Effective == 1;

    public string Describe() => Status switch
    {
        SingleSessionPolicyStatus.Enabled => "已开启（同一账户只保留一条会话）",
        SingleSessionPolicyStatus.Disabled => "未开启（允许同一账户开多条会话）",
        SingleSessionPolicyStatus.Absent => "未设置（取系统默认，等同于允许同一账户开多条会话）",
        SingleSessionPolicyStatus.OverriddenByPolicyKey =>
            $"被策略键覆盖为 {PolicyValue?.ToString() ?? "?"}",
        _ => $"读取失败：{Error}",
    };

    /// <summary>两处来源的一行说明（排障 / 弹窗正文用）。</summary>
    public string Detail =>
        $"本机键 {(LocalValue is int local ? local.ToString() : "不存在")}、" +
        $"策略键 {(PolicyValue is int policy ? policy.ToString() : "不存在")}";
}

/// <summary>一次写入尝试的结果。</summary>
public sealed class SingleSessionPolicyChange
{
    public bool Ok { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>写入前的状态（只用于日志与审计）。</summary>
    public SingleSessionPolicyState? Before { get; set; }

    public SingleSessionPolicyState? After { get; set; }

    /// <summary>这次是否动了策略键（意味着它原本就被别的东西写成了 0）。</summary>
    public bool TouchedPolicyKey { get; set; }
}

/// <summary>
/// <c>fSingleSessionPerUser</c>（限制远程桌面服务用户只使用一个会话）的读写入口。
///
/// <para>【为什么要有这个类】真机证据（2026-10-26）：RDP Wrapper 安装时会把
/// <b>策略键</b>里的这一项写成 0（同一键下还有 <c>MaxInstanceCount=999999</c>），
/// 于是每次 RDP 登录都新建一条会话，旧的留成「已断开」。同一个账户越攒越多会话，
/// 任务派发也就没法确定该落到哪一条。修好这一项，问题从根上消失。</para>
///
/// <para>【两处都要看】本机键是 Windows 自己读的那份；策略键（Policies）有值时覆盖它。
/// 实测组合「本机=1、策略=0」的生效值是 <b>0</b> —— 只写本机键等于没改。</para>
///
/// <para>【写入时机】只有两个：① 解锁 RDP 流程的「修改注册表」阶段（走事务账本，可还原）；
/// ② 用户点「连接到通道」时被明确问过一次并选了「修改」。绝不在启动、连接、
/// 任务执行的任何其它路径上顺手改它。</para>
/// </summary>
public static class RdpSingleSessionPolicy
{
    /// <summary>本机键（Windows 自己读的那一处）。</summary>
    public const string LocalKeyPath = @"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server";

    /// <summary>策略键（域 / 本地策略下发的那一处，Wrapper 也会往这里写）。</summary>
    public const string PolicyKeyPath = @"HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    public const string ValueName = "fSingleSessionPerUser";

    /// <summary>变更审计：与桥目录同级，事后能看出是谁在什么时候改的。</summary>
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
            // 策略键有值就等于它说了算：值是多少就是多少，不假装自己知道原因。
            state.Status = policy == 1
                ? SingleSessionPolicyStatus.Enabled
                : SingleSessionPolicyStatus.OverriddenByPolicyKey;
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
    /// 把两处都写成 1，并回读确认。
    ///
    /// <para>策略键的处理原则：<b>只在它已经有值时才写</b>。有值说明确实有东西在用 0 覆盖本机键
    /// （这台机器上是 RDP Wrapper 的安装脚本），不改它等于没改；没有值时不去创建，
    /// 免得凭空替管理员下一条策略。</para>
    /// </summary>
    public static SingleSessionPolicyChange Enable()
    {
        var change = new SingleSessionPolicyChange { Before = Read() };
        var before = change.Before;

        if (before.Status == SingleSessionPolicyStatus.ReadFailed)
        {
            change.Message = $"读不到当前值，已停止（不做无把握的写入）：{before.Error}";
            LogLedger("enable", before, null, false, change.Message);
            return change;
        }

        var needLocal = before.LocalValue != 1;
        var needPolicy = before.PolicyValue is int policy && policy != 1;

        if (!needLocal && !needPolicy)
        {
            change.Ok = true;
            change.After = before;
            change.Message = "两处都已经是 1，无需修改。";
            return change;
        }

        var notes = new List<string>();

        if (needLocal)
        {
            var (ok, error) = WriteDword(LocalKeyPath, 1);
            if (!ok)
            {
                change.Message = $"写本机键失败：{error}";
                LogLedger("enable", before, null, false, change.Message);
                return change;
            }

            notes.Add($"本机键 0→1（原 {(before.LocalValue?.ToString() ?? "不存在")}）");
        }

        if (needPolicy)
        {
            var (ok, error) = WriteDword(PolicyKeyPath, 1);
            if (!ok)
            {
                change.Message = $"写策略键失败：{error}";
                LogLedger("enable", before, null, false, change.Message);
                return change;
            }

            change.TouchedPolicyKey = true;
            notes.Add($"策略键 {(before.PolicyValue?.ToString() ?? "不存在")}→1");
        }

        var after = Read();
        change.After = after;
        change.Ok = after.Proven;

        var summary = string.Join("；", notes);
        change.Message = after.Proven
            ? $"已改为 1 并回读确认（{summary}）。"
              + (change.TouchedPolicyKey
                  ? "策略键这一份若是由域策略下发的，会在下次策略刷新时被改回。"
                  : string.Empty)
            : $"写入后回读不是 1（{after.Describe()}，{after.Detail}），请检查权限或是否有别的程序在改同一项。";

        LogLedger("enable", before, after, change.Ok, change.Message);
        return change;
    }

    /// <summary>读一个 DWORD。值不存在返回 null；读失败抛异常。</summary>
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

    private static (bool Ok, string Error) WriteDword(string keyPath, int value)
    {
        try
        {
            var (hive, subKey) = SplitKeyPath(keyPath);
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return (false, $"键不存在或无权写入：{keyPath}（通常需要管理员权限）");
            }

            key.SetValue(ValueName, value, RegistryValueKind.DWord);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
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

    /// <summary>记一行审计。写不进去不影响"值到底改没改"这个事实。</summary>
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
                ["value"] = ValueName,
                ["beforeLocal"] = before?.LocalValue,
                ["beforePolicy"] = before?.PolicyValue,
                ["afterLocal"] = after?.LocalValue,
                ["afterPolicy"] = after?.PolicyValue,
                ["ok"] = ok,
                ["message"] = message,
            };

            File.AppendAllText(
                LedgerPath,
                JsonSerializer.Serialize(entry) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            // 审计失败不改变结论
        }
    }
}
