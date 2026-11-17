#!/usr/bin/env python3
# ─────────────────────────────────────────────────────────────────────────────
# 把本地 deps/ 的子目录同步到 ModelScope 仓库 Noetuu/nanikore-deps：一个子目录 → 仓库同名前缀，
# 只写自己这份，不影响仓库里已有的其他子目录。改了带哈希契约的子目录后，完整闭环三步：
#
#   1. 算哈希（打印全部文件 SHA-256，按目标源码清单的口径生成可直接粘贴的行）：
#        python scripts/sync-deps.py phonetics --print-hashes
#   2. 回填：把输出粘进下方「清单绑定」里对应的 C# 源码文件（忘了回填也能在第 3 步的报错里
#      看到「实际」值，抄进去重跑即可）。
#   3. 核对并上传（默认自动核对绑定的源码清单，通过才上传）：
#        python scripts/sync-deps.py phonetics
#
# 环境与令牌：
#   python -m pip install modelscope_hub      # 上传 SDK（或 modelscope 包，两种都认）
#   $env:MODELSCOPE_API_TOKEN = "<token>" # 令牌在 https://modelscope.cn/my/myaccesstoken 申请；
#                                          # 也可 --token 传（会留在 shell 历史里，不推荐）
# 上传只走白名单（SUBDIR_WHITELISTS）：目录里有旧词典、.staging-* 暂存或再生中间产物也不会被推上去；
# .ms_upload_cache、_source、_build 等缓存与中间产物另外还有内建忽略。
#
# 同步：
#   python scripts/sync-deps.py ocr-onnx onnxruntime phonetics   # 一次多个子目录，各自独立提交
#   python scripts/sync-deps.py README.md                         # 同步 deps/README.md
#   python scripts/sync-deps.py phonetics --dry-run              # 预检：列文件 + 核对，不联网
#   python scripts/sync-deps.py <新目录> --no-verify              # 无契约的新目录：跳过核对直接传
#
# 清单绑定（哈希契约的唯一定义处，脚本自动读取，无需手填；要换别的清单用 --verify <文件>，
# 支持 C# 源码定义或每行「路径 哈希」的纯文本；条目路径按尾缀匹配，三种口径都能对）：
#   deps/phonetics/    → src/NaniKore/Phonetics/ChinesePinyinData.cs       路径口径：相对 phonetics/
#                      → src/NaniKore/Phonetics/Native/NativePhoneticManifest.cs  路径口径：语言 ZIP 内
#   deps/ocr-onnx/     → src/NaniKore/Ocr/Models/OcrDependencies.cs       路径口径：相对 deps/
#   deps/onnxruntime/  → src/NaniKore/Ocr/Models/OcrDependencies.cs
# ─────────────────────────────────────────────────────────────────────────────
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import inspect
import os
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_LOCAL_DIR = REPO_ROOT / 'deps'
DEFAULT_REPO_ID = 'Noetuu/nanikore-deps'

# 子目录 → 哈希契约（C# 源码清单）绑定：脚本按同步目标自动读取核对，不用手填。
# 同一份清单可覆盖多个子目录（OCR 权重与 native DLL 共用 OcrDependencyManifest），
# 同一子目录也可绑定多份清单（phonetics 的中文拼音表与日英原生包各由一份清单管）。
SUBDIR_MANIFESTS = {
    'phonetics': [
        'src/NaniKore/Phonetics/ChinesePinyinData.cs',
        'src/NaniKore/Phonetics/Native/NativePhoneticManifest.cs',
    ],
    'ocr-onnx': ['src/NaniKore/Ocr/Models/OcrDependencies.cs'],
    'onnxruntime': ['src/NaniKore/Ocr/Models/OcrDependencies.cs'],
}

# 子目录 → 上传白名单（相对该子目录的 glob）。只上传这些文件，目录里的其他东西一律不传：
# 旧词典、下载暂存（.staging-*）、再生中间产物都不应因为「目录里有」而被推到远端。
SUBDIR_WHITELISTS = {
    'phonetics': (
        'ja.zip',
        'en.zip',
        'zh_pinyin.txt.gz',
    ),
}

