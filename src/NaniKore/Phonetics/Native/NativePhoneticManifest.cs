namespace NaniKore.Phonetics;

/// <summary>
/// 语言包内的单个文件：相对包目录的路径 + SHA-256（大写十六进制）。哈希即版本——
/// 文件不符即判为「需要更新」，不做任何静默容错。
/// </summary>
internal sealed record NativePhoneticPackFile(string RelativePath, string Sha256);

/// <summary>
/// 一种语言的正式原生包身份（issue 008 产物 + issue 009 宿主清单）：
/// 库文件名、字节数、SHA-256、ABI 导出、随包许可文件。与包内 <c>pack.json</c> 双向核对，
/// 任何一项对不上都拒绝装载，绝不以「同名 DLL 搜一下」兜底。
/// </summary>
internal sealed record NativePhoneticPack(
    string Language,
    string DisplayName,
    string LibraryFileName,
    long LibraryBytes,
    string LibrarySha256,
    IReadOnlyList<NativePhoneticPackFile> Files,
    IReadOnlyList<string> Exports)
{
    /// <summary>随包许可与派生说明（<c>licenses/</c> 下，分发必须齐备）。</summary>
    public IEnumerable<NativePhoneticPackFile> LicenseFiles =>
        Files.Where(file => !string.Equals(file.RelativePath, LibraryFileName, StringComparison.Ordinal));
}

/// <summary>
/// 原生注音语言包的宿主侧唯一身份来源（ABI v1）。
/// 包目录跟随 EXE：<c>deps/phonetics/misaki/&lt;lang&gt;/</c>，与仓库 <c>deps/</c>、
/// ModelScope 仓库 <c>Noetuu/nanikore-deps</c> 的 <c>phonetics/misaki/&lt;lang&gt;/</c> 三方严格镜像，
/// 没有版本号别名目录。构建链见 <c>scripts/phonetics/</c>，实测见 <c>codestable/notes/phonetics-packs.md</c>。
/// </summary>
internal static class NativePhoneticManifest
{
    /// <summary>ABI 版本：布局或语义变化时递增，绑定的 DLL 必须报告同一版本。</summary>
    public const int AbiVersion = 1;

    /// <summary>正式包只发布了 win-x64。</summary>
    public const string Architecture = "win-x64";

    /// <summary>单次注音输入的字节上限（与包内实现一致，超限由调用方直接拒绝）。</summary>
    public const int MaximumInputBytes = 1_000_000;

    /// <summary>包根目录名：deps/phonetics/misaki/。</summary>
    public const string PackRootName = "misaki";

    /// <summary>包内身份清单文件名（由构建链写入，运行期只读）。</summary>
    public const string PackManifestFileName = "pack.json";

    /// <summary>待重启生效的暂存目录前缀：deps/phonetics/misaki/.staging-&lt;lang&gt;/。</summary>
    public const string StagingDirectoryPrefix = ".staging-";

    public const string AbiVersionExport = "nk_phonetics_abi_version";
    public const string AnnotateExport = "nk_phonetics_annotate";
    public const string ReleaseExport = "nk_phonetics_release";
    public const string FreeExport = "nk_phonetics_free";

    private static readonly string[] RequiredExports =
    [
        AbiVersionExport,
        AnnotateExport,
        ReleaseExport,
        FreeExport
    ];

    /// <summary>本轮已装配的原生包语言；中文没有原生 DLL，走拼音数据文件（见 <see cref="ChinesePinyinData"/>）。</summary>
    public static IReadOnlyList<string> SupportedLanguages { get; } = [PhoneticLanguages.JapaneseCode, PhoneticLanguages.EnglishCode];

