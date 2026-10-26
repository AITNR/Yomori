// -*- coding: utf-8 -*-
namespace Yomori.Services;

/// <summary>
/// 检查更新 / 下载安装包时用的代理认证密码。
/// </summary>
/// <remarks>
/// <para>与 GitHub 访问令牌、通道密码同一条规则：<b>密码只进 Windows 凭据管理器，不写 config.json</b>。
/// <c>update_proxy_url</c> 是落盘字段，而 config.json 会被导出、备份、贴给别人看 ——
/// 代理密码跟着它走等于随手送出去。</para>
/// <para>界面上那个输入框允许直接写 <c>http://用户:密码@主机:端口</c>（用户最自然的写法），
/// 落盘时由 <c>SettingsPage</c> 把密码摘出来交到这里，地址里只留用户名。</para>
/// </remarks>
internal static class UpdateProxySecretStore
{
    /// <summary>凭据管理器里的键。带前缀，避免和在存的 RDP 密码、GitHub 令牌混在一起。</summary>
    private const string Target = "Yomori/Update/ProxyPassword";

    /// <summary>没填用户名时凭据条目上记的账户名（仅用于显示，不参与鉴权）。</summary>
    private const string DefaultAccount = "update-proxy";

    /// <summary>读取已保存的代理密码；没配过返回空串。</summary>
    public static string Read() => (RdpCredentialStore.ReadSecret(Target) ?? string.Empty).Trim();

    /// <summary>保存密码。返回是否成功。</summary>
    public static bool Save(string user, string password) =>
        RdpCredentialStore.WriteSecret(
            Target,
            string.IsNullOrWhiteSpace(user) ? DefaultAccount : user.Trim(),
            password);

    /// <summary>清除（地址里没有认证时调用）。</summary>
    public static void Clear() => RdpCredentialStore.DeleteSecret(Target);
}
