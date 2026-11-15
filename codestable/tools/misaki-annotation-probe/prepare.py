"""只在 .temp 下准备固定版本探针；不修改包缓存、生产词典或下载清单。"""
import argparse
import csv
import gzip
import hashlib
import json
import mmap
from pathlib import Path
import re
import struct
import urllib.request
import zipfile

COMMIT = "beb91a9f06ebe5e25e595b5a3c990ee616ad75e5"
SOURCE_FILES = ["JapaneseTagger.cs", "EnglishG2P.cs", "EnglishG2P.Lexicon.cs", "EnglishTokenizer.cs", "EnglishTagger.cs", "EspeakG2P.cs", "EspeakReplay.cs", "MisakiSharp.csproj", "NOTICE", "LICENSE", "generators/generate_ja_dict.py", "generators/generate_en_dict.py"]


def replace_once(text, old, new):
    # 固定提交的补丁若失配就停，避免上游升版后悄悄套错修改。
    if text.count(old) != 1:
        raise ValueError(f"补丁锚点不是唯一的：{old[:100]!r}")
    return text.replace(old, new, 1)


def fetch(root):
    for name in SOURCE_FILES:
        target = root / "upstream" / name
        if not target.exists():
            target.parent.mkdir(parents=True, exist_ok=True)
            urllib.request.urlretrieve(f"https://raw.githubusercontent.com/Lyrcaxis/MisakiSharp/{COMMIT}/{name}", target)
    cecil = root / "mono.cecil.0.11.6.zip"
    if not cecil.exists():
        urllib.request.urlretrieve("https://api.nuget.org/v3-flatcontainer/mono.cecil/0.11.6/mono.cecil.0.11.6.nupkg", cecil)
    if not (root / "cecil-0.11.6/lib/netstandard2.0/Mono.Cecil.dll").exists():
        with zipfile.ZipFile(cecil) as archive:
            archive.extractall(root / "cecil-0.11.6")
    dictionary = root / "unidic-3.1.0.zip"
    if not dictionary.exists():
        urllib.request.urlretrieve("https://cotonoha-dic.s3-ap-northeast-1.amazonaws.com/unidic-3.1.0.zip", dictionary)


