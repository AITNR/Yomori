// -*- coding: utf-8 -*-
using System;
using System.Text.Json.Serialization;
using Yomori.Helpers;

namespace Yomori.Models;

/// <summary>
/// 会话通道：一条「主控端 ↔ 目标会话」的完整通道 —— 目标主机 + 目标账户 + 指令桥目录 + 收尾策略。
///
/// 为什么要有它：原来全局只有一组 target_host / target_user，只能跑一路远程；
/// 拆成通道后，任务在「任务执行」页各选自己归哪个通道，开始执行时按通道分组、
/// 通道之间并行、通道内部仍按 order 串行。
///
/// 密码不落在这里（只记"是否已保存"），实际凭据在 Windows 凭据管理器里按 host|user 存。
/// 配置键名全 snake_case，与 config.json 其余部分一致。
/// </summary>
public sealed class RdpChannel : ObservableObject, ICloneable
{
    /// <summary>旧配置（只有一组 target_*）迁移出来的默认通道 id，固定值便于识别与排障。</summary>
    public const string LegacyDefaultId = "default";

    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _host = RdpTargets.DefaultHost;
    private string _user = string.Empty;
    private string _bridgePath = string.Empty;
    private bool _credentialSaved;
    private string _sessionFinish = SessionFinishModes.Keep;
    private int _desktopWidth = RdpResolutions.DefaultWidth;
    private int _desktopHeight = RdpResolutions.DefaultHeight;
    private bool _enabled = true;
    private bool _localMuted;
    private bool _inputLocked;

