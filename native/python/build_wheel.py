"""Builds the agentmemory-native wheel: the Python package with one published native library inside.

Usage: python build_wheel.py <native-library> <output-dir> [--platform-tag manylinux_2_34_x86_64]
                             [--provenance <dir> --commit <sha>]

With --provenance (written by native/gate/publish-linux.sh next to the library) the wheel also carries
agentmemory_native/_native/MANIFEST.json (package and ABI versions, platform, the library's size and SHA-256, the commit,
SDK and native runtime versions, and every linked package with its licence) and, in its dist-info, licenses/LICENSE and
licenses/THIRD-PARTY-NOTICES.txt. The binding refuses a packaged library whose bytes differ from the manifest.

Standard library only, and reproducible: entries are sorted and every timestamp is fixed, so the same inputs give the
same wheel bytes. The platform tag must match the library: on Linux, the oldest glibc whose symbols it uses (check with
objdump -T); the published linux-x64 library needs GLIBC_2.34 at most.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ElementTree
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
PACKAGE = HERE / "agentmemory_native"
NAME = "agentmemory_native"
DIST = "agentmemory-native"
FIXED_TIME = (1980, 1, 1, 0, 0, 0)
LIBRARY_NAMES = {"linux": "agentmemory_native.so", "win": "agentmemory_native.dll", "macosx": "agentmemory_native.dylib"}
# Restored only to build: the compiler and the trimmer. The native runtime they link is the .NET runtime, whose licence
# and notices are carried from the SDK; its version is recorded as build.nativeRuntime.
BUILD_ONLY = ("Microsoft.DotNet.ILCompiler", "runtime.", "Microsoft.NET.ILLink.Tasks")
APACHE_2_TEXT = REPO / "ampack" / "LICENSE"
APACHE_2_SHA256 = "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30"


def version() -> str:
    match = re.search(r'^__version__ = "([^"]+)"', (PACKAGE / "__init__.py").read_text(encoding="utf-8"), re.M)
    if not match:
        sys.exit("no __version__ in agentmemory_native/__init__.py")
    return match.group(1)


def abi_version() -> dict[str, int]:
    text = (PACKAGE / "_library.py").read_text(encoding="utf-8")
    major = re.search(r"^ABI_MAJOR = (\d+)$", text, re.M)
    minor = re.search(r"^ABI_MINOR = (\d+)$", text, re.M)
    if not major or not minor:
        sys.exit("no ABI_MAJOR/ABI_MINOR in agentmemory_native/_library.py")
    return {"major": int(major.group(1)), "minor": int(minor.group(1))}


def record_line(path: str, data: bytes) -> str:
    digest = base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b"=").decode()
    return f"{path},sha256={digest},{len(data)}"


MIT_TEXT = """MIT License

