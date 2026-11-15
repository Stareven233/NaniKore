using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using MisakiSharp;

namespace NaniKore.Phonetics.Native;

/// <summary>
/// 注音语言包的生产 C ABI（v1）。约定：输入是带字节长度的 UTF-8；输出 JSON 里的 start/length
/// 一律是原文的 UTF-16 偏移；所有输出缓冲必须交回本 DLL 的 nk_phonetics_free 释放；
/// 托管异常绝不穿过 unmanaged 边界，只以错误码加 JSON 里的 error 消息返回。
/// 解析、释放与重建共用同一把语言锁，调用方因此无需额外串行化。
/// </summary>
public static unsafe class Exports
{
    /// <summary>ABI 版本：布局或语义变化时递增，宿主在初始化引擎前必须比对。</summary>
    private const int AbiVersion = 1;

    private const int StatusOk = 0;
    private const int StatusBadArgument = 1;
    private const int StatusBadText = 2;
    private const int StatusFailed = 3;

    private static readonly Lock Gate = new();

    /// <summary>严格 UTF-8：遇到非法字节抛异常，转成错误码，而不是静默替换成 U+FFFD。</summary>
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // 注音是给界面用的数据，不再二次转义 CJK/IPA，保持 UTF-8 原样与体积。
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        SkipValidation = true
    };

#if JAPANESE
    private const string Language = "ja";
#else
    private const string Language = "en";
    private static EnglishG2P? _engine;

    /// <summary>英语引擎：不回退 espeak（未收录词不猜读音），保留闪音/喉塞音原貌。</summary>
    private static EnglishG2P Engine => _engine ??= new EnglishG2P(espeakFallback: _ => null, flapsToT: false);
