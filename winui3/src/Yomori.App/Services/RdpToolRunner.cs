// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yomori.Services;

/// <summary>外部工具的运行结果。</summary>
public readonly record struct RdpToolRunResult(int ExitCode, bool TimedOut, string OutputTail);

/// <summary>
/// 跑上游工具（安装器 / 卸载器 / RDPWInst）的公共实现。
/// </summary>
/// <remarks>
/// <para>抽出来是因为解锁流程和卸载流程都要跑同一个 exe，而这两个流程对"超时"的处理必须一致：
/// <b>不杀进程</b>（计划书 6.2：正在写文件的工具不能强杀），把结论交给调用方按"结果不明"处理。</para>
/// <para>⚠️ 2026-10-26 真机教训：<c>RDPW_Installer.exe</c> / <c>RDPW_Uninstaller.exe</c> 就是
/// <c>RDPWInst.exe</c> 改了名，而它**无参运行是交互式菜单**（等着按键）。
/// 无参调用 → 装是装成功了，但进程永不退出 → 180 秒超时 → 界面报"安装器超时"，用户以为失败。
/// 所以两条纪律：① 调用方必须传 <c>-i</c> / <c>-u</c> 这类动作开关；
/// ② 这里把 stdin 重定向后立刻关掉 —— 万一工具里还有 <c>ReadLn</c>/<c>pause</c>，
/// 读到 EOF 会直接返回，而不是永远挂在那里。</para>
/// </remarks>
public static class RdpToolRunner
{
    /// <summary>安装动作开关（RDPWInst 系工具通用）。</summary>
    public const string InstallArguments = "-i";

    /// <summary>卸载动作开关。</summary>
    public const string UninstallArguments = "-u";

    /// <summary>更新 INI 的动作开关。</summary>
    public const string UpdateIniArguments = "-w";

    /// <summary>
    /// 是否还有上游工具在跑。上一次操作超时/崩溃后进程可能还活着，
    /// 那种情况下不能开始新的操作（计划书 5.2：子进程尚在运行时禁止同时回滚和新建操作）。
    /// </summary>
    public static bool IsToolRunning()
    {
        foreach (var name in new[] { "RDPWInst", "RDPW_Installer", "RDPW_Uninstaller" })
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length == 0)
                {
                    continue;
                }

                foreach (var process in processes)
                {
                    process.Dispose();
                }

                return true;
            }
            catch
            {
                // 查不到就当没有
            }
        }

        return false;
    }

    public static async Task<RdpToolRunResult> RunAsync(
        string path,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 重定向 stdin 的目的不是给输入，而是能在启动后立刻关掉它 —— 见类注释
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    output.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    output.AppendLine(e.Data);
                }
            };

            if (!process.Start())
            {
                return new RdpToolRunResult(-1, false, "进程无法启动");
            }

            // 立刻关掉 stdin：工具若在等按键，读到 EOF 就会返回，而不是卡满整个超时窗口
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // 关不掉也无所谓，后面还有超时兜底
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                process.WaitForExit();   // 等异步输出读完
                return new RdpToolRunResult(process.ExitCode, false, Tail(output.ToString()));
            }
            catch (OperationCanceledException)
            {
                // 不 Kill：让工具自己把文件写完，调用方按"结果不明"处理
                return new RdpToolRunResult(-1, true, Tail(output.ToString()));
            }
        }
        catch (Exception ex)
        {
            return new RdpToolRunResult(-1, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Tail(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "（无输出）";
        }

        return trimmed.Length <= 400 ? trimmed : "…" + trimmed.Substring(trimmed.Length - 400);
    }
}
