using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using NaniKore.Imaging;
using NaniKore.Interop;
using NaniKore.Ocr;
using SkiaSharp;
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
    Func<SKBitmap, OcrResult> Recognize,
    IClipboardGateway Clipboard,
    Action SendCopy,
    PollTick Tick,
    Func<bool> WaitForModifiersReleased);

/// <summary>
/// 轮询节拍器：状态机每读完一次剪贴板调用一次，返回 false 表示预算耗尽、停止轮询。
/// 运行期按 100ms 粒度睡到 600ms 预算；自测注入确定性实现，免去每条用例干等 600ms。
/// </summary>
internal delegate bool PollTick(int attempt);

/// <summary>
/// 剪贴板读写抽象：状态机只认这个接口，运行期用纯 Win32 原生剪贴板网关，自测用内存实现。
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
    /// 且不打开剪贴板——并发写入下读取也零异常，是「前台是否回应了 Ctrl+C」最可靠的信号。
    /// </summary>
    uint ReadSequenceNumber();
}

/// <summary>单个剪贴板格式的自持副本。</summary>
/// <param name="Format">注册的格式名（UnicodeText、Bitmap 或自定义名）。</param>
/// <param name="Data">该格式的托管字节数组。</param>
/// <param name="StandardFormatId">标准 Win32 格式 ID（若为标准格式）。</param>
internal sealed record ClipboardFormat(string Format, byte[] Data, uint? StandardFormatId = null);

/// <summary>
/// 剪贴板快照：按格式把内容搬进进程自持内存，彻底脱离 COM 代理与短命句柄生命周期。
/// </summary>
internal sealed class ClipboardSnapshot : IDisposable
{
    private readonly List<ClipboardFormat> _formats;
    private readonly SKBitmap? _image;

    public ClipboardSnapshot(List<ClipboardFormat> formats, string? text, SKBitmap? image)
    {
        _formats = formats;
        Text = text;
        _image = image;
    }

    /// <summary>快照里的文本（Unicode 优先）；没有文本为 null。</summary>
    public string? Text { get; }

    /// <summary>快照覆盖到的剪贴板格式名。</summary>
    public IReadOnlyList<string> FormatNames => _formats.Select(entry => entry.Format).ToList();

    /// <summary>格式明细。</summary>
    public IReadOnlyList<ClipboardFormat> Formats => _formats;

    /// <summary>快照里有没有图像。</summary>
    public bool HasImage => _image is not null;

    /// <summary>
    /// 取出快照里的图像副本，所有权转移给调用方，用毕负责释放。
    /// </summary>
    public SKBitmap? DetachImage() => _image?.Copy();

    /// <summary>
    /// 基于当前快照生成附加文本的新快照（用于 OCR 原文写回：保留原图与原格式，附加识别文本）。
    /// </summary>
    public ClipboardSnapshot WithText(string newText)
    {
        var newFormats = new List<ClipboardFormat>(_formats.Where(f => !IsTextFormat(f.Format)));
        var textBytes = System.Text.Encoding.Unicode.GetBytes(newText + "\0");
        newFormats.Add(new ClipboardFormat("UnicodeText", textBytes, NativeMethods.CF_UNICODETEXT));
        return new ClipboardSnapshot(newFormats, newText, _image?.Copy());
    }

    public static bool IsTextFormat(string format) =>
        string.Equals(format, "UnicodeText", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "Text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "System.String", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "13", StringComparison.Ordinal) ||
        string.Equals(format, "1", StringComparison.Ordinal);

    public static ClipboardSnapshot FromText(string text)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
        var formats = new List<ClipboardFormat>
        {
            new("UnicodeText", bytes, NativeMethods.CF_UNICODETEXT)
        };
        return new ClipboardSnapshot(formats, text, null);
    }

    public static ClipboardSnapshot FromImage(SKBitmap image)
    {
        var copy = image.Copy();
        var formats = new List<ClipboardFormat>
        {
            new("Bitmap", [], NativeMethods.CF_BITMAP)
        };
        return new ClipboardSnapshot(formats, null, copy);
    }

    public static ClipboardSnapshot FromFiles(string[] files)
    {
        var formats = new List<ClipboardFormat>
        {
            new("FileDrop", System.Text.Encoding.UTF8.GetBytes(string.Join("\n", files)))
        };
        return new ClipboardSnapshot(formats, null, null);
    }

