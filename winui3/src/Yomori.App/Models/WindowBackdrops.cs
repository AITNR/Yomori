// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace Yomori.Models;

/// <summary>
/// 窗口背景（云母 / 纯色）的取值与"背景不透明度"的换算。<b>纯逻辑</b>，不碰任何 WinUI 类型。
/// </summary>
/// <remarks>
/// <para>三层结构，别混：</para>
/// <list type="number">
/// <item>窗口的 <c>SystemBackdrop</c>（Mica）—— 由系统画的，铺满整窗；</item>
/// <item>本程序自己的<b>着色层</b>（半透明纯色，盖在 Mica 之上、内容之下）——
///   <see cref="DefaultOpacity"/> / <see cref="ToAlphaByte"/> 管的就是它；</item>
/// <item>页面内容（卡片、列表）—— 各自的画刷，不受这里影响。</item>
/// </list>
///
/// <para>"背景不透明度 100%" = 着色层完全不透明 = 云母被完全盖住（观感等同纯色）；
/// <c>0%</c> = 完全透出云母。所以它既是"云母的强度旋钮"，也是不必切换模式就能回到旧观感的办法。</para>
///
/// <para>明暗不在这里单独设：Mica 的明暗跟着窗口的 <c>RequestedTheme</c> 走，
/// 再给一个独立的明暗开关只会造出"深色云母 + 浅色界面"这种明显不对的组合。</para>
/// </remarks>
public static class WindowBackdrops
{
    /// <summary>纯色：不启用任何系统背景，直接画主题底色。</summary>
    public const string Solid = "solid";

    /// <summary>
    /// 亚克力（DesktopAcrylicBackdrop）：**真把窗口后面的桌面模糊掉**，效果最明显。
    /// </summary>
    public const string Acrylic = "acrylic";

    /// <summary>
    /// 云母（MicaKind.Base）。注意 Mica 只取壁纸的<b>色调</b>、不显示壁纸本身，
    /// 深色主题下会被系统压到接近 <c>#202020</c> —— 在深色桌面上几乎看不出变化，这是设计使然。
    /// </summary>
    public const string Mica = "mica";

    /// <summary>云母 Alt（MicaKind.BaseAlt）：底纹比 Base 更明显一点。</summary>
    public const string MicaAlt = "mica_alt";

    public const int DefaultOpacity = 70;
    public const int MinOpacity = 0;
    public const int MaxOpacity = 100;

    /// <summary>设置页下拉的取值表。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Items { get; } =
        new List<KeyValuePair<string, string>>
        {
            new(Acrylic, "亚克力（Acrylic，模糊桌面，效果最明显）"),
            new(Mica, "云母（Mica，只取壁纸色调）"),
            new(MicaAlt, "云母（Mica Alt，底纹更明显）"),
            new(Solid, "纯色"),
        };

    /// <summary>归一化：认不出的值一律回落到纯色（老配置里没有这个键时也是纯色）。</summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            Acrylic or "blur" or "acrylic_blur" => Acrylic,
            Mica => Mica,
            MicaAlt or "mica-alt" or "micaalt" => MicaAlt,
            _ => Solid,
        };
    }

    /// <summary>这个取值要不要启用系统背景（云母或亚克力）。为 false 时走纯色。</summary>
    public static bool UsesBackdrop(string? value) => Normalize(value) != Solid;

    /// <summary>是不是亚克力那一档（真模糊，效果最明显）。</summary>
    public static bool IsAcrylic(string? value) => Normalize(value) == Acrylic;

    /// <summary>是不是云母（含 Alt）。</summary>
    public static bool UsesMica(string? value)
    {
        var kind = Normalize(value);
        return kind is Mica or MicaAlt;
    }

    /// <summary>是不是 Alt 那一档（只对 Mica 有意义）。</summary>
    public static bool IsAlt(string? value) => Normalize(value) == MicaAlt;

    public static int NormalizeOpacity(int value) => Math.Clamp(value, MinOpacity, MaxOpacity);

    /// <summary>不透明度百分比 → 着色层颜色的 alpha 字节。四舍五入，不截断。</summary>
    public static byte ToAlphaByte(int percent) =>
        (byte)Math.Round(NormalizeOpacity(percent) * 255.0 / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 着色层的基色：用 WinUI 的 <c>SolidBackgroundFillColorBase</c> 那两个值，
    /// 这样"不透明度 100%"和系统自带的不透明底色对得上，不会出现色差。
    /// </summary>
    public static (byte R, byte G, byte B) TintColor(bool dark) =>
        dark ? ((byte)0x20, (byte)0x20, (byte)0x20) : ((byte)0xF3, (byte)0xF3, (byte)0xF3);

    /// <summary>一行说明，用于设置页提示与运行日志。</summary>
    public static string Describe(string? value, int opacity) => Normalize(value) switch
    {
        Acrylic => $"亚克力 Acrylic，背景不透明度 {NormalizeOpacity(opacity)}%",
        Mica => $"云母 Mica，背景不透明度 {NormalizeOpacity(opacity)}%",
        MicaAlt => $"云母 Mica Alt，背景不透明度 {NormalizeOpacity(opacity)}%",
        _ => "纯色背景",
    };
}
