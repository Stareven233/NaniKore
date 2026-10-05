using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.Config;
using NaniKore.Input;

namespace NaniKore.Diagnostics;

/// <summary>
/// 无 UI 自测（UI 套件）的总调度与共享 WPF 无头测试基座，外加热键手势解析/录制自测。
/// 按关注点拆分为三个 partial 文件：
/// - <see cref="SelfTest"/> (Ui): 调度、WPF 无头测试辅助工具、热键手势解析与录制
/// - <see cref="SelfTest"/> (Ui.Settings): 主题资源、自适应布局契约、各设置页绑定与尺寸记忆
/// - <see cref="SelfTest"/> (Ui.ResultPanel): 结果面板的注音、双语态、翻译流、精讲与划词气泡
/// </summary>
/// <remarks>
/// WPF 与 WinForms 类型名撞车，本项目统一经 GlobalUsings.cs 的 Wpf* / WinForms 别名引用，
/// 不再出现全限定名（见 <c>GlobalUsings.cs</c> 顶部说明）。
/// </remarks>
internal static partial class SelfTest
{
    private static void RunUiTests()
    {
        // 热键手势与录制
        TestHotkeyGestureNormalization();
        TestHotkeyRecorderInteraction();

        // 设置页：主题资源、布局契约、页面绑定、尺寸记忆、收藏管理页、窗口层级
        TestThemeResourcesResolve();
        TestSettingsPagesUseAdaptiveLayout();
        TestGeneralPagePipelineDependencyVisibility();
        TestGeneralPagePhoneticsCards();
        TestSharedLanguageChoicesAndDetectionMapping();
        TestTranslationPagePromptEditing();
        TestSettingsCloseSizeMemory();
        TestSettingsCloseSizeWriteBackKeepsExternalUpdates();
        TestFavoritesSettingsPageFilteringAndEvents();
        TestWindowProperties();

        // 结果面板：本地注音、双语态与翻译流、指标条、精讲生命周期、划词气泡、振假名文档
        TestResultPanelSourceAnnotation();
        TestResultPanelRubyDualModeAndTranslationFlow();
        TestResultPanelMetricsBar();
        TestExplanationExpanderLifecycle();
        TestExplanationCardViewFavorites();
        TestSelectionBubbleInteractions();
        TestRubyDocumentBuilderAndSelectionCopy();
    }

    /// <summary>
    /// 无 UI 自测里也需要一个 WPF Application 承载资源；不调用 Run，只做资源解析。
    /// </summary>
    private static void EnsureThemedApplication()
    {
        if (Wpf.Application.Current is null)
        {
            _ = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        }

        NaniKore.App.Theme.ThemeManager.Initialize(Wpf.Application.Current!);
    }

    /// <summary>逻辑树遍历：页面尚未渲染时视觉树为空，断言必须走逻辑子元素。</summary>
    private static IEnumerable<T> FindDescendants<T>(Wpf.DependencyObject root)
        where T : Wpf.DependencyObject
    {
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(root).OfType<Wpf.DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>泛型化的 x:Name 查找：设置页/窗口均在各自名称作用域注册，免去逐处强转。</summary>
    private static T? Find<T>(Wpf.FrameworkElement root, string name)
        where T : Wpf.FrameworkElement =>
        root.FindName(name) as T;

    /// <summary>以路由事件方式唤起点击，比反射触发更贴近真实输入路径。</summary>
    private static void RaiseClick(WpfPrimitives.ButtonBase button) =>
        button.RaiseEvent(new Wpf.RoutedEventArgs(WpfPrimitives.ButtonBase.ClickEvent));

    /// <summary>构造左键路由事件参数（自测不依赖真实时间戳，统一取 0）。</summary>
    private static WpfInput.MouseButtonEventArgs LeftButton(Wpf.RoutedEvent routedEvent, Wpf.IInputElement source) =>
        new(WpfInput.Mouse.PrimaryDevice, 0, WpfInput.MouseButton.Left) { RoutedEvent = routedEvent, Source = source };

    /// <summary>热键手势规范化：修饰键别名与大小写归一，拒绝空串 / 纯修饰键 / 不支持的按键。</summary>
    private static void TestHotkeyGestureNormalization()
    {
        Check(
            "gesture parsing normalizes modifier aliases and casing",
            HotkeyGesture.Normalize("control+shift+q") == "Ctrl+Shift+Q" &&
            HotkeyGesture.Normalize("windows + alt + f9") == "Alt+Win+F9");

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
    }

    /// <summary>录制盒交互：按住修饰键回显半组合、Backspace 保持状态、Esc 还原录制前手势。</summary>
    private static void TestHotkeyRecorderInteraction()
    {
        var recorder = new HotkeyRecorderBox { Gesture = "Ctrl+Shift+A" };
        var suspensions = new List<bool>();
        recorder.RecordingChanged += (_, recording) => suspensions.Add(recording);

        recorder.BeginRecording();
        recorder.ProcessRecordingKey(WinForms.Keys.ShiftKey, HotkeyGesture.ModControl | HotkeyGesture.ModShift);
        Check(
            "holding modifiers echoes the partial combination without ending recording",
            recorder.IsRecording && recorder.DisplayText == "Ctrl+Shift+…" && recorder.Gesture == "Ctrl+Shift+A");

        recorder.ProcessRecordingKey(WinForms.Keys.Back, HotkeyGesture.ModControl);
        Check(
            "recording keeps state for backspace input",
            recorder.IsRecording && recorder.Gesture == "Ctrl+Shift+A");

        recorder.ProcessRecordingKey(WinForms.Keys.Q, HotkeyGesture.ModControl | HotkeyGesture.ModShift);
        Check(
            "recording stores the normalized combination and releases the global hotkey once",
            !recorder.IsRecording &&
            recorder.Gesture == "Ctrl+Shift+Q" &&
            recorder.DisplayText == "Ctrl+Shift+Q" &&
            suspensions.SequenceEqual([true, false]));

        recorder.BeginRecording();
        recorder.ProcessRecordingKey(WinForms.Keys.Escape, 0);
        Check(
            "escape restores the gesture that was active before recording",
            !recorder.IsRecording && recorder.Gesture == "Ctrl+Shift+Q");

        var page = new HotkeyPage();
        page.Load(new AppConfig { Hotkeys = new HotkeyConfig { Capture = "alt+f9" } });
        var config = new AppConfig();
        page.Apply(config);
        Check(
            "hotkey page normalizes stored gestures and validates before applying",
            page.Validate() is null && config.Hotkeys.Capture == "Alt+F9");
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
}
