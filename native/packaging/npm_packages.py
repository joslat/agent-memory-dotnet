"""NATIVE-TS: the npm packages of agentmemory-native, from the wheel of the same build. Standard library only.

A platform package (agentmemory-native-<os>-<cpu>[-gnu]) carries the library, its MANIFEST.json and the licence and
notice files, taken byte for byte from the wheel, so PyPI and npm ship the same library with the same manifest. The
main package (agentmemory-native) is the TypeScript binding's build with exact optional dependencies on every platform
package. Publishable since the owner's go of 22 Nov 2026 (NATIVE-TS-PUBLISH: four platforms, with crash capture on
Linux); the first upload is the owner's, from main's four-platform build.

    python npm_packages.py platform --wheel out/wheel/agentmemory_native-0.2.0-py3-none-win_amd64.whl --rid win-x64 --out out/npm
    python npm_packages.py main --out out/npm        # after npm run build in native/typescript
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TYPESCRIPT = REPO / "native" / "typescript"

# RID -> (npm os, npm cpu, libc or None, the platform package's suffix)
PLATFORMS = {
    "linux-x64": ("linux", "x64", "glibc", "linux-x64-gnu"),
    "linux-arm64": ("linux", "arm64", "glibc", "linux-arm64-gnu"),
    "osx-arm64": ("darwin", "arm64", None, "darwin-arm64"),
    "win-x64": ("win32", "x64", None, "win32-x64"),
}


def version() -> str:
    return json.loads((TYPESCRIPT / "package.json").read_text(encoding="utf-8"))["version"]


def platform_package(wheel: Path, rid: str, out: Path) -> Path:
    os_name, cpu, libc, suffix = PLATFORMS[rid]
    name = f"agentmemory-native-{suffix}"
    folder = out / name
    if folder.exists():
        shutil.rmtree(folder)
    (folder / "_native").mkdir(parents=True)
    with zipfile.ZipFile(wheel) as archive:
        names = archive.namelist()
        native = [n for n in names if n.startswith("agentmemory_native/_native/") and not n.endswith("/")]
        licences = [n for n in names if "/licenses/" in n and not n.endswith("/")]
        if not any(n.endswith("MANIFEST.json") for n in native):
            sys.exit(f"{wheel} has no MANIFEST.json: build it with --provenance")
        for member in native:
            (folder / "_native" / Path(member).name).write_bytes(archive.read(member))
        for member in licences:
            (folder / Path(member).name).write_bytes(archive.read(member))
    manifest = json.loads((folder / "_native" / "MANIFEST.json").read_text(encoding="utf-8"))
    ver = version()
    if manifest["version"] != ver:
        sys.exit(f"the wheel is {manifest['version']}, the TypeScript package {ver}: one version line for every registry")
    package = {
        "name": name,
        "version": ver,
        "description": f"The AgentMemory native library for {os_name} {cpu}, used by agentmemory-native. Install agentmemory-native instead.",
        "license": "MIT AND Apache-2.0",
        "os": [os_name],
        "cpu": [cpu],
        **({"libc": [libc]} if libc else {}),
        "files": ["_native", "LICENSE", "THIRD-PARTY-NOTICES.txt", "README.md"],
        "repository": {"type": "git", "url": "https://github.com/joslat/agent-memory-dotnet.git", "directory": "native/typescript"},
    }
    (folder / "package.json").write_text(json.dumps(package, indent=2) + "\n", encoding="utf-8")
    (folder / "README.md").write_text(
        f"# {name}\n\nThe AgentMemory native library ({manifest['library']['file']}, sha256 {manifest['library']['sha256']}) for "
        f"{os_name} {cpu}{' with ' + libc if libc else ''}, built from commit {manifest['build']['commit']}. It is an optional "
        "dependency of `agentmemory-native`, which loads it after checking it against `_native/MANIFEST.json`. "
        "Third-party notices: `THIRD-PARTY-NOTICES.txt`.\n", encoding="utf-8")
    print(f"{folder}: {manifest['library']['file']} {manifest['library']['size']} bytes")
    return folder


def main_package(out: Path) -> Path:
    built = TYPESCRIPT / "dist" / "src"
    if not (built / "index.js").is_file():
        sys.exit("native/typescript is not built: npm ci && npm run build first")
    folder = out / "agentmemory-native"
    if folder.exists():
        shutil.rmtree(folder)
    shutil.copytree(built, folder / "dist" / "src")
    shutil.copy(TYPESCRIPT / "README.md", folder / "README.md")
    shutil.copy(REPO / "LICENSE", folder / "LICENSE")
    package = json.loads((TYPESCRIPT / "package.json").read_text(encoding="utf-8"))
    ver = package["version"]
    for key in ("scripts", "devDependencies"):
        package.pop(key, None)
    package["files"] = ["dist/src", "README.md", "LICENSE"]
    package["optionalDependencies"] = {f"agentmemory-native-{p[3]}": ver for p in PLATFORMS.values()}
    package["repository"] = {"type": "git", "url": "https://github.com/joslat/agent-memory-dotnet.git", "directory": "native/typescript"}
    (folder / "package.json").write_text(json.dumps(package, indent=2) + "\n", encoding="utf-8")
    print(f"{folder}: optional dependencies {sorted(package['optionalDependencies'])}")
    return folder


def main() -> None:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="kind", required=True)
    one = sub.add_parser("platform")
    one.add_argument("--wheel", type=Path, required=True)
    one.add_argument("--rid", choices=sorted(PLATFORMS), required=True)
    one.add_argument("--out", type=Path, required=True)
    main_ = sub.add_parser("main")
    main_.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    if args.kind == "platform":
        platform_package(args.wheel, args.rid, args.out)
    else:
        main_package(args.out)


if __name__ == "__main__":
    main()
