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
        OnnxRuntimeNativeLoader.EnsureConfigured();
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
            // 截图尺寸动态变化，禁用高水位内存池和形状缓存，避免一次大图推理长期占住缓冲。
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
        };

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputNames.Single();
        _outputNames = [.. _session.OutputNames];
    }

    public TensorOutput Run(ImageTensor tensor)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(tensor.Data, tensor.Dimensions);
        using var runOptions = new RunOptions();
        var outputs = _session.Run(
            runOptions,
            new Dictionary<string, OrtValue>(StringComparer.Ordinal) { [_inputName] = input },
            _outputNames);

        try
        {
            // 输出持有原生缓冲，交由调用方在后处理结束后释放，避免复制整张概率矩阵。
            return new TensorOutput(outputs);
        }
        catch
        {
            outputs.Dispose();
            throw;
        }
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

/// <summary>持有推理输出的原生内存；Data 仅可在本对象释放前同步读取。</summary>
internal sealed class TensorOutput : IDisposable
{
    private readonly IDisposableReadOnlyCollection<OrtValue> _outputs;
    private readonly OrtValue _output;

    public TensorOutput(IDisposableReadOnlyCollection<OrtValue> outputs)
    {
        _outputs = outputs;
        _output = outputs.First();
        Dimensions = _output.GetTensorTypeAndShape().Shape;
    }

    public ReadOnlySpan<float> Data => _output.GetTensorDataAsSpan<float>();

    public long[] Dimensions { get; }

    public void Dispose() => _outputs.Dispose();
}