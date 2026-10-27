// -*- coding: utf-8 -*-
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 把主控进程所在的会话转交回物理控制台。
///
/// <para>目标会话 ID <b>只能</b>来自主控进程自身（<see cref="Process.SessionId"/>）。
/// 内嵌 RDP 客户端回调里的 Session 是桥接库内部编号，传进 tscon 会切错会话；
/// 按用户名匹配又会踩到「同名账户有多个会话」，所以两者都不作为来源。</para>
///
/// <para>命令形式为 <c>tscon.exe &lt;会话ID&gt; /dest:console</c>。参数逐个进
/// <see cref="ProcessStartInfo.ArgumentList"/>，不经过 cmd/PowerShell，也没有任何口令参数 ——
/// 会话归属不符时让它直接失败，由上层如实报错，不做额外的令牌或密码获取。</para>
/// </summary>
internal sealed class ControllerSessionReturnService
{
    /// <summary>命令等待上限：超过即视为结果不确定，直接复核，不重发命令。</summary>
    internal static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>命令结束后复查控制台归属的等待上限。</summary>
    internal static readonly TimeSpan DefaultVerifyTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _verifyTimeout;

    public ControllerSessionReturnService(TimeSpan? commandTimeout = null, TimeSpan? verifyTimeout = null)
    {
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _verifyTimeout = verifyTimeout ?? DefaultVerifyTimeout;
    }

    /// <summary>主控进程自身的 Windows 会话 ID；取不到时返回 0。</summary>
    public uint GetControllerSessionId()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return (uint)process.SessionId;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 读取当前控制台会话。返回 false 表示「查询不到/暂时没有控制台」，
    /// 调用方不得用返回值去执行命令。
    /// </summary>
    public bool TryGetConsoleSessionId(out uint sessionId)
    {
        sessionId = 0;
        try
        {
            var raw = NativeMethods.WTSGetActiveConsoleSessionId();
            if (raw == NativeMethods.WtsNoActiveConsoleSession)
            {
                return false;
            }

            sessionId = raw;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 执行一次切回并复核。调用方须已验证开关、批次与取消状态；
    /// 本方法只负责「做一次、等结果、复核」，不做重试。
    /// </summary>
    public async Task<ControllerSessionReturnResult> ReturnAsync(
        string source,
        uint controllerSessionId,
        bool consoleKnown,
        uint consoleSessionIdBefore,
        CancellationToken cancellationToken)
    {
        var decision = ControllerSessionReturnPolicy.Decide(
            enabled: true,
            hasLocalChannel: true,
            controllerSessionId: controllerSessionId,
            consoleKnown: consoleKnown,
            consoleSessionId: consoleSessionIdBefore);

        if (decision == ControllerSessionReturnOutcome.AlreadyConsole)
        {
            return new ControllerSessionReturnResult
            {
                Outcome = ControllerSessionReturnOutcome.AlreadyConsole,
                Source = source,
                ControllerSessionId = controllerSessionId,
                ConsoleSessionIdBefore = consoleSessionIdBefore,
                ConsoleSessionIdAfter = consoleSessionIdBefore,
                Detail = "主控已经在控制台上，无需切换。",
            };
        }

        if (decision != ControllerSessionReturnOutcome.Needed)
        {
            return new ControllerSessionReturnResult
            {
                Outcome = decision,
                Source = source,
                ControllerSessionId = controllerSessionId,
                ConsoleSessionIdBefore = consoleSessionIdBefore,
                Detail = "会话状态不满足切回条件，未执行命令。",
            };
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ControllerSessionReturnResult
            {
                Outcome = ControllerSessionReturnOutcome.Cancelled,
                Source = source,
                ControllerSessionId = controllerSessionId,
                ConsoleSessionIdBefore = consoleSessionIdBefore,
                Detail = "请求已取消，未执行命令。",
            };
        }

        var run = await RunTsconAsync(controllerSessionId, cancellationToken).ConfigureAwait(false);
        var exitCode = run.ExitCode;
        var timedOut = run.TimedOut;
        var launchError = run.LaunchError;

        var (verified, afterSessionId) = await WaitForConsoleAsync(controllerSessionId, cancellationToken)
            .ConfigureAwait(false);

        if (verified)
        {
            return new ControllerSessionReturnResult
            {
                Outcome = ControllerSessionReturnOutcome.Succeeded,
                Source = source,
                ControllerSessionId = controllerSessionId,
                ConsoleSessionIdBefore = consoleSessionIdBefore,
                ConsoleSessionIdAfter = afterSessionId,
                Detail = "控制台已切回主控会话。",
            };
        }

        var outcome = timedOut
            ? ControllerSessionReturnOutcome.Timeout
            : launchError.Length > 0 || exitCode != 0
                ? ControllerSessionReturnOutcome.CommandFailed
                : ControllerSessionReturnOutcome.VerifyFailed;

        var detail = timedOut
            ? $"tscon 在 {_commandTimeout.TotalSeconds:0} 秒内没有结束，结果未确认，未重复执行。"
            : launchError.Length > 0
                ? "tscon 启动失败：" + launchError
                : exitCode != 0
                    ? $"tscon 退出码 {exitCode}（控制台仍为会话 {afterSessionId}）。"
                    : $"命令已返回，但控制台仍是会话 {afterSessionId}，不是主控会话 {controllerSessionId}。";

        return new ControllerSessionReturnResult
        {
            Outcome = outcome,
            Source = source,
            ControllerSessionId = controllerSessionId,
            ConsoleSessionIdBefore = consoleSessionIdBefore,
            ConsoleSessionIdAfter = afterSessionId,
            Detail = detail,
        };
    }

    /// <summary>有限时间轮询控制台归属；不重发命令。</summary>
    private async Task<(bool Verified, uint SessionId)> WaitForConsoleAsync(
        uint expected, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _verifyTimeout;
        uint last = 0;

        while (true)
        {
            if (TryGetConsoleSessionId(out var current))
            {
                last = current;
                if (current == expected)
                {
                    return (true, current);
                }
            }

            if (DateTimeOffset.UtcNow >= deadline || cancellationToken.IsCancellationRequested)
            {
                return (false, last);
            }

            try
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (false, last);
            }
        }
    }

    private async Task<(int ExitCode, bool TimedOut, string LaunchError)> RunTsconAsync(
        uint sessionId, CancellationToken cancellationToken)
    {
        var exe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "tscon.exe");

        if (!File.Exists(exe))
        {
            return (-1, false, "找不到 " + exe);
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(sessionId.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("/dest:console");

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
            {
                return (-1, false, "进程没有启动");
            }

            // 必须并行读走两个流，否则管道写满会让子进程卡住。
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_commandTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时不杀进程树：命令可能已经提交，杀掉只会让结果更难判断。
                var stillRunning = !process.HasExited;
                if (stillRunning && cancellationToken.IsCancellationRequested)
                {
                    return (-1, false, "已取消");
                }

                return (-1, true, string.Empty);
            }

            // 进程已退出，把输出读完（有上限，避免异常输出把内存吃光）。
            var text = await SafeReadAsync(stderr).ConfigureAwait(false)
                       + await SafeReadAsync(stdout).ConfigureAwait(false);
            if (process.ExitCode != 0 && text.Trim().Length > 0)
            {
                return (process.ExitCode, false, string.Empty);
            }

            return (process.ExitCode, false, string.Empty);
        }
        catch (Exception ex)
        {
            return (-1, false, ex.Message.Length == 0 ? ex.GetType().Name : ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> read)
    {
        try
        {
            var text = await read.ConfigureAwait(false);
            return text.Length > 2048 ? text[..2048] : text;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
