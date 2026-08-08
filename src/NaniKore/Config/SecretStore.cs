using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NaniKore.Config;

/// <summary>
/// DPAPI 加解密 API Key。
/// 加密后落 %APPDATA%\NaniKore\，config.yaml 无 key 字段。
/// </summary>
internal static class SecretStore
{
    private static readonly string SecretsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
         NaniKore);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(NaniKore_v1);

    /// <summary>
    /// 保存 API Key：用 DPAPI 加密后写入 <secretsDir>/<profileId>.key。
    /// </summary>
    public static void Save(string profileId, string apiKey)
    {
        Directory.CreateDirectory(SecretsDir);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey),
            Entropy,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetKeyPath(profileId), encrypted);
    }

    /// <summary>
    /// 读取 API Key：不存在时返回 null。
    /// </summary>
    public static string? Load(string profileId)
    {
        var path = GetKeyPath(profileId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var decrypted = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            // DPAPI 解密失败（如换了用户或机器），返回 null 让调用方处理。
            return null;
        }
    }

    /// <summary>检查指定配置档的密钥是否就位（不回显 key）。</summary>
    public static bool Exists(string profileId) => File.Exists(GetKeyPath(profileId));

    /// <summary>删除指定配置档的密钥。</summary>
    public static void Delete(string profileId)
    {
        var path = GetKeyPath(profileId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string GetKeyPath(string profileId) =>
        Path.Combine(SecretsDir, $ );
}