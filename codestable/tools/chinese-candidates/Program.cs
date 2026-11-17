using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using NaniKore.Phonetics;
using NMeCab;
using PhoneticsBaseline;

namespace ChineseCandidates;

internal static class Program
{
    private const string Commit = "217de636b0223bb4c60b7f0b9d107d288303fed5";
    private static StreamWriter report = null!;
    private static int pass, fail, unknown, productionPass, candidatePass, targets, improved, regressed;
    private static int fixedTargets, fixedProductionPass, fixedCandidatePass;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.Length is < 2 or > 3)
                throw new ArgumentException("用法：ChineseCandidates <仓库根目录> <新报告目录> [中文词典目录]");
            var root = Path.GetFullPath(args[0]);
            var output = Path.GetFullPath(args[1]);
            if (Directory.Exists(output)) throw new IOException("报告目录已存在，请指定新目录，避免覆盖证据");
            Directory.CreateDirectory(output);
            using var writer = new StreamWriter(Path.Combine(output, "report.txt"), false, new UTF8Encoding(false)) { AutoFlush = true };
            report = writer;
            Log($"UTC={DateTime.UtcNow:O} runtime={Environment.Version} aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported} os={Environment.OSVersion} offsets=UTF-16");
            Log("SNAPSHOT production=build-input-hashes; historical-20261114-093xxx=PRE-P1; reruns-capture-current-production");
            Log("期望来自固定Samples.cs；仅目标区间计准确性；整句/扩展字读音未核对。冷启动指进程首次初始化，不是清空磁盘缓存");
            PhoneticDependencyPaths.ChineseDictionaryDirectory = args.Length == 3 ? Path.GetFullPath(args[2]) : Path.Combine(root, "deps/phonetics/zh");
            VerifyInputs(root);
            foreach (var file in PhoneticDependencyPaths.ChineseDictionaryFiles) Fingerprint("PRODUCTION_RESOURCE", file);
            var binary = Environment.ProcessPath!;
            Fingerprint("EXECUTABLE", binary);
            // AOT只记录原生产物；隔离Debug工程的程序集固定位于输出目录，避免使用单文件无意义的Location。
            if (RuntimeFeature.IsDynamicCodeSupported)
                Fingerprint("ASSEMBLY", Path.Combine(AppContext.BaseDirectory, "ChineseCandidates.dll"));
            VerifyCandidateNewlines();
            Memory("before-init");
            using var production = new ChinesePinyinAnnotator();
            var timer = Stopwatch.StartNew();
            if (!production.IsAvailable) throw new IOException("生产中文标注器不可用");
            Log($"INIT production ms={timer.Elapsed.TotalMilliseconds:F4}");
            Memory("after-production-init");
            timer.Restart();
            var candidate = new Pinyin.Pinyin();
            Log($"INIT candidate ms={timer.Elapsed.TotalMilliseconds:F4}");
            AssertCandidateBank(candidate);
            Memory("after-candidate-init-cumulative");
            // 原始节点绕过标注器过滤，仅供诊断，不把它当另一套生产实现。
            using (var raw = MeCabTagger.Create(PhoneticDependencyPaths.ChineseDictionaryDirectory, null))
            {
                foreach (var sample in Samples.All.Where(s => s.Language == "zh")) Compare(sample, production, candidate, raw);
                foreach (var sample in ExtraSamples()) Compare(sample, production, candidate, raw);
            }
            var texts = Samples.All.Where(s => s.Language == "zh").Select(s => s.Text).Concat(ExtraSamples().Select(s => s.Text)).ToArray();
            Warm("production", texts, text => production.Annotate(text, Context("all")));
            Warm("candidate-list", texts, text => candidate.HanziToPinyin(Tokens(text).Select(t => t.Text).ToList()));
            Memory("after-warm-cumulative");
            production.Dispose();
            Memory("after-production-dispose-no-forced-GC");
            // 生产真实Dispose+Reset；候选无Dispose接口，只能重新构造，不声称已回收旧字典。
            production.ResetAvailability();
            timer.Restart();
            if (!production.IsAvailable) throw new IOException("生产重载失败");
            Log($"RELOAD production ms={timer.Elapsed.TotalMilliseconds:F4}");
            candidate = null!;
            timer.Restart();
            candidate = new Pinyin.Pinyin();
            Log($"RELOAD candidate-reconstruct ms={timer.Elapsed.TotalMilliseconds:F4} old-instance-collection=unverified");
            Check(candidate.HanziToPinyin(new List<string> { "银", "行" }).ToStr() == "yín háng", "candidate-reload-bank");
            Check(production.Annotate("银行", Context("all")).Any(s => s.Reading == "yín háng"), "production-reload-bank");
            Memory("after-reload-cumulative");
            Log($"FIXED_TARGETS total={fixedTargets} production-pass={fixedProductionPass} production-fail={fixedTargets - fixedProductionPass} candidate-pass={fixedCandidatePass} candidate-fail={fixedTargets - fixedCandidatePass}");
            Log($"TARGETS total={targets} production-pass={productionPass} production-fail={targets - productionPass} candidate-pass={candidatePass} candidate-fail={targets - candidatePass} improved={improved} regressed={regressed}");
            Log($"SUMMARY PASS={pass} FAIL={fail} UNCOVERED={unknown}");
            Log($"COMPARISON fixed-target-net={(fixedCandidatePass > fixedProductionPass ? "candidate-better-on-this-set" : fixedCandidatePass < fixedProductionPass ? "candidate-worse-on-this-set" : "same-pass-count")} adoption=insufficient-evidence");
            Log("VERDICT 以上只支撑固定目标对照；任何目标失败或边界丢失均不满足直接采用条件。候选集合存在不代表语境消歧成功。P1可保留UTF16明确区间/未知项/候选集合，不能据此宣布候选已达到生产验收");
            Log("LIMIT 内存为同进程累计快照；未测独立引擎峰值、真实闲置计时器、宿主UI、分发下载、权威词典逐项核验。无全长句准确率");
            return fail > 0 ? 1 : 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"BLOCKED {error}");
            return 2;
        }
    }

    private static void Compare(Sample sample, ChinesePinyinAnnotator production, Pinyin.Pinyin candidate, MeCabTagger raw)
    {
        Log($"\nSAMPLE {sample.Id} input={Escape(sample.Text)} mode={sample.Mode} basis={sample.Basis}");
        var spans = production.Annotate(sample.Text, Context(sample.Mode));
        foreach (var span in spans) Log($"PROD start={span.Start} length={span.Length} base={Escape(sample.Text.Substring(span.Start, span.Length))} reading={Escape(span.Reading)}");
        var rawCursor = 0;
        foreach (var node in raw.Parse(sample.Text))
        {
            var start = string.IsNullOrEmpty(node.Surface) ? -1 : sample.Text.IndexOf(node.Surface, rawCursor, StringComparison.Ordinal);
            if (start >= 0) rawCursor = start + node.Surface.Length;
            Log($"RAW start={start} length={node.Surface.Length} surface={Escape(node.Surface)} feature={Escape(node.Feature)}");
        }
        var tokens = Tokens(sample.Text);
        Log($"INPUT_IDENTITY utf16={sample.Text.Length} tokens={tokens.Count} list=scalar-tokens-with-UTF16-offsets boundary=upstream-may-match-across-non-dictionary-tokens");
        var results = candidate.HanziToPinyin(tokens.Select(t => t.Text).ToList());
        Check(results.Count == tokens.Count, $"{sample.Id}:list-count");
        for (var i = 0; i < Math.Min(results.Count, tokens.Count); i++)
        {
            var result = results[i];
            var token = tokens[i];
            Log($"CAND start={token.Start} length={token.Text.Length} original={Escape(token.Text)} returned={Escape(result.hanzi)} reading={Escape(result.pinyin)} error={result.error} candidates={string.Join('|', result.candidates.Select(Escape))}");
            Check(result.hanzi == token.Text, $"{sample.Id}:identity@{token.Start}");
        }
        // 上游string入口会用正则删去标点、换行和扩展字；如实运行，不能用List入口的保真掩盖它。
        var native = candidate.HanziToPinyin(sample.Text);
        var nativeText = string.Concat(native.Select(r => r.hanzi));
        Log($"NATIVE-STRING reconstructed={Escape(nativeText)} count={native.Count}");
        Check(nativeText == sample.Text, $"{sample.Id}:native-string-preserves-input");
        foreach (var expected in sample.ExpectedSpans ?? [])
        {
            var prod = Project(spans, expected);
            var selected = tokens.Select((token, index) => (token, index)).Where(p => p.token.Start >= expected.Start && p.token.Start < expected.Start + expected.Length).ToArray();
            var cand = results.Count == tokens.Count && selected.All(p => !results[p.index].error)
                ? string.Join(' ', selected.Select(p => results[p.index].pinyin)) : null;
            var hit = results.Count == tokens.Count && selected.Length == expected.Length && selected.Select((p, i) =>
                results[p.index].candidates.Contains(expected.Reading.Split(' ')[i], StringComparer.Ordinal)).All(x => x);
            var pOk = prod == expected.Reading;
            var cOk = cand == expected.Reading;
            targets++; if (pOk) productionPass++; if (cOk) candidatePass++; if (!pOk && cOk) improved++; if (pOk && !cOk) regressed++;
            if (sample.Id.StartsWith("zh-", StringComparison.Ordinal))
            {
                fixedTargets++; if (pOk) fixedProductionPass++; if (cOk) fixedCandidatePass++;
            }
            Log($"TARGET start={expected.Start} length={expected.Length} expected={expected.Reading} production={prod ?? "<unmapped>"} candidate={cand ?? "<unknown>"} expected-in-candidates={hit}");
            Check(pOk, $"{sample.Id}:production-target@{expected.Start}");
            Check(cOk, $"{sample.Id}:candidate-target@{expected.Start}");
        }
        if (sample.ExpectedComposed is not null)
            Check(AnnotatedTextFormatter.Compose(sample.Text, spans) == sample.ExpectedComposed, $"{sample.Id}:production-fixed-composed");
        unknown++;
        Log($"UNCOVERED full-sentence-readings; {sample.Unverified ?? "固定目标以外读音未核对"}");
    }

    private static AnnotationContext Context(string mode) => new(AnnotationProfile.Load("zh", rareOnly: mode == "rare", wordCount: mode == "all" ? 0 : int.MaxValue));

    private static string? Project(IReadOnlyList<AnnotationSpan> spans, ExpectedSpan target)
    {
        // 只投影BMP等音节词条；未知feature或非等长项不臆造逐字对齐。
        var slots = new string?[target.Length];
        foreach (var span in spans)
        {
            var syllables = span.Reading.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (syllables.Length != span.Length) continue;
            for (var at = Math.Max(span.Start, target.Start); at < Math.Min(span.Start + span.Length, target.Start + target.Length); at++)
                slots[at - target.Start] = syllables[at - span.Start];
        }
        return slots.Any(s => s is null) ? null : string.Join(' ', slots);
    }

    private static List<(int Start, string Text)> Tokens(string text)
    {
        // 使用上游List入口：有效代理对为一个未知token，孤立代理项也原样保留；不修改候选选音算法。
        var result = new List<(int, string)>();
        for (var i = 0; i < text.Length;)
        {
            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            result.Add((i, text.Substring(i, length)));
            i += length;
        }
        return result;
    }

    private static IEnumerable<Sample> ExtraSamples()
    {
        // 只固定既有已知词义的目标；长句其他读音不计准确率。
        const string unit = "他还在这里。把书还给我。孩子长大了。这里的周长大于十米。銀行与银行。罅缝。\r\n";
        yield return new("sanity-long", "zh", string.Concat(Enumerable.Repeat(unit, 12)), "all", "长文/重复观察；不设整句金样本", Unverified: "长文所有读音未逐项核对");
        yield return new("sanity-boundary", "zh", "银A行；银\r\n行；𠮷𠀀龃龉；银行银行", "all", "边界压缩/重复/未知项观察", Unverified: "跨边界读音不设金样本，完整输出供复核");
        yield return new("sanity-surrogate", "zh", "\uD800龃龉\uDC00", "all", "孤立代理项的UTF16保真", ExpectedSpans: [new(1, 2, "jǔ yǔ")], Unverified: "孤立代理项无规范读音");
    }

    private static void Warm(string name, string[] texts, Action<string> action)
    {
        foreach (var text in texts) action(text);
        var times = new List<double>();
        for (var run = 0; run < 30; run++)
        {
            var timer = Stopwatch.StartNew();
            foreach (var text in texts) action(text);
            times.Add(timer.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Log($"WARM {name} batches=30 texts-per-batch={texts.Length} utf16-per-batch={texts.Sum(t => t.Length)} min-ms={times[0]:F4} median-ms={times[15]:F4} p95-ms={times[28]:F4} max-ms={times[^1]:F4}");
    }

    private static void VerifyCandidateNewlines()
    {
        // 上游按Environment.NewLine分割，不会自动识别LF；先拒绝坏资源，避免空词典变成准确性成绩。
        Log($"DEPENDENCY Pinyin.DictUtil delimiter=Environment.NewLine value={Escape(Environment.NewLine)}");
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Pinyin.dict.mandarin.", StringComparison.Ordinal) && !n.EndsWith("License.txt", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var crlf = text.Split("\r\n", StringSplitOptions.None).Length - 1;
            var loneLf = text.Count(c => c == '\n') - crlf;
            var loneCr = text.Count(c => c == '\r') - crlf;
            Log($"RESOURCE_NEWLINES name={name} CRLF={crlf} LF={loneLf} CR={loneCr}");
            if (Environment.NewLine == "\r\n" && loneLf > 0)
                throw new IOException($"候选资源{name}包含LF行尾，上游Environment.NewLine在Windows要求CRLF。请按README规范化数据后重新构建；本次不计准确性");
        }
    }

    private static void AssertCandidateBank(Pinyin.Pinyin candidate)
    {
        var bank = candidate.HanziToPinyin(new List<string> { "银", "行" });
        var valid = bank.Count == 2 && bank[0].hanzi == "银" && bank[1].hanzi == "行"
            && bank.All(r => !r.error) && bank.ToStr() == "yín háng";
        Log($"INIT_SENTINEL candidate-bank valid={valid} reading={Escape(bank.ToStr())}");
        if (!valid) throw new IOException("候选银行初始化哨兵失败：词典缺失或解析异常；本次不计准确性，请检查资源行尾与Environment.NewLine");
    }

    private static void VerifyInputs(string root)
    {
        var clone = Path.Combine(root, "artifacts/chinese-candidates/csharp-pinyin");
        if (Git(clone, "rev-parse", "HEAD").Trim() != Commit) throw new IOException("候选HEAD不匹配固定commit");
        // 检查固定commit与工作树差异，不能只核对HEAD而漏掉本地源码/资源修改。
        Git(clone, "diff", "--exit-code", Commit, "--", "Pinyin", "LICENSE", ":(exclude)**/_todo*");
        if (Git(clone, "ls-files", "--others", "--exclude-standard", "--", "Pinyin").Length != 0)
            throw new IOException("候选Pinyin目录有未跟踪输入");
        Log($"SOURCE commit={Commit} clean=true");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Comparison.Inputs") ?? throw new IOException("缺少构建输入锁");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var split = line.LastIndexOf('|');
            var path = line[..split];
            var defaultResourceRoot = Path.GetFullPath(Path.Combine(root, "deps/phonetics/zh")) + Path.DirectorySeparatorChar;
            if (path.StartsWith(defaultResourceRoot, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(PhoneticDependencyPaths.ChineseDictionaryDirectory, Path.GetFileName(path));
            var expected = line[(split + 1)..];
            var actual = Fingerprint("INPUT", path);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException($"构建输入已漂移：{path}");
        }
        Log("INPUT_LOCK all-build-input-hashes-match=true");
    }

    private static string Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动git");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"git {string.Join(' ', args)}: {stderr} {stdout}");
        return stdout;
    }

    private static string Fingerprint(string kind, string path)
    {
        using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(file));
        Log($"{kind} path={path} bytes={file.Length} sha256={hash}");
        return hash;
    }
    private static void Memory(string stage) => Log($"MEMORY {stage} managed={GC.GetTotalMemory(false)} working-set={Environment.WorkingSet} private={Process.GetCurrentProcess().PrivateMemorySize64}");
    private static void Check(bool success, string name) { if (success) pass++; else fail++; Log($"{(success ? "PASS" : "FAIL")} {name}"); }
    private static void Log(string text) { Console.WriteLine(text); report.WriteLine(text); }
    private static string Escape(string text) => string.Concat(text.Select(c => c switch { '\r' => "\\r", '\n' => "\\n", '\t' => "\\t", _ when char.IsSurrogate(c) => $"\\u{(int)c:X4}", _ => c.ToString() }));
}
