using System.Text;

namespace NaniKore.Ocr.Recognition;

internal static class CtcDecoder
{
    public static RecognitionText Decode(
        ReadOnlySpan<float> probabilities,
        int timeSteps,
        int classCount,
        IReadOnlyList<string> characters)
    {
        if (classCount != characters.Count + 1)
        {
            throw new InvalidDataException(
                $"Recognizer output has {classCount} classes, but the model dictionary requires {characters.Count + 1}.");
        }

        if (probabilities.Length != checked(timeSteps * classCount))
        {
            throw new ArgumentException("Recognition probability data does not match its shape.", nameof(probabilities));
        }

        var builder = new StringBuilder();
        var previousIndex = -1;
        var selectedCount = 0;
        double confidenceSum = 0;

        for (var time = 0; time < timeSteps; time++)
        {
            var offset = time * classCount;
            var bestIndex = 0;
            var bestProbability = probabilities[offset];
            for (var character = 1; character < classCount; character++)
            {
                var probability = probabilities[offset + character];
                if (probability > bestProbability)
                {
                    bestProbability = probability;
                    bestIndex = character;
                }
            }

            // CTC 贪心解码：blank(0) 不输出；连续重复只保留第一次，但 blank 会打断重复。
            if (bestIndex != 0 && bestIndex != previousIndex)
            {
                builder.Append(characters[bestIndex - 1]);
                confidenceSum += bestProbability;
                selectedCount++;
            }

            previousIndex = bestIndex;
        }

        return new RecognitionText(
            builder.ToString(),
            selectedCount == 0 ? 0 : (float)(confidenceSum / selectedCount));
    }
}

internal sealed record RecognitionText(string Text, float Confidence);