"""Render the spec's name tables from registry.json.

Every table of names in the spec sits between a marker line naming a JSON path into registry.json (dot-separated) and a
closing marker:

    <!-- registry:manifest.fields -->
    ...generated table...
    <!-- /registry -->

Run without arguments to rewrite every table under this folder; run with --check to exit 1 if any table differs from
what the registry gives (nothing is written then). Python 3.12, standard library only.
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
REGISTRY = HERE / "registry.json"
BLOCK = re.compile(r"(<!-- registry:([A-Za-z0-9_.]+) -->\n)(.*?)(<!-- /registry -->)", re.S)

# Column headings only; the names themselves always come from the registry.
HEADINGS = {
    "roles.signing": "Signing role",
    "roles.keys": "Key role",
    "manifest.values": "Field",
    "profiles": "Profile",
    "certificate.confidences": "Confidence (thousandths)",
}


def lookup(registry: dict, path: str):
    value = registry
    for part in path.split("."):
        if not isinstance(value, dict) or part not in value:
            raise KeyError(f"registry.json has no '{path}'")
        value = value[part]
    return value


def heading(path: str) -> str:
    if path in HEADINGS:
        return HEADINGS[path]
    last = path.split(".")[-1]
    words = re.sub(r"(?<=[a-z])(?=[A-Z])", " ", last).lower()
    if words.endswith("s") and not words.endswith("ss"):
        words = words[:-1]
    return words[:1].upper() + words[1:]


def cell(value) -> str:
    if isinstance(value, bool):
        return "yes" if value else "no"
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, str):
        return f"`{value}`"
    if isinstance(value, list):
        return ", ".join(cell(v) for v in value)
    if isinstance(value, dict):
        return "; ".join(f"`{k}`: {cell(v)}" for k, v in value.items())
    raise TypeError(f"cannot render {value!r}")


def text_cell(value) -> str:
    """A free-text value (such as refusal words) shown as text, not as a name."""
    return str(value).replace("|", "\\|")


def table(path: str, value) -> str:
    rows: list[list[str]]
    if isinstance(value, list):
        head = [heading(path)]
        rows = [[cell(v)] for v in value]
    elif isinstance(value, dict) and all(isinstance(v, list) for v in value.values()):
        head = [heading(path), "Values"]
        rows = [[f"`{k}`", cell(v)] for k, v in value.items()]
    elif isinstance(value, dict) and all(isinstance(v, dict) for v in value.values()):
        columns: list[str] = []
        for v in value.values():
            for k in v:
                if k not in columns:
                    columns.append(k)
        head = [heading(path)] + columns
        rows = [[f"`{k}`"] + [(text_cell(v[c]) if isinstance(v.get(c), str) else cell(v[c])) if c in v else "" for c in columns]
                for k, v in value.items()]
    else:
        raise TypeError(f"registry.json '{path}' is not a list or a table")
    lines = ["| " + " | ".join(head) + " |", "|" + "|".join("---" for _ in head) + "|"]
    lines += ["| " + " | ".join(r) + " |" for r in rows]
    return "\n".join(lines) + "\n"


def render(text: str, registry: dict) -> str:
    return BLOCK.sub(lambda m: m.group(1) + table(m.group(2), lookup(registry, m.group(2))) + m.group(4), text)


def main(argv: list[str]) -> int:
    check = "--check" in argv[1:]
    registry = json.loads(REGISTRY.read_text(encoding="utf-8"))
    differs = []
    for path in sorted(HERE.rglob("*.md")):
        text = path.read_text(encoding="utf-8")
        rendered = render(text, registry)
        if rendered != text:
            differs.append(path.relative_to(HERE).as_posix())
            if not check:
                path.write_bytes(rendered.encode("utf-8"))
    if check:
        for name in differs:
            print(f"{name}: a registry table differs from registry.json; run render_tables.py")
        return 1 if differs else 0
    for name in differs:
        print(f"rendered {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