# 本地侧忽略规则：上传工具缓存、再生管线中间产物、下载临时文件等都不应出现在远端。
IGNORED_DIR_NAMES = {'.ms_upload_cache', '_source', '_build', '__pycache__', 'bin', 'obj', '.git'}
IGNORED_FILE_PATTERNS = ('*.download', '*.tmp', '*.log', '*.pyc')

# C# 源码里的清单条目：("path", "HASH") 或 Create("path", "HASH")，自动识别。
MANIFEST_CS_RE = re.compile(r'"(?P<path>[\w./-]+)"\s*,\s*"(?P<hash>[0-9A-Fa-f]{64})"')
# 普通清单文件条目：path  HASH（空格、= 或逗号分隔），# 之后为注释。
MANIFEST_TEXT_RE = re.compile(
    r'^(?P<path>[\w./-]+)\s*(?:[=,]\s*|\s+)(?P<hash>[0-9A-Fa-f]{64})\s*(?:#.*)?$',
    re.MULTILINE,
)

# 退出码：0 成功；1 哈希核对未通过；2 参数/环境问题；3 上传失败。
EXIT_OK, EXIT_VERIFY_FAILED, EXIT_USAGE, EXIT_UPLOAD_FAILED = 0, 1, 2, 3


def iter_local_files(root: Path, whitelist: tuple[str, ...] = ()) -> list[Path]:
    """枚举 root 下应参与同步的文件（跳过缓存与中间产物目录；有白名单时只取名单内的）。"""
    files: list[Path] = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(name for name in dirnames if name not in IGNORED_DIR_NAMES)
        for name in sorted(filenames):
            if any(fnmatch.fnmatch(name, pattern) for pattern in IGNORED_FILE_PATTERNS):
                continue
            path = Path(dirpath) / name
            if whitelist and not any(
                fnmatch.fnmatch(path.relative_to(root).as_posix(), pattern) for pattern in whitelist
            ):
                continue
            files.append(path)
    return files


def sha256_of(path: Path) -> str:
    """计算文件 SHA-256，大写十六进制。"""
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest().upper()


def human_size(size: int) -> str:
    value = float(size)
    for unit in ('B', 'KiB', 'MiB', 'GiB'):
        if value < 1024 or unit == 'GiB':
            return f'{int(value)} B' if unit == 'B' else f'{value:.1f} {unit}'
        value /= 1024
    return f'{value:.1f} GiB'


def parse_manifest(path: Path) -> dict[str, str]:
    """读取 --verify 清单：C# 源码风格或普通文本风格，返回 路径 → SHA-256。"""
    if not path.is_file():
        raise FileNotFoundError(f'找不到清单文件：{path}')
    content = path.read_text(encoding='utf-8')
    manifest = {m.group('path'): m.group('hash').upper() for m in MANIFEST_CS_RE.finditer(content)}
    manifest.update({m.group('path'): m.group('hash').upper() for m in MANIFEST_TEXT_RE.finditer(content)})
    if path.name == 'NativePhoneticManifest.cs':
        manifest = {name: digest for name, digest in manifest.items() if name.endswith('.zip')}
    elif path.name == 'ChinesePinyinData.cs':
        manifest = {name: digest for name, digest in manifest.items() if name == 'zh_pinyin.txt.gz'}
    if not manifest:
        raise ValueError(f'{path} 里没有解析到任何 "路径 + 64 位十六进制哈希" 条目')
    return manifest


