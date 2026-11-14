using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Brushes = Avalonia.Media.Brushes;
using NaniKore.Phonetics;
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
    /// <summary>注音字号相对底字字号的缩放比（略大于旧版 0.61，提升拼音/假名可读性）。</summary>
    private const double RubyFontScale = 0.66;

    /// <summary>注音字间距（像素）：注音字号变小后需加点间距，避免拼音字母粘成一团。</summary>
    private const double RubyLetterSpacing = 0.4;

    /// <summary>底字基础字间距，给中日文字符留出呼吸感。</summary>
    private const double BaseLetterSpacing = 0.8;

    /// <summary>单个字缝允许额外撑开的上限（相对底字字号的倍数）。</summary>
    private const double MaxExtraLetterSpacingRatio = 0.5;

    private readonly TextBlock _rubyBlock;
    private readonly TextBlock _baseBlock;
    private readonly List<(TextBlock Reading, TextBlock Base, IBrush ReadingBrush)> _syllableColumns = [];
    /// <summary>中文多音候选条目：只在存在其他读音时填充，供多音提示色与候选断言。</summary>
    private readonly List<RubyCandidateEntry> _candidateEntries = [];
    /// <summary>逐字读音分组（含普通单音字与多音字），供 hover 面板按模板渲染。</summary>
    private readonly List<SyllableReadingGroup> _readingGroups = [];
    /// <summary>悬浮面板：Popup 承载的半透明 Border，自带顶层宿主，不被滚动区裁切。</summary>
    private readonly Border _hoverPanel;
    private readonly Popup _hoverPopup;
    private bool _isRubyPressed;
    private bool _letterSpacingResolved;

    /// <summary>是否含多音字（该词至少一个音节在词典里还有默认以外的读音）。</summary>
    internal bool HasAlternativeReadings => _candidateEntries.Count > 0;

    /// <summary>多音候选条目快照，供自测断言；生产由候选面板消费。</summary>
    internal IReadOnlyList<RubyCandidateEntry> CandidateEntries => _candidateEntries;

    /// <summary>底字正文。</summary>
    public string BaseText { get; }

    /// <summary>注音读音。</summary>
    public string RubyText { get; }

    /// <summary>注音种类（假名/拼音/音标/通用）。</summary>
    public RubyReadingKind ReadingKind { get; }

    /// <summary>状态栏文本通知请求事件。</summary>
    public static event Action<string>? StatusNotificationRequested;

    public RubyInlineControl(string baseText, string rubyText, double baseFontSize = 14,
        IReadOnlyList<AnnotationSyllable>? syllables = null, int sourceStart = 0)
    {
        BaseText = baseText;
        RubyText = rubyText;
        ReadingKind = RubyReadingTerms.Detect(rubyText);

        Focusable = false;
        VerticalAlignment = VerticalAlignment.Bottom;
        // 紧凑排版：外边距严格归零，避免连续中日文字符之间被拉出虚假切词缝隙；
        // 允许越界不裁切，注音超出自身占位时也能完整绘制
        Margin = new Thickness(0);
        ClipToBounds = false;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 1. 上行注音
        _rubyBlock = new TextBlock
        {
            Text = rubyText,
            FontSize = Math.Max(10, baseFontSize * RubyFontScale),
            LetterSpacing = RubyLetterSpacing,
            LineHeight = double.NaN,
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, -1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        // 悬停高亮 + 悬浮面板：安全解析主题资源，杜绝 UnsetValue 强转异常导致进程崩溃
        _rubyBlock.PointerEntered += (_, _) => { SetReadingForeground(GetAccentBrush(this)); OpenHoverPanel(); };
        _rubyBlock.PointerExited += (_, _) => { RestoreReadingForeground(); _hoverPopup?.Close(); };

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
            LetterSpacing = BaseLetterSpacing,
            LineHeight = double.NaN,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        // 底字行同样可用悬停查看（单列注音模式下底字行隐藏，不影响）
        _baseBlock.PointerEntered += (_, _) => OpenHoverPanel();
        _baseBlock.PointerExited += (_, _) => _hoverPopup?.Close();

        Grid.SetRow(_baseBlock, 1);
        Children.Add(_baseBlock);

        // 3. 中文逐字列只负责显示，命中与复制仍由整词读音行和上下文菜单承载。
        if (syllables is { Count: > 0 } && syllables.Count == baseText.Length &&
            syllables.Select((s, i) => s.Start == sourceStart + i && s.Length == 1).All(valid => valid))
        {
            foreach (var syllable in syllables)
            {
                // 多音提示：词典收录多个候选的字用警示色区分「存在其他读音」，本句默认音仍照常显示
                var hasAlternatives = syllable.Candidates is { Count: > 1 };
                var readingBrush = hasAlternatives ? GetHintBrush(this) : Brushes.Gray;
                var baseChar = baseText.Substring(syllable.Start - sourceStart, syllable.Length);
                var reading = new TextBlock
                {
                    Text = syllable.Reading, FontSize = _rubyBlock.FontSize, LetterSpacing = RubyLetterSpacing,
                    // 必须显式清空行高：否则会继承外层 SelectableTextBlock 的固定 LineHeight,
                    // 使每列被撑到整行高度，注音溢出原文区、并把后续文字挤走
                    LineHeight = double.NaN,
                    Foreground = readingBrush, IsHitTestVisible = false,
                };
                var bottom = new TextBlock
                {
                    Text = baseChar,
                    // 底字命中开启：让渲染出的汉字可框选、右键弹出与注音一致的菜单（事件冒泡到词容器上下文菜单）
                    FontSize = baseFontSize, IsHitTestVisible = true, LineHeight = double.NaN,
                };
                // 底字可命中：悬停同样高亮并弹面板；左键拖拽与右键事件冒泡到 SelectableTextBlock / 词容器上下文菜单
                bottom.PointerEntered += (_, _) => { SetReadingForeground(GetAccentBrush(this)); OpenHoverPanel(); };
                bottom.PointerExited += (_, _) => { RestoreReadingForeground(); _hoverPopup?.Close(); };
                _syllableColumns.Add((reading, bottom, readingBrush));
                Children.Add(reading);
                Children.Add(bottom);

                // 逐字读音分组（默认音在前），供 hover 面板按「同字单换行 / 异字双换行」渲染
                var groupReadings = syllable.Candidates is { Count: > 0 } ? syllable.Candidates : [syllable.Reading];
                _readingGroups.Add(new SyllableReadingGroup(baseChar, groupReadings));

                if (hasAlternatives)
                {
                    foreach (var candidate in syllable.Candidates!)
                    {
                        _candidateEntries.Add(new RubyCandidateEntry(baseChar, candidate,
                            string.Equals(candidate, syllable.Reading, StringComparison.Ordinal)));
                    }
                }
            }
            _rubyBlock.Text = string.Empty;
            _rubyBlock.HorizontalAlignment = HorizontalAlignment.Stretch;
            _rubyBlock.Background = Brushes.Transparent;
            _baseBlock.IsVisible = false;

        }

        // 悬浮候选面板：Popup 承载单个半透明 Border，避免 ToolTip 自带的不透明外壳；锚定注音行
        _hoverPanel = new Border
        {
            Background = GetToolTipBackground(this),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6),
            IsHitTestVisible = false,
            Child = BuildHoverContent(),
        };
        _hoverPopup = new Popup
        {
            PlacementTarget = _rubyBlock,
            Placement = PlacementMode.Top,
            IsLightDismissEnabled = false,
            Child = _hoverPanel,
        };
        Children.Add(_hoverPopup);
        DetachedFromVisualTree += (_, _) => _hoverPopup.Close();

        ContextMenu = CreateContextMenu();
    }

    /// <summary>悬浮面板是否可查看的内容摘要，供自测断言。</summary>
    internal Control? HoverPanelContent => _hoverPanel.Child as Control;

    /// <summary>悬浮面板的容器 Border（半透明无边框），供自测断言。</summary>
    internal Border HoverPanelBorder => _hoverPanel;

    /// <summary>展开悬浮面板；未接入可视树（无顶层）时静默跳过。</summary>
    private void OpenHoverPanel()
    {
        if (_hoverPopup.IsOpen || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }

        try
        {
            _hoverPopup.Open();
        }
        catch
        {
            // 悬浮面板打开失败绝不打崩 UI 主线程；候选仍可通过原文注音与右键复制获得
            _hoverPopup.Close();
        }
    }

    /// <summary>悬停高亮：临时把注音（含逐字列）染成主题高亮色。</summary>
    private void SetReadingForeground(IBrush brush)
    {
        _rubyBlock.Foreground = brush;
        foreach (var (reading, _, _) in _syllableColumns) reading.Foreground = brush;
    }

    /// <summary>悬停结束：逐字列恢复各自底色（多音字回到提示色，其余回灰），不是统一抹成灰色。</summary>
    private void RestoreReadingForeground()
    {
        if (_syllableColumns.Count == 0)
        {
            _rubyBlock.Foreground = Brushes.Gray;
            return;
        }

        foreach (var (reading, _, readingBrush) in _syllableColumns) reading.Foreground = readingBrush;
    }

    /// <summary>测试入口：模拟进入/离开悬停，验证多音提示色能正确恢复。</summary>
    internal void ApplyReadingHoverForTest(bool entered)
    {
        if (entered)
        {
            SetReadingForeground(GetAccentBrush(this));
        }
        else
        {
            RestoreReadingForeground();
        }
    }

    /// <summary>
    /// 依据底字天然宽度与注音宽度推导底字字间距：
    /// 注音超宽时把缺口均摊到相邻底字的字缝里，让每个字的读音与汉字上下对位（拼音/假名排版的常规做法），
    /// 同时把整个词撑开到注音宽度，自然就不会与邻词的注音互相压盖。
    /// 单字底字没有字缝可撑，只能靠占位宽度扩宽，因此保持基础间距。
    /// </summary>
    internal static double ComputeBaseLetterSpacing(int baseLength, double baseWidth, double rubyWidth, double baseFontSize)
    {
        var gaps = baseLength - 1;
        if (gaps <= 0 || rubyWidth <= baseWidth)
        {
            return BaseLetterSpacing;
        }

        // 缺口除以字缝数即每个字缝应添的间距；上限压住半个字宽，避免极端长注音把词拉成散字
        var extra = Math.Min((rubyWidth - baseWidth) / gaps, baseFontSize * MaxExtraLetterSpacingRatio);
        return BaseLetterSpacing + extra;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_syllableColumns.Count > 0)
        {
            foreach (var (reading, bottom, _) in _syllableColumns)
            {
                reading.Measure(Size.Infinity);
                bottom.Measure(Size.Infinity);
            }
            var height = _syllableColumns.Max(c => c.Reading.DesiredSize.Height) +
                _syllableColumns.Max(c => c.Base.DesiredSize.Height) - 1;
            var columnsWidth = _syllableColumns.Sum(c => Math.Max(c.Reading.DesiredSize.Width, c.Base.DesiredSize.Width));
            _rubyBlock.Measure(new Size(columnsWidth, height));
            return new Size(columnsWidth, height);
        }
        if (!_letterSpacingResolved)
        {
            // 首轮先按零字间距测出底字天然宽度，再结合注音宽度决定撑开多少；只结算一次，杜绝测量反馈震荡
            _baseBlock.LetterSpacing = 0;
            base.MeasureOverride(availableSize);
            _letterSpacingResolved = true;
            _baseBlock.LetterSpacing = ComputeBaseLetterSpacing(
                BaseText.Length,
                _baseBlock.DesiredSize.Width,
                _rubyBlock.DesiredSize.Width,
                _baseBlock.FontSize);
        }

        base.MeasureOverride(availableSize);
        var baseDesired = _baseBlock.DesiredSize;
        var rubyDesired = _rubyBlock.DesiredSize;
        var totalHeight = rubyDesired.Height + baseDesired.Height - 1;

        // 占位宽度取底字可视宽度与注音宽度的较大值；
        // 单字底字（或封顶后的超长注音）装不下时就地扩宽本控件，杜绝向两侧悬挂压到邻词的注音上。
        var visualBaseWidth = Math.Max(0, baseDesired.Width - _baseBlock.LetterSpacing);
        var width = Math.Max(visualBaseWidth, rubyDesired.Width);

        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_syllableColumns.Count > 0)
        {
            var topHeight = _syllableColumns.Max(c => c.Reading.DesiredSize.Height);
            var x = 0.0;
            foreach (var (reading, bottom, _) in _syllableColumns)
            {
                var width = Math.Max(reading.DesiredSize.Width, bottom.DesiredSize.Width);
                reading.Arrange(new Rect(x + (width - reading.DesiredSize.Width) / 2, 0,
                    reading.DesiredSize.Width, topHeight));
                bottom.Arrange(new Rect(x + (width - bottom.DesiredSize.Width) / 2, topHeight - 1,
                    bottom.DesiredSize.Width, bottom.DesiredSize.Height));
                x += width;
            }
            _rubyBlock.Arrange(new Rect(0, 0, finalSize.Width, topHeight));
            return finalSize;
        }
        var rubyDesired = _rubyBlock.DesiredSize;
        var baseDesired = _baseBlock.DesiredSize;

        // 注音居中于底字上方；占位宽度已覆盖注音，正常不会溢出，
        // 仅描边/字形边缘细微溢出时自然悬挂，不被裁切
        var rubyX = (finalSize.Width - rubyDesired.Width) / 2.0;
        _rubyBlock.Arrange(new Rect(rubyX, 0, rubyDesired.Width, rubyDesired.Height));

        var baseY = Math.Max(0, rubyDesired.Height - 1);
        // LetterSpacing 会在每个字（含末字）后追加空白，末字尾空会把底字视觉重心带偏；
        // 按「去掉尾空后的可视宽度」居中，保证底字字面与注音同心。
        var visualBaseWidth = baseDesired.Width - _baseBlock.LetterSpacing;
        var baseX = (finalSize.Width - visualBaseWidth) / 2.0;
        _baseBlock.Arrange(new Rect(baseX, baseY, baseDesired.Width, baseDesired.Height));

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
    internal static IBrush GetAccentBrush(Control control) => ResolveBrush(control, "Brush.Accent", Brushes.LimeGreen);

    /// <summary>
    /// 安全解析主题画刷：优先可视树资源，其次应用级资源，最后退回兜底色，
    /// 彻底杜绝直接强转 UnsetValue 导致的 InvalidCastException 致命崩溃。
    /// </summary>
    internal static IBrush ResolveBrush(Control control, string key, IBrush fallback)
    {
        if (control.TryFindResource(key, out var res) && res is IBrush brush)
        {
            return brush;
        }

        if (Application.Current?.TryGetResource(key, out var appRes) == true && appRes is IBrush appBrush)
        {
            return appBrush;
        }

        return fallback;
    }

    /// <summary>多音提示色：复用主题警示色（琥珀/橙），表示该字存在其他读音而默认音仍照常。</summary>
    internal static IBrush GetHintBrush(Control control) => ResolveBrush(control, "Brush.Warning", Brushes.Orange);

    /// <summary>悬浮面板半透明中性底色：沿用卡片色降为半透明（去掉原来的淡绿块与橙色描边框）。</summary>
    private static IBrush GetToolTipBackground(Control control)
    {
        var card = ResolveBrush(control, "Brush.Card.Background", new SolidColorBrush(Color.FromRgb(32, 32, 32)));
        if (card is ISolidColorBrush solid)
        {
            var color = solid.Color;
            return new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B));
        }

        return card;
    }

    /// <summary>
    /// 构建统一悬浮面板内容，普通字与多音字用同一模板：
    /// 每个字一行「底字 读音」；同字的不同读音单换行紧邻；不同字之间留一空行；
    /// 所有字之后空一行，再显示「点击复制注音 / 右键查看更多选项」。
    /// 多音字的当前默认读音用与原文注音相同的橙色高亮（另加半粗体辅助），单音字保持常规色。
    /// 日/英整词无逐字映射时，用一行「底字 读音」代替。
    /// </summary>
    private Control BuildHoverContent()
    {
        var panel = new StackPanel { Spacing = 0 };
        var primary = ResolveBrush(this, "Brush.Text.Primary", Brushes.White);
        var secondary = ResolveBrush(this, "Brush.Text.Secondary", Brushes.LightGray);
        var hint = GetHintBrush(this);
        var size = Math.Max(12, _rubyBlock.FontSize);

        if (_readingGroups.Count > 0)
        {
            var firstGroup = true;
            foreach (var group in _readingGroups)
            {
                if (!firstGroup)
                {
                    // 不同字之间：空一行
                    panel.Children.Add(CreateGap(size));
                }
                firstGroup = false;

                var isMulti = group.Readings.Count > 1;
                for (var i = 0; i < group.Readings.Count; i++)
                {
                    var reading = group.Readings[i];
                    var isDefault = i == 0;
                    panel.Children.Add(BuildReadingRow(group.BaseChar, reading, size, secondary, primary, hint,
                        highlight: isMulti && isDefault));
                }
            }
        }
        else
        {
            // 日/英整词：没有逐字映射，用整词底字 + 读音占位
            panel.Children.Add(BuildReadingRow(BaseText, RubyText, size, secondary, primary, hint, highlight: false));
        }

        // 字与底部提示之间空一行
        panel.Children.Add(CreateGap(size));
        panel.Children.Add(CreateLine("点击复制注音", size, primary));
        panel.Children.Add(CreateLine("右键查看更多选项", size, secondary));

        return panel;
    }

    /// <summary>面板空行：用一个不可见字符撑出与正文一致的一整行高，实现“空一行”。</summary>
    private static TextBlock CreateGap(double size) => new()
    {
        Text = "\u00A0", FontSize = size, LineHeight = double.NaN,
    };

    /// <summary>面板统一文本行：显式清空 LineHeight，避免继承外层 SelectableTextBlock 的固定行高而每行多出空行。</summary>
    private static TextBlock CreateLine(string text, double size, IBrush foreground) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeight.Normal,
        LineHeight = double.NaN,
        Foreground = foreground,
    };

    /// <summary>构造一行「底字 读音」，highlight 时读音用多音提示色并半粗体。</summary>
    private static StackPanel BuildReadingRow(string baseChar, string reading, double size, IBrush secondary,
        IBrush primary, IBrush hint, bool highlight)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var charBlock = CreateLine(baseChar, size, secondary);
        charBlock.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(charBlock);
        var readingBlock = CreateLine(reading, size, highlight ? hint : primary);
        readingBlock.FontWeight = highlight ? FontWeight.SemiBold : FontWeight.Normal;
        readingBlock.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(readingBlock);
        return row;
    }

    /// <summary>多音候选条目：底字、候选读音、是否为该字本句默认读音。</summary>
    internal readonly record struct RubyCandidateEntry(string BaseChar, string Reading, bool IsDefault);

    /// <summary>逐字读音分组：一个底字及其词典收录读音（首项为默认）。</summary>
    internal readonly record struct SyllableReadingGroup(string BaseChar, IReadOnlyList<string> Readings);
}
