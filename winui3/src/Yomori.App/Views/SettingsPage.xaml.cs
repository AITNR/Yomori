// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Yomori.Helpers;
using Yomori.Models;
using Yomori.Services;
using Yomori.ViewModels;

namespace Yomori.Views;

public sealed partial class SettingsPage : Page
{
    private bool _syncing;
    private CancellationTokenSource? _themeHintCts;

    /// <summary>上一次「检查更新」的结果，用来决定「前往下载」按钮指哪儿。</summary>
    private GitHubRelease? _latestRelease;

    /// <summary>最近一次 RDP 解锁只读检测的结果；「查看诊断」按它出文本，不再重新跑一遍检测。</summary>
    private RdpUnlockSnapshot? _rdpSnapshot;

    /// <summary>用户手动指定的安装包路径；空串表示按官方发布下载。</summary>
    private string _rdpManualInstaller = "";

    /// <summary>
    /// 用户在「指定 INI」里粘贴的补丁段文本。点「写入 INI」时已就地合并落盘；
    /// 这里再留一份，是为了解锁流程里的安装器覆盖 rdpwrap.ini 之后能重新合并回来。
    /// </summary>
    private string _rdpManualIniText = "";

    /// <summary>上一次修改操作的完整报告（含逐步现场）。「查看诊断」优先把它带出来。</summary>
    private string _rdpLastReport = "";

    /// <summary>
    /// 只读检测是否正在进行。互斥有两层意义：按钮连点不会叠起两次检测；
    /// 以及「移动中」的结果不乱序盖掉上一份（检测本身是毫秒级，但服务状态的读取是 Win32 调用）。
    /// </summary>
    private bool _rdpDetecting;

    /// <summary>
    /// 是否正在跑修改操作（解锁 / 还原）。与 <see cref="_rdpDetecting"/> 分开记，
    /// 因为两者不能互相覆盖：检测可以单独跑，修改期间检测只在流程内部做。
    /// </summary>
    private bool _rdpBusy;

    /// <summary>页面是否已经卸载。卸载之后不能再往控件上写（会摸到失效元素）。</summary>
    private bool _unloaded;

    /// <summary>
    /// 首次 Loaded 才自动检测。
    /// 为什么不用构造函数：构造函数里控件树还没挂到窗口上，检测结果返回时若页面尚未 Loaded，
    /// 写入的内容会被后续的布局重建覆盖掉。为什么只认第一次：改主题会让根元素的
    /// RequestedTheme 变化、内容树重建，Loaded 会再来一次 —— 那时重跑检测纯属浪费。
    /// </summary>
    private bool _rdpAutoDetectDone;

    /// <summary>通道预检下拉正在程序性同步选中项，避免 SelectionChanged 回写造成循环。</summary>
    private bool _syncingReadinessChannel;

    /// <summary>预检结果只在本页首次加载时刷一次（改主题会让 Loaded 重来）。</summary>
    private bool _readinessRefreshed;

    // ---------------- 分类导航（2026-11-14 重构） ----------------

    /// <summary>分类键。与 XAML 里 Section_&lt;key&gt; 面板、导航项 Tag 一一对应。</summary>
    private static readonly string[] SectionKeys =
    {
        "general", "connection", "notifications", "environment",
        "updates", "data", "developer", "about",
    };

    /// <summary>当前显示的分类。主题切换会重建内容树、Loaded 再来一次，靠它把分类补回去。</summary>
    private string _currentSection = SectionKeys[0];

    /// <summary>InitializeComponent 期间 ListView 的初始化 SelectionChanged 不算用户操作。</summary>
    private bool _sectionsReady;

    /// <summary>ShowSection 同步导航选中态时，挡住 SelectionChanged 回灌造成递归。</summary>
    private bool _syncingSection;

