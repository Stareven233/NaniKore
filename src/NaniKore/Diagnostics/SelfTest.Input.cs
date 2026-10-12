using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NaniKore.Input;
using NaniKore.Ocr;
using FormsDataObject = System.Windows.Forms.DataObject;
using FormsDataFormats = System.Windows.Forms.DataFormats;
using IDataObject = System.Windows.Forms.IDataObject;

namespace NaniKore.Diagnostics;

/// <summary>
/// 无 UI 自测（输入链路）：热键手势解析、F1–F12 单键放开、选取/剪贴板输入获取状态机，
/// 以及真实剪贴板的快照→还原 round-trip。
/// 状态机一律走 <see cref="IClipboardGateway"/> 的内存实现，不碰真实剪贴板、不合成真实按键。
/// </summary>
internal static partial class SelfTest
{
    private static void RunInputTests()
    {
        TestHotkeyGestureNormalization();
        TestHotkeyGestureSingleFunctionKeys();
        TestSendInputLayoutGuard();
        TestClipboardImageRouteResolution();
        TestPasteInputFallbackChain();
        TestClipboardSnapshotRoundTrip();
    }

    /// <summary>热键手势规范化：修饰键别名与大小写归一，拒绝空串 / 纯修饰键 / 不支持的按键。</summary>
    private static void TestHotkeyGestureNormalization()
    {
        Check(
            "gesture parsing normalizes modifier aliases and casing",
            HotkeyGesture.Normalize("control+shift+q") == "Ctrl+Shift+Q" &&
            HotkeyGesture.Normalize("windows + alt + f9") == "Alt+Win+F9" &&
            HotkeyGesture.Normalize("Ctrl+Shift+S") == "Ctrl+Shift+S");

        Check(
            "gesture parsing rejects empty, modifier-only and unsupported keys",
            RejectsGesture(string.Empty) && RejectsGesture("Ctrl") && RejectsGesture("Ctrl+Shift+PrintScreen"));

        Check(
            "captured combinations reuse the registration parser",
            HotkeyGesture.TryCapture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, WinForms.Keys.Q, out var captured, out _) &&
            captured == "Ctrl+Shift+Q" &&
            HotkeyGesture.Parse(captured) == (HotkeyGesture.ModControl | HotkeyGesture.ModShift, (uint)WinForms.Keys.Q));

        Check(
            "modifier-only and unmodified keys are refused with a readable reason",
            !HotkeyGesture.TryCapture(HotkeyGesture.ModControl, WinForms.Keys.ControlKey, out _, out var modifierOnly) &&
            modifierOnly.Length > 0 &&
            !HotkeyGesture.TryCapture(0, WinForms.Keys.Q, out _, out var missingModifier) &&
            missingModifier.Length > 0);

        Check(
            "held modifiers echo a partial combination while recording",
            HotkeyGesture.FormatPreview(HotkeyGesture.ModControl | HotkeyGesture.ModShift) == "Ctrl+Shift+…" &&
            HotkeyGesture.FormatPreview(0).Length == 0);

