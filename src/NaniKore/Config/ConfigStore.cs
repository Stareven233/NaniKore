using System.ComponentModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NaniKore.Config;

/// <summary>
/// 配置加载、默认值生成、原子写盘。
/// 只在用户点保存时写盘，不做实时同步。
/// </summary>
internal sealed class ConfigStore
{
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;

    /// <summary>当前生效的配置，启动时从文件加载或由默认值初始化。</summary>
    public AppConfig Current { get; private set; }

    public ConfigStore(string configDir)
    {
        _configPath = Path.Combine(configDir,  config.yaml);

        _deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()  // 未知字段回落默认值
            .WithNamingConvention(NamingConvention.LowerCaseNamingConvention)
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(NamingConvention.LowerCaseNamingConvention)
            .WithEnumNamingConvention(NamingConvention.LowerCaseNamingConvention)
            .Build();

        Current = Load();
    }

    private AppConfig Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                var defaults = new AppConfig();
                AtomicWrite(defaults);
                return defaults;
            }

            var yaml = File.ReadAllText(_configPath);
            var config = _deserializer.Deserialize<AppConfig>(yaml);

            return config ?? new AppConfig();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($Failed to load config, using defaults: {exception.Message});
            return new AppConfig();
        }
    }

    /// <summary>
    /// 原子写盘：先写入临时文件再重命名，避免写半截导致 yaml 损坏。
    /// </summary>
    public void AtomicWrite(AppConfig config)
    {
        var yaml = _serializer.Serialize(config);
        var tempPath = _configPath + .tmp;
        File.WriteAllText(tempPath, yaml);
        File.Move(tempPath, _configPath, overwrite: true);
        Current = config;
    }

    /// <summary>
    /// 获取当前激活的配置档，不存在时回落第一个。
    /// </summary>
    public ProviderProfile GetActiveProfile()
    {
        var profile = Current.Translation.Profiles
            .FirstOrDefault(p => p.Id.Equals(Current.Translation.ActiveProfile, StringComparison.OrdinalIgnoreCase));

        return profile ?? Current.Translation.Profiles.FirstOrDefault() ?? new ProviderProfile();
    }
}