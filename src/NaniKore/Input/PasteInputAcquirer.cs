using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using NaniKore.Ocr;
using FormsDataObject = System.Windows.Forms.DataObject;
using FormsDataFormats = System.Windows.Forms.DataFormats;
using IDataObject = System.Windows.Forms.IDataObject;
using NativeInput = NaniKore.Interop.NativeMethods.NativeInput;
using TextDataFormat = System.Windows.Forms.TextDataFormat;

namespace NaniKore.Input;

/// <summary>选取/剪贴板链路的输入来源；决定面板以哪种形态承接本次翻译。</summary>
internal enum PasteInputKind
{
    /// <summary>模拟 Ctrl+C 读到的选区文本；剪贴板保持原样（即复制到的原文）。</summary>
    SelectionText,

    /// <summary>剪贴板里现成的文本；还原快照后原样奉还，读写幂等。</summary>
    ClipboardText,

    /// <summary>剪贴板图像：配置档声明支持视觉时 PNG 直喂，否则本地 OCR 成文本。</summary>
    ClipboardImage,

    /// <summary>选区、剪贴板文本、剪贴板图像三者皆空。</summary>
    Empty,
}

/// <summary>一次输入获取的结论：原文、可选的视觉 PNG、以及需要告知用户的剪贴板异状。</summary>
internal sealed record PasteInputOutcome
{
    public PasteInputKind Kind { get; init; }

    /// <summary>原文文本；视觉直喂时为空串（内容在 <see cref="ImagePng"/> 里）。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>视觉直喂的 PNG 字节；文本路径为 null。</summary>
    public byte[]? ImagePng { get; init; }

    /// <summary>本地 OCR 识别出的行数，仅图像 OCR 路径使用。</summary>
    public int OcrLineCount { get; init; }

    /// <summary>本地 OCR 耗时，仅图像 OCR 路径使用。</summary>
    public TimeSpan OcrElapsed { get; init; }

    /// <summary>剪贴板还原失败等非致命异状；面板状态栏原样展示。</summary>
    public string? Notice { get; init; }
}

/// <summary>
/// 一次输入获取的全部外部依赖。自测注入内存实现即可完整驱动状态机：
/// 不碰真实剪贴板、不合成真实按键，也不启动 ONNX 引擎（OCR 用假委托）。
/// </summary>
internal sealed record PasteInputRequest(
    bool SupportsVision,
    Func<Bitmap, OcrResult> Recognize,
    IClipboardGateway Clipboard,
    Action SendCopy,
    PollTick Tick);

/// <summary>
/// 轮询节拍器：状态机每读完一次剪贴板调用一次，返回 false 表示预算耗尽、停止轮询。
/// 运行期按 100ms 粒度睡到 600ms 预算；自测注入确定性实现，免去每条用例干等 600ms。
/// </summary>
internal delegate bool PollTick(int attempt);

/// <summary>
/// 剪贴板读写抽象：状态机只认这个接口，运行期用 WinForms 真实剪贴板，自测用内存 DataObject。
/// </summary>
internal interface IClipboardGateway
{
    /// <summary>全量快照当前剪贴板（内容搬进进程自持内存）；剪贴板里什么都没有时返回 null。</summary>
    ClipboardSnapshot? Snapshot();

    /// <summary>把快照整体还原到剪贴板。</summary>
    void Restore(ClipboardSnapshot snapshot);

    /// <summary>用文本整体替换剪贴板内容。</summary>
    void SetText(string text);

    /// <summary>读取当前剪贴板文本（轮询用）；没有文本返回 null。</summary>
    string? ReadCurrentText();
}

/// <summary>单个剪贴板格式的自持副本。</summary>
/// <param name="Format">注册的格式名（UnicodeText、Bitmap 或自定义名）。</param>
/// <param name="Data">该格式的托管数据；读不出来的格式根本不会进快照。</param>
internal sealed record ClipboardFormat(string Format, object Data);

