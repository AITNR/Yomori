// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>WTS_CONNECTSTATE_CLASS 的公开映射，避免把 internal 的 P/Invoke 类型暴露到公开 API。</summary>
public enum SessionConnectState
{
    Active = 0,
    Connected = 1,
    ConnectQuery = 2,
    Shadow = 3,
    Disconnected = 4,
    Idle = 5,
    Listen = 6,
    Reset = 7,
    Down = 8,
    Init = 9,
}

public sealed class RdpSessionInfo
{
    public uint Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string WinStationName { get; set; } = string.Empty;

    public SessionConnectState State { get; set; }

    public bool IsActive => State is SessionConnectState.Active or SessionConnectState.Connected;

    public string Display => UserName.Length > 0 ? UserName : "(空闲会话)";

    public string StateText => State switch
    {
        SessionConnectState.Active => "运行中",
        SessionConnectState.Connected => "已连接",
        SessionConnectState.Disconnected => "已断开",
        SessionConnectState.Idle => "空闲",
        SessionConnectState.Listen => "监听中",
        _ => State.ToString(),
    };
}

/// <summary>预检结果：目标会话能不能连。</summary>
public sealed class RdpReadiness
{
    public bool UserExists { get; set; }
    public bool RemoteDesktopEnabled { get; set; }

    /// <summary>
    /// 会话代理是否【已部署】：只代表公共启动目录里的快捷方式文件存在。
    /// 这是"静态"状态，不代表代理此刻在跑 —— 在线与否看 <see cref="AgentOnline"/>。
    /// </summary>
    public bool AgentDeployed { get; set; }

    /// <summary>
    /// 会话代理是否【在线】：运行期实况（目标会话里有没有正在等待/执行的代理）。
    /// 与 <see cref="AgentDeployed"/> 分开，是为了把「还没部署代理」和「已部署但当前不在线」
    /// 两种截然不同的情况区分开 —— 前者要部署，后者要注销目标账户后重新连接。
    /// </summary>
    public bool AgentOnline { get; set; }

    public bool CredentialSaved { get; set; }
    public bool LikelySupported { get; set; } = true;
    public string EditionNote { get; set; } = string.Empty;

    /// <summary>目标是否本机。为 false 时账户 / 会话 / Agent 无法在本机查验。</summary>
    public bool IsLocal { get; set; } = true;

    /// <summary>非阻塞的提示信息（区别于 Problems）。</summary>
    public List<string> Notes { get; } = new();

    public List<string> Problems { get; } = new();

    public bool CanConnect => UserExists && RemoteDesktopEnabled && CredentialSaved && Problems.Count == 0;

    public string Summary => Problems.Count == 0 ? "环境就绪，可以连接。" : string.Join("；", Problems);
}

/// <summary>
/// RDP 会话管理：预检、凭据、连接、查询、断开、注销、Agent 部署。
/// 目标是 <see cref="RdpTargets.DefaultHost"/>（本机环回，切本机另一个账户）时，
/// 账户 / 会话 / Agent 都能在本机直接查验；目标为其他主机时，这些检查不适用，只做连通性提示。
/// </summary>
public static class RdpSessionService
{
    private const string StartupShortcutName = "Yomori RDP 会话代理.lnk";

    /// <summary>
    /// 改名前（v2.1.1 及更早）留下的自启项文件名。
    ///
    /// 它们指向 <c>%ProgramData%\YukinoChan\agent</c> —— 那份副本现在既不再创建、
    /// 也没人清理（部署只保证"新的那份"存在）。留着只会让每个账户登录时白试一次：
    /// 快捷方式指向不存在的 exe，Explorer 静默失败，用户什么提示都看不到。
    ///
    /// 安装包的 <c>[UninstallDelete]</c> 里也有同名条目，两边必须一致。
    /// </summary>
    private static readonly string[] LegacyStartupShortcutNames =
    {
        "雪乃酱 RDP 会话代理.lnk",
        "YukinoChan RDP 会话代理.lnk",
    };

    /// <summary>
    /// 代理副本专用 manifest 的文件名（内容 = asInvoker 版）。
    ///
    /// 为什么需要单独一份：主控端的 app.manifest 已改成 requireAdministrator
    /// （双击即管理员，无双进程闪烁）。但 requestedExecutionLevel 是**编译期静态**的，
    /// 同一个 exe 的 manifest 改不了；而代理副本（<see cref="CopyPayload"/>）是整个目录
    /// 原样复制出去的，同一个 Yomori.exe、同一份内嵌 manifest。
    ///
    /// 直接这么发，代理也跟着被要求管理员权限 —— 而它由公共启动目录快捷方式在
    /// **目标账户登录时无人值守**拉起，没人能点 UAC，代理就再也起不来。
    /// 表现为「通道连上了但任务不执行」，且部署日志一切正常，极难定位。
    ///
    /// 所以部署代理时用 mt.exe 把代理副本那份 exe 的内嵌 manifest **就地改回 asInvoker**。
    /// 主控端 exe 不受影响 —— 它本来就该要管理员。
    ///
    /// ⚠️ 别改回"用计划任务免 UAC"那条路：schtasks /RL HIGHEST 当年真机验证过，
    ///    连续 6 轮「触发计划任务失败：错误: 找不到元素。」，代理根本起不来
    ///    （见 memory/2026-09-25.md 阶段十六）。公共启动目录快捷方式才是验证过的选型。
    /// </summary>
    private const string AgentManifestFileName = "Yomori.agent.manifest";

    /// <summary>
    /// 刚保存凭据时暂存的密码（只在内存里，不写配置文件）。
    /// 程序重启后这里会是空的，那时改用 <see cref="TryLoadPassword"/> 从凭据管理器取。
    /// </summary>
    public static string? PendingPassword { get; set; }

    /// <summary>取连接用的密码：先看内存里刚存的，再回落到凭据管理器（按主机 + 账户查）。</summary>
    public static string? ResolvePassword(string? targetHost, string? targetUser = null)
    {
        if (!string.IsNullOrEmpty(PendingPassword))
        {
            return PendingPassword;
        }

        return TryLoadPassword(targetHost, targetUser);
    }

    // ---------------- 会话查询（WTS API） ----------------

    public static List<RdpSessionInfo> EnumerateSessions()
    {
        var result = new List<RdpSessionInfo>();
        var server = NativeMethods.WtsCurrentServerHandle;

        if (!NativeMethods.WTSEnumerateSessionsW(server, 0, 1, out var pInfo, out var count) || pInfo == IntPtr.Zero)
        {
            return result;
        }

        try
        {
            var size = Marshal.SizeOf<NativeMethods.WtsSessionInfo>();
            for (var i = 0; i < (int)count; i++)
            {
                var raw = Marshal.PtrToStructure<NativeMethods.WtsSessionInfo>(pInfo + (i * size));
                result.Add(new RdpSessionInfo
                {
                    Id = raw.SessionId,
                    UserName = QuerySessionString(raw.SessionId, NativeMethods.WtsUserName),
                    Domain = QuerySessionString(raw.SessionId, NativeMethods.WtsDomainName),
                    WinStationName = Marshal.PtrToStringUni(raw.WinStationName) ?? string.Empty,
                    State = (SessionConnectState)raw.State,
                });
            }
        }
        finally
        {
            NativeMethods.WTSFreeMemory(pInfo);
        }

        return result;
    }

    /// <summary>按用户名找会话。支持 "Player2" 与 "Machine\Player2" 两种写法。</summary>
    public static RdpSessionInfo? FindSession(string? userName)
    {
        var bare = BareName(userName);
        if (bare.Length == 0)
        {
            return null;
        }

        foreach (var session in EnumerateSessions())
        {
            if (string.Equals(session.UserName, bare, StringComparison.OrdinalIgnoreCase))
            {
                return session;
            }
        }

        return null;
    }

    /// <summary>
    /// 把 "Machine\User" / "User" 统一成裸账户名，便于跨写法比较。
    /// 会话查询、代理在线判定都依赖它，避免 "Machine\Player2" 与代理回传的 "Player2" 对不上。
    /// </summary>
    private static string BareName(string? userName)
    {
        var text = (userName ?? string.Empty).Trim();
        return text.Contains('\\') ? text[(text.LastIndexOf('\\') + 1)..] : text;
    }

    public static bool IsSessionActive(string? userName)
    {
        var session = FindSession(userName);
        return session is not null && session.IsActive;
    }

    /// <summary>
    /// 目标账户是否已经登录着（本机枚举到 Active / Connected 会话），可以直接复用。
    ///
    /// 为什么必须先问这一句再决定要不要启动 mstsc：
    ///   Windows 客户端版同时只允许一个交互式会话，再连一次不是"多开一个窗口"，
    ///   而是把目标账户正在用的那个桌面接管过来（当前桌面则被踢到锁屏）。
    ///   会话已经在了，代理直接在里面跑就行 —— 新建连接对执行任务没有任何帮助，
    ///   只有副作用。
    ///
    /// 注意「已断开」（Disconnected）不算：登录态还在，但没有交互式画面在显示，
    /// 想让用户看到桌面仍然需要用 mstsc 重连（重连会接回同一个会话，不会新建）。
    /// </summary>
    public static bool TryGetReusableSession(string? userName, out RdpSessionInfo? session)
    {
        session = FindSession(userName);
        return session is not null && session.IsActive;
    }

