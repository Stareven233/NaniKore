using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NaniKore.Config;

/// <summary>
/// 密钥单文件（issue 017 / epic 002 第 7 节）：数据目录下的 <c>keys.bin</c> 密文。
/// 外层带有方案版本头（Scheme 1 = Windows DPAPI CurrentUser），内层是
/// <c>{配置档名: API Key}</c> 的 JSON（经由 AOT 静态源生成）。
/// 现有未带版本头的历史 DPAPI 密文实现零迁移自动识别兼容；文件与目录名不含档名、不含 Key。
/// </summary>
internal sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NaniKore.v1");

    /// <summary>
    /// 方案版本头魔数："NKKEY" (ASCII 5 字节) + Scheme (1 字节, 0x01 = Windows DPAPI) + Version (1 字节, 0x01)
    /// </summary>
    private static readonly byte[] SchemeHeaderPrefix = [(byte)'N', (byte)'K', (byte)'K', (byte)'E', (byte)'Y'];
    private const byte SchemeDpapi = 1;
    private const byte SchemeVersion1 = 1;
    private const int HeaderLength = 7;

    private readonly string _keysPath;

    // 读取失败（密文无法解密、JSON 损坏或未知方案版本）后置位：写盘时拒绝覆盖整份旧密文（R09）。
    private bool _readFailed;
    private string? _loadWarning;

    /// <param name="directory">密钥所在目录；缺省为数据目录 <c>.data</c>，自测一律注入临时目录。</param>
    public SecretStore(string? directory = null)
    {
        var target = directory ?? AppPaths.DataDirectory;
        _keysPath = Path.Combine(target, AppPaths.KeysFileName);
    }

    public string KeysPath => _keysPath;

    /// <summary>密钥文件存在但无法解密/解析；此时写盘会拒绝覆盖原密文（R09）。</summary>
    public bool ReadFailed => _readFailed;

    /// <summary>供托盘/设置页展示的可行动中文说明；成功或文件缺失时为 null。</summary>
    public string? LoadWarning => _loadWarning;

    /// <summary>启动时探测密钥文件是否可读；返回 true 表示存在但无法解密/解析（R09）。</summary>
    public bool CheckReadable()
    {
        _ = LoadMap();
        return _readFailed;
    }

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
    /// 读整份映射。文件缺失或空文件按「未配置」处理；DPAPI 解不开（换用户/换机器）、JSON 损坏或未知方案版本
    /// 同样返回空字典不抛异常（绝不泄露密文内容、不打断调用方），但会置 <see cref="ReadFailed"/> 并记录可行动说明，
    /// 供写盘阶段拒绝覆盖（R09）。自动根据文件头判断方案版本：无 NKKEY 头则作为旧版无头 DPAPI 处理（零迁移兼容）。
    /// </summary>
    private Dictionary<string, string> LoadMap()
    {
        try
        {
            if (!File.Exists(_keysPath))
            {
                ClearReadFailure();
                return NewMap();
            }

            var rawBytes = File.ReadAllBytes(_keysPath);
            if (rawBytes.Length == 0)
            {
                ClearReadFailure();
                return NewMap();
            }

            byte[] ciphertext;
            if (rawBytes.Length >= HeaderLength &&
                rawBytes.AsSpan(0, 5).SequenceEqual(SchemeHeaderPrefix))
            {
                var scheme = rawBytes[5];
                var version = rawBytes[6];
                // 未知方案版本：原文件可能是其它版本的密文，安全降级为空并要求用户手动处理，绝不静默覆盖。
                if (scheme != SchemeDpapi || version != SchemeVersion1)
                {
                    SetReadFailure("keys.bin 的方案版本无法识别，原始密文可能属于其它版本");
                    return NewMap();
                }

                ciphertext = rawBytes[HeaderLength..];
            }
            else
            {
                // 无头旧格式：零迁移直接作为 DPAPI 密文读取
                ciphertext = rawBytes;
            }

            var plaintext = ProtectedData.Unprotect(
                ciphertext,
                Entropy,
                DataProtectionScope.CurrentUser);

            var parsed = JsonSerializer.Deserialize(
                plaintext,
                NaniKoreJsonSerializerContext.Default.DictionaryStringString);
            if (parsed is { } map)
            {
                ClearReadFailure();
                return NewMap(map);
            }

            SetReadFailure("keys.bin 解密后的内容无法解析");
            return NewMap();
        }
        catch (CryptographicException)
        {
            // DPAPI 绑定当前 Windows 用户：换用户/换机器或密文损坏都会落到这里。
            SetReadFailure("keys.bin 无法解密（可能由其它 Windows 用户或机器生成，或密文已损坏）");
            return NewMap();
        }
        catch (JsonException)
        {
            SetReadFailure("keys.bin 解密后的内容已损坏，无法解析");
            return NewMap();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetReadFailure($"keys.bin 读取失败：{exception.Message}");
            return NewMap();
        }
    }

    private void ClearReadFailure()
    {
        _readFailed = false;
        _loadWarning = null;
    }

    private void SetReadFailure(string reason)
    {
        _readFailed = true;
        _loadWarning = $"{reason}为避免覆盖原始密文，保存新密钥将被拒绝；请将 {_keysPath} 改名或删除后重启";
    }

    /// <summary>
    /// 整块加密写盘：先通过源生成上下文序列化 JSON，再经由 DPAPI 加密，
    /// 拼装方案版本头后经临时文件原子替换，避免崩溃损坏文件。
    /// </summary>
    private void WriteMap(Dictionary<string, string> map)
    {
        // 读取失败时拒绝覆盖（R09）：旧密文可能仍可人工恢复，不能因保存一个新 Key 就整份抹掉。
        if (_readFailed)
        {
            throw new SecretStoreWriteRefusedException(_loadWarning ?? "keys.bin 读取失败，已拒绝写入");
        }

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            map,
            NaniKoreJsonSerializerContext.Default.DictionaryStringString);

        var ciphertext = ProtectedData.Protect(
            jsonBytes,
            Entropy,
            DataProtectionScope.CurrentUser);

        // 打包带方案版本头的数据包
        var payload = new byte[HeaderLength + ciphertext.Length];
        SchemeHeaderPrefix.CopyTo(payload, 0);
        payload[5] = SchemeDpapi;
        payload[6] = SchemeVersion1;
        ciphertext.CopyTo(payload, HeaderLength);

        Directory.CreateDirectory(Path.GetDirectoryName(_keysPath)!);
        var temporaryPath = _keysPath + ".tmp";
        File.WriteAllBytes(temporaryPath, payload);
        File.Move(temporaryPath, _keysPath, overwrite: true);
    }

    /// <summary>档名匹配口径与配置、活动档一致：忽略大小写。</summary>
    private static Dictionary<string, string> NewMap(Dictionary<string, string>? source = null) =>
        source is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// keys.bin 读取失败后仍尝试写盘时抛出（R09）：拒绝用新密文覆盖可能仍可恢复的旧密文。
/// </summary>
internal sealed class SecretStoreWriteRefusedException(string message) : Exception(message);
