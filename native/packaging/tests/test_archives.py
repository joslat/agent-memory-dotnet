"""The C/C++ archives of a native release (NATIVE-RELEASE): one per platform, from the platforms' gated builds."""
from __future__ import annotations

import hashlib
import json
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from native_archives import PLATFORMS, ArchiveProblem, build  # noqa: E402


class Archives(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="archives-"))
        self.platforms = self.root / "platforms"
        self.header = self.root / "agentmemory_native.h"
        self.header.write_bytes(b"/* the C ABI */\n")
        self.license = self.root / "LICENSE"
        self.license.write_bytes(b"Apache License 2.0\n")
        for rid, (library, _) in PLATFORMS.items():
            self.platform(rid, library, f"{rid} library".encode())

    def platform(self, rid: str, library: str, data: bytes, recorded: bytes | None = None):
        bin_dir = self.platforms / f"agentmemory-native-{rid}" / "bin"
        bin_dir.mkdir(parents=True, exist_ok=True)
        (bin_dir / library).write_bytes(data)
        manifest = {"rid": rid, "library": {"name": library, "bytes": len(data), "sha256": hashlib.sha256(recorded or data).hexdigest()}}
        (bin_dir / "MANIFEST.json").write_text(json.dumps(manifest), encoding="utf-8")

    def build(self, out: str = "out"):
        return build(self.platforms, self.header, self.license, "0.3.0", self.root / out)

    def test_one_archive_per_platform_with_library_header_manifest_and_licence(self):
        archives = self.build()

        names = sorted(a.name for a in archives)
        self.assertEqual(names, [
            "agentmemory-native-0.3.0-linux-arm64.tar.gz", "agentmemory-native-0.3.0-linux-x64.tar.gz",
            "agentmemory-native-0.3.0-osx-arm64.tar.gz", "agentmemory-native-0.3.0-win-x64.zip"])
        with tarfile.open(self.root / "out" / "agentmemory-native-0.3.0-linux-x64.tar.gz") as t:
            members = {m.name: m for m in t.getmembers()}
            self.assertEqual(sorted(members), [
                "agentmemory-native-0.3.0-linux-x64/LICENSE", "agentmemory-native-0.3.0-linux-x64/MANIFEST.json",
                "agentmemory-native-0.3.0-linux-x64/include/agentmemory_native.h", "agentmemory-native-0.3.0-linux-x64/lib/agentmemory_native.so"])
            library = members["agentmemory-native-0.3.0-linux-x64/lib/agentmemory_native.so"]
            self.assertEqual(t.extractfile(library).read(), b"linux-x64 library")
            self.assertEqual(library.mode, 0o755)
        with zipfile.ZipFile(self.root / "out" / "agentmemory-native-0.3.0-win-x64.zip") as z:
            self.assertIn("agentmemory-native-0.3.0-win-x64/lib/agentmemory_native.dll", z.namelist())
            self.assertEqual(z.read("agentmemory-native-0.3.0-win-x64/include/agentmemory_native.h"), b"/* the C ABI */\n")

        sums = (self.root / "out" / "SHA256SUMS").read_text(encoding="utf-8").splitlines()
        self.assertEqual(len(sums), 4)
        for line in sums:
            digest, name = line.split("  ")
            self.assertEqual(digest, hashlib.sha256((self.root / "out" / name).read_bytes()).hexdigest())

    def test_a_missing_library_is_refused(self):
        (self.platforms / "agentmemory-native-osx-arm64" / "bin" / "agentmemory_native.dylib").unlink()
        with self.assertRaisesRegex(ArchiveProblem, "osx-arm64"):
            self.build()

    def test_a_library_that_is_not_the_one_its_manifest_names_is_refused(self):
        self.platform("linux-arm64", "agentmemory_native.so", b"what was built", recorded=b"something else")
        with self.assertRaisesRegex(ArchiveProblem, "linux-arm64.*SHA-256"):
            self.build()

    def test_building_twice_gives_the_same_bytes(self):
        first = {a.name: hashlib.sha256(a.read_bytes()).hexdigest() for a in self.build("one")}
        second = {a.name: hashlib.sha256(a.read_bytes()).hexdigest() for a in self.build("two")}
        self.assertEqual(first, second)


if __name__ == "__main__":
    unittest.main()
