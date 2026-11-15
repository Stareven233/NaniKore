"""按固定清单与实际产物写 pack.json：字节、SHA-256、PE 普通导入表、导出、资源与来源版本。

记录的是本次实测值，不承诺 NativeAOT 产物字节可复现；重复运行只会覆写这份清单。
"""
import argparse
import hashlib
import json
import struct
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def pe_imports(path: Path) -> list[str]:
    # 只读普通导入表；这是「不带 CRT 私有依赖」的静态证据，不是动态加载依赖全覆盖。
    data = path.read_bytes()
    header = struct.unpack_from("<I", data, 0x3C)[0]
    if data[header:header + 4] != b"PE\0\0":
        raise SystemExit(f"不是 PE 文件：{path}")
    sections, optional_size = struct.unpack_from("<H12xH", data, header + 6)
    optional = header + 24
    if struct.unpack_from("<H", data, optional)[0] != 0x20B:
        raise SystemExit(f"不是 PE32+（win-x64）文件：{path}")
    import_rva = struct.unpack_from("<I", data, optional + 112 + 8)[0]
    sections_at = optional + optional_size
    ranges = []
    for index in range(sections):
        at = sections_at + index * 40
        virtual_size, virtual_address, raw_size, raw_address = struct.unpack_from("<4I", data, at + 8)
        ranges.append((virtual_address, max(virtual_size, raw_size), raw_address))

    def offset(rva: int) -> int:
        for start, size, raw in ranges:
            if start <= rva < start + size:
                return raw + rva - start
        raise SystemExit(f"RVA 不在任何节区内：{rva}")

    at = offset(import_rva)
    names = []
    while any(descriptor := struct.unpack_from("<5I", data, at)):
        name_at = offset(descriptor[3])
        names.append(data[name_at:data.index(0, name_at)].decode("ascii"))
        at += 20
    return sorted(names)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pin", required=True)
    parser.add_argument("--pack", required=True, choices=["ja", "en"])
    parser.add_argument("--resources", required=True, help="本包实际嵌入的资源目录")
    parser.add_argument("--output", required=True, help="包输出目录")
    parser.add_argument("--kana-report", help="日语再生报告")
    arguments = parser.parse_args()
    pin = json.loads(Path(arguments.pin).read_text(encoding="utf-8"))
    definition = pin["packs"][arguments.pack]
    output = Path(arguments.output)
    library = output / definition["library"]
    if not library.exists():
        raise SystemExit(f"缺少原生包：{library}")
    resources = []
    for path in sorted(Path(arguments.resources).glob("*.gz")):
        entry = {"name": path.name, "bytes": path.stat().st_size, "sha256": sha256(path)}
        if path.name in definition["resources"] and definition["regenerated"].get(path.name):
            entry["derived"] = definition["regenerated"][path.name]
        resources.append(entry)
    embedded = sorted(entry["name"] for entry in resources)
    if embedded != sorted(definition["resources"]):
        raise SystemExit(f"资源白名单不符：实际 {embedded}，清单 {sorted(definition['resources'])}")
    manifest = {
        "schema": pin["schema"],
        "language": definition["language"],
        "abi": pin["abi"],
        "architecture": pin["architecture"],
        "library": definition["library"],
        "bytes": library.stat().st_size,
        "sha256": sha256(library),
        "exports": pin["exports"],
        "imports": pe_imports(library),
        "resources": resources,
        "toolchain": pin["toolchain"],
        "upstream": {
            "package": pin["upstream"]["package"],
            "version": pin["upstream"]["version"],
            "commit": pin["upstream"]["commit"],
            "license": pin["upstream"]["license"],
            "nupkgSha256": pin["upstream"]["nupkg"]["sha256"],
            "assemblySha256": pin["upstream"]["assembly"]["sha256"],
            "patchSha256": pin["upstream"]["patch"]["sha256"],
            "sources": definition["sources"],
        },
        "licenses": ["licenses/MisakiSharp-LICENSE.txt", "licenses/MisakiSharp-NOTICE.txt", "licenses/DERIVED.md"],
    }
    if arguments.pack == "ja":
        manifest["dictionary"] = {
            "name": pin["dictionary"]["name"],
            "version": pin["dictionary"]["version"],
            "source": pin["dictionary"]["source"],
            "license": pin["dictionary"]["license"],
            "archiveSha256": pin["dictionary"]["archive"]["sha256"],
            "sysDicSha256": pin["dictionary"]["sysDic"]["sha256"],
            "fieldIndex": pin["dictionary"]["fieldIndex"],
            "licenses": ["licenses/UniDic-BSD.txt", "licenses/UniDic-COPYING.txt", "licenses/UniDic-dicrc.txt"],
        }
        report = json.loads(Path(arguments.kana_report).read_text(encoding="utf-8"))
        manifest["regeneration"] = {key: report[key] for key in ["surfaces", "tokens", "pronKanaDifferences", "missingKana", "identityMismatches"]}
    (output / "pack.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"pack": arguments.pack, "bytes": manifest["bytes"], "sha256": manifest["sha256"], "imports": manifest["imports"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()