/// <summary>
/// 剪贴板快照：按格式把内容搬进进程自持内存。
/// 之所以不直接持有 <see cref="System.Windows.Forms.Clipboard.GetDataObject"/> 返回的 DataObject：
/// 那个对象背后是剪贴板的 COM 代理，别的程序一改写剪贴板，代理指向的数据就可能被释放，
/// 还原出来是垃圾甚至抛异常——快照必须在改写发生前落地成自持数据。
/// </summary>
internal sealed class ClipboardSnapshot
{
    private readonly IReadOnlyList<ClipboardFormat> _formats;
    private readonly Bitmap? _image;

    private ClipboardSnapshot(IReadOnlyList<ClipboardFormat> formats, string? text, Bitmap? image)
    {
        _formats = formats;
        Text = text;
        _image = image;
    }

    /// <summary>快照里的文本（Unicode 优先，其次 ANSI）；没有文本为 null。</summary>
    public string? Text { get; }

    /// <summary>快照覆盖到的剪贴板格式名，按注册名原样保留（自测用于核对多格式完整性）。</summary>
    public IReadOnlyList<string> FormatNames => _formats.Select(entry => entry.Format).ToArray();

    /// <summary>按格式搬走 <paramref name="data"/> 的内容；什么都读不到时返回 null（无可还原内容）。</summary>
    public static ClipboardSnapshot? Capture(IDataObject data)
    {
        var formats = new List<ClipboardFormat>();
        foreach (var format in data.GetFormats(autoConvert: false))
        {
            try
            {
                if (data.GetData(format, autoConvert: false) is { } value)
                {
                    formats.Add(new ClipboardFormat(format, value));
                }
            }
            catch (Exception exception) when (exception is ExternalException or ArgumentException or NotSupportedException)
            {
                // 个别格式读不出来（延迟渲染/专有格式）就放弃它，绝不影响其余格式进快照。
            }
        }

        // 图像额外自持一份副本：COM 代理背后的 HBITMAP 不归我们所有，剪贴板一改写就可能失效。
        Bitmap? image = null;
        try
        {
            image = ClipboardPayloads.ReadImage(data) is { } source ? new Bitmap(source) : null;
        }
        catch (ExternalException)
        {
            image = null;
        }

        if (formats.Count == 0 && image is null)
        {
            return null;
        }

        return new ClipboardSnapshot(formats, ClipboardPayloads.ReadText(data), image);
    }

    /// <summary>
    /// 取出快照里的图像；这是与 <see cref="_formats"/> 相互独立的一份副本，所有权整个转移给调用方，
    /// 用毕负责释放，不会影响还原用的那份。没有图像返回 null。
    /// </summary>
    public Bitmap? DetachImage() => _image;

    /// <summary>把自持内容按原格式写进 <paramref name="target"/>；真实剪贴板与自测假剪贴板共用这一份。</summary>
    public void WriteTo(FormsDataObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var entry in _formats)
        {
            try
            {
                target.SetData(entry.Format, autoConvert: false, entry.Data);
            }
            catch (Exception exception) when (exception is ExternalException or ArgumentException or NotSupportedException)
            {
                // 同一格式写不回去（极少见）就跳过，保证其余格式仍能还原。
            }
        }
    }
}

/// <summary>
/// DataObject 取值口径（真实剪贴板与自测假剪贴板共用同一份，避免两套判断漂移）：
/// 文本优先 Unicode，其次 ANSI；图像只认标准位图格式。
/// </summary>
internal static class ClipboardPayloads
{
    public static string? ReadText(IDataObject data)
    {
        if (data.GetDataPresent(FormsDataFormats.UnicodeText))
        {
            return data.GetData(FormsDataFormats.UnicodeText) as string;
        }

        return data.GetDataPresent(FormsDataFormats.Text)
            ? data.GetData(FormsDataFormats.Text) as string
            : null;
    }

    public static Bitmap? ReadImage(IDataObject data) =>
        data.GetDataPresent(FormsDataFormats.Bitmap)
            ? data.GetData(FormsDataFormats.Bitmap) as Bitmap
            : null;
}

/// <summary>
/// 选取/剪贴板翻译的输入获取状态机（issue 018）。优先级：
/// 1. 模拟 Ctrl+C 读到的选区文本；2. 剪贴板里现成的文本；3. 剪贴板图像（视觉直喂 / 本地 OCR）；4. 都没有。
/// 剪贴板终态规则：只有「模拟 Ctrl+C 自己复制到的」与「本地 OCR 识别出的」两种原文会留在剪贴板里，
/// 其余情况一律把快照原样还原。
/// </summary>
internal static class PasteInputAcquirer
{
    /// <summary>轮询间隔：Ctrl+C 是异步落到剪贴板的，100ms 粒度足以在预算内抓到。</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>轮询总预算：超过即认为前台窗口没有响应复制，转而采用剪贴板现有内容。</summary>
    internal static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(600);

