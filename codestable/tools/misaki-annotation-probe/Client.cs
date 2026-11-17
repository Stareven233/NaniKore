using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;
var failures = 0;
var checks = 0;
var libraries = new List<JsonElement>();
void Check(string name, bool passed) { checks++; if (!passed) { failures++; Console.Error.WriteLine("FAIL " + name); } }
foreach (var path in args)
{
    var native = new NativeProbe(path);
    Check("ABI=1", native.Abi == 1);
    Check("bad-pointer", native.InvalidInput() == 1);
    Check("invalid-utf8", native.InvalidUtf8() == 2);
    var ja = Path.GetFileName(path).Contains("Ja", StringComparison.Ordinal);
    var patched = Path.GetFileName(path).Contains("Patched", StringComparison.Ordinal);
    string[] samples = ja
        ? ["躊躇した。東京へ行く。今日はいい天気", "😀東京 東京\t躊躇\r\n東京", "食べた　食べた。お祝い。凍り付く", "日本語を勉強する", "2026年の未知語ヌャムャ"]
        : ["They will record a new record tomorrow.", "I read a book yesterday, but please read it again.", "The 2026 price is $50 for Dr. Smith.", "😀 read read.\tNASA U.S. ZZQX. Bob's studies abandoned.", "  [record](/zzz/) record\r\nread", "The doctor has an apple and a book.", "Blorptastic qzxwvbnm.",
            "I read every day.", "They read it yesterday.", "He can lead the way with a lead pipe.", "The water is better in the city."];
    var runs = new List<(string Text, JsonElement Result, double Milliseconds)>();
    foreach (var text in samples)
    {
        var watch = Stopwatch.StartNew();
        var result = native.Run(text);
        runs.Add((text, result, watch.Elapsed.TotalMilliseconds));
        var end = 0;
        foreach (var token in result.GetProperty("tokens").EnumerateArray())
        {
            var start = token.GetProperty("start").GetInt32();
            var surface = token.GetProperty("surface").GetString()!;
            Check("UTF16-monotonic-original-slice", start >= end && text.AsSpan(start, surface.Length).SequenceEqual(surface));
            end = start + surface.Length;
            if (!ja && token.GetProperty("reading").ValueKind == JsonValueKind.String)
            {
                var reading = token.GetProperty("reading").GetString()!;
                Check("no-kokoro-codes", !reading.Any(c => "AIOWYTᵊᵻ❓".Contains(c)));
                Check("default-is-first-candidate", token.GetProperty("candidates")[0].GetString() == reading);
                if (surface.Any(char.IsDigit) || surface is "$" or "U.S." or "ZZQX") Check("unsupported-not-annotated", false);
            }
        }
    }
    if (ja)
    {
        var first = runs[0].Result.GetProperty("tokens").EnumerateArray().ToArray();
        Check("Tokyo-reading", first.First(t => t.GetProperty("surface").GetString() == "東京").GetProperty("reading").GetString() == (patched ? "トウキョウ" : "トーキョー"));
        Check("hesitation-reading", first.First(t => t.GetProperty("surface").GetString() == "躊躇").GetProperty("reading").GetString() == (patched ? "チュウチョ" : "チューチョ"));
    }
    else
    {
        var record = runs[0].Result.GetProperty("tokens").EnumerateArray().Where(t => t.GetProperty("surface").GetString() == "record").ToArray();
        Check("record-context", record.Length == 2 && record[0].GetProperty("reading").GetString() != record[1].GetProperty("reading").GetString());
        if (patched) Check("record-word-candidates", record.All(t => t.GetProperty("candidates").GetArrayLength() == 2));
        var read = runs[1].Result.GetProperty("tokens").EnumerateArray().Where(t => t.GetProperty("surface").GetString() == "read").ToArray();
        Check("read-context", read.Length == 2 && read[0].GetProperty("reading").GetString() != read[1].GetProperty("reading").GetString());
        Check("read-ipa", read[0].GetProperty("reading").GetString() == "ˈred" && read[1].GetProperty("reading").GetString() == "ˈriːd");
        var money = runs[2].Result.GetProperty("tokens").EnumerateArray().ToArray();
        Check("digits-currency-skipped", money.Where(t => t.GetProperty("surface").GetString()!.Any(char.IsDigit) || t.GetProperty("surface").GetString() == "$").All(t => t.GetProperty("reading").ValueKind == JsonValueKind.Null));
        Check("price-ipa", money.First(t => t.GetProperty("surface").GetString() == "price").GetProperty("reading").GetString() == "ˈpraɪs");
        if (patched)
        {
            Check("listed-abbreviation", money.Any(t => t.GetProperty("surface").GetString() == "Dr." && t.GetProperty("reading").ValueKind == JsonValueKind.String));
            Check("listed-NASA", runs[3].Result.GetProperty("tokens").EnumerateArray().Any(t => t.GetProperty("surface").GetString() == "NASA" && t.GetProperty("reading").ValueKind == JsonValueKind.String));
            Check("newline-word-boundary", runs[4].Result.GetProperty("tokens").EnumerateArray().Any(t => t.GetProperty("surface").GetString() == "read"));
        }
        if (patched) Check("plain-markdown-preserved", runs[4].Result.GetProperty("tokens").EnumerateArray().All(t => t.GetProperty("raw").GetString() != "zzz"));
    }
    var beforeReload = native.Run(samples[0]);
    var warmWatch = Stopwatch.StartNew();
    for (var iteration = 0; iteration < 100; iteration++) native.Run(samples[0]);
    var warm100Ms = warmWatch.Elapsed.TotalMilliseconds;
    var release = native.Release();
    Check("release-support", release.GetProperty("supported").GetBoolean() == patched);
    if (patched) Check("old-table-collected", !release.GetProperty("oldTargetAlive").GetBoolean());
    var reloadWatch = Stopwatch.StartNew();
    var afterReload = native.Run(samples[0]);
    var reloadMs = reloadWatch.Elapsed.TotalMilliseconds;
    Check("reload-result-stable", beforeReload.GetProperty("tokens").GetRawText() == afterReload.GetProperty("tokens").GetRawText());
    // 并发请求和回收都走 DLL 内同一把锁；客户端不卸载 NativeAOT 库。
    Parallel.For(0, 6, i => { if (patched && i == 2) native.Release(); else native.Run(ja ? "東京" : "record"); });
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject(); writer.WriteString("library", Path.GetFileName(path));
        writer.WriteNumber("warm100Ms", warm100Ms); writer.WriteNumber("reloadMs", reloadMs);
        writer.WriteStartArray("runs");
        foreach (var run in runs) { writer.WriteStartObject(); writer.WriteString("text", run.Text); writer.WriteNumber("ms", run.Milliseconds); writer.WritePropertyName("result"); run.Result.WriteTo(writer); writer.WriteEndObject(); }
        writer.WriteEndArray(); writer.WritePropertyName("release"); release.WriteTo(writer); writer.WriteEndObject();
    }
    libraries.Add(JsonDocument.Parse(stream.ToArray()).RootElement.Clone());
}
using (var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true }))
{
    writer.WriteStartObject(); writer.WriteNumber("checks", checks); writer.WriteNumber("failures", failures);
    writer.WriteStartArray("libraries"); foreach (var library in libraries) library.WriteTo(writer); writer.WriteEndArray(); writer.WriteEndObject();
}
return failures == 0 ? 0 : 1;

