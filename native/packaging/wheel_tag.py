"""The wheel platform tag a native library's build manifest supports (NATIVE-PLAT-WHEELS).

Usage: python wheel_tag.py <MANIFEST.json>

linux-x64 and linux-arm64 get manylinux_<glibc>_x86_64 and manylinux_<glibc>_aarch64 from the newest GLIBC_ symbol the
library asks for; osx-arm64 gets macosx_<major>_0_arm64 from the oldest macOS it runs on (pip offers only minor 0 from
macOS 11 on, so a library that needs 14.2 is tagged 15_0, never a release it does not run on); win-x64 gets win_amd64.
Standard library only.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path


def tag(manifest: dict) -> str:
    rid = manifest["rid"]
    if rid in ("linux-x64", "linux-arm64"):
        glibc = manifest.get("glibcNewest")
        if not glibc:
            raise ValueError(f"{rid}: the manifest records no glibc baseline")
        major, minor = glibc.split(".")[:2]
        return f"manylinux_{major}_{minor}_{'x86_64' if rid == 'linux-x64' else 'aarch64'}"
    if rid == "osx-arm64":
        minimum = manifest.get("macosMinimum")
        if not minimum:
            raise ValueError(f"{rid}: the manifest records no minimum macOS")
        major, minor = (int(n) for n in minimum.split(".")[:2])
        if major >= 11 and minor:
            major += 1
        return f"macosx_{major}_0_arm64" if major >= 11 else f"macosx_{major}_{minor}_arm64"
    if rid == "win-x64":
        return "win_amd64"
    raise ValueError(f"no wheel tag for {rid}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__.strip().splitlines()[2])
    print(tag(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))))
