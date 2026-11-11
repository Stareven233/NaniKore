using System.Security.Cryptography;
using NaniKore.Translation;
using System.Net.Http;
using NaniKore.Ocr.Models;

namespace NaniKore.Phonetics;

/// <summary>
/// 注音词典在本地磁盘的唯一定位：跟随 EXE 的 deps/phonetics/&lt;lang&gt;/，
/// 与仓库 deps/ 目录、ModelScope 仓库 Noetuu/nanikore-deps 三方严格镜像，无版本号别名目录。
/// </summary>
internal static class PhoneticDependencyPaths
{
    /// <summary>
    /// 注音词典定位：优先指向仓库检出的 deps/phonetics/（开发期生成物与 gitignore 的开发态来源），
    /// 找不到仓库标记时回落到跟随 EXE 的 deps/phonetics/（发布态）。
    /// </summary>
    public static string PhoneticsRootDirectory { get; } = ResolvePhoneticsRoot();

    private static string ResolvePhoneticsRoot()
    {
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) })
        {
            if (string.IsNullOrEmpty(startDirectory)) continue;
            foreach (var directory in WalkUp(startDirectory))
            {
                var candidate = Path.Combine(directory, "deps", "phonetics");
                if (Directory.Exists(candidate) || Directory.Exists(Path.Combine(directory, "codestable")))
                {
                    return candidate;
                }
            }
        }

        return Path.Combine(OcrDependencyPaths.ApplicationDependenciesDirectory, "phonetics");
    }

    private static IEnumerable<string> WalkUp(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    /// <summary>EXE 同级 deps/phonetics 根目录（复用 OCR 依赖的运行目录口径：跟随 EXE 不随工作目录漂移）。</summary>
    public static string JapaneseDictionaryDirectory => Path.Combine(PhoneticsRootDirectory, "ja");

    public static IReadOnlyList<string> JapaneseDictionaryFiles =>
    [
        Path.Combine(JapaneseDictionaryDirectory, "sys.dic"),
        Path.Combine(JapaneseDictionaryDirectory, "unk.dic"),
        Path.Combine(JapaneseDictionaryDirectory, "matrix.bin"),
        Path.Combine(JapaneseDictionaryDirectory, "char.bin")
    ];

    public static string EnglishDictionaryDirectory => Path.Combine(PhoneticsRootDirectory, "en");

    public static string EnglishDictionaryFile => Path.Combine(EnglishDictionaryDirectory, "ipa_dict.db");

    public static string ChineseDictionaryDirectory => Path.Combine(PhoneticsRootDirectory, "zh");

    public static IReadOnlyList<string> ChineseDictionaryFiles =>
    [
        Path.Combine(ChineseDictionaryDirectory, "sys.dic"),
        Path.Combine(ChineseDictionaryDirectory, "unk.dic"),
        Path.Combine(ChineseDictionaryDirectory, "matrix.bin"),
        Path.Combine(ChineseDictionaryDirectory, "char.bin")
    ];
}

/// <summary>注音词典清单条目：相对 deps/phonetics/ 的路径 + SHA-256（大写十六进制）+ ModelScope 直链。</summary>
internal sealed record PhoneticDictionaryFile(string RelativePath, string Sha256, string ModelScopeUrl);

/// <summary>按语言词典的本地状态（设置页卡片与软降级口径共用）。</summary>
internal enum PhoneticDictionaryState
{
    /// <summary>未装配该语言（如未来的 ko 在初版清单里没有条目）。</summary>
    Unsupported,

    /// <summary>本地一个文件都没有，需要完整下载。</summary>
    Missing,

    /// <summary>文件在但哈希不符（旧版产物或损坏），需要更新。</summary>
    NeedsUpdate,

    /// <summary>全部文件存在且哈希相符，可直接使用。</summary>
    Ready,
}

/// <summary>
/// 按语言组织的注音词典清单：哈希即版本（不符即提示更新，另设版本号）。
/// 日语 = IPAdic 编译态；简体中文 = CC-CEDICT 转译 MeCab 词典；英语 = CMUdict 基座 IPA 词典。
/// </summary>
internal static class PhoneticDependencyManifest
{
    private const string ModelScopeBaseUrl =
        "https://modelscope.cn/models/Noetuu/nanikore-deps/resolve/master/phonetics/";

    public const string JapaneseCode = "ja";
    public const string EnglishCode = "en";
    public const string ChineseCode = "zh";

    public static IReadOnlyList<string> SupportedLanguages { get; } = [JapaneseCode, EnglishCode, ChineseCode];

    public static IReadOnlyList<PhoneticDictionaryFile> JapaneseFiles { get; } = Build(
    [
        ("ja/sys.dic", "C72DFC026322B6B8F6BBC8D90FD6706BEF9D566D9B36B061EAC8C494279FC427"),
        ("ja/unk.dic", "A5ACCEF0D977C92F6FF5CC30A6428DF2DB8390B3D3A315273DE9851314FA0DB4"),
        ("ja/matrix.bin", "EE44D7350CDCB680EBD699F83E121BE1DC63310F8832D55BCB537A068177611A"),
        ("ja/char.bin", "81BBA502AE48FA005A374819F15E44452EB68086A8B323EC20A044D514400832")
    ]);