internal sealed unsafe class NativeProbe
{
    private readonly delegate* unmanaged[Cdecl]<int> abi;
    private readonly delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, int> run;
    private readonly delegate* unmanaged[Cdecl]<byte**, int*, int> release;
    private readonly delegate* unmanaged[Cdecl]<byte*, void> free;
    public NativeProbe(string path)
    {
        var handle = NativeLibrary.Load(Path.GetFullPath(path));
        abi = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "probe_abi");
        run = (delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, int>)NativeLibrary.GetExport(handle, "probe_run");
        release = (delegate* unmanaged[Cdecl]<byte**, int*, int>)NativeLibrary.GetExport(handle, "probe_release");
        free = (delegate* unmanaged[Cdecl]<byte*, void>)NativeLibrary.GetExport(handle, "probe_free");
    }
    public int Abi => abi();
    public int InvalidInput() { byte* output; int length; return run(null, 0, &output, &length); }
    public int InvalidUtf8() { byte input = 255; byte* output = null; int length; var status = run(&input, 1, &output, &length); free(output); return status; }
    public JsonElement Run(string text)
    {
        var input = Encoding.UTF8.GetBytes(text);
        fixed (byte* pointer = input)
        {
            byte* output = null; int length;
            var status = run(pointer, input.Length, &output, &length);
            try
            {
                var result = JsonDocument.Parse(new ReadOnlySpan<byte>(output, length).ToArray()).RootElement.Clone();
                if (status != 0) throw new InvalidOperationException(result.GetRawText());
                return result;
            }
            finally { free(output); }
        }
    }
    public JsonElement Release()
    {
        byte* output = null; int length;
        var status = release(&output, &length);
        try { if (status != 0) throw new InvalidOperationException("release failed"); return JsonDocument.Parse(new ReadOnlySpan<byte>(output, length).ToArray()).RootElement.Clone(); }
        finally { free(output); }
    }
}
