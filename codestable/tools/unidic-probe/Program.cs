using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NMeCab;
using NMeCab.Specialized;
using NaniKore.Phonetics;
using NaniKore.Translation;

// P0 隔离探针：只接收显式词典目录，不引用生产注音链路、下载器或 ModelScope 清单。
return UniDicProbe.Run(args);

internal static partial class UniDicProbe
{
    private static readonly string[] Samples =
    [
        "日本語を勉強する",
        "食べた",
        "買い出しに行く",
        "行きます",
        "躊躇した",
        "薬の過剰摂取で亡くなった被害者",
        "先生は日本語で問題を説明している",
        "東京スカイツリーで未登録語を試す"
    ];

    private static readonly (string Text, string Expected)[] FuriganaGoldSamples =
    [
        ("食べた", "食[た]べた"),
        ("買い出しに行く", "買い出[かいだ]しに行[い]く"),
        ("行きます", "行[い]きます"),
        ("躊躇した", "躊躇[ちゅうちょ]した")
    ];

    private static readonly Dictionary<int, string> FieldNames = new()
    {
        [0] = "pos1", [1] = "pos2", [2] = "pos3", [3] = "pos4",
        [4] = "cType", [5] = "cForm", [6] = "lForm", [7] = "lemma",
        [8] = "orth", [9] = "pron", [10] = "orthBase", [11] = "pronBase",
        [12] = "goshu", [13] = "iType", [14] = "iForm", [15] = "fType",
        [16] = "fForm", [17] = "kana", [18] = "kanaBase", [19] = "form",
        [20] = "formBase", [21] = "iConType", [22] = "fConType",
        [23] = "aType", [24] = "aConType", [25] = "aModType"
    };

    public static int Run(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (!TryReadOptions(args, out var dictionary, out var text, out var json, out var ipaDictionary))
        {
            PrintUsage();
            return 2;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            ValidateDictionary(dictionary);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var workingSetBefore = process.WorkingSet64;
            double loadMs;
            double steadyStateParseMs;
            double reloadMs;
            long workingSetAfterParse;
            long workingSetAfterReload;
            List<ProbeResult> results;
            IReadOnlyList<GoldCheck> goldChecks;
            IReadOnlyList<GoldCheck> contractChecks;
            using (var tagger = MeCabTagger.Create(dictionary, null))
            {
                loadMs = stopwatch.Elapsed.TotalMilliseconds;
                results = [];
                foreach (var sample in text is null ? Samples : [text])
                {
                    results.Add(ProbeText(tagger, sample));
                }

                goldChecks = text is null ? ProbeGoldSamples(tagger) : [];
                contractChecks = text is null ? ProbeContracts(tagger) : [];

                var secondParse = Stopwatch.StartNew();
                _ = tagger.Parse(text ?? Samples[0]).Count();
                secondParse.Stop();
                steadyStateParseMs = secondParse.Elapsed.TotalMilliseconds;
                process.Refresh();
                workingSetAfterParse = process.WorkingSet64;
            }

            var reloadStopwatch = Stopwatch.StartNew();
            using (var reloadedTagger = MeCabTagger.Create(dictionary, null))
            {
                _ = reloadedTagger.Parse(text ?? Samples[0]).Count();
            }

            reloadStopwatch.Stop();
            reloadMs = reloadStopwatch.Elapsed.TotalMilliseconds;
            process.Refresh();
            workingSetAfterReload = process.WorkingSet64;

            var policyChecks = ipaDictionary is null ? [] : ProbePolicies(dictionary, ipaDictionary);
            var specialized = ProbeSpecializedTagger(dictionary);
            var output = new ProbeOutput(
                Dictionary: Path.GetFullPath(dictionary),
                DictionaryVersion: ReadVersion(dictionary),
                GenericTagger: "pass",
                SpecializedIpaTagger: specialized.Status,
                SpecializedObservation: specialized.Observation,
                LoadMilliseconds: loadMs,
                SteadyStateParseMilliseconds: steadyStateParseMs,
                ReloadMilliseconds: reloadMs,
                WorkingSetBeforeBytes: workingSetBefore,
                WorkingSetAfterParseBytes: workingSetAfterParse,
                WorkingSetAfterReloadBytes: workingSetAfterReload,
                GoldChecks: goldChecks,
                ContractChecks: contractChecks,
                PolicyChecks: policyChecks,
                Samples: results);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(output, ProbeJsonContext.Default.ProbeOutput));
            }
            else
            {
                PrintText(output);
            }