def verify_manifest(manifest: dict[str, str], local_root: Path, targets: list[Path]) -> list[str]:
    """核对清单条目；返回错误信息列表（空 = 全部吻合）。

    索引只建在上传白名单内：清单管不着的东西（旧词典、暂存目录）不会影响核对结论，
    也不会因为「目录里多了一个同名文件」而假报哈希不符。同一尾缀命中多份时全部都要对。
    """
    index: dict[str, set[Path]] = {}
    for target in targets:
        whitelist = SUBDIR_WHITELISTS.get(target.name, ())
        for path in iter_local_files(target, whitelist):
            parts = path.relative_to(target).as_posix().split('/')
            for start in range(len(parts)):
                index.setdefault('/'.join(parts[start:]), set()).add(path)
    errors: list[str] = []
    for relative, expected in sorted(manifest.items()):
        matches = sorted(index.get(relative, set()))
        if not matches:
            errors.append(f'  缺失：{relative}（清单要求 SHA-256 {expected}）')
            continue
        for path in matches:
            actual = sha256_of(path)
            if actual != expected:
                errors.append(f'  哈希不符：{relative}\n    清单 {expected}\n    实际 {actual}（{path}）')
    return errors


def print_plan(name: str, target: Path) -> int:
    """打印单个同步目标的清单，返回文件数。"""
    is_file = target.is_file()
    remote_prefix = name if is_file else f'{name}/'
    print(f'{name}  →  {remote_prefix}   （{target}）')
    if is_file:
        print(f'  {name:<52} {human_size(target.stat().st_size):>10}')
        return 1
    files = iter_local_files(target, SUBDIR_WHITELISTS.get(name, ()))
    for path in files:
        remote = f"{name}/{path.relative_to(target).as_posix()}"
        print(f'  {remote:<52} {human_size(path.stat().st_size):>10}')
    if not files:
        print('  （空目录或白名单外，无文件可同步）')
    return len(files)


def print_hash_lines(targets: list[Path], local_dir: Path) -> None:
    """打印各文件 SHA-256，按各清单的路径口径生成可直接粘贴的行。"""
    print('\n各文件 SHA-256（按下面小节的口径复制回对应清单）：')
    for target in targets:
        print(f'\n# {target.name}/ —— 路径相对被同步子目录（纯文本清单或 ChinesePinyinData 口径）：')
        for path in iter_local_files(target, SUBDIR_WHITELISTS.get(target.name, ())):
            print(f'    ("{path.relative_to(target).as_posix()}", "{sha256_of(path)}"),')

        if target.name == 'phonetics':
            for language in ('ja', 'en'):
                archive = target / f'{language}.zip'
                if archive.is_file():
                    print(f'\n# NativePhoneticManifest 口径（{language} 整包）：')
                    print(f'    new NativePhoneticArchive("{language}.zip", "{sha256_of(archive)}"),')

    if any(target.name in ('ocr-onnx', 'onnxruntime') for target in targets):
        print('\n# OcrDependencyManifest 口径（路径相对 deps/；需 Hugging Face 回源的条目自行补第三个 URL 参数）：')
        for target in targets:
            for path in iter_local_files(target, SUBDIR_WHITELISTS.get(target.name, ())):
                print(f'    Create("{path.relative_to(local_dir).as_posix()}", "{sha256_of(path)}"),')


def create_hub_api(token: str):
    """构造 HubApi；兼容经典 modelscope 包与新的 modelscope-hub 包两种导入路径。"""
    try:
        from modelscope.hub.api import HubApi
    except ImportError:
        try:
            from modelscope_hub.api import HubApi
        except ImportError as error:
            raise SystemExit(
                '未安装 modelscope 或 modelscope_hub 包：请先执行  python -m pip install modelscope_hub  后重试。'
            ) from error
    # 令牌统一写环境变量（两代 SDK 都读 MODELSCOPE_API_TOKEN）；能显式传参的版本再显式传一份。
    os.environ['MODELSCOPE_API_TOKEN'] = token
    if 'token' in inspect.signature(HubApi.__init__).parameters:
        return HubApi(token=token)
    return HubApi()


