using NMeCab;
using NaniKore.Phonetics;
using NaniKore.Translation;

internal static partial class UniDicProbe
{
    private static IReadOnlyList<GoldCheck> ProbeContracts(MeCabTagger tagger)
    {
        var checks = new List<GoldCheck>();
        var eat = ProbeText(tagger, "食べた").Nodes.First(node => node.Surface == "食べ");
        Check("fields/column-count", "26", eat.ColumnCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check("fields/lemma-pron-pronBase-kana-accent", "食べる|タベ|タベル|タベ|2|C1",
            string.Join('|', new[] { "lemma", "pron", "pronBase", "kana", "aType", "aConType" }.Select(key => eat.NamedFields[key])));
        var hesitate = ProbeText(tagger, "躊躇した").Nodes.First(node => node.Surface == "躊躇");
        Check("fields/written-vs-pronounced", "チュウチョ|チューチョ",
            $"{hesitate.NamedFields["kana"]}|{hesitate.NamedFields["pron"]}");
        var intake = ProbeText(tagger, "摂取").Nodes.First(node => node.Surface == "摂取");
        Check("fields/quoted-accent-csv", "1,0|C1", $"{intake.NamedFields["aType"]}|{intake.NamedFields["aConType"]}");
        Check("unknown/no-fabricated-reading", "これはiPhoneです", AnnotateWithUniDicFields(tagger, "これはiPhoneです"));
        Check("fields/missing-value", "", Field(["*"], 0) + Field([], 17));

        // 这里断言已知缺陷可以复现，绝非把旧字符串的展示歧义判定为可发布。
        var annotated = AnnotateWithUniDicFields(tagger, "日本語", new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false)));
        var ruby = RubyTextParser.Parse(annotated).Single(segment => segment.IsRuby);
        Check("known-gap/partial-token-ruby-base", "日本語[ご]|日本語|ご", $"{annotated}|{ruby.Text}|{ruby.Ruby}");
        return checks;

        void Check(string name, string expected, string actual) =>
            checks.Add(new GoldCheck(name, expected, actual, string.Equals(expected, actual, StringComparison.Ordinal)));
    }
}
