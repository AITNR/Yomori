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
/// <para>不假定 <c>/S</c> 之类的静默参数（计划书 4.2）：只按实际工具的用法传参数，
/// 安装器与卸载器都是无参运行。工具自己弹控制台窗口是允许的 ——
/// "不再启动 CnC 状态 GUI"不等于隐藏所有第三方工具窗口。</para>
/// </remarks>
public static class RdpToolRunner
{
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