    public void Dispose()
    {
        _image?.Dispose();
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

    /// <summary>触发热键的修饰键最多等待 2 秒；超时就不合成按键，避免把组合键带进 Ctrl+C。</summary>
    internal static readonly TimeSpan ModifierReleaseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>只算「文本类」格式：前台回应只剩空文本时，剪贴板等同于被清空。</summary>
    private static readonly HashSet<string> TextFormats =
        ["UnicodeText", "Text", "OEMText", "System.String", "Locale", "13", "1", "7"];

    public static PasteInputOutcome Acquire(PasteInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 先等触发热键松开，避免模拟 Ctrl+C 被仍按住的 Shift/Alt/Win 改写成另一个组合键。
        var canSendCopy = request.WaitForModifiersReleased();

        // 先全量快照再读序列号基准：Ctrl+C 可能写空串清掉用户剪贴板，快照是唯一的兜底来源。
        var snapshot = request.Clipboard.Snapshot();
        var baselineSequence = request.Clipboard.ReadSequenceNumber();
        var sawResponse = false;

        // 取「前台对 Ctrl+C 的回应」：序列号一动就说明剪贴板被写过，把内容搬进自持内存再判定。
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
                response = request.Clipboard.Snapshot();
            }
            catch (ExternalException)
            {
                // 剪贴板被短暂占用：下一轮序列号仍显示有回应，会再试。
            }

            if (response?.Text is { Length: > 0 } selection)
            {
                response.DetachImage()?.Dispose();
                return new PasteInputOutcome { Kind = PasteInputKind.SelectionText, Text = selection };
            }

            if (response?.DetachImage() is { } selectionImage)
            {
                var sequenceAtRead = request.Clipboard.ReadSequenceNumber();
                return TranslateImage(selectionImage, request, response, notice: null, sequenceAtRead, programOwnCopy: true);
            }

            // 已有非文本格式就是明确回应；不要再等轮询预算，让 Explorer 等剪贴板监听器有机会改写文件引用。
            if (response is not null && response.FormatNames.Any(static format => !TextFormats.Contains(format)))
            {
                return new PasteInputOutcome
                {
                    Kind = PasteInputKind.Empty,
                    Notice = "前台程序复制了无法翻译的内容（如文件引用），已原样保留你的剪贴板",
                };
            }

            return null;
        }

        if (canSendCopy)
        {
            request.SendCopy();
        }

        for (var attempt = 1; canSendCopy; attempt++)
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
                Notice = "前台程序复制了无法翻译的内容（如文件引用），已原样保留你的剪贴板",
            };
        }

        // 超时（或前台只回了空文本/空剪贴板）：还原快照，之后只从快照取内容。
        var notice = canSendCopy ? RestoreSnapshot(request.Clipboard, snapshot) : null;
        if (snapshot is null)
        {
            return new PasteInputOutcome { Kind = PasteInputKind.Empty, Notice = notice };
        }

        if (snapshot.Text is { Length: > 0 } text)
        {
            snapshot.DetachImage()?.Dispose();
            return new PasteInputOutcome { Kind = PasteInputKind.ClipboardText, Text = text, Notice = notice };
        }

        if (snapshot.DetachImage() is not { } image)
        {
            return new PasteInputOutcome { Kind = PasteInputKind.Empty, Notice = notice };
        }

        var sequenceAfterRestore = request.Clipboard.ReadSequenceNumber();
        return TranslateImage(image, request, snapshot, notice, sequenceAfterRestore, programOwnCopy: false);
    }

    private static bool LeftClipboardEmpty(IClipboardGateway clipboard)
    {
        try
        {
            var current = clipboard.Snapshot();
            return current is null || current.FormatNames.All(static format => TextFormats.Contains(format));
        }
        catch (ExternalException)
        {
            return true;
        }
    }

    private static PasteInputOutcome TranslateImage(
        SKBitmap image,
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
                return new PasteInputOutcome { Kind = PasteInputKind.VisionUnsupported, Notice = notice };
            }

            if (request.ImageRoute == ClipboardImageRoute.DirectFeed)
            {
                return new PasteInputOutcome
            {
                    Kind = programOwnCopy ? PasteInputKind.SelectionImage : PasteInputKind.ClipboardImage,
                    ImagePng = PngEncoder.Encode(image),
                    Notice = notice,
                };
            }

            using var opaque = WhiteMatte(image);
            var result = request.Recognize(opaque);
            if (result.Lines.Count == 0 || string.IsNullOrWhiteSpace(result.Text))
            {
                return new PasteInputOutcome
                {
                    Kind = PasteInputKind.Empty,
                    Notice = notice ?? "图像中没有识别到文本",
                };
            }

            if (request.Clipboard.ReadSequenceNumber() == sequenceAtRead)
            {
                if (programOwnCopy)
                {
                    request.Clipboard.SetText(result.Text);
                }
                else
                {
                    // 剪贴板图像是主人手动复制的：原图保住，原文以附加格式写回
                    request.Clipboard.Restore(source.WithText(result.Text));
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

    private static SKBitmap WhiteMatte(SKBitmap source)
    {
        var target = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }

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
            return $"剪贴板还原失败（{exception.Message}）；你的原剪贴板可能已被修改";
        }
    }
}

