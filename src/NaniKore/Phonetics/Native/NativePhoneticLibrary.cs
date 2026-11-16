using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NaniKore.Phonetics;

/// <summary>
/// 原生包装载失败的原因分类：设置页直接显示，自测逐条断言。
/// 「缺失/需更新」是可下载修复的常态，其余四项属于包被动过或构建口径不一致，必须显式暴露。
/// </summary>
internal enum NativePhoneticPackageFailure
{
    /// <summary>校验通过，可以使用。</summary>
    None,

    /// <summary>该语言没有原生包（中文走拼音数据文件，不在原生包清单里）。</summary>
    Unsupported,

    /// <summary>包目录或包内文件缺失，需要首次下载。</summary>
    Missing,

    /// <summary>文件在但哈希/字节数不符（旧版产物或损坏），需要更新。</summary>
    HashMismatch,

    /// <summary>PE 架构不是 win-x64。</summary>
    ArchitectureMismatch,

    /// <summary>包内 pack.json 与宿主身份清单不符（版本/ABI/导出/哈希对不上）。</summary>
    ManifestMismatch,

    /// <summary>DLL 无法加载（依赖缺失、系统拒绝、文件被占用等）。</summary>
    LoadFailed,

    /// <summary>四个 ABI 导出不齐。</summary>
    MissingExport,

    /// <summary>DLL 报告的 ABI 版本与宿主期望不一致。</summary>
    AbiMismatch,
}

/// <summary>包校验结果的描述（给设置页与自测看的一句话原因）。</summary>
internal static class NativePhoneticPackageFailureText
{
    public static string Describe(NativePhoneticPackageFailure failure, NativePhoneticPack? pack, string? detail)
    {
        var name = pack?.DisplayName ?? "该语言";
        return failure switch
        {
            NativePhoneticPackageFailure.None => "已就绪",
            NativePhoneticPackageFailure.Unsupported => $"{name}没有可用的原生注音包",
            NativePhoneticPackageFailure.Missing => $"{name}注音语言包未安装" + Suffix(detail),
            NativePhoneticPackageFailure.HashMismatch => $"{name}注音语言包版本不符，需要更新" + Suffix(detail),
            NativePhoneticPackageFailure.ArchitectureMismatch => $"{name}注音语言包架构不符（仅支持 win-x64）" + Suffix(detail),
            NativePhoneticPackageFailure.ManifestMismatch => $"{name}注音语言包清单与宿主不一致" + Suffix(detail),
            NativePhoneticPackageFailure.LoadFailed => $"{name}注音语言包加载失败" + Suffix(detail),
            NativePhoneticPackageFailure.MissingExport => $"{name}注音语言包缺少必需的 ABI 导出" + Suffix(detail),
            NativePhoneticPackageFailure.AbiMismatch => $"{name}注音语言包 ABI 版本不符" + Suffix(detail),
            _ => $"{name}注音语言包不可用" + Suffix(detail),
        };

        static string Suffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? "。" : $"：{detail}";
    }
}

/// <summary>
/// 原生包的静态校验：包目录、文件字节数与 SHA-256、PE 架构、包内 pack.json 与宿主身份清单核对。
/// 只读文件，不加载 DLL；设置页状态与自测共用这一套判据，避免「界面说就绪、加载却失败」。
/// </summary>
internal static class NativePhoneticPackVerifier
{
    /// <summary>校验整个包（不含 dlopen）。成功返回 <see cref="NativePhoneticPackageFailure.None"/>。</summary>
    public static NativePhoneticPackageFailure Verify(NativePhoneticPack pack, string packDirectory, out string? detail)
    {
        detail = null;
        string? missingFile = null;
        foreach (var file in pack.Files)
        {
            var path = NativePhoneticManifest.GetFilePath(packDirectory, file.RelativePath);
            if (File.Exists(path))
            {
                continue;
            }

            missingFile ??= file.RelativePath;
        }

        var manifestPath = Path.Combine(packDirectory, NativePhoneticManifest.PackManifestFileName);
        if (missingFile is not null || !File.Exists(manifestPath))
        {
            // 一个文件都没有 = 首次安装；有部分文件 = 下载中断，两者都要重新下载，但文案不同。
            detail = missingFile ?? NativePhoneticManifest.PackManifestFileName;
            var anyPresent = Directory.Exists(packDirectory) && Directory.EnumerateFileSystemEntries(packDirectory).Any();
            return anyPresent ? NativePhoneticPackageFailure.HashMismatch : NativePhoneticPackageFailure.Missing;
        }

        var libraryBytes = new FileInfo(NativePhoneticManifest.GetFilePath(packDirectory, pack.LibraryFileName)).Length;
        if (libraryBytes != pack.LibraryBytes)
        {
            detail = $"字节数 {libraryBytes}，期望 {pack.LibraryBytes}";
            return NativePhoneticPackageFailure.HashMismatch;
        }

        foreach (var file in pack.Files)
        {
            var path = NativePhoneticManifest.GetFilePath(packDirectory, file.RelativePath);
            var actual = HashFile(path);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                detail = file.RelativePath;
                return NativePhoneticPackageFailure.HashMismatch;
            }
        }

