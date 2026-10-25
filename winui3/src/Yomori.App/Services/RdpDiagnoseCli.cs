// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// <c>Yomori.exe --rdp-diagnose</c>：不开窗口，把 RDP 解锁状态与事务账本打出来。
/// </summary>
/// <remarks>
/// <para>计划书 M4 要求「补偿及无界面诊断」。为什么必须有这条路：</para>
/// <list type="bullet">
/// <item>隔离 VM 里验解锁流程时，界面截图没法自动化，退出码 + 文本才是可断言的东西。</item>
/// <item>用户报「解锁失败」时，让他跑这一条命令就能拿到完整现场，比问十个问题快。</item>
/// </list>
/// <para>本命令**只读**：跑检测、读账本，不改任何键、不启动服务、不下载。</para>
/// <para>退出码：0 = 四项全通过；1 = 检测完成但有项没通过；2 = 检测本身失败。</para>
/// </remarks>
public static class RdpDiagnoseCli
{
    public const string SwitchName = "--rdp-diagnose";

    public static bool IsInvocation(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, SwitchName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static int Run()
    {
        var text = new StringBuilder();

        try
        {
            text.AppendLine("Yomori RDP 解锁诊断");
            text.AppendLine("时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            text.AppendLine();

            var snapshot = RdpReadOnlyDetector.Detect();
            text.AppendLine(RdpUnlockDiagnostics.FormatDetail(snapshot));
            text.AppendLine();

            AppendTransactions(text);

            Emit(text.ToString());
            return snapshot.AllPass ? 0 : 1;
        }
        catch (Exception ex)
        {
            text.AppendLine();
            text.AppendLine($"诊断过程出错：{ex.GetType().Name}: {ex.Message}");
            Emit(text.ToString());
            return 2;
        }
    }

    private static void AppendTransactions(StringBuilder text)
    {
        text.AppendLine("--- 事务账本 ---");
        text.AppendLine("目录：" + RdpTransactionStore.RootDir);

        var list = RdpTransactionStore.List();
        if (list.Count == 0)
        {
            text.AppendLine("（还没有任何事务记录）");
            return;
        }

        foreach (var info in list)
        {
            text.AppendLine($"{info.SummaryText}　id={info.Id}"
                            + (info.ReleaseTag.Length > 0 ? $"　发布={info.ReleaseTag}" : "")
                            + (info.Failure.Length > 0 ? $"　原因={info.Failure}" : ""));
        }

        var incomplete = RdpTransactionStore.ListIncomplete();
        if (incomplete.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"⚠ 有 {incomplete.Count} 个事务没有收尾（崩溃或强杀）。"
                            + "解锁/还原在它被处理掉之前会拒绝开始。");
        }
    }

    /// <summary>
    /// 把文本交出去：能挂到父控制台就写到控制台，挂不上就落文件。
    /// </summary>
    /// <remarks>
    /// 本程序是 WinExe，<c>Console.Out</c> 在启动时指向一个无效句柄，必须先
    /// <c>AttachConsole(父进程)</c> 再重新打开标准输出，否则所有输出静默丢弃 ——
    /// 「命令跑了、什么都没打」这种体验比报错更难查。
    /// </remarks>
    private static void Emit(string text)
    {
        try
        {
            if (AttachConsole(AttachParentProcess))
            {
                var stdout = Console.OpenStandardOutput();
                using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
                writer.Write(text);
                writer.WriteLine();
                FreeConsole();
                return;
            }
        }
        catch
        {
            // 落到下面的文件路径
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Yomori", "rdp", "last-diagnose.txt");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch
        {
            // 连文件都写不了就只剩退出码了
        }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
