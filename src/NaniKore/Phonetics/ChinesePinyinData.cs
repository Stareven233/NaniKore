using System.Net.Http;
using System.Security.Cryptography;

namespace NaniKore.Phonetics;

/// <summary>拼音数据在本地磁盘的状态（设置页卡片文案与软降级口径共用）。</summary>
internal enum PhoneticDataState
{
    /// <summary>本地没有数据文件，需要首次下载。</summary>
    Missing,

    /// <summary>文件在但哈希不符（旧版产物或损坏），需要更新。</summary>
    NeedsUpdate,

    /// <summary>文件存在且哈希相符，可直接使用。</summary>
    Ready,
}

/// <summary>数据清单条目：相对 deps/phonetics/ 的路径 + SHA-256（大写十六进制）。</summary>
internal sealed record PhoneticDataFile(string RelativePath, string Sha256);

/// <summary>
/// 中文拼音数据远端以 gzip 分发，本地解压到 deps/phonetics/zh/zh_pinyin.txt，避免长期保留两份。
/// 本地明文与远端压缩包分别校验 SHA-256。
/// </summary>
internal static class ChinesePinyinData
{
    /// <summary>数据清单（路径相对 deps/phonetics/）：同步脚本按这一行核对上传字节。</summary>
    private static readonly PhoneticDataFile DataFile =
        new("zh_pinyin.txt.gz", "2B5C68679BD393624D17B77BCA5734F9A608EB758FFA2F98D69D1DE0E520A981");

    public const string LocalRelativePath = "zh/zh_pinyin.txt";
    public const string LocalSha256 = "81607688986AEF6D9A9C1EE089FAD10ECA0E694345DBBB4989550159DC4E8F6B";

    /// <summary>ModelScope 平铺的压缩源文件路径。</summary>
    public static string RelativePath => DataFile.RelativePath;

    /// <summary>本地明文数据的哈希。</summary>
    public static string Sha256 => LocalSha256;
    public static string ArchiveSha256 => DataFile.Sha256;

    public static string DownloadUrl => PhoneticDataPaths.ModelScopeBaseUrl + RelativePath + "?download=true";

    /// <summary>本地状态：完全缺失与哈希不符分开，前者引导下载、后者提示更新，两者都走同一下载器。</summary>
    public static PhoneticDataState GetState()
    {
        var path = PhoneticDataPaths.ChinesePinyinDataFile;
        if (!File.Exists(path))
        {
            return PhoneticDataState.Missing;
        }

        return MatchesSha256(path) ? PhoneticDataState.Ready : PhoneticDataState.NeedsUpdate;
    }

    /// <summary>数据是否就绪；未就绪时注音链路纯文本降级，不回退任何内置数据。</summary>
    public static bool IsComplete() => GetState() == PhoneticDataState.Ready;

    /// <summary>本地明文文件哈希是否与清单一致。</summary>
    internal static bool MatchesSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), LocalSha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 拼音数据下载器：ModelScope 直链 + 临时文件 + SHA-256 校验 + 失败清理，与原生语言包同一口径。
/// 已存在且哈希相符的文件直接跳过，不做任何静默容错。
/// </summary>
internal sealed class ChinesePinyinDownloader
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task DownloadAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var destination = PhoneticDataPaths.ChinesePinyinDataFile;
        if (File.Exists(destination) && ChinesePinyinData.MatchesSha256(destination))
        {
            progress?.Report($"已存在：{ChinesePinyinData.LocalRelativePath}");
            return;
        }

        progress?.Report($"正在下载：{ChinesePinyinData.RelativePath}");
        var archive = destination + $".{Guid.NewGuid():N}.download.gz";
        var temporary = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var response = await HttpClient.GetAsync(ChinesePinyinData.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true))
            await using (var target = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(true);
            }

            if (!string.Equals(HashFile(archive), ChinesePinyinData.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"压缩包 SHA-256 校验失败（期望 {ChinesePinyinData.ArchiveSha256}）。请重试下载");
            }

            // 先校验远端压缩包，再解压到同目录临时文件；明文校验通过后才替换正式数据。
            await using (var input = File.OpenRead(archive))
            await using (var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await gzip.CopyToAsync(output, cancellationToken).ConfigureAwait(true);
            }
            if (!ChinesePinyinData.MatchesSha256(temporary))
                throw new InvalidDataException($"解压后 SHA-256 校验失败（期望 {ChinesePinyinData.Sha256}）。请重试下载");
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NaniKore/1.0");
        return client;
    }
}