def upload_folder_to_repo(api, repo_id: str, local_dir: Path, path_in_repo: str, commit_message: str) -> None:
    """把 local_dir 里白名单命中的文件上传到 repo_id 的 path_in_repo 前缀下（不影响远端其他前缀）。"""
    whitelist = SUBDIR_WHITELISTS.get(path_in_repo, ())
    parameters = set(inspect.signature(api.upload_folder).parameters)
    kwargs = {
        'repo_id': repo_id,
        'repo_type': 'model',
        'folder_path': str(local_dir),
        'path_in_repo': path_in_repo,
        'commit_message': commit_message,
        'ignore_patterns': ['**/.ms_upload_cache/**', '_source', '_build', '*.download'],
    }
    # 只传当前 SDK 支持的参数，避免 TypeError；仍不支持 path_in_repo 的老 SDK 退化为逐文件上传（仍受白名单约束）。
    supported = {name: value for name, value in kwargs.items() if name in parameters}
    if 'path_in_repo' in parameters and not whitelist:
        api.upload_folder(**supported)
        return
    file_parameters = set(inspect.signature(api.upload_file).parameters)
    for path in iter_local_files(local_dir, whitelist):
        remote = f"{path_in_repo}/{path.relative_to(local_dir).as_posix()}"
        file_kwargs = {
            'repo_id': repo_id,
            'repo_type': 'model',
            'path_or_fileobj': str(path),
            'path_in_repo': remote,
            'commit_message': commit_message,
        }
        api.upload_file(**{name: value for name, value in file_kwargs.items() if name in file_parameters})


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description='把 deps/ 下的子目录或单个文件同步到 ModelScope 仓库。'
    )
    parser.add_argument('sub_items', nargs='*',
                        help='要同步的目录名或文件名（相对 deps/）；不填则同步 deps/ 下所有子目录。')
    parser.add_argument('--local-dir', type=Path, default=DEFAULT_LOCAL_DIR,
                        help=f'本地源目录（默认 {DEFAULT_LOCAL_DIR.relative_to(REPO_ROOT).as_posix()}）。')
    parser.add_argument('--repo-id', default=DEFAULT_REPO_ID,
                        help=f'目标仓库（默认 {DEFAULT_REPO_ID}）。')
    parser.add_argument('--dry-run', action='store_true',
                        help='只列文件并按需核对哈希，不联网、不上传。')
    parser.add_argument('--print-hashes', action='store_true',
                        help='只计算并打印各文件 SHA-256（生成可回填源码清单的行），不联网、不上传。')
    parser.add_argument('--verify', type=Path,
                        help='改用指定清单核对（覆盖子目录的自动绑定）；C# 源码定义或纯文本均可。')
    parser.add_argument('--no-verify', action='store_true',
                        help='跳过哈希核对（仅用于没有绑定契约的新目录）。')
    parser.add_argument('--force', action='store_true',
                        help='哈希核对不通过也强传（仅限确认过的例外，慎用）。')
    parser.add_argument('--token', help='ModelScope 令牌；不传则读环境变量 MODELSCOPE_API_TOKEN。')
    parser.add_argument('--commit-message', help='提交信息；默认按子目录与文件数自动生成。')
    return parser.parse_args()


def resolve_targets(local_dir: Path, sub_items: list[str]) -> list[Path]:
    """解析本地目录或文件目标；未指定时取 deps/ 下全部一级子目录。"""
    if not local_dir.is_dir():
        raise FileNotFoundError(f'找不到本地源目录：{local_dir}')
    if sub_items:
        targets = []
        for name in sub_items:
            target = local_dir / name
            if not target.is_dir() and not target.is_file():
                raise FileNotFoundError(f'找不到目录或文件：{target}')
            targets.append(target)
        return targets
    # 未指定子目录：同步本地源目录下的一级子目录（README 等普通文件不属于同步面）。
    targets = sorted(path for path in local_dir.iterdir() if path.is_dir())
    if not targets:
        raise FileNotFoundError(f'{local_dir} 下没有任何子目录可同步')
    return targets


