// -*- coding: utf-8 -*-
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Yomori.Views;

/// <summary>
/// 两栏之间可拖动的分隔条（只为把指针改成左右箭头而存在的极简控件）。
///
/// <para>为什么要有这个类：WinUI 3 没有内置 GridSplitter（那是 UWP / Community Toolkit
/// 的东西，本项目不想为一条分隔条引一个包 —— 引了还得在
/// <c>third_party/components.json</c> 里登记许可证）。而
/// <c>UIElement.ProtectedCursor</c> 是 **protected**，只有子类能在自己的构造函数里设，
/// 页面里对着普通 Grid 调不了。</para>
///
/// <para>为什么不继承 Border：WinUI 的 <see cref="Border"/> 是 sealed。Grid 是 Panel、
/// 可继承且没有控件模板，正好当载体。</para>
///
/// <para>拖动本身不在这里做 —— 列宽要算页面的实际宽度、还要写配置，那些留在页面里。</para>
/// </summary>
public sealed class SplitterThumb : Grid
{
    public SplitterThumb()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
