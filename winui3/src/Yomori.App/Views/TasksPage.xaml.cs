// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Yomori.Helpers;
using Yomori.Models;
using Yomori.Services;
using Yomori.ViewModels;

namespace Yomori.Views;

/// <summary>
/// 任务执行页 —— **按执行通道编排任务**（计划书 <c>docs/tasks-by-channel-plan.md</c>）。
///
/// 页面只做三件事：把 VM 的「通道 / 通道内任务」视图显示出来、把按钮转发给 VM、同步选中态。
/// 排序、改归属、重编序号这些逻辑全在 <see cref="TaskScopePlanner"/>（纯函数，可脱离 WinUI 测）——
/// 塞进页面里就没法写冒烟了。
/// </summary>
public sealed partial class TasksPage : Page
{
    private bool _syncing;

    /// <summary>
    /// VM.PropertyChanged 的处理器 —— **必须是具名字段并在 Unloaded 里退订**。
    ///
    /// ⚠️ 这里以前是 `VM.PropertyChanged += (_, args) => {...}` 匿名 lambda：无法退订。
    /// 而 VM 是全局单例、活到进程结束，页面每次导航都新建（MainWindow 用
    /// <c>ContentFrame.Navigate</c>，不做缓存），于是**每进一次任务页就多一个订阅者**，
    /// 旧的全都还活着。导入配置时 <c>Tasks.Clear()</c> + 逐个 Add + 多次 RefreshScopeTasks()
    /// 会广播给所有累积的僵尸订阅者，每个都去碰一个早已离开可视树、甚至已被
    /// Frame 换掉的 ListView —— 轻则抛 WinRT 异常（Message 常为空串，日志里只留
    /// 一句「导入配置失败：」），重则把 UI 线程拖进死循环。
    /// </summary>
    private readonly PropertyChangedEventHandler _vmPropertyChanged;

