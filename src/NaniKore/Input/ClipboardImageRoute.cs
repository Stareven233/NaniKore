namespace NaniKore.Input;

/// <summary>
/// 剪贴板/选区图像的去向（issue 019 裁决）：管线偏好优先，能力不足时明确停下，
/// 绝不静默改道——用户选了哪条管线，就按哪条链路走。
/// </summary>
internal enum ClipboardImageRoute
{
    /// <summary>vision_oneshot 且配置档声明支持视觉：PNG 直喂 provider。</summary>
    DirectFeed,

    /// <summary>local_ocr 管线：本地 PP-OCRv6 识别成文本。</summary>
    LocalOcr,

    /// <summary>vision_oneshot 但配置档不支持视觉：不猜、不偷偷 OCR，交由宿主弹提醒。</summary>
    UnsupportedVision,
}

/// <summary>按管线模式与配置档视觉能力解析图像去向；调用点因此共用同一把尺，不会各写一套判断而漂移。</summary>
internal static class ClipboardImageRouteResolver
{
    /// <summary>
    /// 未知/异常管线值按 local_ocr 处理（与截图链路一致）：保守，不把图像喂给可能不吃视觉的 provider。
    /// </summary>
    public static ClipboardImageRoute Resolve(string? pipelineMode, bool supportsVision) =>
        string.Equals(pipelineMode, "vision_oneshot", StringComparison.OrdinalIgnoreCase)
            ? supportsVision ? ClipboardImageRoute.DirectFeed : ClipboardImageRoute.UnsupportedVision
            : ClipboardImageRoute.LocalOcr;
}
