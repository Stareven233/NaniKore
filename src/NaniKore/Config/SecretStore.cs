using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NaniKore.Config;

/// <summary>以当前 Windows 用户的 DPAPI 保护各配置档 API Key。</summary>
internal static class SecretStore
{
    private static readonly string SecretsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NaniKore",
        "secrets");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NaniKore.v1");
    private static readonly Regex ProfileIdPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    public static void Save(string profileId, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        Directory.CreateDirectory(SecretsDirectory);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey),
            Entropy,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetKeyPath(profileId), encrypted);
    }

    public static string? Load(string profileId)
    {
        var path = GetKeyPath(profileId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(path),
                Entropy,
                DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // 用户或机器变化后旧 DPAPI 密文无法读取；按未配置处理，绝不泄露密文内容。
            return null;
        }
    }

    public static bool Exists(string profileId) => File.Exists(GetKeyPath(profileId));

    public static void Delete(string profileId)
    {
        var path = GetKeyPath(profileId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string GetKeyPath(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!ProfileIdPattern.IsMatch(profileId))
        {
            throw new ArgumentException("配置档 ID 只能包含字母、数字、连字符和下划线。", nameof(profileId));
        }

        return Path.Combine(SecretsDirectory, $"{profileId}.bin");
    }
}