/// <summary>
/// 纯 Win32 真实剪贴板网关：直接通过 OpenClipboard / GetClipboardData / SetClipboardData 读写，
/// 彻底摆脱 WinForms Clipboard 与 STA 线程生命周期绑定。
/// </summary>
internal sealed class Win32ClipboardGateway : IClipboardGateway
{
    private const int OpenAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    public uint ReadSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    public ClipboardSnapshot? Snapshot()
    {
        if (!TryOpenClipboard())
        {
            return null;
        }

        try
        {
            string? text = null;
            SKBitmap? image = null;
            var formats = new List<ClipboardFormat>();

            // 1. 读 Unicode 文本
            if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
            {
                var hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                if (hData != 0)
                {
                    var ptr = NativeMethods.GlobalLock(hData);
                    if (ptr != 0)
                    {
                        try
                        {
                            text = Marshal.PtrToStringUni(ptr);
                            if (text is not null)
                            {
                                var size = (int)NativeMethods.GlobalSize(hData);
                                var bytes = new byte[size];
                                Marshal.Copy(ptr, bytes, 0, bytes.Length);
                                formats.Add(new ClipboardFormat("UnicodeText", bytes, NativeMethods.CF_UNICODETEXT));
                            }
                        }
                        finally
                        {
                            NativeMethods.GlobalUnlock(hData);
                        }
                    }
                }
            }

            // 2. 读位图（脱离 System.Drawing，纯 Win32 GetDIBits 直灌 SKBitmap）
            if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_BITMAP))
            {
                var hBmp = NativeMethods.GetClipboardData(NativeMethods.CF_BITMAP);
                if (hBmp != 0)
                {
                    image = ReadBitmapFromHBitmap(hBmp);
                    if (image is not null)
                    {
                        formats.Add(new ClipboardFormat("Bitmap", [], NativeMethods.CF_BITMAP));
                    }
                }
            }

            // 3. 文件列表与注册格式均为 HGLOBAL，保留完整数据块及原格式 ID。
            // CF_HDROP 包含 DROPFILES 头和路径列表，不能只记录格式名，否则清空后无法粘贴文件。
            uint formatId = 0;
            var nameBuffer = new char[256];
            while ((formatId = NativeMethods.EnumClipboardFormats(formatId)) != 0)
            {
                if (formatId >= 0xC000 || formatId == NativeMethods.CF_HDROP)
                {
                    var len = formatId == NativeMethods.CF_HDROP
                        ? 0
                        : NativeMethods.GetClipboardFormatNameW(formatId, nameBuffer, nameBuffer.Length);
                    if (len > 0 || formatId == NativeMethods.CF_HDROP)
                    {
                        var name = formatId == NativeMethods.CF_HDROP ? "FileDrop" : new string(nameBuffer, 0, len);
                        var hData = NativeMethods.GetClipboardData(formatId);
                        if (hData != 0)
                        {
                            var ptr = NativeMethods.GlobalLock(hData);
                            if (ptr != 0)
                            {
                                try
                                {
                                    var size = (int)NativeMethods.GlobalSize(hData);
                                    var data = new byte[size];
                                    Marshal.Copy(ptr, data, 0, size);
                                    formats.Add(new ClipboardFormat(name, data, formatId));
                                }
                                finally
                                {
                                    NativeMethods.GlobalUnlock(hData);
                                }
                            }
                        }
                    }
                }
            }

            if (formats.Count == 0 && text is null && image is null)
            {
                return null;
            }

            return new ClipboardSnapshot(formats, text, image);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public void Restore(ClipboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!TryOpenClipboard())
        {
            throw new ExternalException("无法打开系统剪贴板进行还原");
        }

