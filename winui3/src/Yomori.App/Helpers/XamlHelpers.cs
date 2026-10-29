// -*- coding: utf-8 -*-
using Microsoft.UI.Xaml;

namespace Yomori.Helpers;

/// <summary>
/// XAML 侧要用的静态小函数。
///
/// <para>
/// 为什么放在 <c>Helpers</c> 而不是模型里：<c>Models/*.cs</c> 会被冒烟工程
/// <c>_smoke</c> 以 <c>Compile Include</c> 直接链进纯 .NET 工程，那里没有
/// <c>Microsoft.UI.Xaml</c>，模型一旦暴露 <see cref="Visibility"/> 就编不过。
/// 界面侧要「按布尔收起一块界面」时，走这里的静态函数绑定：
/// <c>Visibility="{x:Bind helpers:XamlHelpers.ToVisibility(SomeBool), Mode=OneWay}"</c>。
/// </para>
///
/// <para>
/// ⚠️ 优先用它，而不是 <c>x:Load</c>。<c>x:Load</c> 生成的是
/// <c>XamlMarkupHelper.UnloadObject()</c> + <c>FindName("名字")</c> 这一对：
/// 值变 false 时把控件从树里摘掉、模板部件全释放，变回 true 时再靠字符串找回来。
/// 2026-10-29 主控端在 <c>Microsoft.UI.Xaml.dll+0x1af9d5</c> 空指针崩过一次
/// （Toggled 里改绑定属性，同时这个带 x:Load 的 ToggleSwitch 正在被卸载），
/// 那个 <c>x:Name</c> 就是给 FindName 用的，去掉后此机制彻底不参与。
/// </para>
/// </summary>
public static class XamlHelpers
{
    /// <summary>true → <see cref="Visibility.Visible"/>，false → <see cref="Visibility.Collapsed"/>。</summary>
    public static Visibility ToVisibility(bool visible)
        => visible ? Visibility.Visible : Visibility.Collapsed;
}
