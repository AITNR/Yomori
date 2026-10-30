// -*- coding: utf-8 -*-
using System;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Yomori.Services;

/// <summary>
/// Windows 系统通知（Toast）。
///
/// 【为什么不用 Windows App SDK 的 AppNotificationManager】
/// 原实现用 <c>AppNotificationManager.Default.Register()</c>，注释里写着"非打包模式下同样可用"。
/// 2026-10-11 在本机实测把这个前提推翻了 —— 而且失败是**完全静默**的：
///
///   · 非提权进程：Register/Show 都成功，注册表键、通知库 handler、通知记录全部落地；
///   · 提权进程（本程序 app.manifest 声明 requireAdministrator，主控端必然是提权跑的）：
///     Register/Show 都**不抛异常**，但注册表键、通知库 handler、通知记录**一个都不产生**
///     —— 通知从未真正投递。因为不抛异常，原有的 catch 降级路径也永远不会触发。
///
/// 这就是"完成通知从来没弹过、日志里也查不到原因"的真正根因：WinAppSDK 的
/// AppNotificationManager 在提权进程里静默失效（这是平台的既有限制，不是用法错误）。
///
/// 【现在改用经典 Toast API】
/// <see cref="ToastNotificationManager"/>（Win32 / Windows.UI.Notifications）在**提权与非提权
/// 两种进程**下实测都能正常投递。本程序必须提权运行（要写 Program Files 与 ProgramData），
/// 所以只有这条路可用。代价是失去 AppNotificationManager 的自带激活回调 ——
/// 而本程序本来就不需要响应通知点击（原实现注册 Invoked 回调也只是为了能发通知）。
///
/// 另外顺手把本程序的 AUMID 注册进 HKCU：这样 Toast 头部会显示"Yomori"这个应用名，
/// 而不是一串兜底文案。注册失败不影响投递，只是名字可能不好看，故只吞异常。
///
/// 重要：Toast 只在"发出通知的那个用户会话"里显示。跨用户会话场景（RDP）下，
/// 目标会话里的通知主控端用户看不到，所以通知**只由主控端发**，代理侧不发。
///
/// 通知属于"锦上添花"，任何失败都不能影响任务执行，因此全部调用都吞掉异常，
/// 由调用方降级到应用内气泡 / 日志。
/// </summary>
public static class NotificationService
{
    /// <summary>
    /// 本程序的 AppUserModelID。<b>必须与 Program.ApplyAppUserModelId 用的那一串一致</b>，
    /// 否则任务栏分组与通知归属会对不上 —— 所以两边共用这一个常量。
    /// </summary>
    public const string Aumid = "com.aitnr.yomori.app";

    /// <summary>Toast 头部显示的应用名（注册进 HKCU\...\AppUserModelId\&lt;Aumid&gt; 的 DisplayName）。</summary>
    private const string DisplayName = "Yomori";

    private static DispatcherQueue? _dispatcher;
    private static ToastNotifier? _notifier;
    private static bool _registered;
    private static bool _disabled;
    private static string _lastError = string.Empty;
    private static string _disabledStack = string.Empty;

    /// <summary>通知是否可用（注册成功且未发生致命错误）。</summary>
    public static bool IsAvailable => !_disabled && _registered;

    public static string LastError => _lastError;

    /// <summary>注册失败时的完整堆栈（排障用；成功或未注册时为空）。</summary>
    public static string DisabledDetail => _disabledStack;

    /// <summary>
    /// 一行式状态说明，供启动时写进运行日志。
    ///
    /// 为什么值得专门记一条：注册/投递失败一直是<b>静默</b>的（通知属"锦上添花"，失败只吞异常），
    /// 于是"用户说没收到完成通知"时根本分不清是没发、还是发不出。把状态写进日志，
    /// 下次翻日志就能立刻判定。
    /// </summary>
    public static string DescribeStatus()
    {
        if (_registered)
        {
            return "系统通知已就绪（经典 Toast API），可正常投递。";
        }

        if (_disabled)
        {
            var reason = _lastError.Length > 0 ? _lastError : "（无消息）";
            return $"⚠ 系统通知不可用，完成通知只会显示为应用内气泡。原因：{reason}";
        }

        return "系统通知尚未初始化。";
    }

    /// <summary>在 UI 线程调用一次。重复调用安全。</summary>
    public static void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        if (_registered || _disabled)
        {
            return;
        }

        // 旧版本在「窗口失焦时」把错过的通知落盘等回前台补发（pending_notification.json）。
        // 该机制已删除 —— 通知现在直接投递、与窗口焦点无关，这里顺手清掉升级残留。
        RemoveLegacyPendingFile();

