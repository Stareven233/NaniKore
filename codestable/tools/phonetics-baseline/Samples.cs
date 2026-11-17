using NaniKore.Phonetics;

namespace PhoneticsBaseline;

internal sealed record ExpectedSpan(int Start, int Length, string Reading);
internal sealed record Sample(string Id, string Language, string Text, string Mode, string Basis,
    string? ExpectedComposed = null, ExpectedSpan[]? ExpectedSpans = null, string? Unverified = null);

internal static class Samples
{
    // 固定值先于实测定义；selftest 金样本是回归契约，不冒充新增语言学核验。
    public static readonly Sample[] All =
    [
        new("ja-chucho", "ja", "躊躇した", "rare", "SelfTest.TestJapaneseFuriganaDictionary", "躊躇[ちゅうちょ]した", [new(0, 2, "ちゅうちょ")]),
        new("ja-okurigana", "ja", "食べた", "all", "SelfTest.TestJapaneseFuriganaDictionary", "食[た]べた", [new(0, 1, "た")]),
        new("ja-repeat", "ja", "日本語を勉強する日本語", "all", "SelfTest.TestJapaneseFuriganaDictionary", "日本語[にほんご]を勉強[べんきょう]する日本語", [new(0, 3, "にほんご"), new(4, 2, "べんきょう")]),
        new("ja-filter", "ja", "日本語", "rare", "SelfTest.TestAnnotationProfileAndDedupe：整词命中过滤，不应假设生产IPAdic把語单独切出", "日本語"),
        new("ja-mixed-newlines", "ja", "躊躇した\r\nHello 123! 躊躇した\n食べた𠮷", "all", "重复首现契约与固定读音；扩展字读音未核对", Unverified: "𠮷的词典覆盖与规范读音未核对"),
        new("en-ubiquitous", "en", "The ubiquitous government", "rare", "SelfTest.TestEnglishIpaDictionary", "The ubiquitous[juː.ˈbɪk.wɪ.təs] government", [new(4, 10, "juː.ˈbɪk.wɪ.təs")]),
        new("en-inflection", "en", "abandoned the field", "rare", "SelfTest.TestEnglishIpaDictionary", "abandoned[ə.ˈbæn.dənd] the field", [new(0, 9, "ə.ˈbæn.dənd")]),
        new("en-treason", "en", "treason", "rare", "SelfTest.TestEnglishIpaDictionary", "treason[ˈtriː.zən]", [new(0, 7, "ˈtriː.zən")]),
        new("en-repeat-mixed", "en", "treason 中文\r\ntreason 123!\nTREASON", "all", "selftest treason + 同词形忽略大小写只注首现", "treason[ˈtriː.zən] 中文\r\ntreason 123!\nTREASON", [new(0, 7, "ˈtriː.zən")]),
        new("en-possessive", "en", "Bob's device", "rare", "selftest只固定Bob区间，未固定完整IPA", Unverified: "Bob/device完整IPA未核对"),
        new("zh-paihuai", "zh", "俳佪", "rare", "SelfTest.TestChinesePinyinDictionary", "俳佪[pái huái]", [new(0, 2, "pái huái")]),
        new("zh-juyu", "zh", "龃龉", "rare", "SelfTest.TestChinesePinyinDictionary", "龃龉[jǔ yǔ]", [new(0, 2, "jǔ yǔ")]),
        new("zh-bank", "zh", "不行就去银行", "filtered", "SelfTest.TestChinesePinyinDictionary；必须另外检查Ruby底字", "不行[bù xíng]就去银行[yín háng]", [new(0, 2, "bù xíng"), new(4, 2, "yín háng")]),
        // 两句保留相同词面“还”，语义分别为仍然与归还；不以词面不同替代语境消歧。
        new("zh-hai", "zh", "他还在这里", "all", "普通话词典义：仍然→还hái；只断言目标区间", ExpectedSpans: [new(1, 1, "hái")]),
        new("zh-huan", "zh", "把书还给我", "all", "普通话词典义：归还→还huán；只断言目标区间", ExpectedSpans: [new(2, 1, "huán")]),
        new("zh-grow", "zh", "孩子长大了", "all", "普通话词典义：成长→长大zhǎng dà；只断言目标区间", ExpectedSpans: [new(2, 2, "zhǎng dà")]),
        new("zh-long", "zh", "这里的周长大于十米", "all", "普通话组合义：周长大于→长cháng、大dà；同词面长大的跨词边界检验", ExpectedSpans: [new(4, 2, "cháng dà")]),
        new("zh-traditional", "zh", "銀行", "all", "銀行与银行均为yín háng，原文繁体保留", ExpectedSpans: [new(0, 2, "yín háng")]),
        new("zh-simplified", "zh", "银行", "all", "普通话银行yín háng，原文简体保留", ExpectedSpans: [new(0, 2, "yín háng")]),
        new("zh-xiafeng", "zh", "罅缝", "all", "普通话罅xià、缝隙fèng；固定目标读音xià fèng", ExpectedSpans: [new(0, 2, "xià fèng")]),
        new("zh-repeat-mixed", "zh", "龃龉\r\nHello 123! 龃龉\n龃龉", "all", "selftest龃龉读音 + 重复首现契约", "龃龉[jǔ yǔ]\r\nHello 123! 龃龉\n龃龉", [new(0, 2, "jǔ yǔ")]),
        new("zh-extension", "zh", "𠮷𠀀龃龉", "all", "扩展B代理对保真与后续区间；不臆造扩展字规范读音", ExpectedSpans: [new(4, 2, "jǔ yǔ")], Unverified: "𠮷/U+20BB7、𠀀/U+20000读音及应否注音未核对"),
    ];
}
