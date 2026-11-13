// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// RDP 会话代理的宿主：<c>--rdp-agent</c> 模式下的进程入口。
///
/// <para>【代理没有 GUI，也就不需要消息泵】</para>
///
/// 这条线是分两步砍下来的，两步都记在这里，免得以后有人又"顺手"加回来：
///
/// <para>第一步（原实现）：agent 也走 <c>Application.Start</c>，加载 App.xaml 的主题资源与 XBF、
/// 造一个 XAML Window 再 <c>AppWindow.Hide()</c>，全部目的只为换一个"消息循环 + 进程别退出"。
/// 而代理的执行链（<see cref="RdpAgentRunner"/> / <see cref="ScriptRunner"/> /
/// <see cref="RdpBridge"/> / 截图 / 关机）<b>不引用任何 XAML 类型</b> ——
/// 全项目引用 XAML 的只有 <c>RdpD3DRenderer</c>、<c>MainViewModel</c>、
/// <c>DialogHelper</c>、<c>WindowHelper</c>，一个都不在这条链上。
/// WinUI 3 空载常驻内存几十 MB（实测每个空闲代理 Private ≈ 105MB / WS ≈ 155MB，2026-10-05），
/// 代理又是「每个目标账户一个、全天常驻」—— 三个账户就是三份白付的钱。</para>
///
/// <para>第二步（本版）：第一步之后改用 <c>Microsoft.UI.Dispatching.DispatcherQueueController</c>
/// 建消息泵，本意是"只维持进程存活、不碰 XAML"。但实测<b>每个真实代理都建不起来</b>：
/// <c>CreateOnCurrentThread()</c> 一律抛 <c>COMException 0x80040111</c>
/// （CLASS_E_CLASSNOTAVAILABLE，代理进程没有 WinAppSDK 的激活上下文），
/// 于是每次都落到"主线程同步等循环"的兜底分支。也就是说：</para>
/// <list type="bullet">
///   <item>那个消息泵在生产的每一次启动里<b>从来没有建成过</b>，
///         <c>startup_error.log</c> 里躺着 20+ 条同样的 0x80040111；</item>
///   <item>但它每次都要先把 WinRT 投影那套 DLL 加载进来才失败 ——
///         实测代理因此额外加载 <c>Microsoft.Windows.SDK.NET.dll</c>（24MB）、
///         <c>Microsoft.WinUI.dll</c>（7MB）、<c>Microsoft.WindowsAppRuntime.dll</c>、
///         <c>CoreMessaging.dll</c> 等一批只有 GUI 才用得着的东西。</item>
/// </list>
///
/// <para>代理是纯后台进程：不开窗、不收窗口消息、不碰 XAML/WinRT。
/// 维持进程存活只需要主线程阻塞在一个等待上，所以这里<b>直接同步等循环</b>，
/// 一个 WinRT 类型都不碰。退出只有两条路：循环自己结束（桥目录彻底不可用），
/// 或 <see cref="ExitAgent"/> 里的 <c>Environment.Exit</c>。</para>
/// </summary>
public static class AgentHost
{
    /// <summary>Agent 登录后等待桌面环境就绪的时间（秒）。</summary>
    private const int AgentStartupDelaySeconds = 5;

    /// <summary>
    /// 常驻等待指令时的轮询间隔（秒）。
    ///
    /// 既是"查一次有没有新指令"的节奏，也是"写一次心跳"的节奏 ——
    /// 主控端 RdpHeartbeat.TimeoutSeconds = 20 秒，1 秒一次留足余量，不会误报失联。
    /// </summary>
    private const int AgentPollIntervalSeconds = 1;

    /// <summary>
    /// 空闲时<b>写心跳</b>的间隔（秒）。与"查指令"的节奏分开，是有原因的：
    ///
    /// 心跳不是只有 372 字节 —— 生产桥里的 status.json 有 20–23 KB（带着上一轮任务最多
    /// 500 条 Events），而 <c>UpdateStatus</c> 是"读回来 → 反序列化 → 改字段 → 缩进序列化 →
    /// 原子写盘"，写盘前后还要各做一次 `GrantUsersFullControl`。按 1 秒一拍算，空闲代理每天
    /// 白写约 2 GB、白分配上百 GB 的临时对象；实测空闲进程的托管堆就是这样以 ~23 KB/s 线性
    /// 涨到 ~28 MB 才稳（见 docs/memory-audit-2026-10-17.md「代理进程」一节）。
    ///
    /// 放松到 5 秒：主控端 `RdpHeartbeat.TimeoutSeconds = 20`，仍留 4 倍余量，
    /// 判失联的依据是 UpdatedAt 的新鲜度，不是写入频率。
    /// 查指令仍是 <see cref="AgentPollIntervalSeconds"/> = 1 秒一拍 —— 指令延迟不变。
    /// </summary>
    private const int AgentHeartbeatIntervalSeconds = 5;

