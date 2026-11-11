using System.ComponentModel;

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.Capture;
using NaniKore.Input;
using NaniKore.Interop;
using NaniKore.Ocr;
using NaniKore.Overlay;
using SkiaSharp;

namespace NaniKore.Diagnostics;

/// <summary>
/// 无 UI 自测（平台与输入链路）：热键手势解析、F1–F11 单键与 F12 保留规则、输入获取状态机、
/// 纯 Win32 真实剪贴板 round-trip、虚拟屏幕捕获与选区几何、热键宿主与应用图标。
/// </summary>
internal static partial class SelfTest
{
    private const uint VkQ = (uint)'Q';
    private const uint VkControl = 0x11;
    private const uint VkF5 = 0x74;
    private const uint VkF7 = 0x76;
    private const uint VkF13 = 0x7C;
    private static readonly TimeSpan HotkeyTestTimeout = TimeSpan.FromSeconds(5);

    private static void RunInputTests()
    {
        TestHotkeyGestureNormalization();
        TestHotkeyGestureSingleFunctionKeys();
        TestHotkeyExpandedKeys();
        TestHotkeyRecorderMapping();
        TestHotkeyRecordingEditingKeys();
        TestSendInputLayoutGuard();
        TestClipboardImageRouteResolution();
        TestPasteInputFallbackChain();
        TestClipboardSnapshotRoundTrip();
        TestForeignImageClipboardRoundTrip();
        TestFileClipboardRoundTrip();
        TestDesktopCapturerAndGeometry();
        TestWin32HotkeyHostLifecycle();
        TestApplicationIconAndComponents();
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
            RejectsGesture(string.Empty) && RejectsGesture("Ctrl") && RejectsGesture("Ctrl+Shift+NotAKey"));

        foreach (var malformed in new[] { "Ctrl++Q", "Ctrl+", "Ctrl+é", "Ctrl+中", "Ctrl+１", "Ctrl+😀" })
        {
            Check($"gesture parsing rejects malformed or Unicode key {malformed}", RejectsGesture(malformed));
        }