    public static IReadOnlyList<PhoneticDictionaryFile> EnglishFiles { get; } = Build(
    [
        ("en/ipa_dict.db", "7B5BC5C0E8D75F7A1C4E7E0C1F990E0F629091CEE7EFAD42A7749AAB94152C13")
    ]);

    public static IReadOnlyList<PhoneticDictionaryFile> ChineseFiles { get; } = Build(
    [
        ("zh/sys.dic", "D98AE5D8F1D5A524C2B15EBB745BE71AF70A6F969D13C1CF7FEED77017F11EEF"),
        ("zh/unk.dic", "BF87973D9DD328D71A17DABF4EFCFAFA91EC4271EA1732445A93FA825B30FCA0"),
        ("zh/matrix.bin", "CD0BAE01D09803811B7A14B69461D4B28F8D6E0F0B779AC030ADFE1CDC0DEE6A"),
        ("zh/char.bin", "FCB88F2B8AD45EC617B8E9123D9925334D39A4CCC11AA7BCA715613E64735ABC")
    ]);

    /// <summary>取某语言的文件清单；不支持的语言返回空。</summary>
    public static IReadOnlyList<PhoneticDictionaryFile> GetFiles(string languageCode) => languageCode switch
    {
        JapaneseCode => JapaneseFiles,
        EnglishCode => EnglishFiles,
        ChineseCode => ChineseFiles,
        _ => []
    };

    /// <summary>本地文件是否全部存在且哈希相符（设置页版本检测与软降级口径）。</summary>
    public static bool IsComplete(string languageCode)
    {
        var files = GetFiles(languageCode);
        return files.Count > 0 && files.All(file => HashMismatch(file) is null);
    }

    /// <summary>
    /// 该语言词典的本地状态：区分「完全缺失」与「哈希不符需更新」——
    /// 前者引导首次下载，后者提示版本更新，两者都走同一下载器（已存在且相符的文件自动跳过）。
    /// </summary>
    public static PhoneticDictionaryState GetState(string languageCode)
    {
        var files = GetFiles(languageCode);
        if (files.Count == 0)
        {
            return PhoneticDictionaryState.Unsupported;
        }

        var anyPresent = files.Any(file => File.Exists(GetApplicationPath(file.RelativePath)));
        return GetIncompleteFiles(languageCode).Count == 0
            ? PhoneticDictionaryState.Ready
            : anyPresent ? PhoneticDictionaryState.NeedsUpdate : PhoneticDictionaryState.Missing;
    }

    /// <summary>缺失/不匹配的文件清单（空 = 完整）。</summary>
    public static IReadOnlyList<PhoneticDictionaryFile> GetIncompleteFiles(string languageCode)
    {
        var files = GetFiles(languageCode);
        var incomplete = new List<PhoneticDictionaryFile>();
        foreach (var file in files)
        {
            if (HashMismatch(file) is not null)
            {
                incomplete.Add(file);
            }
        }

        return incomplete;
    }

    /// <summary>实际落盘路径：优先仓库 dev 布局，发布态回落跟随 EXE 的 deps/phonetics/...。</summary>
    public static string GetApplicationPath(string relativePath) =>
        Path.Combine(PhoneticDependencyPaths.PhoneticsRootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static PhoneticDictionaryFile? HashMismatch(PhoneticDictionaryFile file)
    {
        var path = GetApplicationPath(file.RelativePath);
        if (!File.Exists(path))
        {
            return file;
        }

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase) ? null : file;
    }

    private static List<PhoneticDictionaryFile> Build(IEnumerable<(string RelativePath, string Sha256)> entries) =>
        entries.Select(entry => new PhoneticDictionaryFile(entry.RelativePath, entry.Sha256, ModelScopeBaseUrl + entry.RelativePath + "?download=true")).ToList();
}

/// <summary>
/// 注音词典按语言下载器：与 OCR 依赖一致的 ModelScope 直链 + 临时文件 + SHA-256 校验 + 失败清理。
/// 用户只装需要的语言；已存在且哈希相符的文件跳过。
/// </summary>
internal sealed class PhoneticDependencyDownloader
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task DownloadAsync(string languageCode, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var files = PhoneticDependencyManifest.GetFiles(languageCode);
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"未知的注音词典语言代码：{languageCode}");
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = PhoneticDependencyManifest.GetApplicationPath(file.RelativePath);
            if (File.Exists(destination))
            {
                using var stream = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (string.Equals(Convert.ToHexString(SHA256.HashData(stream)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report($"已存在：{file.RelativePath}");
                    continue;
                }
            }

            progress?.Report($"正在下载：{file.RelativePath}");
            await DownloadWithRetryAsync(file, destination, cancellationToken).ConfigureAwait(true);
        }
    }

    private static async Task DownloadWithRetryAsync(PhoneticDictionaryFile file, string destination, CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var response = await HttpClient.GetAsync(file.ModelScopeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(true);
            }

            // Windows 移动文件前必须释放校验句柄，避免与重命名操作发生共享冲突。
            using (var verifyStream = File.Open(temporary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var actual = Convert.ToHexString(SHA256.HashData(verifyStream));
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SHA-256 校验失败（期望 {file.Sha256}，实际 {actual}）。请重试下载。");
                }
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
