"""从同版 UniDic 再生日语书面假名词典（构建期脚本，只读原件、只写 .temp 与包目录）。

做的事：核对压缩包与 sys.dic 哈希 → 按 dicrc 读出真实字段下标 → 逐条比对 Misaki 词表的
身份信息（词面、左右连接 ID、成本、发音）→ 在保留原字段的前提下追加书面 kana 列。
kana 为 `*` 的条目（符号、颜文字等）写空串，表示「词典没有书面读音」，绝不退回发音字段猜写。
"""
import argparse
import csv
import gzip
import hashlib
import json
import mmap
import re
import struct
import sys
import zipfile
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def fail(message: str) -> None:
    raise SystemExit(message)


def extract(archive: Path, work: Path, names: list[str]) -> dict[str, Path]:
    # 只解出需要的文件，命名沿用压缩包内的相对路径，避免多目录同名互相覆盖。
    work.mkdir(parents=True, exist_ok=True)
    found: dict[str, Path] = {}
    with zipfile.ZipFile(archive) as zip_file:
        for info in zip_file.namelist():
            base = info.rsplit("/", 1)[-1]
            if base in names:
                target = work / info
                target.parent.mkdir(parents=True, exist_ok=True)
                with zip_file.open(info) as source, target.open("wb") as sink:
                    sink.write(source.read())
                found[base] = target
    missing = [name for name in names if name not in found]
    if missing:
        fail(f"UniDic 压缩包缺少文件：{missing}")
    return found


def regenerate(pin: dict, archive: Path, lexicon: Path, work: Path, output: Path) -> dict:
    if archive.stat().st_size != pin["archive"]["bytes"] or sha256(archive) != pin["archive"]["sha256"].lower():
        fail("UniDic 压缩包与固定版本不一致（大小或 SHA-256 不符）。")
    files = extract(archive, work, ["sys.dic", "dicrc"] + [Path(name).name for name in pin["licenseFiles"]])
    sys_dic = files["sys.dic"]
    if sha256(sys_dic) != pin["sysDic"]["sha256"].lower():
        fail("sys.dic 与固定版本不一致。")

    dicrc = files["dicrc"].read_text(encoding="utf-8-sig")
    indices = {name: int(index) for index, name in re.findall(r";\s*f\[(\d+)\]:\s*(\w+)", dicrc)}
    # 旧 UniDic 2.1.2 的第 17 列不是书面假名；字段下标必须现场从 dicrc 读出并锁定。
    for field, expected in pin["fieldIndex"].items():
        if indices.get(field) != expected:
            fail(f"dicrc 的 {field} 字段下标是 {indices.get(field)}，固定版本是 {expected}。")

    output.mkdir(parents=True, exist_ok=True)
    destination = output / lexicon.name
    surfaces = tokens = differences = 0
    missing_kana = 0
    examples: list[dict] = []
    with sys_dic.open("rb") as handle, mmap.mmap(handle.fileno(), 0, access=mmap.ACCESS_READ) as data:
        magic, version, _, _, _, _, dsize, tsize, _, _ = struct.unpack_from("<10I", data)
        if magic ^ len(data) != 0xEF718F77 or version != 102:
            fail("sys.dic 不是预期的 UniDic 二进制格式。")
        token_start, feature_start = 72 + dsize, 72 + dsize + tsize

        def node(index):
            return struct.unpack_from("<iI", data, 72 + index * 8)

        def lookup(surface: str) -> int:
            # 三角查找与 mecab 的词典布局一致；查不到就是词表与词典不同源。
            base = node(0)[0]
            for byte in surface.encode():
                next_base, check = node(base + byte + 1)
                if check != base:
                    fail(f"词面不在同版 sys.dic 中：{surface!r}")
                base = next_base
            value, check = node(base)
            if check != base or value >= 0:
                fail(f"词面不在同版 sys.dic 中：{surface!r}")
            return -value - 1

        with gzip.open(lexicon, "rt", encoding="utf-8") as source, gzip.GzipFile(destination, "wb", mtime=0) as sink:
            for line in source:
                columns = line.rstrip("\n").split("\t")
                surface = columns[0].replace("\\t", "\t").replace("\\n", "\n").replace("\\r", "\r").replace("\\\\", "\\")
                value = lookup(surface)
                if value & 255 != len(columns) - 1:
                    fail(f"词条数量与 sys.dic 不一致：{surface!r}")
                rows = []
                for index, original in enumerate(columns[1:]):
                    lc, rc, _, cost, feature, _ = struct.unpack_from("<HHHhII", data, token_start + ((value >> 8) + index) * 16)
                    start = feature_start + feature
                    fields = next(csv.reader([data[start:data.find(b"\0", start)].decode("utf-8")]))
                    # 逐条核对左右连接 ID、成本与原始发音，身份对不上就说明数据不同源。
                    if len(fields) != 29 or f"{lc},{rc},{cost},{fields[indices['pron']]}" != original:
                        fail(f"词条身份不一致：{surface!r} {original!r}")
                    kana = fields[indices["kana"]]
                    if kana in ("", "*"):
                        # * 表示词典没有书面读音（符号、颜文字等），写空串交给上层跳过。
                        missing_kana += 1
                        kana = ""
                    rows.append(original + "," + kana)
                    tokens += 1
                    if kana and fields[indices["pron"]] != kana:
                        differences += 1
                        if surface in {"東京", "躊躇", "今日", "凍り付く", "は", "へ"} and len(examples) < 12:
                            examples.append({"surface": surface, "pron": fields[indices["pron"]], "kana": kana})
                sink.write((columns[0] + "\t" + "\t".join(rows) + "\n").encode())
                surfaces += 1
                if surfaces % 200000 == 0:
                    print(f"kana: 已核对 {surfaces} 个词面", flush=True)

    return {
        "dictionary": {"version": pin["version"], "archiveSha256": pin["archive"]["sha256"], "sysDicSha256": pin["sysDic"]["sha256"]},
        "fieldIndex": {name: indices[name] for name in pin["fieldIndex"]},
        "surfaces": surfaces,
        "tokens": tokens,
        "pronKanaDifferences": differences,
        "missingKana": missing_kana,
        "identityMismatches": 0,
        "examples": examples,
        "output": {"name": destination.name, "bytes": destination.stat().st_size, "sha256": sha256(destination)},
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pin", required=True, help="misaki-packs.json")
    parser.add_argument("--archive", required=True, help="UniDic 3.1.0 zip")
    parser.add_argument("--lexicon", required=True, help="从上游包导出的 ja_lexicon.tsv.gz")
    parser.add_argument("--work", required=True, help="解包临时目录（必须在 .temp 下）")
    parser.add_argument("--output", required=True, help="再生产的 ja_lexicon.tsv.gz 输出目录")
    parser.add_argument("--report", required=True, help="报告输出路径")
    arguments = parser.parse_args()
    pins = json.loads(Path(arguments.pin).read_text(encoding="utf-8"))
    report = regenerate(pins["dictionary"], Path(arguments.archive), Path(arguments.lexicon), Path(arguments.work), Path(arguments.output))
    Path(arguments.report).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"kana: {report['surfaces']} 词面 / {report['tokens']} 词条，缺书面读音 {report['missingKana']} 条，输出 {report['output']['bytes']} 字节")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()

