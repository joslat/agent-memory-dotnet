"""The native packaging helpers, without a built library: the macOS minimum read from a Mach-O header, and wheel tags."""
from __future__ import annotations

import struct
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from native_manifest import LC_BUILD_VERSION, LC_VERSION_MIN_MACOSX, macos_minimum  # noqa: E402
from wheel_tag import tag  # noqa: E402

SEGMENT_64 = 0x19


def mach_o(*commands: tuple[int, bytes]) -> bytes:
    """A thin little-endian 64-bit Mach-O header (arm64, a dynamic library) followed by the given load commands."""
    body = b"".join(struct.pack("<II", command, 8 + len(payload)) + payload for command, payload in commands)
    header = struct.pack("<IiiIIIII", 0xFEEDFACF, 0x0100000C, 0, 6, len(commands), len(body), 0, 0)
    return header + body


def version(major: int, minor: int) -> int:
    return major << 16 | minor << 8


class MacOsMinimum(unittest.TestCase):
    def test_build_version_after_other_commands(self):
        data = mach_o((SEGMENT_64, bytes(64)), (LC_BUILD_VERSION, struct.pack("<IIII", 1, version(12, 0), version(15, 2), 0)))
        self.assertEqual(macos_minimum(data), "12.0")

    def test_the_older_version_min_command(self):
        data = mach_o((LC_VERSION_MIN_MACOSX, struct.pack("<II", version(10, 15), version(11, 0))))
        self.assertEqual(macos_minimum(data), "10.15")

    def test_not_a_thin_64_bit_mach_o(self):
        self.assertIsNone(macos_minimum(b"\x7fELF" + bytes(60)))
        self.assertIsNone(macos_minimum(b"\xca\xfe\xba\xbe" + bytes(60)))  # a universal (fat) file

    def test_no_version_command(self):
        self.assertIsNone(macos_minimum(mach_o((SEGMENT_64, bytes(64)))))


class WheelTag(unittest.TestCase):
    def test_each_platform(self):
        self.assertEqual(tag({"rid": "linux-x64", "glibcNewest": "2.34"}), "manylinux_2_34_x86_64")
        self.assertEqual(tag({"rid": "linux-arm64", "glibcNewest": "2.34"}), "manylinux_2_34_aarch64")
        self.assertEqual(tag({"rid": "osx-arm64", "macosMinimum": "12.0"}), "macosx_12_0_arm64")
        self.assertEqual(tag({"rid": "win-x64"}), "win_amd64")

    def test_a_macos_minor_release_is_never_promised_below_it(self):
        self.assertEqual(tag({"rid": "osx-arm64", "macosMinimum": "14.2"}), "macosx_15_0_arm64")
        self.assertEqual(tag({"rid": "osx-arm64", "macosMinimum": "10.15"}), "macosx_10_15_arm64")

    def test_what_the_manifest_does_not_record_is_refused(self):
        with self.assertRaises(ValueError):
            tag({"rid": "linux-x64"})
        with self.assertRaises(ValueError):
            tag({"rid": "osx-arm64"})
        with self.assertRaises(ValueError):
            tag({"rid": "linux-musl-x64", "glibcNewest": "2.34"})


if __name__ == "__main__":
    unittest.main()
