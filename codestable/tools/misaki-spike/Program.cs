// MisakiSharp 穿刺探针（仅本地、不发布）。
// 目的：验证「能否从管线中间取出可用于界面注音的数据」而非直接吃 TTS 最终输出。
// 中文：反射读 internal pinyin 字典，自己按 jieba 分词做最长匹配，得到「未变调原始拼音」。
// 日语：用公开的 JapaneseTagger.Parse 取逐词 Surface+Reading（振假名可行性）。
// 英语：用英语 Phonemize 的逐词 token（Text+Phonemes，含词性消歧）。

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using MisakiSharp;

Console.OutputEncoding = Encoding.UTF8;
Section($"环境 .NET={Environment.Version} ptr={(IntPtr.Size * 8)} bit Invariant={System.Globalization.CultureInfo.CurrentCulture.Name}");

// ================= 中文 =================
Section("中文 · 公开输出（TTS 最终产物，仅参照）");
string[] zh =
[
    "他去了银行，然后行走回家。",
    "我还没有还钱。",
    "这条罅缝很窄。",
    "长城很长，孩子长大了。",
    "音乐让人快乐。",
    "重要的会议要重复确认。",
    "你好，世界！2026 年。",
];
foreach (string s in zh)
{
    Console.WriteLine($"[{s}]");
    try { Console.WriteLine("  zhuyin(注音符号): " + ChineseG2P.Phonemize(s)); }
    catch (Exception e) { Console.WriteLine("  zhuyin ERR " + e.Message); }
    try { Console.WriteLine("  legacy(带调IPA) : " + ChineseG2P.PhonemizeLegacy(s)); }
    catch (Exception e) { Console.WriteLine("  legacy ERR " + e.Message); }
}

Section("中文 · 反射取 internal 拼音字典（判断能否拿到逐字原始拼音/候选）");
Dictionary<string, string[]>? hanziToPinyin = null;
int maxPhraseLen = 1;
try
{
    ReadChineseTables(out hanziToPinyin, out maxPhraseLen);
    Console.WriteLine($"modernHanziToPinyin 条数={hanziToPinyin!.Count}  maxPhraseLength={maxPhraseLen}");
    foreach (string w in new[] { "银行", "行走", "还", "罅缝", "长大", "长城", "快乐", "音乐", "重复", "重要", "行", "长", "还", "单" })
    {
        Console.WriteLine($"  {w} => " + (hanziToPinyin.TryGetValue(w, out string[]? a) ? string.Join(" / ", a) : "(无此键)"));
    }
}
catch (Exception e) { Console.WriteLine("反射失败: " + e.GetType().Name + ": " + e.Message); }

if (hanziToPinyin is not null)
{
    Section("中文 · 未变调原始拼音（jieba 分词 + 字典最长匹配，模拟 misaki 变调前一刻）");
    foreach (string s in zh)
    {
        Console.WriteLine($"  [{s}] => {RawPinyin(s, hanziToPinyin, maxPhraseLen)}");
    }
}

// ================= 日语 =================
Section("日语 · JapaneseTagger.Parse 逐词 Surface+Reading（振假名） + 音素参照");
string[] ja =
[
    "日本語は面白い！",
    "睡眠薬を過剰摂取すると致死量になる。",
    "今日はいい天気ですね。",
    "2026年10月に東京へ行きます。",
];
foreach (string s in ja)
{
    Console.WriteLine($"[{s}]");
    try
    {
        foreach (var r in JapaneseTagger.Parse(s))
        {
            Console.WriteLine($"   {r.Surface}\t读:{r.Reading}\tct={r.CharType}\tunk={(r.IsUnk ? "Y" : "n")}");
        }
    }
    catch (Exception e) { Console.WriteLine("   tag ERR " + e.Message); }
    try { Console.WriteLine("   phonemes: " + JapaneseG2P.Phonemize(s)); }
    catch (Exception e) { Console.WriteLine("   ph ERR " + e.Message); }
}

