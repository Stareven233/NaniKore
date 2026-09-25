namespace NaniKore.Diagnostics;

/// <summary>
/// 无 UI 自动化自测试主调度入口。
/// 具体领域的自测已按模块拆分为 partial 类：
/// - <see cref="SelfTest"/> (Ocr): 选区几何、张量转换、DBNet 后处理、CTC 解码、行排序与文本规整
/// - <see cref="SelfTest"/> (Config): YAML 配置加载回写、版本维护、内置资产与 DPAPI 密钥管理
/// - <see cref="SelfTest"/> (Translation): 提示词模板、多语言字集探测、SSE 读取、分栏路由与 Providers
/// - <see cref="SelfTest"/> (Explanation): 精讲卡片解析、流式降级与收藏契约字符串匹配
/// - <see cref="SelfTest"/> (Favorites): SQLite 收藏服务惰性建库、增删查、upsert 与跨实例持久化
/// - <see cref="SelfTest"/> (Ui): 热键手势解析与录制、主题色板字典、自适应卡片布局与设置页绑定
/// </summary>
internal static partial class SelfTest
{
    private static int _failed;

    /// <summary>
    /// 执行全量无 UI 自测，返回退出码（0 表示全绿通过，非 0 表示有失败项）。
    /// </summary>
    public static int Run()
    {
        _failed = 0;

        RunOcrTests();
        RunConfigTests();
        RunTranslationTests();
        RunExplanationTests();
        RunFavoritesTests();
        RunUiTests();

        Console.WriteLine(_failed == 0
            ? "All self-tests passed."
            : $"Self-tests failed: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 自测断言器：打印单项测试状态并在失败时递增计数。
    /// </summary>
    private static void Check(string name, bool condition)
    {
        if (condition)
        {
            Console.WriteLine($"PASS: {name}");
            return;
        }

        _failed++;
        Console.Error.WriteLine($"FAIL: {name}");
    }
}