    public static PasteInputOutcome Acquire(PasteInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 先全量快照再读基准文本：Ctrl+C 可能写空串清掉用户剪贴板，快照是唯一的兜底来源。
        var snapshot = request.Clipboard.Snapshot();
        var baseline = snapshot?.Text;

        request.SendCopy();

        for (var attempt = 1; ; attempt++)
        {
            var current = request.Clipboard.ReadCurrentText();
            // 与基准不同才算「新复制到的内容」：前台程序没响应 Ctrl+C 时剪贴板仍是旧文本，
            // 此时旧文本会在超时后经快照还原路径原样采用，不会被误记成选区。
            if (current is { Length: > 0 } copied && !string.Equals(copied, baseline, StringComparison.Ordinal))
            {
                return new PasteInputOutcome { Kind = PasteInputKind.SelectionText, Text = copied };
            }

            if (!request.Tick(attempt))
            {
                break;
            }
        }

        // 超时：还原快照，之后只从快照（进程自持内存）取内容，不再依赖剪贴板当前状态。
        var notice = RestoreSnapshot(request.Clipboard, snapshot);
        if (snapshot is null)
        {
            return new PasteInputOutcome { Kind = PasteInputKind.Empty, Notice = notice };
        }

        if (snapshot.Text is { Length: > 0 } text)
        {
            // 剪贴板文本：还原即回到原内容，读写幂等，不做任何额外写入。
            return new PasteInputOutcome { Kind = PasteInputKind.ClipboardText, Text = text, Notice = notice };
        }

        if (snapshot.DetachImage() is not { } image)
        {
            return new PasteInputOutcome { Kind = PasteInputKind.Empty, Notice = notice };
        }

        using (image)
        {
            if (request.SupportsVision)
            {
                // 视觉直喂：PNG 原样编码后交给 provider，剪贴板保持不动。
                return new PasteInputOutcome
                {
                    Kind = PasteInputKind.ClipboardImage,
                    ImagePng = EncodePng(image),
                    Notice = notice,
                };
            }

            // 无视觉能力：本地 OCR 识别成文本，原文写回剪贴板（替换图像）。
            var result = request.Recognize(FlattenTransparency(image));
            if (result.Lines.Count == 0)
            {
                return new PasteInputOutcome
                {
                    Kind = PasteInputKind.Empty,
                    Notice = notice ?? "剪贴板图像中没有识别到文本。",
                };
            }

            request.Clipboard.SetText(result.Text);
            return new PasteInputOutcome
            {
                Kind = PasteInputKind.ClipboardImage,
                Text = result.Text,
                OcrLineCount = result.Lines.Count,
                OcrElapsed = result.Elapsed,
                Notice = notice,
            };
        }
    }

    /// <summary>还原剪贴板快照；失败时返回可展示的说明（面板状态栏告知），不影响后续从快照取内容。</summary>
    private static string? RestoreSnapshot(IClipboardGateway clipboard, ClipboardSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        try
        {
            clipboard.Restore(snapshot);
            return null;
        }
        catch (ExternalException exception)
        {
            // 剪贴板被其他程序持有（CLIPBRD_E_CANT_OPEN）时还原失败：内容仍从快照采用。
            return $"剪贴板还原失败：{exception.Message}";
        }
    }

    /// <summary>PNG 编码：与截图链路同一形态，provider 拿到的是无损原图字节。</summary>
    private static byte[] EncodePng(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>
    /// 透明区域铺白后再喂 OCR：剪贴板位图常带 alpha（截图工具/浏览器复制），
    /// PP-OCR 的检测与识别都按不透明纹理处理，直接喂入会把透明区当黑块吞掉文字。
    /// </summary>
    private static Bitmap FlattenTransparency(Bitmap source)
    {
        var flattened = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(flattened))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        return flattened;
    }
}

