using YamlDotNet.Serialization;

namespace NaniKore.Config;

/// <summary>
/// YamlDotNet 静态上下文（裁决 2 + epic 002 穿刺 2 结论，issue 002）：编译期为 config.yaml 的
/// 类型图生成无反射的工厂/类型检查器/属性访问器，NativeAOT 下不再触发 IL3050。
/// <para>
/// 用法要点（穿刺 2 实测情报）：<see cref="YamlStaticContextAttribute"/> 标记本类是上下文；
/// 每条 <see cref="YamlSerializableAttribute"/> 登记一个类型——**生成器不沿类型图递归发现**，
/// 新增配置段或 DTO 必须在此显式登记，漏登记会产生 YDNG001 构建警告（本工程
/// <c>TreatWarningsAsErrors</c> 下直接构建失败，不会静默漏掉）。
/// </para>
/// <para>
/// 必须是 public partial：生成器补全的另一个部分是 public，声明成 internal 会报 CS0262。
/// 类内引用的配置类型仍是 internal，不因本类 public 而泄漏到程序集外。
/// </para>
/// </summary>
[YamlStaticContext]
[YamlSerializable(typeof(AppConfigDocument))]
[YamlSerializable(typeof(GeneralConfig))]
[YamlSerializable(typeof(PipelineConfig))]
[YamlSerializable(typeof(ProviderProfileDocument))]
[YamlSerializable(typeof(HotkeyConfig))]
[YamlSerializable(typeof(WindowConfig))]
[YamlSerializable(typeof(PromptConfig))]
[YamlSerializable(typeof(PhoneticsConfig))]
public partial class ConfigYamlStaticContext : StaticContext
{
}