def kana(root):
    with zipfile.ZipFile(root / "unidic-3.1.0.zip") as archive:
        names = [n for n in archive.namelist() if n.endswith("/sys.dic")]
        assert len(names) == 1, names
        target = root / "unidic" / names[0]
        if not target.exists():
            archive.extract(names[0], root / "unidic")
        for name in archive.namelist():
            if name.rsplit("/", 1)[-1] in {"BSD", "COPYING", "dicrc", "version"}:
                archive.extract(name, root / "unidic")
    output = root / "kana"
    output.mkdir(exist_ok=True)
    dicrc = target.with_name("dicrc").read_text(encoding="utf-8-sig")
    feature_indices = {name: int(index) for index, name in re.findall(r";\s*f\[(\d+)\]:\s*(\w+)", dicrc)}
    assert feature_indices["pron"] == 9 and feature_indices["kana"] == 20, feature_indices
    surfaces = tokens = differences = 0
    examples = []
    with target.open("rb") as file, mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ) as data:
        magic, version, _, _, _, _, dsize, tsize, fsize, _ = struct.unpack_from("<10I", data)
        assert magic ^ len(data) == 0xEF718F77 and version == 102
        token_start, feature_start = 72 + dsize, 72 + dsize + tsize

        def node(index):
            return struct.unpack_from("<iI", data, 72 + index * 8)

        def lookup(surface):
            base = node(0)[0]
            for byte in surface.encode():
                next_base, check = node(base + byte + 1)
                assert check == base, surface
                base = next_base
            value, check = node(base)
            assert check == base and value < 0, surface
            return -value - 1

        with gzip.open(root / "resources/ja_lexicon.tsv.gz", "rt", encoding="utf-8") as source, gzip.GzipFile(output / "ja_lexicon.tsv.gz", "wb", mtime=0) as destination:
            for line in source:
                cols = line.rstrip("\n").split("\t")
                surface = cols[0].replace("\\t", "\t").replace("\\n", "\n").replace("\\r", "\r").replace("\\\\", "\\")
                value = lookup(surface)
                assert value & 255 == len(cols) - 1, surface
                rows = []
                for index, old in enumerate(cols[1:]):
                    lc, rc, _, cost, feature, _ = struct.unpack_from("<HHHhII", data, token_start + ((value >> 8) + index) * 16)
                    start = feature_start + feature
                    fields = next(csv.reader([data[start:data.find(b"\0", start)].decode("utf-8")]))
                    assert len(fields) == 29 and f"{lc},{rc},{cost},{fields[9]}" == old, (surface, old, fields)
                    # UniDic 3.1.0 的 kana 是第 20 列；旧 2.1.2 的第 17 列不能照搬。
                    reading = fields[feature_indices["kana"]]
                    assert reading, surface
                    rows.append(old + "," + reading)
                    tokens += 1
                    if fields[9] != reading:
                        differences += 1
                        if surface in {"東京", "躊躇", "今日", "凍り付く", "は", "へ"}:
                            examples.append({"surface": surface, "pron": fields[9], "kana": reading})
                destination.write((cols[0] + "\t" + "\t".join(rows) + "\n").encode())
                surfaces += 1
                if surfaces % 200000 == 0:
                    print(f"kana: {surfaces} surfaces verified", flush=True)
    with target.open("rb") as original:
        dictionary_hash = hashlib.file_digest(original, "sha256").hexdigest()
    report = {"dictionary_sha256": dictionary_hash, "pron_index": feature_indices["pron"], "kana_index": feature_indices["kana"], "surfaces": surfaces, "tokens": tokens, "pron_kana_differences": differences, "identity_mismatches": 0, "examples": examples, "kana_gzip_bytes": (output / "ja_lexicon.tsv.gz").stat().st_size}
    (root / "kana-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


def patch(root):
    output = root / "patched"
    output.mkdir(exist_ok=True)
    for name in SOURCE_FILES:
        if not name.endswith(".cs"):
            continue
        text = (root / "upstream" / name).read_text(encoding="utf-8-sig")
        if name == "JapaneseTagger.cs":
            text = replace_once(text, "public string Pron;", "public string Pron, Kana;")
            text = replace_once(text, "static readonly Lazy<bool> loaded", "static Lazy<bool> loaded")
            text = replace_once(text, "node.Token.Pron", "node.Token.Kana")
            text = replace_once(text, "parts[i].Split(',', 4)", "parts[i].Split(',', 5)")
            text = replace_once(text, "Pron = Unescape(f[3])", "Pron = Unescape(f[3]), Kana = Unescape(f[4])")
            text += "\n"  # 下方方法插入原类型末尾，保留其余分词与矩阵算法。
            pos = text.rfind("}")
            text = text[:pos] + '''
    // 穿刺专用：调用方持同一把锁，解析期间不得清空共享表。
    public static WeakReference ProbeRelease() {
        var previous = new WeakReference(lexicon);
        lexicon = null; lexiconLengths = null; charInfo = null; unkTokens = null;
        rowMean = null; colMean = null; shiftA = null; factorA = null; factorB = null;
        cacheRowOffsets = null; cacheCols = null; cacheVals = null;
        loaded = new(Load);
        return previous;
    }
''' + text[pos:]
        elif name == "EnglishG2P.cs":
            start = text.index("        var (result, spans, features) =", text.index("static (string Text,"))
            end = text.index("    /// <summary> A feature", start)
            text = text[:start] + "        // 界面原文不接受 TTS 的 markdown 读音指令，也不裁掉前导空白。\n        return (text, [], []);\n    }\n\n" + text[end:]
            text = replace_once(text, "        var words = Retokenize(FoldLeft(Tokenize(processed, spans, features)));", '''        var sourceTokens = Tokenize(processed, spans, features);
        // tab/换行在 tokenizer 中可能独立成词元；保留分隔关系，禁止 TTS 重组把空白粘到下一个单词。
        for (int i = sourceTokens.Count - 1; i >= 0; i--) {
            if (!sourceTokens[i].Text.All(char.IsWhiteSpace)) continue;
            if (i > 0) sourceTokens[i - 1].Whitespace += sourceTokens[i].Text + sourceTokens[i].Whitespace;
            sourceTokens.RemoveAt(i);
        }
        var words = Retokenize(FoldLeft(sourceTokens));''')
            text = replace_once(text, "public void AddWords(List<(string word, string phonemes)> dict) => lexicon.AddWords(dict);", "public void AddWords(List<(string word, string phonemes)> dict) => lexicon.AddWords(dict);\n    public string[] ProbeCandidates(string word) => lexicon.ProbeCandidates(word);")
        elif name == "EnglishG2P.Lexicon.cs":
            anchor = "        /// <summary> Registers custom pronunciations"
            pos = text.index(anchor)
            text = text[:pos] + '''        // 穿刺专用：只认明确词条，不用自动拼字或词干还原冒充候选来源。
        public string[] ProbeCandidates(string word) {
            var entry = golds.GetValueOrDefault(word) ?? silvers.GetValueOrDefault(word)
                ?? golds.GetValueOrDefault(word.ToLowerInvariant()) ?? silvers.GetValueOrDefault(word.ToLowerInvariant());
            return entry is string single ? [single] : entry is Dictionary<string, string> tagged
                ? tagged.Values.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray() : [];
        }

''' + text[pos:]
        elif name == "EnglishTagger.cs":
            text = replace_once(text, "static readonly Lazy<", "static Lazy<")
            pos = text.rfind("}")
            text = text[:pos] + "    public static void ProbeRelease() => model = new(Load);\n" + text[pos:]
        elif name == "EnglishTokenizer.cs":
            text = replace_once(text, "static readonly Dictionary<string, string[]> specialRules", "static Dictionary<string, string[]> specialRules")
            text = replace_once(text, "static readonly Lazy<", "static Lazy<")
            pos = text.rfind("}")
            text = text[:pos] + "    public static void ProbeRelease() { specialRules = []; specialMatcher = new(LoadTables); prefixSearch = suffixSearch = infixFinditer = urlMatch = null; }\n" + text[pos:]
        elif name == "EspeakReplay.cs":
            pos = text.rfind("}")
            text = text[:pos] + "    public static void ProbeRelease() { lock (loaded) { loaded.Clear(); } }\n" + text[pos:]
        (output / name).write_text(text, encoding="utf-8")
    hashes = {name: hashlib.sha256((root / "upstream" / name).read_bytes()).hexdigest() for name in SOURCE_FILES}
    (root / "source-manifest.json").write_text(json.dumps({"commit": COMMIT, "sha256": hashes}, indent=2), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["fetch", "kana", "patch"])
    parser.add_argument("--root", default=".temp/misaki-annotation-spike")
    args = parser.parse_args()
    root = Path(args.root).resolve()
    workspace = Path(__file__).resolve().parents[3]
    if not root.is_relative_to(workspace / ".temp"):
        raise SystemExit("探针产物必须放在仓库 .temp 下")
    root.mkdir(parents=True, exist_ok=True)
    {"fetch": fetch, "kana": kana, "patch": patch}[args.mode](root)
