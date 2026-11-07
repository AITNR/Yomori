// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Yomori.Services;

namespace Yomori.Helpers;

/// <summary>
/// 文件选择器与消息框封装。
/// 必须用 Windows App SDK 1.8 的新选择器（Microsoft.Windows.Storage.Pickers）：
/// 旧的 Windows.Storage.Pickers 在"以管理员身份运行"的进程里会静默失败（点了没反应），
/// 对过滤器格式也苛刻（"." 这类写法直接抛异常，被 async void 吞掉后同样表现为没反应）。
/// 新 API 通过 WindowId 关联窗口，且不设过滤器时默认显示所有文件，两个坑都没有。
///
/// 【对话框纪律】WinUI 同一时刻只允许一个 ContentDialog 打开：第二个 <c>ShowAsync</c> 直接抛
/// COMException（"Only a single ContentDialog can be open at any time"）。而工程里大量调用是
/// <c>_ = DialogHelper.ShowMessageAsync(...)</c> 这种"发了不管"的写法 —— 异常没人接，
/// 未观察异常直接掀掉进程。所以**所有** ContentDialog 都必须从 <see cref="ShowAsync"/> 走：
/// 排队进入 + 异常就地吞掉落盘，绝不再让调用方拿到异常。
/// </summary>
public static class DialogHelper
{
    /// <summary>全局对话框闸门：保证同一时刻只有一个 ContentDialog 存活。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// 把当前续体搬回 UI 线程（无返回值）。
    /// 见 <see cref="ReturnToUiThreadAsync{T}"/> 的说明。
    /// </summary>
    private static Task ToUiThreadAsync(Window window) => ReturnToUiThreadAsync(window, true);

    /// <summary>
    /// 在 UI 线程上交付选择器结果。
    ///
    /// 【为什么必须有这一句】Windows App SDK 1.8 的 <c>Microsoft.Windows.Storage.Pickers</c>
    /// 返回的 awaitable **不捕获 SynchronizationContext**，所以 <c>await picker.PickSingleFileAsync()</c>
    /// 之后的续体**不保证**还在 UI 线程上（实测落在 ThreadPool 上）。而调用方拿到路径第一件事
    /// 几乎都是写 x:Bind 属性 —— 生成的 setter 就是 <c>TextBlock.set_Text</c> 这类 WinRT 调用，
    /// 只能在对象所属的 UI 线程上碰，否则 COMException 0x8001010E (RPC_E_WRONG_THREAD)；
    /// 调用方又多是 <c>async void</c> 事件处理器，异常没人接 → 直接掀进程（无对话框、无日志）。
    ///
    /// 2026-10-04 在「导入配置」那一处栽过（栈：ImportConfigAsync → Tasks.Clear() → ClearItems()
    /// → HomePage.g.cs Update_VM_Tasks_Count → TextBlock.set_Text），当时只在那个调用点包了
    /// RunOnUiThreadAsync；2026-10-23 任务页「浏览」选脚本时同一个坑又踩一次
    /// （栈：OnBrowseScript → TaskConfig.set_ScriptPath → TasksPage.g.cs Update_ScriptPath
    /// → TextBlock.set_Text）。**护栏只护一半最坑**，所以收口在这里：
    /// 本类所有选择器的返回值一律在 UI 线程上交付，调用方不必自己再包一层。
    /// </summary>
    private static Task<T> ReturnToUiThreadAsync<T>(Window window, T value)
    {
        var queue = window.DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            return Task.FromResult(value);
        }

        // 故意不用 RunContinuationsAsynchronously：SetResult 是在 UI 线程的排队回调里触发的，
        // 续体就地跑完（仍在该 UI 线程），调用方 await 之后就是 UI 线程。
        var tcs = new TaskCompletionSource<T>();
        if (!queue.TryEnqueue(() => tcs.TrySetResult(value)))
        {
            // 队列已关闭（程序正在退出）：退回原线程交付，宁可冒险也别让调用方永远挂着。
            tcs.TrySetResult(value);
        }

        return tcs.Task;
    }

    public static async Task<string?> PickSaveFileAsync(string suggestedName, params (string Label, string Pattern)[] filters)
    {
        var window = App.MainWindow;
        if (window is null)
        {
            return null;
        }

        try
        {
            var picker = new FileSavePicker(window.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
            };

            // 不设置 FileTypeChoices 时对话框本身就允许"所有文件 (*.*)"，通配项无需也不能显式添加
            foreach (var (label, pattern) in filters)
            {
                if (IsWildcard(pattern))
                {
                    continue;
                }

                picker.FileTypeChoices.Add(label, new List<string> { pattern });
            }

            var file = await picker.PickSaveFileAsync();
            return await ReturnToUiThreadAsync(window, file?.Path);
        }
        catch (Exception ex)
        {
            var detail = ex.Message;
            await ToUiThreadAsync(window);   // ContentDialog 也必须在 UI 线程上开
            await ShowMessageAsync("无法打开文件选择器", $"保存对话框出错：{detail}");
            return null;
        }
    }

    public static async Task<string?> PickOpenFileAsync(params (string Label, string Pattern)[] filters)
    {
        var window = App.MainWindow;
        if (window is null)
        {
            return null;
        }

        try
        {
            var picker = new FileOpenPicker(window.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };

            // 不设置 FileTypeFilter 时默认显示所有文件 (*.*)；通配项跳过，只保留具体扩展名的过滤
            foreach (var (_, pattern) in filters)
            {
                if (IsWildcard(pattern))
                {
                    continue;
                }

                picker.FileTypeFilter.Add(pattern);
            }

            var file = await picker.PickSingleFileAsync();
            return await ReturnToUiThreadAsync(window, file?.Path);
        }
        catch (Exception ex)
        {
            var detail = ex.Message;
            await ToUiThreadAsync(window);   // ContentDialog 也必须在 UI 线程上开
            await ShowMessageAsync("无法打开文件选择器", $"打开对话框出错：{detail}");
            return null;
        }
    }

    private static bool IsWildcard(string pattern) =>
        pattern is "." or "*" or "*.*";

    public static async Task<ContentDialogResult> ShowMessageAsync(
        string title,
        string message,
        string closeText = "知道了",
        string? primaryText = null,
        string? secondaryText = null)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = closeText,
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText,
        };

        return await ShowAsync(dialog);
    }

    /// <summary>
    /// 唯一允许打开 ContentDialog 的入口：补 XamlRoot → 排队 → 显示 → 吞异常。
    /// 调用方拿到的永远是结果（失败时是 <see cref="ContentDialogResult.None"/>），不会拿到异常。
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (dialog.XamlRoot is null && App.MainWindow?.Content?.XamlRoot is { } root)
        {
            dialog.XamlRoot = root;
        }

        // 窗口还没建好 / 已经关掉：直接放弃，别让调用方因为"没反应"再抛一次
        if (dialog.XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        await Gate.WaitAsync();
        try
        {
            // ⚠️ 这里**不能**插 await Task.Delay 之类的异步等待：WinUI 3 没有
            // SynchronizationContext，await 之后的续体不保证回界面线程，
            // 而 ContentDialog.ShowAsync() 必须在界面线程调用 —— 否则异常被下面吞掉、
            // 返回 None，表现就是「点确认后没反应」。连续弹窗的间隔由调用方负责。
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // 关键：吞掉。调用方多是 fire-and-forget，抛出去就是未观察异常 = 崩溃。
            AppPaths.WriteStartupError(ex);
            return ContentDialogResult.None;
        }
        finally
        {
            Gate.Release();
        }
    }
}
