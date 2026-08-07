namespace NaniKore.Ocr.Models;

internal sealed record PaddleOcrModelConfig(
    string DetectorModelPath,
    string RecognizerModelPath,
    DetectorConfig Detector,
    RecognizerConfig Recognizer)
{
    public static PaddleOcrModelConfig Load(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        var detectorDirectory = Path.Combine(modelDirectory, "det");
        var recognizerDirectory = Path.Combine(modelDirectory, "rec");
        var detectorModelPath = RequireFile(detectorDirectory, "inference.onnx");
        var detectorConfigPath = RequireFile(detectorDirectory, "inference.yml");
        var recognizerModelPath = RequireFile(recognizerDirectory, "inference.onnx");
        var recognizerConfigPath = RequireFile(recognizerDirectory, "inference.yml");

        return new PaddleOcrModelConfig(
            detectorModelPath,
            recognizerModelPath,
            ParseDetector(File.ReadAllLines(detectorConfigPath)),
            ParseRecognizer(File.ReadAllLines(recognizerConfigPath)));
    }

    private static DetectorConfig ParseDetector(IReadOnlyList<string> lines)
    {
        var threshold = ReadFloat(lines, "thresh:");
        var boxThreshold = ReadFloat(lines, "box_thresh:");
        var unclipRatio = ReadFloat(lines, "unclip_ratio:");
        var maxCandidates = ReadInt(lines, "max_candidates:");
        var means = ReadFloatList(lines, "mean:", 3);
        var standardDeviations = ReadFloatList(lines, "std:", 3);

        return new DetectorConfig(
            threshold,
            boxThreshold,
            unclipRatio,
            maxCandidates,
            736,
            4000,
            means,
            standardDeviations);
    }

    private static RecognizerConfig ParseRecognizer(IReadOnlyList<string> lines)
    {
        var imageShape = ReadIntList(lines, "image_shape:", 3);
        if (imageShape[0] != 3)
        {
            throw new InvalidDataException($"Unsupported recognizer channel count: {imageShape[0]}");
        }

        var characters = ReadCharacterDictionary(lines);
        // PaddleOCR 的 CTCLabelDecode 默认在显式字典末尾补空格，并把 blank 放到索引 0。
        characters.Add(" ");

        return new RecognizerConfig(imageShape[1], imageShape[2], 3200, characters);
    }

    private static List<string> ReadCharacterDictionary(IReadOnlyList<string> lines)
    {
        var index = FindLine(lines, "character_dict:") + 1;
        var characters = new List<string>();

        for (; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.StartsWith("  ", StringComparison.Ordinal) &&
                line.Length > 2 &&
                !char.IsWhiteSpace(line[2]) &&
                !line.AsSpan(2).StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            const string itemPrefix = "  - ";
            if (!line.StartsWith(itemPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            characters.Add(ParseYamlScalar(line[itemPrefix.Length..]));
        }

        if (characters.Count == 0)
        {
            throw new InvalidDataException("Recognition character dictionary is empty.");
        }

        return characters;
    }

    private static string ParseYamlScalar(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return value;
    }

    private static float ReadFloat(IReadOnlyList<string> lines, string key) =>
        float.Parse(ReadScalar(lines, key), System.Globalization.CultureInfo.InvariantCulture);

    private static int ReadInt(IReadOnlyList<string> lines, string key) =>
        int.Parse(ReadScalar(lines, key), System.Globalization.CultureInfo.InvariantCulture);

    private static string ReadScalar(IReadOnlyList<string> lines, string key)
    {
        var line = lines[FindLine(lines, key)].Trim();
        return line[(line.IndexOf(':') + 1)..].Trim();
    }

    private static float[] ReadFloatList(IReadOnlyList<string> lines, string key, int count)
    {
        var values = ReadList(lines, key, count);
        return values
            .Select(value => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static int[] ReadIntList(IReadOnlyList<string> lines, string key, int count)
    {
        var values = ReadList(lines, key, count);
        return values
            .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static string[] ReadList(IReadOnlyList<string> lines, string key, int count)
    {
        var index = FindLine(lines, key) + 1;
        var values = new List<string>(count);
        for (; index < lines.Count && values.Count < count; index++)
        {
            var trimmed = lines[index].Trim();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            values.Add(trimmed[2..].Trim());
        }

        if (values.Count != count)
        {
            throw new InvalidDataException($"Expected {count} values after '{key}', got {values.Count}.");
        }

        return values.ToArray();
    }

    private static int FindLine(IReadOnlyList<string> lines, string key)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].TrimStart().StartsWith(key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidDataException($"Missing '{key}' in PaddleOCR model configuration.");
    }

    private static string RequireFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Required PP-OCRv6 model file is missing: {path}", path);
    }
}

internal sealed record DetectorConfig(
    float Threshold,
    float BoxThreshold,
    float UnclipRatio,
    int MaxCandidates,
    int LimitSideLength,
    int MaximumSideLength,
    float[] Means,
    float[] StandardDeviations);

internal sealed record RecognizerConfig(
    int ImageHeight,
    int BaseImageWidth,
    int MaximumImageWidth,
    IReadOnlyList<string> Characters);