// ================= 英语 =================
Section("英语 · Phonemize 逐词 token（词性消歧 + 对齐）");
var en = new EnglishG2P();
string[] eng =
[
    "I read a book yesterday, but please read it again.",
    "They will record a new record tomorrow.",
    "He can lead the way with a lead pipe.",
    "The 2026 price is $50 for Dr. Smith.",
];
foreach (string s in eng)
{
    Console.WriteLine($"[{s}]");
    try
    {
        var (ph, toks) = en.Phonemize(s);
        Console.WriteLine("  ph: " + ph);
        foreach (var tk in toks)
        {
            Console.WriteLine($"   {tk.Text}\t{tk.Phonemes}\trating={tk.Rating}");
        }
    }
    catch (Exception e) { Console.WriteLine("  ERR " + e.Message); }
}

// ================= 其他语言 =================
Section("Espeak 系（后续 5 语，仅探可用性）");
try
{
    Console.WriteLine("es 示例: " + new EspeakG2P("es").Phonemize("Hola, el 8% a 25 °C."));
}
catch (Exception e) { Console.WriteLine("ERR " + e.Message); }

// ================= 候选调查（数据格式） =================
Section("内嵌资源清单");
var asm = typeof(ChineseG2P).Assembly;
foreach (var n in asm.GetManifestResourceNames().OrderBy(x => x))
{
    using var st = asm.GetManifestResourceStream(n);
    Console.WriteLine($"   {n}\t{st?.Length} bytes(压缩)");
}

Section("英语 en_lexicon_us.tsv.gz（看一词多音/POS 结构）");
DumpGz(asm, "MisakiSharp.en_lexicon_us.tsv.gz", 12, w => w is "record" or "read" or "lead" or "close" or "present" or "wind" or "live" or "use");

Section("日语 ja_lexicon.tsv.gz（看是否一词多读）");
DumpGz(asm, "MisakiSharp.ja_lexicon.tsv.gz", 12, _ => false);

Section("中文 zh_g2p_v2.tsv.gz（单字行 vs 词组行，看多音如何表示）");
DumpGz(asm, "MisakiSharp.zh_g2p_v2.tsv.gz", 8, l => l.StartsWith("行\t") || l.StartsWith("长\t") || l.StartsWith("重\t") || l.StartsWith("还\t") || l.StartsWith("乐\t") || l.StartsWith("单\t"));

Section("中文 modernHanziToPinyin 单字多读统计");
if (hanziToPinyin is not null)
{
    int single = 0, multi = 0;
    var ex = new List<string>();
    foreach (var kv in hanziToPinyin)
    {
        if (kv.Key.Length != 1) continue;
        single++;
        if (kv.Value.Length > 1) { multi++; if (ex.Count < 12) ex.Add(kv.Key + "=>" + string.Join("/", kv.Value)); }
    }
    Console.WriteLine($"   单字键 {single} 个；多读 {multi} 个。示例: {string.Join("  ", ex)}");
}

// ================= 写 UTF-8 候选报告（绕开控制台编码） =================
{
    var rep = new List<string>();
    var enTargets = new HashSet<string> { "record", "read", "lead", "close", "present", "wind", "live", "use", "object", "project", "desert", "minute", "content", "produce" };
    using (var s = asm.GetManifestResourceStream("MisakiSharp.en_lexicon_us.tsv.gz")!)
    using (var gz = new System.IO.Compression.GZipStream(s, System.IO.Compression.CompressionMode.Decompress))
    using (var r = new StreamReader(gz, Encoding.UTF8))
    { string? ln; while ((ln = r.ReadLine()) is not null) { if (enTargets.Contains(ln.Split('\t')[0])) rep.Add("EN | " + ln); } }
    var zhTargets = new HashSet<char> { '行', '长', '重', '还', '乐', '单', '地', '得', '着', '了' };
    using (var s = asm.GetManifestResourceStream("MisakiSharp.zh_g2p_v2.tsv.gz")!)
    using (var gz = new System.IO.Compression.GZipStream(s, System.IO.Compression.CompressionMode.Decompress))
    using (var r = new StreamReader(gz, Encoding.UTF8))
    { string? ln; while ((ln = r.ReadLine()) is not null) { if (ln.Length > 1 && ln[1] == '\t' && zhTargets.Contains(ln[0])) rep.Add("ZH | " + ln); } }
    if (hanziToPinyin is not null)
    {
        int single = 0, multi = 0; var ex = new List<string>();
        foreach (var kv in hanziToPinyin) { if (kv.Key.Length != 1) continue; single++; if (kv.Value.Length > 1) { multi++; if (ex.Count < 20) ex.Add(kv.Key + "=>" + string.Join("/", kv.Value)); } }
        rep.Add($"ZH single-char keys={single} multi={multi}");
        rep.Add("ZH multi examples: " + string.Join("   ", ex));
    }
    using (var s = asm.GetManifestResourceStream("MisakiSharp.ja_lexicon.tsv.gz")!)
    using (var gz = new System.IO.Compression.GZipStream(s, System.IO.Compression.CompressionMode.Decompress))
    using (var r = new StreamReader(gz, Encoding.UTF8))
    { string? ln; int i = 0; while ((ln = r.ReadLine()) is not null && i < 6) { rep.Add("JA | " + ln); i++; } }
    System.IO.File.WriteAllLines("cand_report.txt", rep, new UTF8Encoding(false));
    Console.WriteLine("\n[cand_report.txt written: " + rep.Count + " lines]");
}

