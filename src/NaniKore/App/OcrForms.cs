using System.Runtime.InteropServices;
using NaniKore.Ocr;

namespace NaniKore.App;

internal sealed class OcrProgressForm : Form
{
    public OcrProgressForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(300, 84);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        ControlBox = false;
        TopMost = true;
        UseWaitCursor = true;
        Text = "NaniKore";

        Controls.Add(new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "正在用本地 PP-OCRv6 识别…",
            TextAlign = ContentAlignment.MiddleCenter,
        });
    }
}

internal sealed class OcrResultForm : Form
{
    private readonly TextBox _textBox;
    private readonly Label _statusLabel;

    public OcrResultForm(OcrResult result, bool copiedToClipboard)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(660, 420);
        MinimumSize = new Size(420, 260);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        Text = "NaniKore OCR";

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = true,
            Font = new Font("Segoe UI", 10f),
            Text = result.Text,
        };

        var copyButton = new Button
        {
            AutoSize = true,
            Text = "复制",
        };
        copyButton.Click += (_, _) => CopyText();

        var closeButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Text = "关闭",
        };

        _statusLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = $"{result.Lines.Count} 行 · {result.Elapsed.TotalMilliseconds:N0} ms" +
                   (copiedToClipboard ? " · 已复制到剪贴板" : string.Empty),
        };

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 3,
            Dock = DockStyle.Bottom,
            Padding = new Padding(8),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_statusLabel, 0, 0);
        footer.Controls.Add(copyButton, 1, 0);
        footer.Controls.Add(closeButton, 2, 0);

        Controls.Add(_textBox);
        Controls.Add(footer);
        AcceptButton = closeButton;
        CancelButton = closeButton;
    }

    private void CopyText()
    {
        if (string.IsNullOrEmpty(_textBox.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_textBox.Text);
            _statusLabel.Text = "已复制到剪贴板";
        }
        catch (ExternalException exception)
        {
            MessageBox.Show(
                $"剪贴板暂时不可用：{exception.Message}",
                "NaniKore",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}