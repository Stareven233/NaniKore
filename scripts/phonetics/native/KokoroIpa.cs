using System.Text;
using NaniKore.Phonetics;

namespace NaniKore.Phonetics.Native;

/// <summary>
/// Kokoro/misaki 音素编码 → 可读美音 IPA。
/// 口径：只做字符级编码转换，不改词义选音；闪音 ɾ 与喉塞音 ʔ 属合法美音变体，原样保留，
/// 不猜回 /t/、/d/。未收录的字符一律判为不可展示（返回 null），绝不静默吞字符。
/// </summary>
internal static class KokoroIpa
{
    /// <summary>Kokoro 词典里实际出现的全部符号；audit 阶段据此核对映射是否覆盖全量。</summary>
    public const string Alphabet = "AIOWYbdfhijklmnpstuvwzæðŋɑɔəɛɜɡɪɹɾʃʊʌʒʔʤʧˈˌθᵊᵻ";

    /// <summary>直接透传的符号：本身在两种编码里写法一致的辅音、单元音、重音与音节点。</summary>
    private const string Passthrough = "bdfhijklmnpstuvwzæðŋəɡɪʃʊʌʒθˈˌ.ː ";

    /// <summary>转换一个词素串；任何无法解释的字符都返回 null，由调用方决定跳过。</summary>
    public static string? Convert(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var text = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var symbol = value[index];
            switch (symbol)
            {
                case 'A': text.Append("eɪ"); break;
                case 'I': text.Append("aɪ"); break;
                case 'O': text.Append("oʊ"); break;
                case 'W': text.Append("aʊ"); break;
                case 'Y': text.Append("ɔɪ"); break;
                case 'ʤ': text.Append("dʒ"); break;
                case 'ʧ': text.Append("tʃ"); break;
                // ᵊ/ɐ 是上游用来标弱化的近似符，统一落到中央元音。
                case 'ᵊ': text.Append('ə'); break;
                case 'ɐ': text.Append('ə'); break;
                case 'ᵻ': text.Append('ɪ'); break;
                // 卷舌元音在现有排版里是一个音节核，必须整体写成 ɜːr；单独的 ɜ 退化为中央元音，不丢字符。
                case 'ɜ' when index + 1 < value.Length && value[index + 1] == 'ɹ':
                    text.Append("ɜːr");
                    index++;
                    break;
                case 'ɜ': text.Append('ə'); break;
                case 'ɹ': text.Append('r'); break;
                // 词典式排版用 e 表示 DRESS，长元音补 ː。
                case 'ɛ': text.Append('e'); break;
                case 'ɑ': text.Append("ɑː"); break;
                case 'ɔ': text.Append("ɔː"); break;
                // 美音变体原样保留：闪音与喉塞音都是合法 IPA，转换器无权替换。
                case 'ɾ': text.Append('ɾ'); break;
                case 'ʔ': text.Append('ʔ'); break;
                case var other when Passthrough.Contains(other): text.Append(other); break;
                default: return null;
            }
        }

        return IpaTranscription.Normalize(text.ToString());
    }
}
