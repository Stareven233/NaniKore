#!/usr/bin/env python3
# 更新 config.yaml 根级 version 字段；只替换版本值，保持其余配置与格式不变。
from __future__ import annotations

import argparse
import re
from pathlib import Path

# 只匹配行首且无缩进的 version 字段，确保只更新根级字段。
VERSION_LINE_RE = re.compile(
    r'^(version:\s*)(?P<quote>["\']?)(?P<value>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:\+(?:0|[1-9]\d*))?)(?P<rest>\s*(?:#.*)?$)',
    re.MULTILINE,
)
VERSION_VALUE_RE = re.compile(
    r'^(?:(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:\+(?:0|[1-9]\d*))?)$'
)


def parse_version(value: str) -> tuple[int, int, int, int | None]:
    """解析 major.minor.patch 或 major.minor.patch+build。"""
    if not VERSION_VALUE_RE.match(value):
        raise ValueError(f'Invalid version format: {value}')

    base_value, _, build_part = value.partition('+')
    parts = base_value.split('.')
    major, minor, patch = map(int, parts)
    return major, minor, patch, None if build_part == '' else int(build_part)


def format_version(major: int, minor: int, patch: int, build: int | None) -> str:
    # build 为 0 或省略时，不输出 +build，保持项目约定。
    base = f'{major}.{minor}.{patch}'
    return f'{base}+{build}' if build and build != 0 else base


def bump_version(current: str, part: str) -> str:
    major, minor, patch, build = parse_version(current)

    if part == 'major':
        return format_version(major + 1, 0, 0, 0)
    if part == 'minor':
        return format_version(major, minor + 1, 0, 0)
    if part == 'patch':
        return format_version(major, minor, patch + 1, 0)
    if part == 'build':
        return format_version(major, minor, patch, (0 if build is None else build) + 1)

    raise ValueError(f'Unsupported bump part: {part}')


def validate_new_version(new_version: str) -> None:
    """校验并规范化写入结果，避免把 build=0 写回配置文件。"""
    parse_version(new_version)
    if new_version.endswith('+0'):
        raise ValueError('Build part must be omitted when zero; use major.minor.patch instead of +0')


def update_config(config_path: Path, new_version: str) -> str:
    validate_new_version(new_version)

    content = config_path.read_text(encoding='utf-8')
    match = VERSION_LINE_RE.search(content)
    if not match:
        raise FileNotFoundError(f'No root-level version field was found in {config_path}')

    old_value = match.group('value')
    updated_content = VERSION_LINE_RE.sub(
        lambda m: f"{m.group(1)}{m.group('quote')}{new_version}{m.group('rest')}",
        content,
        count=1,
    )

    # 使用临时文件 + 原子重命名，降低写入失败时配置被截断的概率。
    temp_path = config_path.with_suffix(config_path.suffix + '.tmp')
    try:
        temp_path.write_text(updated_content, encoding='utf-8')
        temp_path.replace(config_path)
    finally:
        if temp_path.exists():
            temp_path.unlink()

    return old_value


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description='Update the root version field in config.yaml.')
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument(
        '--bump',
        choices=['major', 'minor', 'patch', 'build'],
        help='Increment one version component; higher increments reset lower components.',
    )
    group.add_argument(
        '--version',
        help='Overwrite the entire version; build=0 must be omitted.',
    )
    parser.add_argument(
        '--config',
        type=Path,
        default=Path(__file__).resolve().parents[1] / 'config.yaml',
        help='Path to config.yaml (default: repository root config.yaml).',
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    new_version = args.version if args.version else bump_version(
        next(VERSION_LINE_RE.search(args.config.read_text(encoding='utf-8')).group('value') for _ in [0]),
        args.bump,
    )
    old_value = update_config(args.config, new_version)
    print(f'Updated version: {old_value} -> {new_version}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