def main() -> int:
    # 与 C# 侧 UseUtf8Console 对齐：Windows 默认按 GBK 写管道，pwsh 下中文会乱码。
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8')
    args = parse_args()
    try:
        targets = resolve_targets(args.local_dir, args.sub_items)
    except FileNotFoundError as error:
        print(f'参数错误：{error}', file=sys.stderr)
        return EXIT_USAGE

    # 哈希核对清单：默认按子目录自动绑定 C# 源码清单；--verify 显式覆盖；--no-verify 跳过。
    manifest: dict[str, str] = {}
    manifest_sources: list[Path] = []
    if args.verify:
        manifest_sources = [args.verify]
    elif not args.no_verify:
        for target in targets:
            for bound in SUBDIR_MANIFESTS.get(target.name, []):
                if (REPO_ROOT / bound) not in manifest_sources:
                    manifest_sources.append(REPO_ROOT / bound)
    for source in manifest_sources:
        try:
            manifest.update(parse_manifest(source))
        except (FileNotFoundError, ValueError) as error:
            print(f'清单错误：{error}', file=sys.stderr)
            return EXIT_USAGE
    if manifest_sources and not manifest:
        print('绑定到的源码清单里没有可核对的条目。', file=sys.stderr)
        return EXIT_USAGE

    print(f'目标仓库：{args.repo_id}（master 分支）  本地源：{args.local_dir}\n')
    counts = {target.name: print_plan(target.name, target) for target in targets}
    total = sum(counts.values())
    if total == 0:
        print('没有可同步的文件。', file=sys.stderr)
        return EXIT_USAGE
    names = ' '.join(target.name for target in targets)
    print(f'\n共 {total} 个文件，涉及目标：{names}')

    if args.print_hashes:
        print_hash_lines(targets, args.local_dir)
        return EXIT_OK

    if not manifest_sources and not args.no_verify:
        unbound = ' '.join(target.name for target in targets)
        print(f'\n以下子目录没有绑定的哈希契约，跳过核对：{unbound}')
    if manifest:
        errors = verify_manifest(manifest, args.local_dir, targets)
        if errors:
            print('\n哈希核对不通过：')
            print('\n'.join(errors))
            if args.dry_run or not args.force:
                print('已中止（--dry-run 或核对未通过）。确认例外后加 --force 强传。')
                return EXIT_VERIFY_FAILED
            print('--force 生效，忽略哈希问题继续上传。')
        else:
            sources = '、'.join(source.name for source in manifest_sources)
            print(f'哈希核对：{len(manifest)} 个条目全部一致（{sources}）。')

    if args.dry_run:
        print('dry-run 模式，不上传。去掉 --dry-run 实际执行同步。')
        return EXIT_OK

    token = args.token or os.environ.get('MODELSCOPE_API_TOKEN')
    if not token:
        print('缺少 ModelScope 令牌。请设置环境变量 MODELSCOPE_API_TOKEN，或用 --token 传入：\n'
              '  $env:MODELSCOPE_API_TOKEN = "<token>"    # 令牌在 https://modelscope.cn/my/myaccesstoken 申请',
              file=sys.stderr)
        return EXIT_USAGE

    try:
        api = create_hub_api(token)
        for target in targets:
            message = args.commit_message or f"update {target.name} ({counts[target.name]} files)"
            print(f'上传 {target.name}/ …')
            if target.is_file():
                file_parameters = set(inspect.signature(api.upload_file).parameters)
                file_kwargs = {
                    'repo_id': args.repo_id,
                    'repo_type': 'model',
                    'path_or_fileobj': str(target),
                    'path_in_repo': target.name,
                    'commit_message': message,
                }
                api.upload_file(**{name: value for name, value in file_kwargs.items() if name in file_parameters})
            else:
                upload_folder_to_repo(api, args.repo_id, target, target.name, message)
    except SystemExit:
        raise
    except Exception as error:  # noqa: BLE001 —— SDK 异常类型杂，统一收敛为提示后以非零码退出
        print(f'上传失败：{type(error).__name__}: {error}', file=sys.stderr)
        return EXIT_UPLOAD_FAILED

    print(f'完成：https://modelscope.cn/models/{args.repo_id}/tree/master  （已更新：{names}）')
    return EXIT_OK


if __name__ == '__main__':
    raise SystemExit(main())
