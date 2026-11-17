namespace NaniKore.Ocr.Models;

// 仅补齐资源路径的宿主依赖；探针不运行下载器或同文件中的词典生成入口。
internal static class OcrDependencyPaths
{
    public static string ApplicationDependenciesDirectory =>
        throw new InvalidOperationException("隔离探针仅支持仓库资源定位");
}
