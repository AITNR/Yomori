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
    public void RenderStep(OnboardingStep step, int index, int total, Rect focus)
    {
        StepTitle.Text = step.Title;
        StepBody.Text = step.Body;
        ProgressText.Text = $"{index}/{total}";

        RenderImage(step);

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

        PositionBubble(focus, width, height);
    }

    private void RenderImage(OnboardingStep step)
    {
        StepImage.Source = null;
        StepImage.Visibility = Visibility.Collapsed;
        ImageErrorText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(step.ImageRelativePath))
        {
            return;
        }

        try
        {
            var relative = step.ImageRelativePath.Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(AppPaths.AssetsDir, relative);
            if (!File.Exists(path))
            {
                ImageErrorText.Visibility = Visibility.Visible;
                return;
            }

            StepImage.Source = new BitmapImage(new Uri(path, UriKind.Absolute));
            StepImage.MaxWidth = Math.Max(200, Math.Min(520, ActualWidth - 80));
            StepImage.MaxHeight = Math.Max(160, ActualHeight - 280);
            StepImage.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // 图片坏了不能挡住引导结束：退回到文字说明
            ImageErrorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 气泡优先放在目标下方，放不下改上方，再放不下居中；始终夹在视口内。
    /// 先手动 Measure 拿 DesiredSize 再定 Canvas 坐标，避免用尚未布局完的 ActualWidth/Height。
    /// </summary>
    private void PositionBubble(Rect focus, double width, double height)
    {
        var available = Math.Min(460, Math.Max(200, width - 32));
        // 显式设 MaxWidth：Canvas 布局时给的是无限空间，只靠 Measure 的入参约束不住
        Bubble.MaxWidth = available;
        Bubble.Measure(new Size(available, double.PositiveInfinity));
        var bubbleWidth = Bubble.DesiredSize.Width;
        var bubbleHeight = Bubble.DesiredSize.Height;

        double left;
        double top;
        if (focus.Width > 0 && focus.Height > 0)
        {
            left = focus.X;
            top = focus.Bottom + 14;
            if (top + bubbleHeight > height - 12)
            {
                top = focus.Y - bubbleHeight - 14;
            }

            if (top < 12)
            {
                top = Math.Max(12, (height - bubbleHeight) / 2);
            }
        }
        else
        {
            left = (width - bubbleWidth) / 2;
            top = (height - bubbleHeight) / 2;
        }

        left = Math.Clamp(left, 12, Math.Max(12, width - bubbleWidth - 12));
        top = Math.Clamp(top, 12, Math.Max(12, height - bubbleHeight - 12));
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
