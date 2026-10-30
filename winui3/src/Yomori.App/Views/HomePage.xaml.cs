// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Streams;
using Yomori.Helpers;
using Yomori.Services;
using Yomori.ViewModels;

namespace Yomori.Views;

/// <summary>
/// 首页：顶部一张横幅图 + 四张入口卡。
///
/// 2026-10-23 改版：原来的「总览」标题 + 状态四格换成这张图，底部「会话通道」概览整块删除
/// （通道入口在左侧菜单，状态在右侧状态栏，首页不必再列一遍）。
///
/// 2026-10-24：横幅不再是"一张固定默认图"，改成**从 <c>assets/home_banners/</c> 图库里随机抽一张**，
/// 每次进首页重抽（见 <see cref="OnNavigatedTo"/>）。右键「更换图片…」仍可钉死某一张。
/// </summary>
public sealed partial class HomePage : Page
{
    /// <summary>允许当首页图的文件大小上限。再大就不是"显示一下"，而是把进程内存拿去解码了。</summary>
    private const long MaxBannerBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 解码宽度上限（物理像素）。只限宽、高按原比例推 —— 一张 6000×4000 的原图
    /// 整幅解码要 ~90MB，而这块区域高只有 200 逻辑像素。
    /// </summary>
    private const int BannerDecodeWidth = 2048;

    /// <summary>
    /// 上一次随机抽中的图。唯一用途是"下一次别又抽到同一张"：
    /// 三张图的图库里，不排除的话有 1/3 概率切回来看着像没随机。
    ///
    /// ⚠️ 必须是 <c>static</c>：页面每次导航都会 new 一个实例（没开 NavigationCacheMode），
    /// 用实例字段的话这个"上一次"永远是 null，避重复等于没写。
    /// 只在 UI 线程写（导航回调 / 右键菜单），不需要加锁。
    /// </summary>
    private static string? _lastRandomBanner;

