// -*- coding: utf-8 -*-
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>等待 / 完成判断模式（配置值保持与 Python 版一致的 snake_case）。</summary>
public static class WaitModes
{
    public const string DirectProcess = "direct_process";
    public const string ProcessName = "process_name";
    public const string FireAndContinue = "fire_and_continue";
    public const string WindowTitle = "window_title";
    public const string CmdlineKeyword = "cmdline_keyword";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>
    {
        new(DirectProcess, "等待直接启动进程"),
        new(ProcessName, "等待指定进程名"),
        new(FireAndContinue, "启动后不等待"),
        new(WindowTitle, "等待窗口标题出现后消失"),
        new(CmdlineKeyword, "等待命令行出现后消失"),
    };

    public static string Normalize(string? value) =>
        value is DirectProcess or ProcessName or FireAndContinue or WindowTitle or CmdlineKeyword
            ? value
            : DirectProcess;

    public static string Label(string? value)
    {
        foreach (var item in Items)
        {
            if (item.Key == value)
            {
                return item.Value;
            }
        }

        return Items[0].Value;
    }
}

/// <summary>超时处理方式。</summary>
public static class TimeoutActions
{
    public const string KillAndContinue = "kill_and_continue";
    public const string SkipAndContinue = "skip_and_continue";
    public const string StopAll = "stop_all";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>
    {
        new(KillAndContinue, "强制结束并继续"),
        new(SkipAndContinue, "不结束，跳过并继续"),
        new(StopAll, "停止全部任务"),
    };

    public static string Normalize(string? value) =>
        value is KillAndContinue or SkipAndContinue or StopAll ? value : KillAndContinue;

    public static string Label(string? value)
    {
        foreach (var item in Items)
        {
            if (item.Key == value)
            {
                return item.Value;
            }
        }

        return Items[0].Value;
    }
}

/// <summary>并发组完成策略。</summary>
public static class ConcurrentPolicies
{
    public const string WaitAll = "wait_all";
    public const string WaitFirst = "wait_first";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>
    {
        new(WaitAll, "等待本组全部完成"),
        new(WaitFirst, "只等本组第一个完成"),
    };

    public static string Normalize(string? value) => value is WaitAll or WaitFirst ? value : WaitAll;

    public static string Label(string? value)
    {
        foreach (var item in Items)
        {
            if (item.Key == value)
            {
                return item.Value;
            }
        }

        return Items[0].Value;
    }
}

/// <summary>监控目标类型（由 V31.17.1 关键词优先级推导）。</summary>
public static class MonitorKinds
{
    public const string Direct = "direct";
    public const string Process = "process";
    public const string Window = "window";
    public const string Cmdline = "cmdline";
}

/// <summary>
/// 检查更新时看的发布通道。
///
/// 与仓库的发布流程一一对应（见 .github/workflows/）：
/// <list type="bullet">
/// <item><see cref="Stable"/>（正式版）—— <c>release.yml</c> 手动发布的 <c>vX.Y.Z</c>；
/// 接口取 <c>/releases/latest</c>，也就是「最新的非预发布版」。</item>
/// <item><see cref="Beta"/>（测试版）—— <c>ci.yml</c> 每次 main 提交自动发的滚动预发布。
/// 接口只在**预发布**里挑版本号最大的一份（正式版与 dev 构建编号不同源，混着比会让
/// 正式版永远赢 —— 2026-10-25 修过这个：「选了测试版却拿到正式版」）。</item>
/// </list>
/// 值会写进 config.json，改名字等于改配置格式，别随手改。
/// </summary>
public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Beta = "beta";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>
    {
        new(Stable, "正式版"),
        new(Beta, "测试版"),
    };

    public static string Normalize(string? value) => value == Beta ? Beta : Stable;

    public static string Label(string? value) => value == Beta ? "测试版" : "正式版";
}

/// <summary>
/// 启动时自动检查更新的方式（写进 config.json 的 <c>update_check_mode</c>）。
///
/// <para>为什么默认是 <see cref="Notify"/>：用户要的是"每次启动自动检测、有就告诉我"，
/// 但**未经同意自动下载**属于会占带宽、会落盘的副作用，不该是默认行为 ——
/// 想省这一步的人在设置里选 <see cref="Download"/> 即可。</para>
/// </summary>
public static class UpdateCheckModes
{
    /// <summary>不检查（连网络请求都不发）。</summary>
    public const string Off = "off";

    /// <summary>检查，有更新时进通知中心。</summary>
    public const string Notify = "notify";

    /// <summary>检查，有更新时后台下载，下载完问要不要现在装。</summary>
    public const string Download = "download";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>
    {
        new(Off, "不检查更新"),
        new(Notify, "有更新时通知我"),
        new(Download, "有更新时自动下载"),
    };

    /// <summary>未知 / 空值一律回落"启动时通知"（用户提这个需求时要的就是它）。</summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            Off => Off,
            Download => Download,
            _ => Notify,
        };
    }

    public static string Label(string? value) => Normalize(value) switch
    {
        Off => "不检查更新",
        Download => "有更新时自动下载",
        _ => "有更新时通知我",
    };
}

/// <summary>
/// 检查更新 / 下载安装包时走哪种代理（写进 config.json 的 <c>update_proxy_mode</c>）。
///
/// <para>为什么单独给下载配一个开关：用户可能只给浏览器挂了代理（或者反过来），
/// 而 GitHub 的 release 资产在部分网络下直连会超时。默认跟随系统代理 —— 那是不做任何
/// 额外配置时最不意外的行为。</para>
/// </summary>
public static class UpdateProxyModes
{
    /// <summary>跟随系统代理（WinINET / 环境变量，<c>HttpClient.DefaultProxy</c>）。</summary>
    public const string System = "system";

    /// <summary>自定义代理地址，形如 <c>http://127.0.0.1:7890</c>。</summary>
    public const string Custom = "custom";

    /// <summary>不走代理（直连）。系统代理配错时用它绕开。</summary>
    public const string None = "none";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>
    {
        new(System, "系统代理"),
        new(Custom, "自定义代理"),
        new(None, "不使用代理"),
    };

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            Custom => Custom,
            None => None,
            _ => System,
        };
    }

    public static string Label(string? value) => Normalize(value) switch
    {
        Custom => "自定义代理",
        None => "不使用代理",
        _ => "系统代理",
    };
}
