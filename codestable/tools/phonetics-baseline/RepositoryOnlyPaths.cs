namespace NaniKore.Ocr.Models;

// 隔离工具不链接 OCR 宿主；仓库定位由链接的生产 PhoneticDependencyPaths 完成。
// 禁止发布态回落，避免误读 EXE 附近的另一份资源而报告通过。
internal static class OcrDependencyPaths
{
    public static string ApplicationDependenciesDirectory =>
        throw new InvalidOperationException("仅支持仓库内运行；未找到 codestable/deps 标记。");
}