        var manifestFailure = VerifyPackManifest(pack, manifestPath, out detail);
        if (manifestFailure != NativePhoneticPackageFailure.None)
        {
            return manifestFailure;
        }

        return VerifyArchitecture(NativePhoneticManifest.GetFilePath(packDirectory, pack.LibraryFileName), out detail)
            ? NativePhoneticPackageFailure.None
            : NativePhoneticPackageFailure.ArchitectureMismatch;
    }

    /// <summary>包内 pack.json 必须与宿主身份清单一致：它记录本次构建的字节、哈希、ABI、架构与导出。</summary>
    private static NativePhoneticPackageFailure VerifyPackManifest(NativePhoneticPack pack, string manifestPath, out string? detail)
    {
        detail = null;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (ReadInt32(root, "abi") != NativePhoneticManifest.AbiVersion ||
                ReadString(root, "architecture") != NativePhoneticManifest.Architecture ||
                ReadString(root, "library") != pack.LibraryFileName ||
                ReadInt64(root, "bytes") != pack.LibraryBytes ||
                !string.Equals(ReadString(root, "sha256"), pack.LibrarySha256, StringComparison.OrdinalIgnoreCase))
            {
                detail = NativePhoneticManifest.PackManifestFileName;
                return NativePhoneticPackageFailure.ManifestMismatch;
            }

            var exports = ReadStringArray(root, "exports");
            foreach (var export in pack.Exports)
            {
                if (!exports.Contains(export, StringComparer.Ordinal))
                {
                    detail = export;
                    return NativePhoneticPackageFailure.ManifestMismatch;
                }
            }

            return NativePhoneticPackageFailure.None;
        }
        catch (JsonException)
        {
            detail = "无法解析 " + NativePhoneticManifest.PackManifestFileName;
            return NativePhoneticPackageFailure.ManifestMismatch;
        }
        catch (IOException exception)
        {
            detail = exception.Message;
            return NativePhoneticPackageFailure.ManifestMismatch;
        }
    }

    /// <summary>PE 头校验：只接受 AMD64（0x8664），避免把 x86/ARM64 产物当成本机包加载。</summary>
    internal static bool VerifyArchitecture(string libraryPath, out string? detail)
    {
        detail = null;
        try
        {
            using var stream = File.Open(libraryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[64];
            if (stream.Read(header) < header.Length)
            {
                detail = "PE 头截断";
                return false;
            }

            if (header[0] != 'M' || header[1] != 'Z')
            {
                detail = "不是 PE 文件";
                return false;
            }

            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
            if (peOffset <= 0 || peOffset > 0x1000_0000)
            {
                detail = "PE 头偏移非法";
                return false;
            }

            stream.Position = peOffset;
            Span<byte> signature = stackalloc byte[6];
            if (stream.Read(signature) < signature.Length)
            {
                detail = "PE 签名截断";
                return false;
            }

            if (signature[0] != 'P' || signature[1] != 'E' || signature[2] != 0 || signature[3] != 0)
            {
                detail = "PE 签名非法";
                return false;
            }

            const ushort machineAmd64 = 0x8664;
            var machine = BinaryPrimitives.ReadUInt16LittleEndian(signature[4..]);
            if (machine != machineAmd64)
            {
                detail = $"PE machine 0x{machine:X4}";
                return false;
            }

            return true;
        }
        catch (IOException exception)
        {
            detail = exception.Message;
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            detail = exception.Message;
            return false;
        }
    }

    /// <summary>流式计算 SHA-256（大写十六进制）；与下载校验、清单回填同一口径。</summary>
    public static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static long? ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var result = new List<string>();
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                {
                    result.Add(text);
                }
            }
        }

        return result;
    }
}

/// <summary>原生引擎返回的单个词元：<see cref="Start"/>/<see cref="Length"/> 一律是原文 UTF-16 偏移。</summary>
internal readonly record struct NativePhoneticToken(
    string Surface,
    int Start,
    int Length,
    string? Reading,
    bool Unknown,
    IReadOnlyList<string>? Candidates);