#endif

    [UnmanagedCallersOnly(EntryPoint = "nk_phonetics_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int AbiVersionExport() => AbiVersion;

    /// <summary>把 UTF-8 原文切成带读音的词元；失败时状态码非零，输出仍是可解析的 JSON。</summary>
    [UnmanagedCallersOnly(EntryPoint = "nk_phonetics_annotate", CallConvs = [typeof(CallConvCdecl)])]
    public static int Annotate(byte* input, int inputLength, byte** output, int* outputLength)
    {
        if (output is null || outputLength is null) return StatusBadArgument;
        *output = null;
        *outputLength = 0;
        // 空文本允许零长度且指针可为空；非零长度必须是可读缓冲。
        if (inputLength < 0 || inputLength > 1_000_000 || (input is null && inputLength > 0)) return StatusBadArgument;
        try
        {
            string text;
            try
            {
                text = inputLength == 0 ? string.Empty : Utf8.GetString(input, inputLength);
            }
            catch (DecoderFallbackException)
            {
                return Write(output, outputLength, AnnotateError, StatusBadText);
            }

            lock (Gate) return Write(output, outputLength, writer => Annotate(writer, text));
        }
        catch (Exception error)
        {
            // 任何泄漏到这里的异常都只能变成错误码加消息，绝不穿过 unmanaged 边界。
            return Write(output, outputLength, writer => WriteError(writer, error.Message), StatusFailed);
        }
    }

    /// <summary>释放本语言的词表、模型与分词缓存，只解除引用（不强制 GC、不卸载 DLL）；随后调用会自动重建。</summary>
    [UnmanagedCallersOnly(EntryPoint = "nk_phonetics_release", CallConvs = [typeof(CallConvCdecl)])]
    public static int Release(byte** output, int* outputLength)
    {
        if (output is null || outputLength is null) return StatusBadArgument;
        *output = null;
        *outputLength = 0;
        try
        {
            lock (Gate)
            {
#if JAPANESE
                JapaneseTagger.Release();
#else
                // 先丢引擎引用再清各静态表，避免重建时拿到半清空的缓存。
                _engine = null;
                EnglishTagger.Release();
                EnglishTokenizer.Release();
                EspeakReplay.Release();
#endif
                return Write(output, outputLength, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("released", true);
                    writer.WriteEndObject();
                });
            }
        }
        catch
        {
            return StatusFailed;
        }
    }

    /// <summary>释放本 DLL 分配的输出缓冲；宿主必须用它释放 nk_phonetics_annotate/release 的返回值。</summary>
    [UnmanagedCallersOnly(EntryPoint = "nk_phonetics_free", CallConvs = [typeof(CallConvCdecl)])]
    public static void Free(byte* buffer) => NativeMemory.Free(buffer);

    private static void Annotate(Utf8JsonWriter writer, string text)
    {
        writer.WriteStartObject();
        writer.WriteNumber("abi", AbiVersion);
        writer.WriteString("language", Language);
        writer.WriteStartArray("tokens");
#if JAPANESE
        var cursor = 0;
        foreach (var (surface, reading, _, unknown) in JapaneseTagger.Parse(text))
        {
            var start = Surface(text, surface, ref cursor);
            // 词典里 kana 为 * 的条目（符号、颜文字等）没有书面读音，返回 null，绝不拿发音字段猜写。
            var value = string.IsNullOrEmpty(reading) ? null : reading;
            WriteToken(writer, surface, start, value, unknown: unknown);
        }
#else
        var cursor = 0;
        foreach (var token in Engine.Phonemize(text).Tokens)
        {
            // 上游会把 emoji 拆成半个代理项；这些词元不认领，避免输出无法切片的区间。
            if (token.Text.Any(char.IsSurrogate)) continue;
            if (string.IsNullOrWhiteSpace(token.Text)) continue;
            var start = Surface(text, token.Text, ref cursor);
            var word = token.Text;
            string? reading;
            if (word.Length > 3 && (word.EndsWith("'s", StringComparison.Ordinal) || word.EndsWith("’s", StringComparison.Ordinal)))
            {
                // 所有格只给底字注音，区间不覆盖 's；读音也必须取底字本身的词典读音，
                // 不能把带 z 音的整词发音挂在底字上。
                word = word[..^2];
                reading = Engine.Candidates(word).Select(KokoroIpa.Convert).FirstOrDefault(static ipa => ipa is not null);
            }
            else
            {
                var eligible = word.Length >= 2 && word.Any(char.IsAsciiLetter) && word.All(c => char.IsAsciiLetter(c) || c is '\'' or '’' or '.');
                reading = eligible ? KokoroIpa.Convert(token.Phonemes) : null;
            }

            string[] candidates = reading is null ? [] : Candidates(word, reading);
            WriteToken(writer, word, start, reading, unknown: reading is null, candidates: candidates);
        }
#endif
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>按出现顺序在原文里定位词元；定位不到就是数据与原文不一致，宁可失败也不猜偏移。</summary>
    private static int Surface(string text, string surface, ref int cursor)
    {
        var start = text.IndexOf(surface, cursor, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException($"词元无法在原文中顺序定位：{surface}");
        cursor = start + surface.Length;
        return start;
    }

#if !JAPANESE
    /// <summary>把词典里明确收录的读音全部转成 IPA，句内读音排首位，转换失败与重复项剔除。</summary>
    private static string[] Candidates(string word, string reading)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { reading };
        var result = new List<string> { reading };
        foreach (var candidate in Engine.Candidates(word))
        {
            if (KokoroIpa.Convert(candidate) is { } converted && seen.Add(converted)) result.Add(converted);
        }

        return [.. result];
    }
#endif

    private static void WriteToken(Utf8JsonWriter writer, string surface, int start, string? reading, bool unknown, string[]? candidates = null)
    {
        writer.WriteStartObject();
        writer.WriteString("surface", surface);
        writer.WriteNumber("start", start);
        writer.WriteNumber("length", surface.Length);
        if (reading is null) writer.WriteNull("reading"); else writer.WriteString("reading", reading);
        writer.WriteBoolean("unknown", unknown);
        if (candidates is not null)
        {
            writer.WriteStartArray("candidates");
            foreach (var candidate in candidates) writer.WriteStringValue(candidate);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void AnnotateError(Utf8JsonWriter writer) => WriteError(writer, "输入不是合法的 UTF-8");

    private static void WriteError(Utf8JsonWriter writer, string message)
    {
        writer.WriteStartObject();
        writer.WriteNumber("abi", AbiVersion);
        writer.WriteString("language", Language);
        writer.WriteString("error", message);
        writer.WriteEndObject();
    }

    private static int Write(byte** output, int* outputLength, Action<Utf8JsonWriter> write, int status = StatusOk)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions)) write(writer);
        var bytes = stream.ToArray();
        var pointer = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(pointer, bytes.Length));
        *output = pointer;
        *outputLength = bytes.Length;
        return status;
    }
}
