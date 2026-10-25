// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    public SettingsPage()
    {
        InitializeComponent();

        // 离开页面就别再往回写提示控件了
        Unloaded += (_, __) => _themeHintCts?.Cancel();

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
            UpdateProxyUrlBox.Visibility = VM.Config.UpdateProxyMode == UpdateProxyModes.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _syncing = false;
        }

        RefreshVersionText();
        RefreshExternalLogSection();
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
    /// 并立刻落盘 —— 它跟主题、更新渠道一样是"拨了就能看见"的开关，等用户想起来
    /// 再点「保存配置」的话，重启后悄悄回到关闭状态会很难理解。
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

    private void OnSave(object sender, RoutedEventArgs e) => VM.SaveConfig();

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
        }

        // 只有"自定义"才需要地址框；切走时保留已填内容，不主动清空
        UpdateProxyUrlBox.Visibility = mode == UpdateProxyModes.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnUpdateProxyUrlLostFocus(object sender, RoutedEventArgs e)
    {
        var text = (UpdateProxyUrlBox.Text ?? string.Empty).Trim();
        if (VM.Config.UpdateProxyUrl == text)
        {
            return;
        }

        VM.Config.UpdateProxyUrl = text;
        VM.SaveConfig();

        // 顺手说一句地址能不能解析，省得用户到"下载失败"才发现填错
        var usable = string.IsNullOrEmpty(text) || Uri.TryCreate(text, UriKind.Absolute, out _);
        VM.AppendLog(usable
            ? $"更新下载代理地址已保存：{(text.Length == 0 ? "（空 → 回退系统代理）" : text)}"
            : $"更新下载代理地址格式可疑（{text}）—— 需要带协议，例如 http://127.0.0.1:7890；" +
              "解析不出来时会退回系统代理。");
    }

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

        UpdateResultText.Text = result.Message;
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
}
