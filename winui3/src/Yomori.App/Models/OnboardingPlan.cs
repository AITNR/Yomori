// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>
/// 一步「开始引导」。<b>纯数据</b>：不得引用任何 Microsoft.UI.Xaml 类型 ——
/// 冒烟工程直接编译 Models/*.cs，链进来就得能跑（见 _smoke/Smoke.csproj）。
/// </summary>
public sealed class OnboardingStep
{
    public OnboardingStep(
        int index,
        string pageTag,
        string? targetName,
        string title,
        string body,
        string? imageRelativePath,
        bool scrollToTop = false)
    {
        Index = index;
        PageTag = pageTag;
        TargetName = targetName;
        Title = title;
        Body = body;
        ImageRelativePath = imageRelativePath;
        ScrollToTop = scrollToTop;
    }

    /// <summary>从 1 开始的步骤序号。</summary>
    public int Index { get; }

    /// <summary>该步要停留的页面 tag，与 MainWindow.NavigateTo 的取值一致。</summary>
    public string PageTag { get; }

    /// <summary>要聚焦的控件 x:Name；null = 不挖洞，气泡居中（最后一步配图）。</summary>
    public string? TargetName { get; }

    public string Title { get; }

    public string Body { get; }

    /// <summary>相对 AppPaths.AssetsDir 的图片路径；null = 该步没有配图。</summary>
    public string? ImageRelativePath { get; }

    /// <summary>
    /// 换页后是否把目标精确滚到内容视口最顶部。
    /// 长卡片（设置页 RDP 模块）必须这么做：StartBringIntoView 只做最小滚动，
    /// 卡片会停在视口下半截，不同分辨率下底部被截，且滚动动画中测坐标会让高亮框套歪。
    /// </summary>
    public bool ScrollToTop { get; }
}

/// <summary>引导的静态步骤表与全部文案。改文案时冒烟里的文字锚点会同步变红，按约定换锚点、不删判据。</summary>
public static class OnboardingPlan
{
    public const string HomeTag = "home";
    public const string SettingsTag = "settings";
    public const string ChannelMgmtTag = "channel-mgmt";

    // ---- 目标控件的 x:Name：XAML 与控制器共用，冒烟逐项核对 ----
    public const string TargetQuickTasks = "QuickTasksButton";
    public const string TargetQuickStats = "QuickStatsButton";
    public const string TargetQuickLogs = "QuickLogsButton";
    public const string TargetQuickChannels = "QuickChannelsButton";
    public const string TargetRdpUnlockCard = "RdpUnlockCard";
    public const string TargetAddChannel = "AddChannelButton";

    /// <summary>设置页里用于把 RDP 卡片滚进视口的 ScrollViewer（不参与聚焦，只参与定位时序）。</summary>
    public const string SettingsScrollName = "SettingsScroll";

    /// <summary>「添加 Windows 本地用户」截图，落在仓库根 assets/ 下，随包整目录分发。</summary>
    public const string ImageAddLocalUser = "onboarding/add_local_user.png";

    /// <summary>结束时回到的页面（用户确认：回到首页）。</summary>
    public const string FinishTag = HomeTag;

    public static IReadOnlyList<OnboardingStep> Build()
    {
        return new[]
        {
            new OnboardingStep(1, HomeTag, TargetQuickTasks,
                "任务执行",
                "在这里编排任务：给每条通道安排要运行的脚本与顺序。",
                null),

            new OnboardingStep(2, HomeTag, TargetQuickStats,
                "运行耗时统计",
                "在这里查看上一次的运行时间，以及平均执行时间。",
                null),

            new OnboardingStep(3, HomeTag, TargetQuickLogs,
                "运行日志",
                "在这里查看 Yomori 的运行日志，排查任务与连接问题。",
                null),

            new OnboardingStep(4, HomeTag, TargetQuickChannels,
                "通道管理",
                "在这里添加并编辑用来执行任务的通道。",
                null),

            new OnboardingStep(5, SettingsTag, TargetRdpUnlockCard,
                "远程桌面解锁（RDP）",
                "通道功能依赖本机支持多账户同时远程连接。这里四项检测结果全部显示「通过」（全绿）后，通道才能正常使用。点「解锁 RDP」开始；组件需要联网下载，网络波动可能导致解锁失败，可以多点击几次重试。",
                null,
                scrollToTop: true),

            new OnboardingStep(6, ChannelMgmtTag, TargetAddChannel,
                "新建通道",
                "在这里添加通道。创建前需要两个前提：电脑已经解锁 RDP，并且已经添加了 Windows 用户账户。",
                null),

            new OnboardingStep(7, ChannelMgmtTag, null,
                "如何添加新用户",
                "打开 Windows 设置 → 账户 → 其他用户 → 添加账户；在「为这台电脑创建用户」中选择「我没有这个人的登录信息」→「添加一个没有 Microsoft 账户的用户」，填写用户名与密码后点「下一步」。创建完成后回到 Yomori，把该账户填入通道。",
                ImageAddLocalUser),
        };
    }

    /// <summary>引导文案里常驻的两行辅助信息（气泡底部）。</summary>
    public const string HintText = "点击任意位置继续";
    public const string SkipText = "跳过";
}
