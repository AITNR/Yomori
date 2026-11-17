// -*- coding: utf-8 -*-
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yomori.Services;

namespace Yomori.Views;

/// <summary>
/// 「新建用户」对话框：收用户名 / 密码 / 是否管理员，校验通过后把请求交给调用方执行。
/// </summary>
/// <remarks>
/// <para>本类<b>只做校验与取值，不创建账户</b> —— 创建要真改本机账户，放在页面里显式调用
/// <see cref="LocalUserAccountService.Create"/>，失败信息才好落到运行日志。</para>
/// <para>校验不通过时用 <c>args.Cancel = true</c> 把对话框留住：<c>ShowAsync</c> 一旦返回，
/// 输入框就没了，用户得从头再填一遍。校验逻辑全部来自 <see cref="LocalUserAccountService"/>，
/// 与冒烟跑的是同一份判据。</para>
/// </remarks>
public sealed partial class CreateLocalUserDialog : ContentDialog
{
    public CreateLocalUserDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    /// <summary>
    /// 校验通过后的取值；对话框被取消 / 校验未通过时保持 <c>null</c>。
    /// </summary>
    public (string Name, string Password, bool MakeAdmin)? Request { get; private set; }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Request = null;

        var name = LocalUserAccountService.ValidateName(NameInput.Text, Environment.MachineName);
        if (!name.Ok)
        {
            Reject(args, name.Reason);
            return;
        }

        var password = LocalUserAccountService.ValidatePassword(PasswordInput.Password, ConfirmInput.Password);
        if (!password.Ok)
        {
            Reject(args, password.Reason);
            return;
        }

        var bare = NameInput.Text.Trim();

        // 建之前先只读确认一次：查不清就不建，别让"可能已存在"变成"多了一个账户"。
        var query = LocalUserAccountService.QueryExists(bare);
        if (query.Found)
        {
            Reject(args, $"本机已有账户「{bare}」，换一个名字（或直接用现有账户建通道）。");
            return;
        }

        if (query.Code != LocalUserAccountService.NerrUserNotFound)
        {
            Reject(args, $"查不清本机是否已有账户「{bare}」（{query.Error}），未做任何修改。");
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        Request = (bare, PasswordInput.Password, AdminToggle.IsOn);
    }

    /// <summary>留住对话框并把原因写在输入框下面。</summary>
    private void Reject(ContentDialogButtonClickEventArgs args, string reason)
    {
        args.Cancel = true;
        ErrorText.Text = reason;
        ErrorText.Visibility = Visibility.Visible;
    }
}
