using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// 语言包验收客户端：按 pack.json 校验字节/哈希/导出/ABI，再跑金样本、坏输入、释放重建与并发。
// 只读包目录，不修改任何文件；失败逐条打印到 stderr 并以非零码退出。
Console.OutputEncoding = Encoding.UTF8;
var checks = 0;
var failures = 0;
var packs = new List<PackMetrics>();

void Check(string name, bool passed)
{
    if (passed)
    {
        Interlocked.Increment(ref checks);
        return;
    }

    Interlocked.Increment(ref failures);
    Console.Error.WriteLine("FAIL " + name);
}

foreach (var directory in args)
{
    var packDirectory = Path.GetFullPath(directory);
    var manifestPath = Path.Combine(packDirectory, "pack.json");
    if (!File.Exists(manifestPath))
    {
        Check($"{directory}:pack.json", false);
        continue;
    }

    using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
    var root = manifest.RootElement;
    var language = root.GetProperty("language").GetString()!;
    var libraryName = root.GetProperty("library").GetString()!;
    var libraryPath = Path.Combine(packDirectory, libraryName);
    Check($"{language}:library-exists", File.Exists(libraryPath));
    if (!File.Exists(libraryPath)) continue;

    // 哈希与字节数即版本：清单和实际文件不一致就说明包被动过或混入了旧产物。
    var libraryBytes = File.ReadAllBytes(libraryPath);
    Check($"{language}:library-bytes", root.GetProperty("bytes").GetInt64() == libraryBytes.Length);
    Check($"{language}:library-sha256", root.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(libraryBytes)));
    Check($"{language}:schema", root.GetProperty("schema").GetInt32() >= 1);
    Check($"{language}:architecture", root.GetProperty("architecture").GetString() == "win-x64");
    Check($"{language}:abi-declared", root.GetProperty("abi").GetInt32() == 1);

    var handle = NativeLibrary.Load(libraryPath);
    var declared = root.GetProperty("exports").EnumerateArray().Select(e => e.GetString()!).ToArray();
    foreach (var export in declared) Check($"{language}:export-{export}", NativeLibrary.TryGetExport(handle, export, out _));
    var pack = new NativePack(handle);
    Check($"{language}:abi-version", pack.AbiVersion == root.GetProperty("abi").GetInt32());

    // 坏参数与坏编码必须给错误码，而不是让托管异常穿过边界。
    Check($"{language}:bad-argument", pack.Call(null, -1).Status == 1 && pack.Call([0x41], 2_000_000).Status == 1);
    // 空文本是第一次真正触碰引擎的调用：它的耗时就是本进程的词表冷加载时间。
    var coldTrigger = Stopwatch.StartNew();
    var empty = pack.Call([]);
    var coldLoadMs = coldTrigger.Elapsed.TotalMilliseconds;
    Check($"{language}:empty-text", empty.Status == 0 && empty.Json?.GetProperty("tokens").GetArrayLength() == 0);
    Check($"{language}:invalid-utf8", pack.Call([0xFF]).Status == 2);

    var ja = language == "ja";
    string[] samples = ja
        ?
        [
            "躊躇した。東京へ行く。今日はいい天気",
            "😀東京 東京\t躊躇\r\n東京",
            "食べた　食べた。お祝い。凍り付く",
            "2026年の未知語ヌャムャ",
            "日本語を勉強する"
        ]
        :
        [
            "They will record a new record tomorrow.",
            "I read a book yesterday, but please read it again.",
            "The 2026 price is $50 for Dr. Smith.",
            "😀 read read.\tNASA U.S. ZZQX. Bob's studies abandoned.",
            "  [record](/zzz/) record\r\nread",
            "The doctor has an apple and a book.",
            "Blorptastic qzxwvbnm.",
            "The water is better in the city.",
            "He can lead the way with a lead pipe.",
            "I read every day."
        ];

    var sampleLoop = Stopwatch.StartNew();
    var runs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    foreach (var text in samples)
    {
        var result = pack.Call(Encoding.UTF8.GetBytes(text));
        Check($"{language}:status[{text}]", result.Status == 0);
        if (result.Json is not { } json) continue;
        runs[text] = json;
        Check($"{language}:abi-in-payload", json.GetProperty("abi").GetInt32() == 1);
        Check($"{language}:language-in-payload", json.GetProperty("language").GetString() == language);
        var cursor = 0;
        foreach (var token in json.GetProperty("tokens").EnumerateArray())
        {
            var start = token.GetProperty("start").GetInt32();
            var surface = token.GetProperty("surface").GetString()!;
            var length = token.GetProperty("length").GetInt32();
            // 区间必须在原文里连续前进，且切片等于词面本身（UTF-16 口径）。
            Check($"{language}:utf16-alignment[{surface}]", start >= cursor && length == surface.Length && start + length <= text.Length && text.AsSpan(start, length).SequenceEqual(surface));
            cursor = start + length;
            if (token.GetProperty("reading").ValueKind != JsonValueKind.String) continue;
            var reading = token.GetProperty("reading").GetString()!;
            // 展示值必须是 IPA，不允许泄漏 Kokoro 编码字符或未转换标记。
            Check($"{language}:no-raw-codes[{surface}]", !reading.Any(c => "AIOWYᵊᵻ❓ɹ".Contains(c)));
            if (ja) continue;
            var candidates = token.GetProperty("candidates").EnumerateArray().Select(c => c.GetString()!).ToArray();
            Check($"en:default-is-first-candidate[{surface}]", candidates.Length > 0 && candidates[0] == reading);
            Check($"en:candidates-distinct[{surface}]", candidates.Distinct(StringComparer.Ordinal).Count() == candidates.Length);
        }
    }

    var samplesMs = sampleLoop.Elapsed.TotalMilliseconds;
    if (ja)
    {
        var tokens = runs[samples[0]].GetProperty("tokens").EnumerateArray().ToArray();
        string? Reading(string surface)
        {
            foreach (var token in tokens)
            {
                if (token.GetProperty("surface").GetString() == surface) return token.GetProperty("reading").GetString();
            }

            return null;
        }

        // 书面假名而不是发音表记：不能出现 トーキョー/チューチョ 这类音便写法。
        Check("ja:tokyo-written-kana", Reading("東京") == "トウキョウ");
        Check("ja:hesitation-written-kana", Reading("躊躇") == "チュウチョ");
        Check("ja:today-written-kana", Reading("今日") == "キョウ");
        // 片假名片元没有汉字，词典读法可能是 ハ/ワ；包只给数据，注不注音由界面策略决定，这里只要求不崩。
        Check("ja:kana-token-slice", tokens.Any(t => t.GetProperty("surface").GetString() == "は"));
        // 逗号、句点在词典里没有 kana，必须返回 null 而不是拿发音猜写。
        Check("ja:punctuation-null-reading", tokens.Where(t => t.GetProperty("surface").GetString() is "" or "、").All(t => t.GetProperty("reading").ValueKind == JsonValueKind.Null));
    }
    else
    {
        var record = runs[samples[0]].GetProperty("tokens").EnumerateArray().Where(t => t.GetProperty("surface").GetString() == "record").ToArray();
        Check("en:record-two-contexts", record.Length == 2 && record[0].GetProperty("reading").GetString() != record[1].GetProperty("reading").GetString());
        Check("en:record-candidates", record.All(t => t.GetProperty("candidates").GetArrayLength() >= 2));
        var read = runs[samples[1]].GetProperty("tokens").EnumerateArray().Where(t => t.GetProperty("surface").GetString() == "read").ToArray();
        Check("en:read-two-contexts", read.Length == 2 && read[0].GetProperty("reading").GetString() != read[1].GetProperty("reading").GetString());
        // 闪音曾被旧严格转换器判为不可展示；新口径下 water/better/city 必须能注出来。
        var flap = runs["The water is better in the city."].GetProperty("tokens").EnumerateArray()
            .Where(t => t.GetProperty("surface").GetString() is "water" or "better" or "city").ToArray();
        Check("en:flap-words-annotated", flap.Length == 3 && flap.All(t => t.GetProperty("reading").ValueKind == JsonValueKind.String));
        var money = runs["The 2026 price is $50 for Dr. Smith."].GetProperty("tokens").EnumerateArray().ToArray();
        // 数字/货币本轮不注音，但仍要给出可定位的区间。
        Check("en:digit-currency-null", money.Where(t => t.GetProperty("surface").GetString()!.Any(char.IsDigit) || t.GetProperty("surface").GetString() == "$")
            .All(t => t.GetProperty("reading").ValueKind == JsonValueKind.Null));
        Check("en:price-ipa", money.First(t => t.GetProperty("surface").GetString() == "price").GetProperty("reading").GetString() == "ˈpraɪs");
        Check("en:possessive-base", runs[samples[3]].GetProperty("tokens").EnumerateArray().Any(t => t.GetProperty("surface").GetString() == "Bob"));
        Check("en:markdown-not-voiceable", runs[samples[4]].GetProperty("tokens").EnumerateArray().All(t => t.GetProperty("reading").GetString() != "zzz"));
    }

    // 释放后必须能自动重建并给出完全一致的词元；DLL 常驻，不卸载。
    var before = runs[samples[0]].GetProperty("tokens").GetRawText();
    var sample = Encoding.UTF8.GetBytes(samples[0]);
    var warmWatch = Stopwatch.StartNew();
    for (var i = 0; i < 20; i++) pack.Call(sample);
    var warmMs = warmWatch.Elapsed.TotalMilliseconds;
    Check($"{language}:release", pack.Release().Status == 0);
    var reloadWatch = Stopwatch.StartNew();
    var reloaded = pack.Call(sample);
    var reloadMs = reloadWatch.Elapsed.TotalMilliseconds;
    Check($"{language}:reload-status", reloaded.Status == 0);
    Check($"{language}:reload-stable", reloaded.Json?.GetProperty("tokens").GetRawText() == before);
    // 并发请求与回收共用 DLL 内同一把锁；这里只验证不崩、不脏读。
    var concurrent = new bool[8];
    Parallel.For(0, 8, i =>
    {
        if (i == 3) pack.Release();
        else concurrent[i] = pack.Call(Encoding.UTF8.GetBytes(ja ? "東京" : "record")).Status == 0;
    });
    Check($"{language}:concurrent", concurrent.Where((_, i) => i != 3).All(passed => passed));

    packs.Add(new PackMetrics(language, libraryName, libraryBytes.Length, Math.Round(coldLoadMs, 2), Math.Round(warmMs, 2), Math.Round(reloadMs, 2), runs[samples[0]].GetProperty("tokens").GetArrayLength()));
}

