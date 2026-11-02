using NaniKore.Ocr.Imaging;
using NaniKore.Ocr.Models;
using SkiaSharp;

namespace NaniKore.Ocr.Recognition;

internal sealed class TextRecognizer : IDisposable
{
    private readonly RecognizerConfig _config;
    private readonly OnnxModelRunner _model;

    public TextRecognizer(string modelPath, RecognizerConfig config)
    {
        _config = config;
        _model = new OnnxModelRunner(modelPath);
    }

    public RecognitionText Recognize(SKBitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var input = TensorImageConverter.CreateRecognizerTensor(image, _config);
        var output = _model.Run(input);
        if (output.Dimensions.Length != 3 || output.Dimensions[0] != 1)
        {
            throw new InvalidDataException(
                $"Unexpected recognizer output shape: [{string.Join(',', output.Dimensions)}]");
        }

        var timeSteps = checked((int)output.Dimensions[1]);
        var classCount = checked((int)output.Dimensions[2]);
        return CtcDecoder.Decode(output.Data, timeSteps, classCount, _config.Characters);
    }

    public string DescribeModel() => _model.Describe();

    public void Dispose() => _model.Dispose();
}
