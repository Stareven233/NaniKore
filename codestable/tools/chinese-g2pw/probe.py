"""隔离运行官方 g2pW；本工具不下载、不安装、不修改上游源码。"""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import sys
import time
import traceback

# 期待值先于推理固定，禁止由预测反向生成。
CASES = (
    ("他还在这里", {1: "hai2"}),
    ("把书还给我", {2: "huan2"}),
    ("这里的周长大于十米", {4: "chang2", 5: "da4"}),
    ("孩子长大", {2: "zhang3", 3: "da4"}),
    ("罅缝", {0: "xia4", 1: "feng4"}),
)
EXPECTED_ZIP_SHA256 = "699f3c1fd7fb0e2c2d49ed2486826fd5bff233fee7759350a91c3b49aedc4ed2"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="Unmodified pinned GitYCC/g2pW checkout")
    parser.add_argument("--revision", required=True, help="Full official upstream commit SHA")
    parser.add_argument("--model", type=Path, required=True, help="Extracted official model directory")
    parser.add_argument("--model-zip", type=Path, required=True)
    parser.add_argument("--packages", type=Path, default=Path("artifacts/chinese-g2pw/python-packages"))
    parser.add_argument("--output", type=Path, default=Path("artifacts/chinese-g2pw/predictions.json"))
    parser.add_argument("--repeats", type=int, default=3)
    args = parser.parse_args()
    if args.repeats < 1:
        parser.error("--repeats must be positive")
    report = {
        "status": "not_run",
        "python": sys.version,
        "platform": platform.platform(),
        "executable": sys.executable,
        "source_url": "https://github.com/GitYCC/g2pW",
        "model_url": "https://huggingface.co/YC-Chen/g2pW",
        "declared_revision": args.revision,
        "model_zip_expected_sha256": EXPECTED_ZIP_SHA256,
        "expectations": [{"text": text, "codepoint_expectations": expected} for text, expected in CASES],
        "boundaries": [
            "Five fixed Mandarin default-reading examples only; no production integration.",
            "No normalization, manual pronunciation overrides, or fallback predictions.",
            "UTF-16 offsets describe original input; converter output must be one item per code point.",
            "Latency includes official converter preprocessing and inference; downloads/imports excluded from per-call latency.",
            "All fixed inputs are BMP; supplementary-plane alignment is not empirically tested.",
            "Source revision is caller-declared; file hashes are evidence, not proof of official provenance.",
        ],
    }
    try:
        actual_hash = sha256(args.model_zip)
        report["model_zip_actual_sha256"] = actual_hash
        if actual_hash != EXPECTED_ZIP_SHA256:
            raise ValueError("Official model ZIP SHA256 mismatch")
        report["source_hashes"] = {
            str(p.relative_to(args.source)): sha256(p)
            for p in sorted(args.source.rglob("*"))
            if p.is_file() and ".git" not in p.parts and "__pycache__" not in p.parts
        }
        report["model_hashes"] = {
            str(p.relative_to(args.model)): sha256(p)
            for p in sorted(args.model.rglob("*")) if p.is_file()
        }
        # 禁止导入写回官方源码；依赖仅从指定隔离目录优先加载。
        sys.dont_write_bytecode = True
        sys.path[:0] = [str(args.source.resolve()), str(args.packages.resolve())]
        report["dependencies"] = sorted(
            ({"name": d.metadata["Name"], "version": d.version}
             for d in importlib.metadata.distributions(path=[str(args.packages.resolve())])),
            key=lambda item: item["name"].lower(),
        )
        started = time.perf_counter()
        from g2pw import G2PWConverter
        report["import_ms"] = (time.perf_counter() - started) * 1000
        started = time.perf_counter()
        converter = G2PWConverter(model_dir=str(args.model.resolve()) + os.sep, style="pinyin")
        report["initialization_ms"] = (time.perf_counter() - started) * 1000
        report["cases"] = []
        for text, expected in CASES:
            runs = []
            for _ in range(args.repeats):
                started = time.perf_counter()
                raw = converter(text)
                elapsed = (time.perf_counter() - started) * 1000
                if len(raw) != 1 or len(raw[0]) != len(text):
                    raise ValueError(f"Unexpected converter output alignment: {raw!r}")
                readings = list(raw[0])
                utf16 = 0
                mapping = []
                for index, (char, reading) in enumerate(zip(text, readings, strict=True)):
                    units = len(char.encode("utf-16-le")) // 2
                    mapping.append({"codepoint_index": index, "utf16_start": utf16,
                                    "utf16_length": units, "char": char, "reading": reading})
                    utf16 += units
                checks = [{"codepoint_index": index, "expected": value,
                           "actual": readings[index], "pass": readings[index] == value}
                          for index, value in expected.items()]
                runs.append({"latency_ms": elapsed, "mapping": mapping, "checks": checks,
                             "pass": all(check["pass"] for check in checks)})
            report["cases"].append({"text": text, "runs": runs,
                                    "pass": all(run["pass"] for run in runs)})
        report["status"] = "pass" if all(case["pass"] for case in report["cases"]) else "fail"
    except Exception:
        report["status"] = "blocked"
        report["error"] = traceback.format_exc()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": report["status"], "output": str(args.output)}, ensure_ascii=False))
    return 0 if report["status"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
