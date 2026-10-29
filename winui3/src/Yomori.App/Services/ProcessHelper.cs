// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Yomori.Services;

/// <summary>
/// 一次"结束进程"尝试的结果。
///
/// 为什么要它：原来 <c>taskkill</c> 是"发了就算完"，输出与退出码全丢，
/// 日志里只留下"正在结束 X"。于是**没人能判断到底关掉没有** ——
/// 用户对着还在跑的游戏，只能猜是没杀掉还是画面没刷新（真机踩过）。
/// </summary>
public sealed class ProcessTerminateResult
{
    public ProcessTerminateResult(bool ok, int exitCode, string detail)
    {
        Ok = ok;
        ExitCode = exitCode;
        Detail = detail ?? string.Empty;
    }

    /// <summary>是否确认结束（或本来就不存在）。</summary>
    public bool Ok { get; }

    /// <summary>taskkill 的退出码；托管 API 或跳过时为 -1。</summary>
    public int ExitCode { get; }

    /// <summary>失败原因 / 补充说明（taskkill 的 stderr 或托管 API 的异常消息）。</summary>
    public string Detail { get; }

    public static ProcessTerminateResult Skip(string reason) => new(true, -1, reason);

    /// <summary>日志用的一行摘要。</summary>
    public string Summary => Ok
        ? (Detail.Length > 0 ? "已结束（" + Detail + "）" : "已结束")
        : (Detail.Length > 0 ? $"未能结束：{Detail}" : "未能结束");

    /// <summary>失败时给用户的下一步建议 —— 绝大多数情况是权限/自我保护。</summary>
    public string Hint => Ok
        ? string.Empty
        : "目标进程可能带反作弊保护、或属于更高权限的运行环境，普通权限结束不了。";
}

/// <summary>
/// 一次进程快照里的一条：PID、父 PID、镜像名（带 <c>.exe</c>）。
///
/// 有了父 PID，才能自己按父子关系列出"某个进程派生了哪些后代"。
/// 这件事 .NET 的 <c>Kill(entireProcessTree: true)</c> 干不可靠（实测漏后代），见 <see cref="ProcessHelper.TerminateProcessTree"/>。
/// </summary>
public sealed class ProcessSnapshotEntry
{
    public ProcessSnapshotEntry(int pid, int parentPid, string name)
    {
        Pid = pid;
        ParentPid = parentPid;
        Name = (name ?? string.Empty).Trim();
    }

    public int Pid { get; }

    public int ParentPid { get; }

    /// <summary>镜像名，Windows 给的本来带 <c>.exe</c>（如 <c>March7th Assistant.exe</c>）。</summary>
    public string Name { get; }

    public override string ToString() => Name.Length > 0 ? $"{Name}(PID {Pid})" : $"PID {Pid}";
}

/// <summary>
/// 进程 / 窗口探测与清理。
/// 对应 Python 版 ScriptRunnerWorker 中的 tasklist / EnumWindows / taskkill 逻辑。
/// </summary>
public static class ProcessHelper
{
    /// <summary>去掉 <c>.exe</c> 的镜像名（<c>Process.ProcessName</c> 不带扩展名，比较前必须统一）。</summary>
    public static string BareImageName(string? imageName)
    {
        var text = (imageName ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;
    }

    /// <summary>
    /// 任务名要拿去**按名字**清理时，先确认它"像个镜像名"。
    ///
    /// 为什么需要这道闸：任务名可能是「原神日常」这种给人看的显示名，
    /// 直接拿去做 <c>taskkill /IM</c> 不但打不中，还有误杀同名程序的风险。
    /// 判定从宽（.exe 结尾、或单个词）—— 因为任务名在本工程里绝大多数就是 exe 名，
    /// 而漏掉它的代价是"子进程漏网"（见 <see cref="TerminateProcessTree"/> 的案例）。
    /// </summary>
    public static bool LooksLikeImageName(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 128)
        {
            return false;
        }

        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !text.Contains('/') && !text.Contains('\\') && !text.Contains(' ');
    }

