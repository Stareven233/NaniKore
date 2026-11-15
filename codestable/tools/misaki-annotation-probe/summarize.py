"""汇总实际 PE 导入表、文件哈希与矩阵结果；不把预期缺口断言通过当成质量通过。"""
import hashlib
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parents[3] / ".temp/misaki-annotation-spike"


def pe_imports(path):
    data = path.read_bytes()
    header = struct.unpack_from("<I", data, 0x3C)[0]
    assert data[header:header + 4] == b"PE\0\0"
    sections, optional_size = struct.unpack_from("<H12xH", data, header + 6)
    optional = header + 24
    assert struct.unpack_from("<H", data, optional)[0] == 0x20B
    import_rva = struct.unpack_from("<I", data, optional + 112 + 8)[0]
    entries = []
    for index in range(sections):
        at = optional + optional_size + index * 40
        virtual_size, virtual_address, raw_size, raw_address = struct.unpack_from("<4I", data, at + 8)
        entries.append((virtual_address, max(virtual_size, raw_size), raw_address))

    def offset(rva):
        for start, size, raw in entries:
            if start <= rva < start + size:
                return raw + rva - start
        raise ValueError(f"RVA 不在节区内：{rva}")

    at = offset(import_rva)
    names = []
    while any(descriptor := struct.unpack_from("<5I", data, at)):
        name_at = offset(descriptor[3])
        names.append(data[name_at:data.index(0, name_at)].decode("ascii"))
        at += 20
    return sorted(names)


def main():
    reports = []
    for path in sorted(ROOT.glob("Misaki*-*.json")):
        content = json.loads(path.read_text(encoding="utf-8-sig"))
        if "libraries" not in content:
            continue
        library = content["libraries"][0]
        reports.append({"report": path.name, "checks": content["checks"], "failures": content["failures"],
                        "cold_first_call_ms": library["runs"][0]["ms"], "warm100_ms": library["warm100Ms"],
                        "reload_ms": library["reloadMs"], "release": library["release"]})
    artifacts = []
    for path in sorted((ROOT / "bin").glob("*/*.dll")):
        if path.name.startswith("Misaki") and path.parent.name == path.stem:
            artifacts.append({"file": path.name, "bytes": path.stat().st_size,
                              "sha256": hashlib.file_digest(path.open("rb"), "sha256").hexdigest(),
                              "imports": pe_imports(path)})
    combined = {name: json.loads((ROOT / name).read_text(encoding="utf-8-sig")) for name in ["combined-aot.json", "combined-aot-invariant.json"]}
    baseline = json.loads((ROOT / "baseline.json").read_text(encoding="utf-8-sig"))
    kana = json.loads((ROOT / "kana-report.json").read_text(encoding="utf-8-sig"))
    english = json.loads((ROOT / "MisakiEnPatched-aot.json").read_text(encoding="utf-8-sig"))["libraries"][0]
    runs = {run["text"]: run["result"]["tokens"] for run in english["runs"]}
    everyday = next(token for token in runs["I read every day."] if token["surface"] == "read")
    metal = [token for token in runs["He can lead the way with a lead pipe."] if token["surface"] == "lead"][1]
    missing_common = [token["surface"] for token in runs["The water is better in the city."] if token["surface"] in {"water", "better", "city"} and token["reading"] is None]
    # 与架构断言分开记录语言质量，避免退出 0 被误解成英语已能切换生产。
    quality = {"present_read": {"expected": "ˈriːd", "actual": everyday["reading"]},
               "metal_lead": {"expected": "ˈled", "actual": metal["reading"]}, "unannotated_common_words": missing_common,
               "rejected_lexicon_variants": baseline["ipaAudit"]["rejected"], "context_stress_candidates_reviewed": False}
    quality["english_ready"] = all(quality[name]["expected"] == quality[name]["actual"] for name in ["present_read", "metal_lead"]) and not missing_common and quality["rejected_lexicon_variants"] == 0 and quality["context_stress_candidates_reviewed"]
    summary = {"artifacts": artifacts, "matrix": reports, "combined": {name: {"checks": value["checks"], "failures": value["failures"]} for name, value in combined.items()},
               "ipaAudit": baseline["ipaAudit"], "kana": kana, "qualityGates": quality}
    (ROOT / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
