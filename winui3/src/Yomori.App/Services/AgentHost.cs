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
    /// 本代理实例的标识。进程启动时生成、进程内不变，写进每一条 status.json。
    ///
    /// <para>为什么需要它：同账户多条会话时会有多个代理往同一份 status.json 里写。
    /// 没有实例标识，主控端与代理自己都分不清"这条 running 是谁写的"，
    /// 于是空闲心跳可以合法地覆盖别人的执行状态。</para>
    /// </summary>
    private static readonly string InstanceId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>本进程所在的 WTS 会话 id（取不到为 0）。</summary>
    private static readonly uint OwnSessionId = RdpSessionService.TryGetCurrentSessionId() ?? 0;

    /// <summary>本进程账户的 SID（取不到为空串 —— 表示身份不可证明）。</summary>
    private static readonly string OwnSid = RdpSessionGuard.ResolveSid(null, Environment.UserName);

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
        var loop = Task.Run(RunAgentLoopAsync);

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

        // 领导者锁。拿不到就只做"待命"，不读指令也不写心跳（计划书 P1）。
        AgentLeadership? leadership = null;
        var followerNoted = false;

        while (true)
        {
            if (leadership is null)
            {
                // 让位规则（2026-10-26 真机日志驱动）：
                // 本会话已断开、而同账户另有工作会话在跑时，领导权必须归那条会话。
                // 不这么做的话，历史遗留会话里的陈旧代理会一直占着领导权 + 写 idle 心跳，
                // 主控端就会"确认到一个不在正确会话里的代理"，新会话的代理永远是跟随者。
                if (ShouldDeferToOtherSession())
                {
                    if (!followerNoted)
                    {
                        followerNoted = true;
                        AppPaths.WriteStartupError(new InvalidOperationException(
                            $"本会话（{OwnSessionId}，{Environment.UserName}）已断开且账户另有工作会话，" +
                            $"本实例（{InstanceId}）让出领导权并待命。"));
                    }

                    await Task.Delay(TimeSpan.FromSeconds(AgentPollIntervalSeconds)).ConfigureAwait(false);
                    continue;
                }

                leadership = AgentLeadership.TryAcquire(bridge.BridgeDir);
                if (leadership is null)
                {
                    // 跟随者：不消费指令、不写心跳。这样同账户多会话时任务只执行一次。
                    // 仍然留在循环里重试 —— 领导者进程退出（句柄释放）后本实例立刻接手。
                    if (!followerNoted)
                    {
                        followerNoted = true;
                        AppPaths.WriteStartupError(new InvalidOperationException(
                            $"同一座桥已存在代理领导者，本实例（{InstanceId}，会话 {OwnSessionId}）作为跟随者待命：" +
                            $"{bridge.BridgeDir}"));
                    }

                    await Task.Delay(TimeSpan.FromSeconds(AgentPollIntervalSeconds)).ConfigureAwait(false);
                    continue;
                }

                followerNoted = false;
            }

            // 领导者身份校验：锁文件被别人抢走、或本会话该让位时，必须交出去。
            if (!leadership.IsLeader || ShouldDeferToOtherSession())
            {
                leadership.Dispose();
                leadership = null;
                followerNoted = false;
                continue;
            }

            if (bridge.TryClaimCommand(InstanceId, out var command) && command is not null)
            {
                bridgeFailures = 0;

                // 归属校验：这条指令是不是发给"我这个会话 / 这个账户"的。
                // 拒收时写一条明确的 error 状态 + Finished 事件，主控端才不会一直干等。
                if (!AcceptsCommand(command, out var rejection))
                {
                    RejectCommand(bridge, command, rejection);
                    bridge.ReleaseClaim(InstanceId);
                    continue;
                }

                // 先把身份写进 status，再交给 Runner ——
                // 主控端的"等代理上线"要按会话核对来源，第一条指令跑起来之前
                // 状态里若没有 AgentSessionId，核对就只能放行（等于没核对）。
                bridge.UpdateStatus(status =>
                {
                    status.AgentUser = Environment.UserName;
                    status.AgentInstanceId = InstanceId;
                    status.AgentSessionId = OwnSessionId;
                    status.AgentSid = OwnSid;
                    return status;
                });

                // 注意：这里【不挂】Completed → ExitAgent。
                // 常驻代理执行完必须回到等待状态继续待命，而不是退出。
                var runner = new RdpAgentRunner(command);
                await runner.RunAsync(CancellationToken.None).ConfigureAwait(false);

                // 认领文件用完就删，免得桥目录里越积越多
                bridge.ReleaseClaim(InstanceId);
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
                // 带实例条件写：桥里若已经有【另一个实例】写的 running，本次故意不写，
                // 否则会把别人正在跑的任务显示成空闲。
                var outcome = bridge.WriteIdleHeartbeat(InstanceId, status =>
                {
                    status.Phase = "idle";
                    status.LiveLogProtocol = RdpLiveLogProtocol.Current;
                    status.LiveLogError = string.Empty;
                    status.StatusText = "代理在线，等待指令。";
                    status.AgentUser = Environment.UserName;
                    status.AgentSessionId = OwnSessionId;
                    status.AgentSid = OwnSid;
                    status.CurrentTask = string.Empty;
                    return status;
                });

                lastHeartbeatAt = now;

                if (outcome != RdpBridge.HeartbeatOutcome.Failed)
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
    /// 本实例是否该把领导权让给同账户的另一条会话。
    ///
    /// <para>判据只有一条：<b>本会话不是「正在跑」的那条，而账户另有「正在跑」的会话。</b></para>
    ///
    /// <para>典型现场（2026-10-26 真机）：服务端 <c>fSingleSessionPerUser=0</c>，
    /// 每次连接都新建会话，旧的留成「已断开」。旧会话里的代理进程还活着、还占着领导权，
    /// 于是新会话的代理拿不到领导权 → 不消费指令；而主控端又因为旧代理的心跳足够新鲜
    /// 而认为"代理已在线"，结果是任务永远不执行。</para>
    ///
    /// <para>查询失败时不表态（返回 false）：宁可维持现状，也不要在接口抖动时来回抢锁。</para>
    /// </summary>
    private static bool ShouldDeferToOtherSession()
    {
        if (OwnSessionId == 0)
        {
            return false;
        }

        var query = RdpSessionService.QuerySessions(resolveSid: false);
        if (!query.Usable)
        {
            return false;
        }

        var mine = query.Sessions.FirstOrDefault(s => s.SessionId == OwnSessionId);
        if (mine is null)
        {
            // 自己的会话在枚举里都找不到了：不据此让位（可能是查询抖动），继续当领导者。
            return false;
        }

        if (mine.IsUsable)
        {
            return false;
        }

        return query.Sessions.Any(s =>
            s.SessionId != OwnSessionId
            && s.IsUsable
            && string.Equals(s.UserName, Environment.UserName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 这条指令该不该由本代理执行。
    ///
    /// <para>判据（计划书 P1，2026-10-26 按真机日志放宽过一次）：</para>
    /// <list type="number">
    /// <item>指令带了目标 SessionId，而本进程不在那个会话里 → 拒收（这是最精确的判据）；</item>
    /// <item>指令带了目标 SID，而本账户 SID 与之不符 → 拒收（防同名账户冒用）；</item>
    /// <item>指令<b>没有</b>目标 SessionId（旧主控端），而本账户有多条会话 → 拒收
    ///（无法证明本会话就是主控端选中的那条）。</item>
    /// </list>
    ///
    /// <para>⚠️ 第 3 条只在"旧主控端 + 多会话"时生效。新主控端一定会写 TargetSessionId，
    /// 所以同账户有历史遗留的已断开会话时不再拒收 —— 那种情况下目标其实是确定的，
    /// 一律拒收只会让"重连后新建了一条会话"的正常路径永远跑不起来。</para>
    /// </summary>
    private static bool AcceptsCommand(RdpCommand command, out string rejection)
    {
        rejection = string.Empty;

        if (command.TargetSessionId != 0 && OwnSessionId != 0 && command.TargetSessionId != OwnSessionId)
        {
            rejection =
                $"指令目标是会话 {command.TargetSessionId}，本代理在会话 {OwnSessionId}，拒收以免两个会话各跑一遍。";
            return false;
        }

        if (command.TargetSid.Length > 0 && OwnSid.Length > 0
            && !string.Equals(command.TargetSid, OwnSid, StringComparison.OrdinalIgnoreCase))
        {
            rejection = $"指令目标 SID 是 {command.TargetSid}，本账户 SID 是 {OwnSid}，拒收（防同名账户冒用）。";
            return false;
        }

        if (command.TargetSessionId == 0)
        {
            var sessions = RdpSessionService.FindSessions(Environment.UserName);
            if (sessions.Count > 1)
            {
                rejection =
                    $"指令没有指定目标会话，而账户「{Environment.UserName}」当前有 {sessions.Count} 条会话，" +
                    "无法证明本会话就是主控端选中的那条，拒收。请升级主控端到会写 TargetSessionId 的版本。";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 拒收一条指令：写一条明确的 error 状态 + Finished 事件。
    ///
    /// <para>必须写 Finished —— 主控端是靠它判定"这一轮结束了"的。
    /// 只写 error 不写事件，主控端会一直显示"等待代理接管"直到超时。</para>
    /// </summary>
    private static void RejectCommand(RdpBridge bridge, RdpCommand command, string reason)
    {
        bridge.UpdateStatus(status =>
        {
            status.CommandId = command.Id;
            status.AgentUser = Environment.UserName;
            status.AgentInstanceId = InstanceId;
            status.AgentSessionId = OwnSessionId;
            status.AgentSid = OwnSid;
            status.Phase = "error";
            status.StatusText = reason;
            status.CurrentTask = string.Empty;
            status.Events.Add(RdpBridge.MakeEvent(
                status, RdpEventKinds.Finished, reason, abnormal: true));
            return status;
        });

        AppPaths.WriteStartupError(new InvalidOperationException(
            $"代理拒收指令 {command.Id}（实例 {InstanceId}，会话 {OwnSessionId}）：{reason}"));
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
