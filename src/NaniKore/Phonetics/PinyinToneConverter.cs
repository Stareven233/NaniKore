using System.Text.RegularExpressions;

namespace NaniKore.Phonetics;

/// <summary>CC-CEDICT 声调数字 → 拼音声调符号（ri4 → rì，lü3 → lǚ，轻声去掉数字）。</summary>
internal static class PinyinToneConverter
{
    private static readonly Dictionary<char, string[]> ToneMarks = new()
    {
        ['a'] = ["ā", "á", "ǎ", "à"],
        ['e'] = ["ē", "é", "ě", "è"],
        ['i'] = ["ī", "í", "ǐ", "ì"],
        ['o'] = ["ō", "ó", "ǒ", "ò"],
        ['u'] = ["ū", "ú", "ǔ", "ù"],
        ['ü'] = ["ǖ", "ǘ", "ǚ", "ǜ"]
    };

    /// <summary>把 "Ri4 Ben3" 形式的音节转成 "rì běn"。</summary>
    public static string ConvertSyllable(string syllable)
    {
        var match = Regex.Match(syllable, @"^([A-Za-züv]+)([0-5])$");
        if (!match.Success)
        {
            return NormalizeLetters(syllable);
        }

        var letters = NormalizeLetters(match.Groups[1].Value);
        var tone = match.Groups[2].Value[0] - '0';
        if (tone is 0 or 5)
        {
            return letters;
        }

        var vowelIndex = ChooseVowelIndex(letters);
        return vowelIndex < 0
            ? letters
            : letters[..vowelIndex] + ToneMarks[letters[vowelIndex]][tone - 1] + letters[(vowelIndex + 1)..];
    }

    /// <summary>把整串拼音（可能多音节空格分隔）统一转成声调符号形态。</summary>
    public static string ConvertPhrase(string pinyin) =>
        string.Join(' ', pinyin.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ConvertSyllable));

    private static string NormalizeLetters(string syllable) =>
        syllable.ToLowerInvariant().Replace('v', 'ü');

    /// <summary>标调位选择：有 a/e 优先，其次 ou 标 o，否则标最后一个元音。</summary>
    private static int ChooseVowelIndex(string letters)
    {
        for (var i = 0; i < letters.Length; i++)
        {
            if (letters[i] is 'a' or 'e')
            {
                return i;
            }
        }

        var ou = letters.IndexOf("ou", StringComparison.Ordinal);
        if (ou >= 0)
        {
            return ou;
        }

        for (var i = letters.Length - 1; i >= 0; i--)
        {
            if (ToneMarks.ContainsKey(letters[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
