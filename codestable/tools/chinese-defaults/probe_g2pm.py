"""只读验证固定 g2pM 源码；固定期望与预测分开，输出原始逐字读音及 UTF-16 偏移。"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import platform
import subprocess
import sys
import time

REVISION = "170526efad0a3ef9b55a9ad4579f73218f9be06c"


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="必须是尚不存在的报告路径")
    parser.add_argument("--cases", type=Path, default=Path(__file__).with_name("cases.json"))
    args = parser.parse_args()
    if args.output.exists():
        parser.error("拒绝覆盖报告")
    # 先核对提交与实际输入，再导入上游；pickle 仅限固定来源的原件。
    actual = subprocess.check_output(["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip()
    if actual != REVISION:
        parser.error(f"源码提交不匹配: {actual}")
    subprocess.run(["git", "-C", str(args.source), "diff", "--exit-code", "HEAD", "--", "g2pM", "LICENSE"], check=True)
    untracked = subprocess.check_output(["git", "-C", str(args.source), "ls-files", "--others", "--exclude-standard", "--", "g2pM"], text=True)
    if untracked.strip():
        parser.error("上游包存在未跟踪输入")
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(args.source.resolve()))
    report = {"source": "https://github.com/kakaobrain/g2pM", "revision": actual,
              "python": sys.version, "platform": platform.platform(), "cases_sha256": sha256(args.cases),
              "inputs": {str(p.relative_to(args.source)): {"sha256": sha256(p), "bytes": p.stat().st_size}
                         for p in sorted((args.source / "g2pM").glob("*")) if p.is_file()}, "cases": []}
    import numpy as np
    from g2pM import G2pM
    report["numpy"] = np.__version__
    started = time.perf_counter()
    model = G2pM()
    report["initialization_ms"] = (time.perf_counter() - started) * 1000
    # 不修正、重排、遮掩模型结果；越出字典候选集合也单独记录。
    for case in json.loads(args.cases.read_text(encoding="utf-8")):
        text = case["text"]
        started = time.perf_counter()
        readings = model(text, char_split=True)
        elapsed = (time.perf_counter() - started) * 1000
        aligned = len(readings) == len(text)
        mapping, utf16 = [], 0
        for index, char in enumerate(text):
            length = len(char.encode("utf-16-le", errors="surrogatepass")) // 2
            reading = readings[index] if aligned else None
            candidates = model.cedict.get(char, [])
            mapping.append({"index": index, "utf16_start": utf16, "utf16_length": length,
                            "char": char, "reading": reading, "candidates": candidates,
                            "in_candidates": reading in candidates if candidates else None})
            utf16 += length
        checks = [{"index": index, "expected": expected,
                   "actual": readings[index] if aligned else None,
                   "pass": aligned and readings[index] == expected} for index, expected in case["targets"]]
        report["cases"].append({**case, "mapping": mapping, "latency_ms": elapsed,
                                "aligned": aligned, "checks": checks, "pass": all(c["pass"] for c in checks)})
    report["passed"] = sum(c["pass"] for c in report["cases"])
    report["total"] = len(report["cases"])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps({"passed": report["passed"], "total": report["total"], "output": str(args.output)}))
    return 0 if report["passed"] == report["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
