using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace NaniKore.Config;

/// <summary>集中管理磁盘配置的 schema 版本和逐版迁移。</summary>
internal static class ConfigMigrations
{
    public const int CurrentSchemaVersion = 1;

    public static string Migrate(string yaml, out bool changed, out int sourceVersion)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new ConfigMigrationException("config.yaml 根节点必须是一个 YAML 对象");
        }

        var version = ReadSchemaVersion(root);
        sourceVersion = version;
        if (version > CurrentSchemaVersion)
        {
            throw new ConfigMigrationException(
                $"config.yaml 使用了较新的 schema_version {version}，当前程序最多支持 {CurrentSchemaVersion}");
        }

        changed = false;
        while (version < CurrentSchemaVersion)
        {
            version = version switch
            {
                0 => Migrate0To1(root),
                _ => throw new ConfigMigrationException($"没有 schema_version {version} 的迁移步骤"),
            };
            changed = true;
        }

        if (!changed)
        {
            return yaml;
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static int ReadSchemaVersion(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("schema_version"), out var node))
        {
            return 0;
        }

        if (node is not YamlScalarNode scalar ||
            !int.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version < 0)
        {
            throw new ConfigMigrationException("config.yaml 的 schema_version 必须是非负整数");
        }

        return version;
    }

    // 首发 schema 同时移除旧产品版本字段；配置值本身不变。
    private static int Migrate0To1(YamlMappingNode root)
    {
        root.Children.Remove(new YamlScalarNode("version"));
        root.Add(new YamlScalarNode("schema_version"), new YamlScalarNode("1"));
        return 1;
    }
}

internal sealed class ConfigMigrationException(string message) : Exception(message);
