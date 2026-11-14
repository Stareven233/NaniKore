namespace NaniKore.Phonetics;

// 工具专用路径接缝，生产标注器本身保持原样；可显式指向另一份只读中文词典。
internal static class PhoneticDependencyPaths
{
    public static string ChineseDictionaryDirectory { get; set; } = "";
    public static string[] ChineseDictionaryFiles => [.. new[] { "sys.dic", "unk.dic", "matrix.bin", "char.bin", "dicrc" }
        .Select(name => Path.Combine(ChineseDictionaryDirectory, name))];
}