        try
        {
            NativeMethods.EmptyClipboard();

            // 1. 写回文本
            if (snapshot.Text is not null)
            {
                var chars = (snapshot.Text + "\0").ToCharArray();
                var bytesNeeded = (nuint)(chars.Length * sizeof(char));
                var hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, bytesNeeded);
                if (hMem != 0)
                {
                    var ptr = NativeMethods.GlobalLock(hMem);
                    if (ptr != 0)
                    {
                        Marshal.Copy(chars, 0, ptr, chars.Length);
                        NativeMethods.GlobalUnlock(hMem);
                        NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem);
                    }
                    else
                    {
                        NativeMethods.GlobalFree(hMem);
                    }
                }
            }

            // 2. 写回图像（纯 Win32 SetDIBits 构造 HBITMAP，脱离 System.Drawing）
            using var image = snapshot.DetachImage();
            if (image is not null)
            {
                var hBitmap = CreateHBitmapFromSKBitmap(image);
                if (hBitmap != 0)
                {
                    NativeMethods.SetClipboardData(NativeMethods.CF_BITMAP, hBitmap);
                }
            }

            // 3. 写回文件列表及自定义格式，标准格式必须使用原 ID，不能注册同名私有格式。
            foreach (var format in snapshot.Formats)
            {
                if (format.Format is "UnicodeText" or "Bitmap")
                {
                    continue;
                }

                if (format.Data.Length == 0)
                {
                    continue;
                }

                var customId = format.StandardFormatId ?? NativeMethods.RegisterClipboardFormatW(format.Format);

                if (customId != 0)
                {
                    var hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)format.Data.Length);
                    if (hMem != 0)
                    {
                        var ptr = NativeMethods.GlobalLock(hMem);
                        if (ptr != 0)
                        {
                            Marshal.Copy(format.Data, 0, ptr, format.Data.Length);
                            NativeMethods.GlobalUnlock(hMem);
                            NativeMethods.SetClipboardData(customId, hMem);
                        }
                        else
                        {
                            NativeMethods.GlobalFree(hMem);
                        }
                    }
                }
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryOpenClipboard())
        {
            throw new ExternalException("无法打开系统剪贴板写入文本");
        }

        try
        {
            NativeMethods.EmptyClipboard();

            var chars = (text + "\0").ToCharArray();
            var bytesNeeded = (nuint)(chars.Length * sizeof(char));
            var hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, bytesNeeded);
            if (hMem == 0)
            {
                throw new ExternalException("分配全局内存失败");
            }

            var ptr = NativeMethods.GlobalLock(hMem);
            if (ptr == 0)
            {
                NativeMethods.GlobalFree(hMem);
                throw new ExternalException("锁定全局内存失败");
            }

            Marshal.Copy(chars, 0, ptr, chars.Length);
            NativeMethods.GlobalUnlock(hMem);

            if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem) == 0)
            {
                NativeMethods.GlobalFree(hMem);
                throw new ExternalException("设置剪贴板数据失败");
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static bool TryOpenClipboard()
    {
        for (var i = 0; i < OpenAttempts; i++)
        {
            if (NativeMethods.OpenClipboard(0))
            {
                return true;
            }
            Thread.Sleep(RetryDelay);
        }
        return false;
    }

    /// <summary>
    /// 从 Win32 HBITMAP 句柄提取像素并构造 SKBitmap，完全摆脱 System.Drawing.Image.FromHbitmap。
    /// </summary>
    private static unsafe SKBitmap? ReadBitmapFromHBitmap(nint hBmp)
    {
        NativeMethods.BITMAP bm;
        if (NativeMethods.GetObject(hBmp, sizeof(NativeMethods.BITMAP), &bm) == 0)
        {
            return null;
        }

        var width = bm.bmWidth;
        var height = bm.bmHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0)
        {
            return null;
        }

        try
        {
            var memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == 0)
            {
                return null;
            }

            try
            {
                var skBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
                var bmi = new NativeMethods.BITMAPINFO
                {
                    bmiHeader = new NativeMethods.BITMAPINFOHEADER
                    {
                        biSize = (uint)sizeof(NativeMethods.BITMAPINFOHEADER),
                        biWidth = width,
                        biHeight = -height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = NativeMethods.BI_RGB
                    }
                };

                var lines = NativeMethods.GetDIBits(
                    memDc,
                    hBmp,
                    0,
                    (uint)height,
                    (void*)skBitmap.GetPixels(),
                    &bmi,
                    NativeMethods.DIB_RGB_COLORS);

                if (lines == 0)
                {
                    skBitmap.Dispose();
                    return null;
                }

                return skBitmap;
            }
            finally
            {
                NativeMethods.DeleteDC(memDc);
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(0, screenDc);
        }
    }

    /// <summary>
    /// 从 SKBitmap 构造 Win32 HBITMAP 句柄以写回剪贴板，完全脱离 System.Drawing.Bitmap.GetHbitmap。
    /// </summary>
    private static unsafe nint CreateHBitmapFromSKBitmap(SKBitmap image)
    {
        var width = image.Width;
        var height = image.Height;
        var screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0)
        {
            return 0;
        }

        try
        {
            var memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == 0)
            {
                return 0;
            }

            try
            {
                var hBmp = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
                if (hBmp == 0)
                {
                    return 0;
                }

                var source = image.ColorType == SKColorType.Bgra8888 ? image : image.Copy(SKColorType.Bgra8888);
                var needDispose = !ReferenceEquals(source, image);

                try
                {
                    var bmi = new NativeMethods.BITMAPINFO
                    {
                        bmiHeader = new NativeMethods.BITMAPINFOHEADER
                        {
                            biSize = (uint)sizeof(NativeMethods.BITMAPINFOHEADER),
                            biWidth = width,
                            biHeight = -height,
                            biPlanes = 1,
                            biBitCount = 32,
                            biCompression = NativeMethods.BI_RGB
                        }
                    };

                    NativeMethods.SetDIBits(
                        memDc,
                        hBmp,
                        0,
                        (uint)height,
                        (void*)source.GetPixels(),
                        &bmi,
                        NativeMethods.DIB_RGB_COLORS);

                    return hBmp;
                }
                finally
                {
                    if (needDispose)
                    {
                        source.Dispose();
                    }
                }
            }
            finally
            {
                NativeMethods.DeleteDC(memDc);
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(0, screenDc);
        }
    }
}