        // 报错文案必须泛化：同一个解析器同时服务截图与选取两个槽位，不允许写死「截图」。
        Check(
            "gesture error messages stay generic and actionable",
            GestureError(string.Empty).Length > 0 &&
            GestureError("Ctrl+Q+A").Length > 0 &&
            !GestureError(string.Empty).Contains("截图", StringComparison.Ordinal));
    }

    /// <summary>
    /// F1–F12 放开零修饰键单键绑定（issue 018）：可录制、可注册、规范化稳定；
    /// F13–F24 与裸字母数字仍然必须搭配修饰键。
    /// </summary>
    private static void TestHotkeyGestureSingleFunctionKeys()
    {
        Check(
            "F1 through F12 parse, normalize and capture without modifiers",
            HotkeyGesture.Normalize("f1") == "F1" &&
            HotkeyGesture.Normalize("F12") == "F12" &&
            HotkeyGesture.Parse("F5") == (0u, (uint)WinForms.Keys.F5) &&
            HotkeyGesture.TryCapture(0, WinForms.Keys.F7, out var bare, out _) &&
            bare == "F7");

        Check(
            "F13 and beyond plus bare letters still require a modifier",
            RejectsGesture("F13") &&
            RejectsGesture("F24") &&
            RejectsGesture("Q") &&
            RejectsGesture("7") &&
            !HotkeyGesture.TryCapture(0, WinForms.Keys.F13, out _, out var bareF13) &&
            bareF13.Length > 0 &&
            HotkeyGesture.TryCapture(HotkeyGesture.ModControl, WinForms.Keys.F13, out var modifiedF13, out _) &&
            modifiedF13 == "Ctrl+F13");

        Check(
            "empty gesture disables registration while any real gesture enables it",
            !HotkeyGesture.IsEnabled(string.Empty) &&
            !HotkeyGesture.IsEnabled("   ") &&
            !HotkeyGesture.IsEnabled(null) &&
            HotkeyGesture.IsEnabled("F1") &&
            HotkeyGesture.IsEnabled("Ctrl+Shift+S"));
    }

    /// <summary>
    /// SendInput 的 INPUT 布局守护（issue 019）：静态构造里的 x64 假设（40 字节、联合体偏移 8）
    /// 在本机必须成立；访问 Default 即触发校验，架构不符会在这里炸开而不是静默发错键。
    /// </summary>
    private static void TestSendInputLayoutGuard()
    {
        Check(
            "the x64 INPUT layout guard holds on this machine",
            SendInputCopySimulator.Default is not null);
    }

    /// <summary>
    /// 图像去向解析（issue 019 裁决）：管线偏好优先；vision_oneshot 遇不支持视觉的配置档明确停下；
    /// 未知管线值按 local_ocr 保守处理，与截图链路口径一致。
    /// </summary>
    private static void TestClipboardImageRouteResolution()
    {
        Check(
            "image route follows the pipeline mode and stops when the profile cannot take images",
            ClipboardImageRouteResolver.Resolve("vision_oneshot", true) == ClipboardImageRoute.DirectFeed &&
            ClipboardImageRouteResolver.Resolve("vision_oneshot", false) == ClipboardImageRoute.UnsupportedVision &&
            ClipboardImageRouteResolver.Resolve("local_ocr", true) == ClipboardImageRoute.LocalOcr &&
            ClipboardImageRouteResolver.Resolve("local_ocr", false) == ClipboardImageRoute.LocalOcr &&
            ClipboardImageRouteResolver.Resolve("Vision_OneShot", true) == ClipboardImageRoute.DirectFeed &&
            ClipboardImageRouteResolver.Resolve("whatever", true) == ClipboardImageRoute.LocalOcr &&
            ClipboardImageRouteResolver.Resolve(null, false) == ClipboardImageRoute.LocalOcr);
    }

    /// <summary>
    /// 输入源判定状态机（issue 018 + 019）：选区文本 / 剪贴板文本 / 剪贴板图像×三条去向 / 全空，
    /// 各自走向与剪贴板终态；另覆盖选区图像×三条去向、相同文本、OCR 写回口径与竞态、
    /// 「Ctrl+C 写空串清空用户剪贴板」「前台复制文件引用」与「还原失败」两条兜底。
    /// </summary>
    private static void TestPasteInputFallbackChain()
    {
        OcrResult FakeOcr(Bitmap bitmap) => new(
            [new OcrLine(new Rectangle(0, 0, 4, 4), "识别行一", 0.99f), new OcrLine(new Rectangle(0, 8, 4, 4), "识别行二", 0.98f)],
            TimeSpan.FromMilliseconds(120));

        // 1) 选区文本：Ctrl+C 后剪贴板出现新文本 → 直接采用，剪贴板不做任何还原。
        var selection = new FakeClipboardGateway { Current = TextDataObject("旧文本") };
        selection.CopyReplacesClipboardWith("选区原文");
        var selectionOutcome = PasteInputAcquirer.Acquire(Request(selection, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "a fresh selection wins over the clipboard and leaves the clipboard untouched",
            selectionOutcome.Kind == PasteInputKind.SelectionText &&
            selectionOutcome.Text == "选区原文" &&
            selection.RestoreCount == 0 &&
            selection.WrittenText is null);

        // 1b) 前台复制了与剪贴板里一模一样的文本：序列号一动就是真的复制过了，判为选区而非剪贴板文本。
        var identical = new FakeClipboardGateway { Current = TextDataObject("同一段文本") };
        identical.CopyReplacesClipboardWith("同一段文本");
        var identicalOutcome = PasteInputAcquirer.Acquire(Request(identical, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "a copy of identical text still counts as a fresh selection",
            identicalOutcome.Kind == PasteInputKind.SelectionText &&
            identicalOutcome.Text == "同一段文本" &&
            identical.RestoreCount == 0 &&
            identical.WrittenText is null);

        // 2) 剪贴板文本：Ctrl+C 无变化 → 还原快照后原样采用，读写幂等。
        var clipboardText = new FakeClipboardGateway { Current = TextDataObject("剪贴板原文") };
        var clipboardTextOutcome = PasteInputAcquirer.Acquire(Request(clipboardText, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "existing clipboard text is restored and reused without rewriting it",
            clipboardTextOutcome.Kind == PasteInputKind.ClipboardText &&
            clipboardTextOutcome.Text == "剪贴板原文" &&
            clipboardTextOutcome.Notice is null &&
            clipboardText.RestoreCount == 1 &&
            clipboardText.WrittenText is null &&
            clipboardText.Current is { } restored &&
            ClipboardPayloads.ReadText(restored) == "剪贴板原文");

        // 3) 剪贴板图像 × 视觉能力：PNG 直喂，剪贴板不动。
        var visionClipboard = new FakeClipboardGateway { Current = ImageDataObject() };
        var visionOutcome = PasteInputAcquirer.Acquire(Request(visionClipboard, ClipboardImageRoute.DirectFeed, FakeOcr));
        Check(
            "a clipboard image feeds the provider directly when the profile supports vision",
            visionOutcome.Kind == PasteInputKind.ClipboardImage &&
            visionOutcome.ImagePng is { Length: > 0 } visionPng &&
            visionPng.ToArray().Take(4).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }) &&
            visionOutcome.Text.Length == 0 &&
            visionOutcome.Notice is null &&
            visionClipboard.RestoreCount == 1 &&
            visionClipboard.WrittenText is null);

        // 3b) 选区图像 × 视觉直喂：前台响应 Ctrl+C 复制来的是图，PNG 直喂，剪贴板保持原样。
        var selectionVision = new FakeClipboardGateway { Current = TextDataObject("旧文本") };
        selectionVision.CopyReplacesClipboardWithImage();
        var selectionVisionOutcome = PasteInputAcquirer.Acquire(Request(selectionVision, ClipboardImageRoute.DirectFeed, FakeOcr));
        Check(
            "an image copied by the foreground app feeds the provider directly",
            selectionVisionOutcome.Kind == PasteInputKind.SelectionImage &&
            selectionVisionOutcome.ImagePng is { Length: > 0 } selectionPng &&
            selectionPng.Take(4).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }) &&
            selectionVisionOutcome.Text.Length == 0 &&
            selectionVision.RestoreCount == 0 &&
            selectionVision.WrittenText is null &&
            selectionVision.WrittenData is null &&
            selectionVision.Current is { } selectionImageData &&
            ClipboardPayloads.ReadImage(selectionImageData) is not null);

        // 3c) 选区图像 × local_ocr：图是程序自己 Ctrl+C 复制的，OCR 原文直接替换，剪贴板不留图。
        var selectionOcr = new FakeClipboardGateway { Current = TextDataObject("旧文本") };
        selectionOcr.CopyReplacesClipboardWithImage();
        var selectionOcrOutcome = PasteInputAcquirer.Acquire(Request(selectionOcr, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "a selection image is recognized locally and the text replaces the program's own copy",
            selectionOcrOutcome.Kind == PasteInputKind.SelectionImage &&
            selectionOcrOutcome.ImagePng is null &&
            selectionOcrOutcome.Text == "识别行一" + Environment.NewLine + "识别行二" &&
            selectionOcrOutcome.OcrLineCount == 2 &&
            selectionOcr.WrittenText == selectionOcrOutcome.Text &&
            selectionOcr.WrittenData is null &&
            selectionOcr.Current is { } selectionReplaced &&
            ClipboardPayloads.ReadImage(selectionReplaced) is null);

        // 3d) 选区图像但配置档不支持视觉：回报宿主弹提醒，OCR 绝不启动，剪贴板保持原样。
        var selectionUnsupported = new FakeClipboardGateway { Current = TextDataObject("旧文本") };
        selectionUnsupported.CopyReplacesClipboardWithImage();
        var selectionUnsupportedOutcome = PasteInputAcquirer.Acquire(Request(selectionUnsupported, ClipboardImageRoute.UnsupportedVision, _ =>
            throw new InvalidOperationException("能力不符时绝不应启动 OCR。")));
        Check(
            "a selection image with no vision support reports back for a user-facing reminder",
            selectionUnsupportedOutcome.Kind == PasteInputKind.VisionUnsupported &&
            selectionUnsupportedOutcome.ImagePng is null &&
            selectionUnsupported.RestoreCount == 0 &&
            selectionUnsupported.WrittenText is null &&
            selectionUnsupported.Current is { } selectionKept &&
            ClipboardPayloads.ReadImage(selectionKept) is not null);

        // 4) 剪贴板图像 × 无视觉能力：本地 OCR 成文本；主人手动复制的图必须保住，原文以附加格式写回。
        var ocrClipboard = new FakeClipboardGateway { Current = ImageDataObject() };
        var ocrOutcome = PasteInputAcquirer.Acquire(Request(ocrClipboard, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "a clipboard image is recognized locally and the text is appended, keeping the image",
            ocrOutcome.Kind == PasteInputKind.ClipboardImage &&
            ocrOutcome.ImagePng is null &&
            ocrOutcome.Text == "识别行一" + Environment.NewLine + "识别行二" &&
            ocrOutcome.OcrLineCount == 2 &&
            ocrOutcome.OcrElapsed == TimeSpan.FromMilliseconds(120) &&
            ocrClipboard.RestoreCount == 1 &&
            ocrClipboard.WrittenText is null &&
            ocrClipboard.WrittenData is not null &&
            ocrClipboard.Current is { } appended &&
            ClipboardPayloads.ReadText(appended) == ocrOutcome.Text &&
            ClipboardPayloads.ReadImage(appended) is not null);

        // 4b) OCR 之前透明区域已铺白：喂进去的必须是 24bpp 不透明位图。
        // 铺白产物在 Acquire 返回前即释放（issue 019），断言只能在识别回调内完成：只取事实，不留引用。
        PixelFormat? ocrPixelFormat = null;
        int? ocrCorner = null;
        var flattenClipboard = new FakeClipboardGateway { Current = ImageDataObject() };
        _ = PasteInputAcquirer.Acquire(Request(flattenClipboard, ClipboardImageRoute.LocalOcr, bitmap =>
        {
            ocrPixelFormat = bitmap.PixelFormat;
            // Color 的 == 连内部 state/knownColor 一起比，GetPixel 取到的值永远不等于 Color.White，只能比 ARGB。
            ocrCorner = bitmap.GetPixel(0, 0).ToArgb();
            return FakeOcr(bitmap);
        }));
        Check(
            "transparency is flattened to white before local ocr",
            ocrPixelFormat is PixelFormat.Format24bppRgb &&
            ocrCorner == Color.White.ToArgb(),
            $"format={ocrPixelFormat} pixel={ocrCorner:X8}");

        // 4c) 剪贴板同时含文本与图像：文本优先，图像既不喂 OCR 也不写回剪贴板。
        var mixed = new FakeClipboardGateway { Current = TextAndImageDataObject("混合原文") };
        var mixedOutcome = PasteInputAcquirer.Acquire(Request(mixed, ClipboardImageRoute.LocalOcr, _ =>
            throw new InvalidOperationException("文本优先时绝不应启动 OCR。")));
        Check(
            "clipboard text wins over a coexisting image without ever starting ocr",
            mixedOutcome.Kind == PasteInputKind.ClipboardText &&
            mixedOutcome.Text == "混合原文" &&
            mixedOutcome.ImagePng is null &&
            mixed.WrittenText is null);

        // 4d) vision_oneshot 但配置档不支持视觉：不直喂也不偷偷 OCR，回报宿主弹提醒；剪贴板原样还原。
        var unsupported = new FakeClipboardGateway { Current = ImageDataObject() };
        var unsupportedOutcome = PasteInputAcquirer.Acquire(Request(unsupported, ClipboardImageRoute.UnsupportedVision, _ =>
            throw new InvalidOperationException("能力不符时绝不应启动 OCR。")));
        Check(
            "an image with no vision support reports back for a user-facing reminder",
            unsupportedOutcome.Kind == PasteInputKind.VisionUnsupported &&
            unsupportedOutcome.ImagePng is null &&
            unsupportedOutcome.Text.Length == 0 &&
            unsupportedOutcome.Notice is null &&
            unsupported.RestoreCount == 1 &&
            unsupported.WrittenText is null);

        // 4e) OCR 期间用户复制了新内容：写回前校验序列号，绝不覆盖用户的新剪贴板。
        var racing = new FakeClipboardGateway { Current = ImageDataObject() };
        var racingOutcome = PasteInputAcquirer.Acquire(Request(racing, ClipboardImageRoute.LocalOcr, _ =>
        {
            racing.CopyReplacesClipboardWith("用户在 OCR 期间复制的新内容");
            racing.ApplyCopy();
            return FakeOcr(new Bitmap(1, 1));
        }));
        Check(
            "a clipboard change during ocr blocks the write-back and keeps the user's new content",
            racingOutcome.Kind == PasteInputKind.ClipboardImage &&
            racingOutcome.Text == "识别行一" + Environment.NewLine + "识别行二" &&
            racing.WrittenText is null &&
            racing.WrittenData is null &&
            ClipboardPayloads.ReadText(racing.Current!) == "用户在 OCR 期间复制的新内容");

        // 5) 全空：只给托盘提示的结论，不写剪贴板。
        var empty = new FakeClipboardGateway { Current = null };
        var emptyOutcome = PasteInputAcquirer.Acquire(Request(empty, ClipboardImageRoute.DirectFeed, FakeOcr));
        Check(
            "nothing anywhere reports empty without touching the clipboard",
            emptyOutcome.Kind == PasteInputKind.Empty &&
            emptyOutcome.Notice is null &&
            empty.RestoreCount == 0 &&
            empty.WrittenText is null);

        // 6) Ctrl+C 写空串（唯一会清空用户剪贴板的场景）：超时后靠还原兜底。
        var cleared = new FakeClipboardGateway { Current = TextDataObject("用户原文") };
        cleared.CopyReplacesClipboardWith(string.Empty);
        var clearedOutcome = PasteInputAcquirer.Acquire(Request(cleared, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "an app that copies an empty string still recovers the user's clipboard text",
            clearedOutcome.Kind == PasteInputKind.ClipboardText &&
            clearedOutcome.Text == "用户原文" &&
            cleared.RestoreCount == 1);

        // 7) 还原失败：内容照取，面板状态栏拿到可展示的说明。
        var failing = new FailingRestoreClipboardGateway { Current = TextDataObject("剪贴板原文") };
        var failingOutcome = PasteInputAcquirer.Acquire(Request(failing, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "a failed restore still yields content plus a status-bar notice",
            failingOutcome.Kind == PasteInputKind.ClipboardText &&
            failingOutcome.Text == "剪贴板原文" &&
            failingOutcome.Notice is { Length: > 0 } notice &&
            notice.Contains("还原失败", StringComparison.Ordinal));

        // 8) 前台回应了文件引用（如资源管理器里选中文件按 Ctrl+C）：读不了的内容原样保留，绝不还原冲掉。
        var fileDrop = new FakeClipboardGateway { Current = TextDataObject("用户原有文本") };
        fileDrop.CopyReplacesClipboardWith(new[] { @"D:\Code\projects\README.md" });
        var fileDropOutcome = PasteInputAcquirer.Acquire(Request(fileDrop, ClipboardImageRoute.DirectFeed, FakeOcr));
        Check(
            "an unusable copy is left alone and reported instead of being restored away",
            fileDropOutcome.Kind == PasteInputKind.Empty &&
            fileDropOutcome.Notice is { Length: > 0 } fileNotice &&
            fileNotice.Contains("无法翻译", StringComparison.Ordinal) &&
            fileDrop.RestoreCount == 0 &&
            fileDrop.WrittenText is null &&
            fileDrop.Current is { } fileData &&
            fileData.GetFormats(false).Contains("FileDrop"));

        // 9) 复制正好落在最后一次轮询的睡眠里：收工前必须再抓一次，否则还原会把刚复制的图冲掉。
        var lateImage = new FakeClipboardGateway { Current = TextDataObject("旧文本") };
        lateImage.CopyReplacesClipboardWithImage();
        var lateOutcome = PasteInputAcquirer.Acquire(new PasteInputRequest(
            ClipboardImageRoute.DirectFeed,
            FakeOcr,
            lateImage,
            () => { },
            // 模拟「Ctrl+C 的响应比轮询预算慢一步」：直到预算耗尽前一刻才落到剪贴板。
            _ =>
            {
                lateImage.ApplyCopy();
                return false;
            }));
        Check(
            "a copy landing in the last poll sleep is still picked up instead of being restored away",
            lateOutcome.Kind == PasteInputKind.SelectionImage &&
            lateOutcome.ImagePng is { Length: > 0 } &&
            lateImage.RestoreCount == 0 &&
            lateImage.Current is { } lateData &&
            ClipboardPayloads.ReadImage(lateData) is not null);
    }

    /// <summary>真实剪贴板快照→还原 round-trip（多格式 DataObject）；无剪贴板访问权时宽容跳过。</summary>
    private static void TestClipboardSnapshotRoundTrip()
    {
        IDataObject? original = null;
        try
        {
            original = WinForms.Clipboard.GetDataObject();
            var gateway = new WinFormsClipboardGateway();

            using (var image = new Bitmap(8, 6))
            {
                var source = new FormsDataObject();
                source.SetText("剪贴板原文");
                source.SetImage(image);
                source.SetData("NaniKore.SelfTest", "自定义格式");
                WinForms.Clipboard.SetDataObject(source, copy: true);

                var snapshot = gateway.Snapshot();
                Check(
                    "real clipboard snapshot exposes text, image and custom formats",
                    snapshot is not null &&
                    snapshot.Text == "剪贴板原文" &&
                    snapshot.DetachImage() is not null &&
                    snapshot.FormatNames.Contains("NaniKore.SelfTest"),
                    $"formats=[{string.Join(", ", snapshot?.FormatNames ?? [])}]");

                // 破坏剪贴板后再还原：多格式内容必须整体回来。
                WinForms.Clipboard.Clear();
                gateway.Restore(snapshot!);
                var restored = gateway.Snapshot();
                Check(
                    "real clipboard restore round-trips every captured format",
                    restored is not null &&
                    restored.Text == "剪贴板原文" &&
                    restored.DetachImage() is not null &&
                    restored.FormatNames.Contains("NaniKore.SelfTest"),
                    $"formats=[{string.Join(", ", restored?.FormatNames ?? [])}]");
            }
        }
        catch (ExternalException)
        {
            // 远程会话/服务会话等拿不到剪贴板访问权：宽容跳过，不影响其余自测。
        }
        finally
        {
            TryRestoreOriginalClipboard(original);
        }
    }

    /// <summary>自测结束把用户原来的剪贴板内容还回去；失败就交给操作系统回收。</summary>
    private static void TryRestoreOriginalClipboard(IDataObject? original)
    {
        try
        {
            if (original is not null)
            {
                WinForms.Clipboard.SetDataObject(original, copy: true);
            }
            else
            {
                WinForms.Clipboard.Clear();
            }
        }
        catch (ExternalException)
        {
            // 还原不了也不让自测失败。
        }
    }

    /// <summary>构造一份「轮询一次即超时」的请求：假剪贴板不会变，等价于前台程序没响应 Ctrl+C。</summary>
    private static PasteInputRequest Request(
        FakeClipboardGateway clipboard,
        ClipboardImageRoute imageRoute,
        Func<Bitmap, OcrResult> recognize) =>
        new(imageRoute, recognize, clipboard, clipboard.ApplyCopy, _ => false);

    private static FormsDataObject TextDataObject(string text)
    {
        var data = new FormsDataObject();
        data.SetText(text);
        return data;
    }

    private static FormsDataObject ImageDataObject()
    {
        // 带 alpha 的位图：状态机必须先铺白再喂 OCR。
        var image = new Bitmap(8, 6, PixelFormat.Format32bppArgb);
        var data = new FormsDataObject();
        data.SetImage(image);
        return data;
    }

    /// <summary>文本与图像共存的剪贴板（如从网页复制富文本）：用于验证文本优先。</summary>
    private static FormsDataObject TextAndImageDataObject(string text)
    {
        var image = new Bitmap(8, 6, PixelFormat.Format32bppArgb);
        var data = new FormsDataObject();
        data.SetText(text);
        data.SetImage(image);
        return data;
    }

    /// <summary>非法手势必须抛 ArgumentException（录制盒与注册表共享同一解析器，拒绝口径一致）。</summary>
    private static bool RejectsGesture(string gesture)
    {
        try
        {
            HotkeyGesture.Parse(gesture);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>取非法手势的用户可读说明；断言文案泛化（不写死具体槽位名）。</summary>
    private static string GestureError(string gesture)
    {
        try
        {
            HotkeyGesture.Parse(gesture);
            return string.Empty;
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>内存剪贴板：记录还原与写回，供断言剪贴板终态。</summary>
    private class FakeClipboardGateway : IClipboardGateway
    {
        /// <summary>当前剪贴板内容；null 表示空剪贴板。</summary>
        public IDataObject? Current { get; set; }

        /// <summary>模拟 Ctrl+C 之后要写进剪贴板的内容；null 表示前台程序没有响应复制。</summary>
        public IDataObject? CopyResult { get; private set; }

        /// <summary>还原调用次数。</summary>
        public int RestoreCount { get; private set; }

        /// <summary>最近一次写回的文本。</summary>
        public string? WrittenText { get; private set; }

        /// <summary>最近一次整体写上的数据对象（OCR 原文追加写回时用于断言剪贴板终态）。</summary>
        public object? WrittenData { get; private set; }

        /// <summary>剪贴板序列号：任何写入都 +1（与真实剪贴板同构），状态机据此感知「前台是否回应了 Ctrl+C」。</summary>
        public uint SequenceNumber { get; private set; }

        /// <summary>设定「Ctrl+C 会复制到的新内容」；空串模拟「程序按 Ctrl+C 写空串清掉剪贴板」。</summary>
        public void CopyReplacesClipboardWith(string text)
        {
            var data = new FormsDataObject();
            // SetText 拒绝空串，这里直接按 UnicodeText 格式落值，等价于「剪贴板里只剩空文本」。
            data.SetData(FormsDataFormats.UnicodeText, false, text);
            CopyResult = data;
        }

        /// <summary>设定「Ctrl+C 会复制到的新内容」为文件引用（如资源管理器里选中文件按 Ctrl+C）。</summary>
        public void CopyReplacesClipboardWith(string[] files)
        {
            var data = new FormsDataObject();
            data.SetData("FileDrop", false, files);
            CopyResult = data;
        }

        /// <summary>设定「Ctrl+C 会复制到的新内容」为图像（前台程序响应复制的是图，不是文字）。</summary>
        public void CopyReplacesClipboardWithImage()
        {
            CopyResult = ImageDataObject();
        }

        public void ApplyCopy()
        {
            Current = CopyResult;
            SequenceNumber++;
        }

        public ClipboardSnapshot? Snapshot() =>
            Current is null ? null : ClipboardSnapshot.Capture(Current);

        public virtual void Restore(ClipboardSnapshot snapshot)
        {
            RestoreCount++;
            var data = new FormsDataObject();
            snapshot.WriteTo(data);
            Current = data;
            SequenceNumber++;
        }

        public void SetText(string text)
        {
            WrittenText = text;
            Current = TextDataObject(text);
            SequenceNumber++;
        }

        public void SetDataObject(object data, bool copy)
        {
            WrittenData = data;
            Current = (IDataObject)data;
            SequenceNumber++;
        }

        public uint ReadSequenceNumber() => SequenceNumber;
    }

    /// <summary>还原必失败的假剪贴板：验证还原失败只产生提示，不影响内容采用。</summary>
    private sealed class FailingRestoreClipboardGateway : FakeClipboardGateway
    {
        public override void Restore(ClipboardSnapshot snapshot) =>
            throw new ExternalException("剪贴板正被其他程序占用。");
    }
}