/// <summary>一次注音的返回：状态码 0 成功，其余为非零错误码；<see cref="Error"/> 是给界面看的消息。</summary>
internal readonly record struct NativePhoneticAnnotateResult(
    int Status,
    IReadOnlyList<NativePhoneticToken> Tokens,
    string? Error)
{
    public bool Succeeded => Status == 0;
}

/// <summary>
/// ABI v1 宿主客户端：按受控绝对路径加载已校验的语言包，调用注音/释放，
/// 输出缓冲一律交回同一 DLL 的 <c>nk_phonetics_free</c>。
/// 约定：原生库永不卸载（NativeAOT 不支持），<see cref="Release"/> 只解除词表/模型引用；
/// 解析、释放与重建由 DLL 内每语一把锁串行化，宿主不需要额外加锁。
/// </summary>
internal sealed unsafe class NativePhoneticLibrary : IDisposable
{
    /// <summary>进程内已加载的模块句柄：同一路径只加载一次，卸载不在提供的能力范围内。</summary>
    private static readonly ConcurrentDictionary<string, nint> ModuleHandles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已经加载过 DLL 的语言（重启生效判定：加载过就必须走暂存目录）。</summary>
    private static readonly ConcurrentDictionary<string, byte> LoadedLanguages = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _languageCode;
    private readonly delegate* unmanaged[Cdecl]<int> _abiVersion;
    private readonly delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, int> _annotate;
    private readonly delegate* unmanaged[Cdecl]<byte**, int*, int> _release;
    private readonly delegate* unmanaged[Cdecl]<byte*, void> _free;