    /// <summary>
    /// 连续写心跳失败多少次就判定桥目录彻底不可用（次）。
    /// 只有到这一步才退出 —— "没有指令"绝不该是退出的理由。
    /// </summary>
    private const int AgentBridgeFailureLimit = 30;

    /// <summary>
    /// 启动代理进程。<b>阻塞</b>到进程退出为止，调用方（<c>Program.Main</c>）拿到控制权时进程已结束。
    ///
    /// 绝不返回：正常情况下靠 <see cref="ExitAgent"/> 直接结束进程；
    /// 桥目录彻底不可用时同样 <c>Environment.Exit</c>。保留 <c>int</c> 返回值只为让
    /// 未来想改成"跑完返回码再退出"时不必改签名。
    /// </summary>
    public static int Run(IReadOnlyList<string> args)
    {
        // 多账户共用同一份代理副本，日志/统计必须按账户分家（见 AppPaths.AgentMode 说明）。
        // 必须在任何 FileLogger / RuntimeStatsManager 构造之前置位。
        AppPaths.AgentMode = true;

        // 确定代理该读写哪座桥：远程场景由 --bridge: 指定，
        // 本机多账户场景则按"自己登录的是哪个账户"自动派生（与主控端算出的目录一致）。
        RdpBridge.ConfigureAgent(RdpAgentRunner.ExtractBridgePath(args));

        // 代理循环跑在工作线程：它全程 await（Task.Delay / 文件 IO / 进程管理），
        // 从不碰 UI 线程，也不需要任何窗口或消息泵。
        //
        // 这里**不初始化 NotificationService**：完成通知已改成"只由主控端发"，
        // 代理侧一颗通知都不发；而代理跑在目标账户里，真去注册也只会在别人的
        // 用户配置里塞一条本程序的通知身份，纯属污染。它内部还要 DispatcherQueue，
        // 碰它就等于把 WinRT 那套重新拉进代理进程。
        // S1 第八批：抓帧采集端是**可选增强**，与指令循环并列、互不阻塞。
        // 指令循环执行任务时会 await 在 runner.RunAsync 上，若把抓帧塞进那条循环，
        // 任务一跑画面就断。开关由桥目录 capture-enable.json 驱动，默认关闭时
        // 这里连 D3D 设备都不创建，空闲代理的内存/CPU 不变。
        // 构造与运行中的任何异常都由 CaptureAgentLoop 自行收敛，绝不掀常驻代理。
        CaptureAgentLoop? captureLoop = null;
        try
        {
            var captureBridge = RdpBridge.Agent;
            captureLoop = new CaptureAgentLoop(
                captureBridge.ChannelTag,
                captureBridge.BridgeDir,
                () => new DxgiFrameSource(),
                // NC3 实测：代理是 asInvoker 非提升进程，token 里没有
                // SeCreateGlobalPrivilege，创建 Global\ 对象必被拒（Win32=5）。
                // 映射由提升运行的主控端创建并设 Everyone ACE，这里只 OpenExisting。
                CaptureSharedMemoryAcl.OpenExistingFactory,
                global: true);
        }
        catch
        {
            // 可选增强初始化失败：忽略即可，代理继续跑指令桥，画面走原有 RDP 帧流。
        }

        var loop = Task.Run(RunAgentLoopAsync);
        if (captureLoop is not null)
        {
            _ = Task.Run(() => captureLoop.RunAsync(CancellationToken.None));
        }

        // 主线程就是"让进程活着"的那根钉子：同步等循环结束。
        // 循环是 while(true)，正常不返回；返回了说明桥目录废了。
        WaitLoop(loop);
        return 0;
    }

