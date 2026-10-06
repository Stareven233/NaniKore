using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NaniKore.Config;

/// <summary>
/// 密钥单文件（issue 017）：数据目录下的 <c>keys.bin</c> 整块 DPAPI 密文，内层是
/// <c>{配置档名: API Key}</c> 的 JSON。密钥位置只有一个，不可改、不需要改；
/// 文件与目录名不含档名、不含 Key，点开文件是乱码。
/// 旧布局 <c>%APPDATA%\NaniKore\secrets\{id}.bin</c> 不读、不删、不提（零兼容立场）。
/// </summary>
internal sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NaniKore.v1");

    private readonly string _keysPath;

    /// <param name="directory">密钥所在目录；缺省为数据目录 <c>.data</c>，自测一律注入临时目录。</param>
    public SecretStore(string? directory = null)
    {
        var target = directory ?? AppPaths.DataDirectory;
        _keysPath = Path.Combine(target, AppPaths.KeysFileName);
    }

    public string KeysPath => _keysPath;

    public void Save(string name, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var map = LoadMap();
        map[name] = apiKey;
        WriteMap(map);
    }

    public string? Load(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return LoadMap().TryGetValue(name, out var apiKey) ? apiKey : null;
    }

    public bool Exists(string name) => Load(name) is not null;

    public void Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!File.Exists(_keysPath))
        {
            return;
        }

        var map = LoadMap();
        if (map.Remove(name))
        {
            WriteMap(map);
        }
    }

    /// <summary>配置档重命名后把密钥键名跟着搬：name 是唯一索引，否则一改名 Key 就丢。</summary>
    public void Rename(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (!File.Exists(_keysPath))
        {
            return;
        }

        var map = LoadMap();
        if (map.Remove(oldName, out var apiKey))
        {
            map[newName] = apiKey;
            WriteMap(map);
        }
    }

    /// <summary>
    /// 读整份映射。文件缺失、DPAPI 解不开（换用户/换机器）或 JSON 损坏一律按「未配置」处理，
    /// 返回空字典，绝不泄露密文内容、绝不抛异常打断调用方。
    /// </summary>
    private Dictionary<string, string> LoadMap()
    {
        try
        {
            if (!File.Exists(_keysPath))
            {
                return NewMap();
            }

            var plaintext = ProtectedData.Unprotect(
                File.ReadAllBytes(_keysPath),
                Entropy,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext) is { } map
                ? NewMap(map)
                : NewMap();
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            return NewMap();
        }
    }

    /// <summary>
    /// 整块加密写盘：先序列化 JSON 再整体 DPAPI，临时文件 + File.Move 原子替换，
    /// 避免崩溃留下半份映射（与 ConfigStore.AtomicWrite 同一套姿势）。
    /// </summary>
    private void WriteMap(Dictionary<string, string> map)
    {
        var ciphertext = ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(map),
            Entropy,
            DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_keysPath)!);
        var temporaryPath = _keysPath + ".tmp";
        File.WriteAllBytes(temporaryPath, ciphertext);
        File.Move(temporaryPath, _keysPath, overwrite: true);
    }

    /// <summary>档名匹配口径与配置、活动档一致：忽略大小写。</summary>
    private static Dictionary<string, string> NewMap(Dictionary<string, string>? source = null) =>
        source is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
}
