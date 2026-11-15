using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NaniKore.Phonetics.Native;

// 构建期工具：只读 NuGet 原件与再生数据，输出 JSON 供构建脚本记录，不写任何生产目录。
//   extract <MisakiSharp.dll> <输出目录> <资源名...>   按白名单导出内嵌资源并回写哈希
//   audit-ipa <en_lexicon_us.tsv.gz>                 核对 Kokoro 编码转换覆盖率（G2 证据）
Console.OutputEncoding = Encoding.UTF8;
var options = new JsonSerializerOptions { WriteIndented = true };
switch (args[0])
{
    case "extract":
    {
        // 只用 LoadFrom 读资源清单，不改原件、不改包缓存。
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[1]));
        var output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);
        var wanted = args[3..].ToHashSet(StringComparer.Ordinal);
        var available = assembly.GetManifestResourceNames().Where(n => n.StartsWith("MisakiSharp.", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var missing = wanted.Where(n => !available.Contains("MisakiSharp." + n)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("上游包缺少白名单资源：" + string.Join(", ", missing));
        var written = new List<object>();
        foreach (var name in wanted.Order(StringComparer.Ordinal))
        {
            var target = Path.Combine(output, name);
            using (var source = assembly.GetManifestResourceStream("MisakiSharp." + name)!)
            using (var file = File.Create(target)) source.CopyTo(file);
            written.Add(new { name, bytes = new FileInfo(target).Length, sha256 = Hash(target) });
        }

        Console.WriteLine(JsonSerializer.Serialize(new { assemblySha256 = Hash(Path.GetFullPath(args[1])), resources = written }, options));
        break;
    }

    case "audit-ipa":
    {
        // 逐条转换词典读音：能转换的要真能展示，转不了的要能指出缺哪个字符，不能悄悄丢字。
        var converted = 0;
        var rejected = 0;
        var unknown = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var examples = new List<string>();
        using var gzip = new GZipStream(File.OpenRead(args[1]), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (reader.ReadLine() is string line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            foreach (var field in line.Split('\t').Skip(1))
            {
                // 词性表字段形如 TAG=读音；空读音表示该词性不可读。
                var value = field.Contains('=') ? field[(field.IndexOf('=') + 1)..] : field;
                if (string.IsNullOrEmpty(value)) continue;
                if (KokoroIpa.Convert(value) is null)
                {
                    rejected++;
                    foreach (var symbol in value.Where(c => !KokoroIpa.Alphabet.Contains(c))) unknown[symbol.ToString()] = unknown.GetValueOrDefault(symbol.ToString()) + 1;
                    if (examples.Count < 12) examples.Add(line.Split('\t')[0] + "=" + value);
                }
                else converted++;
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new { converted, rejected, unknownSymbols = unknown, rejectedExamples = examples }, options));
        break;
    }

    default:
        throw new ArgumentException("未知动作：" + args[0]);
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
