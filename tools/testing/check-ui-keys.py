#!/usr/bin/env python3
"""检查稳定文案键、语言目录、格式参数和富文本。无需游戏文件。"""
import collections
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "src/Mod/UI/Locales"
KEY = r"(?:app|action|choice|panel|potion|search|settings|strategy|summary|tab|text|ui)\.[a-z0-9_]+"
ITEM = re.compile(r"\{(\d+)(?:,-?\d+)?(?::[^{}]+)?\}")
TAG = re.compile(r"\[(/?)(b|i|color)(?:=[^\]]+)?\]")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate key: {key}")
        result[key] = value
    return result


def placeholders(value):
    remaining = ITEM.sub("", value).replace("{{", "").replace("}}", "")
    if "{" in remaining or "}" in remaining:
        raise ValueError("invalid format braces")
    return collections.Counter(int(m.group(1)) for m in ITEM.finditer(value))


def check_tags(value):
    stack = []
    for match in TAG.finditer(value):
        close, name = match.groups()
        if close:
            if not stack or stack.pop() != name:
                raise ValueError(f"unmatched closing tag: {name}")
        else:
            stack.append(name)
    if stack:
        raise ValueError(f"unclosed tags: {stack}")


def main():
    catalogs = {path.stem: json.loads(path.read_text(), object_pairs_hook=unique_object)
                for path in sorted(DIRECTORY.glob("*.json"))}
    reference = catalogs["eng"]
    assert "zhs" in catalogs
    problems = []
    for language, catalog in catalogs.items():
        if not re.fullmatch(r"[a-z]{3}", language):
            problems.append(f"invalid language: {language}")
        for key in sorted(reference.keys() - catalog.keys()):
            problems.append(f"{language}: missing {key}")
        for key in sorted(catalog.keys() - reference.keys()):
            problems.append(f"{language}: extra {key}")
        for key, value in catalog.items():
            if not re.fullmatch(KEY, key):
                problems.append(f"{language}: invalid key {key}")
            if not isinstance(value, str) or not value.strip():
                problems.append(f"{language}: empty/non-string {key}")
                continue
            try:
                if key in reference and placeholders(value) != placeholders(reference[key]):
                    problems.append(f"{language}: placeholders {key}")
                check_tags(value)
            except ValueError as error:
                problems.append(f"{language}: {key}: {error}")
    used = set()
    for root in ("src/Mod", "src/TestHost/ModTesting"):
        for path in (ROOT / root).rglob("*.cs"):
            if any(part in ("bin", "obj", ".godot") for part in path.parts):
                continue
            text = path.read_text()
            used.update(key for key in re.findall(r'"(' + KEY + r')"', text)
                        if not key.endswith(".json"))
            if re.search(r'SolverText\.Format\(\s*\$"', text):
                problems.append(f"old interpolated API: {path.relative_to(ROOT)}")
            for key in re.findall(r'SolverText\.(?:Get|Format)\(\s*"([^"]*)"', text):
                if not re.fullmatch(KEY, key):
                    problems.append(f"non-key argument: {path.relative_to(ROOT)}: {key}")
    for key in sorted(used - reference.keys()):
        problems.append(f"unknown key: {key}")
    for key in sorted(reference.keys() - used):
        problems.append(f"unused key: {key}")
    print(f"languages={len(catalogs)} keys={len(reference)} used={len(used)} errors={len(problems)}")
    for problem in problems:
        print(problem)
    return bool(problems)


if __name__ == "__main__":
    sys.exit(main())
