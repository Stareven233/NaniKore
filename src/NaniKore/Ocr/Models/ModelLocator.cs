namespace NaniKore.Ocr.Models;

internal static class ModelLocator
{
    private const string ModelFolderName = "pp-ocr-v6-small";

    public static string FindPaddleOcrDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("NANIKORE_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Validate(Path.GetFullPath(configured));
        }

        foreach (var root in EnumerateSearchRoots())
        {
            var directCandidate = Path.Combine(root, "models", ModelFolderName);
            if (HasRequiredFiles(directCandidate))
            {
                return directCandidate;
            }

            var siblingCandidate = Path.Combine(root, ModelFolderName);
            if (HasRequiredFiles(siblingCandidate))
            {
                return siblingCandidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"PP-OCRv6 models were not found. Put them in 'models\\{ModelFolderName}' " +
            "beside the project/application, or set NANIKORE_MODEL_DIR.");
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                if (seen.Add(directory.FullName))
                {
                    yield return directory.FullName;
                }
            }
        }
    }

    private static string Validate(string path) => HasRequiredFiles(path)
        ? path
        : throw new DirectoryNotFoundException(
            $"NANIKORE_MODEL_DIR does not contain a complete PP-OCRv6-small model: {path}");

    private static bool HasRequiredFiles(string path) =>
        File.Exists(Path.Combine(path, "det", "inference.onnx")) &&
        File.Exists(Path.Combine(path, "det", "inference.yml")) &&
        File.Exists(Path.Combine(path, "rec", "inference.onnx")) &&
        File.Exists(Path.Combine(path, "rec", "inference.yml"));
}