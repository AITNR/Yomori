// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yomori.Helpers;
using Yomori.Models;
using Yomori.Services;

namespace Yomori.Views;

/// <summary>一次节点选择的结果：<see cref="Acceleration"/> 仅本轮生效，<see cref="CustomPrefix"/> 是要持久化的自定义节点。</summary>
internal sealed record RdpNodePickResult(RdpAccelerationSettings Acceleration, string CustomPrefix);

/// <summary>
/// 「选择下载节点」对话框：打开即自动测速，按延迟升序排序，默认选第一项；
/// 用户可改选、重新测速、填自定义节点，<b>必须点「确定」</b>才返回。
/// </summary>
/// <remarks>
/// <para>代码搭 UI（与 <c>ExportOptionsDialog</c> 同范式），显示统一走
/// <see cref="DialogHelper.ShowAsync"/>（全项目唯一入口）。</para>
/// <para>WinUI 3 没有 SynchronizationContext，<c>await</c> 之后不保证回界面线程：
/// 测速结果一律经 <c>_queue.TryEnqueue</c> 搬回界面线程再碰控件；
/// 「确定」时的选择结果在 <c>PrimaryButtonClick</c>（界面线程）里抓取，
/// 不在 await 之后读控件。</para>
/// <para>测速与后续下载共用 <see cref="UpdateNetworkSettings"/>，代理语义一致。</para>
/// </remarks>
internal sealed class RdpNodePickerDialog
{
    private readonly DispatcherQueue _queue;
    private readonly UpdateNetworkSettings _network;
    private readonly ContentDialog _dialog;
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly ProgressRing _ring = new() { Width = 18, Height = 18, IsActive = false };
    private readonly TextBox _custom = new()
    {
        Header = "自定义节点（可选）",
        PlaceholderText = "https://your-proxy/",
    };
    private readonly Button _retest = new() { Content = "重新测速" };

    private CancellationTokenSource? _cts;
    private RdpNodePickResult? _result;

    public RdpNodePickerDialog(UpdateNetworkSettings network, string customPrefix)
    {
        _queue = DispatcherQueue.GetForCurrentThread()
                 ?? throw new InvalidOperationException("节点选择对话框必须在界面线程上创建。");
        _network = network;
        _custom.Text = customPrefix ?? string.Empty;

        var intro = new TextBlock
        {
            Text = "正在测速，完成后按延迟从低到高排序，默认选中最快的节点。测速与后续下载都走设置里配置的代理。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        };

        var proxy = new TextBlock
        {
            Text = "当前下载代理：" + UpdateProxyModes.Label(UpdateProxyModes.Normalize(network.ProxyMode)),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 12,
        };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(_ring);
        head.Children.Add(_status);

        _retest.Click += (_, _) => _ = MeasureAsync();

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(intro);
        panel.Children.Add(proxy);
        panel.Children.Add(head);
        panel.Children.Add(_rows);
        panel.Children.Add(_custom);
        panel.Children.Add(_retest);

        _dialog = new ContentDialog
        {
            Title = "选择下载节点",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            // 测速完成前不允许确认，避免选中还没排好序的第一项
            IsPrimaryButtonEnabled = false,
        };

        // 事件在界面线程触发，抓取结果不依赖 await 之后的线程
        _dialog.PrimaryButtonClick += (_, _) => CaptureResult();
    }

    /// <summary>显示并等待用户确定；取消或没选到节点返回 null。</summary>
    /// <remarks>方法名不叫 ShowAsync：冒烟断言禁止源码里出现裸 <c>.ShowAsync()</c>，
    /// 与 <c>ExportOptionsDialog.ShowDialogAsync</c> 保持一致。</remarks>
    public async Task<RdpNodePickResult?> ShowDialogAsync()
    {
        // 打开即自动测速；不 await，让对话框先显示出来
        _ = MeasureAsync();

        var shown = await DialogHelper.ShowAsync(_dialog).ConfigureAwait(false);
        _cts?.Cancel();

        return shown == ContentDialogResult.Primary ? _result : null;
    }

    /// <summary>界面线程：把当前选中的行与自定义节点固化成结果。</summary>
    private void CaptureResult()
    {
        var selected = _rows.Children
            .OfType<RadioButton>()
            .FirstOrDefault(r => r.IsChecked == true)?.Tag;

        if (selected is not RdpNodeMeasurement measurement || measurement.Name.Length == 0)
        {
            _result = null;
            return;
        }

        _result = new RdpNodePickResult(
            RdpAccelerationProbe.ToSettings(measurement),
            RdpAcceleration.NormalizePrefix(_custom.Text));
    }

    private async Task MeasureAsync()
    {
        // 第一个 await 之前仍在界面线程（ShowAsync / 按钮 Click 都是），可以读控件
        var customText = _custom.Text ?? string.Empty;
        var nodes = RdpAccelerationProbe.Nodes(customText);

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        _queue.TryEnqueue(() =>
        {
            _dialog.IsPrimaryButtonEnabled = false;
            _ring.IsActive = true;
            _status.Text = $"正在测速…（0/{nodes.Count}）";
            _rows.Children.Clear();
            _rows.Children.Add(new TextBlock { Text = "测速中，请稍候…", Opacity = 0.6 });
        });

        try
        {
            var results = await RdpAccelerationProbe
                .ProbeAsync(nodes, _network, null, cts.Token)
                .ConfigureAwait(false);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            _queue.TryEnqueue(() => Render(results));
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                _queue.TryEnqueue(() => _status.Text = "测速失败：" + ex.GetType().Name);
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                _queue.TryEnqueue(() =>
                {
                    _ring.IsActive = false;
                    _dialog.IsPrimaryButtonEnabled = true;
                });
            }
        }
    }

    /// <summary>界面线程：按排好序的结果重建单选列表，默认选第一项。</summary>
    private void Render(IReadOnlyList<RdpNodeMeasurement> results)
    {
        _rows.Children.Clear();

        var first = true;
        foreach (var measurement in results)
        {
            _rows.Children.Add(new RadioButton
            {
                GroupName = "RdpNodePicker",
                Tag = measurement,
                IsChecked = first,
                Content = measurement.Ok
                    ? $"{measurement.Name} — {measurement.Display}"
                    : $"{measurement.Name} — {measurement.Display}（不可达，选它仍会按候选顺序兜底）",
            });
            first = false;
        }

        if (results.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = "没有可测的节点。" });
        }

        var okCount = results.Count(r => r.Ok);
        _status.Text = okCount > 0
            ? $"测速完成，已按延迟排序（{okCount}/{results.Count} 个可达），默认选中最快节点。"
            : "全部节点都不可达；仍可手动选一个继续，或点「重新测速」。";
    }
}
