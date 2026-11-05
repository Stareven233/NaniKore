using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Brushes = Avalonia.Media.Brushes;
using Point = Avalonia.Point;

namespace NaniKore.App.Controls;

/// <summary>
/// 复制格式枚举：纯正文、带方括号注音文本、纯读音假名。
/// </summary>
internal enum RubyCopyKind
{
    /// <summary>纯正文（例如：“ここは通れないよ”）。</summary>
    Plain,

    /// <summary>带注音方括号（例如：“ここは通[とお]れないよ”）。</summary>
    Annotated,

    /// <summary>纯读音（例如：“ここはとおれないよ”、IPA 音标）。</summary>
    ReadingOnly,
}

/// <summary>
/// Avalonia 版本的注音两行堆叠内联控件：
/// 上行注音（假名/拼音/音标），下行底字，
/// 支持独立悬停高亮、单击复制读音、右键上下文菜单。
/// </summary>
internal sealed class RubyInlineControl : Grid
{
    private readonly TextBlock _rubyBlock;
    private readonly TextBlock _baseBlock;
    private bool _isRubyPressed;

    /// <summary>底字正文。</summary>
    public string BaseText { get; }

    /// <summary>注音读音。</summary>
    public string RubyText { get; }

    /// <summary>注音种类（假名/拼音/音标/通用）。</summary>
    public RubyReadingKind ReadingKind { get; }

    /// <summary>状态栏文本通知请求事件。</summary>
    public static event Action<string>? StatusNotificationRequested;

