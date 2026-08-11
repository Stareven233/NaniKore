using System.Windows;
using NaniKore.App.Theme;

namespace NaniKore.App.Controls;

/// <summary>
/// 主题化的消息与确认对话框，替代 <c>MessageBox</c>。
/// 系统 MessageBox 无法跟随应用配色，深色主题下会出现白底方角窗，破坏整体观感。
/// </summary>
internal sealed partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();
        Icon = ApplicationIcon.LoadWpf();
        ThemeManager.ApplyWindowChrome(this);
    }

    /// <summary>提示型对话框：只有一个确认按钮。</summary>
    public static void Show(Window? owner, string headline, string? body, InfoSeverity severity)
    {
        var dialog = Create(owner, headline, body, severity, "确定");
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.ConfirmButton.IsCancel = true;
        _ = dialog.ShowDialog();
    }

    /// <summary>确认型对话框；返回 true 表示用户点击了确认按钮。</summary>
    public static bool Confirm(Window? owner, string headline, string? body, string confirmText, InfoSeverity severity)
    {
        var dialog = Create(owner, headline, body, severity, confirmText);
        return dialog.ShowDialog() == true;
    }

    private static MessageDialog Create(Window? owner, string headline, string? body, InfoSeverity severity, string confirmText)
    {
        var dialog = new MessageDialog
        {
            // 结果卡与设置窗都是置顶窗口；对话框必须同层级，否则会被立即遮住而看似卡死。
            Owner = owner,
            Topmost = owner?.Topmost ?? true,
        };
        dialog.HeadlineText.Text = headline;
        dialog.BodyText.Text = body ?? string.Empty;
        dialog.BodyText.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
        dialog.ConfirmButton.Content = confirmText;
        (dialog.GlyphText.Text, var brushKey) = severity switch
        {
            InfoSeverity.Success => ("\uE73E", "Brush.Success"),
            InfoSeverity.Warning => ("\uE7BA", "Brush.Warning"),
            InfoSeverity.Error => ("\uEA39", "Brush.Danger"),
            _ => ("\uE946", "Brush.Accent"),
        };
        dialog.GlyphText.SetResourceReference(ForegroundProperty, brushKey);
        // 危险操作让确认按钮成为主按钮，但取消保持默认焦点，避免连按回车误删。
        dialog.ConfirmButton.Style = (Style?)dialog.TryFindResource("Button.Accent");
        dialog.CancelButton.IsDefault = true;
        return dialog;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