// ================= 懒加载计时 =================
Section("初始化耗时（惰性加载体感参考）");
try
{
    var sw = Stopwatch.StartNew();
    ChineseG2P.Phonemize("测试");
    Console.WriteLine($"中文首次(含已初始化,本次近似热): {sw.ElapsedMilliseconds} ms");
    sw.Restart();
    for (int i = 0; i < 100; i++) ChineseG2P.Phonemize("他去了银行然后行走回家");
    Console.WriteLine($"中文热态 100 句: {sw.ElapsedMilliseconds} ms");
    sw.Restart();
    JapaneseG2P.Phonemize("日本語は面白い");
    Console.WriteLine($"日语热态首句: {sw.ElapsedMilliseconds} ms");
    sw.Restart();
    for (int i = 0; i < 100; i++) JapaneseG2P.Phonemize("日本語は面白い");
    Console.WriteLine($"日语热态 100 句: {sw.ElapsedMilliseconds} ms");
}
catch (Exception e) { Console.WriteLine("计时 ERR " + e.Message); }

Console.WriteLine("\n=== Spike 完成 ===");

// ---------- helpers ----------

// 反射读取 ChineseG2P 的 internal 静态字段。AOT 下需配合 DynamicDependency 保留成员。
[DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, typeof(ChineseG2P))]
static void ReadChineseTables(out Dictionary<string, string[]>? dict, out int maxLen)
{
    var t = typeof(ChineseG2P);
    ChineseG2P.Phonemize("初始化"); // 触发 lazy 建表
    dict = (Dictionary<string, string[]>?)t.GetField("modernHanziToPinyin", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
    maxLen = (int)t.GetField("modernMaxPhraseLength", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
}

// 模拟 misaki 变调前：先 jieba 分词，再在每个汉字词内做字典最长匹配，输出带调号的原始拼音。
static string RawPinyin(string s, Dictionary<string, string[]> dict, int maxLen)
{
    var sb = new List<string>();
    foreach (var (word, pos) in PossegTagger.Cut(s))
    {
        bool allHan = word.Length > 0 && word.All(c => c is >= '\u4e00' and <= '\u9fff');
        if (!allHan) { sb.Add(word); continue; }
        for (int i = 0; i < word.Length;)
        {
            int len = Math.Min(maxLen, word.Length - i);
            while (len > 1 && !dict.ContainsKey(word.Substring(i, len))) len--;
            if (dict.TryGetValue(word.Substring(i, len), out var py)) { sb.AddRange(py); i += len; }
            else { sb.Add("?" + word[i]); i++; }
        }
    }
    return string.Join(" ", sb);
}

static void Section(string t) => Console.WriteLine("\n========== " + t + " ==========");

static void DumpGz(System.Reflection.Assembly asm, string name, int max, Func<string, bool> hit)
{
    using var s = asm.GetManifestResourceStream(name);
    if (s is null) { Console.WriteLine("   (missing " + name + ")"); return; }
    using var gz = new System.IO.Compression.GZipStream(s, System.IO.Compression.CompressionMode.Decompress);
    using var r = new StreamReader(gz, Encoding.UTF8);
    int total = 0, shown = 0;
    var hits = new List<string>();
    string? line;
    while ((line = r.ReadLine()) is not null)
    {
        total++;
        if (shown < max) { Console.WriteLine("   | " + line); shown++; }
        if (hit(line) && hits.Count < 25) hits.Add(line);
        if (total > 300000) break;
    }
    foreach (var h in hits) Console.WriteLine("   [命中] " + h);
}