        try
        {
            // 先给 AUMID 补一条显示名 —— 只影响 Toast 里显示的应用名，失败无所谓。
            EnsureAumidRegistered();

            _notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
            _registered = true;
        }
        catch (Exception ex)
        {
            // "静默"仅指不影响主流程 —— 失败原因必须留下来（含 HResult 与堆栈），
            // 否则"为什么没弹通知"永远查不出来。HResult 一并写进消息：
            // 典型值有 0x80040154 (REGDB_E_CLASSNOTREG)、0x803E0105 (通知平台未就绪) 等，
            // 只看 Message 经常是一句没有信息量的中文。
            _disabled = true;
            _lastError = $"{ex.Message}（0x{ex.HResult:X8}）";
            _disabledStack = ex.ToString();
        }
    }

    /// <summary>
    /// 清掉旧版本遗留的待补发文件。失败无所谓（只读目录 / 被占用），下次启动再试。
    /// </summary>
    private static void RemoveLegacyPendingFile()
    {
        try
        {
            var leftover = Path.Combine(AppPaths.BaseDir, "pending_notification.json");
            if (File.Exists(leftover))
            {
                File.Delete(leftover);
            }
        }
        catch
        {
            // 删不掉不影响任何功能
        }
    }

    /// <summary>
    /// 把本程序的 AUMID 与显示名写进 HKCU 的 AppUserModelId 类下（非打包应用注册 Toast 身份的标准做法）。
    /// 这是本进程自己的键，仅为让 Toast 显示"Yomori"；失败只记不抛。
    /// </summary>
    private static void EnsureAumidRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Classes\AppUserModelId\" + Aumid, writable: true);
            if (key is null)
            {
                return;
            }

            key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);

            var icon = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(icon) && File.Exists(icon))
            {
                key.SetValue("IconUri", icon, RegistryValueKind.String);
            }
        }
        catch
        {
            // 注册身份失败不影响投递，只是 Toast 头部可能显示得不好看
        }
    }

    /// <summary>弹出一条系统通知。返回是否成功投递。</summary>
    public static bool Show(string title, string body)
    {
        if (_disabled || !_registered || _notifier is null)
        {
            return false;
        }

        var payload = BuildPayload(title, body);
        var delivered = false;

        Post(() =>
        {
            var xml = new XmlDocument();
            xml.LoadXml(payload);
            _notifier!.Show(new ToastNotification(xml));
            delivered = true;
        });

        return delivered;
    }

    /// <summary>
    /// 任务完成通知的标题（带序号，形如「任务已完成（2/5）」）。
    ///
    /// 单独拆出来是为了让标题文案只在主控端这一处定义：任务完成事件、本地执行、
    /// 日志摘要都要拼同一份说法，散在调用点会各写一套。
    /// </summary>
    public static string TaskDoneTitle(int index, int total, bool abnormal)
    {
        var prefix = abnormal ? "任务异常结束" : "任务已完成";
        return total > 0 && index > 0 ? $"{prefix}（{index}/{total}）" : prefix;
    }

    /// <summary>任务完成通知的正文（任务名 + 耗时）。与 <see cref="TaskDoneTitle"/> 配套。</summary>
    public static string TaskDoneBody(string taskName, int elapsedSeconds) =>
        $"{taskName}　耗时 {Helpers.FormatHelper.FormatSeconds(elapsedSeconds)}";

    /// <summary>任务完成专用通知：标题带序号，正文带耗时。</summary>
    public static bool ShowTaskDone(string taskName, int index, int total, int elapsedSeconds, bool abnormal) =>
        Show(TaskDoneTitle(index, total, abnormal), TaskDoneBody(taskName, elapsedSeconds));

    public static bool ShowSummary(string title, string body) => Show(title, body);

    private static string BuildPayload(string title, string body)
    {
        var lines = body.Split('\n');
        var text = new System.Text.StringBuilder();
        text.Append("<text>").Append(Escape(title)).Append("</text>");
        for (var i = 0; i < lines.Length && i < 3; i++)
        {
            text.Append("<text>").Append(Escape(lines[i])).Append("</text>");
        }

        return "<toast><visual><binding template=\"ToastGeneric\">" + text + "</binding></visual></toast>";
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    /// <summary>
    /// 在 UI 线程执行 Toast 投递。与调用方同线程时直接执行；
    /// 跨线程则排队，并同步等到执行完再返回 —— 因为返回值要如实反映"是否投出去了"，
    /// 不能像旧实现那样排完队就先返回 true（那会把失败当成功，正好掩盖本类要修的问题）。
    /// </summary>
    private static void Post(Action action)
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            using var done = new System.Threading.ManualResetEventSlim(false);
            var queued = _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                }
                finally
                {
                    done.Set();
                }
            });

            if (!queued)
            {
                _lastError = "无法投递到 UI 线程。";
                return;
            }

            done.Wait(TimeSpan.FromSeconds(5));
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }
}
