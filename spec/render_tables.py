"""Render the spec's name tables from registry.json.

Every table of names in the spec sits between a marker line naming a JSON path into registry.json (dot-separated) and a
closing marker:

    <!-- registry:manifest.fields -->
    ...generated table...
    <!-- /registry -->

Run without arguments to rewrite every table under this folder (and every requirement summary in requirements.json, which is
the requirement's own text); run with --check to exit 1 if any table or summary differs from
what the registry gives (nothing is written then). Python 3.12, standard library only.
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
REGISTRY = HERE / "registry.json"
# A requirement's text: from its bold id to the end of its paragraph (a blank line, or the next requirement).
PARAGRAPH = re.compile(r"\*\*(R-[A-Z]+-\d{3})\.\*\*[ \t]*(.*?)(?=\n[ \t]*\n|\n- \*\*R-|\n\*\*R-|\Z)", re.S)
BLOCK = re.compile(r"(<!-- registry:([A-Za-z0-9_.]+) -->\n)(.*?)(<!-- /registry -->)", re.S)

# Column headings only; the names themselves always come from the registry.
HEADINGS = {
    "roles.signing": "Signing role",
    "roles.keys": "Key role",
    "manifest.values": "Field",
    "profiles": "Profile",
    "units.fields.confidencePermille.values": "Confidence (thousandths)",
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
    # Each requirement's summary is its own text (review F02, M6), refreshed with the tables.
    requirements_path = HERE / "requirements.json"
    requirements = json.loads(requirements_path.read_text(encoding="utf-8"))
    texts = {}
    defined = {}
    for path in sorted(HERE.rglob("*.md")):
        for m in PARAGRAPH.finditer(path.read_text(encoding="utf-8").replace("\r\n", "\n")):
            texts[m.group(1)] = " ".join(m.group(2).split())
            defined.setdefault(m.group(1), []).append(path.name)
    # An id is defined once, in the spec and in requirements.json (feature 12: new requirements reused five ids, unnoticed).
    ids = [r["id"] for r in requirements["requirements"]]
    twice = sorted({i for i in ids if ids.count(i) > 1} | {i for i, where in defined.items() if len(where) > 1})
    for rid in twice:
        print(f"{rid}: defined more than once")
    if twice:
        return 1
    stale = [r["id"] for r in requirements["requirements"] if texts.get(r["id"]) != r["summary"]]
    if check:
        for name in differs:
            print(f"{name}: a registry table differs from registry.json; run render_tables.py")
        for rid in stale:
            print(f"{rid}: its summary in requirements.json is not its text; run render_tables.py")
        return 1 if differs or stale else 0
    if stale:
        for r in requirements["requirements"]:
            r["summary"] = texts.get(r["id"], r["summary"])
        requirements_path.write_text(json.dumps(requirements, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"refreshed {len(stale)} summaries in requirements.json")
    for name in differs:
        print(f"rendered {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
