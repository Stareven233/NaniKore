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
/// 中文拼音数据的清单与状态：数据不随程序集分发，放 deps/phonetics/zh_pinyin.txt.gz，
/// 由设置页按需下载。哈希即版本——SHA-256 不符即判为「需要更新」，不另设版本号。
/// </summary>
internal static class ChinesePinyinData
{
    /// <summary>数据清单（路径相对 deps/phonetics/）：同步脚本按这一行核对上传字节。</summary>
    private static readonly PhoneticDataFile DataFile =
        new("zh_pinyin.txt.gz", "2B5C68679BD393624D17B77BCA5734F9A608EB758FFA2F98D69D1DE0E520A981");

    /// <summary>相对 phonetics/ 的路径，同时就是 ModelScope 上的远端路径。</summary>
    public static string RelativePath => DataFile.RelativePath;

    /// <summary>哈希即版本：产物重跑后按 --build-zh-data 打印的值回填。</summary>
    public static string Sha256 => DataFile.Sha256;

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

    /// <summary>文件哈希是否与本清单一致（大写十六进制，与下载校验、清单回填同一口径）。</summary>
    internal static bool MatchesSha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), Sha256, StringComparison.OrdinalIgnoreCase);
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
            progress?.Report($"已存在：{ChinesePinyinData.RelativePath}");
            return;
        }

        progress?.Report($"正在下载：{ChinesePinyinData.RelativePath}");
        var temporary = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var response = await HttpClient.GetAsync(ChinesePinyinData.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(true);
            }

            if (!ChinesePinyinData.MatchesSha256(temporary))
            {
                throw new InvalidDataException($"SHA-256 校验失败（期望 {ChinesePinyinData.Sha256}）。请重试下载");
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
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
