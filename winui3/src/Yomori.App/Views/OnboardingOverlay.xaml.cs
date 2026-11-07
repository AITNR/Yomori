// -*- coding: utf-8 -*-
using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Yomori.Models;
using Yomori.Services;

namespace Yomori.Views;

/// <summary>
/// 首次启动引导的可视层：暗化挖洞、聚焦描边、气泡与配图。
/// 步骤推进与页面导航不在这里，交给 <see cref="OnboardingController"/>。
/// </summary>
public sealed partial class OnboardingOverlay : UserControl
{
    /// <summary>配图步骤的气泡上限：截图是这一步的主角，别把它挤在窄条里。</summary>
    private const double ImageBubbleMaxWidth = 900;

    /// <summary>普通步骤的气泡上限。</summary>
    private const double TextBubbleMaxWidth = 460;

    /// <summary>气泡与视口之间至少留的边距。</summary>
    private const double ViewportMargin = 12;

    public OnboardingOverlay()
    {
        InitializeComponent();

        // 窗口缩放 / 主题切换会让已算好的高亮框与气泡位置失效，通知控制器重画当前步
        SizeChanged += (_, _) => LayoutChangedRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>覆盖层尺寸变了，需要重画当前步。</summary>
    public event EventHandler? LayoutChangedRequested;

    /// <summary>点击覆盖层任意位置 = 下一步。</summary>
    public event EventHandler? AdvanceRequested;

    /// <summary>点「跳过」= 直接结束。</summary>
    public event EventHandler? SkipRequested;

    public void Show() => Visibility = Visibility.Visible;

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        StepImage.Source = null;
        HoleGeometry.Rect = new Rect(0, 0, 0, 0);
        FocusBorder.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 画一步。<paramref name="focus"/> 是目标控件相对本覆盖层左上角的矩形，
    /// 宽高为 0 表示不挖洞（最后一步的配图卡片居中展示）。
    /// </summary>
    public void RenderStep(OnboardingStep step, int index, int total, Rect focus, bool pinBubbleTopRight = false)
    {
        StepTitle.Text = step.Title;
        StepBody.Text = step.Body;
        ProgressText.Text = $"{index}/{total}";

        var width = ActualWidth;
        var height = ActualHeight;
        FullGeometry.Rect = new Rect(0, 0, width, height);

        if (focus.Width > 0 && focus.Height > 0)
        {
            HoleGeometry.Rect = focus;
            FocusBorder.Visibility = Visibility.Visible;
            FocusBorder.Width = focus.Width;
            FocusBorder.Height = focus.Height;
            Canvas.SetLeft(FocusBorder, focus.X);
            Canvas.SetTop(FocusBorder, focus.Y);
        }
        else
        {
            HoleGeometry.Rect = new Rect(0, 0, 0, 0);
            FocusBorder.Visibility = Visibility.Collapsed;
        }

        var hasImage = PrepareImage(step, out var imageAspect);
        var bubbleMaxWidth = hasImage
            ? Math.Min(ImageBubbleMaxWidth, Math.Max(320, width - 48))
            : Math.Min(TextBubbleMaxWidth, Math.Max(200, width - 32));

        // 配图步骤收紧内边距与行距，把腾出来的高度全给图片。
        // 之前固定减 280 的做法在矮窗口里把 1804×1401 的截图压到 270px 宽，看不清标注。
        Bubble.Padding = hasImage ? new Thickness(12) : new Thickness(18);
        BubbleContent.Spacing = hasImage ? 6 : 10;
        Bubble.MaxWidth = bubbleMaxWidth;

        // 两阶段测量：先量「不含图片」的固有高度，再用剩余视口高度反推图片上限，
        // 保证气泡整体不溢出窗口；比拍脑袋减一个常数精确得多。
        StepImage.Visibility = Visibility.Collapsed;
        Bubble.Measure(new Size(bubbleMaxWidth, double.PositiveInfinity));
        var baseHeight = Bubble.DesiredSize.Height;

        if (hasImage)
        {
            // 显式给图片定尺寸。BitmapImage 是异步解码的：只设 MaxWidth/MaxHeight 时，
            // 第二次 Measure 可能还拿不到图片的自然尺寸，气泡按「没有图片」的高度居中，
            // 等图片一撑开就整个掉出窗口底部（2026-11-07 14:41 真机反馈）。
            var imageMaxWidth = Math.Max(240, bubbleMaxWidth - 24);
            var imageMaxHeight = Math.Max(160, height - baseHeight - 32);
            var imageHeight = imageMaxHeight;
            var imageWidth = imageHeight * imageAspect;
            if (imageWidth > imageMaxWidth)
            {
                imageWidth = imageMaxWidth;
                imageHeight = imageWidth / imageAspect;
            }

            StepImage.MaxWidth = double.PositiveInfinity;
            StepImage.MaxHeight = double.PositiveInfinity;
            StepImage.Width = imageWidth;
            StepImage.Height = imageHeight;
            StepImage.Visibility = Visibility.Visible;
        }

        Bubble.Measure(new Size(bubbleMaxWidth, double.PositiveInfinity));
        var bubbleWidth = Bubble.DesiredSize.Width;
        var bubbleHeight = Bubble.DesiredSize.Height;

        // 正文特别长时仍可能超高：按超出的量把图片再压一档，不裁气泡
        if (hasImage && bubbleHeight > height - 2 * ViewportMargin)
        {
            var overflow = bubbleHeight - (height - 2 * ViewportMargin);
            var shrunkHeight = Math.Max(120, StepImage.Height - overflow);
            StepImage.Height = shrunkHeight;
            StepImage.Width = shrunkHeight * imageAspect;
            Bubble.Measure(new Size(bubbleMaxWidth, double.PositiveInfinity));
            bubbleWidth = Bubble.DesiredSize.Width;
            bubbleHeight = Bubble.DesiredSize.Height;
        }

        PositionBubble(focus, width, height, bubbleWidth, bubbleHeight, pinBubbleTopRight);
    }

    /// <summary>
    /// 只准备图片源与宽高比，尺寸与显隐交给 RenderStep 的两阶段测量。
    /// 宽高比从 PNG 文件头读（同步、不依赖 BitmapImage 的异步解码）——
    /// 图片没解码完时 Measure 拿不到自然尺寸，气泡会按「没有图片」的高度居中，
    /// 之后图片一撑开就整个掉出窗口底部。
    /// </summary>
    private bool PrepareImage(OnboardingStep step, out double aspect)
    {
        aspect = 1804.0 / 1401.0; // 兜底：随包资产的已知比例
        StepImage.Source = null;
        StepImage.Visibility = Visibility.Collapsed;
        ImageErrorText.Visibility = Visibility.Collapsed;
        StepImage.Width = double.NaN;
        StepImage.Height = double.NaN;

        if (string.IsNullOrEmpty(step.ImageRelativePath))
        {
            return false;
        }

        try
        {
            var relative = step.ImageRelativePath.Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(AppPaths.AssetsDir, relative);
            if (!File.Exists(path))
            {
                ImageErrorText.Visibility = Visibility.Visible;
                return false;
            }

            aspect = ReadPngAspect(path, aspect);
            StepImage.Source = new BitmapImage(new Uri(path, UriKind.Absolute));
            return true;
        }
        catch (Exception)
        {
            // 图片坏了不能挡住引导结束：退回到文字说明
            ImageErrorText.Visibility = Visibility.Visible;
            return false;
        }
    }

    /// <summary>从 PNG 的 IHDR 读宽高比，只读前 24 字节；读不出来用兜底值。</summary>
    private static double ReadPngAspect(string path, double fallback)
    {
        try
        {
            var header = new byte[24];
            using (var stream = File.OpenRead(path))
            {
                if (stream.Read(header, 0, header.Length) != header.Length)
                {
                    return fallback;
                }
            }

            // PNG 签名 89 50 4E 47
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
            {
                return fallback;
            }

            var w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            var h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return w > 0 && h > 0 ? (double)w / h : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>
    /// 气泡优先放在目标下方，放不下改上方，再放不下居中；始终夹在视口内。
    /// 调用方已经 Measure 过，宽高直接传进来，避免用尚未布局完的 ActualWidth/Height。
    /// </summary>
    private void PositionBubble(Rect focus, double width, double height, double bubbleWidth, double bubbleHeight, bool pinBubbleTopRight)
    {
        if (pinBubbleTopRight)
        {
            // 卡片已顶到视口最上、自身又比视口高，按「目标上下方」找位置会盖住正文
            // 或跑出窗口。固定在右上角，位置不随滚动与分辨率漂移。
            var pinnedLeft = Math.Clamp(width - bubbleWidth - 24, ViewportMargin,
                Math.Max(ViewportMargin, width - bubbleWidth - ViewportMargin));
            var pinnedTop = Math.Clamp(24, ViewportMargin,
                Math.Max(ViewportMargin, height - bubbleHeight - ViewportMargin));
            Canvas.SetLeft(Bubble, pinnedLeft);
            Canvas.SetTop(Bubble, pinnedTop);
            return;
        }

        double left;
        double top;
        if (focus.Width > 0 && focus.Height > 0)
        {
            left = focus.X;
            top = focus.Bottom + 14;
            if (top + bubbleHeight > height - ViewportMargin)
            {
                top = focus.Y - bubbleHeight - 14;
            }

            if (top < ViewportMargin)
            {
                top = Math.Max(ViewportMargin, (height - bubbleHeight) / 2);
            }
        }
        else
        {
            left = (width - bubbleWidth) / 2;
            top = (height - bubbleHeight) / 2;
        }

        left = Math.Clamp(left, ViewportMargin, Math.Max(ViewportMargin, width - bubbleWidth - ViewportMargin));
        top = Math.Clamp(top, ViewportMargin, Math.Max(ViewportMargin, height - bubbleHeight - ViewportMargin));
        Canvas.SetLeft(Bubble, left);
        Canvas.SetTop(Bubble, top);
    }

    private void OnAdvanceTapped(object sender, TappedRoutedEventArgs e)
    {
        AdvanceRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSkipTapped(object sender, TappedRoutedEventArgs e)
    {
        // 不置 Handled 的话会冒泡到 HitLayer，跳过反而变成"下一步"
        e.Handled = true;
        SkipRequested?.Invoke(this, EventArgs.Empty);
    }
}