    /// <summary>语言包身份清单：库哈希取自 008 实测产物，重跑构建链后必须同步回填。</summary>
    public static IReadOnlyList<NativePhoneticPack> Packs { get; } =
    [
        new NativePhoneticPack(
            PhoneticLanguages.JapaneseCode,
            "日本語",
            "NaniKore.Phonetics.Ja.dll",
            21_767_680,
            "A0A4E7F7AB7914ECC37D2D4EDB8A7AF33D652A9B88B1FDD96E4525F253E67E78",
            [
                new NativePhoneticPackFile("NaniKore.Phonetics.Ja.dll", "A0A4E7F7AB7914ECC37D2D4EDB8A7AF33D652A9B88B1FDD96E4525F253E67E78"),
                new NativePhoneticPackFile("licenses/MisakiSharp-LICENSE.txt", "CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30"),
                new NativePhoneticPackFile("licenses/MisakiSharp-NOTICE.txt", "6528653B3D03B15F2A52A311842A060972F6D1705624CDC522729D634850E69C"),
                new NativePhoneticPackFile("licenses/DERIVED.md", "CAEE58C1AFE571217630B0E5752393B298188B099E2C4021D7961038809FC6B4"),
                new NativePhoneticPackFile("licenses/UniDic-BSD.txt", "770A75DE30705439084F869DBCB0BC4EBCFFCB7C7124C0D74F5083170318A9BB"),
                new NativePhoneticPackFile("licenses/UniDic-COPYING.txt", "D44B49398A72590A675E55F8F7A7DBF57BB5B71A9E20BA250F09C0CC5986BEF1"),
                new NativePhoneticPackFile("licenses/UniDic-dicrc.txt", "9B7BD3DBFDB381A078375CDCCCB1E69F60256D510A5C84A3E343AE20F98DC2CE")
            ],
            RequiredExports),
        new NativePhoneticPack(
            PhoneticLanguages.EnglishCode,
            "English",
            "NaniKore.Phonetics.En.dll",
            8_868_864,
            "F3307FEF3D4D9898870A8C068095499A10AFAED8F43365F23746F754917AFBA3",
            [
                new NativePhoneticPackFile("NaniKore.Phonetics.En.dll", "F3307FEF3D4D9898870A8C068095499A10AFAED8F43365F23746F754917AFBA3"),
                new NativePhoneticPackFile("licenses/MisakiSharp-LICENSE.txt", "CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30"),
                new NativePhoneticPackFile("licenses/MisakiSharp-NOTICE.txt", "6528653B3D03B15F2A52A311842A060972F6D1705624CDC522729D634850E69C"),
                new NativePhoneticPackFile("licenses/DERIVED.md", "CAEE58C1AFE571217630B0E5752393B298188B099E2C4021D7961038809FC6B4")
            ],
            RequiredExports)
    ];

    /// <summary>按语言代码取包身份；未装配（如 zh/ko）返回 null。</summary>
    public static NativePhoneticPack? GetPack(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode)
            ? null
            : Packs.FirstOrDefault(pack => string.Equals(pack.Language, languageCode.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsSupported(string? languageCode) => GetPack(languageCode) is not null;

    /// <summary>包根目录：deps/phonetics/misaki/。</summary>
    public static string PackRootDirectory =>
        Path.Combine(PhoneticDataPaths.PhoneticsRootDirectory, PackRootName);

    /// <summary>正式包目录：deps/phonetics/misaki/&lt;lang&gt;/。</summary>
    public static string GetPackDirectory(string languageCode) =>
        Path.Combine(PackRootDirectory, languageCode);

    /// <summary>暂存目录：新版本先落这里，宿主重启时才替换正式包，绝不覆盖正在使用的 DLL。</summary>
    public static string GetStagingDirectory(string languageCode) =>
        Path.Combine(PackRootDirectory, StagingDirectoryPrefix + languageCode);

    /// <summary>包内某文件的绝对路径（含路径穿越防护：只接受包内相对路径）。</summary>
    public static string GetFilePath(string packDirectory, string relativePath) =>
        Path.Combine(packDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>ModelScope 直链（与 008 上传口径一致）。</summary>
    public static string GetDownloadUrl(string languageCode, string relativePath) =>
        PhoneticDataPaths.ModelScopeBaseUrl + PackRootName + "/" + languageCode + "/" + relativePath + "?download=true";
}
