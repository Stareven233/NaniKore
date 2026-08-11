using System.Drawing;
using System.Windows.Forms;

namespace NaniKore.App.Settings;

/// <summary>
/// 设置页统一使用的两列表格布局：标签列按内容自适应，输入列占满剩余宽度。
/// 全部尺寸交给 WinForms 的字体/DPI 缩放决定，避免硬编码坐标在 150%、200% 缩放下与说明文字错位。
/// </summary>
internal static class SettingsLayout
{
    /// <summary>页面四周留白；输入框右侧同样保留同等留白，不贴到页面边缘。</summary>
    internal const int OuterMargin = 16;

    private const int RowSpacing = 6;

    /// <summary>创建可承载字段行的表格；表格随内容增高，由外层 AutoScroll 面板负责滚动。</summary>
    internal static TableLayoutPanel CreateFieldTable() => new()
    {
        ColumnCount = 2,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        Padding = new Padding(OuterMargin),
        GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        ColumnStyles =
        {
            new ColumnStyle(SizeType.AutoSize),
            new ColumnStyle(SizeType.Percent, 100f),
        },
    };

    /// <summary>添加「标签 + 输入控件」行；标签垂直居中于输入控件，输入控件横向填满内容宽度。</summary>
    internal static void AddField(TableLayoutPanel table, string label, Control field)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(field);

        var row = table.RowCount;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(CreateFieldLabel(label), 0, row);
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        field.Margin = new Padding(0, RowSpacing, 0, RowSpacing);
        table.Controls.Add(field, 1, row);
        table.RowCount = row + 1;
    }

    /// <summary>添加跨两列的整行控件，用于复选框、多行文本和说明文字。</summary>
    internal static void AddFullWidthRow(TableLayoutPanel table, Control control, bool stretch = true)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(control);

        var row = table.RowCount;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Anchor = stretch ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
        control.Margin = new Padding(0, RowSpacing, 0, RowSpacing);
        table.SetColumnSpan(control, 2);
        table.Controls.Add(control, 0, row);
        table.RowCount = row + 1;
    }

    /// <summary>
    /// 字段标签填满单元格并垂直居中：输入控件与标签的自然高度随字体不同步增长，
    /// 只有让标签跟着行高伸展，缩放到 150%、200% 时文字才不会与输入框错位。
    /// </summary>
    internal static Label CreateFieldLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, RowSpacing, RowSpacing + 2, RowSpacing),
        };
        // 宽度仍按文字实际需要，交给 AutoSize 列决定标签列宽。
        label.FontChanged += (_, _) => ApplyLabelWidth(label);
        ApplyLabelWidth(label);
        return label;
    }

    private static void ApplyLabelWidth(Label label)
    {
        var preferred = label.GetPreferredSize(Size.Empty);
        label.MinimumSize = new Size(preferred.Width, 0);
        label.Width = preferred.Width;
    }

    /// <summary>把输入控件与右侧操作按钮组合成一行：输入控件伸缩，按钮按文字自适应宽度。</summary>
    internal static TableLayoutPanel CreateFieldWithActions(Control field, params Control[] actions)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(actions);

        var host = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = actions.Length + 1,
            RowCount = 1,
            Margin = Padding.Empty,
        };
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        field.Margin = Padding.Empty;
        host.Controls.Add(field, 0, 0);

        for (var index = 0; index < actions.Length; index++)
        {
            var action = actions[index];
            host.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            action.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            action.Margin = new Padding(RowSpacing, 0, 0, 0);
            host.Controls.Add(action, index + 1, 0);
        }

        return host;
    }

    /// <summary>操作按钮统一使用 AutoSize 与最小高度，避免高 DPI 或大字体下文字被裁切。</summary>
    internal static Button CreateActionButton(string text, int minimumWidth = 0) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(minimumWidth, 30),
        Padding = new Padding(8, 2, 8, 2),
    };

    /// <summary>整行标题文字（如「翻译提示词：」）不参与两列对齐，按内容自适应即可。</summary>
    internal static Label CreateCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
    };

    /// <summary>说明与状态文字必须能换行，否则窄窗口或大字体下会被裁掉尾部。</summary>
    internal static Label CreateWrappingLabel(string text = "") => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(1, 0),
    };

    /// <summary>AutoSize 文本控件需要显式的最大宽度才会换行；宽度变化时同步更新。</summary>
    internal static void UpdateWrapWidth(Control container, params Label[] labels)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(labels);

        var available = container.ClientSize.Width - (OuterMargin * 2) - SystemInformation.VerticalScrollBarWidth;
        if (available <= 0)
        {
            return;
        }

        foreach (var label in labels)
        {
            label.MaximumSize = new Size(available, 0);
        }
    }
}
