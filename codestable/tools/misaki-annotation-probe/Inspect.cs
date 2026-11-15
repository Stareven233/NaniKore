using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MisakiSharp;
using Mono.Cecil;

Console.OutputEncoding = Encoding.UTF8;
var assembly = typeof(JapaneseTagger).Assembly;
if (args[0] == "prepare")
{
    var root = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.Combine(root, "resources"));
    var resources = new List<object>();
    foreach (var name in assembly.GetManifestResourceNames())
    {
        using var input = assembly.GetManifestResourceStream(name)!;
        using var output = File.Create(Path.Combine(root, "resources", name["MisakiSharp.".Length..]));
        input.CopyTo(output);
        resources.Add(new { name, bytes = input.Length });
    }
    var packages = new Dictionary<string, Func<string, bool>>
    {
        ["ja"] = n => n is "MisakiSharp.ja_lexicon.tsv.gz" or "MisakiSharp.ja_char_unk.tsv.gz" or "MisakiSharp.ja_matrix_compact.bin.gz",
        ["en-core"] = n => n.StartsWith("MisakiSharp.en_", StringComparison.Ordinal),
        ["en-full"] = n => n.StartsWith("MisakiSharp.en_", StringComparison.Ordinal) || n is "MisakiSharp.enus_espeak.tsv.gz" or "MisakiSharp.engb_espeak.tsv.gz"
    };
    var manifests = new List<object>();
    foreach (var (package, keep) in packages)
    {
        var output = Path.Combine(root, "trimmed", package);
        Directory.CreateDirectory(output);
        // 每个包都从原件重读；只改 .temp 中的副本，绝不修改 NuGet 缓存。
        using var module = ModuleDefinition.ReadModule(assembly.Location);
        foreach (var resource in module.Resources.Where(r => !keep(r.Name)).ToArray())
            module.Resources.Remove(resource);
        var path = Path.Combine(output, "MisakiSharp.dll");
        module.Write(path);
        manifests.Add(new { package, bytes = new FileInfo(path).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            resources = module.Resources.Select(r => r.Name).ToArray() });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { resources, manifests }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var jaSamples = new[] { "躊躇した。東京へ行く。今日はいい天気。", "食べた　食べた。お祝い。凍り付く。", "😀東京 東京\t躊躇\r\n東京" };
var japanese = jaSamples.Select(text => new { text, tokens = JapaneseTagger.Parse(text).Select(t => new { surface = t.Surface, reading = t.Reading, unknown = t.IsUnk }).ToArray() }).ToArray();
var en = new EnglishG2P(flapsToT: false);
var enSamples = new[]
{
    "They will record a new record tomorrow.",
    "I read a book yesterday, but please read it again.",
    "The 2026 price is $50 for Dr. Smith.",
    "😀 read read.\tNASA U.S. ZZQX. Bob's studies abandoned.",
    "  [record](/zzz/) record\r\nread"
};
var english = enSamples.Select(text => new { text, tokens = en.Phonemize(text).Tokens.Select(t => new { text = t.Text, phonemes = t.Phonemes, tag = t.Tag }).ToArray() }).ToArray();
var targets = new HashSet<string>(["record", "read", "lead", "Dr.", "dr.", "NASA", "U.S.", "ZZQX"], StringComparer.Ordinal);
var entries = new List<string>();
var symbols = new SortedSet<char>();
var converted = 0;
var rejected = 0;
var rejectedExamples = new List<string>();
using (var stream = assembly.GetManifestResourceStream("MisakiSharp.en_lexicon_us.tsv.gz")!)
using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
using (var reader = new StreamReader(gzip))
{
    while (reader.ReadLine() is { } line)
    {
        if (line.StartsWith('#')) continue;
        var fields = line.Split('\t');
        if (targets.Contains(fields[0])) entries.Add(line);
        foreach (var field in fields.Skip(1))
        {
            var value = field.Contains('=') ? field[(field.IndexOf('=') + 1)..] : field;
            foreach (var c in value) symbols.Add(c);
            if (string.IsNullOrEmpty(value)) continue;
            if (ProbeIpa.Convert(value) is null)
            {
                rejected++;
                if (rejectedExamples.Count < 12) rejectedExamples.Add(fields[0] + "=" + value);
            }
            else converted++;
        }
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { japanese, english, entries, symbols = string.Concat(symbols), ipaAudit = new { converted, rejected, rejectedExamples } }, new JsonSerializerOptions { WriteIndented = true }));