    /// <summary>生成一个通道 id（8 位十六进制，够用且不啰嗦）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, (value ?? string.Empty).Trim());
    }

    /// <summary>菜单项与下拉框里显示的名字。</summary>
    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, (value ?? string.Empty).Trim()))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>目标主机：IP / 主机名，可带端口（默认 127.0.0.1 本机环回）。</summary>
    [JsonPropertyName("host")]
    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, RdpTargets.Normalize(value)))
            {
                OnPropertyChanged(nameof(ListSubtitle));
            }
        }
    }

    /// <summary>目标 Windows 账户，例如 "GamePC\Player2" 或 "Player2"。</summary>
    [JsonPropertyName("user")]
    public string User
    {
        get => _user;
        set
        {
            if (SetProperty(ref _user, (value ?? string.Empty).Trim()))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(ListSubtitle));
                OnPropertyChanged(nameof(IsConfigured));
            }
        }
    }

    /// <summary>
    /// 指令桥目录。留空 = 按用户名自动派生（<c>&lt;ProgramData&gt;\Yomori\rdp\&lt;用户名&gt;</c>），
    /// 这样目标会话里的代理不需要额外配置就能找到自己的桥；远程主机才需要显式填共享目录。
    /// </summary>
    [JsonPropertyName("bridge_path")]
    public string BridgePath
    {
        get => _bridgePath;
        set => SetProperty(ref _bridgePath, (value ?? string.Empty).Trim());
    }

    /// <summary>该通道的凭据是否已存入 Windows 凭据管理器（按 host|user 一条）。</summary>
    [JsonPropertyName("credential_saved")]
    public bool CredentialSaved
    {
        get => _credentialSaved;
        set
        {
            if (SetProperty(ref _credentialSaved, value))
            {
                OnPropertyChanged(nameof(ListSubtitle));
            }
        }
    }

    /// <summary>该通道任务全部结束后的会话处理方式，见 <see cref="SessionFinishModes"/>。</summary>
    [JsonPropertyName("session_finish")]
    public string SessionFinish
    {
        get => _sessionFinish;
        set => SetProperty(ref _sessionFinish, SessionFinishModes.Normalize(value));
    }

    /// <summary>
    /// <b>仅用于导出</b>：从 Windows 凭据管理器取出来的明文密码。
    ///
    /// 正常配置里**永远不写**它 —— 密码只存在凭据管理器（DPAPI 保护），
    /// config.json 只留 <see cref="CredentialSaved"/> 标记。
    /// 只有「导出配置」勾选了「远程用户设置（含密码）」时，
    /// 才会临时把读到的密码填进来，让接收方导入即用。
    /// 所以 <see cref="Clone"/> / 导入路径都不该碰这个字段。
    /// </summary>
    [JsonPropertyName("password")]
    public string ExportPassword { get; set; } = string.Empty;

    /// <summary>远程桌面宽度，0 表示自适应。</summary>
    [JsonPropertyName("desktop_width")]
    public int DesktopWidth
    {
        get => _desktopWidth;
        set => SetProperty(ref _desktopWidth, RdpResolutions.Normalize(value, RdpResolutions.MinWidth, RdpResolutions.MaxWidth));
    }

    /// <summary>远程桌面高度，0 表示自适应。</summary>
    [JsonPropertyName("desktop_height")]
    public int DesktopHeight
    {
        get => _desktopHeight;
        set => SetProperty(ref _desktopHeight, RdpResolutions.Normalize(value, RdpResolutions.MinHeight, RdpResolutions.MaxHeight));
    }

    /// <summary>
    /// 上次在这条通道画面上选择的「本机静音」。
    /// 断开 / 重启后仍保留，下一次连接建立时自动恢复；只影响主控端本机播放，
    /// 不会关闭远端会话的音频输出。
    /// </summary>
    [JsonPropertyName("local_muted")]
    public bool LocalMuted
    {
        get => _localMuted;
        set => SetProperty(ref _localMuted, value);
    }

    /// <summary>
    /// 上次在这条通道画面上选择的「锁定输入」。
    /// 断开 / 重启后仍保留，下一次连接建立时自动恢复。
    /// </summary>
    [JsonPropertyName("input_locked")]
    public bool InputLocked
    {
        get => _inputLocked;
        set => SetProperty(ref _inputLocked, value);
    }

    /// <summary>停用后该通道的任务不会被下发（开始执行时归入"未启动"并给出原因）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(ListSubtitle));
            }
        }
    }

    /// <summary>
    /// 通道列表里名称之外的那行小字：目标 + 未配账户 / 已停用 / 凭据已存。
    /// 放在模型上而不是界面里，是为了让列表项模板只绑一个属性（三个来源都能通知到）。
    /// </summary>
    [JsonIgnore]
    public string ListSubtitle
    {
        get
        {
            var user = string.IsNullOrWhiteSpace(User) ? "未配账户" : User;
            var state = Enabled ? string.Empty : " · 已停用";
            var cred = CredentialSaved ? " · 凭据已存" : string.Empty;
            return $"{Host} / {user}{state}{cred}";
        }
    }

    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(User) ? User
        : "未命名通道";

    /// <summary>是否配好了目标账户（没配的通道不能执行，规划时进"未启动"）。</summary>
    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(User);

    public RdpChannel Clone()
    {
        var copy = (RdpChannel)MemberwiseClone();
        // MemberwiseClone 会把「仅导出用」的明文密码一起带走。克隆体是给运行期用的
        // （切页、重连、代理下发），不能让密码在内存里到处复制，更不能被 SaveConfig 写进
        // config.json —— 正常落盘只该有 credential_saved 标记。
        copy.ExportPassword = string.Empty;
        return copy;
    }

    object ICloneable.Clone() => Clone();

    /// <summary>规整为合法值（id 兜底、枚举兜底、分辨率钳位）。</summary>
    public void Sanitize(int fallbackIndex = 1)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = NewId();
        }

        Host = RdpTargets.Normalize(Host);
        User = (User ?? string.Empty).Trim();
        BridgePath = (BridgePath ?? string.Empty).Trim();
        SessionFinish = SessionFinishModes.Normalize(SessionFinish);
        DesktopWidth = RdpResolutions.Normalize(DesktopWidth, RdpResolutions.MinWidth, RdpResolutions.MaxWidth);
        DesktopHeight = RdpResolutions.Normalize(DesktopHeight, RdpResolutions.MinHeight, RdpResolutions.MaxHeight);

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = IsConfigured ? User : $"通道 {Math.Max(1, fallbackIndex)}";
        }
    }
}