    /// <summary>
    /// 等待目标用户的会话变成 Active（远程桌面连接后需要几十秒登录）。
    /// </summary>
    /// <param name="embedded">
    /// true 表示走内嵌画面：登录的是本机账户，同时只允许一个交互式会话，
    /// 当前这个桌面会被顶掉、WTS 里就查不到自己的会话了，所以只看目标账户是否 Active，
    /// 不能把「自己的会话还在」当成还没连上。
    /// </param>
    /// <summary>
    /// 等目标账户的交互式会话真正建立（WTS 能枚举到）。
    ///
    /// 注意：这个方法只判断"会话存在"，也就是 TCP 已连上。
    /// 独立 mstsc 窗口模式下这是够用的 —— 登录失败时 mstsc 自己会报错，
    /// 而且登录不成功时目标会话不会被置为 Active。
    /// </summary>
    public static bool WaitForSession(
        string? userName,
        int timeoutSeconds,
        CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));

        while (DateTime.UtcNow < deadline)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            if (IsSessionActive(userName))
            {
                return true;
            }

            Thread.Sleep(1000);
        }

        return IsSessionActive(userName);
    }

    private static string QuerySessionString(uint sessionId, int infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(
                NativeMethods.WtsCurrentServerHandle, sessionId, infoClass, out var buffer, out _)
            || buffer == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    // ---------------- 远程桌面开关 ----------------

    public static bool IsRemoteDesktopEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"System\CurrentControlSet\Control\Terminal Server");
            var value = key?.GetValue("fDenyTSConnections");
            return value is int number && number == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 开启远程桌面并放行防火墙规则，需要管理员权限。
    ///
    /// 界面已不再提供一键入口（2026-10-24 按需求移除「开启远程桌面」按钮），
    /// 由用户在目标机器的系统设置里自行开启；这里保留能力，预检仍只读检测不开写。
    /// </summary>
    public static bool EnableRemoteDesktop(out string message)
    {
        message = string.Empty;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"System\CurrentControlSet\Control\Terminal Server", writable: true);
            if (key is null)
            {
                message = "无法打开终端服务注册表项，请以管理员身份重试。";
                return false;
            }

            key.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            message = $"写入注册表失败（需要管理员权限）：{ex.Message}";
            return false;
        }

        // 防火墙放行；这一步失败不致命（本机环回通常不受入站规则限制），只作提示。
        var firewallOk = RunHidden("netsh", "advfirewall firewall set rule group=\"remote desktop\" new enable=Yes");
        message = firewallOk
            ? "已开启远程桌面，并已放行防火墙规则。若当前账号不是管理员，此操作可能无效。"
            : "已开启远程桌面，但防火墙规则放行失败（本机环回连接通常不受影响）。";
        return true;
    }

    /// <summary>Windows 家庭版不能作为 RDP 服务端，这里给出提示而不是硬阻断。</summary>
    public static void DescribeEdition(RdpReadiness readiness)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName")?.ToString() ?? string.Empty;
            var edition = key?.GetValue("EditionID")?.ToString() ?? string.Empty;

            readiness.EditionNote = product;

            var home = product.Contains("Home", StringComparison.OrdinalIgnoreCase)
                       || product.Contains("家庭", StringComparison.Ordinal)
                       || edition.Contains("Core", StringComparison.OrdinalIgnoreCase);

            if (home)
            {
                readiness.LikelySupported = false;
                readiness.Problems.Add("检测到 Windows 家庭版，该版本不能作为远程桌面服务端，无法用 RDP 登录其它账户");
            }
        }
        catch
        {
            // 读不到版本就不做限制
        }
    }

    // ---------------- 账户与凭据 ----------------

    public static bool UserExists(string? userName)
    {
        var bare = BareName(userName);
        if (bare.Length == 0)
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo("net")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("user");
            startInfo.ArgumentList.Add(bare);
            startInfo.StandardOutputEncoding = Encoding.Default;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把目标账户凭据存入 Windows 凭据管理器。密码不写入 config.json。
    /// 同时存 "目标主机" 与 "TERMSRV/目标主机" 两个条目，覆盖 mstsc 的不同查找顺序。
    /// </summary>
    public static bool SaveCredential(string? targetHost, string userName, string password, out string message)
    {
        message = string.Empty;
        var host = RdpTargets.Normalize(targetHost);

        if (string.IsNullOrWhiteSpace(userName) || password.Length == 0)
        {
            message = "账户名或密码为空。";
            return false;
        }

        var okA = CmdKey($"/generic:{host}", $"/user:{userName}", $"/pass:{password}");
        var okB = CmdKey($"/generic:TERMSRV/{host}", $"/user:{userName}", $"/pass:{password}");

        // 内嵌的 ActiveX 控件不会主动去凭据管理器取密码（那是 mstsc 的行为），
        // 所以额外用 CredWrite 写一条 TERMSRV 域名账户，配合 CredRead 在连接时取回来。
        var okC = RdpCredentialStore.Write(host, userName, password);

        if (okA || okB || okC)
        {
            message = $"凭据已保存（目标：{host}），配置文件里不会保存密码。";
            return true;
        }

        message = "保存凭据失败，请确认账户名格式（例如 Player2 或 机器名\\Player2）。";
        return false;
    }

    /// <summary>
    /// 删除目标账户凭据。清完后再探一次，确认真的删干净了。
    /// </summary>
    public static void DeleteCredential(string? targetHost, string? targetUser = null)
    {
        var host = RdpTargets.Normalize(targetHost);

        // cmdkey 那两条是给 mstsc 用的（按主机存，多账户会互相覆盖，只有单通道调试才走这条路）
        RunHidden("cmdkey", $"/delete:{host}");
        RunHidden("cmdkey", $"/delete:TERMSRV/{host}");

        RdpCredentialStore.Delete(host, targetUser);
    }

    /// <summary>取出之前保存的目标账户密码，交给内嵌客户端用；没有就返回 null。</summary>
    public static string? TryLoadPassword(string? targetHost, string? targetUser = null) =>
        RdpCredentialStore.Read(RdpTargets.Normalize(targetHost), targetUser);

    /// <summary>
    /// 凭据是否已保存。
    ///
    /// 只认自己用 CredWrite 写的那一条 —— 这是唯一能真正取回密码的来源。
    /// cmdkey 那条是给 mstsc 兜底用的，删掉之后它可能残留，拿它判断会出现
    /// "界面说已保存、实际连不上" 的假阳性，所以这里不用它。
    /// </summary>
    public static bool HasCredential(string? targetHost, string? targetUser = null)
    {
        return RdpCredentialStore.Exists(RdpTargets.Normalize(targetHost), targetUser);
    }

    private static bool CmdKey(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmdkey")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // 用 ArgumentList 让 .NET 负责转义，密码里的空格/引号不会被拆坏
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 连接与收尾 ----------------

    /// <summary>
    /// 临时生成的 .rdp 文件放在这里，每次连接覆盖写。
    /// 放 LocalAppData 而不是 ProgramData：写这里不需要管理员权限。
    /// </summary>
    private static string SessionFilePath => SessionFilePathFor(null);

    /// <summary>
    /// 某条通道（或默认）的 .rdp 文件路径。
    ///
    /// 多通道并行时每条通道必须各用一份文件名 —— 共用一个 session.rdp 会互相覆盖：
    /// 后连接的通道会把前一条的分辨率 / 主机写花，而 mstsc 是在**启动那一刻**读文件的，
    /// 于是先开的窗口可能拿到别人的设置。
    /// </summary>
    internal static string SessionFilePathFor(string? sessionKey)
    {
        var name = string.IsNullOrWhiteSpace(sessionKey) ? "session" : $"session-{SanitizeKey(sessionKey)}";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yomori",
            name + ".rdp");
    }

    /// <summary>把通道 id 收敛成安全的文件名片段（只保留字母数字与 -_，其余替换为 _）。</summary>
    private static string SanitizeKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        return builder.Length == 0 ? "channel" : builder.ToString();
    }

    /// <summary>
    /// 启动 mstsc 连接目标主机；凭据由凭据管理器自动提供。
    /// 目标是本机时走环回（切本机另一个账户），否则连到指定 IP / 主机名。
    /// 指定了分辨率（width/height 均 &gt; 0）时生成一份 .rdp 配置让 mstsc 按这个尺寸开窗口。
    /// </summary>
    /// <param name="targetHost">目标主机，可带端口。</param>
    /// <param name="message">结果说明。</param>
    /// <param name="width">窗口宽度，0 = 用 mstsc 默认。</param>
    /// <param name="height">窗口高度，0 = 用 mstsc 默认。</param>
    /// <param name="sessionKey">通道 id；多通道并行时用来分隔各自的 .rdp 文件。</param>
    public static bool Connect(
        string? targetHost, out string message, int width = 0, int height = 0, string? sessionKey = null)
    {
        message = string.Empty;
        var host = RdpTargets.Normalize(targetHost);
        try
        {
            var (address, port) = SplitHostPort(host);

            // 有指定分辨率就写一份 .rdp，让 mstsc 按尺寸开窗口；
            // 自适应（0×0）时直接命令行连，交给 mstsc 用默认尺寸。
            string? sessionFile = null;
            if (width > 0 && height > 0)
            {
                sessionFile = WriteSessionFile(address, port, width, height, sessionKey, out var writeError);
                if (sessionFile is null)
                {
                    message = writeError;
                    return false;
                }
            }

            var startInfo = new ProcessStartInfo("mstsc")
            {
                UseShellExecute = true,
            };

            if (sessionFile is not null)
            {
                startInfo.ArgumentList.Add(sessionFile);
            }
            else
            {
                startInfo.ArgumentList.Add($"/v:{host}");
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                message = "无法启动远程桌面客户端（mstsc）。";
                return false;
            }

            message = width > 0 && height > 0
                ? $"已启动远程桌面连接（{host}，{width} × {height}），正在登录目标账户。"
                : $"已启动远程桌面连接（{host}），正在登录目标账户。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"启动远程桌面失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 把 "192.168.1.20:3390" 拆成地址和端口；没带端口时端口为 0。
    /// IPv6 的冒号和端口分隔符长得一样，所以只认两种安全写法：
    /// <c>[::1]:3390</c>（有方括号）和 <c>192.168.1.20:3390</c>（只有一段地址）。
    /// 裸 IPv6 如 <c>::1</c> 里冒号不止一个，一律整体当主机名，不拆。
    /// </summary>
    internal static (string Address, int Port) SplitHostPort(string host)
    {
        var colon = host.LastIndexOf(':');
        if (colon <= 0)
        {
            return (host, 0);
        }

        // 有方括号：形如 [::1]:3390，冒号必须出现在 ] 之后才是端口分隔符
        var bracketEnd = host.LastIndexOf(']');
        if (bracketEnd >= 0)
        {
            if (colon < bracketEnd)
            {
                return (host, 0);
            }

            var portAfterBracket = host[(colon + 1)..];
            if (!int.TryParse(portAfterBracket, out var bracketPort) || bracketPort is <= 0 or > 65535)
            {
                return (host, 0);
            }

            return (host[1..bracketEnd], bracketPort);
        }

        // 没方括号但出现多个冒号：是裸 IPv6，整体当主机名
        if (host.IndexOf(':') != colon)
        {
            return (host, 0);
        }

        var portText = host[(colon + 1)..];
        if (!int.TryParse(portText, out var port) || port is <= 0 or > 65535)
        {
            return (host, 0);
        }

        return (host[..colon], port);
    }

    /// <summary>
    /// 拼一份 .rdp 配置，让 mstsc 按固定的 width × height 开窗口。
    ///
    /// 注意 mstsc 的合并规则：.rdp 里**没写**的项，它会拿上一次会话的设置来补。
    /// 所以「指定了分辨率却不生效」几乎都是漏写了下面这几个开关造成的 ——
    /// 光写 desktopwidth/desktopheight 是没用的，必须把会覆盖它们的项一起钉死。
    ///
    /// 抽成纯函数是为了能单独测；落盘由 <see cref="WriteSessionFile"/> 负责。
    /// </summary>
    internal static string BuildSessionContent(string address, int port, int width, int height)
    {
        var builder = new StringBuilder();
        builder.AppendLine("full address:s:" + address);
        if (port > 0)
        {
            builder.AppendLine($"server port:i:{port}");
        }

        // 【必须】显式声明窗口模式。
        // 不写 screen mode id 时 mstsc 会沿用默认的全屏模式（i:2），
        // 而全屏下远程桌面尺寸由客户端显示器决定 ——
        // desktopwidth / desktopheight 直接被忽略，画面永远铺满整个屏幕。
        builder.AppendLine("screen mode id:i:1");

        builder.AppendLine($"desktopwidth:i:{width}");
        builder.AppendLine($"desktopheight:i:{height}");

        // 【必须】关掉动态分辨率。
        // 打开时远程桌面会跟随客户端窗口尺寸自适应，等于把上面的宽高又覆盖掉。
        // 这里要的是「按档位固定尺寸」，所以关掉。
        builder.AppendLine("dynamic resolution:i:0");

        // 【必须】关掉多显示器。
        // 用户可能在某次会话里勾过「在所有显示器上使用我的会话」，
        // 那项一旦被继承，固定分辨率同样失效（画面会横跨多个屏幕）。
        builder.AppendLine("use multimon:i:0");

        // 智能缩放：窗口比远程桌面小时整体缩放显示，不出现滚动条
        builder.AppendLine("smart sizing:i:1");

        // 凭据走凭据管理器，这里不写用户名，避免和保存的凭据冲突
        builder.AppendLine("prompt for credentials:i:0");
        builder.AppendLine("authentication level:i:2");

        return builder.ToString();
    }

    /// <summary>
    /// 把 .rdp 配置写到临时文件。
    /// 返回 null 表示写失败，错误原因在 error 里。
    /// </summary>
    /// <param name="sessionKey">通道 id；多通道并行时各自一份文件，避免互相覆盖。</param>
    internal static string? WriteSessionFile(
        string address, int port, int width, int height, string? sessionKey, out string error)
    {
        error = string.Empty;
        try
        {
            var path = SessionFilePathFor(sessionKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildSessionContent(address, port, width, height), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            error = $"生成远程桌面配置文件失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>清掉上一次生成、当前已不再使用的 .rdp 文件。</summary>
    public static void CleanupSessionFile()
    {
        try
        {
            if (File.Exists(SessionFilePath))
            {
                File.Delete(SessionFilePath);
            }
        }
        catch
        {
            // 删不掉就留着，下次连接会覆盖
        }
    }

    public static bool DisconnectSession(uint sessionId, out string message)
    {
        message = string.Empty;
        if (NativeMethods.WTSDisconnectSession(NativeMethods.WtsCurrentServerHandle, sessionId, false))
        {
            message = $"已断开会话 {sessionId}（后台仍在运行）。";
            return true;
        }

        message = $"断开会话 {sessionId} 失败。";
        return false;
    }

    public static bool LogoffSession(uint sessionId, out string message)
    {
        message = string.Empty;
        if (NativeMethods.WTSLogoffSession(NativeMethods.WtsCurrentServerHandle, sessionId, false))
        {
            message = $"已注销会话 {sessionId}。";
            return true;
        }

        message = $"注销会话 {sessionId} 失败（可能需要管理员权限）。";
        return false;
    }

    // ---------------- Agent 部署 ----------------

    public static string AgentShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupShortcutName);

    /// <summary>
    /// 代理程序副本的公共根目录。目标账户必须读得到，所以放 ProgramData ——
    /// 不能直接指向主控端的 exe：那个通常躺在 C:\Users\&lt;主控账户&gt;\ 下，
    /// 目标账户读不到，快捷方式会被拒绝启动。
    /// </summary>
    public static string AgentPayloadRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Yomori");

    /// <summary>首选副本目录。</summary>
    public static string AgentProgramDir => Path.Combine(AgentPayloadRoot, "agent");

    /// <summary>
    /// 备用副本目录。首选那份被运行中的代理锁着、又送不走它时改用它，
    /// 详见 <see cref="EnsureAgentPayload"/> 的说明。
    /// </summary>
    public static string AgentProgramDirSpare => Path.Combine(AgentPayloadRoot, "agent_b");

    /// <summary>精简代理的主程序名（独立工程 <c>src/Yomori.Agent</c> 的产物）。</summary>
    internal const string AgentExeName = "Yomori.Agent.exe";

    /// <summary>
    /// 旧式代理的主程序名：整目录复制时代，代理就是主控端自己的 exe。
    /// 只用于「识别并清掉旧部署」，新部署一律用 <see cref="AgentExeName"/>。
    /// </summary>
    internal const string LegacyAgentExeName = "Yomori.exe";

    /// <summary>
    /// 随包分发的精简代理目录：安装目录下的 <c>agent\</c>（打包脚本铺、安装包带）。
    ///
    /// 为什么不在部署时现场编译/挑文件：代理副本必须是「目标账户读得到、**不依赖主控端目录**」
    /// 的一整套程序，现场挑文件的规则一旦和主控端的依赖图脱节，就会变成
    /// 「主控端好用、目标账户 FileNotFound」这种最难查的故障。发布时由 CI 用独立的
    /// 工程（不含 WinUI）产出一份完整可运行的程序，部署只负责搬运。
    /// </summary>
    public static string ShippedAgentDir => Path.Combine(AppPaths.BaseDir, "agent");

    public static string AgentProgramPath => Path.Combine(AgentProgramDir, AgentExeName);

    /// <summary>副本候选目录，按优先级排列（internal 供冒烟断言"两槽不同"）。</summary>
    internal static string[] PayloadSlotDirs() => new[] { AgentProgramDir, AgentProgramDirSpare };

    public static bool IsAgentDeployed() => File.Exists(AgentShortcutPath);

    /// <summary>
    /// 铺好代理程序副本，返回真正生效的那份主程序路径。
    /// 托管程序、依赖配置及 XAML 资源内容一致才跳过，省掉一次 140MB 的搬运。
    ///
    /// 这里要绕的坎：会话代理是**常驻进程**（登录后一直活着等指令），它把副本目录里的
    /// CoreMessagingXP.dll 之类的依赖持有着。此时 <c>File.Copy(overwrite: true)</c> 会抛
    /// 「The process cannot access the file ... because it is being used by another process」。
    /// 而且**连把整个目录改名都做不到** —— 实测只要目录内有文件被持有句柄，
    /// MoveFile 就返回 Access denied，无论对方用的是哪种共享模式（None / Read /
    /// Read+Delete / ReadWrite+Delete 全试过）。所以「先挪开旧目录再重建」这条路是死的。
    ///
    /// 可行的两步：
    ///   ① 先把旧代理送走（仅当它空闲），然后原地覆盖 —— 路径不变，最干净；
    ///   ② 送不走（跨会话杀不掉、或它正在跑任务）就改用备用目录 agent_b：
    ///      旧代理继续用它那份，新副本落在新路径，快捷方式改指新路径。
    /// </summary>
    /// <param name="sourceExe">主控端正在运行的主程序。</param>
    /// <param name="targetUser">目标账户，用于判断代理是不是正在执行任务。</param>
    /// <param name="targetExe">最终生效的代理主程序完整路径。</param>
    /// <param name="error">失败原因。</param>
    /// <param name="note">过程说明（送走了旧代理 / 改用了备用目录）。</param>
    private static bool EnsureAgentPayload(
        string sourceExe, string? targetUser, out string targetExe, out string error, out string note)
    {
        error = string.Empty;
        note = string.Empty;
        targetExe = AgentProgramPath;

        var sourceDir = Path.GetDirectoryName(sourceExe);
        if (string.IsNullOrEmpty(sourceDir))
        {
            error = $"无法确定主程序所在目录：{sourceExe}";
            return false;
        }

        // 主控端本身就是从公共副本目录跑起来的（整个程序装在那里）→ 不用复制
        if (IsUnder(sourceDir, AgentPayloadRoot))
        {
            return true;
        }

        var tried = new List<string>();
        var notes = new List<string>();
        var failure = string.Empty;
        var refusal = string.Empty;
        string? chosen = null;
        var agentStopped = false;

        var sourceExeName = Path.GetFileName(sourceExe);

        foreach (var slot in PayloadSlotDirs())
        {
            var exe = Path.Combine(slot, sourceExeName);

            // ① 业务 DLL / 配置 / PRI 内容一致才复用；manifest 改写后的 EXE 时间不代表版本。
            if (AgentPayloadIdentity.Matches(sourceExe, exe))
            {
                chosen = slot;
                break;
            }

            // ② 空槽 → 直接铺一份
            if (!Directory.Exists(slot))
            {
                if (CopyPayload(sourceDir, slot, out var createError))
                {
                    chosen = slot;
                    notes.Add(DescribeSlot(slot));
                    break;
                }

                failure = createError;
                tried.Add(slot);
                continue;
            }

            // ③ 有旧副本、要更新。先看是不是被运行中的代理锁着。
            if (IsPayloadLocked(slot))
            {
                if (!agentStopped)
                {
                    agentStopped = true;
                    if (!StopAgentProcess(targetUser, out var stopNote, out refusal))
                    {
                        // 代理正在执行任务：不硬来，让用户自己决定什么时候部署
                        break;
                    }

                    if (stopNote.Length > 0)
                    {
                        notes.Add(stopNote);
                    }
                }

                // 还是锁着（多半是跨会话杀不掉）→ 退到下一个槽
                if (IsPayloadLocked(slot))
                {
                    tried.Add(slot);
                    continue;
                }
            }

            // ③.5 形态变了（旧部署是整目录复制的 Yomori.exe，新部署是精简的 Yomori.Agent.exe）
            //      → 必须整目录重建。只覆盖不但降不下占用，反而更糟：
            //      旧副本里那 150MB 界面栈不在新载荷清单里，永远不会被覆盖，会一直躺着。
            if (NeedsShapeRebuild(slot, sourceExeName))
            {
                if (!TryDeleteDirectory(slot, out var deleteError))
                {
                    failure = deleteError;
                    tried.Add(slot);
                    continue;
                }

                if (CopyPayload(sourceDir, slot, out var rebuildError))
                {
                    chosen = slot;
                    var rebuilt = DescribeSlot(slot);
                    notes.Add(rebuilt.Length > 0
                        ? rebuilt + "；旧的全量副本已按精简载荷重建"
                        : "旧的全量副本已按精简载荷重建");
                    break;
                }

                failure = rebuildError;
                tried.Add(slot);
                continue;
            }

            // ④ 没被锁 → 原地覆盖
            if (CopyPayload(sourceDir, slot, out var copyError))
            {
                chosen = slot;
                notes.Add(DescribeSlot(slot));
                break;
            }

            failure = copyError;
            tried.Add(slot);
        }

        note = JoinNotes(notes);

        if (chosen is not null)
        {
            targetExe = Path.Combine(chosen, sourceExeName);
            OpenAgentDataDirs(chosen);
            return true;
        }

        if (refusal.Length > 0)
        {
            error = refusal;
            return false;
        }

        error = $"部署代理程序副本失败。已尝试：{string.Join("、", tried)}"
            + (failure.Length > 0 ? $"；最后一次报错：{failure}" : string.Empty)
            + "。若提示文件被占用，注销目标账户（其中的会话代理会随之结束）或重启后再试。";
        return false;
    }

    /// <summary>
    /// 给代理运行期数据目录（logs / runtime_stats）放开 BUILTIN\Users 完全控制，并让新建内容继承。
    ///
    /// 为什么需要：ProgramData 默认 ACL 只给了 Users「读 + 建新文件」，而文件归创建者所有
    /// （CREATOR OWNER）。多个账户共用同一份代理副本时，A 账户建的 last_abnormal_report.json
    /// 之类固定名文件，B 账户改不动 → 写统计抛 UnauthorizedAccessException。
    /// 代理本身还会按账户再分子目录（AppPaths.AgentMode），这里只是兜底，失败也不阻断部署。
    /// </summary>
    internal static void OpenAgentDataDirs(string agentDir)
    {
        foreach (var name in new[] { "logs", "runtime_stats" })
        {
            var dir = Path.Combine(agentDir, name);
            try
            {
                Directory.CreateDirectory(dir);

                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                new DirectoryInfo(dir).SetAccessControl(security);
            }
            catch
            {
                // 权限设置失败不阻断部署：按账户分家后通常已经够用
            }
        }
    }

    /// <summary>把若干说明拼成一句话（跳过空项）。</summary>
    private static string JoinNotes(List<string> notes)
    {
        var cleaned = new List<string>();
        foreach (var item in notes)
        {
            if (item.Length > 0)
            {
                cleaned.Add(item);
            }
        }

        return string.Join("；", cleaned);
    }

    /// <summary>用了备用目录就说一声，免得用户看到快捷方式指向 agent_b 犯嘀咕。</summary>
    private static string DescribeSlot(string slot) =>
        string.Equals(slot, AgentProgramDir, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"首选副本目录被运行中的代理占用，已改用备用目录 {slot}";

    /// <summary>把主程序连同依赖铺到一个副本目录里。</summary>
    private static bool CopyPayload(string sourceDir, string targetDir, out string error)
    {
        error = string.Empty;

        try
        {
            Directory.CreateDirectory(targetDir);
            CopyDirectory(sourceDir, targetDir);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 副本目录是不是被运行中的进程锁着（能不能覆盖）。
    ///
    /// 只看主程序一个文件就够：代理进程一跑起来，它的 exe 映像必然被持有，
    /// 而这个文件覆盖不了，整个部署就没有意义。只探一个也把对运行中代理的
    /// 干扰降到最低（探测要拿一次独占写句柄）。
    ///
    /// 两种文件名都要探：旧部署跑的是 <c>Yomori.exe</c>，新部署是 <c>Yomori.Agent.exe</c>，
    /// 升级当口可能正好是旧的那份在跑。
    /// </summary>
    internal static bool IsPayloadLocked(string dir)
    {
        foreach (var name in new[] { AgentExeName, LegacyAgentExeName })
        {
            var exe = Path.Combine(dir, name);
            if (!File.Exists(exe))
            {
                continue;
            }

            try
            {
                using var probe = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 这个槽里的旧副本是不是「另一种形态」，需要整目录重建而不是覆盖。
    ///
    /// 判定只看主程序名：旧部署 = 主控端 exe 的整目录副本（<c>Yomori.exe</c> 在），
    /// 新部署 = 精简代理（<c>Yomori.Agent.exe</c> 在）。两者同名文件极少，
    /// 覆盖式更新会让旧形态独有的文件（WinUI / WindowsAppSDK / ONNX 那些）永久留存 ——
    /// 而这次改工程的全部目的就是把它们清掉。
    /// </summary>
    internal static bool NeedsShapeRebuild(string dir, string sourceExeName)
    {
        var expected = Path.Combine(dir, sourceExeName);
        if (File.Exists(expected))
        {
            return false;
        }

        // 目标目录里一个能跑的主程序都没有 → 当空壳处理，重建。
        // 有别的形态的主程序 → 也是重建。
        return true;
    }

    /// <summary>
    /// 递归删除副本目录。先清只读属性：旧代理副本可能被 mt.exe 改写过资源，
    /// 个别文件带只读位，直接 Delete 会半途失败留下不完整的目录。
    /// </summary>
    private static bool TryDeleteDirectory(string dir, out string error)
    {
        error = string.Empty;

        try
        {
            if (!Directory.Exists(dir))
            {
                return true;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch
                {
                    // 清不掉属性就让下面的删除自己报错，别在这里吞掉原因
                }
            }

            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            error = $"清空旧代理副本失败：{dir}\n{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 把常驻的旧代理送走，好让它占着的程序文件能被覆盖。
    ///
    /// 只在它空闲时动手：**正在执行任务就直接拒绝部署**（返回 false + 原因），
    /// 免得把用户跑了一半的活打断。跨会话 / 权限不足导致杀不掉不算错误 ——
    /// 调用方会退到备用副本目录，照样能部署成功。
    /// </summary>
    /// <param name="targetUser">目标账户，用于读它那路桥的状态。</param>
    /// <param name="note">成功时的说明（结束了几个进程）。</param>
    /// <param name="refusal">拒绝部署的原因。</param>
    private static bool StopAgentProcess(string? targetUser, out string note, out string refusal)
    {
        note = string.Empty;
        refusal = string.Empty;

        // 代理正在跑任务就别动它。它那路桥的目录按目标账户派生（见 RdpChannelPaths）。
        // "stopping" 也算忙：停止请求已收到、正在按三层顺序杀进程，
        // 这时候把代理杀掉，目标进程就没人收了（用户会看到"停了但游戏还在"）。
        var status = RdpBridge.For(RdpChannelPaths.Resolve(null, targetUser)).TryReadStatus();
        if (status is not null && status.Phase is "running" or "stopping")
        {
            // 光看 "running" 三个字不够：注销目标账户会把代理一起杀掉，而 status.json
            // 会永远停在最后一帧的 running（见 RdpModels 里那段说明）。要是就这么拦着，
            // 用户以后再也部署不了 —— 所以要求心跳也是新鲜的，才算真在跑任务。
            var heartbeat = RdpHeartbeat.Evaluate(status.UpdatedAt, status.Phase, DateTimeOffset.Now);
            if (heartbeat.Parsed && !heartbeat.IsStale)
            {
                var who = string.IsNullOrWhiteSpace(targetUser) ? "目标账户" : targetUser;
                var busy = status.Phase == "stopping" ? "正在按停止请求收尾" : "正在执行任务";
                refusal = $"{who} 的会话代理{busy}，现在部署会打断它。"
                    + "请等它彻底空下来，或先点「停止执行」，然后再部署。";
                return false;
            }
        }

        var killed = 0;
        foreach (var process in EnumerateAgentProcesses())
        {
            try
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId || !IsAgentProcess(process))
                    {
                        continue;
                    }

                    process.Kill();
                    process.WaitForExit(3000);
                    killed++;
                }
            }
            catch
            {
                // 单个进程杀不掉不影响整体：剩下的占用由备用目录兜住
            }
        }

        if (killed > 0)
        {
            note = $"已结束 {killed} 个正在运行的旧会话代理";
        }

        return true;
    }

    /// <summary>
    /// 代理副本里可能出现的进程名。
    ///
    /// 两个都要认：新部署跑 <c>Yomori.Agent.exe</c>，旧部署跑主控端那一份 <c>Yomori.exe</c>。
    /// 升级当口正是「旧的那份还在跑、新的要覆盖」的时刻，只认一个就会漏杀，
    /// 表现为「部署成功但重启后还是旧程序」。
    /// <para>⚠️ 按名字取到的进程**必须**再经 <see cref="IsAgentProcess"/> 按路径过滤：
    /// <c>Yomori</c> 这个名字也匹配用户正开着的主控端窗口，直接 Kill 就是把人家的界面干掉。</para>
    /// </summary>
    private static IEnumerable<Process> EnumerateAgentProcesses()
    {
        foreach (var name in new[] { "Yomori.Agent", "Yomori" })
        {
            Process[] batch;
            try
            {
                batch = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var process in batch)
            {
                yield return process;
            }
        }
    }

    /// <summary>
    /// 这个进程是不是从代理副本目录里跑起来的。
    /// 加这道判断是为了别误杀用户另外开着的主控端窗口。
    /// </summary>
    private static bool IsAgentProcess(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return !string.IsNullOrEmpty(path)
                && path.StartsWith(AgentPayloadRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 读不到模块路径（跨会话且权限不足）时不敢乱杀，交给备用目录方案兜底
            return false;
        }
    }

    /// <summary>path 是否落在 root 目录之下（含等于）。</summary>
    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\');

        return full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>递归复制目录，跳过日志/配置这类运行时产物，避免把主控端的状态带过去。</summary>
    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);

            // 运行时产物不复制：目标账户要自己生成
            if (name.Equals("config.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(targetDir, name), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(directory);

            // 日志和统计属于运行期数据，不搬
            if (name.Equals("logs", StringComparison.OrdinalIgnoreCase)
                || name.Equals("runtime_stats", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var child = Path.Combine(targetDir, name);
            Directory.CreateDirectory(child);
            CopyDirectory(directory, child);
        }
    }

    /// <summary>
    /// 把 Agent 注册成**登录自启任务计划**（ONLOGON + HIGHEST），目标账户登录后自动拉起Yomori代理。
    ///
    /// ❗❗ 为什么不能再用"公共启动目录快捷方式"——这是 app.manifest 改成
    ///     requireAdministrator 的直接后果，不是顺手重构：
    ///
    ///   requestedExecutionLevel 是**编译期静态**的，没法按参数豁免。而代理副本
    ///   （<see cref="EnsureAgentPayload"/>）是把主控端**整个目录原样复制**过去的同一个 exe，
    ///   manifest 一旦声明 requireAdministrator，代理也跟着被要求管理员权限。
    ///
    ///   快捷方式由 Explorer 在目标账户登录时拉起进程，此时**没有任何人能点 UAC** ——
    ///   代理就再也起不来。表现为"通道连上了但任务不执行"，而且极难定位：
    ///   部署环节一切正常、日志干净，只有任务就是不跑。
    ///
    ///   任务计划（/RL HIGHEST）正是微软给这种无人值守场景的正规做法：
    ///   它自己申请最高权限，**不触发 UAC 交互**，目标账户甚至不需要是管理员。
    /// </summary>
    /// <param name="message">结果说明。</param>
    /// <param name="bridgePath">桥目录；填了会写进任务参数，让 Agent 与主控端读写同一位置。</param>
    /// <param name="targetUser">目标账户，用于判断它的代理是不是正在执行任务。</param>
    public static bool DeployAgent(out string message, string? bridgePath = null, string? targetUser = null)
    {
        message = string.Empty;

        // 顺手收掉改名前留下的旧自启项。放在最前面是因为它和"这次要不要重写自启项"无关：
        // 「新 lnk 已存在 → 保留不动」正是最常见的升级路径，而旧 lnk 恰好永远不会被那条路径碰到。
        CleanupLegacyStartupShortcuts();

        // 代理载荷的来源有两种，优先用随包分发的精简版：
        //   ① <BaseDir>\agent\Yomori.Agent.exe —— 正式安装版 / CI 产物走这条。
        //      独立工程产出的纯后台程序，没有 WinUI / WindowsAppSDK / ONNX，
        //      约 73MB 而不是整套程序的 228MB（见 src/Yomori.Agent/Yomori.Agent.csproj）。
        //   ② 没有 agent\ 子目录时回退旧行为：复制主控端整个目录、按 --rdp-agent 拉起
        //      Yomori.exe。开发期直接 dotnet build 就是这个情形（没有发布步骤就没有 agent\）。
        //      这只是兜底 —— 它正是这次要摆脱的 228MB，别把它当常态。
        var shippedAgentExe = Path.Combine(ShippedAgentDir, AgentExeName);
        var usingShippedAgent = File.Exists(shippedAgentExe);

        var sourceExe = usingShippedAgent ? shippedAgentExe : Environment.ProcessPath;
        if (string.IsNullOrEmpty(sourceExe))
        {
            sourceExe = Path.Combine(AppPaths.BaseDir, LegacyAgentExeName);
        }

        if (!File.Exists(sourceExe))
        {
            message = $"找不到会话代理载荷：{sourceExe}\n"
                + $"安装目录下应有 agent\\{AgentExeName}；缺失说明安装包不完整，请重新安装。";
            return false;
        }

        // 关键一步：先把程序复制到所有用户都能读的公共目录。
        // 之前直接指向主控端 exe，目标账户没权限读那个目录，快捷方式静默失败，
        // 表现为"连上了但任务不执行"。
        if (!EnsureAgentPayload(sourceExe, targetUser, out var exe, out var copyError, out var payloadNote))
        {
            message = copyError;
            return false;
        }

        var arguments = RdpTargets.BuildAgentArguments(bridgePath);

        // ❗❗ 提权这件事按载荷分两种，别混：
        //    ① 精简代理（Yomori.Agent.exe）：它自己的 manifest 就是 asInvoker，这里会直接
        //       判成「本就不要求管理员」，mt.exe 一次都不跑 —— 独立工程顺带削掉了
        //       这段「改不成就中止部署」的脆弱逻辑。
        //    ② 回退成全量副本时：代理就是主控端那个 exe，而主控端 manifest 是
        //       requireAdministrator（编译期静态，没法按参数豁免）。它由公共启动目录快捷方式
        //       在目标账户登录时**无人值守**拉起，没人能点 UAC —— 不改写就永远起不来。
        //    两种情况都走同一个入口，让它自己判断现状，省得调用方维护分支。
        if (!TryDowngradeAgentManifest(exe, out var manifestNote))
        {
            message = $"{manifestNote}\n"
                + "代理副本的提权豁免没做成，为避免部署出一个起不来的代理，本次部署已中止。";
            return false;
        }

        if (!usingShippedAgent)
        {
            payloadNote = JoinNotes(new List<string>
            {
                payloadNote,
                "本次没有使用精简代理载荷（安装目录缺少 agent\\），已回退为复制整个程序目录"
            });
        }

        if (!CreateStartupShortcut(exe, arguments, out var shortcutError))
        {
            message = shortcutError;
            return false;
        }

        var notes = new StringBuilder();
        notes.Append($"已部署到所有用户共享启动目录：{AgentShortcutPath}（程序副本：{Path.GetDirectoryName(exe)}）");

        if (manifestNote.Length > 0)
        {
            notes.Append("；").Append(manifestNote);
        }

        if (payloadNote.Length > 0)
        {
            notes.Append("；").Append(payloadNote);
        }

        if (IsOtherInstanceRunning())
        {
            notes.Append("；检测到还有其他Yomori进程在跑（多半是目标账户里那个常驻代理），"
                + "要让它换成新程序，需注销目标账户后重新连接");
        }

        message = notes.ToString();
        return true;
    }

    /// <summary>
    /// 删掉改名前留下的旧自启项（见 <see cref="LegacyStartupShortcutNames"/>）。
    ///
    /// 单文件删除，全程吞异常：删不掉（被占用 / 权限不够）最多让登录时多空试一次，
    /// 不该让「部署代理」整个失败 —— 那两件事的后果完全不在一个量级。
    /// </summary>
    private static void CleanupLegacyStartupShortcuts()
    {
        var directory = Path.GetDirectoryName(AgentShortcutPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        foreach (var name in LegacyStartupShortcutNames)
        {
            try
            {
                var stale = Path.Combine(directory, name);
                if (File.Exists(stale))
                {
                    File.Delete(stale);
                }
            }
            catch
            {
                // 忽略：见方法注释
            }
        }
    }

    /// <summary>
    /// 在公共启动目录写下代理自启项（所有账户登录时由 Explorer 拉起它）。
    ///
    /// 抽成独立方法是因为现在有两个调用方：手动部署（<see cref="DeployAgent"/>）与
    /// 安装时自动部署（<see cref="AutoDeployForInstall"/>）。两处的差别只在"要不要重写已有项"，
    /// 写快捷方式本身的手法必须完全一致 —— 分开写两份迟早会漂移（比如只在一处补了参数）。
    /// </summary>
    /// <param name="exe">代理主程序（已在公共副本目录里）。</param>
    /// <param name="arguments">启动参数，通常是 <c>--rdp-agent [--bridge:...]</c>。</param>
    /// <param name="error">失败原因（成功时为空串）。</param>
    private static bool CreateStartupShortcut(string exe, string arguments, out string error)
    {
        error = string.Empty;

        var shortcut = AgentShortcutPath;
        var directory = Path.GetDirectoryName(shortcut);
        if (!string.IsNullOrEmpty(directory))
        {
            // 公共启动目录只有管理员能写。这里必须自己接住异常，
            // 否则会以未处理异常的形式冒到 UI 线程之外（以前就是这样，失败了还看不出原因）。
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                error = $"无法写入公共启动目录：{directory}\n{ex.Message}";
                return false;
            }
        }

        var workDir = Path.GetDirectoryName(exe);
        var script = new StringBuilder()
            .AppendLine("$ws = New-Object -ComObject WScript.Shell")
            .AppendLine($"$sc = $ws.CreateShortcut('{shortcut.Replace("'", "''")}')")
            .AppendLine($"$sc.TargetPath = '{exe.Replace("'", "''")}'")
            .AppendLine($"$sc.Arguments = '{arguments.Replace("'", "''")}'")
            .AppendLine($"$sc.WorkingDirectory = '{(workDir ?? string.Empty).Replace("'", "''")}'")
            .AppendLine("$sc.Description = 'Yomori RDP 会话执行代理'")
            // 图标：直接取代理 exe 自己那张（ApplicationIcon 编译期嵌进 exe）。
            // 不设 IconLocation 时 .lnk 不带图标，任务管理器/启动文件夹里是个白纸图标；
            // 这里逗号后的 0 = 用 exe 里的第一个图标组。
            .AppendLine($"$sc.IconLocation = '{(exe + ",0").Replace("'", "''")}'")
            .AppendLine("$sc.Save()")
            .ToString();

        if (!RunPowerShell(script, out var shellError))
        {
            // 只有「写不进公共启动目录」才和权限有关。以前这里不分青红皂白地
            // 回一句「请以管理员身份运行Yomori后重试」，把文件占用之类的失败
            // 全说成权限问题，排查方向直接跑偏。现在原样带出真实报错。
            var hint = IsElevated
                ? "当前进程已经是管理员，因此这不是权限问题，请对照上面的具体报错。"
                : "当前进程不是管理员：写公共启动目录需要管理员权限。";

            error = $"创建启动项失败：{(shellError.Length > 0 ? shellError : "PowerShell 未给出错误信息")}\n"
                + $"启动项路径：{shortcut}\n{hint}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 已部署的副本是不是就是随包分发的这一份（<see cref="ShippedAgentDir"/> 里的载荷）。
    ///
    /// 只用于"安装时自动部署"的幂等判断：内容一致就跳过整套搬运与快捷方式重建。
    /// 比对走 <see cref="AgentPayloadIdentity"/>，不看时间戳 —— 全量副本时代那个 exe
    /// 会被 mt.exe 就地改写，时间戳说明不了版本。
    /// </summary>
    public static bool IsAgentPayloadCurrent()
    {
        var shippedExe = Path.Combine(ShippedAgentDir, AgentExeName);
        if (!File.Exists(shippedExe))
        {
            return false;
        }

        foreach (var slot in PayloadSlotDirs())
        {
            if (AgentPayloadIdentity.Matches(shippedExe, Path.Combine(slot, AgentExeName)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 安装程序在文件铺完后调用（<c>Yomori.exe --deploy-agent</c>）：把随包的代理载荷
    /// 部署到公共目录，让目标账户登录后立刻就能跑代理，不必等用户进程序点一次「部署会话代理」。
    ///
    /// 与 <see cref="DeployAgent"/> 刻意的两点不同：
    ///
    /// <para>① <b>已有自启项一律不动。</b>自启项的参数里带着 <c>--bridge:</c>，那是用户在
    /// 「通道管理」里配的（远程主机共享目录）。安装器不知道通道配置，重写它等于把桥目录
    /// 改回默认值 —— 远程通道当场失效，而且现象是"装了个新版本就再也不执行任务了"。
    /// 只有压根没有自启项时（全新安装，或"卸载后重装"）才新建，参数从 config.json 里
    /// 能唯一确定的 bridge_path 取，取不到就用默认（与手动部署在干净配置下完全一致）。</para>
    ///
    /// <para>② <b>不杀正在执行任务的代理。</b>安装是无人值守的，用户可能只是在装新版本；
    /// 这时把别人跑了一半的活打断不可接受。是否安全由调用方（<c>AgentAutoDeploy</c>）
    /// 先用桥状态确认，这里只负责搬运（<see cref="EnsureAgentPayload"/> 对"文件被占用"
    /// 本来就有退到备用目录的降级，不会硬来）。</para>
    /// </summary>
    /// <param name="message">结果说明（成功也可能带过程说明）。</param>
    public static bool AutoDeployForInstall(out string message)
    {
        message = string.Empty;

        // 与手动部署同一个理由：旧自启项要在**任何分支之前**收掉，
        // 否则「已有自启项 → 原样保留」这个分支会连带把旧 lnk 也一起保留下来。
        CleanupLegacyStartupShortcuts();

        var shippedExe = Path.Combine(ShippedAgentDir, AgentExeName);
        if (!File.Exists(shippedExe))
        {
            message = $"安装目录下缺少代理载荷：{shippedExe}\n"
                + "安装包不完整（应当随包带上 agent\\ 子目录），请重新安装。";
            return false;
        }

        // targetUser 传 null：安装时不知道（也不该猜）目标账户。
        // 它的作用只是"部署前看看那一路桥上的代理是不是正忙着" —— 那件事由调用方
        // 扫全部桥统一做，比按一个猜出来的账户名查更准。
        if (!EnsureAgentPayload(shippedExe, null, out var exe, out var copyError, out var payloadNote))
        {
            message = copyError;
            return false;
        }

        // 部署完如果还有代理进程活着，说明旧程序仍在内存里跑（部署换了磁盘上的文件，
        // 但进程不会自己换成新版）。这句话在安装日志里很关键：用户升级完发现"版本号变了、
        // 代理行为没变"时，能从这里知道要先注销目标账户。
        var runningNote = IsOtherInstanceRunning()
            ? "；检测到还有Yomori进程在跑（多半是目标账户里那个常驻代理），"
              + "要让它换成新程序，需注销目标账户后重新连接"
            : string.Empty;

        if (File.Exists(AgentShortcutPath))
        {
            message = payloadNote.Length > 0
                ? $"代理程序副本已更新（{payloadNote}）；自启项已存在，保留原有参数未改动{runningNote}"
                : $"代理程序副本已是最新；自启项已存在，保留原有参数未改动{runningNote}";
            return true;
        }

        var bridgePath = ResolveConfiguredBridgePath();
        if (!CreateStartupShortcut(exe, RdpTargets.BuildAgentArguments(bridgePath), out var createError))
        {
            message = createError;
            return false;
        }

        var note = bridgePath is null
            ? "已写入登录自启项（目标账户登录后自动启动代理）。"
            : $"已写入登录自启项，桥目录取自已保存的通道配置：{bridgePath}";

        message = payloadNote.Length > 0
            ? $"{note}（{payloadNote}）{runningNote}"
            : $"{note}{runningNote}";
        return true;
    }

    /// <summary>
    /// 从已保存的 config.json 里找出"能唯一确定"的指令桥目录，供安装时重建自启项用。
    ///
    /// 为什么要读配置：自启项的参数带着 <c>--bridge:</c>，而安装器重装/升级时会把旧的
    /// 自启项清掉（卸载器只认安装时登记过的文件，自启项不在其中，但本产品自己的
    /// 卸载清理会删）。这时若一律按"无 --bridge"重建，配了共享目录的通道就静默失效。
    ///
    /// 规则保守到只做"不会猜错"的事：把所有通道的 bridge_path 收集起来，
    ///   · 一个非空都没有 → 返回 null（本机环回场景，默认就够）；
    ///   · 只有一个不同取值 → 用它；
    ///   · 有多个不同取值 → 返回 null 并在日志里说明（公共自启项本来就只有一个快捷方式，
    ///     多通道不同桥这种配置，任何自动选择都是在替用户瞎决定）。
    /// 任何异常（文件损坏、字段缺失）都退回 null —— 安装过程绝不能因读配置而中断。
    /// </summary>
    /// <returns>bridge_path；无法唯一确定时为 null。</returns>
    private static string? ResolveConfiguredBridgePath()
    {
        try
        {
            var path = AppPaths.ConfigPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
                json,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            if (config?.Rdp is null)
            {
                return null;
            }

            var candidates = new List<string>();
            void Consider(string? value)
            {
                var text = (value ?? string.Empty).Trim();
                if (text.Length > 0
                    && !candidates.Exists(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(text);
                }
            }

            Consider(config.Rdp.BridgePath);
            foreach (var channel in config.Rdp.Channels)
            {
                Consider(channel.BridgePath);
            }

            return candidates.Count == 1 ? candidates[0] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把代理副本那份 exe 的内嵌 manifest 从 requireAdministrator 改回 asInvoker。
    ///
    /// 为什么非做不可：主控端要管理员（双击即弹 UAC，是用户要的行为），
    /// 代理**绝对不能**要 —— 它无人值守自启，弹 UAC 就等于永远起不来。
    /// 同一个 exe 同一份 manifest 改不了，所以只能在铺完副本后**就地改写**它的资源。
    ///
    /// 做法：mt.exe -manifest &lt;asInvoker 版&gt; -outputresource:&lt;exe&gt;#1。
    /// mt.exe 随 Microsoft.Windows.SDK.BuildTools 一起装，构建机上必有；
    /// 本机则在 Windows SDK 常见位置找。两条路都找不到 → 返回失败并中止部署，
    /// 绝不"先部署上去再说" —— 那会留下一个连上了却从不执行任务的代理，比部署失败更难查。
    ///
    /// ✅ 已实测（2026-10-06，440KB 的 Release 产物）：
    ///   · exit code 0，manifest 确实从 requireAdministrator 变成 asInvoker
    ///     （反向 -inputresource 抽出来核对过）
    ///   · 资源类型表前后一致 [RT_ICON(3), RT_GROUP_ICON(14), 16, 24]，
    ///     图标与组图标都还在 —— **不会丢图标**
    ///   · 文件从 440KB 变 312KB：这是 mt.exe 按默认对齐重写了整个 .rsrc 节，
    ///     **不是资源丢失**。别看到体积缩水就以为改坏了。
    /// </summary>
    /// <param name="note">实际做了什么（成功时也会说明"本就已是不提权"），失败时是原因。</param>
    /// <returns>false 表示没做成，调用方必须中止部署。</returns>
    private static bool TryDowngradeAgentManifest(string exe, out string note)
    {
        note = string.Empty;

        try
        {
            // 先看现状：已经是 asInvoker 就不用动（部署第二次、或旧副本已改过）。
            var bytes = File.ReadAllBytes(exe);
            var asIsInvoker = System.Text.Encoding.UTF8.GetString(bytes).Contains("asInvoker", StringComparison.Ordinal);
            var wantsAdmin = System.Text.Encoding.UTF8.GetString(bytes).Contains("requireAdministrator", StringComparison.Ordinal);

            if (asIsInvoker && !wantsAdmin)
            {
                note = "代理副本本就不要求管理员";
                return true;
            }

            var mt = FindMtExe();
            if (string.IsNullOrEmpty(mt))
            {
                note = "找不到 mt.exe（Windows SDK 资源工具），无法改写代理副本的 manifest。"
                    + "请安装 Microsoft.Windows.SDK.BuildTools 后重试。";
                return false;
            }

            var manifestPath = WriteAgentManifestFile();
            if (manifestPath.Length == 0)
            {
                note = "无法写出代理专用的 asInvoker manifest 临时文件。";
                return false;
            }

            try
            {
                // -outputresource:<exe>;#1 —— 覆盖资源 ID 1（应用清单固定就是 1）。
                // ⚠️ 必须用 ArgumentList：手工拼字符串会被 .NET 再转义一次，
                //    路径里的反斜杠会把参数拆碎（当年 schtasks 的 /TR 就这么丢过参数，
                //    见 memory/2026-09-25.md 阶段十五）。
                //    但注意 ArgumentList **只保证"一个元素 = 一个 argv"**，
                //    拼错参数形状它救不了 —— 见下面关于 `#1` 的说明。
                var psi = new ProcessStartInfo(mt)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                psi.ArgumentList.Add("-manifest");
                psi.ArgumentList.Add(manifestPath);
                // 🔑🔑 `-outputresource:<exe>;#1` 必须作为**一个**参数传给 mt.exe。
                //
                // 错在哪：早先写成两个参数
                //     ArgumentList.Add($"-outputresource:{exe}");
                //     ArgumentList.Add("#1");            // <-- 独立参数
                // mt.exe 于是把 `#1` 当成未知选项：
                //     mt.exe : command line error c1010007: Unexpected/Unknown option "#1".
                // 退出码 31，整个代理部署被中止（"为避免部署出起不来的代理，本次部署已中止"）。
                //
                // ⚠️ `ArgumentList` 只保证"每个元素作为一个参数传给子进程"，
                //   **不保证 mt.exe 自己会把它当一个选项值** —— 拼错的是参数本身的形状，
                //   不是转义问题。所以"用 ArgumentList 就不会再拆参数"这个说法只对了一半：
                //   它解决的是 .NET 二次转义（路径里的反斜杠/分号），解决不了语义拼写。
                //
                // ✅ 实测（2026-10-07，440320 字节的 Release 产物，本机 mt.exe 10.0.26100.0）：
                //     写法 A（分开）：exit=31，c1010007，manifest 未改写
                //     写法 B（合并）：exit=0，回读 -inputresource 得 level="asInvoker"
                //   B 的体积 440320 -> 312320，这是 mt.exe 按默认对齐重写整个 .rsrc 节，
                //   **不是资源丢失**（见方法头注释）。
                psi.ArgumentList.Add($"-outputresource:{exe};#1");

                using var process = Process.Start(psi);
                if (process is null)
                {
                    note = "无法启动 mt.exe。";
                    return false;
                }

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    note = $"mt.exe 改写代理 manifest 失败（exit {process.ExitCode}）："
                        + (stderr.Length > 0 ? stderr.Trim() : stdout.Trim());
                    return false;
                }
            }
            finally
            {
                // 临时 manifest 用完即删，别留在公共目录里让人摸不着头脑。
                try
                {
                    if (manifestPath.Length > 0 && File.Exists(manifestPath))
                    {
                        File.Delete(manifestPath);
                    }
                }
                catch
                {
                    // 删不掉不算失败，文件在副本目录里，下次部署会覆盖
                }
            }

            note = "已把代理副本的 manifest 改回 asInvoker（主控端保持 requireAdministrator 不变）";
            return true;
        }
        catch (Exception ex)
        {
            note = $"改写代理 manifest 时出错：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 找 mt.exe（Windows SDK 的资源工具，用来改 exe 内嵌 manifest）。
    ///
    /// 优先用 NuGet 缓存里随 Microsoft.Windows.SDK.BuildTools 装的那份 ——
    /// 版本与构建时一致，最可靠；找不到再退回 Windows SDK 的默认安装位置。
    ///
    /// ⚠️ 必须**按当前进程架构筛掉其它架构的 mt.exe**：那个包里同时躺着
    ///    arm64 / x64 / x86 三个 mt.exe，而目录枚举不保证先给哪个。
    ///    命中 arm64 的那份就会以"BadImageFormatException"失败，
    ///    或者更糟：恰好在 ARM64 机器上部署时挑到 x86 的，静默出错。
    ///    程序集本身是 x64（csproj 的 PlatformTarget），所以按 ProcessArchitecture 选最稳。
    /// </summary>
    private static string FindMtExe()
    {
        var wanted = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        // ① NuGet 包缓存：…\microsoft.windows.sdk.buildtools\<版本>\bin\<版本>\<arch>\mt.exe
        var nugetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        if (Directory.Exists(nugetRoot))
        {
            var pkgDir = Path.Combine(nugetRoot, "microsoft.windows.sdk.buildtools");
            if (Directory.Exists(pkgDir))
            {
                var candidate = Directory.GetDirectories(pkgDir)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(d => SafeEnumerateMtExe(d))
                    .FirstOrDefault(p => PathMatchesArchitecture(p, wanted));

                if (!string.IsNullOrEmpty(candidate))
                {
                    return candidate;
                }
            }
        }

        // ② Windows SDK 默认位置：C:\Program Files (x86)\Windows Kits\10\bin\<ver>\<arch>\mt.exe
        var kits = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");

        if (Directory.Exists(kits))
        {
            var candidate = Directory.GetDirectories(kits)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .SelectMany(d => SafeEnumerateMtExe(d))
                .FirstOrDefault(p => PathMatchesArchitecture(p, wanted));

            if (!string.IsNullOrEmpty(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// mt.exe 的路径里，架构那一层目录名要跟当前进程架构对得上。
    ///
    /// 认三种写法：目录名本身就是 arch（…\x64\mt.exe）、含 arch 的 bin 子目录，
    /// 以及万一路径里压根没有 arch 段（保守放行，让 mt.exe 自己去报错）。
    /// </summary>
    private static bool PathMatchesArchitecture(string mtPath, string wanted)
    {
        var dir = Path.GetDirectoryName(mtPath);
        if (string.IsNullOrEmpty(dir))
        {
            return false;
        }

        var leaf = Path.GetFileName(dir).ToLowerInvariant();
        if (leaf.Length > 0 && !char.IsDigit(leaf[0]) && !leaf.StartsWith("v", StringComparison.Ordinal))
        {
            // 末级目录名不是版本号也不是纯数字 → 它就是架构目录（x64 / x86 / arm64）
            return leaf == wanted;
        }

        var parent = Path.GetDirectoryName(dir);
        return !string.IsNullOrEmpty(parent)
            && string.Equals(Path.GetFileName(parent), wanted, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeEnumerateMtExe(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "mt.exe", SearchOption.AllDirectories);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 写一份 asInvoker 版的 manifest 到代理副本目录，返回路径（失败返回空串）。
    /// </summary>
    private static string WriteAgentManifestFile()
    {
        var target = Path.Combine(
            Path.GetDirectoryName(AgentProgramPath) ?? AgentPayloadRoot, AgentManifestFileName);

        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<assembly manifestVersion=\"1.0\" xmlns=\"urn:schemas-microsoft-com:asm.v1\">\n"
            + "  <assemblyIdentity version=\"1.0.0.0\" name=\"Yomori.app\" />\n"
            + "  <trustInfo xmlns=\"urn:schemas-microsoft-com:asm.v2\">\n"
            + "    <security>\n"
            + "      <requestedPrivileges xmlns=\"urn:schemas-microsoft-com:asm.v3\">\n"
            + "        <!-- 代理专用：不要求管理员。代理由公共启动目录快捷方式无人值守拉起，\n"
            + "             弹 UAC 就等于永远起不来。内容与主控端 app.manifest 的差别仅在此行。 -->\n"
            + "        <requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\" />\n"
            + "      </requestedPrivileges>\n"
            + "    </security>\n"
            + "  </trustInfo>\n"
            + "</assembly>\n";

        try
        {
            // ASCII 写法：manifest 解析器对编码敏感，且这里没有任何需要非 ASCII 的内容。
            File.WriteAllText(target, xml, new System.Text.UTF8Encoding(false));
            return target;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>当前进程是否以管理员身份运行（写公共启动目录、放行防火墙都需要）。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 除本进程外，是否还有别的Yomori进程在跑 —— 通常是目标账户里那个常驻的会话代理。
    /// 部署完新副本后，这个旧进程仍然是旧程序（文件已加载进内存），只有注销重登才会换新。
    /// </summary>
    public static bool IsOtherInstanceRunning()
    {
        try
        {
            foreach (var process in EnumerateAgentProcesses())
            {
                using (process)
                {
                    if (process.Id != Environment.ProcessId)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // 查不到就算了，这只是给用户的提示信息
        }

        return false;
    }

    public static bool RemoveAgent(out string message)
    {
        var problems = new List<string>();

        try
        {
            if (File.Exists(AgentShortcutPath))
            {
                File.Delete(AgentShortcutPath);
            }
        }
        catch (Exception ex)
        {
            problems.Add($"删除启动项快捷方式失败：{ex.Message}");
        }

        // 顺手清掉程序副本（可能有首选 + 备用两份），
        // 免得公共目录里留着 140MB 的旧文件
        foreach (var slot in PayloadSlotDirs())
        {
            if (!Directory.Exists(slot))
            {
                continue;
            }

            try
            {
                Directory.Delete(slot, recursive: true);
            }
            catch (Exception ex)
            {
                // 代理还常驻着就会这样：文件被占用，删不掉。
                // 这次先留着（注销目标账户后就能删），不算致命。
                problems.Add($"{slot} 正被运行的进程占用，未能删除：{ex.Message}");
            }
        }

        if (problems.Count == 0)
        {
            message = "已移除 RDP 会话代理启动项与程序副本。";
            return true;
        }

        message = "启动项已处理；" + string.Join("；", problems)
            + "。注销目标账户后重试一次即可清干净。";
        return false;
    }

    // ---------------- 预检汇总 ----------------

    public static RdpReadiness CheckReadiness(
        string? targetHost, string? targetUser, bool credentialSaved, string? bridgePath = null)
    {
        var host = RdpTargets.Normalize(targetHost);
        var isLocal = RdpTargets.IsLocal(host);

        var readiness = new RdpReadiness { IsLocal = isLocal };

        // 这座桥对应「目标主机 + 目标账户」这一路会话：目录按账户名派生，
        // 与目标会话里代理自己算出来的目录一致（见 RdpChannelPaths）
        var bridge = RdpBridge.For(RdpChannelPaths.Resolve(bridgePath, targetUser));

        // 本机只是客户端时才受"家庭版不能当服务端"限制；连别人不受影响
        if (isLocal)
        {
            DescribeEdition(readiness);
            readiness.UserExists = UserExists(targetUser);
            readiness.RemoteDesktopEnabled = IsRemoteDesktopEnabled();
            readiness.AgentDeployed = IsAgentDeployed();

            // "已部署"（快捷方式在不在）与"在线"（代理此刻在不在跑）是两回事，分别给出：
            // 前者决定要不要去部署，后者决定要不要注销目标账户后重新连接。
            readiness.AgentOnline = IsAgentOnline(bridge.TryReadStatus(), targetUser, DateTimeOffset.Now);
        }
        else
        {
            // 远程目标：账户是否存在、对方有没有开远程桌面、有没有部署 Agent，本机都查不到
            readiness.UserExists = true;
            readiness.RemoteDesktopEnabled = true;
            readiness.AgentDeployed = true;
            readiness.AgentOnline = false;
            readiness.Notes.Add($"目标 {host} 是远程主机：账户、远程桌面开关、会话代理都要在那台机器上确认好。");
            readiness.Notes.Add("对方机器也要装Yomori并部署会话代理，任务才能在那边自动跑起来。");

            // 远程主机的 Agent 读不到本机 ProgramData，必须有个双方共享的位置
            if ((bridgePath ?? string.Empty).Trim().Length == 0)
            {
                readiness.Problems.Add("远程目标需要配置「指令桥目录」：填一个两台机器都能读写的共享位置");
            }
        }

        readiness.CredentialSaved = credentialSaved && HasCredential(host, targetUser);

        if (string.IsNullOrWhiteSpace(targetUser))
        {
            readiness.Problems.Add("还没有填写目标账户");
        }
        else if (isLocal && !readiness.UserExists)
        {
            readiness.Problems.Add($"本机不存在账户「{targetUser}」");
        }

        if (isLocal && !readiness.RemoteDesktopEnabled)
        {
            readiness.Problems.Add("系统尚未开启远程桌面");
        }

        if (!readiness.CredentialSaved)
        {
            readiness.Problems.Add($"还没有保存目标账户凭据（目标：{host}）");
        }

        if (isLocal && !readiness.AgentDeployed)
        {
            readiness.Problems.Add("尚未部署会话代理（目标账户登录后无法自动接管任务）");
        }
        else if (isLocal && !readiness.AgentOnline)
        {
            // 已部署但当前不在线：不是错误（目标账户可能还没登录），但要如实告知，
            // 与「还没部署」区分开 —— 两者的处理方式完全不同。
            readiness.Notes.Add("会话代理已部署，但当前不在线（目标账户未登录，或代理没有随会话启动）。");
        }

        return readiness;
    }

    // ---------------- 代理在线判定 ----------------

    /// <summary>
    /// 判断桥里的状态是不是"目标会话的代理正在线"。
    ///
    /// 与 <see cref="IsAgentDeployed"/>（只查快捷方式文件在不在）不同，这里看的是运行期实况：
    ///   AgentUser 与目标账户一致 + Phase 属活跃态 + UpdatedAt 新鲜。
    /// 时间新鲜度复用 <see cref="RdpHeartbeat.Evaluate"/>（归属参数留空 = 只判活、不绑定某条指令）。
    /// </summary>
    /// <param name="status">桥里的状态快照（通常来自 RdpBridge.TryReadStatus）。</param>
    /// <param name="targetUser">目标账户名，支持 "Player2" / "Machine\Player2"。</param>
    /// <param name="now">当前时刻，便于测试注入。</param>
    public static bool IsAgentOnline(RdpStatus? status, string? targetUser, DateTimeOffset now)
    {
        if (status is null)
        {
            return false;
        }

        if (!string.Equals(BareName(status.AgentUser), BareName(targetUser), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 只有"运行中 / 空闲"算在线；done / stopped / error 是终态，不算。
        if (status.Phase is not ("running" or "idle"))
        {
            return false;
        }

        // 只判活、不绑定某条指令 → 归属参数留空；宽限期对"在线判定"没有意义，也不传。
        var heartbeat = RdpHeartbeat.Evaluate(status.UpdatedAt, status.Phase, now);
        return heartbeat.Parsed && !heartbeat.IsStale;
    }

    /// <summary>
    /// 轮询桥目录，等目标会话里的代理上线（<see cref="IsAgentOnline"/> 为真）。
    /// 超时用配置的连接超时；返回 false 表示超时前代理始终没上线。
    ///
    /// 为什么是"等它上线"而不是"主动拉起"：
    ///   代理现在随目标账户登录自启并常驻等指令，主控端只需确认它在不在。
    ///   若目标账户本来就登录着（登录启动项不会重跑），代理自然不在线 ——
    ///   这时只能引导用户注销后重新连接，让登录动作把代理带起来。
    /// </summary>
    public static bool WaitForAgentOnline(
        string? targetUser, string? bridgePath, int timeoutSeconds, CancellationToken token)
    {
        var bridge = RdpBridge.For(RdpChannelPaths.Resolve(bridgePath, targetUser));
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));

        while (DateTime.UtcNow < deadline)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            if (IsAgentOnline(bridge.TryReadStatus(), targetUser, DateTimeOffset.Now))
            {
                return true;
            }

            Thread.Sleep(1000);
        }

        return IsAgentOnline(bridge.TryReadStatus(), targetUser, DateTimeOffset.Now);
    }

    /// <summary>
    /// 保证 CodePagesEncodingProvider 只注册一次（幂等 + 异常安全）。
    ///
    /// .NET 8 默认【不含】GBK / CP936 这类代码页编码 —— 不注册提供程序时
    /// <c>Encoding.GetEncoding(936)</c> 会抛 NotSupportedException，最终只能退到 UTF8，
    /// 而 UTF8 恰好就是改前 <see cref="Encoding.Default"/> 的行为，等于没修。
    /// 用 <see cref="Lazy{T}"/> 自注册：主工程（自定义 Main）与 _smoke 两个入口谁先用编码谁触发，
    /// 不必各自记得手动注册；注册本身幂等，重复调用无副作用。
    /// </summary>
    private static readonly Lazy<bool> CodePagesProviderRegistered = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return true;
        }
        catch
        {
            // 注册失败（极罕见）时不抛异常，交给下面的降级链兜底
            return false;
        }
    });

    /// <summary>
    /// 子进程输出用的系统 ANSI 编码（中文 Windows 上是 GBK / 代码页 936）。
    ///
    /// 注意：.NET Core / .NET 8 下 <see cref="Encoding.Default"/> 是 UTF-8，
    /// 【不是】系统 ANSI 代码页 —— 用它去读 netsh / cmdkey / net 这类原生工具的 GBK 输出，
    /// 结果全是乱码，错误信息就没法看了。
    /// 所以这里先确保代码页提供程序已注册，再显式按当前区域设置取 ANSI 代码页，并逐级降级。
    /// </summary>
    private static Encoding ConsoleOutputEncoding()
    {
        // 关键：必须先注册提供程序，否则下面第一级 GetEncoding(936) 直接抛，掉到 UTF8（= 没修）
        _ = CodePagesProviderRegistered.Value;

        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch
        {
            try
            {
                // 取不到 ANSI 代码页（罕见的区域设置 / 代码页未安装）时退回系统 OEM 代码页
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                // 最后兜底：至少不抛异常，让命令本身的结果仍可用
                return Encoding.UTF8;
            }
        }
    }

    private static bool RunHidden(string fileName, string arguments)
    {
        return RunHidden(fileName, arguments, out _, out _);
    }

    /// <summary>
    /// 静默执行外部命令并带回输出。
    /// 用系统 ANSI 代码页读输出 —— netsh / cmdkey 这类原生工具在中文 Windows 上输出 GBK，
    /// 用 UTF-8 读会变成乱码，错误信息就没法看了（见 <see cref="ConsoleOutputEncoding"/>）。
    /// </summary>
    private static bool RunHidden(string fileName, string arguments, out string stdout, out string stderr)
    {
        stdout = string.Empty;
        stderr = string.Empty;

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
            startInfo.StandardOutputEncoding = ConsoleOutputEncoding();
            startInfo.StandardErrorEncoding = ConsoleOutputEncoding();

            return RunCore(startInfo, out stdout, out stderr);
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            return false;
        }
    }

    private static bool RunCore(ProcessStartInfo startInfo, out string stdout, out string stderr)
    {
        stdout = string.Empty;
        stderr = string.Empty;

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            stderr = "进程启动返回空句柄。";
            return false;
        }

        // 先异步收完输出再等退出，避免管道写满造成死锁
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(15000))
        {
            try { process.Kill(true); } catch { }
            stderr = "命令执行超时。";
            return false;
        }

        stdout = outTask.GetAwaiter().GetResult();
        stderr = errTask.GetAwaiter().GetResult();
        return process.ExitCode == 0;
    }

    /// <summary>
    /// 跑一段 PowerShell 脚本（目前只用于生成启动项快捷方式）。
    /// 失败时把 stderr / 退出码原样带出来 —— 以前这里无声地返回 false，
    /// 上层只好编一句「请以管理员身份运行」，真实原因全丢，排查直接跑偏。
    /// </summary>
    private static bool RunPowerShell(string script, out string detail)
    {
        detail = string.Empty;

        try
        {
            var startInfo = new ProcessStartInfo("powershell")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);
            startInfo.StandardOutputEncoding = ConsoleOutputEncoding();
            startInfo.StandardErrorEncoding = ConsoleOutputEncoding();

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                detail = "无法启动 PowerShell 进程。";
                return false;
            }

            // 先异步收完输出再等退出，避免管道写满造成死锁
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(20000))
            {
                try { process.Kill(true); } catch { }
                detail = "PowerShell 执行超时（20 秒）。";
                return false;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode == 0)
            {
                return true;
            }

            var picked = FirstLines(stderr, 4);
            if (picked.Length == 0)
            {
                picked = FirstLines(stdout, 4);
            }

            detail = $"PowerShell 退出码 {process.ExitCode}"
                + (picked.Length > 0 ? $"：{picked}" : "（无输出）");
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>取前几行拼成一行，避免把一整段堆进对话框。</summary>
    private static string FirstLines(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var picked = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            if (picked.Count >= count)
            {
                break;
            }

            var line = raw.Trim();
            if (line.Length > 0)
            {
                picked.Add(line);
            }
        }

        return string.Join(" / ", picked);
    }
}