    public TasksPage()
    {
        InitializeComponent();

        TimeoutActionBox.ItemsSource = VM.TimeoutActionItems;
        WaitModeBox.ItemsSource = VM.WaitModeItems;
        ConcurrentPolicyBox.ItemsSource = VM.ConcurrentPolicyItems;

        _vmPropertyChanged = OnViewModelPropertyChanged;
        VM.PropertyChanged += _vmPropertyChanged;

        // 捕获挂在 TaskList 上，移动/松开/捕获丢失都在这一层接：
        // handledEventsToo 是保险（万一 ListViewItem 先抢了指针并标了 Handled，
        // 事件仍会冒泡上来）；三个处理函数对「没在拖」是空转，重复触发无害。
        TaskList.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnTaskGripPointerMoved), true);
        TaskList.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnTaskGripPointerReleased), true);
        TaskList.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnDragCaptureLost), true);

        // 页面离开可视树就退订。Frame 可能缓存页面，所以 Loaded 时要重新挂上，
        // 否则从别的页返回时选中态再也不跟着 VM 走。
        // 退订后再挂是幂等的（同一 handler 挂两次也会被去重），这里仍写成
        // 「先 - 再 +」是为了让「任何时候最多只有一个订阅」的意图显式。
        Loaded += (_, _) =>
        {
            VM.PropertyChanged -= _vmPropertyChanged;
            VM.PropertyChanged += _vmPropertyChanged;
        };
        Unloaded += (_, _) => VM.PropertyChanged -= _vmPropertyChanged;

        SyncScopeSelection();
        SyncTaskSelection();
        SyncForm();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(MainViewModel.SelectedTask):
                SyncTaskSelection();
                SyncForm();
                break;

            case nameof(MainViewModel.SelectedScope):
                SyncScopeSelection();
                SyncTaskSelection();
                SyncForm();
                break;

            // 任务视图被重建（增删 / 排序 / 改派）后要把列表选中态对回去
            case nameof(MainViewModel.ScopeTasks):
                SyncTaskSelection();
                break;
        }
    }

    public MainViewModel VM => App.ViewModel;

    // ---------------- 左：执行通道 ----------------

    private void OnScopeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ScopeList.SelectedItem is not ChannelScope scope)
        {
            return;
        }

        VM.SelectedScope = scope;

        // 切通道后默认看这条通道的第一个任务 —— 沿用上一条通道的选中项会指向别的通道，看着像串台
        VM.SelectedTask = VM.ScopeTasks.FirstOrDefault();
    }

    /// <summary>
    /// 通道行右侧的开关：确认这条通道本轮要不要执行。
    ///
    /// 走 <c>Toggled</c> 而不是纯 <c>x:Bind</c> 两向绑定 —— <see cref="ChannelScope"/> 只是展示模型，
    /// 真正要改的是通道配置里的 <c>enabled</c>（「通道管理」页那个开关同一份），必须由 VM 落盘。
    /// 列表项实体化时 x:Bind 也会填一次 IsOn 触发本事件，VM 里对「值没变」做了空转处理。
    /// </summary>
    private void OnScopeEnabledToggled(object sender, RoutedEventArgs e)
    {
        // ⚠️ 必须读控件自己的 IsOn，不能读 scope.Enabled —— Toggled 早于 TwoWay 绑定回写，
        //    这时 scope.Enabled 还是**旧值**。读旧值的症状：把开关拨到"开"，配置却仍是停用，
        //    行上还挂着「通道已停用 —— 本轮不会下发」（2026-10-24 用户实测就是这个）。
        if (sender is ToggleSwitch toggle && toggle.DataContext is ChannelScope scope)
        {
            VM.SetScopeEnabled(scope, toggle.IsOn);
        }
    }

    /// <summary>去「会话通道」父项下的「通道管理…」子项（增删通道、填凭据）。</summary>
    private void OnOpenChannels(object sender, RoutedEventArgs e) => VM.Navigate("channel-mgmt");

    // ---------------- 中：通道内的任务 ----------------

    private void OnAddTask(object sender, RoutedEventArgs e) => VM.AddTaskToScope();

    private void OnDeleteTask(object sender, RoutedEventArgs e) => VM.DeleteSelectedTask();

    private void OnMoveUp(object sender, RoutedEventArgs e) => VM.MoveSelectedTaskInScope(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => VM.MoveSelectedTaskInScope(1);

    // ---------------- 中：拖放排序（抓手拖动） ----------------

    /// <summary>
    /// 拖动状态。除了「被拖的是谁」，还要记住**起拖那一刻的行布局**：
    /// 拖动期间我们不碰集合，所以每行的布局位置是不变的 —— 缓存一次，
    /// 之后所有落点计算都是纯算术，不用再反复测量（测量还会被位移干扰）。
    /// </summary>
    private TaskConfig? _dragTask;
    private FrameworkElement? _dragContainer;
    private TranslateTransform? _dragTranslate;
    private uint _dragPointerId;
    private bool _dragActive;
    private double _dragStartY;
    private double _dragGrabOffset;
    private double _lastPointerY;
    private double _dragSlotTop;
    private double _rowHeight;
    private double[] _rowTops = Array.Empty<double>();
    private int _dragFrom = -1;
    private int _dropTarget = -1;
    private int _shiftedTarget = -1;

    /// <summary>被「让位」视觉推开的行 → 它身上的位移变换（拖动结束要整批还原）。</summary>
    private readonly Dictionary<FrameworkElement, TranslateTransform> _rowTransforms = new();

    /// <summary>
    /// 正在播的位移动画。Storyboard 必须自己保活：只写局部变量的话可能播到一半被回收，
    /// 表现就是"偶尔动一下、偶尔瞬移"。
    /// </summary>
    private readonly List<Storyboard> _activeAnimations = new();

    /// <summary>
    /// 抓手按下：抢在 ListViewItem 前面捕获指针。
    ///
    /// 之前两版都栽在这里：① 抓手挂 `CanDrag` 自己做 DragStarting/Drop —— 指针被
    /// ListViewItem 的按下/选择逻辑接走，子元素的拖拽手势根本不启动；② 改用 ListView
    /// 内置重排（`CanReorderItems` + `ReorderMode`）—— 同样按不动。
    ///
    /// 捕获挂在 **`TaskList`** 上而不是抓手/行容器上：拖动中容器可能被回收重建，
    /// 挂在它身上会连带丢失捕获、拖动当场断开。`e.Handled = true` 则挡住 ListViewItem
    /// 自己的按下处理（否则它随后又把指针抢回去）。
    ///
    /// 代价：点抓手不再顺带选中那一行 —— 抓手只有二十来像素宽，点行内文字照常选中。
    /// </summary>
    private void OnTaskGripPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement grip || grip.DataContext is not TaskConfig task)
        {
            return;
        }

        _dragTask = task;
        _dragPointerId = e.Pointer.PointerId;
        _dragStartY = e.GetCurrentPoint(TaskList).Position.Y;
        _lastPointerY = _dragStartY;
        _dragFrom = VM.ScopeTasks.IndexOf(task);
        _dropTarget = _dragFrom;
        _dragActive = false;

        TaskList.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// 移动：过 4px 阈值才算起拖，之后 ① 被拖的行追着指针走；② 其余行按落点上下让位。
    ///
    /// ⚠️ **全程不改集合**。改一次 ListView 就会回收/重建那一行的容器，位移和捕获一起失效
    /// （症状：拖过一行就断开、直接跳到新位置）。"让位"是纯视觉的 —— 给别的行加
    /// <c>TranslateTransform</c>，布局一个像素都没动。真正的排序只在松手时做一次。
    /// </summary>
    private void OnTaskGripPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragTask is null || e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        _lastPointerY = e.GetCurrentPoint(TaskList).Position.Y;

        if (!_dragActive)
        {
            if (Math.Abs(_lastPointerY - _dragStartY) < 4)
            {
                return;
            }

            if (!BeginDragVisual())
            {
                return;
            }

            _dragActive = true;
        }

        FollowPointer();
        UpdateDropTarget();
        e.Handled = true;
    }

    private void OnTaskGripPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragTask is null || e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        var task = _dragTask;
        var moved = _dragActive;
        var target = _dropTarget;

        EndDrag(e);

        // 松手才排序：一整段拖动只改一次集合
        if (moved)
        {
            VM.MoveTaskInScopeTo(task, target);
        }
    }

    /// <summary>
    /// 捕获真丢了（Esc、窗口失焦、被别的元素抢走）才收尾。
    /// 拖动中容器被回收**不会**再走到这里 —— 捕获在 ListView 上，容器挪动不影响它。
    /// </summary>
    private void OnDragCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragTask is not null)
        {
            EndDrag(null);
        }
    }

    // ---------------- 拖动的内部逻辑 ----------------

    /// <summary>起拖：挂位移变换、压暗、抬到兄弟之上，并把此刻的行布局缓存下来。</summary>
    private bool BeginDragVisual()
    {
        if (_dragTask is null)
        {
            return false;
        }

        _dragContainer = TaskList.ContainerFromItem(_dragTask) as FrameworkElement;
        if (_dragContainer is null)
        {
            return false;
        }

        _dragTranslate = new TranslateTransform();
        _dragContainer.RenderTransform = _dragTranslate;
        _dragContainer.Opacity = 0.95;
        Canvas.SetZIndex(_dragContainer, 100);

        // 位移此刻还是 0，量到的是真实槽位；抓取偏移按它算，之后一路不变量
        _dragSlotTop = TopOf(_dragContainer);
        _dragGrabOffset = _lastPointerY - _dragSlotTop;
        _rowHeight = _dragContainer.ActualHeight;

        var count = TaskList.Items.Count;
        var tops = new double[count];
        for (var i = 0; i < count; i++)
        {
            tops[i] = TaskList.ContainerFromIndex(i) is FrameworkElement row ? TopOf(row) : double.NaN;
        }

        _rowTops = tops;
        _shiftedTarget = -1;
        return true;
    }

    /// <summary>
    /// 让被拖的行跟住指针：维持「行顶 = 指针 − 抓取偏移」。
    ///
    /// 直接用起拖时缓存的槽位算，不再测量 —— `TransformToVisual` 会把元素自身的
    /// RenderTransform 算进去，边量边改就成了「位移取决于位移」的自指，会越拖越飘。
    /// </summary>
    private void FollowPointer()
    {
        if (!_dragActive || _dragTranslate is null)
        {
            return;
        }

        _dragTranslate.Y = _lastPointerY - _dragGrabOffset - _dragSlotTop;
    }

    /// <summary>
    /// 算落点 + 让别的行让位 + 画提示线。全部按起拖时缓存的行位置算，纯算术。
    /// </summary>
    private void UpdateDropTarget()
    {
        if (_dragTask is null || _dragFrom < 0)
        {
            return;
        }

        var count = _rowTops.Length;
        if (count == 0)
        {
            return;
        }

        var insertBefore = count;
        for (var i = 0; i < count; i++)
        {
            if (i == _dragFrom || double.IsNaN(_rowTops[i]))
            {
                continue;
            }

            if (_lastPointerY < _rowTops[i] + _rowHeight / 2)
            {
                insertBefore = i;
                break;
            }
        }

        // 「插到第 i 行之前」抽走被拖项之后要减一，否则往后拖永远差一位
        _dropTarget = insertBefore >= count
            ? count - 1
            : insertBefore > _dragFrom ? insertBefore - 1 : insertBefore;

        ShiftRows(_dragFrom, _dropTarget);

        if (_dropTarget == _dragFrom)
        {
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }

        var y = insertBefore >= count
            ? _rowTops[count - 1] + _rowHeight
            : _rowTops[insertBefore];

        DropLine.Margin = new Thickness(1, Math.Max(0, y), 1, 0);
        DropLine.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 让位：被拖行原位置与落点之间的其它行整体上移/下移一个行高，**带补间**。
    ///
    /// 只加 `RenderTransform`，布局与集合都不动 —— 这是拖动期间唯一既安全又能出效果的做法。
    /// 目标值没变的行直接跳过，避免每次指针移动都重建动画。
    /// </summary>
    private void ShiftRows(int from, int to)
    {
        if (to == _shiftedTarget)
        {
            return;
        }

        _shiftedTarget = to;

        var up = to > from;
        var low = Math.Min(from, to);
        var high = Math.Max(from, to);

        for (var i = 0; i < _rowTops.Length; i++)
        {
            if (i == from || TaskList.ContainerFromIndex(i) is not FrameworkElement row)
            {
                continue;
            }

            // 落在区间内的行让开一个行高，其余（包括退出区间的）回原位
            var target = to != from && i >= low && i <= high ? (up ? -_rowHeight : _rowHeight) : 0.0;
            MoveRowTo(row, target);
        }
    }

    /// <summary>把某一行推到 <paramref name="target"/>（0 = 原位），带补间。</summary>
    private void MoveRowTo(FrameworkElement row, double target)
    {
        if (!_rowTransforms.TryGetValue(row, out var transform))
        {
            if (Math.Abs(target) < 0.5)
            {
                return;   // 本来就在原位，不用白建一个变换
            }

            transform = new TranslateTransform();
            row.RenderTransform = transform;
            _rowTransforms[row] = transform;
        }

        AnimateShift(transform, target);
    }

    /// <summary>
    /// `TranslateTransform.Y` 的补间。
    ///
    /// 它是**独立动画**（不触发布局），所以不加 `EnableDependentAnimation` 也能跑；
    /// 只给 `To`，起始值就是当前值，连续改变落点时能顺滑接力。
    /// </summary>
    private void AnimateShift(TranslateTransform transform, double target)
    {
        if (Math.Abs(transform.Y - target) < 0.5)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(120)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "Y");

        var board = new Storyboard();
        board.Children.Add(animation);
        board.Completed += (_, _) =>
        {
            _activeAnimations.Remove(board);

            // 播完显式落值：HoldEnd 会让动画值长期压住属性，之后谁再改都不生效
            transform.Y = target;
        };

        _activeAnimations.Add(board);
        board.Begin();
    }

    /// <summary>整批还原：停掉动画、把位移归零、摘掉变换。</summary>
    private void ClearRowTransforms()
    {
        foreach (var board in _activeAnimations.ToList())
        {
            board.Stop();
        }

        _activeAnimations.Clear();

        foreach (var pair in _rowTransforms)
        {
            pair.Value.Y = 0;
            pair.Key.RenderTransform = null;
        }

        _rowTransforms.Clear();
        _shiftedTarget = -1;
    }

    /// <summary>收尾：摘位移、还原让位、恢复不透明度与层级、隐藏提示线。排序在调用方做。</summary>
    private void EndDrag(PointerRoutedEventArgs? e)
    {
        if (_dragContainer is not null)
        {
            _dragContainer.RenderTransform = null;
            _dragContainer.Opacity = 1;
            Canvas.SetZIndex(_dragContainer, 0);
        }

        ClearRowTransforms();

        // 先清状态再放捕获：ReleasePointerCapture 会同步抛 PointerCaptureLost，
        // 那时若 _dragTask 还在，就会又走一遍 EndDrag。
        _dragTask = null;
        _dragContainer = null;
        _dragTranslate = null;
        _dragActive = false;
        _dragFrom = -1;
        _rowTops = Array.Empty<double>();
        DropLine.Visibility = Visibility.Collapsed;

        if (e is not null)
        {
            TaskList.ReleasePointerCapture(e.Pointer);
        }
    }

    private double TopOf(FrameworkElement element) =>
        element.TransformToVisual(TaskList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        VM.SelectedTask = TaskList.SelectedItem as TaskConfig;
    }

    // ---------------- 右：任务详情 ----------------

    /// <summary>
    /// 「移到通道…」菜单：点击时现填 —— 通道随时可能被增删改，写死必然过期。
    ///
    /// 顺带说明为什么它是**代码里 new** 的而不是写在 XAML 里：放在 <c>Page.Resources</c> 并带
    /// x:Name 时，XAML 生成的 <c>Connect()</c> 会把具名资源的连接序号与后面控件的序号错位，
    /// 页面一构造就抛 <c>InvalidCastException</c>（把 Flyout 当 Button 转型）—— 页面根本进不去。
    /// </summary>
    private void OnMoveToChannelClick(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();

        foreach (var scope in VM.ChannelScopes)
        {
            if (VM.SelectedScope is not null &&
                string.Equals(scope.Id, VM.SelectedScope.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var id = scope.Id;
            var item = new MenuFlyoutItem { Text = scope.Name };
            item.Click += (_, _) => VM.MoveSelectedTaskToScope(id);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = "没有别的通道", IsEnabled = false });
        }

        flyout.ShowAt((FrameworkElement)sender);
    }

    // ---------------- 选中态同步 ----------------

    private void SyncScopeSelection()
    {
        _syncing = true;
        try
        {
            // 同 SyncTaskSelection：ChannelScopes 每次刷新都被整体重建，
            // 旧引用必然不在集合里 —— 绝不能把它直接塞给 ListView.SelectedItem。
            var scope = VM.SelectedScope;
            if (scope is null || !VM.ChannelScopes.Contains(scope))
            {
                ScopeList.SelectedItem = null;
            }
            else
            {
                ScopeList.SelectedItem = scope;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncTaskSelection()
    {
        _syncing = true;
        try
        {
            // ⚠️ 赋一个「不在 ItemsSource 里」的对象给 ListView.SelectedItem，
            //    WinUI 会反复测量/布局失效把 UI 线程拖进死循环（2026-10-04 导入配置后卡死）。
            //    集合刚被整体替换（增删 / 排序 / 改派 / 导入）时旧引用必然已失效，
            //    所以这里先置空再设 —— 让列表始终回到一个自洽的选中态。
            var selected = VM.SelectedTask;
            if (selected is null || !VM.ScopeTasks.Contains(selected))
            {
                TaskList.SelectedItem = null;
            }
            else
            {
                TaskList.SelectedItem = selected;
            }
        }
        finally
        {
            _syncing = false;
        }

        ScopeEmptyState.Visibility = VM.ScopeTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncForm()
    {
        _syncing = true;
        try
        {
            var task = VM.SelectedTask;
            FormHost.DataContext = task;

            var hasSelection = task is not null;
            FormHost.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
            OwnerBar.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

            if (task is null)
            {
                return;
            }

            TimeoutBox.Value = task.TimeoutMinutes;
            ConfirmEnterBox.Value = task.ConfirmEnterDelaySeconds;
            SelectByKey(TimeoutActionBox, task.TimeoutAction);
            SelectByKey(WaitModeBox, task.WaitMode);
            SelectByKey(ConcurrentPolicyBox, task.ConcurrentPolicy);

            // 换任务或首次进入时刷新启动参数候选：候选依赖脚本路径，路径没变则走缓存（不重复读盘）。
            ReloadArgsForCurrentTask(autoFillPrimary: false);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void SelectByKey(ComboBox box, string key)
    {
        if (box.ItemsSource is not IEnumerable<KeyValuePair<string, string>> items)
        {
            return;
        }

        var index = 0;
        foreach (var item in items)
        {
            if (item.Key == key)
            {
                box.SelectedIndex = index;
                return;
            }

            index++;
        }

        box.SelectedIndex = 0;
    }

    private static string? SelectedKey(ComboBox box, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is KeyValuePair<string, string> pair)
        {
            return pair.Key;
        }

        return null;
    }

    private void OnTimeoutChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || VM.SelectedTask is null)
        {
            return;
        }

        VM.SelectedTask.TimeoutMinutes = (int)Math.Round(double.IsNaN(args.NewValue) ? 0 : args.NewValue);
    }

    private void OnConfirmEnterChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || VM.SelectedTask is null)
        {
            return;
        }

        VM.SelectedTask.ConfirmEnterDelaySeconds = (int)Math.Round(double.IsNaN(args.NewValue) ? 0 : args.NewValue);
    }

    private void OnTimeoutActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || VM.SelectedTask is null)
        {
            return;
        }

        var key = SelectedKey((ComboBox)sender, e);
        if (key is not null)
        {
            VM.SelectedTask.TimeoutAction = key;
        }
    }

    private void OnWaitModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || VM.SelectedTask is null)
        {
            return;
        }

        var key = SelectedKey((ComboBox)sender, e);
        if (key is not null)
        {
            VM.SelectedTask.WaitMode = key;
        }
    }

    private void OnConcurrentPolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || VM.SelectedTask is null)
        {
            return;
        }

        var key = SelectedKey((ComboBox)sender, e);
        if (key is not null)
        {
            VM.SelectedTask.ConcurrentPolicy = key;
        }
    }

    // ---------------- 启动命令 / 参数：可写可下拉（AutoSuggestBox） ----------------
    //
    // ⚠️ 这块**曾经**用 `ComboBox IsEditable="True"`，实测"字能打上、一离开就变回空"：
    //    可编辑 ComboBox 的 Text 不随输入实时提交（只在回车/选项变化时更新），
    //    所以"内部 TextBox 的 TextChanged + 失焦保存"两条补救路径都必须成立才不丢字；
    //    再加上整体替换 ItemsSource 会清空 Text，回填又只能手写 —— 三个坑叠在一起。
    //    换成 AutoSuggestBox 后 Text 是普通 DP、双向绑到 Args，输入即落库，
    //    换脚本时由绑定负责恢复，不再需要任何"回填"代码。**不要再换回 ComboBox。**

    /// <summary>当前脚本可选的启动参数（换脚本时整体替换 —— 候选集本来就随脚本变）。</summary>
    private List<LaunchChoice> _argsChoices = new();

    /// <summary>识别依据（「已识别：M9A（依据 interface.json）」），没识别到就是兜底文案。</summary>
    private string _argsBasis = string.Empty;

    /// <summary>项目自己要求"手写补充"的说明（多实例、配置名之类）。</summary>
    private string _argsManualHint = string.Empty;

    /// <summary>
    /// 本次识别出的日志推荐值（勾了「同时套用推荐设置」或点「套用推荐值」才写进任务）。
    /// 为 null 表示没识别到 / 该项目没收录，此时不碰日志字段。
    /// </summary>
    private LaunchLogPlan? _argsLogPlan;

    /// <summary>
    /// 防重入：程序自己套用推荐值时，别把控件的变化当成"用户改了"。
    ///
    /// <para>与 <see cref="_suppressArgsEvents"/> 同类，但用途相反：那一个是防"用户操作被程序误判"，
    /// 这一个是防"程序写值被当成用户手改" —— 一旦误判，<c>ExternalLogTouched</c> 会被置位，
    /// 之后预设永远不再更新，用户会以为功能坏了。</para>
    /// </summary>
    private bool _suppressExternalLogEvents;

    /// <summary>防重入：程序自己换候选时不要把相关事件当成用户操作。</summary>
    private bool _suppressArgsEvents;

    /// <summary>箭头按钮弹出的候选列表（每次点都重建，用完即弃）。</summary>
    private Flyout? _argsFlyout;

    private void OnArgsBoxLoaded(object sender, RoutedEventArgs e) =>
        ReloadArgsForCurrentTask(autoFillPrimary: false);

    /// <summary>
    /// 用户输入。Args 已经由 <c>Text="{Binding Args, Mode=TwoWay}"</c> 落库，
    /// 这里只负责刷新说明行（命中 / 未命中的提示）。
    /// </summary>
    private void OnArgsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressArgsEvents)
        {
            return;
        }

        RefreshArgsHint();
    }

    /// <summary>
    /// 箭头按钮：把该脚本已知的启动参数列出来。
    ///
    /// 为什么自己 Show 一个 Flyout，而不是用 ComboBox / AutoSuggestBox 自带的下拉：
    /// 那两个控件的候选列表开合由它们内部决定（AutoSuggestBox 在文本为空时会主动关掉列表，
    /// 点箭头也弹不出来）；这里只有几项，开合完全该由我们说了算。
    /// </summary>
    private void OnArgsDropClick(object sender, RoutedEventArgs e)
    {
        var task = VM.SelectedTask;
        if (task is null)
        {
            return;
        }

        if (_argsChoices.Count == 0)
        {
            // 没有候选也要给个交代，不能点了没反应
            var empty = new Flyout
            {
                Content = new TextBlock
                {
                    Text = _argsBasis.Length > 0 ? _argsBasis : "这个脚本没有可选的启动参数，直接手写即可。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 320,
                },
            };
            empty.ShowAt(ArgsDropButton);
            return;
        }

        var panel = new StackPanel { Spacing = 2, MinWidth = 340 };
        foreach (var choice in _argsChoices)
        {
            var line = new StackPanel { Spacing = 1 };
            line.Children.Add(new TextBlock
            {
                Text = choice.Args.Length > 0 ? choice.Args : "（不带参数）",
                FontFamily = new FontFamily("Consolas"),
            });
            line.Children.Add(new TextBlock
            {
                Text = choice.Detail,
                FontSize = 11,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
            });

            var item = new Button
            {
                Tag = choice,
                Content = line,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
            };
            item.Click += OnArgsChoiceClick;
            panel.Children.Add(item);
        }

        _argsFlyout = new Flyout
        {
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
        };
        _argsFlyout.ShowAt(ArgsDropButton);
    }

    private void OnArgsChoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LaunchChoice choice })
        {
            return;
        }

        ApplyChoice(choice);
        _argsFlyout?.Hide();
        _argsFlyout = null;
    }

    private void OnApplyRecommendedChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressArgsEvents || ApplyRecommendedCheck.IsChecked != true)
        {
            // 取消勾选不回滚：用户可能刚手调过完成判断，自动还原会把他的手改冲掉。
            return;
        }

        var task = VM.SelectedTask;
        var match = task is null ? null : LaunchPresetResolver.Match(_argsChoices, task.Args);
        if (match is not null)
        {
            ApplyChoice(match);
        }
    }

    /// <summary>把一条候选写进任务；界面上的 Text 会跟着 Args 由绑定更新。</summary>
    private void ApplyChoice(LaunchChoice choice)
    {
        var task = VM.SelectedTask;
        if (task is null)
        {
            return;
        }

        LaunchPresetResolver.Apply(
            choice, task, ApplyRecommendedCheck.IsChecked == true, _argsLogPlan, LaunchPresetStore.Library.Rev);

        RefreshArgsHint();
        RefreshLogProfileHint();
    }

    /// <summary>
    /// 重新识别 + 换候选。
    /// ⚠️ 这里**不碰 Text** —— 双向绑定负责在换任务时把 Args 显示出来；
    /// 只有"Args 为空"时才主动预填一条主推项（Apply 写 Args，绑定自然跟着变）。
    /// </summary>
    private void ReloadArgsForCurrentTask(bool autoFillPrimary)
    {
        var task = VM.SelectedTask;
        var saved = _suppressArgsEvents;
        _suppressArgsEvents = true;
        try
        {
            _argsChoices = new List<LaunchChoice>();
            _argsBasis = string.Empty;
            _argsManualHint = string.Empty;
            _argsLogPlan = null;

            var path = (task?.ScriptPath ?? string.Empty).Trim();
            ArgsBox.IsEnabled = task is not null && path.Length > 0;
            ApplyRecommendedCheck.IsEnabled = task is not null;
            // 没有候选时把箭头也停掉 —— 按钮点了没反应比灰着更让人困惑
            ArgsDropButton.IsEnabled = ArgsBox.IsEnabled;

            if (task is null || path.Length == 0)
            {
                _argsChoices = new List<LaunchChoice>();
            }
            else
            {
                var (resolution, profile) = LaunchPresetStore.Resolve(path);
                _argsChoices = LaunchPresetResolver.BuildChoices(resolution, profile, out var manualHint);
                _argsBasis = resolution.Basis;
                _argsManualHint = manualHint;
                _argsLogPlan = LaunchPresetResolver.ResolveLog(
                    LaunchPresetStore.Library, resolution, profile, path);
                ArgsDropButton.IsEnabled = _argsChoices.Count > 0;

                // 只在参数为空时才预填主推项：绝不因为换了脚本就重写用户已经填好的参数。
                if (autoFillPrimary && string.IsNullOrWhiteSpace(task.Args))
                {
                    var primary = LaunchPresetResolver.Primary(_argsChoices);
                    if (primary is not null)
                    {
                        LaunchPresetResolver.Apply(primary, task, ApplyRecommendedCheck.IsChecked == true,
                            _argsLogPlan, LaunchPresetStore.Library.Rev);
                    }
                }
            }
        }
        finally
        {
            _suppressArgsEvents = saved;
        }

        RefreshArgsHint();
        RefreshLogProfileHint();
    }

    // ---------------------------------------------------------------- 外部日志推荐值

    /// <summary>
    /// 把推荐值写进任务的日志五项。
    ///
    /// <para>为什么写之前先清 <c>ExternalLogTouched</c>：点这个按钮的语义就是"回到推荐值"，
    /// 若保留"已自定义"标记，下一次重算又不会更新，用户会以为按钮没生效。</para>
    ///
    /// <para>无日志项目 / 目录没探测到：一个字段都不写，只刷状态行说明原因 ——
    /// 写一个不存在的路径会静默采集不到，比不启用更难查。</para>
    /// </summary>
    private void ApplyLogPlan()
    {
        var task = VM.SelectedTask;
        if (task is null || _argsLogPlan is null || !_argsLogPlan.Enabled || !_argsLogPlan.HasDirectory)
        {
            RefreshLogProfileHint();
            return;
        }

        var saved = _suppressExternalLogEvents;
        _suppressExternalLogEvents = true;
        try
        {
            task.ExternalLogTouched = false;
            task.ExternalLogEnabled = true;
            task.ExternalLogPath = _argsLogPlan.Directory;
            task.ExternalLogPattern = _argsLogPlan.Pattern;
            task.ExternalLogEncoding = _argsLogPlan.Encoding;
            task.ExternalLogFollowTail = _argsLogPlan.FollowTail;
            task.ExternalLogRecursive = _argsLogPlan.Recursive;
            if (_argsLogPlan.Keywords.Count > 0)
            {
                task.ExternalLogKeywords = new List<string>(_argsLogPlan.Keywords);
            }

            task.ExternalLogPresetId = _argsLogPlan.PresetId;
            task.ExternalLogRev = LaunchPresetStore.Library.Rev;
        }
        finally
        {
            _suppressExternalLogEvents = saved;
        }

        RefreshLogProfileHint();
    }

    private void OnApplyLogPresetClick(object sender, RoutedEventArgs e) => ApplyLogPlan();

    private void OnRestoreLogPresetClick(object sender, RoutedEventArgs e) => ApplyLogPlan();

    /// <summary>用户在路径 / 模式 / 关键字上编辑结束，或在「末尾开始跟踪」上动过。</summary>
    private void OnExternalLogFieldEdited(object sender, RoutedEventArgs e) => MarkExternalLogTouched();

    private void OnExternalLogEncodingChanged(object sender, SelectionChangedEventArgs e) => MarkExternalLogTouched();

    /// <summary>
    /// 标记「已自定义」——**只在值真的与推荐值不同时才标**。
    ///
    /// <para>为什么不无条件标：焦点进出输入框也会触发 LostFocus。
    /// 若照单全收，用户只是点进去看了一眼，<c>ExternalLogTouched</c> 就被置位，
    /// 之后换脚本再也不更新推荐值，而界面上还会显示"已自定义" —— 看起来像功能坏了。</para>
    /// </summary>
    private void MarkExternalLogTouched()
    {
        if (_suppressExternalLogEvents)
        {
            return;
        }

        var task = VM.SelectedTask;
        if (task is null || task.ExternalLogTouched)
        {
            return;
        }

        if (_argsLogPlan is not null && SameAsPlan(task, _argsLogPlan))
        {
            return;
        }

        task.ExternalLogTouched = true;
        RefreshLogProfileHint();
    }

    private static bool SameAsPlan(TaskConfig task, LaunchLogPlan plan) =>
        string.Equals((task.ExternalLogPath ?? string.Empty).Trim(), plan.Directory ?? string.Empty,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(ExternalLogDefaults.NormalizePattern(task.ExternalLogPattern), plan.Pattern,
            StringComparison.Ordinal)
        && string.Equals(ExternalLogEncodings.Normalize(task.ExternalLogEncoding), plan.Encoding,
            StringComparison.Ordinal)
        && task.ExternalLogFollowTail == plan.FollowTail
        && task.ExternalLogRecursive == plan.Recursive;

    /// <summary>状态行 + 两个按钮的可用性，全部由"识别结果 + 是否已自定义"推导。</summary>
    private void RefreshLogProfileHint()
    {
        var task = VM.SelectedTask;
        ApplyLogPresetButton.IsEnabled = false;
        RestoreLogPresetButton.Visibility = Visibility.Collapsed;

        if (task is null)
        {
            LogProfileHint.Text = "选完脚本路径后，这里会显示识别出的项目与推荐日志位置";
            return;
        }

        if (_argsLogPlan is null)
        {
            LogProfileHint.Text = "未收录这个脚本，日志位置请手动填写";
            return;
        }

        if (!_argsLogPlan.Enabled)
        {
            LogProfileHint.Text = $"该项目不落盘日志：{_argsLogPlan.Summary}";
            return;
        }

        var target = _argsLogPlan.HasDirectory
            ? DescribeLogTarget(task, _argsLogPlan)
            : "（未找到推荐目录，需手填）";

        if (_argsLogPlan.HasDirectory)
        {
            ApplyLogPresetButton.IsEnabled = true;
        }

        if (task.ExternalLogTouched)
        {
            LogProfileHint.Text = $"已自定义（推荐值：{target}）";
            RestoreLogPresetButton.Visibility = Visibility.Visible;
            return;
        }

        var name = string.IsNullOrWhiteSpace(_argsLogPlan.PresetName) ? string.Empty : $" · {_argsLogPlan.PresetName}";
        LogProfileHint.Text = $"{_argsLogPlan.Basis}{name}：{target}";
    }

    /// <summary>
    /// 「目录 + 模式」的显示形态：能相对脚本目录就相对（<c>logs · 模式 log-YYYYMMDD.log</c>），
    /// 否则退回绝对路径。整条 <c>C:\Program Files\...</c> 会把这行挤爆。
    /// </summary>
    private static string DescribeLogTarget(TaskConfig task, LaunchLogPlan plan)
    {
        var directory = plan.Directory;
        try
        {
            var exeDirectory = Path.GetDirectoryName((task.ScriptPath ?? string.Empty).Trim());
            if (!string.IsNullOrEmpty(exeDirectory)
                && directory.StartsWith(exeDirectory, StringComparison.OrdinalIgnoreCase))
            {
                directory = directory.Substring(exeDirectory.Length).TrimStart('\\', '/');
            }
        }
        catch (Exception)
        {
            // 路径非法就用原样，显示层不该抛
        }

        return directory.Length == 0 ? plan.Pattern : $"{directory} · 模式 {plan.Pattern}";
    }

    /// <summary>
    /// 说明行 + 完整命令行。
    /// 命中判定**以 Args 的文本反查为准**（不用选中项：手写时压根没有选中项）。
    /// </summary>
    private void RefreshArgsHint()
    {
        var task = VM.SelectedTask;
        if (task is null)
        {
            ArgsHintText.Text = string.Empty;
            ArgsCommandText.Text = string.Empty;
            return;
        }

        var text = (task.Args ?? string.Empty).Trim();
        var match = LaunchPresetResolver.Match(_argsChoices, text);
        var parts = new List<string>();

        // ⚠️ 先判命中、后判空：有项目的主推方式就是"不带参数启动"（MaaEnd 依赖自己的设置项），
        //    这种情况空参数**也是**一条命中的预设，不能落到通用的"可留空"提示上，
        //    否则用户选完看不到那条方式的前置条件。
        if (match is not null)
        {
            if (match.Summary.Length > 0)
            {
                parts.Add(match.Summary);
            }

            if (match.Precondition.Length > 0)
            {
                parts.Add(match.Precondition);
            }

            if (match.Volatile)
            {
                parts.Add("参数里的序号随上游版本变化，建议先按界面任务顺序核对。");
            }

            if (!match.Verified)
            {
                parts.Add("该参数未在项目文档中确认。");
            }
        }
        else if (text.Length == 0)
        {
            parts.Add("可留空：留空表示不带任何参数启动。");
        }
        else
        {
            parts.Add("未收录的参数，请确认拼写。");
            var nearest = LaunchPresetResolver.Nearest(_argsChoices, text);
            if (nearest is not null)
            {
                parts.Add($"最接近的已知参数：{nearest.Args}");
            }

            if (text.Contains('{'))
            {
                parts.Add("含 {…} 占位符，运行时会被原样传给程序，记得换成实际名字。");
            }
        }

        if (text.Length > 0 && !task.UseArgs)
        {
            parts.Add("注意：「启用命令 / 参数」是关闭状态，这些参数不会生效。");
        }

        // 候选为空时把「识别依据」补上 —— 那是用户唯一的线索来源。
        if (_argsChoices.Count == 0 && _argsBasis.Length > 0)
        {
            parts.Add(_argsBasis);
        }

        if (_argsManualHint.Length > 0)
        {
            parts.Add(_argsManualHint);
        }

        ArgsHintText.Text = string.Join(" ", parts);
        ArgsHintText.Foreground = text.Length > 0 && match is null
            ? ThemeBrush("SystemFillColorCautionBrush") ?? ArgsHintText.Foreground
            : ThemeBrush("TextFillColorSecondaryBrush") ?? ArgsHintText.Foreground;

        RefreshArgsCommand(task, text);
    }

    private void RefreshArgsCommand(TaskConfig task, string text)
    {
        var path = (task.ScriptPath ?? string.Empty).Trim();
        if (path.Length == 0 || !task.UseArgs)
        {
            ArgsCommandText.Text = task.UseArgs ? string.Empty : "（命令 / 参数未启用，只启动脚本本身）";
            return;
        }

        try
        {
            var (fileName, arguments) = CommandLine.BuildTaskCommand(path, task.UseArgs, text);
            var exe = Path.GetFileName(fileName);
            ArgsCommandText.Text = arguments.Length > 0
                ? $"完整命令：\"{exe}\" {arguments}"
                : $"完整命令：\"{exe}\"（无参数）";
        }
        catch (Exception)
        {
            ArgsCommandText.Text = string.Empty;
        }
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private async void OnBrowseScript(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedTask is null)
        {
            return;
        }

        var path = await DialogHelper.PickOpenFileAsync(
            ("脚本文件", ".exe"),
            ("脚本文件", ".bat"),
            ("脚本文件", ".cmd"),
            ("脚本文件", ".py"),
            ("所有文件", "."));

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        VM.SelectedTask.ScriptPath = path;

        if (string.IsNullOrWhiteSpace(VM.SelectedTask.Name) || VM.SelectedTask.Name.StartsWith("新任务", StringComparison.Ordinal))
        {
            VM.SelectedTask.Name = Path.GetFileNameWithoutExtension(path);
        }

        // 选了新脚本 = 候选集可能整个换掉：重新识别、换 ItemsSource、回填文本，
        // 并在参数为空时预填主推项（"选完脚本就能跑"）。
        ReloadArgsForCurrentTask(autoFillPrimary: true);
    }
}
