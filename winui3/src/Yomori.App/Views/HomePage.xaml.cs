// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
    /// 已解码横幅最多留三张，且总像素数不超过这个预算（BGRA 约 32MB 上限）。这里只缓存最终位图，
    /// 不缓存源文件字节；默认三张 1920×1080 约 24MB 解码内存，换到超大图时
    /// 会按实际解码尺寸自动少留或不留，不会因为缓存把首页常驻内存放大。
    /// </summary>
    private const int BannerCacheCapacity = 3;
    private const long BannerCachePixelBudget = 8_000_000;

    private static readonly object BannerCacheGate = new();
    private static readonly List<CachedBanner> BannerCache = new(BannerCacheCapacity);

    /// <summary>
    /// 上一次随机抽中的图。唯一用途是"下一次别又抽到同一张"：
    /// 三张图的图库里，不排除的话有 1/3 概率切回来看着像没随机。
    ///
    /// ⚠️ 必须是 <c>static</c>：页面每次导航都会 new 一个实例（没开 NavigationCacheMode），
    /// 用实例字段的话这个"上一次"永远是 null，避重复等于没写。
    /// </summary>
    private static string? _lastRandomBanner;

    /// <summary>当前页面这一轮生命周期的取消源；离开页面后，所有旧请求都必须失效。</summary>
    private CancellationTokenSource? _pageLifetimeCts;

    /// <summary>当前正在加载的横幅请求。快速「换一张」时先取消上一笔，避免并发读同一批文件。</summary>
    private CancellationTokenSource? _activeBannerRequestCts;

    /// <summary>每个请求的世代号。异步续体写 UI 前必须核对它仍是当前请求。</summary>
    private long _bannerRequestVersion;

    private volatile bool _isPageActive;

    private readonly record struct BannerFileStamp(long Length, DateTime LastWriteTimeUtc);

    private readonly record struct BannerReadResult(
        byte[]? Bytes,
        long Length,
        DateTime LastWriteTimeUtc)
    {
        public BannerFileStamp Stamp => new(Length, LastWriteTimeUtc);
    }

    private sealed class CachedBanner
    {
        public CachedBanner(
            string path,
            BannerFileStamp stamp,
            BitmapImage bitmap,
            long pixelCount)
        {
            Path = path;
            Stamp = stamp;
            Bitmap = bitmap;
            PixelCount = pixelCount;
        }

        public string Path { get; }
        public BannerFileStamp Stamp { get; }
        public BitmapImage Bitmap { get; }
        public long PixelCount { get; }
    }

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

        CancelActiveBannerRequest();
        _pageLifetimeCts?.Cancel();
        _pageLifetimeCts?.Dispose();
        _pageLifetimeCts = new CancellationTokenSource();

        Interlocked.Increment(ref _bannerRequestVersion);
        _isPageActive = true;

        // 图片加载要 await 解码，导航回调里不能 await，发了就走（异常全在内部接住）
        _ = UpdateBannerAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // 先让页面和所有在途请求失效，再做基类导航清理。即使 SetSourceAsync 无法取消，
        // 它完成后的续体也会在 ApplyBanner / SetPlaceholder 前被世代检查挡掉。
        _isPageActive = false;
        Interlocked.Increment(ref _bannerRequestVersion);
        CancelActiveBannerRequest();

        var lifetime = _pageLifetimeCts;
        _pageLifetimeCts = null;
        lifetime?.Cancel();
        lifetime?.Dispose();

        base.OnNavigatedFrom(e);
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
    ///
    /// 同路径、同长度、同最后写入时间的已解码图直接复用；文件变化会 miss。
    /// 每次开始都会取消上一笔请求并推进世代号，离开页面后旧续体不会写回 UI。
    /// </summary>
    private async Task UpdateBannerAsync()
    {
        var generation = Interlocked.Increment(ref _bannerRequestVersion);
        var pageLifetime = _pageLifetimeCts;

        if (!_isPageActive || pageLifetime is null)
        {
            return;
        }

        CancelActiveBannerRequest();
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(pageLifetime.Token);
        _activeBannerRequestCts = requestCts;
        var token = requestCts.Token;
        var pinned = false;
        var decoding = false;

        try
        {
            if (!IsCurrentRequest(generation))
            {
                return;
            }

            var configured = (VM.Config.HomeBannerPath ?? string.Empty).Trim();
            string path;
            pinned = configured.Length > 0 && File.Exists(configured);

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
                        generation,
                        "首页图库是空的",
                        "把 png / jpg 图片放进程序目录的 assets\\home_banners 文件夹，"
                        + "之后每次进入首页都会随机显示其中一张。");
                    return;
                }

                path = picked;
                _lastRandomBanner = picked;
            }

            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("图片文件已不存在。", path);
            }

            if (info.Length > MaxBannerBytes)
            {
                ShowTooLarge(generation, info.Length);
                return;
            }

            var beforeStamp = new BannerFileStamp(info.Length, info.LastWriteTimeUtc);
            if (TryGetCachedBanner(path, beforeStamp, out var cached) && cached is not null)
            {
                ApplyBanner(cached, generation);
                return;
            }

            var read = await ReadBannerFileAsync(path, token);

            if (!IsCurrentRequest(generation))
            {
                return;
            }

            if (read.Bytes is null)
            {
                ShowTooLarge(generation, read.Length);
                return;
            }

            decoding = true;
            var bitmap = await DecodeBannerAsync(read.Bytes, token);

            if (!IsCurrentRequest(generation))
            {
                return;
            }

            var pixelCount = await RunOnUiThreadAsync(
                () => (long)bitmap.PixelWidth * bitmap.PixelHeight);
            CacheBanner(path, read.Stamp, bitmap, pixelCount);

            ApplyBanner(bitmap, generation);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 快速换图或离开页面：旧请求主动退出，不显示错误占位。
        }
        catch (Exception ex)
        {
            if (!IsCurrentRequest(generation))
            {
                return;
            }

            if (decoding)
            {
                SetPlaceholder(
                    generation,
                    pinned ? "这张图打不开" : "图库里的这张图打不开",
                    $"支持 png / jpg / bmp / gif / webp。{ex.Message}");
            }
            else
            {
                SetPlaceholder(generation, "这张图读不出来", ex.Message);
            }
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _activeBannerRequestCts,
                null,
                requestCts);
            requestCts.Dispose();
        }
    }

    private static async Task<BannerReadResult> ReadBannerFileAsync(
        string path,
        CancellationToken token)
    {
        // 正常情况下一次通过。若读到一半文件被替换，再试一次拿一份长度/时间与内容一致的快照；
        // 这份 stamp 同时作为解码缓存键，避免“缓存了半旧文件”的长期显示。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();

            var before = new FileInfo(path);
            if (!before.Exists)
            {
                throw new FileNotFoundException("图片文件已不存在。", path);
            }

            var beforeWriteTime = before.LastWriteTimeUtc;
            if (before.Length > MaxBannerBytes)
            {
                return new BannerReadResult(null, before.Length, beforeWriteTime);
            }

            var bytes = await File.ReadAllBytesAsync(path, token);
            token.ThrowIfCancellationRequested();

            var after = new FileInfo(path);
            if (after.Exists
                && after.Length == bytes.LongLength
                && after.LastWriteTimeUtc == beforeWriteTime)
            {
                return new BannerReadResult(bytes, after.Length, after.LastWriteTimeUtc);
            }
        }

        throw new IOException("图片正在被其他程序修改，请稍后重试。");
    }

    private async Task<BitmapImage> DecodeBannerAsync(
        byte[] bytes,
        CancellationToken token)
    {
        var bitmap = await RunOnUiThreadAsync(() => new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = BannerDecodeWidth,
        });

        using var stream = new InMemoryRandomAccessStream();

        // 走内存流而不是 new BitmapImage(new Uri(path))：后者会把源文件握住不放，
        // 用户想删/移动那张图时会失败。
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            token.ThrowIfCancellationRequested();
            writer.DetachStream();
        }

        stream.Seek(0);
        var decode = await RunOnUiThreadAsync(() => bitmap.SetSourceAsync(stream));
        await decode;
        token.ThrowIfCancellationRequested();
        return bitmap;
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<T> action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("UI dispatcher is shutting down."));
        }

        return completion.Task;
    }

    private static bool TryGetCachedBanner(
        string path,
        BannerFileStamp stamp,
        out BitmapImage? bitmap)
    {
        var normalizedPath = NormalizeBannerPath(path);

        lock (BannerCacheGate)
        {
            for (var i = 0; i < BannerCache.Count; i++)
            {
                var entry = BannerCache[i];
                if (!string.Equals(entry.Path, normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Stamp == stamp)
                {
                    BannerCache.RemoveAt(i);
                    BannerCache.Add(entry);
                    bitmap = entry.Bitmap;
                    return true;
                }

                // 同一路径的旧版本立即丢掉，后续也不会被错误复用。
                BannerCache.RemoveAt(i);
                break;
            }
        }

        bitmap = null;
        return false;
    }

    private static void CacheBanner(
        string path,
        BannerFileStamp stamp,
        BitmapImage bitmap,
        long pixelCount)
    {
        if (pixelCount <= 0 || pixelCount > BannerCachePixelBudget)
        {
            // 单张图已超出预算时宁可不留，也不把首页解码内存长期钉在进程里。
            return;
        }

        var normalizedPath = NormalizeBannerPath(path);
        lock (BannerCacheGate)
        {
            for (var i = BannerCache.Count - 1; i >= 0; i--)
            {
                if (string.Equals(
                    BannerCache[i].Path,
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    BannerCache.RemoveAt(i);
                }
            }

            var usedPixels = 0L;
            foreach (var entry in BannerCache)
            {
                usedPixels += entry.PixelCount;
            }

            while (BannerCache.Count > 0
                   && (BannerCache.Count >= BannerCacheCapacity
                       || usedPixels + pixelCount > BannerCachePixelBudget))
            {
                usedPixels -= BannerCache[0].PixelCount;
                BannerCache.RemoveAt(0);
            }

            if (BannerCache.Count < BannerCacheCapacity
                && usedPixels + pixelCount <= BannerCachePixelBudget)
            {
                BannerCache.Add(new CachedBanner(normalizedPath, stamp, bitmap, pixelCount));
            }
        }
    }

    private static string NormalizeBannerPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private void ShowTooLarge(long generation, long length)
    {
        SetPlaceholder(
            generation,
            "这张图太大了",
            $"它有 {length / (1024 * 1024)} MB，首页图请控制在 32 MB 以内。");
    }

    private bool IsCurrentRequest(long generation)
        => _isPageActive
           && generation == Interlocked.Read(ref _bannerRequestVersion);

    private void CancelActiveBannerRequest()
    {
        var request = Interlocked.Exchange(ref _activeBannerRequestCts, null);
        if (request is null)
        {
            return;
        }

        try
        {
            request.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 请求恰好已完成并释放；它的续体已经有世代检查兜底。
        }
    }

    /// <summary>
    /// 把解码好的图挂到界面上。
    ///
    /// 单独抽一层是为了收口线程：<c>SetSourceAsync</c> 的续体不保证还在 UI 线程上，
    /// 而 Image.Source 是 WinRT 属性，在别的线程上碰它 = COMException 0x8001010E。
    /// </summary>
    private void ApplyBanner(BitmapImage bitmap, long generation)
    {
        if (!IsCurrentRequest(generation))
        {
            return;
        }

        if (DispatcherQueue is { HasThreadAccess: false } queue)
        {
            queue.TryEnqueue(() => ApplyBanner(bitmap, generation));
            return;
        }

        if (!IsCurrentRequest(generation))
        {
            return;
        }

        BannerImage.Source = bitmap;
        BannerImage.Visibility = Visibility.Visible;
        BannerPlaceholder.Visibility = Visibility.Collapsed;
        BannerHint.Visibility = Visibility.Visible;
    }

    /// <summary>退回占位：清掉图（否则旧的图会留在后面），并写上为什么没有图。</summary>
    private void SetPlaceholder(long generation, string title, string hint)
    {
        if (!IsCurrentRequest(generation))
        {
            return;
        }

        if (DispatcherQueue is { HasThreadAccess: false } queue)
        {
            queue.TryEnqueue(() => SetPlaceholder(generation, title, hint));
            return;
        }

        if (!IsCurrentRequest(generation))
        {
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
