#!/usr/bin/env python3
# 更新项目文件中的产品版本；只替换 Version 节点，保持其余 XML 格式不变。
from __future__ import annotations

import argparse
import re
from pathlib import Path
import xml.etree.ElementTree as ET

VERSION_TAG_RE = re.compile(r'(<Version>)([^<]+)(</Version>)')
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


def read_project_version(project_path: Path) -> str:
    root = ET.parse(project_path).getroot()
    for group in root.findall('PropertyGroup'):
        version = group.findtext('Version')
        if version:
            return version.strip()
    raise FileNotFoundError(f'No Version property was found in {project_path}')


def update_project(project_path: Path, new_version: str) -> str:
    validate_new_version(new_version)

    content = project_path.read_text(encoding='utf-8')
    match = VERSION_TAG_RE.search(content)
    if not match:
        raise FileNotFoundError(f'No Version property was found in {project_path}')

    old_value = match.group(2)
    updated_content = VERSION_TAG_RE.sub(
        lambda m: f'{m.group(1)}{new_version}{m.group(3)}',
        content,
        count=1,
    )

    temp_path = project_path.with_suffix(project_path.suffix + '.tmp')
    try:
        temp_path.write_text(updated_content, encoding='utf-8')
        temp_path.replace(project_path)
    finally:
        if temp_path.exists():
            temp_path.unlink()

    return old_value


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description='Update the project product version.')
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
        '--project',
        type=Path,
        default=Path(__file__).resolve().parents[1] / 'src' / 'NaniKore' / 'NaniKore.csproj',
        help='Path to the project file (default: src/NaniKore/NaniKore.csproj).',
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    project_path = args.project
    new_version = args.version if args.version else bump_version(read_project_version(project_path), args.bump)
    old_value = update_project(project_path, new_version)
    print(f'Updated version: {old_value} -> {new_version}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
