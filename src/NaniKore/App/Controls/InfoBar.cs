using System.Windows;
using System.Windows.Controls;
using Control = System.Windows.Controls.Control;

namespace NaniKore.App.Controls;

/// <summary>状态提示语义；决定 <see cref="InfoBar"/> 的图标与配色。</summary>
internal enum InfoSeverity
{
    /// <summary>中性说明或进行中。</summary>
    Neutral,

    Success,

    Warning,

    Error,
}

/// <summary>
/// 内联提示条：替代校验失败时的模态 MessageBox，也承载「测试连接」的三态反馈。
/// 提示留在页面里而不是弹窗，用户不必先关弹窗才能改错。
/// </summary>
internal sealed class InfoBar : Control
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(InfoBar),
        new PropertyMetadata(string.Empty, OnMessageChanged));

    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity),
        typeof(InfoSeverity),
        typeof(InfoBar),
        new PropertyMetadata(InfoSeverity.Neutral));

    /// <summary>消息为空时自动折叠，避免留下一条空白色块。</summary>
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(InfoBar),
        new PropertyMetadata(false));

    static InfoBar() => DefaultStyleKeyProperty.OverrideMetadata(
        typeof(InfoBar),
        new FrameworkPropertyMetadata(typeof(InfoBar)));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public InfoSeverity Severity
    {
        get => (InfoSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        private set => SetValue(IsOpenProperty, value);
    }

    /// <summary>一次性设置文案与语义，调用方不必分别赋值两个属性。</summary>
    public void Show(string message, InfoSeverity severity)
    {
        Severity = severity;
        Message = message;
    }

    public void Clear() => Message = string.Empty;

    private static void OnMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((InfoBar)sender).IsOpen = !string.IsNullOrWhiteSpace((string)e.NewValue);
}
