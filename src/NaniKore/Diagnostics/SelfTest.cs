namespace NaniKore.Diagnostics;

/// <summary>
/// 无 UI 自动化自测试主调度入口。
/// 具体领域的自测已按模块拆分为 partial 类：
/// - <see cref="SelfTest"/> (Ocr): 选区几何、张量转换、DBNet 后处理、CTC 解码、行排序与文本规整
/// - <see cref="SelfTest"/> (Config): YAML 配置加载回写、版本维护、内置资产与 DPAPI 密钥管理
/// - <see cref="SelfTest"/> (Translation): 提示词模板、多语言字集探测、SSE 读取、分栏路由与 Providers
/// - <see cref="SelfTest"/> (Explanation): 精讲卡片解析、流式降级与收藏契约字符串匹配
/// - <see cref="SelfTest"/> (Favorites): SQLite 收藏服务惰性建库、增删查、upsert 与跨实例持久化
/// - <see cref="SelfTest"/> (Ui): 无头 WPF 测试辅助工具、热键录制盒交互、设置页与结果面板自测调度
/// - <see cref="SelfTest"/> (Ui.Settings): 主题资源、自适应卡片布局、各设置页绑定契约、尺寸记忆回写、收藏管理页、窗口层级
/// - <see cref="SelfTest"/> (Ui.ResultPanel): 本地注音、振假名/纯文本双语态与翻译流、指标条、精讲生命周期、划词气泡与振假名文档
/// - <see cref="SelfTest"/> (Input): 热键手势解析、扩展按键与 F1–F11 单键规则、选取/剪贴板输入获取状态机与真实剪贴板 round-trip
/// </summary>
internal static partial class SelfTest
{
    private static int _passed;
    private static int _failed;
    private static bool _verbose;
    private static readonly List<string> Failures = [];

    /// <summary>
    /// 执行全量无 UI 自测，返回退出码（0 表示全绿通过，非 0 表示有失败项）。
    /// </summary>
    public static int Run(bool verbose = false)
    {
        _verbose = verbose;
        _passed = 0;
        _failed = 0;
        Failures.Clear();

        RunIdleResourceTests();
        RunOcrTests();
        RunConfigTests();
        RunInputTests();
        RunTranslationTests();
        RunPhoneticsTests();
        RunExplanationTests();
        RunFavoritesTests();
        RunUiTests();

        var total = _passed + _failed;
        Console.WriteLine(_failed == 0
            ? $"All self-tests passed ({_passed} checks)."
            : $"Self-tests failed: {_failed}, passed: {_passed} (total: {total}).");
        if (_failed > 0)
        {
            // 失败明细写 UTF-8 文件：控制台在 Windows 默认代码页下中文/IPA 会花屏
            try
            {
                Directory.CreateDirectory("artifacts");
                File.WriteAllLines(Path.Combine("artifacts", "self-test-failures.log"), Failures, new System.Text.UTF8Encoding(false));
            }
            catch (IOException)
            {
            }
        }

        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 自测断言器：打印单项测试状态并在失败时递增计数。
    /// </summary>
    private static void Check(string name, bool condition) => Check(name, condition, string.Empty);

    /// <summary>带失败明细的自测断言器：黄金样例不一致时打印期望与实际，便于直接定位。</summary>
    private static void Check(string name, bool condition, string detail)
    {
        if (condition)
        {
            _passed++;
            if (_verbose)
            {
                Console.WriteLine($"PASS: {name}");
            }
            return;
        }

        _failed++;
        Failures.Add($"FAIL: {name}{(detail.Length == 0 ? string.Empty : $" [{detail}]")}");
        Console.WriteLine($"FAIL: {name}{(detail.Length == 0 ? string.Empty : $" [{detail}]")}");
    }
}
