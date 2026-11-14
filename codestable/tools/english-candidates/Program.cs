using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NaniKore.Phonetics;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("用法：dotnet run --project codestable/tools/english-candidates -- <本地cmudict.dict>");
    return 2;
}
var source = Path.GetFullPath(args[0]);
using var stream = File.OpenRead(source);
Console.WriteLine($"source={source} bytes={stream.Length} sha256={Convert.ToHexString(SHA256.HashData(stream))}");
var watch = Stopwatch.StartNew();
var lines = File.ReadAllLines(source);
var entries = CmuDictionarySourceParser.Parse(lines).ToArray();
var index = EnglishPronunciationIndex.FromCmuEntries(entries, ArpabetIpaConverter.Convert);
watch.Stop();
var groups = entries.GroupBy(e => e.Word).ToArray();
var checks = 0;
var failures = 0;
void Check(string name, bool passed)
{
    checks++;
    if (!passed) failures++;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
}
// 独立统计输入中的多字母源行，不以解析器自己的输出数量自证完整。
var eligible = lines.Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
    .Where(parts => parts.Length >= 2 && !parts[0].StartsWith('#') && !parts[0].StartsWith(";;;", StringComparison.Ordinal))
    .Count(parts => parts[0].Split('(')[0].Length >= 2);
Check("多字母源行完整进入解析层", eligible == entries.Length);
Check("IPA转换不丢候选", groups.All(g => index.LookupAll(g.Key).Count == g.Count()));
Check("来源词面与编号无冲突", entries.Select(e => (e.Word, e.Variant)).Distinct().Count() == entries.Length);
Check("每个词面恰有一个主项", groups.All(g => g.Count(e => e.IsDefault) == 1));
Check("所有默认项仍等于源编号1", groups.All(g => index.Lookup(g.Key)?.Ipa == ArpabetIpaConverter.Convert(g.Single(e => e.IsDefault).Arpabet)));
Check("行尾来源标签不混入ARPAbet", entries.All(e => !e.Arpabet.Contains('#')));
foreach (var (word, expected) in new (string, string[])[]
{
    ("lead", ["L EH1 D", "L IY1 D"]),
    ("record", ["R AH0 K AO1 R D", "R EH1 K ER0 D", "R IH0 K AO1 R D"]),
    ("read", ["R EH1 D", "R IY1 D"]),
    ("wind", ["W AY1 N D", "W IH1 N D"]),
    ("bass", ["B AE1 S", "B EY1 S"]),
    ("abandoned", ["AH0 B AE1 N D AH0 N D"])
})
{
    var actual = entries.Where(e => e.Word == word).OrderBy(e => e.Variant).ToArray();
    Check($"固定源读音 {word}", actual.Select(e => e.Arpabet).SequenceEqual(expected));
    foreach (var candidate in index.LookupAll(word))
        Console.WriteLine($"  {word} variant={candidate.Variant} default={candidate.IsDefault} ipa={candidate.Ipa}");
}
Check("lead两个固定IPA", index.LookupAll("LEAD").Select(c => c.Ipa).SequenceEqual(["ˈled", "ˈliːd"]));
Check("read两个固定IPA", index.LookupAll("read").Select(c => c.Ipa).SequenceEqual(["ˈred", "ˈriːd"]));
var orphan = EnglishPronunciationIndex.FromCmuEntries(CmuDictionarySourceParser.Parse(["orphan(2) AO1 R F AH0 N"]), ArpabetIpaConverter.Convert);
Check("缺失默认项不提升备选", orphan.LookupAll("orphan").Count == 1 && orphan.Lookup("orphan") is null);
Console.WriteLine($"lines={lines.Length} eligible={eligible} entries={entries.Length} words={groups.Length} numbered={entries.Count(e => !e.IsDefault)} multiWords={groups.Count(g => g.Count() > 1)}");
Console.WriteLine($"parse-and-index-ms={watch.Elapsed.TotalMilliseconds:F3} managed-bytes={GC.GetTotalMemory(false)} working-set={Environment.WorkingSet}");
Console.WriteLine("LIMIT: 单字母词沿用既有过滤；编号仅表示源顺序，没有词义/词性/口音标签。默认项不根据句子变化。正式SQLite、屈折回退、UI、分发与宿主AOT未覆盖。");
Console.WriteLine($"SUMMARY checks={checks} failures={failures}");
return failures == 0 ? 0 : 1;
