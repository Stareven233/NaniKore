using Microsoft.ML.OnnxRuntime;
using NaniKore.Ocr.Imaging;

namespace NaniKore.Ocr.Models;

internal sealed class OnnxModelRunner : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _outputNames;

    public OnnxModelRunner(string modelPath)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
        };

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputNames.Single();
        _outputNames = [.. _session.OutputNames];
    }

    public TensorOutput Run(ImageTensor tensor)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(tensor.Data, tensor.Dimensions);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(
            runOptions,
            new Dictionary<string, OrtValue>(StringComparer.Ordinal) { [_inputName] = input },
            _outputNames);

        var output = outputs.First();
        var typeAndShape = output.GetTensorTypeAndShape();
        return new TensorOutput(output.GetTensorDataAsSpan<float>().ToArray(), typeAndShape.Shape);
    }

    public string Describe()
    {
        var inputs = string.Join(", ", _session.InputMetadata.Select(DescribeNode));
        var outputs = string.Join(", ", _session.OutputMetadata.Select(DescribeNode));
        return $"inputs: {inputs}; outputs: {outputs}";
    }

    private static string DescribeNode(KeyValuePair<string, NodeMetadata> node) =>
        $"{node.Key}[{string.Join(',', node.Value.Dimensions)}] {node.Value.ElementType.Name}";

    public void Dispose() => _session.Dispose();
}

internal sealed record TensorOutput(float[] Data, long[] Dimensions);