Copyright (c) {holder}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"""


def package_description(nuspec: Path) -> dict[str, str]:
    """The licence expression (or URL), project URL, copyright and authors a package declares."""
    root = ElementTree.parse(nuspec).getroot()
    namespace = root.tag.split("}")[0] + "}" if root.tag.startswith("{") else ""
    metadata = root.find(f"{namespace}metadata")

    def text(name: str) -> str:
        element = metadata.find(f"{namespace}{name}") if metadata is not None else None
        return (element.text or "").strip() if element is not None else ""

    licence = metadata.find(f"{namespace}license") if metadata is not None else None
    if licence is not None and licence.get("type") == "expression" and licence.text:
        declared = licence.text.strip()
    elif text("licenseUrl"):
        declared = f"see {text('licenseUrl')}"
    else:
        sys.exit(f"{nuspec.name}: no licence declared; decide by hand before shipping it")
    return {"license": declared, "projectUrl": text("projectUrl"), "copyright": text("copyright"),
            "authors": text("authors")}


def provenance(folder: Path, commit: str) -> tuple[dict, str]:
    """The manifest's build and dependency sections and the third-party notices, from publish-linux.sh's output."""
    assets = json.loads((folder / "project.assets.json").read_text(encoding="utf-8"))
    dependencies, native_runtime = [], None
    for key, library in sorted(assets["libraries"].items()):
        if library.get("type") != "package":
            continue
        package_id, package_version = key.split("/", 1)
        if package_id.startswith("Microsoft.DotNet.ILCompiler") or package_id.startswith("runtime."):
            native_runtime = native_runtime or package_version
        if package_id.startswith(BUILD_ONLY):
            continue
        key_lower = f"{package_id.lower()}.{package_version.lower()}"
        nuspec = folder / "nuspecs" / f"{key_lower}.nuspec"
        if not nuspec.is_file():
            sys.exit(f"no package description for {key} in {folder / 'nuspecs'}")
        described = package_description(nuspec)
        own = folder / "packages" / key_lower
        dependencies.append({"id": package_id, "version": package_version, "license": described["license"],
                             "projectUrl": described["projectUrl"],
                             "_files": sorted(own.iterdir()) if own.is_dir() else [],
                             "_holder": described["copyright"] or described["authors"]})
    licences = {d["license"] for d in dependencies}
    unknown = licences - {"MIT", "Apache-2.0"}
    if unknown:
        sys.exit(f"licences without notice text here: {sorted(unknown)}; add their texts before shipping")
    apache = APACHE_2_TEXT.read_bytes().replace(b"\r\n", b"\n")
    if hashlib.sha256(apache).hexdigest() != APACHE_2_SHA256:
        sys.exit(f"{APACHE_2_TEXT} is not the standard Apache-2.0 text")
    sdk = (folder / "sdk-version.txt").read_text(encoding="utf-8").strip()
    build = {"commit": commit, "sdk": sdk, "nativeRuntime": native_runtime}

    # Each package's own licence and notice files; where a package ships none, the standard text of the licence it
    # declares (MIT with its declared copyright holder, or Apache-2.0). Identical texts are printed once.
    texts: dict[str, dict] = {}
    for dependency in dependencies:
        label = f"{dependency['id']} {dependency['version']}"
        own_texts = [(f.name, f.read_text(encoding="utf-8", errors="replace")) for f in dependency.pop("_files")]
        holder = dependency.pop("_holder")
        if not any(name.lower().startswith(("license", "licence")) for name, _ in own_texts):
            if dependency["license"] == "MIT":
                if not holder:
                    sys.exit(f"{label}: MIT without a copyright holder or authors; add its notice by hand")
                holder = re.sub(r"^\s*(copyright\s*)?(\(c\)|©)?\s*", "", holder, flags=re.I)
                own_texts.append(("MIT licence (standard text; the package ships no licence file)",
                                  MIT_TEXT.format(holder=holder)))
            else:
                own_texts.append(("Apache License 2.0 (the package ships no licence file)", apache.decode("utf-8")))
        for name, body in own_texts:
            body = body.replace("\r\n", "\n").rstrip()
            entry = texts.setdefault(hashlib.sha256(body.encode()).hexdigest(), {"body": body, "name": name, "packages": []})
            entry["packages"].append(label)
    runtime_texts = [
        ("LICENSE.txt", (folder / "dotnet" / "LICENSE.txt").read_text(encoding="utf-8")),
        ("ThirdPartyNotices.txt", (folder / "dotnet" / "ThirdPartyNotices.txt").read_text(encoding="utf-8")),
    ]
    for name, body in runtime_texts:
        body = body.replace("\r\n", "\n").rstrip()
        entry = texts.setdefault(hashlib.sha256(body.encode()).hexdigest(), {"body": body, "name": name, "packages": []})
        entry["packages"].append(f".NET runtime (native AOT) {native_runtime}")

    rule = "=" * 100
    lines = [
        "AgentMemory native library: third-party notices",
        "",
        "The library inside this package (agentmemory_native/_native) is a Native AOT build. It contains the .NET",
        "runtime and the following packages, compiled in:",
        "",
    ]
    lines += [f"  {d['id']} {d['version']}  ({d['license']})  {d['projectUrl']}".rstrip() for d in dependencies]
    lines += ["", f"  .NET runtime (native AOT) {native_runtime}  (MIT)  https://github.com/dotnet/runtime", ""]
    for entry in texts.values():
        lines += [rule, f"{entry['name']}, for: {', '.join(entry['packages'])}", rule, "", entry["body"], ""]
    return {"build": build, "dependencies": dependencies}, "\n".join(lines) + "\n"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("library", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--platform-tag", default="manylinux_2_34_x86_64")
    parser.add_argument("--provenance", type=Path, help="publish-linux.sh's provenance folder")
    parser.add_argument("--commit", help="the commit the library was built from (40 hex digits)")
    args = parser.parse_args()

    tag = args.platform_tag
    family = next((key for key in LIBRARY_NAMES if tag.startswith(key) or tag.startswith(f"many{key}")), None)
    if family is None:
        sys.exit(f"unknown platform tag {tag}")
    if not args.library.is_file():
        sys.exit(f"native library not found: {args.library}")
    if args.provenance and not re.fullmatch(r"[0-9a-f]{40}", args.commit or ""):
        sys.exit("--provenance needs --commit with the full 40-digit commit")

    ver = version()
    dist_info = f"{NAME}-{ver}.dist-info"
    library = args.library.read_bytes()
    library_name = LIBRARY_NAMES[family]
    files: dict[str, bytes] = {}
    for source in sorted(PACKAGE.rglob("*.py")):
        files[f"{NAME}/{source.relative_to(PACKAGE).as_posix()}"] = source.read_bytes()
    files[f"{NAME}/_native/{library_name}"] = library
    licence_field = "MIT"
    if args.provenance:
        sections, notices = provenance(args.provenance, args.commit)
        manifest = {
            "schemaVersion": 1, "package": DIST, "version": ver, "abi": abi_version(), "platformTag": tag,
            "library": {"file": library_name, "size": len(library), "sha256": hashlib.sha256(library).hexdigest()},
            **sections,
        }
        files[f"{NAME}/_native/MANIFEST.json"] = (json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode()
        files[f"{dist_info}/licenses/LICENSE"] = (REPO / "LICENSE").read_bytes()
        files[f"{dist_info}/licenses/THIRD-PARTY-NOTICES.txt"] = notices.encode()
        if any(d["license"] == "Apache-2.0" for d in sections["dependencies"]):
            licence_field = "MIT AND Apache-2.0 (see licenses/THIRD-PARTY-NOTICES.txt)"
    files[f"{dist_info}/METADATA"] = (
        "Metadata-Version: 2.1\n"
        f"Name: {DIST}\n"
        f"Version: {ver}\n"
        "Summary: AgentMemory for Python through the AgentMemory native library (no .NET installation needed)\n"
        f"License: {licence_field}\n"
        "Requires-Python: >=3.10\n"
        "Project-URL: Homepage, https://github.com/joslat/agent-memory-dotnet\n"
        "Project-URL: Documentation, https://github.com/joslat/agent-memory-dotnet/blob/main/native/CONTRACT.md\n"
        "Project-URL: Source, https://github.com/joslat/agent-memory-dotnet/tree/main/native/python\n"
        "Provides-Extra: langchain\n"
        "Requires-Dist: langchain-core>=0.3; extra == \"langchain\"\n"
        # The page PyPI shows: this package's README, next to this script.
        "Description-Content-Type: text/markdown\n"
        "\n"
        + (Path(__file__).parent / "README.md").read_text(encoding="utf-8")
    ).encode()
    files[f"{dist_info}/WHEEL"] = (
        "Wheel-Version: 1.0\nGenerator: agentmemory build_wheel.py\nRoot-Is-Purelib: false\n" f"Tag: py3-none-{tag}\n"
    ).encode()
    files[f"{dist_info}/top_level.txt"] = f"{NAME}\n".encode()

    record = [record_line(path, data) for path, data in sorted(files.items())]
    record.append(f"{dist_info}/RECORD,,")
    files[f"{dist_info}/RECORD"] = ("\n".join(record) + "\n").encode()

    args.output.mkdir(parents=True, exist_ok=True)
    wheel = args.output / f"{NAME}-{ver}-py3-none-{tag}.whl"
    with zipfile.ZipFile(wheel, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(files):
            info = zipfile.ZipInfo(path, date_time=FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o755 if path.endswith(library_name) else 0o644) << 16
            archive.writestr(info, files[path])
    print(f"{wheel.name}  {wheel.stat().st_size:,} bytes  sha256 {hashlib.sha256(wheel.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
