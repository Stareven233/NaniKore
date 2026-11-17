using NMeCab;
using NaniKore.Phonetics;

internal static partial class UniDicProbe
{
    private sealed record PolicyObservation(int WordCount, bool AnnotateAll, string Annotated);
    private sealed record PolicyCheck(string Name, string Text, PolicyObservation UniDic, PolicyObservation IpaDic, bool Passed);

    private static IReadOnlyList<PolicyCheck> ProbePolicies(string dictionary, string ipaDictionary)
    {
        using var uniDic = MeCabTagger.Create(dictionary, null);
        using var ipaDic = MeCabTagger.Create(ipaDictionary, null);
        var checks = new List<PolicyCheck>();
        // 9/10 词边界使用相同词面，既验证阈值，也验证短文本豁免不绕过去重。
        foreach (var count in new[] { 9, 10 })
        {
            var text = string.Join("", Enumerable.Repeat("食べた", count));
            Add($"threshold-{count}", text, true, true, false,
                value => value.WordCount == count && value.AnnotateAll == (count < 10)
                    && value.Annotated.Count(ch => ch == '[') == (count < 10 ? 1 : 0));
        }

        Add("short-gold", "買い出しに行く", true, true, false,
            value => value.AnnotateAll && value.Annotated == "買い出[かいだ]しに行[い]く");
        Add("repeat", "躊躇した。躊躇した", true, true, false,
            value => value.Annotated == "躊躇[ちゅうちょ]した。躊躇した");
        Add("disabled", "躊躇した", false, true, false,
            value => value.Annotated == "躊躇した");
        Add("long-filter", "先生は日本語で問題を説明している。出来る。躊躇した", true, true, true,
            value => !value.AnnotateAll && !value.Annotated.Contains("先生[")
                && !value.Annotated.Contains("日本語[") && value.Annotated.Contains("躊躇[ちゅうちょ]"));
        Add("stopwords-only", "先生は日本語で問題を説明している。出来る。躊躇した", true, false, true,
            value => !value.AnnotateAll && !value.Annotated.Contains("出来["));
        Add("production-long-sample", "薬の過剰摂取で亡くなった被害者。その原因は致死量を超える6錠の睡眠薬", true, true, false,
            value => value.WordCount >= 10 && !value.AnnotateAll && value.Annotated.Contains('['));
        var boundaryText = "先生は日本語で問題を説明している。出来る。躊躇した。食べた。食べた。食べた";
        var uniBoundary = Observe(uniDic, boundaryText, false, true, true, false);
        var ipaBoundary = Observe(ipaDic, boundaryText, true, true, true, false);
        checks.Add(new PolicyCheck("segmentation-threshold-drift", boundaryText, uniBoundary, ipaBoundary,
            uniBoundary.WordCount == 10 && !uniBoundary.AnnotateAll
            && ipaBoundary.WordCount == 9 && ipaBoundary.AnnotateAll
            && !uniBoundary.Annotated.Contains("食[") && ipaBoundary.Annotated.Contains("食[た]")));
        return checks;

        void Add(string name, string text, bool enabled, bool rareOnly, bool forceLong, Func<PolicyObservation, bool> verify)
        {
            var uni = Observe(uniDic, text, false, enabled, rareOnly, forceLong);
            var ipa = Observe(ipaDic, text, true, enabled, rareOnly, forceLong);
            // 两套引擎分别满足策略契约；输出差异留作迁移证据，不把词元相同当作前提。
            checks.Add(new PolicyCheck(name, text, uni, ipa, verify(uni) && verify(ipa)));
        }
    }

    private static PolicyObservation Observe(MeCabTagger tagger, string text, bool ipaDic, bool enabled, bool rareOnly, bool forceLong)
    {
        var count = tagger.Parse(text).Count(node => !string.IsNullOrEmpty(node.Surface) && ContainsKanji(node.Surface));
        var profile = AnnotationProfile.Load("ja", enabled, rareOnly, forceLong ? int.MaxValue : count);
        var annotated = AnnotateWithUniDicFields(tagger, text, new AnnotationContext(profile), ipaDic);
        return new PolicyObservation(count, profile.AnnotateAll, annotated);
    }
}