    public RubyInlineControl(string baseText, string rubyText, double baseFontSize = 14)
    {
        BaseText = baseText;
        RubyText = rubyText;
        ReadingKind = RubyReadingTerms.Detect(rubyText);

        var readingNoun = RubyReadingTerms.Reading(ReadingKind);
        var baseNoun = RubyReadingTerms.Base(ReadingKind);

        Focusable = false;
        VerticalAlignment = VerticalAlignment.Bottom;
        // 紧凑排版：外边距与裁剪严格归零，允许注音自然悬挂，杜绝连续中日文字符之间被拉出虚假空格切词缝隙
        Margin = new Thickness(0);
        ClipToBounds = false;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 1. 上行注音
        _rubyBlock = new TextBlock
        {
            Text = rubyText,
            FontSize = Math.Max(10, baseFontSize * 0.61),
            LetterSpacing = 0.4,
            LineHeight = double.NaN,
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, -1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_rubyBlock, $"点击复制{readingNoun}: {rubyText}\r\n右键查看更多选项");

        // 悬停高亮反馈（优雅主题高亮或草地绿）：安全解析主题资源，杜绝 UnsetValue 强转异常导致进程崩溃
        _rubyBlock.PointerEntered += (_, _) => _rubyBlock.Foreground = GetAccentBrush(this);
        _rubyBlock.PointerExited += (_, _) => _rubyBlock.Foreground = Brushes.Gray;

        Point rubyPressStartPos = default;

        // 单击复制读音（原地轻触时拦截，按住拖拽划选时不阻断外层 SelectableTextBlock）
        _rubyBlock.PointerPressed += (sender, e) =>
        {
            if (e.GetCurrentPoint(_rubyBlock).Properties.IsLeftButtonPressed)
            {
                _isRubyPressed = true;
                rubyPressStartPos = e.GetPosition(_rubyBlock);
                e.Handled = true;
            }
        };

        _rubyBlock.PointerMoved += (sender, e) =>
        {
            if (_isRubyPressed)
            {
                var curPos = e.GetPosition(_rubyBlock);
                var delta = curPos - rubyPressStartPos;
                if (Math.Abs(delta.X) > 4 || Math.Abs(delta.Y) > 4)
                {
                    // 移动超过微小位移阈值，识别为用户意图划选文本而非单击复制注音
                    _isRubyPressed = false;
                }
            }
        };

        _rubyBlock.PointerReleased += async (sender, e) =>
        {
            if (_isRubyPressed)
            {
                _isRubyPressed = false;
                e.Handled = true;

                var currentPoint = e.GetPosition(_rubyBlock);
                var bounds = new Rect(new Point(0, 0), _rubyBlock.Bounds.Size).Inflate(16);
                if (bounds.Contains(currentPoint))
                {
                    await CopyReadingAsync(TopLevel.GetTopLevel(this)?.Clipboard);
                }
            }
        };

        _rubyBlock.PointerCaptureLost += (_, _) => _isRubyPressed = false;

        Grid.SetRow(_rubyBlock, 0);
        Children.Add(_rubyBlock);

        // 2. 下行底字
        _baseBlock = new TextBlock
        {
            Text = baseText,
            FontSize = baseFontSize,
            LetterSpacing = 0.8,
            LineHeight = double.NaN,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        ToolTip.SetTip(_baseBlock, $"{baseNoun}: {baseText} ({readingNoun}: {rubyText})\r\n右键查看更多选项");

        Grid.SetRow(_baseBlock, 1);
        Children.Add(_baseBlock);

        // 3. 右键专属复制菜单
        ContextMenu = CreateContextMenu();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        var baseDesired = _baseBlock.DesiredSize;
        var rubyDesired = _rubyBlock.DesiredSize;
        var totalHeight = rubyDesired.Height + baseDesired.Height - 1;

        // 适当给底字放大字间距预留呼吸空间，让注音能够舒展对齐底字；
        // 若注音稍宽于底字，在微小阈值（<= 4px）内平滑自适应扩展，让注音与字优雅居中对齐；
        // 超出部分则自然轻微悬挂（Overhang），既满足对齐美感，又彻底杜绝长文本被生硬空格切碎。
        var width = baseDesired.Width > 0 ? baseDesired.Width : rubyDesired.Width;
        if (baseDesired.Width > 0 && rubyDesired.Width > baseDesired.Width)
        {
            var extra = Math.Min(3.0, (rubyDesired.Width - baseDesired.Width) / 2.0);
            width += extra * 2.0;
        }

        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rubyDesired = _rubyBlock.DesiredSize;
        var baseDesired = _baseBlock.DesiredSize;

        // 注音居中于底字上方，允许向两侧轻微溢出悬挂（Overhang），不被裁切
        var rubyX = (finalSize.Width - rubyDesired.Width) / 2.0;
        _rubyBlock.Arrange(new Rect(rubyX, 0, rubyDesired.Width, rubyDesired.Height));

        var baseY = Math.Max(0, rubyDesired.Height - 1);
        _baseBlock.Arrange(new Rect(0, baseY, finalSize.Width, baseDesired.Height));

        return finalSize;
    }

    /// <summary>异步将文本写入剪贴板。</summary>
    public static async Task SetClipboardTextAsync(IClipboard? clipboard, string text)
    {
        if (clipboard is null)
        {
            return;
        }

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(transfer);
    }

    /// <summary>复制读音到剪贴板并激发通知。</summary>
    public async Task CopyReadingAsync(IClipboard? clipboard)
    {
        await SetClipboardTextAsync(clipboard, RubyText);
        StatusNotificationRequested?.Invoke($"已复制{RubyReadingTerms.Reading(ReadingKind)}到剪贴板：{RubyText}");
    }

    /// <summary>复制底字到剪贴板并激发通知。</summary>
    public async Task CopyBaseTextAsync(IClipboard? clipboard)
    {
        await SetClipboardTextAsync(clipboard, BaseText);
        StatusNotificationRequested?.Invoke($"已复制{RubyReadingTerms.Base(ReadingKind)}到剪贴板：{BaseText}");
    }

    /// <summary>复制带注音文本到剪贴板并激发通知。</summary>
    public async Task CopyAnnotatedAsync(IClipboard? clipboard)
    {
        var text = $"{BaseText}[{RubyText}]";
        await SetClipboardTextAsync(clipboard, text);
        StatusNotificationRequested?.Invoke($"已复制带注音文本到剪贴板：{text}");
    }

    /// <summary>构建右键菜单项。</summary>
    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();

        var copyBaseItem = new MenuItem
        {
            Header = $"复制{RubyReadingTerms.Base(ReadingKind)} \"{BaseText}\"",
        };
        copyBaseItem.Click += async (_, _) => await CopyBaseTextAsync(TopLevel.GetTopLevel(this)?.Clipboard);
        menu.Items.Add(copyBaseItem);

        var copyRubyItem = new MenuItem
        {
            Header = $"复制{RubyReadingTerms.Reading(ReadingKind)} \"{RubyText}\"",
        };
        copyRubyItem.Click += async (_, _) => await CopyReadingAsync(TopLevel.GetTopLevel(this)?.Clipboard);
        menu.Items.Add(copyRubyItem);

        var copyAnnotatedItem = new MenuItem
        {
            Header = $"复制带注音 \"{BaseText}[{RubyText}]\"",
        };
        copyAnnotatedItem.Click += async (_, _) => await CopyAnnotatedAsync(TopLevel.GetTopLevel(this)?.Clipboard);
        menu.Items.Add(copyAnnotatedItem);

        return menu;
    }

    /// <summary>
    /// 安全解析主题高亮色画刷：
    /// 沿可视树优先使用 TryFindResource 查找 "Brush.Accent"，
    /// 查不到或未命中时优雅降级为草地绿，彻底杜绝直接强转 UnsetValue 导致的 InvalidCastException 致命崩溃。
    /// </summary>
    internal static IBrush GetAccentBrush(Control control)
    {
        if (control.TryFindResource("Brush.Accent", out var res) && res is IBrush brush)
        {
            return brush;
        }

        if (Application.Current?.TryGetResource("Brush.Accent", out var appRes) == true && appRes is IBrush appBrush)
        {
            return appBrush;
        }

        return Brushes.LimeGreen;
    }
}