            return goldChecks.All(check => check.Passed) && contractChecks.All(check => check.Passed)
                && policyChecks.All(check => check.Passed) ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"[FAIL] UniDic 隔离探针：{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static ProbeResult ProbeText(MeCabTagger tagger, string text)
    {
        var nodes = new List<NodeResult>();
        foreach (var node in tagger.Parse(text))
        {
            var feature = node.Feature ?? string.Empty;
            var fields = ParseFeature(feature);
            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (index, name) in FieldNames)
            {
                named[name] = index < fields.Count ? fields[index] : "";
            }

            nodes.Add(new NodeResult(
                node.Surface,
                feature,
                node.GetFeatureAt(0),
                fields.Count,
                named,
                IsBoundary: string.IsNullOrEmpty(node.Surface)));
        }

        return new ProbeResult(text, nodes, AnnotateWithUniDicFields(tagger, text));
    }

    private static IReadOnlyList<GoldCheck> ProbeGoldSamples(MeCabTagger tagger)
    {
        var checks = new List<GoldCheck>(FuriganaGoldSamples.Length);
        foreach (var (text, expected) in FuriganaGoldSamples)
        {
            var actual = AnnotateWithUniDicFields(tagger, text);
            checks.Add(new GoldCheck(text, expected, actual, string.Equals(expected, actual, StringComparison.Ordinal)));
        }

        return checks;
    }

    private static string AnnotateWithUniDicFields(MeCabTagger tagger, string text, AnnotationContext? context = null, bool ipaDic = false)
    {
        var spans = new List<(int Start, int Length, string Reading)>();
        var cursor = 0;
        foreach (var node in tagger.Parse(text))
        {
            var surface = node.Surface;
            if (string.IsNullOrEmpty(surface))
            {
                continue;
            }

            var start = text.IndexOf(surface, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            cursor = start + surface.Length;
            var fields = ParseFeature(node.Feature ?? string.Empty);
            // kana 是书面振假名；pron 可能含音便长音，不能直接替代 kana。
            var reading = Field(fields, ipaDic ? 7 : 17);
            if (string.IsNullOrWhiteSpace(reading))
            {
                reading = Field(fields, ipaDic ? 8 : 9);
            }

            if (string.IsNullOrWhiteSpace(reading) || !ContainsKanji(surface))
            {
                continue;
            }

            reading = KatakanaToHiragana(reading);
            var prefixKanaCount = 0;
            while (prefixKanaCount < surface.Length && IsKana(surface[prefixKanaCount]))
            {
                prefixKanaCount++;
            }

            if (prefixKanaCount > 0)
            {
                var prefix = KatakanaToHiragana(surface[..prefixKanaCount]);
                if (reading.StartsWith(prefix, StringComparison.Ordinal))
                {
                    reading = reading[prefix.Length..];
                }
                else
                {
                    prefixKanaCount = 0;
                }
            }

            var remainingSurface = surface[prefixKanaCount..];
            var suffixKanaCount = 0;
            while (suffixKanaCount < remainingSurface.Length && IsKana(remainingSurface[^(suffixKanaCount + 1)]))
            {
                suffixKanaCount++;
            }

            if (suffixKanaCount > 0)
            {
                var suffix = KatakanaToHiragana(remainingSurface[^suffixKanaCount..]);
                if (reading.EndsWith(suffix, StringComparison.Ordinal))
                {
                    reading = reading[..^suffix.Length];
                }
                else
                {
                    suffixKanaCount = 0;
                }
            }

            var baseLength = remainingSurface.Length - suffixKanaCount;
            // 与生产底字校验及 ShouldEmit 调用顺序一致，去重按完整词面登记。
            var baseText = remainingSurface[..baseLength];
            if (baseLength > 0 && !string.IsNullOrWhiteSpace(reading)
                && baseText.All(ch => RubyTextParser.IsCjkKanji(ch) || RubyTextParser.IsKana(ch))
                && baseText.Any(RubyTextParser.IsCjkKanji)
                && (context?.ShouldEmit(surface, reading) ?? true))
            {
                spans.Add((start + prefixKanaCount, baseLength, reading));
            }
        }

        var builder = new StringBuilder(text.Length + spans.Count * 8);
        var offset = 0;
        foreach (var span in spans)
        {
            if (span.Start < offset)
            {
                continue;
            }

            builder.Append(text, offset, span.Start - offset);
            builder.Append(text, span.Start, span.Length);
            builder.Append('[').Append(span.Reading).Append(']');
            offset = span.Start + span.Length;
        }

        return builder.Append(text, offset, text.Length - offset).ToString();
    }

    private static string Field(IReadOnlyList<string> fields, int index) =>
        index < fields.Count && fields[index] != "*" ? fields[index] : string.Empty;

    private static bool IsKana(char ch) => RubyTextParser.IsKana(ch);

    private static bool ContainsKanji(string surface) => surface.Any(RubyTextParser.IsCjkKanji);

    private static string KatakanaToHiragana(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '\u30A1' and <= '\u30F6')
            {
                chars[i] = (char)(chars[i] - 0x60);
            }
        }

        return new string(chars);
    }

    private static SpecializedProbe ProbeSpecializedTagger(string dictionary)
    {
        try
        {
            using var tagger = MeCabIpaDicTagger.Create(dictionary, null);
            var node = tagger.Parse("食べた").FirstOrDefault(node => !string.IsNullOrEmpty(node.Surface));
            var observation = $"surface={node?.Surface ?? "<none>"} reading={node?.Reading ?? "<null>"} pronunciation={node?.Pronounciation ?? "<null>"}";
            return new SpecializedProbe("pass-opened-meaning-unverified", observation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new SpecializedProbe("fail", $"type={exception.GetType().Name} message={exception.Message}");
        }
    }

    private sealed record SpecializedProbe(string Status, string Observation);


    private static List<string> ParseFeature(string feature)
    {
        // UniDic 的 Feature 是带引号转义的 CSV，不是制表符分隔；不能把 node-format 输出列当原始字段。
        var fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < feature.Length; i++)
        {
            var ch = feature[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < feature.Length && feature[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(ch);
            }
        }

        fields.Add(value.ToString());
        return fields;
    }

    private static void PrintText(ProbeOutput output)
    {
        Console.WriteLine($"[meta] dict={output.Dictionary}");
        Console.WriteLine($"[meta] version={output.DictionaryVersion} generic={output.GenericTagger} specialized={output.SpecializedIpaTagger}");
        Console.WriteLine($"[specialized] {output.SpecializedObservation}");
        Console.WriteLine($"[timing] load_ms={output.LoadMilliseconds:F2} steady_parse_ms={output.SteadyStateParseMilliseconds:F2} reload_ms={output.ReloadMilliseconds:F2}");
        Console.WriteLine($"[memory] working_set_before={output.WorkingSetBeforeBytes} after_parse={output.WorkingSetAfterParseBytes} after_reload={output.WorkingSetAfterReloadBytes}");
        Console.WriteLine($"[gold] {output.GoldChecks.Count(check => check.Passed)}/{output.GoldChecks.Count} UniDic 字段适配金样本通过");
        foreach (var check in output.GoldChecks)
        {
            Console.WriteLine($"  {(check.Passed ? "PASS" : "FAIL")} {check.Text}: {check.Actual} (expected {check.Expected})");
        }
        foreach (var check in output.ContractChecks)
        {
            Console.WriteLine($"[contract] {(check.Passed ? "PASS" : "FAIL")} {check.Text}: {check.Actual} (expected {check.Expected})");
        }
        foreach (var check in output.PolicyChecks)
        {
            Console.WriteLine($"[policy] {(check.Passed ? "PASS" : "FAIL")} {check.Name}: UniDic={check.UniDic} IPAdic={check.IpaDic}");
        }
        foreach (var sample in output.Samples)
        {
            Console.WriteLine($"[sample] {sample.Text}");
            foreach (var node in sample.Nodes)
            {
                var named = string.Join(", ", node.NamedFields.Select(pair => $"{pair.Key}={pair.Value}"));
                Console.WriteLine($"  surface={node.Surface} columns={node.ColumnCount} boundary={node.IsBoundary} feature_at_0={node.FeatureAtZero} feature={node.Feature}");
                Console.WriteLine($"  fields={named}");
            }
        }
    }

    private static bool TryReadOptions(string[] args, out string dictionary, out string? text, out bool json, out string? ipaDictionary)
    {
        ipaDictionary = null;
        dictionary = "";
        text = null;
        json = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dict" when i + 1 < args.Length:
                    dictionary = args[++i];
                    break;
                case "--text" when i + 1 < args.Length:
                    text = args[++i];
                    break;
                case "--ipa-dict" when i + 1 < args.Length:
                    ipaDictionary = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    return false;
            }
        }

        return !string.IsNullOrWhiteSpace(dictionary);
    }

    private static void ValidateDictionary(string dictionary)
    {
        if (!Directory.Exists(dictionary))
        {
            throw new DirectoryNotFoundException($"词典目录不存在：{dictionary}");
        }

        foreach (var file in new[] { "sys.dic", "unk.dic", "matrix.bin", "char.bin", "dicrc" })
        {
            if (!File.Exists(Path.Combine(dictionary, file)))
            {
                throw new FileNotFoundException($"UniDic 文件缺失：{file}");
            }
        }
    }

    private static string ReadVersion(string dictionary)
    {
        var path = Path.Combine(dictionary, "version");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "<missing>";
    }

    private static void PrintUsage() => Console.Error.WriteLine("用法：P0UniDic --dict <UniDic目录> [--text <文本>] [--ipa-dict <IPAdic对照目录>] [--json]");

    private sealed partial record ProbeOutput(
        string Dictionary,
        string DictionaryVersion,
        string GenericTagger,
        string SpecializedIpaTagger,
        string SpecializedObservation,
        double LoadMilliseconds,
        double SteadyStateParseMilliseconds,
        double ReloadMilliseconds,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterParseBytes,
        long WorkingSetAfterReloadBytes,
        IReadOnlyList<GoldCheck> GoldChecks,
        IReadOnlyList<GoldCheck> ContractChecks,
        IReadOnlyList<PolicyCheck> PolicyChecks,
        IReadOnlyList<ProbeResult> Samples);

    private sealed partial record GoldCheck(string Text, string Expected, string Actual, bool Passed);


    private sealed partial record ProbeResult(string Text, IReadOnlyList<NodeResult> Nodes, string UniDicFurigana);

    private sealed partial record NodeResult(
        string Surface,
        string Feature,
        string FeatureAtZero,
        int ColumnCount,
        IReadOnlyDictionary<string, string> NamedFields,
        bool IsBoundary);

    [JsonSerializable(typeof(ProbeOutput))]
    [JsonSerializable(typeof(GoldCheck))]
    [JsonSerializable(typeof(ProbeResult))]
    [JsonSerializable(typeof(NodeResult))]
    private partial class ProbeJsonContext : JsonSerializerContext;
}
