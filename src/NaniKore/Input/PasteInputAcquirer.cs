using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using NaniKore.Imaging;
using NaniKore.Ocr;
using FormsDataObject = System.Windows.Forms.DataObject;
using FormsDataFormats = System.Windows.Forms.DataFormats;
using IDataObject = System.Windows.Forms.IDataObject;
using NativeInput = NaniKore.Interop.NativeMethods.NativeInput;

namespace NaniKore.Input;

/// <summary>选取/剪贴板链路的输入来源；决定面板以哪种形态承接本次翻译。</summary>
internal enum PasteInputKind
{
    /// <summary>模拟 Ctrl+C 读到的选区文本；剪贴板保持原样（即复制到的原文）。</summary>
    SelectionText,

    /// <summary>剪贴板里现成的文本；还原快照后原样奉还，读写幂等。</summary>
    ClipboardText,

    /// <summary>剪贴板图像（主人手动复制的）：视觉直喂或本地 OCR，剪贴板终态见 <see cref="PasteInputAcquirer"/>。</summary>
    ClipboardImage,

    /// <summary>程序自己 Ctrl+C 复制到的选区图像：与 <see cref="ClipboardImage"/> 同路由，剪贴板保持原样。</summary>
    SelectionImage,

    /// <summary>vision_oneshot 管线但配置档不支持视觉：宿主据此弹提醒，不静默改道。</summary>
    VisionUnsupported,

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
/// <param name="ImageRoute">剪贴板图像的去向；<see cref="ClipboardImageRoute.UnsupportedVision"/> 时不调用 Recognize，直接回报宿主弹提醒。</param>
/// <param name="Recognize">本地 OCR 委托：传入的位图仅在调用期间有效（铺白产物在状态机返回前释放），不得在调用外持有。</param>
internal sealed record PasteInputRequest(
    ClipboardImageRoute ImageRoute,
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

    /// <summary>
    /// 读系统剪贴板序列号：任何写入（文本/图像/文件引用/空串）都会让它 +1，只读不改变它，
    /// 且不打开剪贴板——并发写入下读取也零异常（实测），是「前台是否回应了 Ctrl+C」最可靠的信号。
    /// </summary>
    uint ReadSequenceNumber();

    /// <summary>把数据对象整体写上剪贴板（OCR 原文追加：原图与识别文本一起进剪贴板）。</summary>
    void SetDataObject(object data, bool copy);
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

    /// <summary>快照里有没有图像（<see cref="DetachImage"/> 的非破坏性版本，供核对完整性用）。</summary>
    public bool HasImage => _image is not null;

    /// <summary>按格式搬走 <paramref name="data"/> 的内容；什么都读不到时返回 null（无可还原内容）。</summary>
    public static ClipboardSnapshot? Capture(IDataObject data)
    {
        // 文本先按产品口径读一遍：GetData(format, false) 在剪贴板代理上偶发返回空串——issue 021
        // 实测出现过「同一次读取里 ReadText 拿到了原文、条目却是空串」，照它还原用户的文本就没了；
        // ReadText 走的 autoConvert 通道稳得多，条目读到空串时按它修正。
        var text = ClipboardPayloads.ReadText(data);
        var formats = new List<ClipboardFormat>();
        foreach (var format in data.GetFormats(autoConvert: false))
        {
            try
            {
                if (data.GetData(format, autoConvert: false) is { } value)
                {
                    if (value is string { Length: 0 } && text is { Length: > 0 } robust && IsTextFormat(format))
                    {
                        value = robust;
                    }

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

        return new ClipboardSnapshot(formats, text, image);
    }

    /// <summary>剪贴板上的两种文本格式名（条目读到空串时按产品口径修正）。</summary>
    private static bool IsTextFormat(string format) =>
        string.Equals(format, FormsDataFormats.UnicodeText, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, FormsDataFormats.Text, StringComparison.OrdinalIgnoreCase);

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
                // 位图条目必须带 autoConvert 写回：DataStore 只把「可转换」条目的别名格式报给剪贴板，
                // System.Drawing.Bitmap 带上它才会同时提供 Bitmap（CF_BITMAP）。一律 autoConvert: false
                // 还原出来的 DataObject 只报 .NET 私有格式名，系统剪贴板上没有 CF_BITMAP，
                // 画图/浏览器/Word 这些只认标准格式的程序就粘不出图——019 的「绝不冲掉用户内容」
                // 在图像上被打破（issue 021 实测：还原后 Win32 枚举只剩 System.Drawing.Bitmap）。
                // 其余格式按原样精确写回，不多报别名，保持快照的格式集合不变。
                target.SetData(entry.Format, autoConvert: entry.Data is Bitmap, entry.Data);
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

    public static Bitmap? ReadImage(IDataObject data)
    {
        // 走「按类型取」的强类型通道，且类型参数必须落在 Bitmap 而不是 Image 上。剪贴板上的图像
        // 有三种存在形态：CF_BITMAP（真实程序复制来的，TYMED_GDI，非强类型读取要求 TYMED_HGLOBAL
        // 直接取不到）；.NET 私有格式 System.Drawing.Bitmap 的序列化副本（还原写回的产物，反序列化
        // 根类型是 Bitmap，按 Image 取会被 Nrbf 的根类型校验挡下）；两者并存。
        // TryGetData<Bitmap> 对 CF_BITMAP 直接 Image.FromHbitmap，对序列化副本按 Bitmap 反序列化，
        // 三条路都通；GetDataPresent + GetData 的老口径在 CF_BITMAP 形态下图就在眼前也读不出来。
        return data.TryGetData<Bitmap>(FormsDataFormats.Bitmap, autoConvert: true, out var image) ? image : null;
    }
}

/// <summary>
/// 选取/剪贴板翻译的输入获取状态机（issue 018 + 019）。优先级：
/// 1. 模拟 Ctrl+C 读到的选区内容（文本，或前台程序复制来的图像）；2. 剪贴板里现成的文本；
/// 3. 剪贴板图像（视觉直喂 / 本地 OCR）；4. 都没有。
/// 剪贴板终态规则：程序自己 Ctrl+C 复制到的内容原样留着；主人手动复制的图像经本地 OCR 后，
/// 原文以附加格式写回、原图必须保住；其余情况一律把快照原样还原。
/// </summary>
internal static class PasteInputAcquirer
{
    /// <summary>轮询间隔：Ctrl+C 是异步落到剪贴板的，100ms 粒度足以在预算内抓到。</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>轮询总预算：超过即认为前台窗口没有响应复制，转而采用剪贴板现有内容。</summary>
    internal static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>只算「文本类」格式：前台回应只剩空文本时，剪贴板等同于被清空（实测原生空文本的格式名集合）。</summary>
    private static readonly HashSet<string> TextFormats =
        [FormsDataFormats.UnicodeText, FormsDataFormats.Text, FormsDataFormats.OemText, "System.String", "Locale"];

    public static PasteInputOutcome Acquire(PasteInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 先全量快照再读序列号基准：Ctrl+C 可能写空串清掉用户剪贴板，快照是唯一的兜底来源。
        var snapshot = request.Clipboard.Snapshot();
        // 轮询基准用剪贴板序列号而不是文本：任何写入都会让它 +1（实测：文本——含与原文一模一样的文本、
        // 图像、文件引用、空串），读它不打开剪贴板、并发写入下也零异常。只读文本的探测器对图像彻底瞎眼：
        // 前台复制图像时会误判「没选中」，超时后的还原又把刚复制到的图像冲掉，最后翻的是旧剪贴板图像。
        var baselineSequence = request.Clipboard.ReadSequenceNumber();
        var sawResponse = false;

        // 取「前台对 Ctrl+C 的回应」：序列号一动就说明剪贴板被写过，把内容搬进自持内存再判定。
        // 返回 null 表示这一轮没有可用回应（没回应，或回应是读不了的内容），交给轮询继续。
        PasteInputOutcome? TakeResponse()
        {
            if (request.Clipboard.ReadSequenceNumber() == baselineSequence)
            {
                return null;
            }

            sawResponse = true;
            ClipboardSnapshot? response = null;
            try
            {
                // COM 代理活不过下一次写入，判定必须在自持内存上进行。
                response = request.Clipboard.Snapshot();
            }
            catch (ExternalException)
            {
                // 剪贴板被别的程序短暂占用：这一轮读不到，下一轮序列号仍显示有回应，会再试。
            }

            if (response?.Text is { Length: > 0 } selection)
            {
                // 序列号一动就说明前台真的写了东西，与原有文本相同也同样是新复制到的选区。
                // 同时释放自持图像副本：文本优先，这份克隆没人会再用，不能拖到 GC 终结。
                response.DetachImage()?.Dispose();
                return new PasteInputOutcome { Kind = PasteInputKind.SelectionText, Text = selection };
            }

            if (response?.DetachImage() is { } selectionImage)
            {
                // 选区图像：程序自己 Ctrl+C 复制的，剪贴板保持原样（018 终态规则）。
                var sequenceAtRead = request.Clipboard.ReadSequenceNumber();
                return TranslateImage(selectionImage, request, response, notice: null, sequenceAtRead, programOwnCopy: true);
            }

            return null;
        }

        request.SendCopy();

        for (var attempt = 1; ; attempt++)
        {
            if (TakeResponse() is { } outcome)
            {
                return outcome;
            }

            if (!request.Tick(attempt))
            {
                break;
            }
        }

        // 收工前再抓一次：复制可能正好落在最后一次轮询的睡眠里，漏掉它就会被还原冲掉。
        if (TakeResponse() is { } lastOutcome)
        {
            return lastOutcome;
        }

        // 前台回应了但给的是读不了的内容（文件引用、私有格式）：原样保留，绝不还原冲掉用户刚复制的东西。
        if (sawResponse && !LeftClipboardEmpty(request.Clipboard))
        {
            return new PasteInputOutcome
            {
                Kind = PasteInputKind.Empty,
                Notice = "前台程序复制了无法翻译的内容（如文件引用），已原样保留你的剪贴板。",
            };
        }

        // 超时（或前台只回了空文本/空剪贴板）：还原快照，之后只从快照（进程自持内存）取内容。
        var notice = RestoreSnapshot(request.Clipboard, snapshot);
        if (snapshot is null)
        {
            return new PasteInputOutcome { Kind = PasteInputKind.Empty, Notice = notice };
        }

        if (snapshot.Text is { Length: > 0 } text)
        {
            // 剪贴板文本：还原即回到原内容，读写幂等，不做任何额外写入。
            snapshot.DetachImage()?.Dispose();
            return new PasteInputOutcome { Kind = PasteInputKind.ClipboardText, Text = text, Notice = notice };
        }

        if (snapshot.DetachImage() is not { } image)
        {
            return new PasteInputOutcome { Kind = PasteInputKind.Empty, Notice = notice };
        }

        // 还原本身就是一次写入：写回防覆盖校验的基准必须取还原之后的序列号。
        var sequenceAfterRestore = request.Clipboard.ReadSequenceNumber();
        return TranslateImage(image, request, snapshot, notice, sequenceAfterRestore, programOwnCopy: false);
    }

    /// <summary>
    /// 前台回应了却只留下空文本/空剪贴板时为 true——那等于毁了用户旧内容却没给回任何东西，
    /// 走还原兜底；文件引用、私有格式等真实内容为 false，必须原样保留（用户刚复制的东西不能被我们冲掉）。
    /// </summary>
    private static bool LeftClipboardEmpty(IClipboardGateway clipboard)
    {
        try
        {
            var current = clipboard.Snapshot();
            return current is null || current.FormatNames.All(static format => TextFormats.Contains(format));
        }
        catch (ExternalException)
        {
            // 读不动时按空处理：还原兜底，与旧行为一致。
            return true;
        }
    }

    /// <summary>
    /// 图像输入的去向（issue 019 裁决）：管线优先，能力不符明确停下，绝不静默改道。
    /// </summary>
    /// <param name="image">取自持快照的图像副本，所有权已转移给本方法，用毕即释。</param>
    /// <param name="source">图像所在的快照：追加写回时按它的自持内容重建剪贴板，不再回头读剪贴板。</param>
    /// <param name="notice">剪贴板还原失败等异状，随结论透传给面板状态栏。</param>
    /// <param name="sequenceAtRead">读到这份图像时的剪贴板序列号。OCR 要跑几百毫秒，写回前用它确认
    /// 剪贴板没被用户动过，绝不覆盖期间新复制的内容。</param>
    /// <param name="programOwnCopy">true＝程序自己 Ctrl+C 复制的（选区图像）→ OCR 原文直接替换；
    /// false＝主人手动复制的（剪贴板图像）→ 原文以「图像 + 文本」附加写回，原图必须保住。</param>
    private static PasteInputOutcome TranslateImage(
        Bitmap image,
        PasteInputRequest request,
        ClipboardSnapshot source,
        string? notice,
        uint sequenceAtRead,
        bool programOwnCopy)
    {
        using (image)
        {
            if (request.ImageRoute == ClipboardImageRoute.UnsupportedVision)
            {
                // 能力不符时明确停下：不直喂（provider 只会报错），也不偷偷 OCR（用户会困惑两把热键为何行为不同）。
                return new PasteInputOutcome { Kind = PasteInputKind.VisionUnsupported, Notice = notice };
            }

            if (request.ImageRoute == ClipboardImageRoute.DirectFeed)
            {
                // 视觉直喂：PNG 原样编码后交给 provider，剪贴板保持不动。
                return new PasteInputOutcome
                {
                    Kind = programOwnCopy ? PasteInputKind.SelectionImage : PasteInputKind.ClipboardImage,
                    ImagePng = PngEncoder.Encode(image),
                    Notice = notice,
                };
            }

            // local_ocr：本地 OCR 识别成文本。
            // 铺白产物是本次调用的临时位图，用毕即释，不拖到 GC 终结。
            using var flattened = FlattenTransparency(image);
            var result = request.Recognize(flattened);
            if (result.Lines.Count == 0)
            {
                return new PasteInputOutcome
                {
                    Kind = PasteInputKind.Empty,
                    Notice = notice ?? "图像中没有识别到文本。",
                };
            }

            if (request.Clipboard.ReadSequenceNumber() == sequenceAtRead)
            {
                // OCR 写回口径（issue 019 第二轮裁决）：按图像的来路分流。
                if (programOwnCopy)
                {
                    // 选区图像是程序自己 Ctrl+C 复制的：识别原文直接替换，没人要那张图。
                    request.Clipboard.SetText(result.Text);
                }
                else
                {
                    // 剪贴板图像是主人手动复制的：原图必须保住，原文以附加格式写回。
                    var appended = new FormsDataObject();
                    source.WriteTo(appended);
                    appended.SetText(result.Text);
                    request.Clipboard.SetDataObject(appended, copy: true);
                }
            }

            return new PasteInputOutcome
            {
                Kind = programOwnCopy ? PasteInputKind.SelectionImage : PasteInputKind.ClipboardImage,
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
    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();

    /// <summary>
    /// 写入核对重试上限：Windows 在 SetClipboardData 失败时（别的程序此刻正开着剪贴板）会静默
    /// 丢掉该格式而 SetDataObject 照返回成功——格式名可能留个坏壳、数据整个不见（issue 021
    /// 实测：96 次还原里 2 次整个格式组悄无声息消失），只能靠写入后把内容读回来核对才发现。
    /// </summary>
    private const int WriteAttempts = 3;

    /// <summary>重试间隔：剪贴板被占用是瞬时状态，稍让一步即可，不值得按 WinForms 内部那套 100ms 量级等待。</summary>
    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 读取重试上限：刚写完的剪贴板会偶发「格式名在位、按格式读不出值」的坏样本（issue 021 实测：
    /// 自定义格式条目整个消失，而同一份内容毫秒后重读就正常——坏样本与好样本只差一次重新读取）。
    /// 多取几份、留更全的那份，别让一次坏读取把用户的内容吞掉。
    /// </summary>
    private const int ReadAttempts = 3;

    /// <summary>重试间隔：坏样本是瞬时状态，稍让一步即可。</summary>
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(20);

    public ClipboardSnapshot? Snapshot()
    {
        ClipboardSnapshot? best = null;
        for (var attempt = 1; ; attempt++)
        {
            var data = System.Windows.Forms.Clipboard.GetDataObject();
            var snapshot = data is null ? null : ClipboardSnapshot.Capture(data);
            if (snapshot is null)
            {
                // 剪贴板读不到、或里面没有可捕内容：没有重取的意义。
                return null;
            }

            if (best is null || snapshot.FormatNames.Count > best.FormatNames.Count)
            {
                best = snapshot;
            }

            if (IsComplete(snapshot, data!) || attempt == ReadAttempts)
            {
                return best;
            }

            Thread.Sleep(ReadRetryDelay);
        }
    }

    /// <summary>
    /// 快照是否把剪贴板上的内容拿全了：每个格式都有条目（CF_BITMAP 别名除外），文本与图像也都读到了。
    /// 刚写完的剪贴板会偶发坏样本（issue 021 实测：自定义格式条目整个消失、图像读成 null、
    /// 文本条目读成空串，而同一份内容毫秒后重读就正常），拿不全就重取，别让一次坏读取吞掉用户内容。
    /// </summary>
    private static bool IsComplete(ClipboardSnapshot snapshot, IDataObject data)
    {
        var formats = data.GetFormats(autoConvert: false);
        if (formats.Any(format =>
                !string.Equals(format, FormsDataFormats.Bitmap, StringComparison.OrdinalIgnoreCase) &&
                !snapshot.FormatNames.Contains(format, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return (ClipboardPayloads.ReadText(data) is not { Length: > 0 } || snapshot.Text is { Length: > 0 })
            && HasReadableImage(data) == snapshot.HasImage;
    }

    /// <summary>按产品口径把图像读一遍看它到底在不在：读出的位图随即释放，这里只要「读不读得到」这个信号。</summary>
    private static bool HasReadableImage(IDataObject data)
    {
        using var image = ClipboardPayloads.ReadImage(data);
        return image is not null;
    }

    public void Restore(ClipboardSnapshot snapshot)
    {
        for (var attempt = 1; ; attempt++)
        {
            var data = new FormsDataObject();
            snapshot.WriteTo(data);
            // copy: true 让数据整体落到系统剪贴板，设置它的 STA 线程随后退出也不会丢内容。
            System.Windows.Forms.Clipboard.SetDataObject(data, copy: true);
            if (attempt == WriteAttempts || Landed(data))
            {
                return;
            }

            Thread.Sleep(WriteRetryDelay);
        }
    }

    public void SetText(string text) => System.Windows.Forms.Clipboard.SetText(text);

    public uint ReadSequenceNumber() => GetClipboardSequenceNumber();

    public void SetDataObject(object data, bool copy)
    {
        // 追加写回同走 SetDataObject，同样可能被别的程序瞬时占用剪掉一块，一并核对重试。
        for (var attempt = 1; ; attempt++)
        {
            System.Windows.Forms.Clipboard.SetDataObject(data, copy);
            if (attempt == WriteAttempts || Landed((IDataObject)data))
            {
                return;
            }

            Thread.Sleep(WriteRetryDelay);
        }
    }

    /// <summary>
    /// 核对刚写上去的内容在系统剪贴板上读得回来：每个格式名都在，且文本与图像按产品口径真读得到。
    /// 只核对名字不够——Windows 会留下渲染失败的格式名（数据是坏壳），必须把内容读一遍。
    /// 读不动剪贴板（正被别的程序占用）按没写成功处理：那正是写入会丢格式的时刻，重试就是了。
    /// </summary>
    private static bool Landed(IDataObject written)
    {
        try
        {
            var data = System.Windows.Forms.Clipboard.GetDataObject();
            if (data is null)
            {
                return false;
            }

            var present = data.GetFormats(autoConvert: false);
            if (!written.GetFormats(autoConvert: false).All(format => present.Contains(format, StringComparer.OrdinalIgnoreCase)))
            {
                return false;
            }

            return ClipboardPayloads.ReadText(data) == ClipboardPayloads.ReadText(written)
                && HasReadableImage(data) == HasReadableImage(written);
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}

/// <summary>用 SendInput 向前台窗口合成一次 Ctrl+C；只有前台程序响应复制时剪贴板才会变。</summary>
internal sealed class SendInputCopySimulator
{
    public static readonly SendInputCopySimulator Default = new();

    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyC = 0x43;

    /// <summary>
    /// x64 对齐下 Win32 INPUT 为 40 字节、联合体自偏移 8 起（type 4 + 填充 4 + 联合 32）。
    /// 架构一旦变化，SendInput 不会报错，只会把按键静默发成垃圾——宁可在此早失败，也不放行。
    /// </summary>
    static SendInputCopySimulator()
    {
        if (Marshal.SizeOf<NativeInput>() != 40 || Marshal.OffsetOf<NativeInput>(nameof(NativeInput.Keyboard)).ToInt64() != 8)
        {
            throw new NotSupportedException("当前架构不受支持：INPUT 结构布局与 x64 假设不符。");
        }
    }

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
    public static PasteInputOutcome Acquire(ClipboardImageRoute imageRoute, Func<Bitmap, OcrResult> recognize)
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
            imageRoute,
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