    private NativePhoneticLibrary(
        string languageCode,
        nint handle)
    {
        _languageCode = languageCode;
        _abiVersion = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, NativePhoneticManifest.AbiVersionExport);
        _annotate = (delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, int>)NativeLibrary.GetExport(handle, NativePhoneticManifest.AnnotateExport);
        _release = (delegate* unmanaged[Cdecl]<byte**, int*, int>)NativeLibrary.GetExport(handle, NativePhoneticManifest.ReleaseExport);
        _free = (delegate* unmanaged[Cdecl]<byte*, void>)NativeLibrary.GetExport(handle, NativePhoneticManifest.FreeExport);
        AbiVersion = _abiVersion();
    }

    public string LanguageCode => _languageCode;

    /// <summary>DLL 自报的 ABI 版本（已与宿主期望比对通过才构造成功）。</summary>
    public int AbiVersion { get; }

    /// <summary>该语言在本进程内是否已经加载过 DLL（决定新下载的包能否即时替换）。</summary>
    public static bool IsLanguageLoaded(string languageCode) => LoadedLanguages.ContainsKey(languageCode);

    /// <summary>清空「已加载」标记：仅供自测模拟重启后的进程。</summary>
    internal static void ResetLoadedLanguagesForTests() => LoadedLanguages.Clear();

    /// <summary>按语言打开正式包：校验 → 加载 → 检查 ABI 与必需导出。失败时返回明确原因，绝不抛异常给调用方。</summary>
    public static bool TryOpen(string languageCode, out NativePhoneticLibrary? library, out NativePhoneticPackageFailure failure, out string? detail) =>
        TryOpenAt(languageCode, NativePhoneticManifest.GetPackDirectory(languageCode), out library, out failure, out detail);

    /// <summary>在指定目录打开包：正式目录、暂存目录与自测夹具共用同一条校验链。</summary>
    public static bool TryOpenAt(string languageCode, string packDirectory, out NativePhoneticLibrary? library, out NativePhoneticPackageFailure failure, out string? detail)
    {
        library = null;
        var pack = NativePhoneticManifest.GetPack(languageCode);
        if (pack is null)
        {
            failure = NativePhoneticPackageFailure.Unsupported;
            detail = null;
            return false;
        }

        var verified = NativePhoneticPackVerifier.Verify(pack, packDirectory, out detail);
        if (verified != NativePhoneticPackageFailure.None)
        {
            failure = verified;
            return false;
        }

        var libraryPath = Path.GetFullPath(NativePhoneticManifest.GetFilePath(packDirectory, pack.LibraryFileName));
        if (!ModuleHandles.TryGetValue(libraryPath, out var handle))
        {
            // 只接受校验过的绝对路径：不走同名 DLL 搜索，也不接受相对路径带来的工作目录歧义。
            if (!Path.IsPathFullyQualified(libraryPath) || !NativeLibrary.TryLoad(libraryPath, out handle))
            {
                failure = NativePhoneticPackageFailure.LoadFailed;
                detail = libraryPath;
                return false;
            }

            ModuleHandles[libraryPath] = handle;
        }

        if (!VerifyExports(handle, pack, out failure, out detail))
        {
            return false;
        }

        NativePhoneticLibrary candidate;
        try
        {
            candidate = new NativePhoneticLibrary(pack.Language, handle);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or BadImageFormatException or DllNotFoundException)
        {
            failure = NativePhoneticPackageFailure.LoadFailed;
            detail = exception.Message;
            return false;
        }

        if (candidate.AbiVersion != NativePhoneticManifest.AbiVersion)
        {
            failure = NativePhoneticPackageFailure.AbiMismatch;
            detail = $"ABI {candidate.AbiVersion}，期望 {NativePhoneticManifest.AbiVersion}";
            return false;
        }

        library = candidate;
        failure = NativePhoneticPackageFailure.None;
        detail = null;
        LoadedLanguages[pack.Language] = 1;
        return true;
    }

    /// <summary>
    /// 四项必需导出必须齐备：缺任何一个都判为不可用，而不是加载后调用时才炸在宿主里。
    /// 自测用「加载一个没有这些导出的系统 DLL」验证这条判定确实会拦住。
    /// </summary>
    internal static bool VerifyExports(nint handle, NativePhoneticPack pack, out NativePhoneticPackageFailure failure, out string? detail)
    {
        ArgumentNullException.ThrowIfNull(pack);
        foreach (var export in pack.Exports)
        {
            if (!NativeLibrary.TryGetExport(handle, export, out _))
            {
                failure = NativePhoneticPackageFailure.MissingExport;
                detail = export;
                return false;
            }
        }

        failure = NativePhoneticPackageFailure.None;
        detail = null;
        return true;
    }

    /// <summary>
    /// 注音：UTF-8 进、JSON 出。输入超限或引擎报错都返回非零状态码加消息，不抛异常。
    /// </summary>
    public NativePhoneticAnnotateResult Annotate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > NativePhoneticManifest.MaximumInputBytes)
        {
            return new NativePhoneticAnnotateResult(1, [], "输入超过长度上限");
        }

        fixed (byte* input = bytes)
        {
            byte* output = null;
            int outputLength;
            var status = _annotate(input, bytes.Length, &output, &outputLength);
            try
            {
                if (output is null || outputLength <= 0)
                {
                    return new NativePhoneticAnnotateResult(status == 0 ? 3 : status, [], status == 0 ? "引擎没有返回内容" : "引擎调用失败");
                }

                return Parse(status, new ReadOnlySpan<byte>(output, outputLength));
            }
            finally
            {
                _free(output);
            }
        }
    }

    /// <summary>释放词表、模型与分词缓存（不卸载 DLL、不强制 GC）；随后调用会自动重建。</summary>
    public int Release()
    {
        byte* output = null;
        int outputLength;
        var status = _release(&output, &outputLength);
        _free(output);
        return status;
    }

    public void Dispose() => Release();

    private static NativePhoneticAnnotateResult Parse(int status, ReadOnlySpan<byte> json)
    {
        NativePhoneticAnnotateResult result;
        try
        {
            result = ParseCore(status, json);
        }
        catch (JsonException exception)
        {
            return new NativePhoneticAnnotateResult(3, [], "返回内容不是合法 JSON：" + exception.Message);
        }

        return result;
    }

    private static NativePhoneticAnnotateResult ParseCore(int status, ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            return new NativePhoneticAnnotateResult(status == 0 ? 3 : status, [], error.GetString());
        }

        if (status != 0)
        {
            return new NativePhoneticAnnotateResult(status, [], "引擎返回错误码 " + status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var tokens = new List<NativePhoneticToken>();
        if (root.TryGetProperty("tokens", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                tokens.Add(ParseToken(item));
            }
        }

        return new NativePhoneticAnnotateResult(status, tokens, null);
    }

    private static NativePhoneticToken ParseToken(JsonElement item)
    {
        var surface = ReadString(item, "surface") ?? string.Empty;
        var reading = ReadString(item, "reading");
        var start = ReadInt32(item, "start") ?? -1;
        var length = ReadInt32(item, "length") ?? surface.Length;
        var unknown = item.TryGetProperty("unknown", out var flag) && flag.ValueKind == JsonValueKind.True;
        List<string>? candidates = null;
        if (item.TryGetProperty("candidates", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            candidates = [];
            foreach (var candidate in array.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String && candidate.GetString() is { } text)
                {
                    candidates.Add(text);
                }
            }
        }

        return new NativePhoneticToken(surface, start, length, reading, unknown, candidates);
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt32(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
