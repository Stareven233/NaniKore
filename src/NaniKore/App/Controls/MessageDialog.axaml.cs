using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace NaniKore.App.Controls;

/// <summary>
/// 主题化的消息与确认对话框，替代系统 MessageBox。
/// 具备深浅色主题自适应能力，保证在置顶卡片与设置窗口之上有序交互。
/// </summary>
internal sealed partial class MessageDialog : Window
{
    private bool _result;

    public MessageDialog()
    {
        InitializeComponent();
        Icon = ApplicationIcon.Load();
    }

    /// <summary>提示型对话框：只有一个确认按钮。</summary>
    public static void Show(Window? owner, string headline, string? body, InfoSeverity severity)
    {
        var dialog = Create(owner, headline, body, severity, "确定");
        dialog.CancelButton.IsVisible = false;
        if (owner is not null && owner.IsVisible)
        {
            dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    /// <summary>确认型对话框；返回 true 表示用户点击了确认按钮。</summary>
    public static async Task<bool> ConfirmAsync(Window? owner, string headline, string? body, string confirmText, InfoSeverity severity)
    {
        var dialog = Create(owner, headline, body, severity, confirmText);
        if (owner is not null && owner.IsVisible)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var tcs = new TaskCompletionSource<bool>();
            dialog.Closed += (_, _) => tcs.TrySetResult(dialog._result);
            dialog.Show();
            await tcs.Task;
        }
        return dialog._result;
    }

    private static MessageDialog Create(Window? owner, string headline, string? body, InfoSeverity severity, string confirmText)
    {
        var dialog = new MessageDialog
        {
            Owner = owner,
            Topmost = owner?.Topmost ?? true,
        };
        dialog.HeadlineText.Text = headline;
        dialog.BodyText.Text = body ?? string.Empty;
        dialog.BodyText.IsVisible = !string.IsNullOrWhiteSpace(body);
        dialog.ConfirmButton.Content = confirmText;

        var (glyph, brushKey) = severity switch
        {
            InfoSeverity.Success => ("\uE73E", "Brush.Success"),
            InfoSeverity.Warning => ("\uE7BA", "Brush.Warning"),
            InfoSeverity.Error => ("\uEA39", "Brush.Danger"),
            _ => ("\uE946", "Brush.Accent"),
        };
        dialog.GlyphText.Text = glyph;
        if (dialog.TryFindResource(brushKey, out var brush) && brush is IBrush foregroundBrush)
        {
            dialog.GlyphText.Foreground = foregroundBrush;
        }

        return dialog;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }
}
