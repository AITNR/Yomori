// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 记录并查询「我们启动的上游工具进程树还在不在跑」。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>（2026-10-26 检查记录 P0）：隐藏桌面上启动的安装器只返回一个 PID，
/// 之后没人再关心它还活着没有。180 秒超时后流程把事务记成失败就返回了，可那个安装器
/// 可能正在写 ServiceDll / 注册表 / 安装目录。用户紧接着点卸载或还原，两边就会互相抢文件。</para>
/// <para><b>为什么不止看根进程</b>：上游工具会拉起子进程（<c>reg.exe</c>、<c>netsh</c>、
/// <c>RDP_CnC.exe</c>），根进程退出不代表它们停了。所以按 Toolhelp 快照里的父进程关系
/// 把整棵子树算出来。</para>
/// <para><b>为什么记启动时间</b>：PID 会被系统复用。只比 PID 会把「别人的进程」当成我们的工具，
/// 于是永远拦着用户不放；比一下启动时间就能区分。</para>
/// <para><b>为什么不主动结束</b>：计划书 6.2 明确不强制结束正在写系统文件的工具。
/// 这里只负责如实回答「还在不在」，把结论交给流程决定等还是拦。</para>
/// </remarks>
public static class RdpToolProcessTree
{
    /// <summary>把一个刚启动的进程记成可查询的记录。进程已退出或读不到启动时间时返回 null。</summary>
    public static RdpToolProcessRecord? Capture(int pid, string role, string path = "")
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var startUtc = process.StartTime.ToUniversalTime();

            return new RdpToolProcessRecord
            {
                Pid = pid,
                Name = process.ProcessName,
                Path = path,
                StartTimeUtcTicks = startUtc.Ticks,
                Role = role,
            };
        }
        catch
        {
            // 进程已经退出（很常见：控制台工具秒退）→ 没有需要跟踪的东西
            return null;
        }
    }

    /// <summary>
    /// 一条记录对应的进程树是否还在跑。<c>Detail</c> 为空表示整棵树都没了。
    /// </summary>
    /// <remarks>
    /// 根进程被 PID 复用时靠启动时间区分：启动时间对不上就当它已经退出了。
    /// 子进程不做启动时间比对（它们没有记录），但父链里出现「PID 相同而启动时间不同」时，
    /// 那条父链不算我们的子树 —— 复用 PID 的新进程不会替旧进程养孩子。
    /// </remarks>
    public static (bool Alive, string Detail) Check(RdpToolProcessRecord record)
    {
        var processes = Snapshot();
        if (processes.Count == 0)
        {
            // 快照读不出来：不知道就当「还在跑」是更安全的答案 —— 但它会让流程永远等下去，
            // 所以这里返回「查不到」，由调用方按「无法确认」处理（既不放行也不断言还活着）。
            return (false, "");
        }

        var rootIndex = IndexOfPid(processes, record.Pid);
        var rootIsOurs = rootIndex >= 0 && StartTimeMatches(record);

        var alive = new List<string>();

        if (rootIsOurs)
        {
            var root = processes[rootIndex];
            alive.Add($"{root.Name}(pid {root.Pid})");
        }

        // 子进程：父链能走到我们那个 PID 的都算。根进程已退出时 Windows 不会改父 PID，
        // 所以孤儿进程照样能被找回来。
        foreach (var process in processes)
        {
            if (rootIsOurs && process.Pid == record.Pid)
            {
                continue;
            }

            if (IsDescendantOf(processes, process, record.Pid))
            {
                alive.Add($"{process.Name}(pid {process.Pid})");
            }
        }

        return alive.Count == 0
            ? (false, "")
            : (true, string.Join("、", alive));
    }

    /// <summary>
    /// 一组记录里还活着的阻塞型工具（<see cref="RdpToolProcessRecord.RoleBlocking"/>）。
    /// </summary>
    /// <param name="records">账本里记下的进程。</param>
    /// <param name="detail">为空表示没有阻塞型工具在跑。</param>
    public static bool AnyBlockingAlive(IEnumerable<RdpToolProcessRecord> records, out string detail)
    {
        var alive = new List<string>();

        foreach (var record in records)
        {
            if (!string.Equals(record.Role, RdpToolProcessRecord.RoleBlocking, StringComparison.Ordinal))
            {
                continue;
            }

            var (running, text) = Check(record);
            if (running && text.Length > 0)
            {
                alive.Add(text);
            }
        }

        detail = alive.Count == 0 ? "" : string.Join("；", alive);
        return detail.Length > 0;
    }

    /// <summary>按可执行文件名找进程（用于报告上游安装器拉起的 <c>RDP_CnC.exe</c> 残留）。</summary>
    public static IReadOnlyList<(int Pid, string Name)> FindByName(string exeNameWithoutExtension)
    {
        var result = new List<(int, string)>();

        foreach (var process in Snapshot())
        {
            if (process.Name.Equals(exeNameWithoutExtension, StringComparison.OrdinalIgnoreCase))
            {
                result.Add((process.Pid, process.Name));
            }
        }

        return result;
    }

    /// <summary>读一份进程快照（PID / 父 PID / 映像名）。读不出来返回空表。</summary>
    public static List<RdpProcess> Snapshot()
    {
        var result = new List<RdpProcess>();
        var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);

        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            var entry = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };

            if (!Native.Process32FirstW(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result.Add(new RdpProcess(
                    (int)entry.th32ProcessID,
                    (int)entry.th32ParentProcessID,
                    (entry.szExeFile ?? "").TrimEnd('\0')));

                entry.dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>();
            }
            while (Native.Process32NextW(snapshot, ref entry));
        }
        catch
        {
            // 快照读到一半失败：已读到的部分照样有用，别丢
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }

        return result;
    }

    /// <summary>一条进程快照记录。</summary>
    public readonly record struct RdpProcess(int Pid, int ParentPid, string Name);

    /// <summary>父链能不能一路走到 <paramref name="ancestorPid"/>。</summary>
    private static bool IsDescendantOf(List<RdpProcess> all, RdpProcess process, int ancestorPid)
    {
        var current = process;
        // 深度上限防环（父 PID 关系出现环只可能是数据异常，但不能让它把流程卡死）
        for (var depth = 0; depth < 64; depth++)
        {
            if (current.ParentPid == ancestorPid && current.Pid != ancestorPid)
            {
                return true;
            }

            if (current.ParentPid <= 0 || current.ParentPid == current.Pid)
            {
                return false;
            }

            var parentIndex = IndexOfPid(all, current.ParentPid);
            if (parentIndex < 0)
            {
                return false;
            }

            current = all[parentIndex];
        }

        return false;
    }

    /// <summary>按 PID 找下标；找不到返回 -1（结构体不能用 <c>List.Find</c>，它返回 default 而不是 null）。</summary>
    private static int IndexOfPid(List<RdpProcess> all, int pid)
    {
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Pid == pid)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>记录里的启动时间与当前同名 PID 的启动时间是否一致（读不到启动时间时按一致处理）。</summary>
    private static bool StartTimeMatches(RdpToolProcessRecord record)
    {
        if (record.StartTimeUtcTicks <= 0)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(record.Pid);
            return process.StartTime.ToUniversalTime().Ticks == record.StartTimeUtcTicks;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>子进程不做启动时间比对（它们没有单独记录），只在父链上确认归属。</summary>
    private static class Native
    {
        public const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
