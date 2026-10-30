// -*- coding: utf-8 -*-
using Microsoft.UI.Xaml;
using Yomori.Helpers;

namespace Yomori.ViewModels;

/// <summary>
/// 右侧执行状态栏里的一行「执行项目」—— 一条会话通道，或未分配通道的「本地执行」组。
///
/// <para>**为什么固定建好 8 行复用，而不是每秒重建集合**：状态栏以 1Hz 轮询刷新
/// （通道会话的属性不可观察，只能轮询）。若每拍 <c>Clear()</c> + <c>Add()</c>，
/// ItemsControl 会整体重建每个项的容器，列表在视觉上每秒闪一下 ——
/// 主页的「会话通道」概览就是那种写法，但它只在主页可见时才跑。
/// 通道数上限本来就是 8，一次建满、之后只改属性最省事也最稳。</para>
///
/// <para>字段全部是「显示用字符串」，不做任何计算 —— 取值与汇总都在
/// <c>MainWindow.RefreshSideStatus</c> 里一次算完，这里只负责扛住变更通知。</para>
/// </summary>
public sealed class SideStatusRow : ObservableObject
{
    private string _name = string.Empty;
    private string _progressText = string.Empty;
    private string _shortText = string.Empty;
    private string _stateText = string.Empty;
    private double _progressRatio;
    private Visibility _activeDot = Visibility.Collapsed;
    private Visibility _visible = Visibility.Collapsed;

    /// <summary>通道名（「本地执行」或配置里的通道显示名）。</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>该通道的完成数 / 总数，例如 <c>3 / 7</c>。</summary>
    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    /// <summary>
    /// 该通道的进度比例（0..1），直接喂给这一行自己的细进度条。
    ///
    /// <para>不做成"从 ProgressText 里解析"：比例是本轮汇总时就有的原始数字，
    /// 让界面去拆字符串既慢又容易在「合计」「总计」这类文案上出错。</para>
    /// </summary>
    public double ProgressRatio
    {
        get => _progressRatio;
        set => SetProperty(ref _progressRatio, value);
    }

    /// <summary>该通道的状态文案（运行中 / 已完成 / 已失联…）。</summary>
    public string StateText
    {
        get => _stateText;
        set => SetProperty(ref _stateText, value);
    }

    /// <summary>
    /// 收起态那一行用的短文本，形如 <c>z 3/7</c>。
    ///
    /// <para>44px 的窄条放不下通道名，只留**名字的第一个字**做区分 —— 纯数字会变成一串
    /// 没法对号的进度（三条通道都还没开始时全是 <c>0/0</c>，看着完全一样）。
    /// 完整名字挂在悬浮提示上，拿不准时悬停一下就知道。</para>
    /// </summary>
    public string ShortText
    {
        get => _shortText;
        set => SetProperty(ref _shortText, value);
    }

    /// <summary>收起态用来区分通道的那个字：「本地执行」→「本」，「zdh」→「z」。</summary>
    private static string ShortName(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "－";
        }

        // 代理对（emoji 之类的通道名）要按码点取，切掉半个字符会显示成方块
        var length = char.IsHighSurrogate(text[0]) && text.Length > 1 ? 2 : 1;
        return text[..length];
    }

    /// <summary>正在跑的那一行前面亮一个色点，方便一眼扫到。</summary>
    public Visibility ActiveDot
    {
        get => _activeDot;
        set => SetProperty(ref _activeDot, value);
    }

    /// <summary>行本身的可视性 —— 实际通道数少于 8 时，多出来的行整行收起。</summary>
    public Visibility Visible
    {
        get => _visible;
        set => SetProperty(ref _visible, value);
    }

    /// <summary>把某一行的显示内容整体刷一遍（值没变时 SetProperty 不会发通知）。</summary>
    public void Show(string name, int progress, int total, string stateText, bool active)
    {
        Name = name;

        // 总数是 0（还没下发 / 刚复位）时给 0 而不是 NaN —— ProgressBar 拿到 NaN 会把值拦在旧值上
        ProgressRatio = total > 0 ? Math.Clamp((double)progress / total, 0d, 1d) : 0d;
        ProgressText = $"{progress} / {total}";
        ShortText = $"{ShortName(name)} {progress}/{total}";
        StateText = stateText;
        ActiveDot = active ? Visibility.Visible : Visibility.Collapsed;
        Visible = Visibility.Visible;
    }

    /// <summary>这一拍用不到这一行（本轮通道数变少了）—— 收起，但不清空文本。</summary>
    public void Hide()
    {
        ActiveDot = Visibility.Collapsed;
        Visible = Visibility.Collapsed;
    }
}
