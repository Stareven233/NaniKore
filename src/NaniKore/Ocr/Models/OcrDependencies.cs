using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NaniKore.Ocr.Models;

internal static class OcrDependencyPaths
{
    public const string ModelRelativePath = "ocr-onnx/pp-ocr-v6-small";

    /// <summary>依赖必须跟随当前 EXE，不能因为工作目录或宿主加载方式改变而漂移。</summary>
    public static string ApplicationDirectory =>
        Environment.ProcessPath is { } processPath
            ? Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;

    public static string ApplicationDependenciesDirectory =>
        Path.Combine(ApplicationDirectory, "deps");

    public static string GetApplicationPath(string relativePath) =>
        Path.Combine(ApplicationDependenciesDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
}

internal sealed record OcrDependencyFile(
    string RelativePath,
    string Sha256,
    string ModelScopeUrl,
    string? HuggingFaceUrl);

internal static class OcrDependencyManifest
{
    private const string ModelScopeBaseUrl =
        "https://modelscope.cn/models/Noetuu/nanikore-deps/resolve/master/";

    public static IReadOnlyList<OcrDependencyFile> Files =
    [
        Create("ocr-onnx/pp-ocr-v6-small/det/inference.onnx", "D73E0058B7A8086BBD57F3D10B8BCD4FF95363F67E06E2762B5E814FE9C9410E", "https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx/resolve/main/inference.onnx?download=true"),
        Create("ocr-onnx/pp-ocr-v6-small/det/inference.yml", "193F435274BF9F0B5F71A929BBFBCF148282DF7E633B34E7C373E8F44741B516", "https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx/resolve/main/inference.yml?download=true"),
        Create("ocr-onnx/pp-ocr-v6-small/rec/inference.onnx", "5435FD747C9E0EFE15A96D0B378D5BD157E9492ED8FD80EDF08F30D02FA24634", "https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/main/inference.onnx?download=true"),
        Create("ocr-onnx/pp-ocr-v6-small/rec/inference.yml", "AB078671BB49F06228EADCCD34F1BB501E157F7A047095FFB943BA81512C77D1", "https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/main/inference.yml?download=true"),
        Create("onnxruntime/onnxruntime.dll", "18370C375F07357FA5874344A9D9AC17E6B6FE1EB18B1DD209D79483B4470257"),
        Create("onnxruntime/onnxruntime_providers_shared.dll", "599629FA643707DEFE9156140AE5EDD73531F221AA97B7585B1C9BB0A93586F8"),
    ];

    public static bool IsComplete() => Files.All(file =>
        HasExpectedHash(OcrDependencyPaths.GetApplicationPath(file.RelativePath), file.Sha256));

    private static bool HasExpectedHash(string path, string expectedSha256)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static OcrDependencyFile Create(string relativePath, string sha256, string? huggingFaceUrl = null) =>
        new(relativePath, sha256, ModelScopeBaseUrl + relativePath + "?download=true", huggingFaceUrl);
}

internal sealed class OcrDependencyDownloader
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task DownloadAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        foreach (var file in OcrDependencyManifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = OcrDependencyPaths.GetApplicationPath(file.RelativePath);
            if (HasExpectedHash(destination, file.Sha256))
            {
                progress?.Report($"已存在：{file.RelativePath}");
                continue;
            }

            progress?.Report($"正在下载：{file.RelativePath}");
            await DownloadFileWithFallbackAsync(file, destination, cancellationToken).ConfigureAwait(true);
        }
    }

    private static async Task DownloadFileWithFallbackAsync(
        OcrDependencyFile file,
        string destination,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var sources = new[]
        {
            (Name: "ModelScope", Url: file.ModelScopeUrl),
            (Name: "Hugging Face", Url: file.HuggingFaceUrl),
        }.Where(source => !string.IsNullOrWhiteSpace(source.Url));

        foreach (var source in sources)
        {
            var temporary = destination + $".{Guid.NewGuid():N}.download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await DownloadAndVerifyAsync(source.Url!, temporary, file.Sha256, cancellationToken).ConfigureAwait(true);
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or CryptographicException or InvalidDataException)
            {
                errors.Add($"{source.Name}: {exception.Message}");
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        throw new InvalidOperationException(
            $"无法下载依赖文件 {file.RelativePath}" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private static async Task DownloadAndVerifyAsync(
        string url,
        string temporary,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true))
        await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(true);
        }

        if (!HasExpectedHash(temporary, expectedSha256))
        {
            throw new InvalidDataException(
                $"SHA-256 校验失败，期望 {expectedSha256}。请重试下载。 ");
        }
    }

    private static bool HasExpectedHash(string path, string expectedSha256)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NaniKore/1.0");
        return client;
    }
}

internal static class OnnxRuntimeNativeLoader
{
    private static readonly string[] NativeLibraryNames = ["onnxruntime", "onnxruntime_providers_shared"];
    private static int _configured;

    public static void EnsureConfigured()
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var dependencies = OcrDependencyPaths.ApplicationDependenciesDirectory;
        foreach (var name in NativeLibraryNames)
        {
            if (!string.Equals(name, libraryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = Path.Combine(dependencies, "onnxruntime", name + ".dll");
            if (NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }
}
