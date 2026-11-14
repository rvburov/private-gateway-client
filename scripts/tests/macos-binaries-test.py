#!/usr/bin/env python3
"""Готовый пакет не проходит проверку с чужим CPU, новой ОС или непроверяемым Mach-O."""

import importlib.util
import struct
import sys
import tempfile
import unittest
from pathlib import Path

sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("macos_binaries", Path(__file__).parents[1] / "check-macos-binaries.py")
CHECK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECK)
X64 = CHECK.ARCHITECTURES["osx-x64"]
ARM64 = CHECK.ARCHITECTURES["osx-arm64"]


def thin(cpu, major, minor=0, legacy=False, platform=1):
    value = major << 16 | minor << 8
    command = struct.pack("<IIII", 0x24, 16, value, value) if legacy else struct.pack("<IIIIII", 0x32, 24, platform, value, value, 0)
    header = struct.pack("<IIIIIIII", 0xFEEDFACF, cpu, 3, 6, 1, len(command), 0, 0)
    return header + command


def universal():
    x64 = thin(X64, 10, 13, legacy=True)
    arm = thin(ARM64, 11)
    start = 8 + 2 * 20
    return (struct.pack(">II", 0xCAFEBABE, 2)
            + struct.pack(">IIIII", X64, 3, start, len(x64), 0)
            + struct.pack(">IIIII", ARM64, 0, start + len(x64), len(arm), 0) + x64 + arm)


class MacosBinariesTest(unittest.TestCase):
    def test_legacy_and_modern_versions_are_read(self):
        self.assertEqual((10, 13, 0), CHECK.minimum(thin(X64, 10, 13, legacy=True), X64))
        self.assertEqual((12, 0, 0), CHECK.minimum(thin(ARM64, 12), ARM64))

    def test_universal_uses_the_requested_slice(self):
        self.assertEqual((10, 13, 0), CHECK.minimum(universal(), X64))
        self.assertEqual((11, 0, 0), CHECK.minimum(universal(), ARM64))

    def test_wrong_architecture_or_apple_platform_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "процессора"):
            CHECK.minimum(thin(ARM64, 11), X64)
        with self.assertRaisesRegex(ValueError, "платформы"):
            CHECK.minimum(thin(X64, 12, platform=6), X64)

    def test_missing_or_damaged_metadata_is_rejected(self):
        without_version = struct.pack("<IIIIIIII", 0xFEEDFACF, X64, 3, 6, 0, 0, 0, 0)
        with self.assertRaisesRegex(ValueError, "не указывает"):
            CHECK.minimum(without_version, X64)
        with self.assertRaisesRegex(ValueError, "поврежденная"):
            CHECK.minimum(thin(X64, 12)[:-1], X64)

    def test_one_newer_nested_dependency_prevents_packaging(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "PrivateGateway").write_bytes(thin(X64, 12))
            core = root / "cores" / "xray"
            core.mkdir(parents=True)
            (core / "xray").write_bytes(thin(X64, 13))
            with self.assertRaisesRegex(ValueError, "cores/xray/xray.*новее"):
                CHECK.check(root, "osx-x64", (12, 0, 0))
            (core / "xray").write_bytes(thin(X64, 12))
            (root / "data.json").write_text("{}")
            CHECK.check(root, "osx-x64", (12, 0, 0))

    def test_no_binaries_is_not_a_successful_check(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaisesRegex(ValueError, "нет Mach-O"):
                CHECK.check(Path(folder), "osx-x64", (12, 0, 0))


if __name__ == "__main__":
    unittest.main()