    /// <summary>
    /// 一次 Toolhelp32 快照拿全系统的 PID / 父 PID / 镜像名。
    /// 失败（极少数受限环境）返回空表，调用方按"拿不到父子关系"降级即可。
    /// </summary>
    public static List<ProcessSnapshotEntry> SnapshotProcesses()
    {
        var result = new List<ProcessSnapshotEntry>();
        var snapshot = IntPtr.Zero;
        try
        {
            snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == NativeMethods.InvalidHandleValue)
            {
                return result;
            }

            var size = Marshal.SizeOf<NativeMethods.ProcessEntry32W>();
            var entry = new NativeMethods.ProcessEntry32W { dwSize = size };
            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return result;
            }

            while (true)
            {
                var pid = (int)entry.th32ProcessID;
                if (pid > 0)
                {
                    result.Add(new ProcessSnapshotEntry(pid, (int)entry.th32ParentProcessID, entry.szExeFile ?? string.Empty));
                }

                // 每轮用新结构体，避免上一轮较长的镜像名残留在缓冲区里
                entry = new NativeMethods.ProcessEntry32W { dwSize = size };
                if (!NativeMethods.Process32NextW(snapshot, ref entry))
                {
                    break;
                }
            }
        }
        catch
        {
            // 快照失败不该影响调用方，返回已收集到的部分
        }
        finally
        {
            if (snapshot != IntPtr.Zero && snapshot != NativeMethods.InvalidHandleValue)
            {
                try
                {
                    NativeMethods.CloseHandle(snapshot);
                }
                catch
                {
                    // 忽略句柄关闭失败
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 收集 <paramref name="pid"/> 的**全部后代**（不含自己），父在前的 BFS 顺序（越靠后 = 越深）。
    ///
    /// 关键性质：进程死了之后，它的子进程仍带着**原始父 PID**（Windows 不会重新认父），
    /// 所以"根已经退出、只剩孤儿"的场景照样能列出来 —— 这正是漏网事故里最难查的那一半。
    /// </summary>
    public static List<ProcessSnapshotEntry> DescendantsOf(int pid, List<ProcessSnapshotEntry>? snapshot = null)
    {
        var result = new List<ProcessSnapshotEntry>();
        if (pid <= 0)
        {
            return result;
        }

        var all = snapshot ?? SnapshotProcesses();
        if (all.Count == 0)
        {
            return result;
        }

        var seen = new HashSet<int> { pid };
        var queue = new Queue<int>();
        queue.Enqueue(pid);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var item in all)
            {
                if (item.ParentPid != current || !seen.Add(item.Pid))
                {
                    continue;
                }

                result.Add(item);
                queue.Enqueue(item.Pid);
            }
        }

        return result;
    }

    /// <summary>
    /// 指定 PID 是否**还是原来那个进程**（镜像名一致）。
    /// 加名字校验是为了防 PID 被系统复用后把"新来的无关进程"误判成残留。
    /// </summary>
    public static bool IsSameProcessAlive(int pid, string? imageName)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var bare = BareImageName(imageName);
            if (bare.Length == 0)
            {
                return true;
            }

            return string.Equals(process.ProcessName, bare, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // 查不到 = 已经退出了
            return false;
        }
        catch
        {
            // 拒绝访问 = 它还在那儿，只是我们读不到细节
            return true;
        }
    }

    /// <summary>把用户填写的进程名转成 taskkill /IM 可尝试的名称。</summary>
    public static List<string> ProcessNameCandidates(string? raw)
    {
        var result = new List<string>();
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return result;
        }

        var name = Path.GetFileName(text);
        AddUnique(result, name);
        if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            AddUnique(result, name + ".exe");
        }

        return result;
    }

    /// <summary>
    /// 按 Windows 镜像名检测进程是否在运行。
    /// 优先用托管 API（快、不受系统语言 / 代码页影响），失败再回落到 tasklist（与 Python 版一致）。
    /// 名称可以不带 .exe，也可以写成路径，内部会展开候选名。
    /// </summary>
    public static bool IsProcessNameRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        foreach (var candidate in ProcessNameCandidates(processName))
        {
            if (IsExactImageRunning(candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>单个镜像名（可带 .exe）的检测：托管 API 优先，tasklist 兜底。</summary>
    private static bool IsExactImageRunning(string imageName)
    {
        var bare = imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? imageName[..^4]
            : imageName;
        if (bare.Length == 0)
        {
            return false;
        }

        try
        {
            if (Process.GetProcessesByName(bare).Length > 0)
            {
                return true;
            }
        }
        catch
        {
            // 某些受保护进程枚举会抛异常，落到 tasklist 兜底
        }

        return TasklistHasImage(imageName);
    }

    /// <summary>tasklist /FI 兜底检测（对应 Python 版实现）。</summary>
    private static bool TasklistHasImage(string imageName)
    {
        try
        {
            var startInfo = new ProcessStartInfo("tasklist")
            {
                Arguments = $"/FI \"IMAGENAME eq {imageName}\" /NH",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.StandardOutputEncoding = Encoding.Default;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill(true);
            }

            var lower = output.ToLowerInvariant();
            if (lower.Contains("没有运行的任务") || lower.Contains("no tasks"))
            {
                return false;
            }

            // tasklist 命中时会回显镜像名（形如 smoke.exe  1234 Console ...）
            var bare = imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? imageName[..^4]
                : imageName;
            return lower.Contains(bare.ToLowerInvariant());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>兜底：按进程名 / 主窗口标题包含关键词检测。</summary>
    public static bool IsProcessKeywordPresent(string keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (IsProcessNameRunning(text))
        {
            return true;
        }

        try
        {
            var lower = text.ToLowerInvariant();
            var currentPid = Environment.ProcessId;
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == currentPid)
                    {
                        continue;
                    }

                    var name = process.ProcessName;
                    if (name.ToLowerInvariant().Contains(lower))
                    {
                        return true;
                    }

                    var title = process.MainWindowTitle;
                    if (!string.IsNullOrEmpty(title) && title.ToLowerInvariant().Contains(lower))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 进程已退出或拒绝访问，跳过
                }
            }
        }
        catch
        {
            // 忽略
        }

        return false;
    }

    /// <summary>按命令行关键词检测（PowerShell / CIM，沿用 Python 版实现）。</summary>
    public static bool IsCommandlineKeywordRunning(string keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var safe = text.Replace("'", "''");
        var ps = "Get-CimInstance Win32_Process | "
                 + $"Where-Object {{$_.CommandLine -like '*{safe}*'}} | "
                 + "Select-Object -First 1 -ExpandProperty ProcessId";

        try
        {
            var startInfo = new ProcessStartInfo("powershell")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command {ps}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                process.Kill(true);
            }

            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>是否存在包含指定关键词的可见窗口。</summary>
    public static bool IsWindowTitlePresent(string keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        foreach (var title in VisibleWindowTitles())
        {
            if (title.ToLowerInvariant().Contains(lower))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 关键词 → 用于匹配**窗口标题 / 镜像名**的键（小写、已剥扩展名）。
    ///
    /// 为什么必须剥：镜像名（<c>ProcessName</c>）本来就不带 <c>.exe</c>，
    /// 窗口标题是「原神」「星穹铁道」这种人类可读文本也不会带 ——
    /// 而游戏本体的真实镜像名往往是英文（原神 = <c>YuanShen</c>、崩铁 = <c>StarRail</c>），
    /// 按名字 <c>taskkill /IM</c> 注定失败，**只剩按窗口标题兜底这一条路**能把游戏清掉。
    /// 拿带扩展名的原始关键词去比标题（「原神.exe」vs「原神」）会让这条路整个失效。
    /// </summary>
    public static string TitleMatchKey(string? keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var name = Path.GetFileNameWithoutExtension(text).Trim();
        return (name.Length == 0 ? text : name).ToLowerInvariant();
    }

    /// <summary>按窗口标题关键词收集窗口所属 PID。</summary>
    public static List<int> WindowPidsByKeyword(string keyword)
    {
        var result = new List<int>();
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return result;
        }

        var lower = text.ToLowerInvariant();
        var pids = new List<int>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (title is null || !title.ToLowerInvariant().Contains(lower))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0 && !pids.Contains((int)pid))
            {
                pids.Add((int)pid);
            }

            return true;
        }, IntPtr.Zero);

        return pids;
    }

    /// <summary>枚举所有可见窗口标题。</summary>
    public static List<string> VisibleWindowTitles()
    {
        var titles = new List<string>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (title is not null)
            {
                titles.Add(title);
            }

            return true;
        }, IntPtr.Zero);

        return titles;
    }

    /// <summary>按镜像名结束进程树：taskkill /IM /T /F。返回结果用于日志回执。</summary>
    public static ProcessTerminateResult TerminateProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return ProcessTerminateResult.Skip("进程名为空");
        }

        // 【先查再杀】taskkill 哪怕目标根本不存在，也要花近 2 秒（实测 1931ms，
        // 它要跟内核做一轮往返）。清理名单里大量名字本来就是"已经不在"的状态，
        // 不先判断的话，每个名字白等 2 秒 —— 一次停止光这一项就能拖掉十几秒。
        if (!IsProcessNameRunning(processName))
        {
            return ProcessTerminateResult.Skip("未在运行");
        }

        var result = RunHidden("taskkill", $"/IM \"{processName}\" /T /F", 10000);
        return new ProcessTerminateResult(result.Ok, result.ExitCode, result.Detail);
    }

    /// <summary>按 PID 结束进程树：taskkill /PID /T /F。返回结果用于日志回执。</summary>
    public static ProcessTerminateResult TerminatePidTree(int pid)
    {
        if (pid <= 0)
        {
            return ProcessTerminateResult.Skip("PID 无效");
        }

        // 同上：PID 已经被系统回收 / 进程早已退出时，别让 taskkill 白跑 2 秒。
        if (!IsSameProcessAlive(pid, null))
        {
            return ProcessTerminateResult.Skip("进程已不存在");
        }

        var result = RunHidden("taskkill", $"/PID {pid} /T /F", 10000);
        return new ProcessTerminateResult(result.Ok, result.ExitCode, result.Detail);
    }

    /// <summary>
    /// 一次 taskkill 结束多个 PID（`/PID a /PID b …`）。
    ///
    /// <para>【为什么需要】taskkill 的固定开销约 2 秒/次，与"杀几个进程"无关。
    /// 逐个调用的代价是 N×2 秒：真机日志里一次停止要清 12 个后代，光这就 24 秒，
    /// 用户看到的是"点了停止，十几秒后才轮到游戏本体"。</para>
    ///
    /// <para>只有 1 个时退回单个版本；0 个时直接算成功（本来就没有）。</para>
    /// </summary>
    public static ProcessTerminateResult TerminatePidTreeBatch(IReadOnlyList<int> pids)
    {
        var alive = new List<int>();
        foreach (var pid in pids)
        {
            if (pid > 0 && !alive.Contains(pid) && IsSameProcessAlive(pid, null))
            {
                alive.Add(pid);
            }
        }

        if (alive.Count == 0)
        {
            return new ProcessTerminateResult(true, -1, "进程都已不存在");
        }

        if (alive.Count == 1)
        {
            return TerminatePidTree(alive[0]);
        }

        var arguments = new StringBuilder();
        foreach (var pid in alive)
        {
            arguments.Append("/PID ").Append(pid).Append(' ');
        }

        arguments.Append("/T /F");
        var result = RunHidden("taskkill", arguments.ToString(), 15000);
        return new ProcessTerminateResult(result.Ok, result.ExitCode, result.Detail);
    }

    /// <summary>
    /// 直接结束单个 PID，不走 taskkill（毫秒级）。返回 true = 已结束或本来就不在。
    /// 清理后代时先走这个，只有被拒的才退回批量 taskkill。
    /// </summary>
    public static bool TryKill(int pid) => pid > 0 && KillByPid(pid).Length == 0;

    /// <summary>
    /// 结束**整棵进程树**：taskkill 不成自己按 PPID 逐层收，最后**复查**才给结论。
    ///
    /// 【为什么不再信"托管 API 说成功"】2026-09-27 真机事故：
    /// 任务配的 `March7th Launcher.exe` 只是个壳，真正干活的引擎是它派生的 `March7th Assistant.exe`。
    /// 停止时 <c>taskkill /IM /T /F</c> 被拒（拒绝访问），降级 <c>Kill(entireProcessTree: true)</c>
    /// **回报"结束成功"，却只带走了根 PID**；漏网的子进程发现游戏窗口没了，
    /// 22:42:57 自己 `游戏终止 → 游戏启动` 把游戏重新拉起来，一直跑到 22:49 才收尾 ——
    /// 用户看到的就是"明明停止了，过一会又自己跑起来"。
    ///
    /// 所以本方法的纪律是：**任何结束动作之后都必须自己复查**，
    /// 结论只能来自"目标还在不在"，绝不来自 taskkill 的退出码或托管 API 没抛异常。
    ///
    /// 顺序也重要：父子关系必须在**动手之前**取（根一死，查询方就只剩孤儿记录），
    /// 结束时**从叶子往根**收（先断掉会自己重启的子进程，最后才收根）。
    /// </summary>
    public static ProcessTerminateResult TerminateProcessTree(int pid)
    {
        if (pid <= 0)
        {
            return ProcessTerminateResult.Skip("PID 无效");
        }

        // ① 先拍快照，把整棵树记下来 —— 动手之后父子关系就变了
        var snapshot = SnapshotProcesses();
        var targets = DescendantsOf(pid, snapshot);
        var root = FindById(snapshot, pid);
        if (root is not null)
        {
            targets.Insert(0, root);
        }

        if (targets.Count == 0)
        {
            // 根和后代都查不到 = 本来就不在
            return new ProcessTerminateResult(true, -1, "进程已不存在");
        }

        // ② taskkill 便宜且能带上我们枚举不到的层，先让它试一把
        var viaTaskkill = TerminatePidTree(pid);

        // ③ 复查①：不看退出码，看进程还在不在
        var remaining = AliveTargets(targets);
        if (remaining.Count == 0)
        {
            var how = viaTaskkill.Ok ? viaTaskkill.Detail : "taskkill 报错但复查确认已无残留";
            return new ProcessTerminateResult(true, viaTaskkill.ExitCode, DescribeKilled(targets, how));
        }

        // ④ 剩下的自己动手，**从叶子往根**（BFS 顺序倒过来就是最深优先）
        var failures = new List<string>();
        for (var i = remaining.Count - 1; i >= 0; i--)
        {
            var item = remaining[i];
            var reason = KillByPid(item.Pid);
            if (reason.Length > 0)
            {
                failures.Add($"{item}：{reason}");
            }
        }

        // ⑤ 复查②：结论必须是查出来的，不是猜出来的
        Thread.Sleep(200);
        var stillAlive = AliveTargets(targets);
        if (stillAlive.Count == 0)
        {
            var how = viaTaskkill.Ok ? "taskkill 部分生效" : "taskkill 被拒，改用托管 API 逐层结束";
            return new ProcessTerminateResult(true, -1, DescribeKilled(targets, how));
        }

        var names = new List<string>();
        foreach (var item in stillAlive)
        {
            AddUnique(names, item.ToString());
        }

        var prefix = failures.Count > 0 ? string.Join("；", failures) + "；" : string.Empty;
        return new ProcessTerminateResult(
            false,
            -1,
            $"{prefix}整树 {targets.Count} 个进程中仍有 {stillAlive.Count} 个在运行：{string.Join("、", names)}");
    }

    private static ProcessSnapshotEntry? FindById(List<ProcessSnapshotEntry> snapshot, int pid)
    {
        foreach (var item in snapshot)
        {
            if (item.Pid == pid)
            {
                return item;
            }
        }

        return null;
    }

    private static List<ProcessSnapshotEntry> AliveTargets(List<ProcessSnapshotEntry> targets)
    {
        var result = new List<ProcessSnapshotEntry>();
        foreach (var item in targets)
        {
            if (IsSameProcessAlive(item.Pid, item.Name))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>结束单个 PID（不管后代 —— 后代由调用方按顺序各自处理）。返回空串 = 成功或本来就不在。</summary>
    private static string KillByPid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(2000);
            return string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string DescribeKilled(List<ProcessSnapshotEntry> targets, string how)
    {
        var names = new List<string>();
        foreach (var item in targets)
        {
            AddUnique(names, item.Name.Length > 0 ? item.Name : $"PID {item.Pid}");
        }

        var list = names.Count > 4
            ? string.Join("、", names.GetRange(0, 4)) + $" 等 {names.Count} 种"
            : string.Join("、", names);

        return $"整棵进程树 {targets.Count} 个进程已结束（{list}）；{how}";
    }

    /// <summary>
    /// 给指定进程的所有可见顶层窗口发 WM_CLOSE —— 走游戏自己的"正常退出"流程。
    ///
    /// 为什么要这条：对带反作弊保护的游戏，强制结束进程会被驱动拦下；
    /// 但"请求窗口关闭"是应用层消息，走的是游戏自己的退出逻辑，反作弊不拦。
    /// 代价是有些游戏会弹一个"确认退出"对话框，需要用户或后续按键确认。
    /// </summary>
    public static int CloseMainWindowsByPid(int pid)
    {
        if (pid <= 0)
        {
            return 0;
        }

        var closed = 0;
        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
                if (owner != (uint)pid || !NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                if (NativeMethods.PostMessageW(hwnd, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero))
                {
                    closed++;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败不影响主流程
        }

        return closed;
    }

    /// <summary>向当前前台窗口发送一次 Enter（仅用于用户明确确认过的启动确认框）。</summary>
    public static void PressEnter()
    {
        NativeMethods.keybd_event(NativeMethods.VkReturn, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VkReturn, 0, NativeMethods.KeyEventFKeyUp, UIntPtr.Zero);
    }

    private static string? GetWindowTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return null;
        }

        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowTextW(hwnd, builder, builder.Capacity);
        return builder.Length > 0 ? builder.ToString() : null;
    }

    /// <summary>
    /// 跑一个隐藏外部命令并**把结果带回来**。
    ///
    /// 为什么必须带回来：以前这里把 taskkill 的输出与退出码全丢掉、异常也吞掉，
    /// 于是"结束失败"和"结束成功"在日志里长得一模一样。
    /// 输出用异步读，避免子进程写满管道缓冲导致 WaitForExit 卡住（经典的死锁写法）。
    /// </summary>
    private static (bool Ok, int ExitCode, string Detail) RunHidden(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // taskkill 在中文系统上按 ANSI/OEM 输出，用 UTF-8 读会乱码
            var encoding = ConsoleTextEncoding();
            startInfo.StandardOutputEncoding = encoding;
            startInfo.StandardErrorEncoding = encoding;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, -1, $"无法启动 {fileName}");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // 忽略：超时进程杀不掉也不该影响调用方
                }

                return (false, -1, $"{fileName} 超时（>{timeoutMs}ms）");
            }

            var text = ((stderr.Result ?? string.Empty) + (stdout.Result ?? string.Empty)).Trim();
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 300)
            {
                text = text[..300];
            }

            return (process.ExitCode == 0, process.ExitCode, text);
        }
        catch (Exception ex)
        {
            return (false, -1, ex.Message);
        }
    }

    /// <summary>取控制台命令的文本编码（中文系统 = CP936）；取不到就退回 UTF-8。</summary>
    private static Encoding ConsoleTextEncoding()
    {
        try
        {
            return Encoding.GetEncoding(936);
        }
        catch
        {
            // 未注册 CodePagesEncodingProvider 时 GetEncoding(936) 会抛，退回默认
            return Encoding.UTF8;
        }
    }

    private static void AddUnique(List<string> target, string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        foreach (var item in target)
        {
            if (string.Equals(item, text, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        target.Add(text);
    }
}
