// -*- coding: utf-8 -*-
using System.Threading;
using Microsoft.UI.Xaml;
using Yomori.Services;

namespace Yomori;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>全局共享视图模型，必须在 UI 线程上创建（内部会捕获 DispatcherQueue）。</summary>
    public static ViewModels.MainViewModel ViewModel { get; private set; } = null!;
    /// <summary>主控进程内的本机 MCP 服务，复用当前内嵌 RDP 连接。</summary>
    private YomoriMcpServer? _mcpServer;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        // 崩溃落盘必须在构造时就装：App 的构造是 XAML 启动之后第一批跑的代码，
        // 而崩在它之前的那些（导航、布局、绑定）就归 CrashLog 在 Main 里装的
        // AppDomain.UnhandledException 兜底 —— 两者互补，缺一个就有一段盲区。
        Services.CrashLog.Install();
    }

    /// <summary>
    /// 主控端的正常启动路径。
    ///
    /// <b>注意：这里不再处理 <c>--rdp-agent</c>。</b>
    /// 代理模式由 <see cref="Program"/> 在调 <c>Application.Start</c> <b>之前</b>就分流走
    /// <see cref="Services.AgentHost"/> 了 —— 那条路不加载 XAML 运行时，
    /// 空载常驻内存比走完整 WinUI 启动低几十 MB（每个目标账户一份、全天常驻）。
    /// 所以能走到 OnLaunched 的，一定是主控端。
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ViewModel = new ViewModels.MainViewModel();
        MainWindow = new MainWindow();
        MainWindow.Activate();
        NotificationService.Initialize(MainWindow.DispatcherQueue);
        ViewModel.Initialize();

        // 配置读完之后才能拿到真正的「窗口背景」取值，所以背景在这里落一次
        // （MainWindow 构造时配置还是空的，那里只订阅了明暗变化）。
        MainWindow.ApplyBackdrop();

        // 单实例：用户又双击了一次 exe 时，第二个进程会把信号打到这条命名事件上。
        // 窗口若收在状态栏里，这里负责把它叫回前台（否则用户会以为程序没反应，再点几次）。
        SingleInstance.StartListener(MainWindow.DispatcherQueue, () => MainWindow?.RestoreFromTray());

        // UI 线程无响应看门狗：必须在窗口建好（拿得到 HWND）之后装。
        //
        // 为什么单独一个后台线程，而不是又一个 DispatcherQueueTimer：本应用原有的
        // 心跳轮询 / 黑屏看门狗 / 1Hz 刷新 / 呈现节拍器全部跑在 UI 线程上，
        // UI 线程一卡它们会一起静默失效 —— 卡死于是表现为「没有任何日志、不会自愈」。
        // 看门狗用自己的线程 + SendMessageTimeout(SMTO_ABORTIFHUNG) 探测，
        // 与目标的消息泵无关，目标卡死时它一定超时。详见 UiHangWatchdog 的类注释。
        Services.UiHangWatchdog.Install(Helpers.WindowHelper.GetHwnd(MainWindow));

        // 通知注册一直是静默降级的，出问题时"没收到通知"和"发不出去"分不清。
        // 这里把状态直接写进运行日志（失败时附带原因与 HResult）。
        ViewModel.AppendLog(NotificationService.DescribeStatus());
        if (NotificationService.DisabledDetail.Length > 0)
        {
            Services.CrashLog.WriteNote(
                "系统通知注册失败：" + Environment.NewLine + NotificationService.DisabledDetail);
        }
        _mcpServer = new YomoriMcpServer(ViewModel, MainWindow.DispatcherQueue, ViewModel.AppendLog);
        _mcpServer.Start();
        MainWindow.Closed += (_, _) => _mcpServer?.Dispose();
    }

    /// <summary>
    /// UI 线程的未处理异常。
    ///
    /// 这里<b>仍然标记 <c>e.Handled = true</c>（不掀进程）</b>：WinUI 3 下一个未捕获的
    /// UI 异常会直接终止进程，正在跑的任务、已建立的 RDP 会话、尚未落盘的配置全丢 ——
    /// 那些损失远大于"进程活着但可能有点 inconsistent"。
    ///
    /// 但代价就是<b>崩溃变成了沉默</b>：界面僵住、进程还在、不留任何痕迹。
    /// 所以补上两件事，让沉默变成可查：
    ///   ① 完整异常栈 + 崩前业务日志末尾写进 crash.log；
    ///   ② 首次异常弹一次通知（只弹一次，否则连着崩会把用户淹了）。
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Services.CrashLog.WriteManaged("UI 线程未处理异常（已标记 Handled，进程继续）", e.Exception);
        }
        catch
        {
            // 落盘失败不再抛出，避免二次崩溃
        }

        if (Interlocked.Exchange(ref _uiCrashNotified, 1) == 0)
        {
            // 通知是"锦上添花"：崩在 OnLaunched 之前时它还没注册，Show 会返回 false。
            // 那时用户什么提示都收不到 —— 顺手把"为什么没弹出来"记进 crash.log，
            // 否则下次看到"用户说没收到提示"时无从判断是通知坏了还是本来就没发。
            var path = Services.CrashLog.CrashLogPath;
            if (!Services.NotificationService.Show(
                    "Yomori遇到问题",
                    "界面出现了一个未处理的错误，程序没有关闭。详情见 " + path))
            {
                Services.CrashLog.WriteNote(
                    "UI 崩溃提示未投递（通知尚未注册或不可用）：" + Services.NotificationService.LastError);
            }
        }

        e.Handled = true;
    }

    /// <summary>UI 崩溃通知只发一次（1 = 已发过）。</summary>
    private static int _uiCrashNotified;
}
