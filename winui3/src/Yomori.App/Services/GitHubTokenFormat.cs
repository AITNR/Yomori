// -*- coding: utf-8 -*-
using System;
using System.Linq;

namespace Yomori.Services;

/// <summary>
/// GitHub 令牌的**纯文本处理**：清理输入、做本地格式检查、生成掩码。
/// </summary>
/// <remarks>
/// 单独拆出来是为了能被离线断言：这两个函数出错只会给错答案 ——
/// 把带换行的令牌判成「无效」，或者把整串令牌回显到界面上，都不会报错。
/// 这里不碰任何 Win32 / 网络，所以冒烟可以直接编进去跑。
/// </remarks>
internal static class GitHubTokenFormat
{
    /// <summary>
    /// 规范化并做**本地**格式检查（不联网）。
    /// </summary>
    /// <remarks>
    /// 从网页复制令牌经常带上换行或前后空格；粘错时最典型的表现是 401，
    /// 而 401 又会被说成「令牌无效」，让人以为令牌本身有问题。
    /// 所以先在这里把空白清掉、把明显不是令牌的输入挡住。
    /// </remarks>
    public static (string Token, string Error) Sanitize(string? raw)
    {
        var text = (raw ?? string.Empty)
            .Trim()
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace("\t", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);

        if (text.Length == 0)
        {
            return ("", "令牌是空的。");
        }

        if (text.Length < 20)
        {
            return ("", $"长度只有 {text.Length} 个字符，不像 GitHub 令牌（一般 40 个以上）。");
        }

        if (text.Length > 255)
        {
            return ("", "长度超过 255 个字符，多半是粘到了整段文字。");
        }

        if (!text.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return ("", "令牌里出现了字母数字下划线以外的字符，可能粘错了。");
        }

        // 只提示不强拦：GitHub 改过几次前缀（ghp_ / github_pat_ / gho_ 等），
        // 判错会把能用的令牌挡在门外，比放过一条坏令牌更糟。
        return (text, "");
    }

    /// <summary>用于界面显示的掩码，例如 <c>ghp_…9f2c</c>。绝不整串回显。</summary>
    public static string Mask(string? token)
    {
        var text = (token ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "";
        }

        // 太短的令牌不显示头尾，避免变相泄露
        if (text.Length < 12)
        {
            return new string('•', text.Length);
        }

        return text.Substring(0, 4) + "…" + text.Substring(text.Length - 4);
    }

}
