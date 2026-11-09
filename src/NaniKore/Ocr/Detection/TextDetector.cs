using NaniKore.Ocr.Imaging;
using NaniKore.Ocr.Models;
using SkiaSharp;

namespace NaniKore.Ocr.Detection;

internal sealed class TextDetector : IDisposable
{
    private readonly DetectorConfig _config;
    private readonly OnnxModelRunner _model;

    public TextDetector(string modelPath, DetectorConfig config)
    {
        _config = config;
        _model = new OnnxModelRunner(modelPath);
    }

    public IReadOnlyList<DetectionBox> Detect(SKBitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var input = TensorImageConverter.CreateDetectorTensor(image, _config);
        using var output = _model.Run(input);
        if (output.Dimensions.Length < 2)
        {
            throw new InvalidDataException(
                $"Unexpected detector output shape: [{string.Join(',', output.Dimensions)}]");
        }

        var mapHeight = checked((int)output.Dimensions[^2]);
        var mapWidth = checked((int)output.Dimensions[^1]);
        var mapLength = checked(mapWidth * mapHeight);
        if (output.Data.Length < mapLength)
        {
            throw new InvalidDataException("Detector output contains less data than its declared shape.");
        }

        return DetectionPostprocessor.Extract(
            output.Data[..mapLength],
            mapWidth,
            mapHeight,
            new SKSizeI(image.Width, image.Height),
            _config);
    }

    public string DescribeModel() => _model.Describe();

    public void Dispose() => _model.Dispose();
}
