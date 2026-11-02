using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Color = Avalonia.Media.Color;

namespace NaniKore.App.Controls;

/// <summary>
/// 内联状态提示条：替代校验失败或状态反馈的弹窗，将提示留在页面内。
/// 支持四种状态语义配色，并在消息为空时自动折叠。
/// </summary>
internal sealed class InfoBar : Border
{
    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<InfoBar, string>(nameof(Message), string.Empty);

    public static readonly StyledProperty<InfoSeverity> SeverityProperty =
        AvaloniaProperty.Register<InfoBar, InfoSeverity>(nameof(Severity), InfoSeverity.Neutral);

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<InfoBar, bool>(nameof(IsOpen), false);

    private readonly TextBlock _iconBlock;
    private readonly TextBlock _messageBlock;

    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public InfoSeverity Severity
    {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public InfoBar()
    {
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 6);
        IsVisible = false;

        _iconBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
        };

        _messageBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _iconBlock, _messageBlock },
        };

        UpdateVisuals();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MessageProperty)
        {
            var msg = change.GetNewValue<string>();
            _messageBlock.Text = msg;
            IsOpen = !string.IsNullOrWhiteSpace(msg);
        }
        else if (change.Property == SeverityProperty)
        {
            UpdateVisuals();
        }
        else if (change.Property == IsOpenProperty)
        {
            IsVisible = change.GetNewValue<bool>();
        }
    }

    /// <summary>一次性设置文案与语义并展示。</summary>
    public void Show(string message, InfoSeverity severity)
    {
        Severity = severity;
        Message = message;
    }

    /// <summary>清空文案并收起提示条。</summary>
    public void Clear() => Message = string.Empty;

    private void UpdateVisuals()
    {
        switch (Severity)
        {
            case InfoSeverity.Success:
                _iconBlock.Text = "✔";
                _iconBlock.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x90));
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0x4E, 0xC9, 0x90));
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x4E, 0xC9, 0x90));
                break;

            case InfoSeverity.Warning:
                _iconBlock.Text = "⚠";
                _iconBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xFA, 0xAD, 0x14));
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFA, 0xAD, 0x14));
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFA, 0xAD, 0x14));
                break;

            case InfoSeverity.Error:
                _iconBlock.Text = "✖";
                _iconBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xEF, 0x44, 0x44));
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xEF, 0x44, 0x44));
                break;

            case InfoSeverity.Neutral:
            default:
                _iconBlock.Text = "ℹ";
                _iconBlock.Foreground = new SolidColorBrush(Color.FromRgb(0x95, 0xCA, 0x00));
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x95, 0xCA, 0x00));
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x95, 0xCA, 0x00));
                break;
        }
    }
}
