using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MisakiSharp;

// 隔离 ABI 探针：UTF-8 输入按字节计数，结果内所有偏移均为原文 UTF-16，所有输出必须交回本 DLL 释放。
public static unsafe class NativeExports
{
    private static readonly Lock Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
#if !JAPANESE
    private static EnglishG2P engine;
    private static EnglishG2P Engine => engine ??= new EnglishG2P(
#if NO_FALLBACK
        espeakFallback: _ => null,
#endif
        flapsToT: false);
#endif

    [UnmanagedCallersOnly(EntryPoint = "probe_abi", CallConvs = [typeof(CallConvCdecl)])]
    public static int Abi() => 1;

    [UnmanagedCallersOnly(EntryPoint = "probe_run", CallConvs = [typeof(CallConvCdecl)])]
    public static int Run(byte* input, int inputLength, byte** output, int* outputLength)
    {
        if (output == null || outputLength == null) return 1;
        *output = null; *outputLength = 0;
        if (input == null || inputLength < 0 || inputLength > 1_000_000) return 1;
        try
        {
            lock (Gate)
            {
                var text = Utf8.GetString(input, inputLength);
                return Write(output, outputLength, writer => Annotate(writer, text));
            }
        }
        catch (Exception error)
        {
            // 异常只能转换成错误码/消息；绝不让托管异常穿出 unmanaged 边界。
            return Write(output, outputLength, w => { w.WriteStartObject(); w.WriteString("error", error.Message); w.WriteEndObject(); }, 2);
        }
    }

    private static void Annotate(Utf8JsonWriter writer, string text)
    {
        writer.WriteStartObject(); writer.WriteNumber("heap", GC.GetTotalMemory(false));
        writer.WriteStartArray("tokens");
        var cursor = 0;
#if JAPANESE
        foreach (var token in JapaneseTagger.Parse(text))
        {
            var start = text.IndexOf(token.Surface, cursor, StringComparison.Ordinal);
            if (start < 0) throw new InvalidDataException("词元无法在原文中顺序定位");
            cursor = start + token.Surface.Length;
            writer.WriteStartObject();
            writer.WriteString("surface", token.Surface); writer.WriteString("reading", token.Reading);
            writer.WriteNumber("start", start); writer.WriteNumber("length", token.Surface.Length);
            writer.WriteBoolean("unknown", token.IsUnk); writer.WriteEndObject();
        }
#else
        foreach (var token in Engine.Phonemize(text).Tokens)
        {
            var start = text.IndexOf(token.Text, cursor, StringComparison.Ordinal);
            if (start < 0) throw new InvalidDataException("词元无法在原文中顺序定位");
            cursor = start + token.Text.Length;
            // 上游会把 emoji 拆成半个代理项；英语不认领这些词元，避免 JSON 把半字符替换成 U+FFFD。
            if (token.Text.Any(char.IsSurrogate)) continue;
            var word = token.Text;
            var length = word.Length;
            // 保持当前所有格底字口径；该分支用于验证偏移，不是生产适配器。
            if (word.Length > 3 && (word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || word.EndsWith("’s", StringComparison.OrdinalIgnoreCase)))
                word = word[..(length -= 2)];
#if PATCHED
            var rawCandidates = Engine.ProbeCandidates(word);
#else
            string[] rawCandidates = [];
#endif
            var abbreviation = word.Contains('.') || word.Length > 1 && word.All(c => c is >= 'A' and <= 'Z');
            var eligible = word.Length >= 2 && word.All(c => char.IsAsciiLetter(c) || c is '\'' or '’' or '.')
                && word.Any(char.IsAsciiLetter) && (!abbreviation || rawCandidates.Length > 0);
            var ipa = eligible ? ProbeIpa.Convert(token.Phonemes) : null;
            var candidates = ipa is null ? [] : new[] { ipa }.Concat(rawCandidates.Select(ProbeIpa.Convert).Where(p => p != null)).Distinct().ToArray();
            writer.WriteStartObject();
            writer.WriteString("surface", token.Text); writer.WriteString("raw", token.Phonemes); writer.WriteString("reading", ipa);
            writer.WriteString("tag", token.Tag); writer.WriteNumber("start", start); writer.WriteNumber("length", length);
            writer.WriteStartArray("candidates"); foreach (var candidate in candidates) writer.WriteStringValue(candidate); writer.WriteEndArray();
            writer.WriteEndObject();
        }
#endif
        writer.WriteEndArray(); writer.WriteNumber("heapAfter", GC.GetTotalMemory(false)); writer.WriteEndObject();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReleaseTables()
    {
#if PATCHED
#if JAPANESE
        return JapaneseTagger.ProbeRelease();
#else
        var weak = new WeakReference(engine);
        engine = null;
        EnglishTagger.ProbeRelease(); EnglishTokenizer.ProbeRelease(); EspeakReplay.ProbeRelease();
        return weak;
#endif
#else
        return null;
#endif
    }

    [UnmanagedCallersOnly(EntryPoint = "probe_release", CallConvs = [typeof(CallConvCdecl)])]
    public static int Release(byte** output, int* outputLength)
    {
        if (output == null || outputLength == null) return 1;
        *output = null; *outputLength = 0;
        try
        {
            lock (Gate)
            {
                var before = GC.GetTotalMemory(true);
                var weak = ReleaseTables();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var after = GC.GetTotalMemory(true);
                return Write(output, outputLength, writer =>
                {
                    writer.WriteStartObject(); writer.WriteNumber("beforeHeap", before); writer.WriteNumber("afterHeap", after);
                    writer.WriteBoolean("supported", weak != null); writer.WriteBoolean("oldTargetAlive", weak?.IsAlive ?? true);
                    writer.WriteEndObject();
                });
            }
        }
        catch { return 2; }
    }

    [UnmanagedCallersOnly(EntryPoint = "probe_free", CallConvs = [typeof(CallConvCdecl)])]
    public static void Free(byte* buffer) => NativeMemory.Free(buffer);

    private static int Write(byte** output, int* outputLength, Action<Utf8JsonWriter> write, int status = 0)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        var bytes = stream.ToArray();
        var pointer = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(pointer, bytes.Length));
        *output = pointer; *outputLength = bytes.Length;
        return status;
    }
}