/// <summary>WinForms 真实剪贴板网关：必须运行在 STA 线程上（见 <see cref="ClipboardInputSource"/>）。</summary>
internal sealed class WinFormsClipboardGateway : IClipboardGateway
{
    public ClipboardSnapshot? Snapshot()
    {
        var data = System.Windows.Forms.Clipboard.GetDataObject();
        return data is null ? null : ClipboardSnapshot.Capture(data);
    }

    public void Restore(ClipboardSnapshot snapshot)
    {
        var data = new FormsDataObject();
        snapshot.WriteTo(data);
        // copy: true 让数据整体落到系统剪贴板，设置它的 STA 线程随后退出也不会丢内容。
        System.Windows.Forms.Clipboard.SetDataObject(data, copy: true);
    }

    public void SetText(string text) => System.Windows.Forms.Clipboard.SetText(text);

    public string? ReadCurrentText()
    {
        try
        {
            return System.Windows.Forms.Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? System.Windows.Forms.Clipboard.GetText(TextDataFormat.UnicodeText)
                : null;
        }
        catch (ExternalException)
        {
            // 剪贴板被其他程序短暂占用：本轮读不到，交给下一次轮询。
            return null;
        }
    }
}

/// <summary>用 SendInput 向前台窗口合成一次 Ctrl+C；只有前台程序响应复制时剪贴板才会变。</summary>
internal sealed class SendInputCopySimulator
{
    public static readonly SendInputCopySimulator Default = new();

    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyC = 0x43;

    public void SendCopy()
    {
        NativeInput[] presses =
        [
            Interop.NativeMethods.KeyboardEvent(VirtualKeyControl, keyUp: false),
            Interop.NativeMethods.KeyboardEvent(VirtualKeyC, keyUp: false),
            Interop.NativeMethods.KeyboardEvent(VirtualKeyC, keyUp: true),
            Interop.NativeMethods.KeyboardEvent(VirtualKeyControl, keyUp: true),
        ];

        var size = Marshal.SizeOf<NativeInput>();
        if (Interop.NativeMethods.SendInput((uint)presses.Length, presses, size) == presses.Length)
        {
            return;
        }

        // 注入被拦（UIPI/提权窗口）或只注入了一部分：补发抬起事件，绝不把 Ctrl 留在按下态。
        // 之后轮询自然超时，链路按「剪贴板现有内容」回落，不额外报错打断用户。
        NativeInput[] releases =
        [
            Interop.NativeMethods.KeyboardEvent(VirtualKeyC, keyUp: true),
            Interop.NativeMethods.KeyboardEvent(VirtualKeyControl, keyUp: true),
        ];
        _ = Interop.NativeMethods.SendInput((uint)releases.Length, releases, size);
    }
}

/// <summary>
/// 运行期输入获取入口：把整个状态机搬到专用 STA 线程上跑完再回主线程。
/// 剪贴板的 OLE 读写要求单线程单元，且 <c>OleSetClipboard</c> 的数据归设置它的线程所有——
/// 线程必须活到本次操作（含还原 / OCR 文本写回）全部完成再退出，不能借用线程池的短命线程。
/// </summary>
internal static class ClipboardInputSource
{
    public static PasteInputOutcome Acquire(bool supportsVision, Func<Bitmap, OcrResult> recognize)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // 运行期节拍器：每 100ms 轮询一次剪贴板，累计达到 600ms 预算即告知状态机收工。
        bool Tick(int _)
        {
            if (stopwatch.Elapsed >= PasteInputAcquirer.PollTimeout)
            {
                return false;
            }

            Thread.Sleep(PasteInputAcquirer.PollInterval);
            return true;
        }

        var request = new PasteInputRequest(
            supportsVision,
            recognize,
            new WinFormsClipboardGateway(),
            SendInputCopySimulator.Default.SendCopy,
            Tick);

        return RunOnStaThread(request);
    }

    private static PasteInputOutcome RunOnStaThread(PasteInputRequest request)
    {
        PasteInputOutcome? outcome = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                outcome = PasteInputAcquirer.Acquire(request);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "NaniKore clipboard input",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            // 保留原始堆栈：STA 线程里抛出的异常不应在 Task.Run 外层只剩一层包装。
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return outcome!;
    }
}
