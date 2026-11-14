"""只读评估“纯数据词组方案”对中文固定目标的默认选音。

方案：pypinyin 词组词典 + 单字频率默认；繁体先 OpenCC t2s 归一查音、展示仍用原文。
不修改生产代码/词典，不联网，不做 ML 推理。固定期望先于测评，逐项 PASS/FAIL。
输出 UTF-8 JSON 报告；绝不把中文写向控制台（Windows 控制台 GBK 会崩）。

复跑（隔离依赖目录，禁止全局 pip）：
    python -m pip install --target <PKGS> pypinyin==0.55.0 opencc-python-reimplemented==0.1.7
    set PYTHONPATH=<PKGS>
    python codestable/tools/chinese-phrase-eval/evaluate.py --packages <PKGS> --output artifacts/chinese-phrase-eval/report.json
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

# 固定期望直接镜像 codestable/tools/phonetics-baseline/Samples.cs 的 zh-* 目标（13 个 span）。
# 不由 pypinyin 运行结果反向生成。Extra=False 为核心目标，Extra=True 为额外语义反例（不计核心）。
CORE_CASES = [
    {"id": "zh-paihuai", "text": "俳佪", "targets": [(0, 2, "pái huái")]},
    {"id": "zh-juyu", "text": "龃龉", "targets": [(0, 2, "jǔ yǔ")]},
    {"id": "zh-bank", "text": "不行就去银行", "targets": [(0, 2, "bù xíng"), (4, 2, "yín háng")]},
    {"id": "zh-hai", "text": "他还在这里。", "targets": [(1, 1, "hái")]},
    {"id": "zh-huan", "text": "把书还给我。", "targets": [(2, 1, "huán")]},
    {"id": "zh-grow", "text": "孩子长大了。", "targets": [(2, 2, "zhǎng dà")]},
    {"id": "zh-long", "text": "这里的周长大于十米。", "targets": [(4, 2, "cháng dà")]},
    {"id": "zh-traditional", "text": "銀行", "targets": [(0, 2, "yín háng")]},
    {"id": "zh-simplified", "text": "银行", "targets": [(0, 2, "yín háng")]},
    {"id": "zh-xiafeng", "text": "罅缝", "targets": [(0, 2, "xià fèng")]},
    {"id": "zh-repeat-mixed", "text": "龃龉\r\nHello 123! 龃龉\n龃龉", "targets": [(0, 2, "jǔ yǔ")]},
    {"id": "zh-extension", "text": "𠮷𠀀龃龉", "targets": [(4, 2, "jǔ yǔ")]},
]
EXTRA_CASES = [
    {"id": "x-return-object", "text": "请把钱还我。", "targets": [(3, 1, "huán")]},
    {"id": "x-return-money", "text": "他终于还钱了。", "targets": [(3, 1, "huán")]},
    {"id": "x-sewing", "text": "她正在缝补衣服。", "targets": [(3, 1, "féng")]},
    {"id": "x-gap", "text": "墙上的缝隙很大。", "targets": [(3, 1, "fèng")]},
]


def utf16_units(ch: str) -> int:
    return len(ch.encode("utf-16-le", errors="surrogatepass")) // 2


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def readings_tone(text: str, Style):
    """逐 code point 返回带声调符号的读音；未知字保留原字。"""
    import pypinyin
    # errors=list: 非汉字（标点/空白/拉丁/扩展字）按 code point 逐个展开，保证与输入逐字对齐
    return [item[0] for item in pypinyin.pinyin(text, style=Style.TONE, heteronym=False, errors=lambda x: list(x))]


def build_entries(original: str, lookup_text: str, readings: list[str]):
    """用原文 code point 的 UTF-16 偏移，读取按 lookuptext 对齐的读音。

    lookup_text 是 t2s 归一后的串；只有与原文 code point 数一致才逐位映射，否则返回 None。
    读音来自 lookup_text，但展示底字与偏移始终取 original。
    """
    if len(lookup_text) != len(original):
        return None
    entries = []
    utf16 = 0
    for ch, reading in zip(original, readings, strict=True):
        units = utf16_units(ch)
        entries.append({"char": ch, "utf16_start": utf16, "utf16_length": units, "reading": reading})
        utf16 += units
    return entries


def span_reading(entries, start: int, length: int) -> str | None:
    picked = [e["reading"] for e in entries if e["utf16_start"] >= start and e["utf16_start"] + e["utf16_length"] <= start + length]
    if not picked:
        return None
    return " ".join(picked)


def evaluate(cases, entries_of, mode):
    results, total, passed = [], 0, 0
    for case in cases:
        entries = entries_of(case["text"])
        checks = []
        for start, length, expected in case["targets"]:
            total += 1
            actual = span_reading(entries, start, length) if entries else None
            ok = actual == expected
            passed += 1 if ok else 0
            checks.append({"start": start, "length": length, "expected": expected, "actual": actual, "pass": ok})
        results.append({"id": case["id"], "text": case["text"], "mode": mode, "checks": checks,
                        "pass": all(c["pass"] for c in checks)})
    return {"total": total, "passed": passed, "results": results}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--packages", type=Path, required=True, help="隔离依赖目录（须含 pypinyin/opencc）")
    parser.add_argument("--output", type=Path, required=True, help="UTF-8 JSON 报告路径（须不存在）")
    args = parser.parse_args()
    if args.output.exists():
        parser.error("拒绝覆盖报告")

    import sys
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(args.packages.resolve()))
    import pypinyin
    from pypinyin import Style
    from opencc import OpenCC

    cc = OpenCC("t2s")

    def entries_raw(text):
        return build_entries(text, text, readings_tone(text, Style))

    def entries_t2s(text):
        simplified = cc.convert(text)
        readings = readings_tone(simplified, Style)
        built = build_entries(text, simplified, readings)
        if built is None:  # t2s 改变 code point 数：退回原文，报告里如实记录
            built = build_entries(text, text, readings_tone(text, Style))
        return built

    report = {
        "engine": "pypinyin + opencc(t2s)",
        "pypinyin_version": pypinyin.__version__,
        "note": "纯数据词组方案评估；不修改生产；固定期望镜像 Samples.cs",
        "mode_raw": evaluate(CORE_CASES + EXTRA_CASES, entries_raw, "raw"),
        "mode_t2s": evaluate(CORE_CASES + EXTRA_CASES, entries_t2s, "t2s"),
        "core_ids": [c["id"] for c in CORE_CASES],
        "data_sha256": {},
    }
    # 记录 pypinyin 数据文件指纹，便于复跑核对
    pkg_root = args.packages.resolve() / "pypinyin"
    for name in ("phrases_dict.json", "pinyin_dict.json"):
        p = pkg_root / name
        if p.exists():
            report["data_sha256"][name] = sha256(p)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.write("\n")

    # 控制台只输出 ASCII 摘要；核心未全过时退出 1（存在明确偏差，非全绿验收）
    core = set(report["core_ids"])
    core_all_pass = True
    for mode in ("mode_raw", "mode_t2s"):
        m = report[mode]
        core_pass = sum(1 for r in m["results"] if r["id"] in core for c in r["checks"] if c["pass"])
        core_total = sum(1 for r in m["results"] if r["id"] in core for c in r["checks"])
        print(f"{mode}: overall {m['passed']}/{m['total']}  core {core_pass}/{core_total}")
        if mode == "mode_t2s" and core_pass != core_total:
            core_all_pass = False
    return 0 if core_all_pass else 1


if __name__ == "__main__":
    raise SystemExit(main())
