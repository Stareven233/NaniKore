using Avalonia;
using Avalonia.Media;
using NaniKore.Config;

namespace NaniKore.App.Theme;

/// <summary>
/// 字体族资源的唯一运行期写入口（issue 033）：把 config.yaml 的 <c>fonts</c> 段写进
/// <see cref="Application.Resources"/>，已存在的控件经 <c>{DynamicResource}</c> 自动重绘。
/// 代码内不持有任何字体族常量——空串即不写资源键，控件落到 Avalonia 平台默认字体。
/// </summary>
internal static class FontResources
{
    public const string UiKey = "Font.Ui";
    public const string PromptKey = "Font.Prompt";

    private static IReadOnlyList<string>? _installedFamilies;
    private static HashSet<string>? _installedLookup;
    private static Task? _warmUp;

    /// <summary>本机字体表是否已枚举完成（未完成时设置页跳过同步告警，等预热完成再刷新）。</summary>
    public static bool IsInstalledFamiliesLoaded => _installedFamilies is not null;

    /// <summary>
    /// 后台预热字体表快照（设置页打开时调用）：不阻塞首帧，重复调用共享同一个任务。
    /// 枚举本身只读平台字体表，与渲染线程无共享可变态。
    /// </summary>
    public static Task WarmUpAsync() => _warmUp ??= Task.Run(() => _ = InstalledFamilies);

    /// <summary>
    /// 本机已安装字体族名快照：去重 + 大小写不敏感排序，首次访问枚举一次后缓存。
    /// 只作为设置页候选源与警告探针，不参与渲染决策。
    /// </summary>
    public static IReadOnlyList<string> InstalledFamilies => _installedFamilies ??= LoadInstalledFamilies();

    /// <summary>从已注入的 <see cref="UiKey"/> 解析字符型 Typeface；资源缺失时退回平台默认。</summary>
    public static Typeface UiTypeface => TryGetApplied(UiKey) is { } family ? new Typeface(family) : Typeface.Default;

    /// <summary>把配置里的两个字体族写进应用资源；空串/非法值删除对应键（= 平台默认）。</summary>
    public static void Apply(FontConfig fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        ApplyKey(UiKey, fonts.Ui);
        ApplyKey(PromptKey, fonts.Prompt);
    }

    /// <summary>
    /// 判断字体族是否落在本机字体表。空串视为「未设置」= 合法（不告警）；
    /// 逗号回退链只要有一环命中就算可用，与实际渲染的命中语义一致。
    /// </summary>
    public static bool IsInstalled(string? familyList)
    {
        if (string.IsNullOrWhiteSpace(familyList))
        {
            return true;
        }

        var lookup = _installedLookup ??= new HashSet<string>(InstalledFamilies, StringComparer.OrdinalIgnoreCase);
        return familyList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(lookup.Contains);
    }

    /// <summary>
    /// 尝试把配置字符串构造为 FontFamily：空串、非法语法一律返回 null，
    /// 由调用方回落到平台默认，绝不把用户输入变成异常。
    /// </summary>
    public static FontFamily? TryCreate(string? familyList)
    {
        if (string.IsNullOrWhiteSpace(familyList))
        {
            return null;
        }

        try
        {
            // 逗号分隔的回退链由 FontFamily 自身的 FamilyNames 解析承载，不在这里拆。
            return new FontFamily(familyList.Trim());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ApplyKey(string key, string? familyList)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        if (TryCreate(familyList) is { } family)
        {
            app.Resources[key] = family;
        }
        else if (app.Resources.ContainsKey(key))
        {
            // 从有值改为空串时必须删除旧键，否则上一次注入的字体继续生效。
            app.Resources.Remove(key);
        }
    }

    private static FontFamily? TryGetApplied(string key) =>
        Application.Current is { } app && app.Resources.TryGetValue(key, out var value) && value is FontFamily family
            ? family
            : null;

    private static IReadOnlyList<string> LoadInstalledFamilies() =>
        FontManager.Current.SystemFonts
            .Select(family => family.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
