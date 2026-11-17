using NaniKore.Ocr.Models;

namespace NaniKore.Phonetics;

/// <summary>
/// 注音数据在本地磁盘的唯一定位：跟随 EXE 的 deps/phonetics/，与仓库 deps/ 目录、
/// ModelScope 仓库 Noetuu/nanikore-deps 的 phonetics/ 子树三方严格镜像，无版本号别名目录。
/// </summary>
internal static class PhoneticDataPaths
{
    /// <summary>ModelScope phonetics/ 子树的直链前缀；各数据包拼上自己的相对路径即为下载地址。</summary>
    public const string ModelScopeBaseUrl =
        "https://modelscope.cn/models/Noetuu/nanikore-deps/resolve/master/phonetics/";

    /// <summary>
    /// 数据根目录：优先指向仓库检出的 deps/phonetics/（开发期产物与 gitignore 的开发态来源），
    /// 找不到仓库标记时回落到跟随 EXE 的 deps/phonetics/（发布态）。
    /// </summary>
    public static string PhoneticsRootDirectory { get; } = ResolvePhoneticsRoot();

    /// <summary>中文拼音运行期数据（pypinyin + CC-CEDICT + Unihan 合并表，见 <see cref="ChinesePinyinDataBuilder"/>）。</summary>
    public static string ChinesePinyinDataFile => CombineRelative(ChinesePinyinData.LocalRelativePath);

    /// <summary>把相对 phonetics/ 的路径拼成本地绝对路径。</summary>
    public static string CombineRelative(string relativePath) =>
        Path.Combine(PhoneticsRootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

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
}
