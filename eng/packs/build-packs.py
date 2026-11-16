"""Build native v1.2 authoring folders from the private pack catalogue. Never builds a NuGet knowledge package.

python eng/packs/build-packs.py --key <publisher.key.pem> --policy <trust.json> --only martial-arts lantern-hill-dojo
The catalogue defaults to strategy/ampack/packs. Sources live in <pack>/source, outputs in <pack>/dist.
The private key is supplied externally and is never copied into the catalogue. Existing archives are retained under archives/.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

REPO = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalogue", type=Path, default=REPO / "strategy/ampack/packs")
    parser.add_argument("--key", required=True, type=Path)
    parser.add_argument("--policy", required=True, type=Path)
    parser.add_argument("--only", nargs="+", required=True, help="Pack folder names, not package IDs.")
    args = parser.parse_args()
    catalogue = args.catalogue.resolve()
    key, policy = args.key.resolve(), args.policy.resolve()
    if not key.is_file() or not policy.is_file():
        parser.error("Provide an existing external signing key and matching trust policy.")
    executable = REPO / "tools/Ampack/bin/Release/net10.0/Ampack.dll"
    if not executable.exists():
        parser.error("Build tools/Ampack in Release first.")
    def run(*parts):
        process = subprocess.run(["dotnet", str(executable), *map(str, parts)], cwd=REPO, text=True, capture_output=True, encoding="utf-8")
        if process.returncode:
            raise SystemExit(process.stdout + process.stderr)
        return process.stdout.strip()
    results = []
    for name in args.only:
        folder = (catalogue / name).resolve()
        if not folder.is_relative_to(catalogue) or folder == catalogue:
            parser.error("Pack names must stay inside the catalogue.")
        source = folder / "source"
        manifest = json.loads((source / "pack.json").read_text(encoding="utf-8-sig"))
        if manifest.get("format") != "agentmemory.pack/1.2":
            parser.error(f"{name}: expected native agentmemory.pack/1.2 authoring files.")
        output = folder / "dist"
        output.mkdir(exist_ok=True)
        archive = output / (manifest["id"] + "-" + manifest["version"] + ".ampack")
        run("lint", source)
        built = run("build", source, "--key", key, "--out", archive)
        verified = run("verify", archive, "--policy", policy)
        run("lint", archive, "--policy", policy)
        shutil.copyfile(policy, output / "trust.json")
        results.append({"folder": name, "id": manifest["id"], "version": manifest["version"], "file": archive.relative_to(catalogue).as_posix(),
                        "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "build": built, "verification": verified})
        print(verified)
    report = {"format": "ampack.catalogue.build/1", "packs": results, "keyLocation": "external; private key not copied"}
    (catalogue / "last-build.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(results)} native .ampack archives built and verified.")


if __name__ == "__main__":
    main()
