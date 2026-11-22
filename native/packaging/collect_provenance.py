"""Collects what build_wheel.py --provenance reads, on the machine that just published the native library (NATIVE-PLAT-WHEELS).

Usage: python collect_provenance.py --assets <project.assets.json> --out <folder>

The folder gets the same layout native/gate/publish-linux.sh writes inside its container, so one wheel builder serves
every platform: project.assets.json; nuspecs/<id>.<version>.nuspec and packages/<id>.<version>/<licence and notice
files> for every package the restore used (from the NuGet folder the assets file names); dotnet/LICENSE.txt and
dotnet/ThirdPartyNotices.txt of the .NET installation that built it; sdk-version.txt. Standard library only.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

NOTICE = re.compile(r"^(license|licence|notice|third-party-notices|thirdpartynotices)", re.IGNORECASE)


def dotnet_root() -> Path:
    if os.environ.get("DOTNET_ROOT") and (Path(os.environ["DOTNET_ROOT"]) / "LICENSE.txt").is_file():
        return Path(os.environ["DOTNET_ROOT"])
    listed = subprocess.run(["dotnet", "--list-sdks"], capture_output=True, text=True, check=True).stdout
    version = subprocess.run(["dotnet", "--version"], capture_output=True, text=True, check=True).stdout.strip()
    for line in listed.splitlines():
        match = re.fullmatch(r"(\S+) \[(.+)\]", line.strip())
        if match and match.group(1) == version:
            return Path(match.group(2)).parent
    sys.exit(f"cannot find the installation of the .NET SDK {version}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--assets", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    assets = json.loads(args.assets.read_text(encoding="utf-8"))
    folders = [Path(f) for f in assets.get("packageFolders", {})]
    if not folders:
        sys.exit(f"{args.assets} names no package folder")
    out = args.out
    if out.exists():
        shutil.rmtree(out)
    (out / "nuspecs").mkdir(parents=True)
    (out / "dotnet").mkdir()
    shutil.copyfile(args.assets, out / "project.assets.json")

    copied = 0
    for key, library in sorted(assets["libraries"].items()):
        if library.get("type") != "package":
            continue
        package_id, version = key.split("/", 1)
        name = f"{package_id.lower()}.{version.lower()}"
        home = next((f / library["path"] for f in folders if (f / library["path"]).is_dir()), None)
        if home is None:
            sys.exit(f"{key} is not in {', '.join(map(str, folders))}")
        spec = home / f"{package_id.lower()}.nuspec"
        if not spec.is_file():
            sys.exit(f"no {spec.name} in {home}")
        shutil.copyfile(spec, out / "nuspecs" / f"{name}.nuspec")
        notices = [f for f in home.iterdir() if f.is_file() and NOTICE.match(f.name)]
        if notices:
            (out / "packages" / name).mkdir(parents=True)
            for notice in notices:
                shutil.copyfile(notice, out / "packages" / name / notice.name)
        copied += 1

    root = dotnet_root()
    for item in ("LICENSE.txt", "ThirdPartyNotices.txt"):
        shutil.copyfile(root / item, out / "dotnet" / item)
    sdk = subprocess.run(["dotnet", "--version"], capture_output=True, text=True, check=True).stdout.strip()
    (out / "sdk-version.txt").write_text(sdk + "\n", encoding="utf-8")
    print(f"provenance in {out}: {copied} packages, .NET SDK {sdk} from {root}")


if __name__ == "__main__":
    main()
