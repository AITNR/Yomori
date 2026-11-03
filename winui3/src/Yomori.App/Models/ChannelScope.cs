// -*- coding: utf-8 -*-
using Yomori.Helpers;

namespace Yomori.Models;

/// <summary>
/// 任务执行页左侧「执行通道」列表的一项 —— **编排任务的组织单位**
/// （计划书 <c>docs/tasks-by-channel-plan.md</c> §3.1）。
///
/// 三种来源：
///   · <see cref="IsLocal"/>      → 本地执行（<c>Id = ""</c>），恒为第一项，永远可用；
///   · 普通通道                    → 对应 <c>Config.Rdp.Channels</c> 里的一条；
///   · <see cref="IsMissing"/>     → 任务指向了已被删除的通道（孤儿），单独成组以便改派，
///                                  不然那些任务会变成"看不见、但执行时被静默跳过"的死角。
///
/// 只做展示与计数，**不是数据源**：真正的任务仍然只有 <c>MainViewModel.Tasks</c> 一份。
///
/// ⚠️ 本类不能引 <c>Microsoft.UI.Xaml</c> 的类型（哪怕是 <c>Visibility</c>）：
/// 冒烟工程 <c>_smoke</c> 是把这些 .cs 直接 <c>Compile Include</c> 进一个纯 .NET
/// 工程编译的，一旦引了界面类型就编不过。界面侧要用 x:Load / 静态函数绑定绕开。
/// </summary>
public sealed class ChannelScope : ObservableObject
{
    private int _taskCount;
    private bool _enabled = true;
    private bool _isUsable = true;
    private string _hintText = string.Empty;
    private string _subtitle = string.Empty;

    /// <summary>空串表示本地执行（与 <see cref="TaskConfig.ChannelId"/> 同一套语义）。</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 名称下面那行小字：目标主机 / 账户 / 凭据 / 停用。
    /// 可写是为了**就地**跟着开关变化（停用后要补上「· 已停用」），
    /// 不必为此重建整个 <c>ChannelScopes</c> 集合。
    /// </summary>
    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public bool IsLocal { get; init; }

    /// <summary>指向已删除的通道。</summary>
    public bool IsMissing { get; init; }

    /// <summary>
    /// 这一行的开关状态。
    ///
    /// <para>远程通道：镜像 <see cref="RdpChannel.Enabled"/>，**不是第二份数据源**，
    /// 界面改动由 <c>MainViewModel.SetScopeEnabled</c> 写回通道配置并落盘。</para>
    ///
    /// <para>本地执行：镜像 <c>AppConfig.LocalExecutionEnabled</c>（2026-11-03 起本地也能关）。
    /// 关掉只是"本轮不跑本机任务"，任务不会丢。</para>
    ///
    /// <para>未知通道没有开关，这个值对它没有意义。</para>
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    /// <summary>
    /// 这一行能不能切开关。
    ///
    /// <para>只有「未知通道」不行 —— 它的配置已经不存在，开关写不回去。
    /// 本地执行与远程通道一样可以关（2026-11-03 改：原先本地恒执行、不给开关，
    /// 想单独跑远端时没法把本机那一组按下去）。</para>
    /// </summary>
    public bool CanToggle => !IsMissing;

    /// <summary>
    /// 开关的悬停说明。本地执行与远程通道的语义不同（本地没有「通道管理」页那一份配置），
    /// 用同一句会指向一个不存在的地方。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ToggleTooltip => IsLocal
        ? "本地执行本轮要不要跑：关掉后本机那批任务不会下发。任务本身保留，随时可以再打开。"
        : "这条通道本轮要不要执行：关掉后它的任务不会下发。改动即时保存，下一次「开始执行」生效；与「通道管理」页里的「启用该通道」是同一个开关。";

    /// <summary>能不能往里排任务（通道停用 / 未配账户 / RDP 总开关关掉都为 false）。</summary>
    public bool IsUsable
    {
        get => _isUsable;
        set
        {
            if (SetProperty(ref _isUsable, value))
            {
                OnPropertyChanged(nameof(RowOpacity));
            }
        }
    }

    /// <summary>不可用原因（停用 / 未配账户 / 总开关关闭 / 未知通道），可用时为空串。</summary>
    public string HintText
    {
        get => _hintText;
        set
        {
            if (SetProperty(ref _hintText, value))
            {
                OnPropertyChanged(nameof(ShowHint));
            }
        }
    }

    public int TaskCount
    {
        get => _taskCount;
        set
        {
            if (SetProperty(ref _taskCount, value))
            {
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string CountText => _taskCount == 0 ? "无任务" : $"{_taskCount} 个任务";

    public double RowOpacity => IsUsable ? 1.0 : 0.5;

    public bool ShowHint => HintText.Length > 0;

    /// <summary>
    /// 能否「添加」任务：孤儿组不行（新建任务不该再制造孤儿）。
    /// **停用 / 未配账户的通道照样能排任务** —— 那些任务只是本轮不执行，
    /// 禁止编辑反而会让用户没法提前排好再启用。
    /// </summary>
    public bool CanAddTask => !IsMissing;
}
