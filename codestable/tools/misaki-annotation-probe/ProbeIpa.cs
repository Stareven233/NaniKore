using System.Text;
using NaniKore.Phonetics;

internal static class ProbeIpa
{
    // 仅验证 en-US 到现有词典式排版的适配；无法还原的闪音/喉塞音拒绝展示，不能猜回 /t/ 或 /d/。
    public static string? Convert(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOfAny(['ɾ', 'ʔ', 'T', '❓']) >= 0) return null;
        var text = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var mapped = value[index] switch
            {
                'A' => "eɪ", 'I' => "aɪ", 'O' => "oʊ", 'W' => "aʊ", 'Y' => "ɔɪ",
                'ʤ' => "dʒ", 'ʧ' => "tʃ", 'ᵊ' => "ə", 'ᵻ' => "ɪ", 'ɐ' => "ə",
                'ɹ' => "r", 'ɛ' => "e", 'ɑ' => "ɑː", 'ɔ' => "ɔː",
                // 现有格式器把卷舌长元音作为一个音节核，必须整体映射。
                'ɜ' when index + 1 < value.Length && value[index + 1] == 'ɹ' => "ɜː",
                var c when "bdfhijklmnpstuvwzæðŋəɡɪʃʊʌʒˈˌθ .ː".Contains(c) => c.ToString(),
                _ => null
            };
            if (mapped is null) return null;
            text.Append(mapped);
        }
        return IpaTranscription.Normalize(text.ToString());
    }
}
