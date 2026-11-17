using System.IO.Compression;

namespace NaniKore.Phonetics;

/// <summary>安装结果：<see cref="RequiresRestart"/> 为 true 表示新版本已暂存，等宿主重启替换。</summary>
internal readonly record struct NativePhoneticInstallOutcome(bool RequiresRestart);

/// <summary>
/// 原生语言包以单个 ZIP 下载并校验，解压到 <c>deps/phonetics/.staging-&lt;lang&gt;/</c> 后
/// 再逐文件校验 SHA-256 与 pack.json 身份，整个包通过后才提升为正式包。
/// 正在使用中的 DLL 绝不被覆盖——那种情况下保留暂存目录，由 <see cref="PromoteStaged"/> 在下次启动时替换。
/// 目录与下载动作都可注入：自测据此在临时目录里完整跑一遍「缺失 → 下载 → 校验 → 替换 → 重启生效」，不联网。
/// </summary>
internal sealed class NativePhoneticPackInstaller
{
    private readonly Func<string, string> _stagingDirectory;
    private readonly Func<string, string> _packDirectory;
    private readonly Func<string, string, string?, CancellationToken, Task> _download;

    public NativePhoneticPackInstaller(
        Func<string, string> stagingDirectory,
        Func<string, string> packDirectory,
        Func<string, string, string?, CancellationToken, Task>? download = null)
    {
        ArgumentNullException.ThrowIfNull(stagingDirectory);
        ArgumentNullException.ThrowIfNull(packDirectory);
        _stagingDirectory = stagingDirectory;
        _packDirectory = packDirectory;
        _download = download ?? DownloadFileAsync;
    }

    /// <summary>生产口径：暂存与正式目录都取自 <see cref="NativePhoneticManifest"/>。</summary>
    public static NativePhoneticPackInstaller Default { get; } =
        new(NativePhoneticManifest.GetStagingDirectory, NativePhoneticManifest.GetPackDirectory);

    /// <summary>暂存目录里是否已有候选包（不校验内容）。</summary>
    public bool HasStagedPack(NativePhoneticPack pack) => Directory.Exists(_stagingDirectory(pack.Language));

    /// <summary>校验暂存包：与正式包同一套判据（哈希 + pack.json + PE 架构）。</summary>
    public NativePhoneticPackageFailure VerifyStaged(NativePhoneticPack pack) =>
        NativePhoneticPackVerifier.Verify(pack, _stagingDirectory(pack.Language), out _);

    /// <summary>当前正式包已完整且没有待生效的新版本：无需下载。</summary>
    public bool IsUpToDate(NativePhoneticPack pack) =>
        NativePhoneticPackVerifier.Verify(pack, _packDirectory(pack.Language), out _) == NativePhoneticPackageFailure.None &&
        !HasStagedPack(pack);

    /// <summary>
    /// 下载并安装：已是暂存状态的候选包直接复用；校验通过且该语言尚未加载时立即替换，
    /// 已加载时保留暂存目录等重启。取消与失败都会向上抛出，由宿主把状态置为「失败」并展示原因。
    /// </summary>
    public async Task<NativePhoneticInstallOutcome> InstallAsync(
        NativePhoneticPack pack,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var staging = _stagingDirectory(pack.Language);
        var archive = staging + ".download.zip";
        if (Directory.Exists(staging) && NativePhoneticPackVerifier.Verify(pack, staging, out _) == NativePhoneticPackageFailure.None)
        {
            progress?.Report($"已存在：{NativePhoneticManifest.ArchiveFileName(pack.Language)}");
        }
        else
        {
            progress?.Report($"正在下载：{NativePhoneticManifest.ArchiveFileName(pack.Language)}");
            await _download(
                PhoneticDataPaths.ModelScopeBaseUrl + NativePhoneticManifest.ArchiveFileName(pack.Language) + "?download=true",
                archive,
                NativePhoneticManifest.ArchiveSha256(pack.Language),
                cancellationToken).ConfigureAwait(true);

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ExtractArchive(archive, staging);
        }

        var failure = NativePhoneticPackVerifier.Verify(pack, staging, out var detail);
        if (failure != NativePhoneticPackageFailure.None)
            throw new InvalidDataException(NativePhoneticPackageFailureText.Describe(failure, pack, detail) + " 请重试下载");

        var downloadedArchive = staging + ".download.zip";
        if (File.Exists(downloadedArchive)) File.Delete(downloadedArchive);

        if (NativePhoneticLibrary.IsLanguageLoaded(pack.Language))
        {
            // 正在使用的 DLL 不能被覆盖：暂存目录留着，重启时由 ApplyStagedPacks 提升。
            progress?.Report($"{pack.DisplayName}语言包已下载到暂存目录，重启后生效");
            return new NativePhoneticInstallOutcome(RequiresRestart: true);
        }

        PromoteStaged(pack);
        return new NativePhoneticInstallOutcome(RequiresRestart: false);
    }

    /// <summary>把校验通过的暂存包替换为正式包（启动时与未加载时的即时安装共用）。</summary>
    public void PromoteStaged(NativePhoneticPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var staging = _stagingDirectory(pack.Language);
        var target = _packDirectory(pack.Language);
        Directory.CreateDirectory(target);

        foreach (var file in pack.Files)
        {
            var source = NativePhoneticManifest.GetFilePath(staging, file.RelativePath);
            var destination = NativePhoneticManifest.GetFilePath(target, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: true);
        }

        var sourceManifest = NativePhoneticManifest.GetFilePath(staging, NativePhoneticManifest.PackManifestFileName);
        File.Move(sourceManifest, Path.Combine(target, NativePhoneticManifest.PackManifestFileName), overwrite: true);
        DiscardStaged(pack);
    }

    /// <summary>丢弃暂存目录（校验失败或已提升完成）。</summary>
    public void DiscardStaged(NativePhoneticPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var staging = _stagingDirectory(pack.Language);
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (IOException)
        {
            // 目录被占用不影响后续判定：下次启动仍会重新校验，坏包不会覆盖正式包。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static void ExtractArchive(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("语言包 ZIP 含有越界路径");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    /// <summary>默认下载实现：ModelScope 直链 + 临时文件 + 哈希校验 + 失败清理。</summary>
    private static async Task DownloadFileAsync(
        string url,
        string destination,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(true);
            }

            if (expectedSha256 is not null)
            {
                // Windows 上移动文件前必须释放校验句柄，避免与重命名冲突。
                var actual = NativePhoneticPackVerifier.HashFile(temporary);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SHA-256 校验失败（期望 {expectedSha256}，实际 {actual}）。请重试下载");
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