/// <summary>用 SendInput 向前台窗口合成一次 Ctrl+C；只有前台程序响应复制时剪贴板才会变。</summary>
internal sealed class SendInputCopySimulator
{
    public static readonly SendInputCopySimulator Default = new();

    private static readonly int[] ModifierVirtualKeys = [
        0xA0, 0xA1, // 左右 Shift
        0xA2, 0xA3, // 左右 Ctrl
        0xA4, 0xA5, // 左右 Alt
        0x5B, 0x5C, // 左右 Win
    ];

    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyC = 0x43;

    static SendInputCopySimulator()
    {
        if (Marshal.SizeOf<NativeInput>() != 40 || Marshal.OffsetOf<NativeInput>(nameof(NativeInput.Keyboard)).ToInt64() != 8)
        {
            throw new NotSupportedException("当前架构不受支持：INPUT 结构布局与 x64 假设不符");
        }
    }

    public void SendCopy()
    {
        NativeInput[] presses =
        [
            NativeMethods.KeyboardEvent(VirtualKeyControl, keyUp: false),
            NativeMethods.KeyboardEvent(VirtualKeyC, keyUp: false),
            NativeMethods.KeyboardEvent(VirtualKeyC, keyUp: true),
            NativeMethods.KeyboardEvent(VirtualKeyControl, keyUp: true),
        ];

        var size = Marshal.SizeOf<NativeInput>();
        if (NativeMethods.SendInput((uint)presses.Length, presses, size) == presses.Length)
        {
            return;
        }

        NativeInput[] releases =
        [
            NativeMethods.KeyboardEvent(VirtualKeyC, keyUp: true),
            NativeMethods.KeyboardEvent(VirtualKeyControl, keyUp: true),
        ];
        _ = NativeMethods.SendInput((uint)releases.Length, releases, size);
    }

    public bool WaitForModifiersReleased()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            if (ModifierVirtualKeys.All(static key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) == 0))
            {
                return true;
            }

            Thread.Sleep(10);
        }
        while (stopwatch.Elapsed < PasteInputAcquirer.ModifierReleaseTimeout);

        return ModifierVirtualKeys.All(static key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) == 0);
    }
}

/// <summary>
/// 运行期输入获取入口：在后台线程上安全跑完剪贴板获取流程。
/// </summary>
internal static class ClipboardInputSource
{
    public static PasteInputOutcome Acquire(ClipboardImageRoute imageRoute, Func<SKBitmap, OcrResult> recognize)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
            new Win32ClipboardGateway(),
            SendInputCopySimulator.Default.SendCopy,
            Tick,
            SendInputCopySimulator.Default.WaitForModifiersReleased);

        return RunOnWorkerThread(request);
    }

    private static PasteInputOutcome RunOnWorkerThread(PasteInputRequest request)
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
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return outcome!;
    }
}
