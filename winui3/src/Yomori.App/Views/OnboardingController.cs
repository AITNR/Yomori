// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Yomori.Models;

namespace Yomori.Views;

/// <summary>
/// 首次启动引导的状态机：推进步骤、换页、延后测量、结束清理。
///
/// <para>🔑 时序纪律：换页后不能立刻取控件坐标 —— Frame.Navigate 之后页面还没布局完，
/// 设置页的 RDP 卡片还要先滚进视口。跨页走两段式定时器：
/// 阶段 1（导航后 120ms，最多重试 3 次）定位目标并 StartBringIntoView；
/// 阶段 2（再 350ms）测坐标、画气泡。同页切目标不必重建页面，下一拍直接重画，
/// 免得每点一次都要等半秒。定时器挂字段保活（局部 CreateTimer 会被 GC）。</para>
///
/// <para>全部在 UI 线程完成，不使用 Task.Run 续体改绑定 —— 跨线程改绑定是既有
/// COMException 0x8001010E → 原生 AV 的复发点。</para>
/// </summary>
public sealed class OnboardingController
{
    private const int LocateDelayMs = 120;
    private const int ScrollSettleMs = 350;
    private const int ImmediateRedrawMs = 1;
    private const int MaxLocateRetries = 3;

    private readonly MainWindow _window;
    private readonly OnboardingOverlay _overlay;
    private readonly IReadOnlyList<OnboardingStep> _steps = OnboardingPlan.Build();
    private readonly DispatcherQueueTimer _measureTimer;

    private int _index = -1;
    private int _phase;
    private int _locateRetry;
    private bool _running;
    private bool _awaitingMeasure;

    public OnboardingController(MainWindow window, OnboardingOverlay overlay, DispatcherQueue dispatcher)
    {
        _window = window;
        _overlay = overlay;

        _measureTimer = dispatcher.CreateTimer();
        _measureTimer.IsRepeating = false;
        _measureTimer.Tick += (_, _) => OnMeasureTick();
        _overlay.AdvanceRequested += (_, _) => Advance();
        _overlay.SkipRequested += (_, _) => Finish();

        // 窗口缩放 / 主题切换后高亮框会偏：重画当前步。
        // 已有定时器在排队时忽略，免得把跨页的「定位 → 滚动 → 渲染」时序打断。
        _overlay.LayoutChangedRequested += (_, _) =>
        {
            if (_running && !_awaitingMeasure)
            {
                Schedule(ImmediateRedrawMs, phase: 2);
            }
        };
    }

    public bool IsRunning => _running;

    /// <summary>
    /// 开始播放。自动触发时 <paramref name="force"/> 为 false，只认全新安装；
    /// 设置页「重新观看开始引导」传 true，无视首次启动标记。
    /// </summary>
    public void Start(bool force)
    {
        if (_running || (!force && !_window.VM.IsFreshInstall))
        {
            return;
        }

        _running = true;
        _index = -1;
        _locateRetry = 0;
        _overlay.Show();
        _window.VM.AppendLog($"[引导] 开始（共 {_steps.Count} 步，回退页 {OnboardingPlan.FinishTag}）");
        GoTo(0);
    }

    public void Advance()
    {
        if (!_running)
        {
            return;
        }

        if (_index + 1 >= _steps.Count)
        {
            Finish();
            return;
        }

        GoTo(_index + 1);
    }

    /// <summary>完成或跳过：停定时器、摘覆盖层、回到首页。</summary>
    public void Finish()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _measureTimer.Stop();
        _awaitingMeasure = false;
        _overlay.Hide();
        _window.NavigateForOnboarding(OnboardingPlan.FinishTag);
        _window.VM.AppendLog("[引导] 结束");
    }

    private void GoTo(int index)
    {
        _index = index;
        var step = _steps[index];
        _locateRetry = 0;

        var pageChanged = !string.Equals(_window.CurrentPageTag, step.PageTag, StringComparison.Ordinal);
        if (pageChanged)
        {
            _window.NavigateForOnboarding(step.PageTag);
            Schedule(LocateDelayMs, phase: 1);
        }
        else
        {
            // 首页四步 / 最后一步都在同一页：页面没重建，直接下一拍重画
            Schedule(ImmediateRedrawMs, phase: 2);
        }
    }

    private void Schedule(int delayMs, int phase)
    {
        _phase = phase;
        _awaitingMeasure = true;
        _measureTimer.Stop();
        _measureTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _measureTimer.Start();
    }

    private void OnMeasureTick()
    {
        _measureTimer.Stop();
        _awaitingMeasure = false;
        if (!_running)
        {
            return;
        }

        if (_phase == 1)
        {
            PhaseLocate();
        }
        else
        {
            PhaseRender();
        }
    }

    /// <summary>定位目标；位于 ScrollViewer 内的目标先滚进视口。找不到就有限重试。</summary>
    private void PhaseLocate()
    {
        var step = _steps[_index];
        var target = FindTarget(step);

        // 换页后页面可能还没进 namescope：直接放弃会让设置页的 RDP 卡片停在视口外，
        // 表现为「没挖洞、气泡居中」。有限重试几次比一次定生死稳。
        if (target is null && step.TargetName is not null && _locateRetry < MaxLocateRetries)
        {
            _locateRetry++;
            Schedule(LocateDelayMs, phase: 1);
            return;
        }

        if (target is not null)
        {
            try
            {
                target.StartBringIntoView();
            }
            catch (Exception ex)
            {
                _window.VM.AppendLog("[引导] 滚动到目标失败：" + ex.Message);
            }
        }

        Schedule(ScrollSettleMs, phase: 2);
    }

    /// <summary>测量目标矩形并交给覆盖层绘制；找不到目标就降级为居中气泡。</summary>
    private void PhaseRender()
    {
        var step = _steps[_index];
        var target = FindTarget(step);
        var focus = new Rect(0, 0, 0, 0);

        if (target is not null && target.ActualWidth > 0 && target.ActualHeight > 0)
        {
            try
            {
                focus = target.TransformToVisual(_overlay)
                    .TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));

                // 卡片可能比视口高：与覆盖层视口求交，别把高亮框画到窗口外
                focus.Intersect(new Rect(0, 0, _overlay.ActualWidth, _overlay.ActualHeight));
            }
            catch (Exception ex)
            {
                _window.VM.AppendLog("[引导] 目标定位失败，降级为居中气泡：" + ex.Message);
                focus = new Rect(0, 0, 0, 0);
            }
        }
        else if (step.TargetName is not null)
        {
            _window.VM.AppendLog("[引导] 未找到目标控件 " + step.TargetName + "，降级为居中气泡");
        }

        _overlay.RenderStep(step, _index + 1, _steps.Count, focus);
    }

    private FrameworkElement? FindTarget(OnboardingStep step) =>
        step.TargetName is null ? null : _window.FindOnboardingTarget(step.TargetName);
}
