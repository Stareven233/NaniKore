using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NaniKore.Phonetics;
using NaniKore.Translation;
using PhoneticsBaseline;

Console.OutputEncoding = new UTF8Encoding(false);
var root = Path.GetFullPath(Path.Combine(PhoneticDependencyPaths.PhoneticsRootDirectory, "../.."));
var output = Path.Combine(root, "artifacts", "phonetics-baseline", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
Directory.CreateDirectory(output);
using var report = new StreamWriter(Path.Combine(output, "report.txt"), false, new UTF8Encoding(false)) { AutoFlush = true };
void Log(string value) { Console.WriteLine(value); report.WriteLine(value); }
var counts = new Dictionary<string, int>();
void Check(string status, string name, string detail)
{
    counts[status] = counts.GetValueOrDefault(status) + 1;
    Log($"  {status} {name}: {detail}");
}
Log($"UTC={DateTime.UtcNow:O} runtime={Environment.Version} OS={Environment.OSVersion} offsets=UTF-16");
Log($"resources={PhoneticDependencyPaths.PhoneticsRootDirectory}");
Log("固定期望见Samples.cs；KNOWN_FAIL是规范/契约偏差，不计作PASS。未覆盖/未核对不计作PASS");
// 固定selftest区间的最小挂载反例，与词典分词变化无关；不得把字符串相等当挂载正确。
var mountingText = "不行就去银行";
var mountingSpans = new[] { new AnnotationSpan(0, 2, "bù xíng"), new AnnotationSpan(4, 2, "yín háng") };
var mountingComposed = AnnotatedTextFormatter.Compose(mountingText, mountingSpans);
var mountingRuby = RubyTextParser.Parse(mountingComposed).Where(s => s.IsRuby).ToArray();
Log($"REPRO fixed-selftest-spans input={mountingText} spans=(0,2,bù xíng);(4,2,yín háng) composed={mountingComposed}");
Log($"  RUBY {string.Join(" | ", mountingRuby.Select(s => $"{s.Text}[{s.Ruby}]"))}");
Check(mountingRuby.Length == 2 && mountingRuby[1].Text == "银行" ? "PASS" : "KNOWN_FAIL", "固定区间Ruby挂载反例", "第二个底字必须为银行，不得吞入未标注的就去");
var unavailable = false;
foreach (var language in new[] { "ja", "en", "zh" })
{
    var languageDirectory = Path.Combine(PhoneticDependencyPaths.PhoneticsRootDirectory, language);
    foreach (var file in (Directory.Exists(languageDirectory) ? Directory.EnumerateFiles(languageDirectory) : []).Order())
    {
        using var stream = File.OpenRead(file);
        Log($"RESOURCE {Path.GetRelativePath(root, file)} bytes={stream.Length} sha256={Convert.ToHexString(SHA256.HashData(stream))}");
    }
    var state = PhoneticDependencyManifest.GetState(language);
    Log($"MANIFEST {language}={state}");
    if (state != PhoneticDictionaryState.Ready)
    {
        unavailable = true;
        Check("UNCOVERED", "资源身份", "当前资源不满足生产清单；本次观测不能作为正式资源基线");
    }
    using var annotator = language switch
    {
        "ja" => (IDisposable)new JapaneseFuriganaAnnotator(),
        "en" => new EnglishIpaAnnotator(),
        _ => new ChinesePinyinAnnotator()
    };
    var engine = (ITextAnnotator)annotator;
    var watch = Stopwatch.StartNew();
    var available = engine.IsAvailable;
    watch.Stop();
    Log($"LOAD {language} available={available} first-open-ms={watch.Elapsed.TotalMilliseconds:F3} working-set={Environment.WorkingSet}");
    foreach (var sample in Samples.All.Where(s => s.Language == language))
    {
        Log($"\nSAMPLE {sample.Id} mode={sample.Mode} basis={sample.Basis}\n  input={Escape(sample.Text)}");
        if (!available)
        {
            unavailable = true;
            Check("UNCOVERED", "词典加载", "不可用，未执行样本");
            continue;
        }
        // 每条样本独立上下文；all使用短文本全注开关，避免停用表遮住目标选音。
        var profile = AnnotationProfile.Load(language, rareOnly: sample.Mode == "rare",
            wordCount: sample.Mode == "all" ? 0 : int.MaxValue);
        watch.Restart();
        var spans = engine.Annotate(sample.Text, new AnnotationContext(profile));
        watch.Stop();
        Log($"  words={engine.CountWords(sample.Text)} annotate-ms={watch.Elapsed.TotalMilliseconds:F3}");
        foreach (var span in spans)
            Log($"  SPAN start={span.Start} length={span.Length} base={Escape(sample.Text.Substring(span.Start, span.Length))} reading={span.Reading}");
        var composed = AnnotatedTextFormatter.Compose(sample.Text, spans);
        Log($"  composed={Escape(composed)}");
        var parsed = RubyTextParser.Parse(composed);
        foreach (var segment in parsed)
            Log($"  RUBY base={Escape(segment.Text)} reading={segment.Ruby ?? "<plain>"}");
        Check(RubyTextParser.ToPlainText(composed) == sample.Text ? "PASS" : "KNOWN_FAIL", "原文往返", "ToPlainText(Compose) == input");
        var ruby = parsed.Where(s => s.IsRuby).ToArray();
        var mounting = ruby.Length == spans.Count && spans.Select((s, i) =>
            i < ruby.Length && ruby[i].Text == sample.Text.Substring(s.Start, s.Length) && ruby[i].Ruby == s.Reading).All(x => x);
        Check(mounting ? "PASS" : "KNOWN_FAIL", "Ruby挂载", "每个解析底字/读音应与原始span一一相同");
        if (sample.ExpectedComposed is not null)
            Check(composed == sample.ExpectedComposed ? "PASS" : "KNOWN_FAIL", "固定字符串", $"expected={Escape(sample.ExpectedComposed)}");
        if (sample.ExpectedSpans is not null)
        {
            foreach (var expected in sample.ExpectedSpans)
            {
                // 中文逐字音节投影只用于目标读音校验，要求每个字恰有一个音节；不伪造生产映射。
                // 非等长词条不推断，直接记偏差；实际span与Ruby仍完整输出。
                var actual = language == "zh" ? ChineseTarget(spans, expected) :
                    spans.FirstOrDefault(s => s.Start == expected.Start && s.Length == expected.Length).Reading;
                Check(actual == expected.Reading ? "PASS" : "KNOWN_FAIL", "固定目标读音", $"start={expected.Start} length={expected.Length} expected={expected.Reading} actual={actual ?? "<missing/unmapped>"}");
            }
        }
        if (sample.Unverified is not null) Check("UNCOVERED", "规范期望未核对", sample.Unverified);
        if (sample.ExpectedComposed is null) Check("UNCOVERED", "整句规范", "仅验证上述目标及结构，其余读音未核对");
    }
}
foreach (var item in new[] { "候选引擎比对与收益", "NativeAOT真实宿主", "UI复制/选区/收藏", "冷磁盘/稳态统计/闲置释放重载/完整内存基准", "资源下载与许可再分发" })
    Check("UNCOVERED", "本切片边界", item);
Log($"\nSUMMARY PASS={counts.GetValueOrDefault("PASS")} KNOWN_FAIL={counts.GetValueOrDefault("KNOWN_FAIL")} UNCOVERED={counts.GetValueOrDefault("UNCOVERED")}");
Log($"report={Path.Combine(output, "report.txt")}");
// 基线有已知失败仍返回非零，调用方不能误判为全部正确；2表示依赖未就绪。
return unavailable ? 2 : counts.GetValueOrDefault("KNOWN_FAIL") > 0 ? 1 : 0;

static string Escape(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");
static string? ChineseTarget(IReadOnlyList<AnnotationSpan> spans, ExpectedSpan expected)
{
    var readings = new string?[expected.Length];
    foreach (var span in spans)
    {
        var syllables = span.Reading.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (syllables.Length != span.Length) continue;
        for (var index = Math.Max(span.Start, expected.Start); index < Math.Min(span.Start + span.Length, expected.Start + expected.Length); index++)
            readings[index - expected.Start] = syllables[index - span.Start];
    }
    return readings.Any(s => s is null) ? null : string.Join(' ', readings);
}