        Check(
            "captured combinations reuse the registration parser",
            HotkeyGesture.TryCapture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, VkQ, out var captured, out _) &&
            captured == "Ctrl+Shift+Q" &&
            HotkeyGesture.Parse(captured) == (HotkeyGesture.ModControl | HotkeyGesture.ModShift, VkQ));

        Check(
            "modifier-only and unmodified keys are refused with a readable reason",
            !HotkeyGesture.TryCapture(HotkeyGesture.ModControl, VkControl, out _, out var modifierOnly) &&
            modifierOnly.Length > 0 &&
            !HotkeyGesture.TryCapture(0, VkQ, out _, out var missingModifier) &&
            missingModifier.Length > 0);

        Check(
            "held modifiers echo a partial combination while recording",
            HotkeyGesture.FormatPreview(HotkeyGesture.ModControl | HotkeyGesture.ModShift) == "Ctrl+Shift+…" &&
            HotkeyGesture.FormatPreview(0).Length == 0);

        Check(
            "gesture error messages stay generic and actionable",
            GestureError(string.Empty).Length > 0 &&
            GestureError("Ctrl+Q+A").Length > 0 &&
            !GestureError(string.Empty).Contains("截图", StringComparison.Ordinal));
    }

    /// <summary>
    /// F1–F11 可单独绑定；F12 保留给 Windows 调试器，任何修饰键组合均不可绑定。
    /// F13–F24 与裸字母数字仍然必须搭配修饰键。
    /// </summary>
    private static void TestHotkeyGestureSingleFunctionKeys()
    {
        for (uint key = 0x70; key <= 0x7A; key++)
        {
            var name = $"F{key - 0x70 + 1}";
            Check(
                $"{name} parses, formats and captures without modifiers",
                HotkeyGesture.Normalize(name.ToLowerInvariant()) == name &&
                HotkeyGesture.Parse(name) == (0u, key) &&
                HotkeyGesture.Format(0, key) == name &&
                HotkeyGesture.TryCapture(0, key, out var captured, out var message) &&
                captured == name && message.Length == 0);
        }

        // 穷举修饰键位，确保加修饰键也不能绕过 F12 系统保留规则。
        for (uint modifiers = 0; modifiers <= 0x0F; modifiers++)
        {
            Check(
                $"reserved F12 is rejected in parsing and capture with modifiers {modifiers}",
                RejectsGesture(HotkeyGesture.Format(modifiers, 0x7B)) &&
                !HotkeyGesture.TryCapture(modifiers, 0x7B, out var captured, out var message) &&
                captured.Length == 0 && message.Contains("F12", StringComparison.Ordinal));
        }

        Check("bare function key virtual codes remain stable",
            HotkeyGesture.Parse("F5") == (0u, VkF5) &&
            HotkeyGesture.TryCapture(0, VkF7, out var bare, out _) && bare == "F7");

        Check(
            "F13 and beyond plus bare letters still require a modifier",
            RejectsGesture("F13") &&
            RejectsGesture("F24") &&
            RejectsGesture("Q") &&
            RejectsGesture("7") &&
            !HotkeyGesture.TryCapture(0, VkF13, out _, out var bareF13) &&
            bareF13.Length > 0 &&
            HotkeyGesture.TryCapture(HotkeyGesture.ModControl, VkF13, out var modifiedF13, out _) &&
            modifiedF13 == "Ctrl+F13");

        Check(
            "empty gesture disables registration while any real gesture enables it",
            !HotkeyGesture.IsEnabled(string.Empty) &&
            !HotkeyGesture.IsEnabled("   ") &&
            !HotkeyGesture.IsEnabled(null) &&
            HotkeyGesture.IsEnabled("F1") &&
            HotkeyGesture.IsEnabled("Ctrl+Shift+S"));
    }

    /// <summary>扩展键名与 OEM 别名必须按稳定虚拟键码往返，小键盘数字不能折叠成主键盘数字。</summary>
    private static void TestHotkeyExpandedKeys()
    {
        foreach (var (key, name) in new (uint, string)[]
        {
            (0x08, "Backspace"), (0x09, "Tab"), (0x0C, "Clear"), (0x0D, "Enter"),
            (0x13, "Pause"), (0x14, "CapsLock"), (0x1B, "Escape"), (0x20, "Space"),
            (0x21, "PageUp"), (0x22, "PageDown"), (0x23, "End"), (0x24, "Home"),
            (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"),
            (0x2C, "PrintScreen"), (0x2D, "Insert"), (0x2E, "Delete"), (0x5D, "Apps"),
            (0x6A, "Multiply"), (0x6B, "Add"), (0x6C, "Separator"), (0x6D, "Subtract"),
            (0x6E, "Decimal"), (0x6F, "Divide"), (0x90, "NumLock"), (0x91, "ScrollLock"),
            (0xA6, "BrowserBack"), (0xAC, "BrowserHome"), (0xAD, "VolumeMute"), (0xAF, "VolumeUp"),
            (0xB0, "MediaNextTrack"), (0xB3, "MediaPlayPause"), (0xB4, "LaunchMail"),
            (0xB5, "SelectMedia"), (0xB6, "LaunchApplication1"), (0xB7, "LaunchApplication2"),
            (0xDF, "Oem8"), (0xE2, "OemBackslash"), (0x87, "F24"),
        })
        {
            Check($"expanded key {name} parses, captures and formats canonically",
                HotkeyGesture.Parse($"ctrl+{name.ToLowerInvariant()}") == (HotkeyGesture.ModControl, key) &&
                HotkeyGesture.Format(HotkeyGesture.ModControl, key) == $"Ctrl+{name}" &&
                HotkeyGesture.TryCapture(HotkeyGesture.ModControl, key, out var captured, out _) &&
                captured == $"Ctrl+{name}" &&
                HotkeyGesture.Parse(captured) == (HotkeyGesture.ModControl, key));
        }

        foreach (var (alias, canonical, key) in new (string, string, uint)[]
        {
            (";", "OemSemicolon", 0xBA), ("=", "OemPlus", 0xBB), ("Plus", "OemPlus", 0xBB),
            (",", "OemComma", 0xBC), ("-", "OemMinus", 0xBD), ("Minus", "OemMinus", 0xBD),
            (".", "OemPeriod", 0xBE), ("/", "OemQuestion", 0xBF), ("`", "OemTilde", 0xC0),
            ("[", "OemOpenBrackets", 0xDB), ("\\", "OemPipe", 0xDC),
            ("]", "OemCloseBrackets", 0xDD), ("'", "OemQuotes", 0xDE),
        })
        {
            Check($"OEM alias {alias} round-trips through stable key name {canonical}",
                HotkeyGesture.Normalize($"Ctrl+{alias}") == $"Ctrl+{canonical}" &&
                HotkeyGesture.Parse($"Ctrl+{canonical}") == (HotkeyGesture.ModControl, key) &&
                HotkeyGesture.TryCapture(HotkeyGesture.ModControl, key, out var captured, out _) &&
                captured == $"Ctrl+{canonical}" &&
                HotkeyGesture.Format(HotkeyGesture.ModControl, key) == captured);
        }

        for (uint digit = 0; digit <= 9; digit++)
        {
            Check($"NumPad{digit} stays distinct from top-row {digit}",
                HotkeyGesture.Parse($"Ctrl+NumPad{digit}") == (HotkeyGesture.ModControl, 0x60 + digit) &&
                HotkeyGesture.Parse($"Ctrl+{digit}") == (HotkeyGesture.ModControl, 0x30 + digit) &&
                HotkeyGesture.TryCapture(HotkeyGesture.ModControl, 0x60 + digit, out var captured, out _) &&
                captured == $"Ctrl+NumPad{digit}" &&
                HotkeyGesture.Format(HotkeyGesture.ModControl, 0x30 + digit) == $"Ctrl+{digit}");
        }
    }

    /// <summary>遍历 Avalonia 枚举，防止录制映射新增按键后遗漏解析器支持。</summary>
    private static void TestHotkeyRecorderMapping()
    {
        foreach (var key in Enum.GetValues<Key>().Distinct())
        {
            var vk = HotkeyRecorderBox.ToVirtualKey(key);
            if (vk == 0 || HotkeyGesture.IsModifierKey(vk))
            {
                continue;
            }

            if (vk == 0x7B)
            {
                Check($"mapped {key} remains reserved",
                    !HotkeyGesture.TryCapture(HotkeyGesture.ModControl, vk, out _, out _));
                continue;
            }

            Check($"Avalonia {key} maps to a captureable virtual key and round-trips",
                HotkeyGesture.TryCapture(HotkeyGesture.ModControl, vk, out var captured, out var message) &&
                message.Length == 0 &&
                HotkeyGesture.Parse(captured) == (HotkeyGesture.ModControl, vk) &&
                HotkeyGesture.Format(HotkeyGesture.ModControl, vk) == captured &&
                HotkeyGesture.Normalize(captured) == captured);
        }
    }

    /// <summary>编辑键带修饰键时录制为热键；只有裸 Esc 执行取消或解绑。</summary>
    private static void TestHotkeyRecordingEditingKeys()
    {
        // 输入测试可能先于 UI 套件运行，控件创建前显式准备 Avalonia 服务。
        EnsureThemedApplication();
        foreach (var allowUnbind in new[] { false, true })
        {
            var box = new HotkeyRecorderBox { Gesture = "Ctrl+Q", AllowUnbind = allowUnbind, EmptyHint = "未绑定" };
            var transitions = new List<bool>();
            box.RecordingChanged += (_, recording) => transitions.Add(recording);
            foreach (var (key, name) in new[] { (Key.Delete, "Delete"), (Key.Back, "Backspace"), (Key.Escape, "Escape") })
            {
                box.BeginRecording();
                box.ProcessRecordingKey(HotkeyRecorderBox.ToVirtualKey(key), HotkeyGesture.ModControl);
                Check($"modified {name} records with AllowUnbind={allowUnbind}",
                    !box.IsRecording && box.Gesture == $"Ctrl+{name}" && box.DisplayText == box.Gesture);
            }

            box.BeginRecording();
            box.ProcessRecordingKey(0x7B, HotkeyGesture.ModControl);
            Check($"reserved F12 keeps recording and the previous binding with AllowUnbind={allowUnbind}",
                box.IsRecording && box.Gesture == "Ctrl+Escape");
            box.ProcessRecordingKey(0x1B, 0);
            Check($"bare Escape cancels or unbinds with AllowUnbind={allowUnbind}",
                !box.IsRecording &&
                box.Gesture == (allowUnbind ? string.Empty : "Ctrl+Escape") &&
                box.DisplayText == (allowUnbind ? "未绑定" : "Ctrl+Escape") &&
                transitions.SequenceEqual(new[] { true, false, true, false, true, false, true, false }));
        }
    }

    /// <summary>
    /// SendInput 的 INPUT 布局守护（issue 019）：静态构造里的 x64 假设（40 字节、联合体偏移 8）
    /// 在本机必须成立；访问 Default 即触发校验，架构不符会在此报错。
    /// </summary>
    private static void TestSendInputLayoutGuard()
    {
        Check(
            "the x64 INPUT layout guard holds on this machine",
            SendInputCopySimulator.Default is not null);
    }

    /// <summary>
    /// 图像去向解析（issue 019 裁决）：管线偏好优先；vision_oneshot 遇不支持视觉的配置档明确停下；
    /// 未知管线值按 local_ocr 保守处理。
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
    /// 各自走向与剪贴板终态。
    /// </summary>
    private static void TestPasteInputFallbackChain()
    {
        OcrResult FakeOcr(SKBitmap bitmap) => new(
            [new OcrLine(new SKRectI(0, 0, 4, 4), "识别行一", 0.99f), new OcrLine(new SKRectI(0, 8, 4, 12), "识别行二", 0.98f)],
            TimeSpan.FromMilliseconds(120));

        // 1) 选区文本：Ctrl+C 后剪贴板出现新文本 → 直接采用，剪贴板不做任何还原。
        var selection = new FakeClipboardGateway { Current = ClipboardSnapshot.FromText("旧文本") };
        selection.CopyReplacesClipboardWith("选区原文");
        var selectionOutcome = PasteInputAcquirer.Acquire(Request(selection, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "a fresh selection wins over the clipboard and leaves the clipboard untouched",
            selectionOutcome.Kind == PasteInputKind.SelectionText &&
            selectionOutcome.Text == "选区原文" &&
            selection.RestoreCount == 0 &&
            selection.WrittenText is null);

        // 1b) 前台复制了与剪贴板里一模一样的文本：序列号一动就是真的复制过了，判为选区而非剪贴板文本。
        var identical = new FakeClipboardGateway { Current = ClipboardSnapshot.FromText("同一段文本") };
        identical.CopyReplacesClipboardWith("同一段文本");
        var identicalOutcome = PasteInputAcquirer.Acquire(Request(identical, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "a copy of identical text still counts as a fresh selection",
            identicalOutcome.Kind == PasteInputKind.SelectionText &&
            identicalOutcome.Text == "同一段文本" &&
            identical.RestoreCount == 0 &&
            identical.WrittenText is null);

        // 2) 剪贴板文本：Ctrl+C 无变化 → 还原快照后原样采用，读写幂等。
        var clipboardText = new FakeClipboardGateway { Current = ClipboardSnapshot.FromText("剪贴板原文") };
        var clipboardTextOutcome = PasteInputAcquirer.Acquire(Request(clipboardText, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "existing clipboard text is restored and reused without rewriting it",
            clipboardTextOutcome.Kind == PasteInputKind.ClipboardText &&
            clipboardTextOutcome.Text == "剪贴板原文" &&
            clipboardTextOutcome.Notice is null &&
            clipboardText.RestoreCount == 1 &&
            clipboardText.WrittenText is null &&
            clipboardText.Current?.Text == "剪贴板原文");

        // 3) 剪贴板图像 × 视觉能力：PNG 直喂，剪贴板不动。
        using var rawImg = new SKBitmap(8, 6, SKColorType.Bgra8888, SKAlphaType.Premul);
        var visionClipboard = new FakeClipboardGateway { Current = ClipboardSnapshot.FromImage(rawImg) };
        var visionOutcome = PasteInputAcquirer.Acquire(Request(visionClipboard, ClipboardImageRoute.DirectFeed, FakeOcr));
        Check(
            "a clipboard image feeds the provider directly when the profile supports vision",
            visionOutcome.Kind == PasteInputKind.ClipboardImage &&
            visionOutcome.ImagePng is { Length: > 0 } visionPng &&
            visionPng.Take(4).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }) &&
            visionOutcome.Text.Length == 0 &&
            visionOutcome.Notice is null &&
            visionClipboard.RestoreCount == 1 &&
            visionClipboard.WrittenText is null);

        // 3b) 选区图像 × 视觉直喂：前台响应 Ctrl+C 复制来的是图，PNG 直喂，剪贴板保持原样。
        var selectionVision = new FakeClipboardGateway { Current = ClipboardSnapshot.FromText("旧文本") };
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
            selectionVision.Current?.HasImage == true);

        // 3c) 选区图像 × local_ocr：图是程序自己 Ctrl+C 复制的，OCR 原文直接替换，剪贴板不留图。
        var selectionOcr = new FakeClipboardGateway { Current = ClipboardSnapshot.FromText("旧文本") };
        selectionOcr.CopyReplacesClipboardWithImage();
        var selectionOcrOutcome = PasteInputAcquirer.Acquire(Request(selectionOcr, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "a selection image is recognized locally and the text replaces the program's own copy",
            selectionOcrOutcome.Kind == PasteInputKind.SelectionImage &&
            selectionOcrOutcome.ImagePng is null &&
            selectionOcrOutcome.Text == "识别行一" + Environment.NewLine + "识别行二" &&
            selectionOcrOutcome.OcrLineCount == 2 &&
            selectionOcr.WrittenText == selectionOcrOutcome.Text &&
            selectionOcr.Current?.HasImage == false);

        // 3d) 选区图像但配置档不支持视觉：回报宿主弹提醒，OCR 绝不启动，剪贴板保持原样。
        var selectionUnsupported = new FakeClipboardGateway { Current = ClipboardSnapshot.FromText("旧文本") };
        selectionUnsupported.CopyReplacesClipboardWithImage();
        var selectionUnsupportedOutcome = PasteInputAcquirer.Acquire(Request(selectionUnsupported, ClipboardImageRoute.UnsupportedVision, _ =>
            throw new InvalidOperationException("能力不符时绝不应启动 OCR。")));
        Check(
            "a selection image with no vision support reports back for a user-facing reminder",
            selectionUnsupportedOutcome.Kind == PasteInputKind.VisionUnsupported &&
            selectionUnsupportedOutcome.ImagePng is null &&
            selectionUnsupported.RestoreCount == 0 &&
            selectionUnsupported.WrittenText is null &&
            selectionUnsupported.Current?.HasImage == true);

        // 4) 剪贴板图像 × 无视觉能力：本地 OCR 成文本；主人手动复制的图必须保住，原文以附加格式写回。
        var ocrClipboard = new FakeClipboardGateway { Current = ClipboardSnapshot.FromImage(rawImg) };
        var ocrOutcome = PasteInputAcquirer.Acquire(Request(ocrClipboard, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "a clipboard image is recognized locally and the text is appended, keeping the image",
            ocrOutcome.Kind == PasteInputKind.ClipboardImage &&
            ocrOutcome.ImagePng is null &&
            ocrOutcome.Text == "识别行一" + Environment.NewLine + "识别行二" &&
            ocrOutcome.OcrLineCount == 2 &&
            ocrOutcome.OcrElapsed == TimeSpan.FromMilliseconds(120) &&
            ocrClipboard.RestoreCount == 2 && // 初始还原 + OCR 原文附加写回
            ocrClipboard.WrittenText is null &&
            ocrClipboard.Current?.Text == ocrOutcome.Text &&
            ocrClipboard.Current?.HasImage == true);

        // 4b) OCR 之前透明区域已铺白：喂进去的必须是不透明位图。
        SKAlphaType? ocrAlphaType = null;
        SKColor? ocrCorner = null;
        var flattenClipboard = new FakeClipboardGateway { Current = ClipboardSnapshot.FromImage(rawImg) };
        _ = PasteInputAcquirer.Acquire(Request(flattenClipboard, ClipboardImageRoute.LocalOcr, bitmap =>
        {
            ocrAlphaType = bitmap.AlphaType;
            ocrCorner = bitmap.GetPixel(0, 0);
            return FakeOcr(bitmap);
        }));
        Check(
            "transparency is flattened to white before local ocr",
            ocrAlphaType == SKAlphaType.Opaque &&
            ocrCorner == SKColors.White,
            $"alpha={ocrAlphaType} pixel={ocrCorner}");

        // 4c) 剪贴板同时含文本与图像：文本优先，图像既不喂 OCR 也不写回剪贴板。
        var mixed = new FakeClipboardGateway
        {
            Current = new ClipboardSnapshot(
                [new ClipboardFormat("UnicodeText", System.Text.Encoding.Unicode.GetBytes("混合原文\0"), NativeMethods.CF_UNICODETEXT), new ClipboardFormat("Bitmap", [], NativeMethods.CF_BITMAP)],
                "混合原文",
                new SKBitmap(8, 6, SKColorType.Bgra8888, SKAlphaType.Premul))
        };
        var mixedOutcome = PasteInputAcquirer.Acquire(Request(mixed, ClipboardImageRoute.LocalOcr, _ =>
            throw new InvalidOperationException("文本优先时绝不应启动 OCR。")));
        Check(
            "clipboard text wins over a coexisting image without ever starting ocr",
            mixedOutcome.Kind == PasteInputKind.ClipboardText &&
            mixedOutcome.Text == "混合原文" &&
            mixedOutcome.ImagePng is null &&
            mixed.WrittenText is null);

        // 4d) vision_oneshot 但配置档不支持视觉：回报宿主弹提醒；剪贴板原样还原。
        var unsupported = new FakeClipboardGateway { Current = ClipboardSnapshot.FromImage(rawImg) };
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
        var racing = new FakeClipboardGateway { Current = ClipboardSnapshot.FromImage(rawImg) };
        var racingOutcome = PasteInputAcquirer.Acquire(Request(racing, ClipboardImageRoute.LocalOcr, _ =>
        {
            racing.CopyReplacesClipboardWith("用户在 OCR 期间复制的新内容");
            racing.ApplyCopy();
            return FakeOcr(new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        }));
        Check(
            "a clipboard change during ocr blocks the write-back and keeps the user's new content",
            racingOutcome.Kind == PasteInputKind.ClipboardImage &&
            racingOutcome.Text == "识别行一" + Environment.NewLine + "识别行二" &&
            racing.WrittenText is null &&
            racing.Current?.Text == "用户在 OCR 期间复制的新内容");

        // 5) 全空：只给托盘提示的结论，不写剪贴板。
        var empty = new FakeClipboardGateway { Current = null };
        var emptyOutcome = PasteInputAcquirer.Acquire(Request(empty, ClipboardImageRoute.DirectFeed, FakeOcr));
        Check(
            "nothing anywhere reports empty without touching the clipboard",
            emptyOutcome.Kind == PasteInputKind.Empty &&
            emptyOutcome.Notice is null &&
            empty.RestoreCount == 0 &&
            empty.WrittenText is null);

        // 6) Ctrl+C 写空串：超时后靠还原兜底。
        var cleared = new FakeClipboardGateway { Current = ClipboardSnapshot.FromText("用户原文") };
        cleared.CopyReplacesClipboardWith(string.Empty);
        var clearedOutcome = PasteInputAcquirer.Acquire(Request(cleared, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "an app that copies an empty string still recovers the user's clipboard text",
            clearedOutcome.Kind == PasteInputKind.ClipboardText &&
            clearedOutcome.Text == "用户原文" &&
            cleared.RestoreCount == 1);

        // 7) 还原失败：内容照取，面板状态栏拿到可展示的说明。
        var failing = new FailingRestoreClipboardGateway { Current = ClipboardSnapshot.FromText("剪贴板原文") };
        var failingOutcome = PasteInputAcquirer.Acquire(Request(failing, ClipboardImageRoute.LocalOcr, FakeOcr));
        Check(
            "a failed restore still yields content plus a status-bar notice",
            failingOutcome.Kind == PasteInputKind.ClipboardText &&
            failingOutcome.Text == "剪贴板原文" &&
            failingOutcome.Notice is { Length: > 0 } notice &&
            notice.Contains("还原失败", StringComparison.Ordinal));

        // 8) 前台回应了文件引用：原样保留，绝不还原冲掉。
        var fileDrop = new FakeClipboardGateway { Current = ClipboardSnapshot.FromText("用户原有文本") };
        fileDrop.CopyReplacesClipboardWith([@"D:\Code\projects\README.md"]);
        var fileDropOutcome = PasteInputAcquirer.Acquire(Request(fileDrop, ClipboardImageRoute.DirectFeed, FakeOcr));
        Check(
            "an unusable copy is left alone and reported instead of being restored away",
            fileDropOutcome.Kind == PasteInputKind.Empty &&
            fileDropOutcome.Notice is { Length: > 0 } fileNotice &&
            fileNotice.Contains("无法翻译", StringComparison.Ordinal) &&
            fileDrop.RestoreCount == 0 &&
            fileDrop.WrittenText is null &&
            fileDrop.Current?.FormatNames.Contains("FileDrop") == true);
    }

    /// <summary>真实剪贴板快照→还原 round-trip；无剪贴板访问权时宽容跳过。</summary>
    private static void TestClipboardSnapshotRoundTrip()
    {
        var gateway = new Win32ClipboardGateway();
        ClipboardSnapshot? original = null;
        try
        {
            original = gateway.Snapshot();

            using var image = new SKBitmap(8, 6, SKColorType.Bgra8888, SKAlphaType.Premul);
            var customBytes = System.Text.Encoding.UTF8.GetBytes("自定义格式数据");
            var testFormats = new List<ClipboardFormat>
            {
                new("UnicodeText", System.Text.Encoding.Unicode.GetBytes("剪贴板原文\0"), NativeMethods.CF_UNICODETEXT),
                new("Bitmap", [], NativeMethods.CF_BITMAP),
                new("NaniKore.SelfTest", customBytes)
            };
            var testSnapshot = new ClipboardSnapshot(testFormats, "剪贴板原文", image);

            gateway.Restore(testSnapshot);

            var snapshot = gateway.Snapshot();
            Check(
                "real clipboard snapshot exposes text, image and custom formats",
                snapshot is not null &&
                snapshot.Text == "剪贴板原文" &&
                snapshot.DetachImage() is not null &&
                snapshot.FormatNames.Contains("NaniKore.SelfTest"),
                $"formats=[{string.Join(", ", snapshot?.FormatNames ?? [])}]");

            // 清空剪贴板后再还原
            if (NativeMethods.OpenClipboard(0))
            {
                NativeMethods.EmptyClipboard();
                NativeMethods.CloseClipboard();
            }

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
        catch (ExternalException)
        {
            Skip("真实剪贴板不可访问（被其他进程持续占用或会话无剪贴板权限），round-trip 用例未执行");
        }
        finally
        {
            if (original is not null)
            {
                try { gateway.Restore(original); } catch { }
            }
        }
    }

    /// <summary>
    /// 外来图像剪贴板的 round-trip：验证通过 Win32 API 写入的标准位图能被正确识别与还原。
    /// </summary>
    private static void TestForeignImageClipboardRoundTrip()
    {
        var gateway = new Win32ClipboardGateway();
        ClipboardSnapshot? original = null;
        try
        {
            original = gateway.Snapshot();

            var screenDc = NativeMethods.GetDC(0);
            var hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, 8, 6);
            NativeMethods.ReleaseDC(0, screenDc);
            var text = "外来剪贴板文本\0";
            var chars = text.ToCharArray();
            var hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)(chars.Length * sizeof(char)));

            if (NativeMethods.OpenClipboard(0))
            {
                try
                {
                    NativeMethods.EmptyClipboard();
                    NativeMethods.SetClipboardData(NativeMethods.CF_BITMAP, hBitmap);
                    if (hMem != 0)
                    {
                        var ptr = NativeMethods.GlobalLock(hMem);
                        if (ptr != 0)
                        {
                            Marshal.Copy(chars, 0, ptr, chars.Length);
                            NativeMethods.GlobalUnlock(hMem);
                            NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem);
                        }
                    }
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            var snapshot = gateway.Snapshot();
            Check(
                "foreign image clipboard snapshot keeps the image",
                snapshot is not null && snapshot.HasImage && snapshot.Text == "外来剪贴板文本",
                $"formats=[{string.Join(", ", snapshot?.FormatNames ?? [])}]");

            // 清空并还原
            if (NativeMethods.OpenClipboard(0))
            {
                NativeMethods.EmptyClipboard();
                NativeMethods.CloseClipboard();
            }

            gateway.Restore(snapshot!);
            var restored = gateway.Snapshot();
            Check(
                "restored foreign image keeps CF_BITMAP other apps can paste",
                restored is not null &&
                restored.HasImage &&
                restored.Text == "外来剪贴板文本",
                $"formats=[{string.Join(", ", restored?.FormatNames ?? [])}]");
        }
        catch (ExternalException)
        {
            Skip("真实剪贴板不可访问（被其他进程持续占用），外来图像 round-trip 用例未执行");
        }
        finally
        {
            if (original is not null)
            {
                try { gateway.Restore(original); } catch { }
            }
        }
    }

    /// <summary>测试 DesktopCapturer 与 SelectionGeometry 的纯几何计算。</summary>
    private static void TestDesktopCapturerAndGeometry()
    {
        var bounds = DesktopCapturer.GetVirtualScreenBounds();
        Check("DesktopCapturer bounds has positive dimensions", bounds.Width > 0 && bounds.Height > 0);

        using var frame = DesktopCapturer.CaptureVirtualScreen(allowHeadlessFallback: true);
        Check("DesktopCapturer produces valid SKBitmap matching bounds", frame.Image.Width == bounds.Width && frame.Image.Height == bounds.Height);

        var norm = SelectionGeometry.Normalize(new PixelPoint(90, 70), new PixelPoint(10, 20), new PixelSize(100, 80));
        Check("SelectionGeometry normalizes reverse drag to PixelRect", norm == new PixelRect(10, 20, 80, 50));

        var outside = SelectionGeometry.GetOutsideRegions(new PixelRect(0, 0, 100, 80), new PixelRect(10, 20, 30, 25));
        var totalOutsideArea = outside.Sum(static r => r.Width * r.Height);
        Check("SelectionGeometry outside dim regions preserve area", totalOutsideArea == (100 * 80 - 30 * 25));
    }

    /// <summary>验证重复 ID 保留原注册与回调，以及注册/注销和并发关闭竞争能有界完成。</summary>
    private static void TestWin32HotkeyHostLifecycle()
    {
        // 连构造和清理也放入有界等待，生命周期回归不能卡住整套自测。
        static void Complete(Action action) =>
            Task.Run(action).WaitAsync(HotkeyTestTimeout).GetAwaiter().GetResult();

        var host = Task.Run(() => new Win32HotkeyHost())
            .WaitAsync(HotkeyTestTimeout).GetAwaiter().GetResult();
        try
        {
            Check("Win32HotkeyHost creates valid window handle", host.Handle != 0);
            const int testId = 8888;
            const uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
            const uint originalKey = 0x86; // F23：避开 Windows 为调试器保留的 F12。
            const uint replacementKey = 0x87; // F24
            var callback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Complete(() => host.RegisterHotKey(testId, modifiers, originalKey, () => callback.TrySetResult(true)));

            var duplicateRejected = false;
            Complete(() =>
            {
                try
                {
                    host.RegisterHotKey(testId, modifiers, replacementKey, () => callback.TrySetResult(false));
                }
                catch (InvalidOperationException)
                {
                    duplicateRejected = true;
                }
            });
            Check("duplicate hotkey id is rejected before replacing the registration", duplicateRejected);

            var originalStillRegistered = false;
            Complete(() =>
            {
                try
                {
                    host.RegisterHotKey(testId + 1, modifiers, originalKey, () => { });
                }
                catch (Win32Exception exception) when (exception.NativeErrorCode == 1409)
                {
                    originalStillRegistered = true;
                }
            });
            Check("duplicate id leaves the original native gesture registered", originalStillRegistered);

            // 直接投递消息验证保留的是原回调，不发送真实键盘输入干扰桌面。
            Check("original hotkey callback message is posted",
                NativeMethods.PostMessageW(host.Handle, NativeMethods.WM_HOTKEY, testId, 0));
            Check("duplicate id leaves the original callback intact",
                callback.Task.WaitAsync(HotkeyTestTimeout).GetAwaiter().GetResult());

            Complete(() => host.RegisterHotKey(testId + 2, modifiers, replacementKey, () => { }));
            bool unregistered = false;
            Complete(() => unregistered = host.TryUnregisterHotKey(testId));
            Check("Win32HotkeyHost unregisters the original hotkey successfully", unregistered);
            Complete(() => host.TryUnregisterHotKey(testId + 2));

            // 真实调用 RegisterHotKey 覆盖扩展键族，避免只验证文本与枚举之间的自洽。
            foreach (var gesture in new[] { "Ctrl+Alt+Shift+OemPlus", "Ctrl+Alt+Shift+Left", "Ctrl+Alt+Shift+NumPad7", "Ctrl+Alt+Shift+Delete", "Ctrl+Alt+Shift+MediaPlayPause" })
            {
                var parsed = HotkeyGesture.Parse(gesture);
                Complete(() => host.RegisterHotKey(testId + 4, parsed.Modifiers, parsed.Key, () => { }));
                Complete(() => unregistered = host.TryUnregisterHotKey(testId + 4));
                Check($"expanded gesture {gesture} registers and unregisters with Win32", unregistered);
            }

            // 同时释放请求与两个关闭者，覆盖已入队请求排空和停止后拒绝请求两条路径。
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operations = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
            {
                await start.Task;
                _ = host.TryUnregisterHotKey(testId + 100 + index);
            })).ToList();
            operations.Add(Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    host.RegisterHotKey(testId + 3, modifiers, replacementKey, () => { });
                }
                catch (ObjectDisposedException)
                {
                    // 关闭先取得停止边界时，这是注册请求唯一允许的失败。
                }
            }));
            for (var index = 0; index < 2; index++)
            {
                operations.Add(Task.Run(async () =>
                {
                    await start.Task;
                    host.Dispose();
                }));
            }
            start.SetResult(true);
            Task.WhenAll(operations).WaitAsync(HotkeyTestTimeout).GetAwaiter().GetResult();
            Check("registration, unregistration and concurrent disposal finish without hanging", host.Handle == 0);

            var registrationRejected = false;
            Complete(() =>
            {
                try
                {
                    host.RegisterHotKey(testId, modifiers, originalKey, () => { });
                }
                catch (ObjectDisposedException)
                {
                    registrationRejected = true;
                }
            });
            Check("disposed host rejects registration", registrationRejected);
            Complete(() => unregistered = host.TryUnregisterHotKey(testId));
            Check("disposed host reports unregistration failure", !unregistered);
        }
        finally
        {
            Complete(host.Dispose);
        }
    }

    /// <summary>测试应用图标与 Avalonia 组件实例化。</summary>
    private static void TestApplicationIconAndComponents()
    {
        EnsureThemedApplication();

        var icon = ApplicationIcon.Load();
        Check("ApplicationIcon loads Avalonia WindowIcon successfully", icon is not null);

        var dialog = new MessageDialog();
        Check("MessageDialog creates successfully with theme icon", dialog.Icon is not null);
    }



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

    private static PasteInputRequest Request(
        FakeClipboardGateway clipboard,
        ClipboardImageRoute imageRoute,
        Func<SKBitmap, OcrResult> recognize) =>
        new(imageRoute, recognize, clipboard, clipboard.ApplyCopy, _ => false);

    /// <summary>内存剪贴板：记录还原与写回，供断言剪贴板终态。</summary>
    private class FakeClipboardGateway : IClipboardGateway
    {
        public ClipboardSnapshot? Current { get; set; }
        public ClipboardSnapshot? CopyResult { get; private set; }
        public int RestoreCount { get; private set; }
        public string? WrittenText { get; private set; }
        public uint SequenceNumber { get; private set; }

        public void CopyReplacesClipboardWith(string text)
        {
            CopyResult = ClipboardSnapshot.FromText(text);
        }

        public void CopyReplacesClipboardWith(string[] files)
        {
            CopyResult = ClipboardSnapshot.FromFiles(files);
        }

        public void CopyReplacesClipboardWithImage()
        {
            using var img = new SKBitmap(8, 6, SKColorType.Bgra8888, SKAlphaType.Premul);
            CopyResult = ClipboardSnapshot.FromImage(img);
        }

        public void ApplyCopy()
        {
            Current = CopyResult;
            SequenceNumber++;
        }

        public ClipboardSnapshot? Snapshot() => Current;

        public virtual void Restore(ClipboardSnapshot snapshot)
        {
            RestoreCount++;
            Current = snapshot;
            SequenceNumber++;
        }

        public void SetText(string text)
        {
            WrittenText = text;
            Current = ClipboardSnapshot.FromText(text);
            SequenceNumber++;
        }

        public uint ReadSequenceNumber() => SequenceNumber;
    }

    private sealed class FailingRestoreClipboardGateway : FakeClipboardGateway
    {
        public override void Restore(ClipboardSnapshot snapshot) =>
            throw new ExternalException("剪贴板正被其他程序占用。");
    }
}
