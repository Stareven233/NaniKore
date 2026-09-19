using System.Text;

namespace NaniKore.Translation;

/// <summary>会话消息角色。</summary>
internal enum ChatRole
{
    System,
    User,
    Assistant,
}

/// <summary>多轮会话中的单条消息实体，支持图文多模态 payload。</summary>
internal sealed record ChatMessage(
    ChatRole Role,
    string Text,
    ReadOnlyMemory<byte>? ImagePng = null);

/// <summary>
/// 跨两阶段维护对话历史与图文上下文，用于第二阶段完整回放以命中大模型前缀缓存 (Prompt Cache)。
/// </summary>
internal sealed class TranslationSession
{
    private readonly StringBuilder _rawAssistantBuffer = new();

    /// <summary>阶段 1 初始化时确定的 System Prompt，阶段 2 严格保持一致以保障前缀缓存。</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>多轮对话消息序列（User/Assistant 交替）。</summary>
    public List<ChatMessage> Messages { get; } = [];

    /// <summary>本次会话的目标语言。</summary>
    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>视觉一条龙模式下的原始图像二进制，若存在则在多轮中完整保留。</summary>
    public ReadOnlyMemory<byte>? ImagePng { get; set; }

    /// <summary>初始输入的原文文本（OCR 识别或用户输入）。</summary>
    public string InitialSourceText { get; set; } = string.Empty;

    /// <summary>标记第二阶段精讲是否已生成完成，避免折叠再展开重复调用。</summary>
    public bool HasExplained { get; set; }

    /// <summary>标记第二阶段精讲是否正在流式生成中。</summary>
    public bool IsExplainInProgress { get; set; }

    public TranslationSession() { }

    public TranslationSession(
        string systemPrompt,
        string targetLanguage,
        string? sourceText,
        ReadOnlyMemory<byte>? imagePng)
    {
        SystemPrompt = systemPrompt;
        TargetLanguage = targetLanguage;
        InitialSourceText = sourceText ?? string.Empty;
        ImagePng = imagePng;

        // 阶段 1 初始 User 消息：携带文本与可能的截图
        Messages.Add(new ChatMessage(ChatRole.User, InitialSourceText, ImagePng));
    }

    /// <summary>
    /// 在阶段 1 流式接收过程中无损累积模型原始输出（含注音与译文及分隔符），
    /// 保证第二阶段前缀回放时与模型首轮输出 100% 逐字一致。
    /// </summary>
    public void AppendRawAssistantOutput(string chunk)
    {
        if (!string.IsNullOrEmpty(chunk))
        {
            _rawAssistantBuffer.Append(chunk);
        }
    }

    /// <summary>阶段 1 流式结束，将完整原始 Assistant 响应封包入会话历史。</summary>
    public void FinalizeStage1Output()
    {
        var rawText = _rawAssistantBuffer.ToString();
        // 移除可能存在的旧 Assistant 消息（例如重试场景）
        Messages.RemoveAll(m => m.Role == ChatRole.Assistant);
        if (!string.IsNullOrEmpty(rawText))
        {
            Messages.Add(new ChatMessage(ChatRole.Assistant, rawText));
        }
    }

    /// <summary>为第二阶段精讲准备追问消息。</summary>
    public void PrepareStage2Request(string explainUserPrompt)
    {
        // 若此前已有第二轮 User 追问或历史尝试，先清理确保前缀回放严格对接第一轮 Assistant
        if (Messages.Count > 2)
        {
            Messages.RemoveRange(2, Messages.Count - 2);
        }

        Messages.Add(new ChatMessage(ChatRole.User, explainUserPrompt));
    }

    /// <summary>获取当前记录的完整阶段 1 Assistant 文本。</summary>
    public string GetStage1Output() => _rawAssistantBuffer.ToString();
}

