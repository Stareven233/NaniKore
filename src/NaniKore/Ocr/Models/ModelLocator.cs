namespace NaniKore.Ocr.Models;

internal static class ModelLocator
{
    private static readonly string[] RequiredFiles =
    [
        "det/inference.onnx",
        "det/inference.yml",
        "rec/inference.onnx",
        "rec/inference.yml",
    ];

    private const string ModelFolderName = "pp-ocr-v6-small";

    public static string FindPaddleOcrDirectory()
    {
        var candidate = Path.Combine(
            OcrDependencyPaths.ApplicationDependenciesDirectory,
            "ocr-onnx",
            ModelFolderName);
        var missingFiles = RequiredFiles
            .Where(relativePath => !File.Exists(Path.Combine(candidate, relativePath.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();
        if (missingFiles.Length == 0)
        {
            return candidate;
        }

        throw new DirectoryNotFoundException(
            $"PP-OCRv6 models were not found in 'deps\\ocr-onnx\\{ModelFolderName}'. " +
            $"Missing: {string.Join(", ", missingFiles)}. " +
            $"Expected directory: {candidate}. Open Settings and download the local OCR dependencies.");
    }

    private static bool HasRequiredFiles(string path) => RequiredFiles.All(relativePath =>
        File.Exists(Path.Combine(path, relativePath.Replace('/', Path.DirectorySeparatorChar))));
}