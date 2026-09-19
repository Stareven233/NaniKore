using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfClipboard = System.Windows.Clipboard;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfPoint = System.Windows.Point;

namespace NaniKore.App.Controls;

/// <summary>
/// 振假名内联排版控件：上行小字注音假名，下行汉字正文底字，支持假名/汉字独立复制与快捷右键菜单。
/// </summary>
internal sealed class RubyInlineControl : Grid
{
    private WpfPoint _rubyMouseDownPoint;
    private bool _isRubyPressed;

    /// <summary>通知宿主面板展示操作状态文本的静态事件。</summary>
    public static event Action<string>? StatusNotificationRequested;

    public string BaseText { get; }

    public string RubyText { get; }

    public RubyInlineControl(string baseText, string rubyText, double baseFontSize = 14)
    {
        BaseText = baseText;
        RubyText = rubyText;

        Focusable = false;
        IsHitTestVisible = true; // 允许子元素接收鼠标悬停、单独点击与右键上下文菜单
        Background = WpfBrushes.Transparent; // 确保透明区域也能正确命中鼠标交互
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(1.5, 0, 1.5, 0);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var defaultSecondaryBrush = WpfApplication.Current?.TryFindResource("Brush.Text.Secondary") as WpfBrush ?? WpfBrushes.Gray;
        var hoverAccentBrush = WpfApplication.Current?.TryFindResource("Brush.Accent") as WpfBrush ?? WpfBrushes.DodgerBlue;

        // 1. 上行假名注音：手型光标、悬停提示、左键单击独立复制
        var rubyBlock = new TextBlock
        {
            Text = rubyText,
            FontSize = Math.Max(9, baseFontSize * 0.68),
            FontFamily = WpfApplication.Current?.TryFindResource("Font.Ui") as WpfFontFamily ?? new WpfFontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = defaultSecondaryBrush,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0.5),
            Cursor = WpfCursors.Hand,
            ToolTip = $"点击复制假名: {rubyText}\r\n右键查看更多选项",
        };

        rubyBlock.MouseEnter += (_, _) => rubyBlock.Foreground = hoverAccentBrush;
        rubyBlock.MouseLeave += (_, _) => rubyBlock.Foreground = defaultSecondaryBrush;
        rubyBlock.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _isRubyPressed = true;
            _rubyMouseDownPoint = e.GetPosition(rubyBlock);
            rubyBlock.CaptureMouse();
            // 阻止事件穿透给 FlowDocumentScrollViewer 的 TextEditor，避免其抢占鼠标捕获导致 Up 事件丢失
            e.Handled = true;
        };
        rubyBlock.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!_isRubyPressed)
            {
                return;
            }

            _isRubyPressed = false;
            rubyBlock.ReleaseMouseCapture();
            e.Handled = true;

            var currentPoint = e.GetPosition(rubyBlock);
            var hitBox = new Rect(new WpfPoint(0, 0), rubyBlock.RenderSize);
            hitBox.Inflate(16, 16); // 允许微弱抖动与手势误差
            if (hitBox.Contains(currentPoint))
            {
                CopyReading();
            }
        };
        rubyBlock.LostMouseCapture += (_, _) => _isRubyPressed = false;

        SetRow(rubyBlock, 0);
        Children.Add(rubyBlock);

        // 2. 下行汉字底字：悬停提示、允许原生拖选与双击编辑
        var baseBlock = new TextBlock
        {
            Text = baseText,
            FontSize = baseFontSize,
            FontFamily = WpfApplication.Current?.TryFindResource("Font.Ui") as WpfFontFamily ?? new WpfFontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = WpfApplication.Current?.TryFindResource("Brush.Text.Primary") as WpfBrush ?? WpfBrushes.Black,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            ToolTip = $"汉字: {baseText} (读音: {rubyText})\r\n右键查看更多选项",
        };
        SetRow(baseBlock, 1);
        Children.Add(baseBlock);

        // 3. 专属右键菜单：直达复制汉字、假名与带注音文本
        ContextMenu = CreateContextMenu();
    }

    public void CopyReading()
    {
        _ = TrySetClipboard(RubyText);
        StatusNotificationRequested?.Invoke($"已复制假名到剪贴板：{RubyText}");
    }

    public void CopyBaseText()
    {
        _ = TrySetClipboard(BaseText);
        StatusNotificationRequested?.Invoke($"已复制汉字到剪贴板：{BaseText}");
    }

    public void CopyAnnotated()
    {
        var text = $"{BaseText}[{RubyText}]";
        _ = TrySetClipboard(text);
        StatusNotificationRequested?.Invoke($"已复制带注音文本到剪贴板：{text}");
    }

    private static bool TrySetClipboard(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                WpfClipboard.SetText(text);
                return true;
            }
            catch
            {
                System.Threading.Thread.Sleep(50);
            }
        }

        return false;
    }

    private WpfContextMenu CreateContextMenu()
    {
        var menu = new WpfContextMenu();

        var copyBaseItem = new WpfMenuItem
        {
            Header = $"复制汉字 \"{BaseText}\"",
        };
        copyBaseItem.Click += (_, _) => CopyBaseText();
        menu.Items.Add(copyBaseItem);

        var copyRubyItem = new WpfMenuItem
        {
            Header = $"复制假名 \"{RubyText}\"",
        };
        copyRubyItem.Click += (_, _) => CopyReading();
        menu.Items.Add(copyRubyItem);

        var copyAnnotatedItem = new WpfMenuItem
        {
            Header = $"复制带注音 \"{BaseText}[{RubyText}]\"",
        };
        copyAnnotatedItem.Click += (_, _) => CopyAnnotated();
        menu.Items.Add(copyAnnotatedItem);

        return menu;
    }
}