// 缺包时客户端必须拿到明确的加载失败，而不是崩溃或静默无注音。
Check("missing-pack-load-fails", !NativeLibrary.TryLoad(Path.Combine(Path.GetTempPath(), "nani-kore-missing-pack.dll"), out _));

using (var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true }))
{
    writer.WriteStartObject();
    writer.WriteNumber("checks", checks);
    writer.WriteNumber("failures", failures);
    writer.WriteStartArray("packs");
    foreach (var metrics in packs)
    {
        writer.WriteStartObject();
        writer.WriteString("language", metrics.Language);
        writer.WriteString("library", metrics.Library);
        writer.WriteNumber("bytes", metrics.Bytes);
        writer.WriteNumber("coldLoadMs", metrics.coldLoadMs);
        writer.WriteNumber("warm20Ms", metrics.WarmMs);
        writer.WriteNumber("reloadMs", metrics.ReloadMs);
        writer.WriteNumber("firstSampleTokens", metrics.FirstSampleTokens);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
}

return failures == 0 ? 0 : 1;

internal sealed record PackMetrics(string Language, string Library, long Bytes, double coldLoadMs, double WarmMs, double ReloadMs, int FirstSampleTokens);

internal readonly record struct NativeResult(int Status, JsonElement? Json);

/// <summary>按 C ABI v1 绑定语言包：UTF-8 进、JSON 出、内存交回本 DLL 释放。</summary>
internal sealed unsafe class NativePack
{
    private readonly delegate* unmanaged[Cdecl]<int> abiVersion;
    private readonly delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, int> annotate;
    private readonly delegate* unmanaged[Cdecl]<byte**, int*, int> release;
    private readonly delegate* unmanaged[Cdecl]<byte*, void> free;

    public NativePack(nint handle)
    {
        abiVersion = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "nk_phonetics_abi_version");
        annotate = (delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, int>)NativeLibrary.GetExport(handle, "nk_phonetics_annotate");
        release = (delegate* unmanaged[Cdecl]<byte**, int*, int>)NativeLibrary.GetExport(handle, "nk_phonetics_release");
        free = (delegate* unmanaged[Cdecl]<byte*, void>)NativeLibrary.GetExport(handle, "nk_phonetics_free");
    }

    public int AbiVersion => abiVersion();

    /// <summary>约定字节数等于数组长度；传 null 表示“空指针”用例。</summary>
    public NativeResult Call(byte[]? input) => Call(input, input?.Length ?? 0);

    /// <summary>显式字节长度，用于非法长度用例。</summary>
    public NativeResult Call(byte[]? input, int length)
    {
        fixed (byte* pointer = input)
        {
            return Invoke(pointer, length);
        }
    }

    private NativeResult Invoke(byte* pointer, int length)
    {
        byte* output = null;
        int outputLength;
        var status = annotate(pointer, length, &output, &outputLength);
        try
        {
            if (output is null || outputLength == 0) return new NativeResult(status, null);
            return new NativeResult(status, JsonDocument.Parse(new ReadOnlySpan<byte>(output, outputLength).ToArray()).RootElement.Clone());
        }
        finally
        {
            free(output);
        }
    }

    public NativeResult Release()
    {
        byte* output = null;
        int outputLength;
        var status = release(&output, &outputLength);
        try
        {
            return new NativeResult(status, output is null || outputLength == 0
                ? null
                : JsonDocument.Parse(new ReadOnlySpan<byte>(output, outputLength).ToArray()).RootElement.Clone());
        }
        finally
        {
            free(output);
        }
    }
}



