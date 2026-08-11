using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Control = System.Windows.Controls.Control;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace NaniKore.App.Controls;

/// <summary>
/// 现代数字输入框，替代 WinForms 的 NumericUpDown：文本可直接编辑，右侧提供上下步进按钮，
/// 失焦或回车时把非法输入夹到合法范围，绝不把无效值写回配置。
/// </summary>
[TemplatePart(Name = TextBoxPart, Type = typeof(TextBox))]
[TemplatePart(Name = UpButtonPart, Type = typeof(ButtonBase))]
[TemplatePart(Name = DownButtonPart, Type = typeof(ButtonBase))]
internal sealed class NumberBox : Control
{
    private const string TextBoxPart = "PART_TextBox";
    private const string UpButtonPart = "PART_Up";
    private const string DownButtonPart = "PART_Down";

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(int),
        typeof(NumberBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged, CoerceValue));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(int),
        typeof(NumberBox),
        new PropertyMetadata(0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(int),
        typeof(NumberBox),
        new PropertyMetadata(int.MaxValue, OnRangeChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step),
        typeof(int),
        typeof(NumberBox),
        new PropertyMetadata(1));

    /// <summary>数值单位后缀（例如「秒」），只作为提示展示，不参与解析。</summary>
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix),
        typeof(string),
        typeof(NumberBox),
        new PropertyMetadata(string.Empty));

    private TextBox? _textBox;

    static NumberBox() => DefaultStyleKeyProperty.OverrideMetadata(
        typeof(NumberBox),
        new FrameworkPropertyMetadata(typeof(NumberBox)));

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_textBox is not null)
        {
            _textBox.LostFocus -= OnTextBoxLostFocus;
            _textBox.PreviewKeyDown -= OnTextBoxPreviewKeyDown;
        }

        _textBox = GetTemplateChild(TextBoxPart) as TextBox;
        if (_textBox is not null)
        {
            _textBox.LostFocus += OnTextBoxLostFocus;
            _textBox.PreviewKeyDown += OnTextBoxPreviewKeyDown;
        }

        if (GetTemplateChild(UpButtonPart) is ButtonBase up)
        {
            up.Click += (_, _) => Nudge(Step);
        }

        if (GetTemplateChild(DownButtonPart) is ButtonBase down)
        {
            down.Click += (_, _) => Nudge(-Step);
        }

        SyncText();
    }

    /// <summary>滚轮微调是数字输入的常见预期；仅在控件获得焦点时生效，避免误改滚动中的页面。</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!IsKeyboardFocusWithin)
        {
            return;
        }

        Nudge(e.Delta > 0 ? Step : -Step);
        e.Handled = true;
    }

    private void OnTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                Nudge(Step);
                e.Handled = true;
                break;
            case Key.Down:
                Nudge(-Step);
                e.Handled = true;
                break;
            case Key.Enter:
                Commit();
                e.Handled = true;
                break;
            default:
                break;
        }
    }

    private void OnTextBoxLostFocus(object sender, RoutedEventArgs e) => Commit();

    private void Nudge(int delta) => Value = Clamp(Value + delta);

    /// <summary>把当前文本解析为数值；无法解析时回退到已有值，让显示与数据始终一致。</summary>
    private void Commit()
    {
        if (_textBox is null)
        {
            return;
        }

        Value = int.TryParse(_textBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed)
            ? Clamp(parsed)
            : Value;
        SyncText();
    }

    private int Clamp(int value) => Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum));

    private void SyncText()
    {
        if (_textBox is not null)
        {
            _textBox.Text = Value.ToString(CultureInfo.CurrentCulture);
        }
    }

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((NumberBox)sender).SyncText();

    private static void OnRangeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        sender.CoerceValue(ValueProperty);

    private static object CoerceValue(DependencyObject sender, object baseValue)
    {
        var box = (NumberBox)sender;
        return Math.Clamp((int)baseValue, box.Minimum, Math.Max(box.Minimum, box.Maximum));
    }
}
