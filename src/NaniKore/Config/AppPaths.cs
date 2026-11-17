namespace NaniKore.Config;

/// <summary>
/// 本机数据目录（<c>程序目录\.data\</c>）的唯一真相：程序目录根只放交付物，运行期数据全部收拢到这里。
/// 发布 zip 不含 <c>.data</c>，解压覆盖在物理上碰不到用户数据；备份整个软件 = 复制 <c>.data</c>。
/// </summary>
internal static class AppPaths
{
    /// <summary>数据目录名：与 exe 同级，dot 前缀表明「本机数据」而非交付物。</summary>
    public const string DataDirectoryName = ".data";

    /// <summary>用户配置文件名（首启由模板字节级复制生成）。</summary>
    public const string ConfigFileName = "config.yaml";

    /// <summary>随包发布的配置模板文件名；程序永不写它。</summary>
    public const string TemplateFileName = "config.template.yaml";

    /// <summary>密钥单文件名：整块 DPAPI 密文，内部是 {配置档名: API Key} 的 JSON。</summary>
    public const string KeysFileName = "keys.bin";

    /// <summary>收藏库文件名（SQLite）。</summary>
    public const string CollectionsFileName = "collections.db";

    /// <summary>可写性探测用的临时文件名；探测结束即删除，不残留。</summary>
    private const string WriteProbeFileName = ".write-probe";

    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, DataDirectoryName);

    public static string ConfigPath => Path.Combine(DataDirectory, ConfigFileName);

    public static string KeysPath => Path.Combine(DataDirectory, KeysFileName);

    /// <summary>
    /// 确保数据目录存在且可写，失败抛 <see cref="DataDirectoryUnavailableException"/>（拒绝启动，不回落别处）。
    /// 宁可明确失败，也不要两份真相：程序目录不可写时没有任何「换个地方存」的备选方案。
    /// </summary>
    public static void EnsureWritableDataDirectory(string? directory = null)
    {
        var target = directory ?? DataDirectory;
        try
        {
            Directory.CreateDirectory(target);
            // 目录创建成功不代表可写（只读属性、防病毒软锁定、.data 是同名文件等），必须真写一次。
            var probe = Path.Combine(target, WriteProbeFileName);
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new DataDirectoryUnavailableException(target, exception);
        }
    }
}

/// <summary>
/// 数据目录不可创建或不可写；宿主据此拒绝启动（GUI 弹错、CLI 走 Console.Error，退出码均为 1）。
/// </summary>
internal sealed class DataDirectoryUnavailableException(string directory, Exception innerException)
    : Exception($"数据目录不可用（无法创建或无法写入）：{directory}。请确认程序目录可写后重试", innerException)
{
    public string Directory { get; } = directory;
}
