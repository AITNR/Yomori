// -*- coding: utf-8 -*-
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Xaml;
using Yomori.Services;

namespace Yomori;

/// <summary>
/// 自定义入口点（配 DISABLE_XAML_GENERATED_MAIN 使用）。
///
/// 为什么要自己写 Main：
///   WinUI 3 的自动生成 Main 只做「启动 XAML 运行时」一件事，
///   一旦 XAML 初始化失败（例如运行在非交互式窗口站、缺少桌面会话），
///   进程就直接退出，App 的异常处理根本没机会执行，连日志都留不下。
///   实测表现就是「代理被拉起后进程秒退、什么线索都没有」。
///
///   自己接管 Main 之后，可以在 XAML 之前先把「谁在启动、在什么环境启动」落到日志，
///   这样即使后面崩了，也能从日志里看出原因。
/// </summary>
public static class Program
{
    /// <summary>agent 模式下把启动上下文写进该文件，便于排查「进程秒退」。</summary>
    private const string AgentBootLogName = "agent_boot.log";

    /// <summary>M4 自检通道：--embed-auto <host> <user> <password> 启动时自动连接内嵌预览。
    /// 状态全走 VM.AppendLog（日志文件），供外部无人值守验收。</summary>
    /// <summary>M5/M6 自检：--embed-vm host user password 走正式 VM 连接路径（client_mode=embedded）。</summary>
    public static string[]? EmbedVmArgs { get; private set; }
    public static bool EmbedNoKeys { get; private set; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(
        IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetStartupInfoW(out StartupInfo lpStartupInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    /// <summary>
    /// 提权被拒时的兜底对话框。
    ///
    /// 为什么用 MessageBoxW 而不是项目里常用的 <c>DialogHelper.ShowAsync</c>（ContentDialog）：
    /// 走到这里 XAML 运行时还没起来（崩溃日志装完、界面没建），而 ContentDialog 依赖 XAML 就绪，
    /// 用它会在最需要提示的场合抛异常。MessageBox 是纯 Win32，与运行时无关，最稳。
    ///
    /// 局部声明而非放进 NativeMethods：只有这一处用，凑进公共类反而让那儿的"全项目 P/Invoke 收口"
    /// 名不副实（顺带也免了冒烟 RdpNativeAbiCheck 那边跟着改）。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;

    /// <summary>
    /// 任务栏 / 任务栏分组 / 通知归属用的 AppUserModelID。
    ///
    /// 必须是**稳定**的一串：改掉它，用户固定过的快捷方式、任务栏分组、跳转列表都会失联重排。
    /// 命名按惯例用反向域名式，别跟 Company/Product 随手改。
    ///
    /// 取值直接引用 <see cref="Services.NotificationService.Aumid"/> —— 通知身份与任务栏身份
    /// 必须完全一致（Toast 与任务栏分组都按它归属），常量定义只留一处。
    /// </summary>
    private const string AppUserModelId = Services.NotificationService.Aumid;

    /// <summary>
    /// 给本进程打上显式 AppUserModelID。
    ///
    /// unpackaged（WindowsPackageType=None）模式没有 MSIX 清单替我们声明 AUMID，
    /// 不显式设置的话任务栏只认"所有 unpackaged Win32 共享同一段无名身份"：
    ///   · 任务栏可能不显示图标，退回 exe 自己的默认图标；
    ///   · 多窗口 / 多实例被并进同一个无名分组；
    ///   · 固定到任务栏后再换图标，缓存的旧位图不会跟着刷新。
    ///
    /// 必须在 Application.Start（进而创建第一个窗口）**之前**调：身份是进程级属性，
    /// 窗口创建完再补，任务栏已经按旧身份登记过了。
    ///
    /// 失败不致命 —— 图标会退回到 exe 内嵌图标那条路（ApplicationIcon），所以只吞异常。
    /// </summary>
    private static void ApplyAppUserModelId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
            // 非交互窗口站等场景下可能失败，不影响启动
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        var isAgent = IsAgentInvocation(args);

        // 崩溃日志必须先于一切业务代码装上：装晚了，从进程起到装上之间这段代码崩掉就没人管。
        //
        // 顺序上有两处讲究：
        //   ① 先置 AgentMode 再 Install —— CrashLog 的落盘位置跟着 AppPaths.LogDir 走，
        //      而代理模式与主控端的 LogDir 不同（logs/<账户>/ vs logs/）。装的时候就要定好。
        //   ② 在提权判定之前 —— UAC 被拒后退出那一轮不是"上一轮"的真实结局，
        //      标记逻辑见下面的 MarkHandoff。
        AppPaths.AgentMode = isAgent;
        Services.CrashLog.Install();

        if (isAgent)
        {
            WriteAgentBootLog(args);
        }
        else if (RdpSessionService.IsElevated)
        {
            // 管理员身份已就位，继续往下走。
            // 提权由 app.manifest 的 requireAdministrator **声明式**完成，不再自己 runas 重启
            // （那套要先起普通权限进程再拉第二个，双进程且偶发窗口闪烁）。
        }
        else
        {
            // manifest 已经声明 requireAdministrator，还能走到这里只有两种可能：
            // 用户在 UAC 上点了「否」，或进程被非交互式宿主拉起（弹不出 UAC）。
            // 别再 runas 重试 —— manifest 仍生效，重试只会**再弹一次 UAC**，多半还是被拒。
            //
            // 这一轮要 MarkHandoff：它是"没跑起来"的一轮，不该把 last_run.state 写成 clean-exit，
            // 否则下次启动的"上次退出"判据会把提权被拒误判成正常退出。
            Services.CrashLog.MarkHandoff();

            // 安装时自动部署是无人值守的：被拒时只留日志与退出码，绝不弹这个对话框
            // （调用方在安装向导后面，静默安装时连向导都没有，弹出来只会把安装卡住）。
            if (AgentAutoDeploy.IsInvocation(args))
            {
                Environment.Exit(AgentAutoDeploy.Run());
            }

            ReportElevationRefused(isAgent: false);
            return;
        }

        // M5/M6 自检：--embed-vm host user password [--embed-nokeys]
        for (var i = 0; i < args.Length - 3; i++)
        {
            if (args[i] == "--embed-vm")
            {
                EmbedVmArgs = new[] { args[i + 1], args[i + 2], args[i + 3] };
                EmbedNoKeys = Array.Exists(args, a => a == "--embed-nokeys");
                break;
            }
        }

        // 会话代理：在这里就分流，**不进 WinUI 启动**。
        //
        // 为什么必须放在 Application.Start 之前：Application.Start 会加载 App.xaml 的
        // 主题资源与 XBF、并把整个 XAML 运行时拉起来（空载就要几十 MB 常驻内存）。
        // 而代理的执行链（RdpAgentRunner / ScriptRunner / RdpBridge / 截图 / 关机）
        // 不引用任何 XAML 类型，那个运行时纯属白付 —— 代理是每个目标账户一份、全天常驻。
        // AgentHost 现在是纯后台宿主：不开窗口、不建消息泵、不碰 WinRT，
        // 主线程同步等常驻循环就是"让进程别退出"的全部手段（详见 AgentHost 类注释）。
        //
        // 放在这里还有一个附带好处：XAML 启动失败（窗口站非交互、缺桌面会话）这条老故障
        // 对代理彻底不存在了 —— 代理根本不需要 XAML。
        if (isAgent)
        {
            try
            {
                AgentHost.Run(args);
            }
            catch (Exception ex)
            {
                AppPaths.WriteStartupError(ex);
                AppendAgentBootLog("代理宿主启动失败：" + ex);
                Environment.Exit(1);
            }

            return;
        }

        // 安装时自动部署（安装程序在文件铺完后调 `Yomori.exe --deploy-agent`）。
        //
        // 三条约束都跟"无人值守"有关：
        //   ① 弹窗一律不要：调用方是安装向导，静默安装时连向导都没有，弹出来只会把安装卡住；
        //   ② 不参与单实例、不打 AUMID：它是个一次性任务，退出码才是结论，
        //      更不能因为"用户正开着主控端"就被交接逻辑叫回去；
        //   ③ 自己判管理员权限（见 AgentAutoDeploy.RunCore）—— 走不到下面那段
        //      ReportElevationRefused，否则被拒时会弹一个没人看得见的消息框。
        if (AgentAutoDeploy.IsInvocation(args))
        {
            Environment.Exit(AgentAutoDeploy.Run());
        }

        // RDP 解锁的无界面诊断（计划书 M4）：只读检测 + 事务账本，退出码即结论。
        // 和 --deploy-agent 一样在单实例之前分流：它是查询任务，不该因为
        // 「用户正开着主控端」就被交接逻辑叫回去，也不需要窗口。
        if (RdpDiagnoseCli.IsInvocation(args))
        {
            Environment.Exit(RdpDiagnoseCli.Run());
        }

        // 会话诊断（计划书 P0 / P2）：只读打印会话清单 + 单账户单会话策略状态；
        // 带 --enable-single-session / --restore-single-session 才写注册表。
        // 与 --rdp-diagnose 同样放在单实例之前：它是显式的运维动作，不该被"程序已经开着"挡回来。
        if (RdpSessionCli.IsInvocation(args))
        {
            Environment.Exit(RdpSessionCli.Run(args));
        }

        // 任务栏身份要在第一个窗口创建之前定下来，代理模式不涉及窗口、跳过。
        if (!isAgent)
        {
            ApplyAppUserModelId();

            // 单实例：窗口能收进状态栏之后，程序"看不见但还在跑"，用户很容易再双击一次 exe。
            // 没有这道闸门就是两个进程、两个托盘图标、两套任务（两条脚本可能抢同一个存档）。
            // 必须放在创建窗口之前：已有实例时把它的窗口叫出来，本进程立刻退出。
            if (!SingleInstance.TryClaim())
            {
                SingleInstance.SignalExisting();

                // 这一轮什么活都没干，绝不能把自己的退出写成 clean-exit ——
                // 那会把正在跑的那一份的"上次是否正常退出"证据覆盖掉（真正的崩溃会被误报成正常）。
                // handoff 标记的语义正是"本进程只做了交接"，见 CrashLog.MarkCleanExit。
                Services.CrashLog.MarkHandoff();
                return;
            }
        }

        try
        {
            // 走 WinUI 的标准启动流程。unpackaged 模式下用 Application.Start，
            // 由它创建 App 实例并驱动消息循环。
            Application.Start(_ => new App());
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// UAC 被拒绝时给用户一句能看懂的说明，然后退出。
    ///
    /// 提权本身已交给 app.manifest 的 <c>requireAdministrator</c> 声明式完成
    /// （双击时由系统直接弹 UAC，不存在"先起普通权限进程再拉第二个"那套双进程毛刺），
    /// 所以这里**不再自己 runas 重启** —— 那套逻辑已经不需要，且留着会在
    /// "已经是管理员"时白白多跑一次。
    ///
    /// 走到这里只有一种可能：用户在 UAC 上点了「否」，或者进程被非交互式宿主拉起
    /// （UAC 弹不出来）。两种情况下继续跑都没有意义 ——
    /// 程序要往安装目录写 config.json / logs / runtime_stats，还要写 ProgramData 部署会话代理，
    /// 标准用户都做不了，跑起来只会在后续某处报一个跟"权限"八竿子打不着的错，
    /// 用户很难反推回真正原因（这跟 MEMORY「权限误报」那条同一个教训）。
    /// 这里直接说清楚，并给出正确做法。
    ///
    /// 代理模式（--rdp-agent）不适用：它由任务计划程序以最高权限拉起，
    /// 拿到的本来就是管理员令牌，进到这里说明环境异常，但也别弹窗打断 ——
    /// 没人看得见弹窗，只会把日志写下来就算。
    /// </summary>
    private static void ReportElevationRefused(bool isAgent)
    {
        AppPaths.WriteStartupError(new InvalidOperationException(
            "进程未以管理员权限运行（UAC 被拒绝或无法交互），主控端无法写入安装目录与 ProgramData。"));

        if (isAgent)
        {
            Environment.Exit(2);
        }

        try
        {
            // 提权被拒时还没起 XAML 运行时，不能用 ContentDialog（那需要 XAML 就绪）。
            // 这里退到最朴素可靠的通道：系统消息框。
            MessageBoxW(
                IntPtr.Zero,
                "Yomori需要管理员权限才能运行。\n\n"
                + "原因：要写入安装目录（配置与日志）与 ProgramData（部署会话代理），这些位置标准用户没有权限。\n\n"
                + "请关闭本窗口，然后右键程序图标 →「以管理员身份运行」。",
                "Yomori — 需要管理员权限",
                MB_OK | MB_ICONWARNING);
        }
        catch
        {
            // 连消息框都弹不出来（非交互式宿主）就静默退出，日志已经写过了
        }

        Environment.Exit(3);
    }

    private static bool IsAgentInvocation(string[] args)
    {
        foreach (var argument in args)
        {
            if (string.Equals(argument, "--rdp-agent", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把启动环境写下来：账户、会话、窗口站、桌面 —— 这几项能直接判定
    /// 「是不是跑在非交互式窗口站里」，也就是 WinUI 启动失败的典型原因。
    /// </summary>
    private static void WriteAgentBootLog(string[] args)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine(new string('=', 70));
            builder.AppendLine("时间      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("账户      : " + Environment.UserName);
            builder.AppendLine("进程ID    : " + Environment.ProcessId);
            builder.AppendLine("BaseDir   : " + AppContext.BaseDirectory);
            builder.AppendLine("命令行    : " + string.Join(' ', args));
            builder.AppendLine("桌面      : " + DescribeDesktop());
            builder.AppendLine("窗口站    : " + DescribeWindowStation());

            var path = Path.Combine(AppContext.BaseDirectory, AgentBootLogName);
            File.AppendAllText(path, builder.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 写日志失败不应影响启动
        }
    }

    private static void AppendAgentBootLog(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, AgentBootLogName);
            File.AppendAllText(path, message + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // 同上
        }
    }

    private static string DescribeWindowStation()
    {
        try
        {
            var handle = GetProcessWindowStation();
            if (handle == IntPtr.Zero)
            {
                return "(拿不到窗口站句柄)";
            }

            var buffer = new StringBuilder(256);
            // UOI_NAME = 2
            return GetUserObjectInformationW(handle, 2, buffer, buffer.Capacity * 2, out _)
                ? buffer.ToString()
                : "(读取窗口站名失败)";
        }
        catch
        {
            return "(查询窗口站异常)";
        }
    }

    private static string DescribeDesktop()
    {
        try
        {
            if (GetStartupInfoW(out var info) && info.lpDesktop != IntPtr.Zero)
            {
                var desktop = Marshal.PtrToStringUni(info.lpDesktop);
                return string.IsNullOrEmpty(desktop) ? "(空)" : desktop;
            }

            return "(lpDesktop 为空 —— 通常意味着非交互式启动)";
        }
        catch
        {
            return "(查询桌面异常)";
        }
    }
}