    /// <summary>
    /// 用户点左侧分类：只切 Visibility，不重建任何集合。
    /// 动态 Clear() 再 Add 会撞上树进入期的 stowed exception。
    /// </summary>
    private void OnSectionNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_sectionsReady || _syncingSection || _unloaded)
        {
            return;
        }

        if (e.AddedItems.Count > 0
            && e.AddedItems[0] is ListViewItem item
            && item.Tag is string key)
        {
            ShowSection(key);
        }
    }

    /// <summary>切到指定分类：显示对应面板、更新页眉、同步导航选中态、滚动回顶部。</summary>
    private void ShowSection(string key, bool resetScroll = true)
    {
        if (Array.IndexOf(SectionKeys, key) < 0 || _unloaded)
        {
            return;
        }

        _currentSection = key;

        // 一律用 FindName 取控件：主题切换会重建内容树，生成字段可能指向失效元素。
        _syncingSection = true;
        try
        {
            foreach (var k in SectionKeys)
            {
                if (FindName("Section_" + k) is FrameworkElement panel)
                {
                    panel.Visibility = k == key ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            if (FindName("SectionTitle") is TextBlock title)
            {
                title.Text = SectionTitleOf(key);
            }

            if (FindName("SectionSubtitle") is TextBlock subtitle)
            {
                subtitle.Text = SectionSubtitleOf(key);
            }

            if (FindName("SectionNav") is ListView nav)
            {
                foreach (var navItem in nav.Items)
                {
                    if (navItem is ListViewItem li && li.Tag is string tag)
                    {
                        li.IsSelected = tag == key;
                    }
                }
            }
        }
        finally
        {
            _syncingSection = false;
        }

        if (resetScroll && FindName("SettingsScroll") is ScrollViewer scroll)
        {
            scroll.ChangeView(null, 0, null, true);
        }
    }

    /// <summary>
    /// 引导等外部定位：把目标控件所在的分类展开，并强制同步布局。
    /// RdpUnlockCard 在「环境检查」分类里，若分类折叠着，TransformToVisual 量不到
    /// 正确坐标（元素高宽为 0），高亮框会套歪。见 MainWindow.ScrollOnboardingTargetToTop。
    /// </summary>
    internal void EnsureSectionOfTargetVisible(string targetName)
    {
        if (_unloaded || FindName(targetName) is not FrameworkElement target)
        {
            return;
        }

        DependencyObject? node = target;
        while (node is not null)
        {
            if (node is FrameworkElement fe
                && fe.Tag is string key
                && Array.IndexOf(SectionKeys, key) >= 0)
            {
                ShowSection(key, resetScroll: false);
                // Visibility 改变只是标记布局失效，这里强制跑完一趟，
                // 后面紧接着的 TransformToVisual 才能量到展开后的尺寸。
                if (FindName("SettingsScroll") is ScrollViewer scroll)
                {
                    scroll.InvalidateMeasure();
                    scroll.UpdateLayout();
                }

                return;
            }

            node = VisualTreeHelper.GetParent(node);
        }
    }

    private static string SectionTitleOf(string key) => key switch
    {
        "general" => "常规",
        "connection" => "连接与显示",
        "notifications" => "通知",
        "environment" => "环境检查",
        "updates" => "更新",
        "data" => "数据与日志",
        "developer" => "开发者",
        "about" => "关于",
        _ => "全局设置",
    };

    private static string SectionSubtitleOf(string key) => key switch
    {
        "general" => "程序启动、收尾与界面相关的常用设置。",
        "connection" => "远程桌面的连接方式、画面解码与会话行为；改动大多在下次连接生效。",
        "notifications" => "任务与通道跑完时，本机主控端弹出的通知。",
        "environment" => "连接前的通道预检，以及本机多账户远程桌面（RDP）的只读检测与解锁。",
        "updates" => "版本信息、更新渠道、启动检查方式与下载代理。",
        "data" => "配置文件的导入导出、外部脚本日志归档与常用目录。",
        "developer" => "诊断开关与 GitHub 访问令牌。",
        "about" => "关于 Yomori、首次启动引导与开源许可。",
        _ => string.Empty,
    };

    /// <summary>
    /// 「重新观看开始引导」：一次性重播，不写配置、不改首次启动标记。
    /// 放在设置页是为了让跳过引导的用户有回头路。
    /// </summary>
    private void OnRestartOnboarding(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.RestartOnboarding();
    }

    public SettingsPage()
    {
        InitializeComponent();

        // 离开页面就别再往回写提示控件了
        Unloaded += (_, __) =>
        {
            _unloaded = true;
            _themeHintCts?.Cancel();
            // 单例 VM：页面切换时解绑，别把本页的控件留在订阅链上
            VM.PropertyChanged -= OnReadinessPropertyChanged;
        };

        Loaded += OnPageLoaded;

        // 2026-11-12：执行与通知 / 通道预检从通道管理页迁入。
        // 两个下拉的 ItemsSource 必须早于 SelectedValue 生效（x:Bind 求值顺序坑），
        // 所以在构造函数里显式赋值；Loaded 再补一次选中值。
        RenderBackendCombo.ItemsSource = RenderBackends.Items;
        SyncRenderBackendSelection();
        DecodeBackendCombo.ItemsSource = DecodeBackends.Items;
        SyncDecodeBackendSelection();

        // 预检的通道下拉完全在代码里管：绑 TwoWay 会在列表还是空的时候把
        // SelectedItem 的赋值丢掉，甚至把 null 回写进 VM.SelectedChannel。
        ReadinessChannelCombo.ItemsSource = VM.Channels;
        SyncReadinessChannelSelection();

        Loaded += OnReadinessSectionLoaded;
        VM.PropertyChanged += OnReadinessPropertyChanged;

        _syncing = true;
        try
        {
            ShutdownDelayBox.Value = VM.Config.ShutdownDelaySeconds;

            var theme = VM.Config.Theme;
            var index = 0;
            var found = 0;
            foreach (var item in AppConfig.ThemeItems)
            {
                if (item.Key == theme)
                {
                    found = index;
                    break;
                }

                index++;
            }

            ThemeBox.SelectedIndex = found;
            UpdateChannelBox.SelectedIndex = IndexOfUpdateChannel(VM.Config.UpdateChannel);
            CloseActionBox.SelectedIndex = IndexOfCloseAction(VM.Config.Window.CloseAction);

            // 启动检查方式 / 下载代理：同样在 _syncing 里摆初值，别让 SelectionChanged 把
            // "读配置"当成"用户改设置"而反复写盘。
            UpdateCheckModeBox.SelectedIndex = IndexOfItem(
                UpdateCheckModes.Items, UpdateCheckModes.Normalize(VM.Config.UpdateCheckMode));
            UpdateProxyModeBox.SelectedIndex = IndexOfItem(
                UpdateProxyModes.Items, UpdateProxyModes.Normalize(VM.Config.UpdateProxyMode));
            UpdateProxyUrlBox.Text = VM.Config.UpdateProxyUrl;
            ApplyProxyMode(VM.Config.UpdateProxyMode, DescribeStoredProxy(VM.Config.UpdateProxyUrl));
        }
        finally
        {
            _syncing = false;
        }

        RefreshVersionText();
        RefreshExternalLogSection();

        // 分类导航初始化完毕，此后 SelectionChanged 才算用户操作。
        // XAML 里第一项带 IsSelected="True"，构造期间那次回调要挡掉。
        _sectionsReady = true;
    }

    public MainViewModel VM => App.ViewModel;

    public IReadOnlyList<KeyValuePair<string, string>> ThemeItems => AppConfig.ThemeItems;

    private void OnShutdownDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        VM.Config.ShutdownDelaySeconds = (int)Math.Round(double.IsNaN(args.NewValue) ? 60 : args.NewValue);
    }

    /// <summary>
    /// ⚠️ 这里**不能**用 ContentDialog 报"已切换"。
    /// 改窗口根元素的 <c>RequestedTheme</c> 会让 ComboBox 重新模板化，SelectionChanged 会再触发一次；
    /// 于是同一个交互里连着两次 ShowAsync —— 第二次必抛 COMException 0x80000019
    /// （"Only a single ContentDialog can be open at any time"）。调用方若是 <c>_ = …</c> 的
    /// fire-and-forget 写法，未观察异常直接掀掉进程：实测表现就是"知道了"点不动，过一会儿窗口崩掉。
    /// 切换效果本来就肉眼可见，反馈改成页面内的一行文字，几秒后自动收起。
    /// </summary>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ThemeBox.SelectedItem is not KeyValuePair<string, string> item)
        {
            return;
        }

        // 值没变 = 程序性重设（改 RequestedTheme 会让 ComboBox 重新模板化，进而再触发一次
        // SelectionChanged），别当成用户操作 —— 否则会重复落盘 + 重复提示。
        if (VM.Config.Theme == item.Key)
        {
            return;
        }

        _syncing = true;
        try
        {
            VM.Config.Theme = item.Key;
            ApplyTheme(item.Key);
            VM.SaveConfig();

            ShowThemeHint($"已切换到「{item.Value}」。");
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ShowThemeHint(string text)
    {
        ThemeHintText.Text = text;
        ThemeHintText.Visibility = Visibility.Visible;

        // 连点下拉框时只留最后一次的计时
        _themeHintCts?.Cancel();
        var cts = new CancellationTokenSource();
        _themeHintCts = cts;

        _ = HideThemeHintLaterAsync(cts);
    }

    private async Task HideThemeHintLaterAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);

            if (!cts.IsCancellationRequested)
            {
                ThemeHintText.Visibility = Visibility.Collapsed;
            }
        }
        catch (TaskCanceledException)
        {
            // 已被下一次切换取代
        }
    }

    private static void ApplyTheme(string theme)
    {
        if (App.MainWindow?.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>
    /// 开发者模式：改完立刻生效（主控端 <c>Config.PropertyChanged</c> 那边重排日志页）
    /// 并立刻落盘 —— 它跟主题、更新渠道一样是"拨了就能看见"的开关，等自动保存的去抖窗口
    /// 走完再存的话，重启后悄悄回到关闭状态会很难理解。
    ///
    /// 两道 guard 各管一种情形：
    /// · <see cref="_syncing"/> —— 本页自己做的程序性赋值（构造期间的控件初始化）；
    /// · "值没变" —— <c>x:Bind</c> 把配置里的值写进 <c>IsOn</c> 时控件也会抛 <c>Toggled</c>
    ///   （构造期 <see cref="_syncing"/> 还没置位，挡不住这一类），那会儿两个值相等，
    ///   直接返回即可，不然会白写一次盘、白追一条日志。
    /// </summary>
    private void OnDeveloperModeToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        // 用 sender 而不是字段 DeveloperModeSwitch：Toggled 有可能在
        // InitializeComponent 期间（x:Bind 初次求值写 IsOn 时）就触发，
        // 那会儿字段是否已赋值不该成为一条隐含前提。
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        var on = toggle.IsOn;
        if (VM.Config.DeveloperMode == on)
        {
            return;
        }

        VM.Config.DeveloperMode = on;
        VM.SaveConfig();

        // ⚠️ 这两句**不能**写成 "[embed]" 那个字面标签：过滤是按行里有没有该标签做的，
        // 关掉的那一刻写进来的这句话会被自己过滤掉 —— 用户就看不到任何反馈了。
        VM.AppendLog(on
            ? "已打开开发者模式：运行日志页开始显示带 embed 标签的内嵌画面诊断日志。"
            : "已关闭开发者模式：运行日志页不再显示带 embed 标签的内嵌画面诊断日志（日志文件里仍完整保留）。");
    }

    /// <summary>
    /// 自动切回主控会话开关。
    ///
    /// <para>与开发者模式同一套路：<c>OneWay</c> 绑定 + 手动写回 + 立刻落盘。
    /// 原因不是风格问题 —— <c>TwoWay</c> 下 <c>Toggled</c> 早于绑定写回，
    /// 在回调里读配置会读到旧值，判断「有没有变」就永远不成立。</para>
    ///
    /// <para>关闭时顺手作废排队中的请求：用户关掉开关的意思就是「别再动我的桌面」。</para>
    /// </summary>
    private void OnAutoReturnToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        var on = toggle.IsOn;
        if (VM.RdpSettings.AutoReturnToControllerSession == on)
        {
            return;
        }

        VM.RdpSettings.AutoReturnToControllerSession = on;
        VM.SaveConfig();

        if (!on)
        {
            VM.SuppressControllerSessionReturn();
        }

        VM.AppendLog(on
            ? "已开启「连接本机通道后自动切回主控会话」：本机内嵌通道连上后，控制台会切回本程序所在会话。"
            : "已关闭「连接本机通道后自动切回主控会话」：之后不再自动切换桌面归属。");
    }

    /// <summary>
    /// UU 远程修复开关：只记录「是否启用」，**不立即执行**。
    ///
    /// <para>真正的应用交给 <see cref="UuSessionFixScheduler"/>：在「开始执行 / 连接通道」
    /// 这类可能改变控制台归属的操作之后静置 18 秒再动手，避免与正在建立的会话抢时序。
    /// 勾选时立刻注入会撞上连接过程，之前就是那么把 UU 弄得设备离线的。</para>
    ///
    /// <para>关闭时只取消尚未开始的等待，不重启 UU 服务 —— 撤销动作副作用太大，
    /// 不该藏在一个开关里。已生效的修正随服务重启自然失效。</para>
    /// </summary>
    private void OnUuFixToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        var on = toggle.IsOn;
        if (VM.RdpSettings.UuSessionFixEnabled == on)
        {
            return;
        }

        VM.RdpSettings.UuSessionFixEnabled = on;
        VM.SaveConfig();

        if (on)
        {
            UuFixStatusText.Visibility = Visibility.Visible;
            UuFixStatusText.Text = "已开启：正在装载修正…";

            // 立刻装 hook（不杀投屏进程）。装晚了的代价很实在：
            // 在这段窗口里 UU 会照旧去占通道会话，把内嵌画面踢掉并让主控崩掉。
            _ = LoadUuFixHookAsync();
            VM.AppendLog("[UU 修复] 已开启，正在装载会话修正。");
            return;
        }

        VM.CancelUuFixSchedule();
        UuFixStatusText.Visibility = Visibility.Visible;
        UuFixStatusText.Text = "已关闭：不再自动应用。已生效的修正会在 UU 服务重启后失效。";
        VM.AppendLog("[UU 修复] " + UuFixStatusText.Text);
    }

    /// <summary>开启开关后立刻装 hook；结果回 UI 线程再写状态文字。</summary>
    private async Task LoadUuFixHookAsync()
    {
        try
        {
            var result = await UuSessionFixService.InjectOnlyAsync(CancellationToken.None);
            ApplyUuFixStatus(result.IsSuccess
                ? "已开启：修正已装载。连接或开始执行后 18 秒会自动检查投屏进程所在会话。"
                : "已开启，但装载失败：" + result.Detail);
            VM.AppendLog("[UU 修复] " + result.Detail);
        }
        catch (Exception ex)
        {
            var detail = ex.Message.Length == 0 ? ex.GetType().Name : ex.Message;
            ApplyUuFixStatus("已开启，但装载异常：" + detail);
            VM.AppendLog("[UU 修复] 装载异常：" + detail);
        }
    }

    /// <summary>状态文字必须自己回 UI 线程（服务内部有 ConfigureAwait(false)）。</summary>
    private void ApplyUuFixStatus(string text)
    {
        void Apply()
        {
            UuFixStatusText.Visibility = Visibility.Visible;
            UuFixStatusText.Text = text;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Apply);
        }
    }

    /// <summary>
    /// AVC444 低色度模式开关。
    ///
    /// <para>与开发者模式同一套路：<c>OneWay</c> 绑定 + 手动写回 + 立刻落盘。
    /// <c>TwoWay</c> 下 <c>Toggled</c> 早于绑定写回，回调里读配置会读到旧值。</para>
    ///
    /// <para>落盘失败必须把开关拨回：留下「看着是开的、其实没存」的状态，
    /// 下次启动就静默恢复成关闭。</para>
    /// </summary>
    private void OnAvc444LowChromaToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        var on = toggle.IsOn;
        if (VM.RdpSettings.LowChromaAvc444 == on)
        {
            return;
        }

        VM.RdpSettings.LowChromaAvc444 = on;
        try
        {
            VM.SaveConfig();
        }
        catch (Exception ex)
        {
            VM.RdpSettings.LowChromaAvc444 = !on;
            toggle.IsOn = !on;
            var detail = ex.Message.Length == 0 ? ex.GetType().Name : ex.Message;
            VM.AppendLog("AVC444 低色度模式保存失败，已恢复原状：" + detail);
            return;
        }

        VM.AppendLog(on
            ? "已打开 AVC444 低色度模式（下次连接生效）：客户端只解主流并直接转 BGRA，彩色细边可能变软。"
            : "已关闭 AVC444 低色度模式（下次连接生效）：恢复完整 AVC444 4:4:4 解码。");
    }

    /// <summary>
    /// 「通道账户密码永不过期」开关：默认勾选，只作用于已添加进 Yomori 的本机本地通道账户。
    ///
    /// <para>刻意用 OneWay + Toggled：必须**先执行成功再落配置**。执行失败或被拒绝还原时
    /// 把开关拨回原位，不留下「看着是开的、其实没做」的状态。</para>
    ///
    /// <para>取消勾选会还原 Windows 账户的标志位；若某个账户的密码年龄已达系统策略上限，
    /// 服务整批拒绝还原 —— 那种情况下清掉标志位会让密码立刻过期、通道当场断连。</para>
    /// </summary>
    private async void OnChannelPasswordPolicyToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        var on = toggle.IsOn;
        if (VM.RdpSettings.ChannelPasswordNeverExpires == on)
        {
            return;
        }

        // 账户快照在界面线程取好；服务本身不读配置，也就不会碰到绑定对象。
        var accounts = VM.GetChannelPolicyAccounts();
        (bool AllOk, string Summary, int Changed) result;
        try
        {
            result = await VM.ApplyChannelPasswordPolicyAsync(accounts, on).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var detail = ex.Message.Length == 0 ? ex.GetType().Name : ex.Message;
            result = (false, "执行异常：" + detail, 0);
        }

        // await 的续体不保证在界面线程：所有控件写入与配置写入都显式投递回去。
        RunOnUi(DispatcherQueue, () =>
        {
            try
            {
                VM.AppendLog($"[账户策略] {(on ? "开启" : "关闭")}：{result.Summary}");

                if (!result.AllOk)
                {
                    _syncing = true;
                    try
                    {
                        toggle.IsOn = !on;
                    }
                    finally
                    {
                        _syncing = false;
                    }

                    VM.AppendLog("[账户策略] 执行未全部成功，开关已拨回原位，配置保持原值。");
                    return;
                }

                VM.RdpSettings.ChannelPasswordNeverExpires = on;
                VM.SaveConfig();
            }
            catch (Exception ex)
            {
                var detail = ex.Message.Length == 0 ? ex.GetType().Name : ex.Message;
                VM.AppendLog("[账户策略] 收尾失败：" + detail);
            }
        });
    }

    private async void OnExport(object sender, RoutedEventArgs e) => await VM.ExportConfigAsync();

    private async void OnImport(object sender, RoutedEventArgs e) => await VM.ImportConfigAsync();

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e) => VM.OpenDirectory(AppPaths.BaseDir);

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => VM.OpenDirectory(AppPaths.LogDir);

    private void OnOpenStatsFolder(object sender, RoutedEventArgs e) => VM.OpenDirectory(AppPaths.StatsDir);

    // ---------------- 外部脚本日志：占用 / 清理 ----------------

    /// <summary>
    /// 刷新「最多保留」输入框与占用概览。
    ///
    /// 每次进设置页都重算，不缓存 —— 日志文件是外部程序在写，
    /// 缓存会在用户刚跑完一轮时给出过期数字。
    /// </summary>
    private void RefreshExternalLogSection()
    {
        _syncing = true;
        try
        {
            ExternalLogKeepBox.Value = VM.Config.ExternalLogKeepFiles;
        }
        finally
        {
            _syncing = false;
        }

        var usage = ExternalLogStore.GetUsage();
        ExternalLogInfoText.Text = usage.FileCount == 0
            ? "还没有外部日志文件。任务跑起来、并且任务上配了外部日志路径之后，这里会显示占用。"
              + "（占用统计与清理同时覆盖两处：本机执行的那份，以及目标账户会话里代理写的那份。）"
            : $"共 {usage.FileCount} 份，占用 {usage.TotalText}。每通道每天一份，超出 {VM.Config.ExternalLogKeepFiles} 份的会在启动时自动清理。";
    }

    /// <summary>改「最多保留」—— 只落盘，清理交给启动流程或手动按钮。</summary>
    private void OnExternalLogKeepChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        var value = (int)Math.Round(double.IsNaN(args.NewValue) ? ExternalLogDefaults.KeepFiles : args.NewValue);
        VM.Config.ExternalLogKeepFiles = value;
        VM.SaveConfig();
        RefreshExternalLogSection();
    }

    /// <summary>手动清理超出保留份数的外部日志。</summary>
    private void OnExternalLogPrune(object sender, RoutedEventArgs e)
    {
        var keep = VM.Config.ExternalLogKeepFiles;
        var (deleted, freed) = ExternalLogStore.Prune(keep);
        VM.AppendLog(deleted == 0
            ? "外部日志清理：没有超出保留份数的文件。"
            : $"外部日志清理：已删除 {deleted} 份，释放 {ExternalLogStore.FormatBytes(freed)}。");
        RefreshExternalLogSection();

        // 明细面板正开着就跟着刷新，别让列表里留着已经删掉的文件
        if (ExternalLogUsagePanel.Visibility == Visibility.Visible)
        {
            RefreshExternalLogUsageDetail(announce: false);
        }

        SetExternalLogAction(deleted == 0
            ? $"清理：没有超出 {keep} 份的文件 —— {DateTime.Now:HH:mm:ss}"
            : $"清理：已删除 {deleted} 份，释放 {ExternalLogStore.FormatBytes(freed)} —— {DateTime.Now:HH:mm:ss}");
    }

    /// <summary>
    /// 刷新占用概览，并在本页展开占用明细。
    ///
    /// <para>⛔ 只往「运行日志」页追加一行是不够的 —— 用户站在设置页，
    /// 那一行在别的页面里，视觉上就是「点了没反应」。
    /// 所以反馈必须落在本页：明细列表 + 带时间戳的结果文字。</para>
    /// </summary>
    private void OnExternalLogUsage(object sender, RoutedEventArgs e)
    {
        RefreshExternalLogUsageDetail(announce: true);
    }

    /// <summary>填充并展开占用明细。<paramref name="announce"/> 为 false 时不覆盖结果文字（供清理后同步用）。</summary>
    private void RefreshExternalLogUsageDetail(bool announce)
    {
        var usage = ExternalLogStore.GetUsage();
        RefreshExternalLogSection();

        // 每次重建，不复用旧元素 —— 文件可能整批被删掉，复用容易把过期行留在界面上
        ExternalLogUsageLines.Children.Clear();

        if (usage.FileCount == 0)
        {
            ExternalLogUsageTitle.Text = "占用明细：暂无文件";

            var hint = new TextBlock
            {
                Text = "还没有外部日志文件。任务跑起来、并且任务上配了「外部日志路径」之后，再回来看。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.65,
            };
            ExternalLogUsageLines.Children.Add(hint);
        }
        else
        {
            ExternalLogUsageTitle.Text = $"占用明细：共 {usage.FileCount} 份，合计 {usage.TotalText}";

            foreach (var file in usage.Files)
            {
                var line = new TextBlock
                {
                    Text = $"{file.SourceLabel} · {file.ChannelTag}  ·  {file.FileName}  ·  {file.SizeText}  ·  {file.LastWriteTime:yyyy-MM-dd HH:mm}",
                    FontSize = 12,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Opacity = 0.8,
                };
                ExternalLogUsageLines.Children.Add(line);
            }
        }

        ExternalLogUsagePanel.Visibility = Visibility.Visible;

        if (!announce)
        {
            return;
        }

        VM.AppendLog(usage.FileCount == 0
            ? "外部日志占用：暂无文件。"
            : $"外部日志占用：共 {usage.FileCount} 份，{usage.TotalText}。");

        SetExternalLogAction(usage.FileCount == 0
            ? $"已刷新：暂无外部日志文件 —— {DateTime.Now:HH:mm:ss}"
            : $"已刷新：共 {usage.FileCount} 份，{usage.TotalText} —— {DateTime.Now:HH:mm:ss}");
    }

    /// <summary>把一次操作的结果写到本页（设置页没有别的即时报错渠道）。</summary>
    private void SetExternalLogAction(string text)
    {
        ExternalLogActionText.Text = text;
        ExternalLogActionText.Visibility = Visibility.Visible;
    }

    private void OnOpenExternalLogFolder(object sender, RoutedEventArgs e)
    {
        // 打开"真正在写日志的那一处"，不是本进程的安装目录。
        // 目标账户会话里的日志是**代理**写的，落在 %ProgramData%\Yomori\agent\external_logs；
        // 以前这里打开 <程序目录>\external_logs，对绝大多数用户来说是个永远不长的空文件夹。
        var root = ExternalLogStore.PreferredRoot();
        VM.OpenDirectory(root.Path);
        SetExternalLogAction($"已请求打开：{root.Path}（{root.Label}）");
    }

    // ---------------- 关于：版本号 + 检查更新 ----------------

    /// <summary>
    /// 把「关于」卡片里的版本号刷成当前程序集版本。
    ///
    /// 版本号来自程序集信息版本（CI 用 <c>-p:Version=</c> 写进去，本机编译则是 csproj 的兜底值），
    /// 不是写死的字面量 —— 以前这里硬编码「开发基线：v2.0」，发布版也照抄，等于没版本号。
    /// 提交哈希只在本机自编译（<c>-dev.local</c>）时显示：正式发布里有版本号就够了。
    /// </summary>
    private void RefreshVersionText()
    {
        VersionText.Text = $"版本 {UpdateService.CurrentVersion}";

        var detail = $"运行环境：.NET {Environment.Version} · WinUI 3";
        if (UpdateService.IsLocalBuild && UpdateService.CurrentCommit.Length > 0)
        {
            detail += $" · 提交 {UpdateService.CurrentCommit}";
        }

        VersionDetailText.Text = detail;
    }

    /// <summary>
    /// 在更新渠道下拉项里找当前配置对应的下标（找不到就回到第一项 —— 正式版）。
    /// 与主题下拉同一套路：<c>SelectedIndex</c> 不依赖 <c>SelectedValue</c> 的绑定解析，最稳。
    /// </summary>
    private static int IndexOfUpdateChannel(string? channel)
    {
        var normalized = UpdateChannels.Normalize(channel);
        for (var i = 0; i < UpdateChannels.Items.Count; i++)
        {
            if (UpdateChannels.Items[i].Key == normalized)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>在「关闭按钮」下拉项里找当前配置对应的下标（找不到回到"每次询问"）。</summary>
    private static int IndexOfCloseAction(string? action)
    {
        var normalized = WindowConfig.NormalizeCloseAction(action);

        for (var i = 0; i < CloseDecision.Items.Count; i++)
        {
            if (CloseDecision.Items[i].Key == normalized)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// 改「关闭按钮」行为。只落盘、不弹对话框：
    /// 这里改的是下次关窗口时的行为，当下没有任何可见效果，弹窗只会打断人。
    /// </summary>
    private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CloseActionBox.SelectedItem is not KeyValuePair<string, string> item)
        {
            return;
        }

        if (VM.Config.Window.CloseAction == item.Key)
        {
            return;
        }

        VM.SaveCloseAction(item.Key);
    }

    /// <summary>切更新渠道：落盘 + 收起上一次的结果（那份结果是按旧渠道查的，留着会误导）。</summary>
    private void OnUpdateChannelChanged(object sender, SelectionChangedEventArgs e)    {
        if (_syncing || UpdateChannelBox.SelectedItem is not KeyValuePair<string, string> item)
        {
            return;
        }

        var channel = item.Key;

        if (VM.Config.UpdateChannel == channel)
        {
            return;
        }

        VM.Config.UpdateChannel = channel;
        VM.SaveConfig();

        HideUpdateResult();
        VM.AppendLog($"检查更新渠道已切换为「{UpdateChannels.Label(channel)}」。");
    }

    /// <summary>
    /// 在一组 (Key, Value) 选项里找 Key 对应的下标；找不到回落到 0。
    /// 用 SelectedIndex 而不是 SelectedValue —— 后者在列表还没填好的时候赋值会被丢弃
    /// （本页两次踩过：渲染后端下拉框、通道页的会话处理下拉框）。
    /// </summary>
    private static int IndexOfItem(IReadOnlyList<KeyValuePair<string, string>> items, string key)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Key == key)
            {
                return i;
            }
        }

        return 0;
    }

    // ---------------- 启动检查更新 / 下载代理 ----------------

    private void OnUpdateCheckModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || UpdateCheckModeBox.SelectedItem is not KeyValuePair<string, string> item)
        {
            return;
        }

        var mode = UpdateCheckModes.Normalize(item.Key);
        if (VM.Config.UpdateCheckMode == mode)
        {
            return;
        }

        VM.Config.UpdateCheckMode = mode;
        VM.SaveConfig();
        VM.AppendLog($"启动时检查更新已设为「{UpdateCheckModes.Label(mode)}」。");
    }

    private void OnUpdateProxyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || UpdateProxyModeBox.SelectedItem is not KeyValuePair<string, string> item)
        {
            return;
        }

        var mode = UpdateProxyModes.Normalize(item.Key);
        if (VM.Config.UpdateProxyMode != mode)
        {
            VM.Config.UpdateProxyMode = mode;
            VM.SaveConfig();
            VM.AppendLog($"更新下载代理已切换为「{UpdateProxyModes.Label(mode)}」。");

            // 换模式等于换出口：缓存里按旧模式建的 client 不能再复用
            UpdateService.InvalidateClients();
        }

        // 只有"自定义"才需要地址与说明；切走时保留已填内容，不主动清空
        ApplyProxyMode(mode, DescribeStoredProxy(VM.Config.UpdateProxyUrl));
    }

    /// <summary>「自定义代理」才显示地址框与它下面那行说明。状态文案为空时只显示固定写法提示。</summary>
    private void ApplyProxyMode(string mode, string status = "")
    {
        var custom = UpdateProxyModes.Normalize(mode) == UpdateProxyModes.Custom;
        UpdateProxyUrlBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        UpdateProxyUrlHint.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

        if (custom)
        {
            SetProxyHint(status);
        }
    }

    /// <summary>固定的写法提示（教怎么填认证）+ 一句当前状态。</summary>
    private void SetProxyHint(string status)
    {
        const string usage =
            "留空表示直连（不使用代理）。需要认证时把「用户名:密码@」写在主机前面，例如 " +
            "http://user:pass@127.0.0.1:7890；密码在保存时存进 Windows 凭据管理器，不写进 config.json，" +
            "所以框里回显的只有用户名。";

        UpdateProxyUrlHint.Text = status.Length == 0 ? usage : usage + "\n" + status;
    }

    /// <summary>页面打开时说明"现在存的是什么"（已配认证 / 地址可疑 / 没配）。</summary>
    private static string DescribeStoredProxy(string storedUrl)
    {
        var text = (storedUrl ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (!UpdateProxyModes.TryParse(text, out var address))
        {
            return "现在存的地址解析不出来，检查更新时会退回系统代理 —— 请重新填写。";
        }

        return address.HasUser
            ? $"当前认证：用户 {address.User}（密码存于 Windows 凭据管理器）。"
            : string.Empty;
    }

    private void OnUpdateProxyUrlLostFocus(object sender, RoutedEventArgs e)
    {
        var typed = (UpdateProxyUrlBox.Text ?? string.Empty).Trim();
        var commit = CommitProxyAddress(typed);
        var changed = VM.Config.UpdateProxyUrl != commit.Stored;

        if (changed)
        {
            VM.Config.UpdateProxyUrl = commit.Stored;
            VM.SaveConfig();
        }

        // 只改密码时地址一字不变，缓存键也就没变 —— 必须显式让旧的 client 作废，
        // 否则表现是"密码改了还是 407"（见 UpdateService.InvalidateClients）。
        // 反过来，光标进出但什么都没改时不能动缓存，那等于每次失焦都新建一个连接池。
        if (changed || commit.SecretTouched)
        {
            UpdateService.InvalidateClients();
            VM.AppendLog("更新下载代理：" + commit.Status);
        }

        // 回显落盘后的形状（密码不在里面），免得用户以为没存住又反复重填
        if (typed != commit.Stored)
        {
            UpdateProxyUrlBox.Text = commit.Stored;
        }

        SetProxyHint(commit.Status);
    }

    /// <summary>
    /// 把输入框里的内容落成"配置里该存的样子"：认证拆开，密码交给凭据管理器。
    /// </summary>
    private static (string Stored, string Status, bool SecretTouched) CommitProxyAddress(string typed)
    {
        if (typed.Length == 0)
        {
            return (string.Empty, "地址已清空 —— 自定义代理不再使用，请求直连。", ClearSecretIfAny());
        }

        if (!UpdateProxyModes.TryParse(typed, out var address))
        {
            // 解析不出来就原样存着，让用户自己改回来（静默清掉他填的东西更糟）。
            // 状态里不回显地址本身 —— 未解析的串里可能带着密码。
            return (typed, "地址解析不出来：这次请求会退回系统代理，请检查协议与端口。", false);
        }

        if (!address.HasUser)
        {
            return (address.StoredText, $"已保存代理 {address.Authority}（不使用认证）。", ClearSecretIfAny());
        }

        if (address.Password.Length > 0 && !UpdateProxySecretStore.Save(address.User, address.Password))
        {
            return (address.StoredText,
                $"代理地址已保存，但密码写不进 Windows 凭据管理器 —— 认证会失败（用户 {address.User}）。",
                false);
        }

        var tail = address.Password.Length > 0
            ? "密码已存进 Windows 凭据管理器"
            : "密码沿用上次保存的那份";

        return (address.StoredText,
            $"已保存代理 {address.Authority}（用户 {address.User}，{tail}）。",
            address.Password.Length > 0);
    }

    /// <summary>
    /// 清掉凭据管理器里那份代理密码，返回"原来确实有"。
    /// 为什么要判断：光标进出输入框每次都会走一遍落盘逻辑，如果无脑返回 true，
    /// 每次失焦都会作废 HttpClient 缓存并往运行日志写一行 —— 纯噪声。
    /// </summary>
    private static bool ClearSecretIfAny()
    {
        if (UpdateProxySecretStore.Read().Length == 0)
        {
            return false;
        }

        UpdateProxySecretStore.Clear();
        return true;
    }

    // 下载节点不再在设置页配置：改为解锁流程里的测速选节点弹窗（RdpNodePickerDialog）。

    /// <summary>
    /// 查一次更新。查询是网络 IO，所以丢到线程池；结果用 <see cref="RunOnUi"/> 搬回 UI 线程再画 ——
    /// 控件只能在 UI 线程上碰，而 await 之后的续体落在哪个线程**不能靠假设**
    /// （工程里已经栽过一次：Windows App SDK 的选择器 await 之后就不在 UI 线程了）。
    /// <see cref="UpdateService.CheckAsync"/> 自己不抛异常（网络/限流都变成一条结果），catch 只是兜底。
    /// </summary>
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        var channel = UpdateChannels.Normalize(VM.Config.UpdateChannel);

        // 先在本线程（UI 线程）拿到派发器：await 之后就未必还在这条线程上了
        var queue = DispatcherQueue;

        SetUpdateBusy(true);
        HideUpdateResult();

        try
        {
            // 带上网代理设置：用户在这里配的代理同样作用于手动检查（否则"自定义代理"
            // 只在启动自动检查时生效，手动点一下反而连不上，像是代理没配好）。
            var network = UpdateNetworkSettings.FromConfig(VM.Config);
            var result = await Task.Run(() => UpdateService.CheckAsync(channel, network));
            RunOnUi(queue, () =>
            {
                SetUpdateBusy(false);
                ShowUpdateResult(result);
            });
        }
        catch (Exception ex)
        {
            // 兜底：真出了意外也要把按钮恢复可点（这是用户唯一的重试入口），并如实报出来
            var detail = $"{ex.GetType().Name}: {ex.Message}";
            RunOnUi(queue, () =>
            {
                SetUpdateBusy(false);
                _latestRelease = null;
                UpdateResultText.Text = $"检查更新时出错：{detail}";
                UpdateResultBar.Title = "检查更新失败";
                UpdateResultBar.Severity = InfoBarSeverity.Error;
                UpdateResultBar.IsOpen = true;
            });
            VM.AppendLog($"[更新检查] 异常：{detail}");
        }
    }

    /// <summary>检查进行中：按钮禁用 + 转圈，避免重复点。</summary>
    private void SetUpdateBusy(bool busy)
    {
        CheckUpdateButton.IsEnabled = !busy;
        UpdateProgress.IsActive = busy;
        UpdateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 已经在 UI 线程就直接执行，否则投递过去。
    /// 为什么不用「await 之后自然回到 UI 线程」：那个假设在 WinUI 3 里不成立
    /// （见 <see cref="OnCheckUpdate"/> 的注释）。
    /// </summary>
    private static void RunOnUi(Microsoft.UI.Dispatching.DispatcherQueue? queue, Action action)
    {
        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }

    /// <summary>把检查结果画进 InfoBar，并把「前往下载」指向这一份发布。</summary>
    private void ShowUpdateResult(UpdateCheckResult result)
    {
        _latestRelease = result.Latest;
        VM.AppendLog(result.LogLine);

        // 诊断行：不管结论是什么都记下「发布说明抓没抓到、摘要出几行」。
        // 「通知里没有更新内容」这种反馈，只看着上面那行结论分不清是没拿到 body
        // 还是摘要规则没命中 —— 多这一行就能一眼判定（2026-10-29 为此查过一轮）。
        if (result.Latest is { } probed)
        {
            VM.AppendLog($"[更新检查] 发布说明 {probed.Body.Length} 字符，摘要 {UpdateNotes.Summarize(probed.Body).Count} 行。");
        }

        UpdateResultText.Text = BuildUpdateResultText(result);
        UpdateResultBar.Severity = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => InfoBarSeverity.Success,
            UpdateCheckStatus.UpToDate => InfoBarSeverity.Success,
            UpdateCheckStatus.Failed => InfoBarSeverity.Error,
            UpdateCheckStatus.LocalNewer => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Informational,
        };
        UpdateResultBar.Title = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => "有新版本",
            UpdateCheckStatus.UpToDate => "已是最新",
            UpdateCheckStatus.LocalBuild => "本机自编译版",
            UpdateCheckStatus.LocalNewer => "本地版本更新",
            UpdateCheckStatus.NoRelease => "该渠道暂无发布",
            _ => "检查更新失败",
        };

        // 「前往下载」只在这份发布真有下载页时才露出来
        UpdateDownloadButton.Visibility = result.Latest is { } latest && latest.PageUrl.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateResultBar.IsOpen = true;

        // 手动检查命中新版本时，铃铛里也要有和启动检查**同一套**通知：
        // InfoBar 一离开设置页就没了，而「有新版本可装」这件事得能一直看到、能直接点开下。
        // 共用 VM.NotifyUpdateAvailable 而不是在这里另写一份文案，两边才不会慢慢走散。
        if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Latest is { } found)
        {
            VM.NotifyUpdateAvailable(found);
            VM.AppendLog($"[更新检查] 已在通知中心挂上「发现新版本 {found.Version}」。");
        }
    }

    /// <summary>
    /// InfoBar 里那段话：结论 + 更新内容要点。
    ///
    /// <para>原来只有一句「发现新版本 X —— 当前是 Y。发布日期：…」，用户点完「检查更新」
    /// 仍然不知道这次改了什么，还得自己开浏览器去翻发布页。发布说明本来就在手上
    /// （<see cref="GitHubRelease.Body"/>），这里和铃铛里那条通知共用
    /// <see cref="UpdateNotes"/> 的同一套摘要，两处才不会各说各的。</para>
    /// </summary>
    private static string BuildUpdateResultText(UpdateCheckResult result)
    {
        if (result is not { Status: UpdateCheckStatus.UpdateAvailable, Latest: { } release })
        {
            return result.Message;
        }

        var notes = UpdateNotes.Summarize(release.Body);
        if (notes.Count == 0)
        {
            return result.Message;
        }

        var text = new System.Text.StringBuilder(result.Message);
        text.Append("\n更新内容：");
        foreach (var note in notes)
        {
            text.Append("\n· ").Append(note);
        }

        return text.ToString();
    }

    private void HideUpdateResult()
    {
        _latestRelease = null;
        UpdateResultBar.IsOpen = false;
        UpdateDownloadButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 打开这一份发布的页面。
    ///
    /// ⚠️ 只开浏览器，**不下载、不安装、不替换任何文件** —— 程序安装在 Program Files、
    /// 且带 requireAdministrator，"自动更新"要么得写提权代码路径，要么会覆盖正在运行的本体。
    /// 那是另一件事，不在这一步里做。
    /// </summary>
    private void OnOpenUpdatePage(object sender, RoutedEventArgs e)
    {
        if (_latestRelease is not { } release)
        {
            return;
        }

        // 优先给安装版直链；没有就给发布页（里面两个资产都在）
        var target = release.SetupUrl ?? release.PageUrl;
        if (UpdateService.OpenUrl(target))
        {
            VM.AppendLog($"已在浏览器打开下载页：{target}");
            return;
        }

        VM.AppendLog($"打开下载页失败，请手动访问：{target}");
        _ = DialogHelper.ShowMessageAsync("打不开浏览器", $"请手动访问：{Environment.NewLine}{target}");
    }

    private async void OnSponsor(object sender, RoutedEventArgs e)
    {
        await DialogHelper.ShowMessageAsync(
            "赞赏支持",
            "感谢喜欢Yomori～\n\n如果它确实帮你省下了时间，可以在 Bilibili 主页找到支持入口，或者到 GitHub 点个 Star、提个 Issue。\n\n每一份反馈都会变成下一个版本的动力。");
    }

    /// <summary>
    /// 列出随包分发的第三方组件与各自许可证。
    ///
    /// 这不是"顺手加的致谢"：包里带着 FreeRDP / WinPR / OpenSSL 的 DLL，三者都是
    /// Apache-2.0，该许可证 §4 要求分发时附上许可证副本 —— 用户得能在程序里查到用了什么。
    /// 完整原文在发布包的 THIRD-PARTY-NOTICES.txt（由 tools/gen_third_party_notices.py 生成）。
    ///
    /// 必须走 DialogHelper 的全局闸门：同一时刻只允许一个 ContentDialog，直接 ShowAsync
    /// 会抛 COMException，而这是 async void —— 异常没人接就是掀进程。
    /// </summary>
    private async void OnOpenThirdPartyNotices(object sender, RoutedEventArgs e)
    {
        await DialogHelper.ShowAsync(new ThirdPartyNoticesDialog());
    }

    // ---------------- 远程桌面解锁：只读检测（计划书 M1） ----------------

    /// <summary>
    /// 首次进入设置页时的统一入口：刷新 GitHub 令牌状态 + 自动跑一次 RDP 只读检测。
    /// 用户点「重新检测」不经过这里，走 <see cref="OnRdpRedetect"/>（同一条检测路径）。
    /// </summary>
    /// <remarks>
    /// 为什么不用构造函数：构造函数里控件树还没挂到窗口上，检测结果返回时若页面尚未 Loaded，
    /// 写入的内容会被后续的布局重建覆盖掉。为什么只认第一次：改主题会让根元素的
    /// RequestedTheme 变化、内容树重建，Loaded 会再来一次 —— 那时重跑检测纯属浪费。
    /// <para>令牌区 2026-10-26 从「远程桌面解锁」卡挪进了「开发者」卡，但状态刷新仍然挂在
    /// 这个页面级入口上 —— 它和 RDP 检测本来就没有因果关系。</para>
    /// </remarks>
    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_rdpAutoDetectDone)
        {
            // 改主题会让根元素 RequestedTheme 变化、内容树重建，Loaded 再来一次。
            // 这时把当前分类补回去，否则用户正在看的分类会莫名跳回「常规」。
            ShowSection(_currentSection, resetScroll: false);
            return;
        }

        _rdpAutoDetectDone = true;

        // 令牌状态在构造时读不出来（PasswordBox 还没进树），放在首次加载
        RunOnUi(DispatcherQueue, RefreshGitHubTokenState);
        await RefreshRdpSectionAsync(announce: false);
    }

    // ---------------- 执行与通知 / 通道预检（2026-11-12 从通道管理页迁入） ----------------

    /// <summary>
    /// 把渲染后端下拉框拉回配置里的当前值。x:Bind 求值顺序是 SelectedValue 先、
    /// ItemsSource 后，Items 还空着时那次赋值会被丢弃；构造函数和 Loaded 各补一次。
    /// 空配置一律归一成 auto（D3D11），所以不会出现"没有选中项"的状态。
    /// </summary>
    private void SyncRenderBackendSelection()
    {
        var key = RenderBackends.Normalize(VM.RdpSettings.RenderBackend);
        if (!string.Equals(RenderBackendCombo.SelectedValue as string, key, StringComparison.Ordinal))
        {
            RenderBackendCombo.SelectedValue = key;
        }
    }

    /// <summary>解码方式下拉框与渲染后端一样受 x:Bind 求值顺序影响，ctor 和 Loaded 都补一次。</summary>
    private void SyncDecodeBackendSelection()
    {
        var key = DecodeBackends.Normalize(VM.RdpSettings.DecodeBackend);
        if (!string.Equals(DecodeBackendCombo.SelectedValue as string, key, StringComparison.Ordinal))
        {
            DecodeBackendCombo.SelectedValue = key;
        }
    }

    /// <summary>把预检下拉的选中项拉回 VM 当前选中的通道（同一份全局状态，两页共用）。</summary>
    private void SyncReadinessChannelSelection()
    {
        _syncingReadinessChannel = true;
        try
        {
            if (!ReferenceEquals(ReadinessChannelCombo.SelectedItem, VM.SelectedChannel))
            {
                ReadinessChannelCombo.SelectedItem = VM.SelectedChannel;
            }
        }
        finally
        {
            _syncingReadinessChannel = false;
        }
    }

    /// <summary>用户在预检卡里换通道：回写 VM，setter 会顺带刷新该通道的预检结论。</summary>
    private void OnReadinessChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingReadinessChannel)
        {
            return;
        }

        if (ReadinessChannelCombo.SelectedItem is RdpChannel channel
            && !ReferenceEquals(channel, VM.SelectedChannel))
        {
            VM.SelectedChannel = channel;
        }
    }

    /// <summary>预检结论变了就换 InfoBar 颜色（就绪=绿，否则=黄）。</summary>
    private void SyncReadinessChrome()
    {
        ChannelReadinessBar.Severity = VM.ChannelReadinessText.Contains("就绪")
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
    }

    /// <summary>
    /// 页面加载时把迁入的两块界面摆好。与 RDP 检测的 OnPageLoaded 分开 ——
    /// 那个有 _rdpAutoDetectDone 早退，主题切换导致 Loaded 再来一次时不该跳过这里的同步。
    /// </summary>
    private void OnReadinessSectionLoaded(object sender, RoutedEventArgs e)
    {
        SyncRenderBackendSelection();
        SyncDecodeBackendSelection();
        SyncReadinessChannelSelection();

        if (!_readinessRefreshed)
        {
            _readinessRefreshed = true;
            // 启动后直接进设置页时，SelectedChannel 的预检文本还没算过（Initialize 里
            // ReloadChannels(refreshReadiness:false) 故意不跑），这里补一次。
            VM.RefreshChannelReadiness();
        }

        SyncReadinessChrome();
    }

    /// <summary>
    /// VM 里的预检 / 选中通道变了，同步本页的显示。
    /// 统一调度到 UI 线程：这些属性也可能由后台任务写，直接碰控件会踩跨线程 COM 异常。
    /// Unloaded 时解绑，避免单例 VM 订阅泄漏。
    /// </summary>
    private void OnReadinessPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ChannelReadinessText):
            case nameof(MainViewModel.RdpStatusText):
                RunOnUi(DispatcherQueue, () =>
                {
                    if (!_unloaded)
                    {
                        SyncReadinessChrome();
                    }
                });
                break;
            case nameof(MainViewModel.SelectedChannel):
            case nameof(MainViewModel.Channels):
                RunOnUi(DispatcherQueue, () =>
                {
                    if (!_unloaded)
                    {
                        SyncReadinessChannelSelection();
                    }
                });
                break;
        }
    }

    private void OnRefreshChannelReadiness(object sender, RoutedEventArgs e)
    {
        VM.RefreshChannelReadiness();
        SyncReadinessChrome();
    }

    private void OnDeployChannelAgent(object sender, RoutedEventArgs e)
    {
        VM.DeployChannelAgentCommand.Execute(null);
        SyncReadinessChrome();
    }

    private void OnRemoveAgent(object sender, RoutedEventArgs e)
    {
        VM.RemoveAgentCommand.Execute(null);
        SyncReadinessChrome();
    }

    /// <summary>把「令牌配没配」写到界面上。只显示掩码，绝不回显整串。</summary>
    private void RefreshGitHubTokenState()
    {
        GitHubTokenState.Text = GitHubTokenStore.DescribeState();
    }

    /// <summary>
    /// 保存令牌并顺手验证一次。
    /// </summary>
    /// <remarks>
    /// 顺序是「先落盘，再验证」：断网时用户仍然能把令牌存好，界面如实说「暂时无法验证」。
    /// 唯一会回滚的情况是 <see cref="GitHubTokenProbe.Invalid"/> —— 401 或配额仍是 60/小时，
    /// 说明这串东西根本不被认，留着只会让之后每次请求都失败。
    /// <para>这里不再等 RDP 操作（解锁 / 还原）跑完：令牌与解锁流程无关，原来那道
    /// <c>_rdpBusy</c> 门禁只是当年放在同一张卡里的副产物。</para>
    /// </remarks>
    private async void OnSaveGitHubToken(object sender, RoutedEventArgs e)
    {
        var (ok, error) = GitHubTokenStore.Save(GitHubTokenBox.Password);

        if (!ok)
        {
            PostUi(() => GitHubTokenState.Text = "没有保存：" + error);
            return;
        }

        // 明文不留在控件里
        PostUi(() =>
        {
            GitHubTokenBox.Password = "";
            GitHubTokenState.Text = "已保存，正在向 GitHub 验证…";
        });

        var network = UpdateNetworkSettings.FromConfig(VM.Config);
        var (state, message) = await GitHubTokenProbeRunner.ProbeAsync(network);

        if (state == GitHubTokenProbe.Invalid)
        {
            GitHubTokenStore.Clear();
            PostUi(() => GitHubTokenState.Text = "验证失败，已清除：" + message);
            return;
        }

        PostUi(() => GitHubTokenState.Text =
            message + (state == GitHubTokenProbe.Valid ? "" : "（令牌已保存）"));
    }

    /// <summary>清除令牌。</summary>
    private void OnClearGitHubToken(object sender, RoutedEventArgs e)
    {
        GitHubTokenStore.Clear();
        PostUi(() =>
        {
            GitHubTokenBox.Password = "";
            GitHubTokenState.Text = "已清除。" + GitHubTokenStore.DescribeState();
        });
    }

    private async void OnRdpRedetect(object sender, RoutedEventArgs e)
    {
        await RefreshRdpSectionAsync(announce: true);
    }

    /// <summary>
    /// 跑一次只读检测并刷新本页。
    /// </summary>
    /// <remarks>
    /// ⚠️ 检测读注册表、查 SCM、枚举会话，都是可能阻塞几十毫秒的 Win32 调用 ——
    /// 一律丢到线程池，结果用 <see cref="RunOnUi"/> 搬回来再碰控件。
    /// 「await 之后自然回到 UI 线程」在这个工程里不成立（见 OnCheckUpdate 的注释），
    /// 而这里改的是 TextBlock.Text / Children，跨线程就是 RPC_E_WRONG_THREAD + 掀进程。
    /// <para>检测器自己不抛异常，catch 只是兜底；兜底时也要把按钮恢复可点，那是用户唯一的重试入口。</para>
    /// </remarks>
    private async Task RefreshRdpSectionAsync(bool announce)
    {
        if (_rdpDetecting || _rdpBusy)
        {
            return;
        }

        _rdpDetecting = true;
        var queue = DispatcherQueue;

        RdpProbeSummaryText.Text = "正在检测…";
        SetRdpBusy(true);

        try
        {
            var snapshot = await Task.Run(RdpReadOnlyDetector.Detect);
            RunOnUi(queue, () =>
            {
                if (_unloaded)
                {
                    return;
                }

                _rdpSnapshot = snapshot;

                // 先放开 detecting 再刷按钮：UpdateRdpButtonStates 会读这个标志，
                // 顺序反了的话按钮会停在禁用态（这一处踩过：检测完成后「重新检测」点不动）
                _rdpDetecting = false;
                RenderRdpSnapshot(snapshot);
                SetRdpBusy(false);

                if (announce)
                {
                    var line = RdpUnlockDiagnostics.FormatLogLine(snapshot);
                    VM.AppendLog(line);
                    SetRdpAction(line);
                }
            });
        }
        catch (Exception ex)
        {
            var detail = $"{ex.GetType().Name}: {ex.Message}";
            RunOnUi(queue, () =>
            {
                if (_unloaded)
                {
                    return;
                }

                _rdpDetecting = false;
                SetRdpBusy(false);
                RdpProbeSummaryText.Text = $"检测失败：{detail}";
                VM.AppendLog($"[RDP 解锁] 检测异常：{detail}");
                SetRdpAction($"检测失败：{detail}");
            });
        }
        finally
        {
            _rdpDetecting = false;
        }
    }

    private void SetRdpBusy(bool busy)
    {
        RdpProbeProgress.IsActive = busy;
        RdpProbeProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        UpdateRdpButtonStates();
    }

    /// <summary>
    /// 三个修改按钮 + 两个只读入口的可用性。
    /// </summary>
    /// <remarks>
    /// 「还原注册表」没有备份时必须禁用并说明原因（计划书 5.3 第 2 条：无有效备份时
    /// 禁用按钮，不猜 Windows 默认值）。所以这里每次都要问一次账本 ——
    /// 磁盘上的一目录扫描，比在内存里维护一份可能过期的"有没有备份"可靠。
    /// </remarks>
    private void UpdateRdpButtonStates()
    {
        var busy = _rdpBusy || _rdpDetecting;

        RdpRedetectButton.IsEnabled = !busy;
        RdpUnlockButton.IsEnabled = !busy;

        // 卸载只在"确实装着"的时候可点：没装的时候点它是无意义的（服务也会直接返回"无需卸载"）
        RdpUninstallButton.IsEnabled = !busy && _rdpSnapshot?.WrapperState == RdpWrapperState.Installed;

        var hasBaseline = false;
        var hasAnyTransaction = false;
        try
        {
            hasBaseline = RdpTransactionStore.FindFirstBaseline() is not null;
            hasAnyTransaction = RdpTransactionStore.List().Count > 0;
        }
        catch
        {
            // 账本目录不可读：两个按钮都保持禁用
        }

        RdpRestoreRegistryButton.IsEnabled = !busy && hasBaseline;
        RdpBackupButton.IsEnabled = !busy && hasAnyTransaction;
        RdpDiagnoseButton.IsEnabled = !busy && _rdpSnapshot is not null;
        RdpPickInstallerButton.IsEnabled = !busy;
        RdpPickIniButton.IsEnabled = !busy;

        // 令牌按钮不跟着 RDP 忙闲走：令牌区已挪进「开发者」卡，和解锁流程没有关系。
    }

    /// <summary>
    /// 「指定 INI」：粘贴社区在 rdpwrap issues 里针对当前系统版本贴出的补丁段，
    /// 程序按段名合并写入本机 rdpwrap.ini。
    /// </summary>
    /// <remarks>
    /// <para>2026-11-07 从「选本地 .ini 文件」改成「粘贴配置文本」：社区适配新版本时给的是
    /// 两三段补丁配置，不是完整 ini。整份替换会抹掉 <c>[Main]</c>/<c>Updated=</c>/<c>[PatchCodes]</c>，
    /// 所以服务层按段名 upsert（见 <see cref="RdpIniMerge"/>）。</para>
    /// <para>对话框走 <see cref="DialogHelper.ShowAsync"/> 单入口；写入复用
    /// <see cref="RunRdpOperationAsync"/> 的后台线程 + 结果条框架，UI 线程不碰文件与服务停启。</para>
    /// </remarks>
    private async void OnRdpPickIni(object sender, RoutedEventArgs e)
    {
        if (_rdpBusy || _rdpDetecting)
        {
            return;
        }

        const string issuesUrl = "https://github.com/stascorp/rdpwrap/issues";
        var version = _rdpSnapshot?.TermsrvVersion ?? "";

        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 240,
            MaxHeight = 320,
            IsSpellCheckEnabled = false,
            PlaceholderText = "[10.0.26100.9818]" + Environment.NewLine
                + "SingleUserPatch.x64=1" + Environment.NewLine
                + "SingleUserOffset.x64=AC5DB" + Environment.NewLine
                + "..." + Environment.NewLine
                + "[10.0.26100.9818-SLInit]" + Environment.NewLine
                + "bInitialized.x64=135350",
        };

        var latest = new Button { Content = "获取最新", HorizontalAlignment = HorizontalAlignment.Left };
        latest.Click += (_, _) =>
        {
            if (UpdateService.OpenUrl(issuesUrl))
            {
                SetRdpAction("已在浏览器打开 rdpwrap issues；找到当前系统版本的补丁段后复制粘贴到下面。");
            }
            else
            {
                SetRdpAction($"打不开浏览器，请手动访问：{issuesUrl}");
            }
        };

        var scroll = new ScrollViewer
        {
            Content = box,
            MaxWidth = 640,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(latest);
        panel.Children.Add(new TextBlock
        {
            Text = "社区还没适配本机系统版本时，在这里粘贴针对该版本的补丁段；程序按段名合并写入 rdpwrap.ini，不会覆盖其它段。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });
        panel.Children.Add(new TextBlock
        {
            Text = version.Length > 0
                ? $"本机 termsrv.dll 版本：{version}（配置里必须有 [{version}] 段）"
                : "本机 termsrv.dll 版本：读不到（此时只写入、不重启服务）",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });
        panel.Children.Add(scroll);

        var dialog = new ContentDialog
        {
            Title = "指定 INI 配置",
            Content = panel,
            PrimaryButtonText = "写入 INI",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };

        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(box.Text);

        if (await DialogHelper.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var text = box.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _rdpManualIniText = text;

        // 只有「段名与本机版本一致」时服务层才重启 TermService；重启会断开当前远控会话。
        await RunRdpOperationAsync("写入 INI 配置",
            (progress, token) => RdpUnlockService.ApplyManualIniAsync(text, progress, token));
    }

    /// <summary>
    /// 指定本地安装包：GitHub 附件在国内网络常被拦成网页，这时让用户自己下一个交给程序。
    /// </summary>
    /// <remarks>
    /// 选完不立刻安装 —— 装系统组件是重动作，要用户在确认窗口里看到「会用什么、从哪来」。
    /// 所以这里只记路径并把状态写到操作行，下次点「解锁 RDP」才用它。
    /// </remarks>
    private async void OnRdpPickInstaller(object sender, RoutedEventArgs e)
    {
        if (_rdpBusy || _rdpDetecting)
        {
            return;
        }

        var path = await DialogHelper.PickOpenFileAsync(("可执行文件", ".exe"));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var pe = RdpToolDownloader.InspectPe(path);
        if (!pe.IsPe)
        {
            // 用户选错文件是常事（选成网页另存的 .exe、选成压缩包），必须当场说清楚
            _rdpManualInstaller = "";
            SetRdpAction($"这个文件用不了：{Path.GetFileName(path)} {pe.ProblemText}");
            return;
        }

        _rdpManualInstaller = path;
        SetRdpAction($"已指定安装包：{Path.GetFileName(path)}（{new FileInfo(path).Length} 字节，架构 {pe.MachineName}）。"
                     + "下次点「解锁 RDP」将直接使用它，不再下载。");
    }

    /// <summary>
    /// 把快照画进本页：四行状态 + 一行汇总 + 一行补充信息。
    /// </summary>
    /// <remarks>
    /// 颜色与文字同时表达（计划书 3.1）：色块只起快速定位作用，真正可读的信息是
    /// 「已安装（通过）」这类文字。只给颜色的话，色觉障碍用户拿不到任何信息。
    /// </remarks>
    private void RenderRdpSnapshot(RdpUnlockSnapshot snapshot)
    {
        // 整体重建，不复用旧元素 —— 四项的等级每次都可能变，复用容易把上一轮的颜色留在界面上
        RdpProbeRows.Children.Clear();
        foreach (var item in snapshot.Items)
        {
            RdpProbeRows.Children.Add(BuildProbeRow(item));
        }

        RdpProbeSummaryText.Text = RdpUnlockDiagnostics.FormatSummaryLine(snapshot);

        var details = new List<string>
        {
            $"termsrv.dll {Dash(snapshot.TermsrvVersion)}",
            $"INI Updated {Dash(snapshot.IniUpdated)}",
            $"安装目录 {Dash(snapshot.InstallDirectory)}",
            snapshot.ListenerSessionId > 0 ? $"RDP-Tcp 会话 ID {snapshot.ListenerSessionId}" : "RDP-Tcp 会话 ID —",
            snapshot.Tcp3389Listening ? "3389 有监听" : "3389 无监听",
            "最近事务：尚无（解锁流程未接入）",
            "恢复状态：无备份",
        };

        if (!snapshot.IsElevated)
        {
            details.Add("本进程不是管理员：部分注册表值可能读不到，结果会标为未知");
        }

        RdpProbeDetailText.Text = string.Join("　·　", details);
    }

    private static FrameworkElement BuildProbeRow(RdpProbeItem item)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = LevelBrush(item.Level),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 7, 0, 0),
        };
        Grid.SetColumn(dot, 0);

        var stack = new StackPanel { Spacing = 2 };

        stack.Children.Add(new TextBlock
        {
            Text = $"{item.Title}　{item.StateText}（{RdpProbeText.LevelLabel(item.Level)}）",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        stack.Children.Add(new TextBlock
        {
            Text = item.ValueText,
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        Grid.SetColumn(stack, 1);
        row.Children.Add(dot);
        row.Children.Add(stack);
        return row;
    }

    /// <summary>
    /// 等级 → 色块。四色在两套主题下都要能看清，所以取饱和度较高的固定色，
    /// 不跟主题色走（主题资源里的 Accent 在深色主题下偏暗，做小圆点会糊成背景）。
    /// </summary>
    private static SolidColorBrush LevelBrush(RdpProbeLevel level) => level switch
    {
        RdpProbeLevel.Pass => new SolidColorBrush(Color.FromArgb(0xFF, 0x2F, 0xAE, 0x5B)),
        RdpProbeLevel.Warn => new SolidColorBrush(Color.FromArgb(0xFF, 0xC9, 0x9A, 0x00)),
        RdpProbeLevel.Fail => new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
        _ => new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)),
    };

    /// <summary>
    /// 诊断详情：只读文本框 + 复制。
    /// </summary>
    /// <remarks>
    /// 用 TextBox 而不是 TextBlock：需要 Ctrl+C 与拖动选择，而 ContentDialog 的
    /// Content 直接放字符串时用户没法选中。只读 + 无边框，视觉上仍是一段文字。
    /// </remarks>
    private async void OnRdpDiagnose(object sender, RoutedEventArgs e)
    {
        if (_rdpSnapshot is not { } snapshot)
        {
            await DialogHelper.ShowMessageAsync("还没有检测结果", "先点「重新检测」拿到一份结果，再看诊断。");
            return;
        }

        var text = _rdpLastReport.Length > 0
            ? _rdpLastReport + Environment.NewLine + Environment.NewLine
              + "--- 当前状态 ---" + Environment.NewLine
              + RdpUnlockDiagnostics.FormatDetail(snapshot)
            : RdpUnlockDiagnostics.FormatDetail(snapshot);

        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
        };

        var scroll = new ScrollViewer
        {
            Content = box,
            MaxHeight = 420,
            MaxWidth = 640,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            Title = "RDP 解锁诊断",
            Content = scroll,
            CloseButtonText = "关闭",
            PrimaryButtonText = "复制诊断",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await DialogHelper.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            CopyRdpDiagnostics(text);
        }
    }

    /// <summary>
    /// 复制诊断文本。
    /// </summary>
    /// <remarks>
    /// 整段经 <see cref="PostUi"/> 走界面线程：调用点在「诊断对话框关闭之后」，
    /// 那里可能已经在别的线程上 —— 而剪贴板与 <c>VM.AppendLog</c>（改 ObservableCollection）
    /// 都只能在界面线程上碰。
    /// </remarks>
    private void CopyRdpDiagnostics(string text) => PostUi(() =>
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            SetRdpAction($"诊断已复制到剪贴板（{text.Length} 字）—— {DateTime.Now:HH:mm:ss}");
            VM.AppendLog("[RDP 解锁] 诊断文本已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            // 剪贴板被别的进程独占时 SetContent 会抛 —— 这条路径是 async void 的上游，绝不能放出去
            SetRdpAction($"复制失败：{ex.GetType().Name}: {ex.Message}");
        }
    });

    /// <summary>
    /// 手动兜底方案：一键解锁反复失败时，按第三方教程自己装。
    /// 打不开就直说。教程只在用户点的时候才开浏览器（计划书 3.4）。
    /// </summary>
    private void OnRdpTutorial(object sender, RoutedEventArgs e)
    {
        const string url = "https://www.bettergi.com/tutorial/rdp.html";

        if (UpdateService.OpenUrl(url))
        {
            SetRdpAction("已在浏览器打开手动解锁方案（第三方教程）。");
            return;
        }

        SetRdpAction($"打不开浏览器，请手动访问：{url}");
        _ = DialogHelper.ShowMessageAsync("打不开浏览器", $"请手动访问：{Environment.NewLine}{url}");
    }

    private static string Dash(string text) => string.IsNullOrWhiteSpace(text) ? "—" : text;

    // ---------------- 远程桌面解锁：修改动作（M2–M4） ----------------

    /// <summary>
    /// 解锁：系统修改风险确认 → （缺组件时）下载前的杀软人工确认 → 选择下载节点 → 开始。
    /// </summary>
    /// <remarks>
    /// <para>三个对话框的**顺序**是计划书定的，不能合并：系统修改风险（3.2 第一层）、
    /// 下载前的人肉确认（3.2 第二层，只在需要下载时）、下载节点测速选择。</para>
    /// <para>最后一道是测速界面，按钮「确定并开始下载」—— 点它才真正开始下载。
    /// 两个前置确认的按钮都是「继续」，不再许诺「开始下载」。</para>
    /// </remarks>
    private async void OnRdpUnlock(object sender, RoutedEventArgs e)
    {
        if (_rdpBusy || _rdpDetecting)
        {
            return;
        }

        await EnsureRdpSnapshotAsync();

        // 对话框必须在界面线程上创建（见 RunOnUiAsync 的说明），而这里已经在 await 之后
        var queue = DispatcherQueue;
        var needDownload = _rdpSnapshot?.WrapperState != RdpWrapperState.Installed;

        if (!await RunOnUiAsync(queue, () => ConfirmUnlockRiskAsync(needDownload)))
        {
            return;
        }

        // ---- 2. 下载前的人肉确认（计划书 3.2）----
        var antivirusConfirmed = false;
        if (needDownload)
        {
            if (!await RunOnUiAsync(queue, ConfirmAntivirusAsync))
            {
                SetRdpAction("已取消：没有下载任何东西，也没有改动系统。");
                return;
            }

            antivirusConfirmed = true;
        }

        var network = UpdateNetworkSettings.FromConfig(VM.Config);
        var savedPrefix = VM.Config.RdpAccelerationPrefix;

        // ---- 3. 选择下载节点：两个确认弹窗都过完才弹 ----
        RdpAccelerationSettings acceleration;
        if (!needDownload && _rdpSnapshot?.IniState == RdpSupportState.FullySupported)
        {
            // 本轮既不需要下载组件、INI 也已覆盖当前版本：不打扰用户
            acceleration = RdpAccelerationSettings.Disabled;
        }
        else
        {
            // 连续弹窗留一拍：前一个 ContentDialog 关闭动画没走完就显示下一个，
            // WinUI 会「Only a single ContentDialog can be open at any time」把前一个顶掉。
            // 延迟放在调用方；后续对话框仍由 RunOnUiAsync 搬回界面线程，线程安全。
            // （绝不能把 Task.Delay 放进 DialogHelper.ShowAsync：那里 await 后不保证回界面线程。）
            await Task.Delay(150);

            var pick = await RunOnUiAsync(queue, async () =>
            {
                var picker = new RdpNodePickerDialog(network, savedPrefix);
                return await picker.ShowDialogAsync();
            });

            if (pick is null)
            {
                SetRdpAction("已取消：没有下载任何东西，也没有改动系统。");
                return;
            }

            // 配置写入必须回界面线程：SetProperty 会触发绑定的 PropertyChanged，
            // 跨线程改绑定会抛 0x8001010E 并掀掉进程。
            if (VM.Config.RdpAccelerationPrefix != pick.CustomPrefix)
            {
                RunOnUi(queue, () =>
                {
                    VM.Config.RdpAccelerationPrefix = pick.CustomPrefix;
                    VM.SaveConfig();
                });
                UpdateService.InvalidateClients();
            }

            acceleration = pick.Acceleration;
        }

        var request = new RdpUnlockRequest
        {
            AntivirusConfirmed = antivirusConfirmed,
            ManualInstallerPath = _rdpManualInstaller,
            ManualIniText = _rdpManualIniText,
            Acceleration = acceleration,
        };

        await RunRdpOperationAsync("解锁 RDP",
            (progress, token) => RdpUnlockService.UnlockAsync(request, network, progress, token));
    }

    /// <summary>还原注册表：默认用最早那份基线，用户也可以从「历史备份」里挑。</summary>
    private async void OnRdpRestoreRegistry(object sender, RoutedEventArgs e)
    {
        if (_rdpBusy || _rdpDetecting)
        {
            return;
        }

        RdpTransactionInfo? baseline;
        try
        {
            baseline = RdpTransactionStore.FindFirstBaseline();
        }
        catch (Exception ex)
        {
            SetRdpAction($"读不到备份账本：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (baseline is null)
        {
            SetRdpAction("没有可用的注册表备份，已跳过。");
            return;
        }

        var confirmed = await DialogHelper.ShowMessageAsync(
            "还原注册表",
            $"将把本功能改过的注册表值恢复成解锁前的状态。{Environment.NewLine}{Environment.NewLine}"
            + $"使用备份：{baseline.SummaryText}{Environment.NewLine}"
            + "变更范围：仅 fDenyTSConnections / fSingleSessionPerUser 等白名单键值。"
            + Environment.NewLine + Environment.NewLine
            + "注意：这**不会**卸载解锁组件，也不会删除 Windows 用户、Yomori 通道或凭据。"
            + "恢复可能需要重启远程桌面服务，正在连接的远程会话会断开。",
            closeText: "取消",
            primaryText: "确认还原");

        if (confirmed != ContentDialogResult.Primary)
        {
            return;
        }

        var id = baseline.Id;
        await RunRdpOperationAsync("还原注册表",
            (progress, token) => RdpUnlockService.RestoreRegistryAsync(id, progress, token));
    }

    /// <summary>
    /// 卸载：确认后果 →（缺卸载器时）杀软人工确认 → 调官方卸载器 → 回读 ServiceDll。
    /// </summary>
    /// <remarks>
    /// 与「还原注册表」的分界写在确认框里：卸载只移除组件，不还原注册表、不删账户与凭据。
    /// 两个动作都会断会话，所以也都不能静默执行。
    /// </remarks>
    private async void OnRdpUninstall(object sender, RoutedEventArgs e)
    {
        if (_rdpBusy || _rdpDetecting)
        {
            return;
        }

        await EnsureRdpSnapshotAsync();

        if (_rdpSnapshot?.WrapperState != RdpWrapperState.Installed)
        {
            SetRdpAction("本机没有安装解锁组件，无需卸载。");
            return;
        }

        var hasLocalTool = RdpUninstallService.HasLocalUninstaller(out var toolDetail);

        var confirmed = await DialogHelper.ShowMessageAsync(
            "卸载 RDP 解锁",
            "将移除本机 RDP Wrapper 解锁组件，可能断开所有相关远程桌面会话。"
            + Environment.NewLine + Environment.NewLine
            + "不会删除：Windows 用户、密码、Yomori 通道配置、凭据、恢复备份。"
            + Environment.NewLine + Environment.NewLine
            + "家庭版卸载后可能不再提供远程桌面服务端；专业版的原生远程桌面不受影响。"
            + Environment.NewLine + Environment.NewLine
            + $"卸载器：{toolDetail}",
            closeText: "取消",
            primaryText: "确认卸载");

        if (confirmed != ContentDialogResult.Primary)
        {
            return;
        }

        var antivirusConfirmed = false;
        if (!hasLocalTool)
        {
            if (!await RunOnUiAsync(DispatcherQueue, ConfirmAntivirusAsync))
            {
                SetRdpAction("已取消：没有下载任何东西，也没有改动系统。");
                return;
            }

            antivirusConfirmed = true;
        }

        // 卸载不弹测速界面：沿用已保存的自定义节点，其余交给内置候选轮换兜底。
        // 同样走 network 里的代理设置。
        var request = new RdpUninstallRequest
        {
            AntivirusConfirmed = antivirusConfirmed,
            Acceleration = new RdpAccelerationSettings(true, VM.Config.RdpAccelerationPrefix),
        };
        var network = UpdateNetworkSettings.FromConfig(VM.Config);

        await RunRdpOperationAsync("卸载 RDP 解锁",
            (progress, token) => RdpUninstallService.UninstallAsync(request, network, progress, token));
    }

    /// <summary>历史备份：列出所有事务，允许挑一份还原。</summary>
    private async void OnRdpBackups(object sender, RoutedEventArgs e)
    {
        if (_rdpBusy || _rdpDetecting)
        {
            return;
        }

        var list = new List<RdpTransactionInfo>();
        try
        {
            foreach (var item in RdpTransactionStore.List())
            {
                list.Add(item);
            }
        }
        catch (Exception ex)
        {
            SetRdpAction($"读不到备份账本：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (list.Count == 0)
        {
            SetRdpAction("还没有任何事务记录。");
            return;
        }

        var picker = new ComboBox
        {
            ItemsSource = list,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 460,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"备份目录：{RdpTransactionStore.RootDir}",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "「未完成」的事务是崩溃或强杀留下的半截记录；它本身也能还原，"
                   + "但删除前请先看诊断确认它做到哪一步。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(picker);

        var dialog = new ContentDialog
        {
            Title = "历史备份",
            Content = panel,
            PrimaryButtonText = "还原这一份",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await DialogHelper.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        if (picker.SelectedItem is not RdpTransactionInfo selected)
        {
            return;
        }

        var confirmed = await RunOnUiAsync(DispatcherQueue, () => DialogHelper.ShowMessageAsync(
            "还原注册表",
            $"将按 {selected.SummaryText} 恢复注册表。"
            + Environment.NewLine + Environment.NewLine
            + "这不会卸载解锁组件，也不会动 Windows 用户与通道凭据。",
            closeText: "取消",
            primaryText: "确认还原"));

        if (confirmed != ContentDialogResult.Primary)
        {
            return;
        }

        var id = selected.Id;
        await RunRdpOperationAsync("还原注册表",
            (progress, token) => RdpUnlockService.RestoreRegistryAsync(id, progress, token));
    }

    /// <summary>没有检测结果时先补一次，避免"按 Wrapper 状态分岔"的判断基于 null。</summary>
    private async Task EnsureRdpSnapshotAsync()
    {
        if (_rdpSnapshot is not null || _rdpDetecting)
        {
            return;
        }

        await RefreshRdpSectionAsync(announce: false);
    }

    private async Task<bool> ConfirmUnlockRiskAsync(bool needDownload)
    {
        var scope = new System.Text.StringBuilder();
        scope.AppendLine("这个操作会修改本机系统：");
        scope.AppendLine("1. " + (needDownload ? "下载并安装第三方 RDP Wrapper 解锁组件" : "复用本机已有的解锁组件（不重装）"));
        scope.AppendLine("2. 修改注册表：HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server 的 fDenyTSConnections 改为 0");
        scope.AppendLine("3. 修改注册表：fSingleSessionPerUser 改为 1（本机键；若策略键里已有该值也一并改为 1）");
        scope.AppendLine("   这一项让同一账户只保留一条会话 —— 不设它，每连一次就会多一条「已断开」的历史会话。");
        scope.AppendLine("4. 更新本机 rdpwrap.ini（需要联网；下载会走设置里配的更新代理）");
        scope.AppendLine("5. 重启 Remote Desktop Services（TermService）");
        scope.AppendLine();
        scope.AppendLine("风险：");
        scope.AppendLine("· 精简版系统上可能无效；");
        scope.AppendLine("· 重启服务会断开当前所有远程会话，正在跑的任务无法通过注册表备份恢复；");
        scope.AppendLine("· 卸载不保证能还原成安装前的全部状态。");
        scope.AppendLine();
        scope.AppendLine("备份位置：");
        scope.AppendLine("· " + RdpTransactionStore.RootDir + "\\<事务ID>\\");
        scope.AppendLine("  内含注册表快照、INI 备份与逐步日志。");

        var dialog = new ContentDialog
        {
            Title = needDownload ? "解锁 RDP：将下载组件并修改系统" : "解锁 RDP：将修改系统",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = scope.ToString(),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                },
                MaxHeight = 420,
            },
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await DialogHelper.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 下载前的人肉确认（计划书 3.2）。文案是计划书给定的，不要"优化"掉任何一句 ——
    /// 其中「不检测、不验证也不修改」「取消后不会开始下载」「请及时重新开启防护」都是承诺。
    /// </summary>
    private async Task<bool> ConfirmAntivirusAsync()
    {
        var check = new CheckBox
        {
            Content = "我已确认关闭所有杀毒软件",
            Margin = new Thickness(0, 8, 0, 0),
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "此操作将从 RDP Wrapper 官方 Releases 下载最新正式版。"
                   + "请确认已关闭所有杀毒软件（包括系统自带防护及第三方安全软件）后再点击「继续」。"
                   + "Yomori 不检测、不验证也不修改杀毒软件状态。取消后不会开始下载。"
                   + "操作完成、失败或取消后，请及时重新开启安全防护。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "下载源：" + RdpReleaseResolver.LatestApiUrl,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        panel.Children.Add(check);

        var dialog = new ContentDialog
        {
            Title = "下载前确认",
            Content = panel,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };

        check.Checked += (_, __) => dialog.IsPrimaryButtonEnabled = true;
        check.Unchecked += (_, __) => dialog.IsPrimaryButtonEnabled = false;

        return await DialogHelper.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 跑一次修改操作并渲染结果。
    /// </summary>
    /// <remarks>
    /// <para>⛔ 这里**不能**用 <see cref="Progress{T}"/>。它把回调交给构造时捕获的
    /// <see cref="System.Threading.SynchronizationContext"/>，而 WinUI 3 的界面线程上
    /// <c>SynchronizationContext.Current</c> 是 <c>null</c> —— 于是 <c>Progress</c> 退回线程池：
    /// 回调在后台线程直接写 <c>TextBlock.Text</c>，抛 COMException 0x8001010E（RPC_E_WRONG_THREAD），
    /// 而这条回调没人接异常 → 未观察异常 → <b>直接掀进程</b>。</para>
    /// <para>2026-10-26 真机实测：点「确定并开始下载」后立刻崩，栈就是
    /// <c>Progress`1.InvokeHandlers → TextBlock.set_Text → SettingsPage.RunRdpOperationAsync</c>，
    /// 而且一次连崩两个线程（两次进度回调同时在飞）。</para>
    /// <para>所以进度一律走 <see cref="DispatcherProgress{T}"/>：显式投递到 DispatcherQueue 并吞掉异常；
    /// 本方法开头那几行 UI 赋值同样不假设自己在界面线程上。</para>
    /// </remarks>
    private async Task RunRdpOperationAsync(
        string title,
        Func<IProgress<RdpProgressInfo>, CancellationToken, Task<RdpOperationResult>> operation)
    {
        if (_rdpBusy)
        {
            return;
        }

        var queue = DispatcherQueue;
        _rdpBusy = true;

        RunOnUi(queue, () =>
        {
            SetRdpBusy(true);
            RdpResultBar.IsOpen = false;
        });

        var progress = new DispatcherProgress<RdpProgressInfo>(queue, info =>
        {
            if (_unloaded)
            {
                return;
            }

            RdpProbeSummaryText.Text = $"{title}：第 {info.Step}/{info.Total} 步 —— {info.Text}";
        });

        try
        {
            // 必须在后台线程起步：解锁流程里有注册表写入与 SCM 停启（最长两轮 45 秒等待），
            // 而"不需要下载"这条分支在第一个 await 之前就会走到它们 —— 直接在 UI 线程调用等于界面卡死。
            var result = await Task.Run(() => operation(progress, CancellationToken.None));
            RunOnUi(queue, () =>
            {
                if (_unloaded)
                {
                    return;
                }

                _rdpBusy = false;
                SetRdpBusy(false);
                RenderRdpOperationResult(title, result);
            });
        }
        catch (Exception ex)
        {
            // 服务层设计上不抛异常；真抛出来也要把按钮恢复可点，并把异常本身说清楚
            var detail = $"{ex.GetType().Name}: {ex.Message}";
            RunOnUi(queue, () =>
            {
                if (_unloaded)
                {
                    return;
                }

                _rdpBusy = false;
                SetRdpBusy(false);
                RdpResultBar.Severity = InfoBarSeverity.Error;
                RdpResultBar.Title = title + "：出现未预期错误";
                RdpResultBar.Message = detail;
                RdpResultBar.IsOpen = true;
                SetRdpAction($"{title} 失败：{detail}");
            });
            VM.AppendLog($"[RDP 解锁] {title} 异常：{detail}");
        }
    }

    /// <summary>把一次操作的结果画出来：结果条 + 步骤列表 + 用流程内的最终检测刷新四项。</summary>
    private void RenderRdpOperationResult(string title, RdpOperationResult result)
    {
        RdpResultBar.Severity = result.Success
            ? InfoBarSeverity.Success
            : HasWarningStep(result.Steps)
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Error;
        RdpResultBar.Title = result.Success ? title + "：完成" : title + "：没有全部成功";
        RdpResultBar.Message = result.Summary;
        RdpResultBar.IsOpen = true;

        // 步骤：页面与运行日志都只记短行，完整明细留在「查看诊断」的报告里
        RdpStepsLines.Children.Clear();
        foreach (var step in result.Steps)
        {
            RdpStepsLines.Children.Add(BuildStepLine(step));
            VM.AppendLog($"[RDP 解锁] {step.BriefLine()}");
        }

        RdpStepsTitle.Text = result.Steps.Count > 0
            ? $"执行步骤（{result.Steps.Count} 步）· 明细见「查看诊断」"
            : "执行步骤";
        RdpStepsPanel.Visibility = result.Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 用流程内部的最终检测刷新四项，不重跑一次检测（重跑可能读到中间态）
        if (result.Final is not null)
        {
            _rdpSnapshot = result.Final;
            RenderRdpSnapshot(result.Final);
        }

        if (result.NeedsProtectionReminder)
        {
            SetRdpAction($"提示：本轮下载前您确认关闭过安全软件，请现在重新开启 —— {DateTime.Now:HH:mm:ss}");
        }
        else
        {
            SetRdpAction($"{title} 结束：{(result.Success ? "成功" : "未全部成功")} —— {DateTime.Now:HH:mm:ss}");
        }

        VM.AppendLog($"[RDP 解锁] {title}：{(result.Success ? "完成" : "未全部成功")}");

        // 失败时把完整报告留一份，用户点「查看诊断」就能拿到逐步现场
        _rdpLastReport = result.FormatReport();
    }

    /// <summary>
    /// 一行步骤。
    /// </summary>
    /// <remarks>
    /// 所有等级统一只画「等级 + 标题」的短行（十个字以内）。本页要回答的是
    /// 「走到第几步、哪一步出事」，不是审计正文；完整说明只留在「查看诊断」的报告里。
    /// 以前 Warn / Fail 会把整段 Detail 直接铺在本页，十几步展开后整列都是长句，
    /// 反而看不清。
    /// </remarks>
    private static FrameworkElement BuildStepLine(RdpStepRecord step)
    {
        var panel = new StackPanel { Spacing = 1 };

        panel.Children.Add(new TextBlock
        {
            Text = step.BriefLine(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = StepBrush(step.Outcome),
        });

        return panel;
    }

    /// <summary>
    /// 把一行操作反馈写到本页。
    /// </summary>
    /// <remarks>
    /// 调用点散落在多个 await 之后 —— 本工程没在界面线程上装 SynchronizationContext，
    /// 续体可能落在**线程池**上（2026-10-26 那次崩溃就是这条链）。所以这里不假设自己在
    /// 界面线程上，统一经 <see cref="PostUi"/> 投递。
    /// </remarks>
    private void SetRdpAction(string text) => PostUi(() =>
    {
        RdpProbeActionText.Text = text;
        RdpProbeActionText.Visibility = Visibility.Visible;
    });

    /// <summary>
    /// 把一个 UI 动作安全地送到界面线程。
    /// </summary>
    /// <remarks>
    /// 三件事一起做：页面已卸载就丢弃；已在界面线程就地执行；否则投递。
    /// 执行期异常一律吞掉并落 startup_error.log —— 这些调用点的上游多是 <c>async void</c>，
    /// 放出去就是未观察异常，而"一行提示没显示出来"远不值得让进程消失。
    /// </remarks>
    private void PostUi(Action action)
    {
        if (_unloaded)
        {
            return;
        }

        var queue = DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            SafeUiInvoke(action);
            return;
        }

        queue.TryEnqueue(() => SafeUiInvoke(action));
    }

    /// <summary>
    /// 在界面线程上跑一段返回 Task 的代码，并把结果带回调用方。
    /// </summary>
    /// <remarks>
    /// 专给「对话框必须在界面线程上创建」用。调用点常常在某个 <c>await</c> 之后，
    /// 而 WinUI 的 <see cref="ContentDialog"/> 从别的线程 new 出来会抛 RPC_E_WRONG_THREAD；
    /// 更糟的是 <see cref="DialogHelper.ShowAsync"/> 会把异常吞掉并返回 <c>None</c> ——
    /// 表现成"按钮点了没反应"，比崩溃还难查。
    /// </remarks>
    private static Task<T> RunOnUiAsync<T>(Microsoft.UI.Dispatching.DispatcherQueue? queue, Func<Task<T>> func)
    {
        if (queue is null || queue.HasThreadAccess)
        {
            return func();
        }

        var tcs = new TaskCompletionSource<T>();

        if (!queue.TryEnqueue(async () =>
            {
                try
                {
                    tcs.TrySetResult(await func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("界面消息队列已关闭"));
        }

        return tcs.Task;
    }

    private static void SafeUiInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);
        }
    }

    /// <summary>
    /// 显式投递到 <see cref="Microsoft.UI.Dispatching.DispatcherQueue"/> 的 <see cref="IProgress{T}"/>。
    /// </summary>
    /// <remarks>
    /// <para>为什么不直接用 <see cref="Progress{T}"/>（2026-10-26 血的教训）：
    /// 它依赖构造时捕获的 <see cref="System.Threading.SynchronizationContext"/>，
    /// 而 WinUI 3 的界面线程上它是 <c>null</c> —— <c>Progress</c> 于是退回线程池，
    /// 回调里碰任何控件都是 RPC_E_WRONG_THREAD，而且回调没有异常接收方，一次进度更新就能掀进程。</para>
    /// <para>为什么回调里要吞异常：进度的调用方是后台流程，它们<b>不</b>会在 Report 外面套 try。
    /// 一条进度没画出来是可忽略的，进程消失不是。</para>
    /// </remarks>
    private sealed class DispatcherProgress<T> : IProgress<T>
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _queue;
        private readonly Action<T> _action;

        public DispatcherProgress(Microsoft.UI.Dispatching.DispatcherQueue? queue, Action<T> action)
        {
            _queue = queue;
            _action = action;
        }

        public void Report(T value)
        {
            if (_queue is null || _queue.HasThreadAccess)
            {
                SafeUiInvoke(() => _action(value));
                return;
            }

            _queue.TryEnqueue(() => SafeUiInvoke(() => _action(value)));
        }
    }

    /// <summary>结果条用 Warning 还是 Error：有「注意」级步骤时说明不是硬失败，别一律报红。</summary>
    private static bool HasWarningStep(IReadOnlyList<RdpStepRecord> steps)
    {
        foreach (var step in steps)
        {
            if (step.Outcome == RdpStepOutcome.Warn)
            {
                return true;
            }
        }

        return false;
    }

    private static SolidColorBrush StepBrush(RdpStepOutcome outcome) => outcome switch    {
        RdpStepOutcome.Ok => new SolidColorBrush(Color.FromArgb(0xFF, 0x2F, 0xAE, 0x5B)),
        RdpStepOutcome.Warn => new SolidColorBrush(Color.FromArgb(0xFF, 0xC9, 0x9A, 0x00)),
        RdpStepOutcome.Fail => new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
        RdpStepOutcome.Skipped => new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)),
        _ => new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)),
    };
}