    /// <summary>
    /// 常驻主循环：等指令 → 执行 → 回到等待，只有在桥目录彻底不可用时才退出。
    ///
    /// 关键约束：
    ///   1. "没有指令"进入等待，不再退出（旧实现读不到就 ExitAgent，正是事故根因）。
    ///   2. 等待期间持续写心跳（Phase=idle），主控端据此判定"代理在线"；
    ///      写盘节奏见 <see cref="AgentHeartbeatIntervalSeconds"/>。
    ///   3. 写心跳只在【等待】分支执行；任务执行期间本方法正 await 在 RunAsync 上，
    ///      不会插手，从而不会覆盖 RdpAgentRunner 正在写的任务状态。
    /// </summary>
    private static async Task RunAgentLoopAsync()
    {
        try
        {
            // 刚登录时桌面环境可能还没就绪，等一下再开工，避免脚本启动失败
            await Task.Delay(TimeSpan.FromSeconds(AgentStartupDelaySeconds)).ConfigureAwait(false);

            await WaitForCommandsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);
            ExitAgent();
        }
    }

    private static async Task WaitForCommandsAsync()
    {
        var bridgeFailures = 0;
        var bridge = RdpBridge.Agent;

        // 上一次成功写心跳的时刻。初值取 MinValue：刚进入等待（含一轮任务跑完回来）
        // 立刻写一拍，主控端不用等满一个心跳周期才看到 idle。
        var lastHeartbeatAt = DateTime.MinValue;

        while (true)
        {
            if (bridge.TryReadCommand(out var command) && command is not null)
            {
                bridgeFailures = 0;

                // 立刻删掉指令，防止代理被再次拉起时重复执行同一批任务
                bridge.ConsumeCommand();

                // 注意：这里【不挂】Completed → ExitAgent。
                // 常驻代理执行完必须回到等待状态继续待命，而不是退出。
                var runner = new RdpAgentRunner(command);
                await runner.RunAsync(CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            // 没有指令：清掉桥里可能残留的停止请求。
            //
            // 为什么会残留：停止请求只有在执行期间才会被检查，而"点停止 → 清理跑十几秒 →
            // 收尾"这段时间里如果主控端又补了一份（或代理当时正卡在清理里没读），
            // 那份 stop.json 就会一直躺到下一轮。靠 CommandId 校验虽然不会误停新指令，
            // 但排障时看到一份没人消费的 stop.json 极其误导。
            // 这里能安全删除的前提：本分支只在"没有待执行指令"时才走到 ——
            // 有指令在跑时不会进来，不存在把正在用的停止请求删掉的风险。
            bridge.ClearStop();

            // 没有指令：写一条 idle 心跳，证明"代理在线，等待指令"。
            // 任务在跑时不会走到这里，因此不会覆盖 Runner 的任务状态。
            //
            // 节奏按 AgentHeartbeatIntervalSeconds 放慢（写盘比查指令贵得多，理由见常量注释）；
            // 不写盘的那几拍只做上面的读指令 + 清 stop，指令延迟仍是 1 秒。
            var now = DateTime.UtcNow;
            if (lastHeartbeatAt == DateTime.MinValue ||
                now - lastHeartbeatAt >= TimeSpan.FromSeconds(AgentHeartbeatIntervalSeconds))
            {
                var alive = bridge.UpdateStatus(status =>
                {
                    status.Phase = "idle";
                    status.LiveLogProtocol = RdpLiveLogProtocol.Current;
                    status.LiveLogError = string.Empty;
                    status.StatusText = "代理在线，等待指令。";
                    status.AgentUser = Environment.UserName;
                    status.CurrentTask = string.Empty;
                    return status;
                });

                lastHeartbeatAt = now;

                if (alive)
                {
                    bridgeFailures = 0;
                }
                else
                {
                    // 连状态都写不出去 —— 共享目录已不可用，累加到阈值再退出，避免死撑。
                    // 计数按"真的尝试过"的次数来，所以只在写盘分支里累加。
                    bridgeFailures++;
                    if (bridgeFailures >= AgentBridgeFailureLimit)
                    {
                        AppPaths.WriteStartupError(new IOException(
                            $"会话代理无法写入指令桥目录 {bridge.BridgeDir}：{bridge.LastError}"));
                        ExitAgent();
                        return;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(AgentPollIntervalSeconds)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 主线程阻塞等待常驻循环。代理既没有窗口也没有消息泵，
    /// "进程别退出"靠的就是这里 —— 直到循环返回（桥目录彻底不可用）才离开。
    /// </summary>
    private static void WaitLoop(Task loop)
    {
        try
        {
            loop.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppPaths.WriteStartupError(ex);
            ExitAgent();
        }
    }

    private static void ExitAgent()
    {
        try
        {
            Environment.Exit(0);
        }
        catch
        {
            // 忽略
        }
    }
}