    public HomePage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 每次导航到首页都重抽一张。挂在导航事件而不是构造里：
    /// 页面没设 <c>NavigationCacheMode</c>，现在每次切页都会 new 一个实例，构造函数碰巧也能跑；
    /// 但那是**实现细节**，哪天给页面开了缓存，随机就悄悄退化成"只在启动时随机"。导航事件才是可靠信号。
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 图片加载要 await 解码，导航回调里不能 await，发了就走（异常全在内部接住）
        _ = UpdateBannerAsync();
    }

    public MainViewModel VM => App.ViewModel;

    // ---------------- 四张入口卡 ----------------

    private void OnOpenTasks(object sender, RoutedEventArgs e) => VM.Navigate("tasks");

    private void OnOpenStats(object sender, RoutedEventArgs e) => VM.Navigate("stats");

    private void OnOpenLogs(object sender, RoutedEventArgs e) => VM.Navigate("logs");

    /// <summary>通道管理页（与左侧菜单「会话通道 → 通道管理…」同一个 tag）。</summary>
    private void OnOpenChannels(object sender, RoutedEventArgs e) => VM.Navigate("channel-mgmt");

    // ---------------- 首页横幅图 ----------------

    /// <summary>
    /// 随程序发布的首页图库（<c>assets/home_banners/</c>，与 exe 同级落地）。
    /// 每次进首页从里面随机抽一张；用户往这个文件夹里加图，下次进首页就会参与抽取。
    /// </summary>
    private static string BannerFolder => Path.Combine(AppPaths.AssetsDir, "home_banners");

    /// <summary>
    /// 按配置重新加载首页图。
    ///
    /// 优先级：配置里钉死的图 → 图库里随机抽一张 → 占位。
    /// 配置的图**只是找不到**（被删/移动/盘没挂）时静默回退随机图，不打扰；
    /// 但"找到了却读不出来/打不开"是真问题，必须写清原因，否则用户只会看到一片空白。
    /// </summary>
    private async Task UpdateBannerAsync()
    {
        var configured = (VM.Config.HomeBannerPath ?? string.Empty).Trim();

        string path;
        var pinned = configured.Length > 0 && File.Exists(configured);

        if (pinned)
        {
            path = configured;
        }
        else
        {
            var picked = HomeBannerPicker.PickRandom(BannerFolder, _lastRandomBanner);
            if (picked is null)
            {
                SetPlaceholder(
                    "首页图库是空的",
                    "把 png / jpg 图片放进程序目录的 assets\\home_banners 文件夹，"
                    + "之后每次进入首页都会随机显示其中一张。");
                return;
            }

            path = picked;
            _lastRandomBanner = picked;
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBannerBytes)
            {
                SetPlaceholder(
                    "这张图太大了",
                    $"它有 {info.Length / (1024 * 1024)} MB，首页图请控制在 32 MB 以内。");
                return;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            SetPlaceholder("这张图读不出来", ex.Message);
            return;
        }

        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = BannerDecodeWidth,
        };

        try
        {
            using var stream = new InMemoryRandomAccessStream();

            // 走内存流而不是 new BitmapImage(new Uri(path))：后者会把源文件握住不放，
            // 用户想删/移动那张图时会失败。
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        catch (Exception ex)
        {
            SetPlaceholder(
                pinned ? "这张图打不开" : "图库里的这张图打不开",
                $"支持 png / jpg / bmp / gif / webp。{ex.Message}");
            return;
        }

        ApplyBanner(bitmap);
    }

    /// <summary>
    /// 把解码好的图挂到界面上。
    ///
    /// 单独抽一层是为了收口线程：<c>SetSourceAsync</c> 的续体不保证还在 UI 线程上，
    /// 而 Image.Source 是 WinRT 属性，在别的线程上碰它 = COMException 0x8001010E。
    /// </summary>
    private void ApplyBanner(BitmapImage bitmap)
    {
        if (DispatcherQueue is { HasThreadAccess: false } queue)
        {
            queue.TryEnqueue(() => ApplyBanner(bitmap));
            return;
        }

        BannerImage.Source = bitmap;
        BannerImage.Visibility = Visibility.Visible;
        BannerPlaceholder.Visibility = Visibility.Collapsed;
        BannerHint.Visibility = Visibility.Visible;
    }

    /// <summary>退回占位：清掉图（否则旧的图会留在后面），并写上为什么没有图。</summary>
    private void SetPlaceholder(string title, string hint)
    {
        if (DispatcherQueue is { HasThreadAccess: false } queue)
        {
            queue.TryEnqueue(() => SetPlaceholder(title, hint));
            return;
        }

        BannerImage.Source = null;
        BannerImage.Visibility = Visibility.Collapsed;
        BannerPlaceholder.Visibility = Visibility.Visible;
        BannerHint.Visibility = Visibility.Collapsed;
        PlaceholderTitle.Text = title;
        PlaceholderHint.Text = hint;
    }

    /// <summary>
    /// 右键菜单随状态变化：
    /// 钉死了某张图时「换一张」「恢复随机展示」各是各的 —— 抽随机没意义、恢复随机才有；
    /// 没钉死（正在随机）时反过来。
    /// </summary>
    private void OnBannerMenuOpening(object sender, object e)
    {
        var pinned = (VM.Config.HomeBannerPath ?? string.Empty).Trim().Length > 0;
        ShuffleBannerItem.IsEnabled = !pinned;
        ClearBannerItem.IsEnabled = pinned;
    }

    /// <summary>原地换一张随机图：不用切走再切回来。</summary>
    private async void OnShuffleBanner(object sender, RoutedEventArgs e) => await UpdateBannerAsync();

    private async void OnChangeBannerImage(object sender, RoutedEventArgs e)
    {
        // PickOpenFileAsync 已经把结果搬回 UI 线程了（见 DialogHelper 里的说明），
        // 这里拿到路径就能直接改界面。
        var path = await DialogHelper.PickOpenFileAsync(
            ("图片", ".png"),
            ("图片", ".jpg"),
            ("图片", ".jpeg"),
            ("图片", ".bmp"),
            ("图片", ".gif"),
            ("图片", ".webp"));

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        VM.SaveHomeBanner(path);
        await UpdateBannerAsync();
    }

    /// <summary>清掉钉死的那张图，回到「每次进首页随机抽一张」。</summary>
    private async void OnRestoreRandomBanner(object sender, RoutedEventArgs e)
    {
        VM.SaveHomeBanner(string.Empty);
        await UpdateBannerAsync();
    }
}
