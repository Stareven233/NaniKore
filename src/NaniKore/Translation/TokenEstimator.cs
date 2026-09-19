namespace NaniKore.Translation;

/// <summary>
/// 当大模型 API 未在响应流中返回真实 usage（如部分第三方反代或非标端点）时的降级 Token 估算器。
/// </summary>
internal static class TokenEstimator
{
    /// <summary>
    /// 结合 CJK 汉字/假名、拉丁单词与视觉图像输入综合估算 Token 数量。
    /// </summary>
    public static int Estimate(string? text, bool hasImage = false)
    {
        if (string.IsNullOrEmpty(text) && !hasImage)
        {
            return 0;
        }

        var tokens = 0;
        if (!string.IsNullOrEmpty(text))
        {
            var cjkCount = 0;
            var asciiWordCount = 0;
            var inWord = false;

            foreach (var c in text)
            {
                // CJK 统一表意文字、平假名、片假名、谚文等中日韩文字
                if ((c >= 0x4E00 && c <= 0x9FFF) ||
                    (c >= 0x3040 && c <= 0x309F) ||
                    (c >= 0x30A0 && c <= 0x30FF) ||
                    (c >= 0xAC00 && c <= 0xD7AF))
                {
                    if (inWord)
                    {
                        asciiWordCount++;
                        inWord = false;
                    }
                    cjkCount++;
                }
                else if (char.IsWhiteSpace(c) || char.IsPunctuation(c))
                {
                    if (inWord)
                    {
                        asciiWordCount++;
                        inWord = false;
                    }
                }
                else
                {
                    inWord = true;
                }
            }

            if (inWord)
            {
                asciiWordCount++;
            }

            // CJK 单字约折算 1.1~1.3 个 Token，英文单词约折算 1.3 个 Token
            tokens = (int)Math.Ceiling(cjkCount * 1.1 + asciiWordCount * 1.3);
        }

        if (hasImage)
        {
            // 选区截图进入多模态大模型时通常经分块编码，单张约折算 85~250 tokens
            tokens += 120;
        }

        return Math.Max(1, tokens);
    }
}

