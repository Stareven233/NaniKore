using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.Config;
using NaniKore.Input;

namespace NaniKore.Diagnostics;

/// <summary>
/// 无 UI 自测（UI 套件）的总调度与共享 WPF 无头测试基座。
/// 按关注点拆分为四个 partial 文件：
/// - <see cref="SelfTest"/> (Ui): 调度、WPF 无头测试辅助工具、热键录制盒交互
/// - <see cref="SelfTest"/> (Ui.Settings): 主题资源、自适应布局契约、各设置页绑定与尺寸记忆
/// - <see cref="SelfTest"/> (Ui.ResultPanel): 结果面板的注音、双语态、翻译流、精讲与划词气泡
/// - <see cref="SelfTest"/> (Input): 热键手势解析、F1–F12 单键与选取/剪贴板输入获取状态机
/// </summary>
/// <remarks>
/// WPF 与 WinForms 类型名撞车，本项目统一经 GlobalUsings.cs 的 Wpf* / WinForms 别名引用，
/// 不再出现全限定名（见 <c>GlobalUsings.cs</c> 顶部说明）。
/// </remarks>
internal static partial class SelfTest
{
    private static void RunUiTests()
    {
        // 热键录制盒（手势解析与 F1–F12 单键规则在 SelfTest.Input）
        TestHotkeyRecorderInteraction();
        TestPasteRecorderUnbind();

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
        page.Load(new AppConfig { Hotkeys = new HotkeyConfig { Capture = "alt+f9", Paste = "f1" } });
        var config = new AppConfig();
        page.Apply(config);
        Check(
            "hotkey page normalizes stored gestures and validates before applying",
            page.Validate() is null && config.Hotkeys.Capture == "Alt+F9" && config.Hotkeys.Paste == "F1");
    }

    /// <summary>选取翻译录制框（issue 018）：Esc 解除绑定落空串，空手势显示占位提示。</summary>
    private static void TestPasteRecorderUnbind()
    {
        var unbind = new HotkeyRecorderBox { Gesture = "Ctrl+Shift+S", AllowUnbind = true, EmptyHint = "未绑定（Esc 解绑）" };
        Check(
            "a bound recorder echoes the configured gesture before recording",
            !unbind.IsRecording && unbind.DisplayText == "Ctrl+Shift+S");

        unbind.BeginRecording();
        unbind.ProcessRecordingKey(WinForms.Keys.Escape, 0);
        Check(
            "escape unbinds the paste gesture instead of restoring the previous one",
            !unbind.IsRecording && unbind.Gesture.Length == 0 && unbind.DisplayText == "未绑定（Esc 解绑）");

        // 解绑后仍可重新录制：空手势不锁死录制框。
        unbind.BeginRecording();
        unbind.ProcessRecordingKey(WinForms.Keys.F5, 0);
        Check(
            "an unbound paste recorder accepts a new single function key",
            !unbind.IsRecording && unbind.Gesture == "F5" && unbind.DisplayText == "F5");

        // 截图录制框保持原语义：Esc 只是取消并还原。
        var keep = new HotkeyRecorderBox { Gesture = "Ctrl+Shift+A" };
        keep.BeginRecording();
        keep.ProcessRecordingKey(WinForms.Keys.Escape, 0);
        Check(
            "the capture recorder still cancels and restores on escape",
            !keep.IsRecording && keep.Gesture == "Ctrl+Shift+A");
    }
}
