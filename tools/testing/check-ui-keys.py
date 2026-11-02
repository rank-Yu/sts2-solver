#!/usr/bin/env python3
"""Check that every SolverText source string is in English.json and vice versa."""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / 'src/Mod/UI/English.json'


def unescape(text: str) -> str:
    return (text.replace('\\"', '"').replace('\\\\', '\\')
                .replace('\\n', '\n').replace('\\r', '\r').replace('\\t', '\t'))


def read_csharp_string(source: str, index: int) -> tuple[str, int]:
    """Read a normal C# string; index points at the opening quote."""
    assert source[index] == '"'
    index += 1
    out = []
    while index < len(source):
        c = source[index]
        if c == '\\':
            out.append(source[index:index + 2])
            index += 2
            continue
        if c == '"':
            return unescape(''.join(out)), index + 1
        out.append(c)
        index += 1
    raise ValueError('unterminated string')


def parse_interpolated(source: str, index: int) -> tuple[str, int]:
    """Parse $"..." or $@"..." starting at the opening quote and return the format key."""
    assert source[index] == '"'
    index += 1
    out = []
    holes = 0
    while index < len(source):
        c = source[index]
        if c == '\\':
            out.append(source[index:index + 2])
            index += 2
            continue
        if c == '"':
            return unescape(''.join(out)), index + 1
        if c == '{':
            if index + 1 < len(source) and source[index + 1] == '{':
                out.append('{{')
                index += 2
                continue
            depth = 1
            cursor = index + 1
            in_string = False
            while cursor < len(source) and depth:
                ch = source[cursor]
                if in_string:
                    if ch == '\\':
                        cursor += 2
                        continue
                    if ch == '"':
                        in_string = False
                    cursor += 1
                    continue
                if ch == '"':
                    in_string = True
                elif ch == '{':
                    depth += 1
                elif ch == '}':
                    depth -= 1
                cursor += 1
            if depth:
                raise ValueError('unterminated interpolation hole')
            hole = source[index + 1:cursor - 1]
            fmt = None
            depth = 0
            in_string = False
            for pos, ch in enumerate(hole):
                if in_string:
                    if ch == '\\':
                        continue
                    if ch == '"':
                        in_string = False
                    continue
                if ch == '"':
                    in_string = True
                elif ch in '({[':
                    depth += 1
                elif ch in ')}]':
                    depth -= 1
                elif ch == ':' and depth == 0:
                    fmt = hole[pos + 1:].strip()
                    break
            out.append('{%d%s}' % (holes, ':' + fmt if fmt else ''))
            holes += 1
            index = cursor
            continue
        if c == '}':
            if index + 1 < len(source) and source[index + 1] == '}':
                out.append('}}')
                index += 2
                continue
            raise ValueError('unexpected }')
        out.append(c)
        index += 1
    raise ValueError('unterminated interpolated string')


def parse_calls(text: str) -> set[str]:
    keys: set[str] = set()
    pattern = re.compile(r'SolverText\.(Get|Format)\s*\(')
    for match in pattern.finditer(text):
        i = match.end()
        while i < len(text) and text[i] in ' \t\r\n':
            i += 1
        if match.group(1) == 'Get' and text[i:i + 1] not in ('$', '"'):
            # Literal-only conditional, e.g. Get(disabled ? "on" : "off"). Switch
            # expressions and mapped variables are covered by parse_mapping_values.
            depth = 1
            cursor = i
            while cursor < len(text):
                ch = text[cursor]
                if ch == '(':
                    depth += 1
                elif ch == ')':
                    depth -= 1
                    if depth == 0:
                        break
                cursor += 1
            argument = text[i:cursor]
            if '?' in argument and 'switch' not in argument:
                for literal in re.finditer(r'"(?:\\.|[^"\\])*"', argument):
                    keys.add(unescape(literal.group(0)[1:-1]))
            continue
        if text.startswith('$"', i):
            key, _ = parse_interpolated(text, i + 1)
        elif text[i:i + 1] == '"':
            key, _ = read_csharp_string(text, i)
        else:
            continue
        keys.add(key)
    return keys


def parse_mapping_values(text: str) -> set[str]:
    keys = set()
    for match in re.finditer(r'\["[^"]*"\]\s*=\s*"((?:[^"\\]|\\.)*)"', text):
        keys.add(unescape(match.group(1)))
    return keys


def main() -> int:
    catalog = json.loads(CATALOG.read_text())
    used: set[str] = set()
    for path in sorted((ROOT / 'src').rglob('*.cs')):
        text = path.read_text()
        used |= parse_calls(text)
        used |= parse_mapping_values(text)
    missing = sorted(used - catalog.keys())
    unused = sorted(catalog.keys() - used)
    print(f'catalog={len(catalog)} used={len(used)} missing={len(missing)} unused={len(unused)}')
    for key in missing:
        print(f'MISSING {key!r}')
    for key in unused:
        print(f'UNUSED  {key!r}')
    return 1 if missing or unused else 0


if __name__ == '__main__':
    sys.exit(main())
