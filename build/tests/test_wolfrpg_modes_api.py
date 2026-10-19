"""Offline regression for the UberWolfLib default-crypt-modes accessor patch.

The pinned WolfDec.{h,cpp} snapshots are repository-owned fixtures. The test
verifies their recorded provenance, confirms Git keeps them byte-for-byte, and
then applies the real port patch with ``git apply`` into a throwaway member tree
to prove the accessor is added while the DEFAULT_CRYPT_MODES table stays
untouched. No network, no external user paths, and no skipped cases.
"""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "build/tests/fixtures/uberwolf-modes"
MANIFEST = FIXTURES / "manifest.json"
PATCH = ROOT / "build/vcpkg/ports/uberwolf/default-crypt-modes.patch"

ACCESSOR_DECL = b"static const CryptModes& DefaultCryptModes() noexcept;"
# The patch inserts exactly this block between the table and the constructor.
CPP_INSERTION = (
    b"const CryptModes& WolfDec::DefaultCryptModes() noexcept\n"
    b"{\n"
    b"\treturn DEFAULT_CRYPT_MODES;\n"
    b"}\n"
    b"\n"
)
CPP_ANCHOR = b"};\n\nWolfDec::WolfDec"


class UberWolfModesPatchContract(unittest.TestCase):
    def _manifest(self) -> dict:
        return json.loads(MANIFEST.read_text(encoding="utf-8"))

    def test_fixtures_match_recorded_provenance(self) -> None:
        document = self._manifest()
        self.assertTrue(document["ref"])
        self.assertTrue(document["repository"])
        self.assertEqual(len(document["files"]), 2)
        for row in document["files"]:
            with self.subTest(fixture=row["fixture"]):
                data = (FIXTURES / row["fixture"]).read_bytes()
                self.assertEqual(len(data), row["bytes"])
                self.assertEqual(hashlib.sha256(data).hexdigest(), row["sha256"])

    def test_git_disables_text_conversion_for_every_fixture(self) -> None:
        paths = sorted(
            path.relative_to(ROOT).as_posix() for path in FIXTURES.glob("*.fixture")
        )
        self.assertTrue(paths)
        result = subprocess.run(
            ["git", "-c", f"safe.directory={ROOT}", "-C", str(ROOT),
             "check-attr", "-z", "--stdin", "text"],
            input=("\0".join(paths) + "\0").encode(),
            capture_output=True,
            check=True,
        )
        fields = result.stdout.decode().rstrip("\0").split("\0")
        self.assertEqual(len(fields), len(paths) * 3)
        for index in range(0, len(fields), 3):
            with self.subTest(path=fields[index]):
                self.assertEqual(fields[index + 2], "unset")

    def test_patch_members_and_added_lines(self) -> None:
        text = PATCH.read_text(encoding="utf-8")
        members = set()
        removed = []
        added = []
        for line in text.splitlines():
            if line.startswith("diff --git "):
                members.add(line.split(" b/", 1)[1])
            elif line.startswith("---") or line.startswith("+++"):
                continue
            elif line.startswith("-"):
                removed.append(line)
            elif line.startswith("+"):
                added.append(line[1:])
        self.assertEqual(members, {"UberWolfLib/WolfDec.h", "UberWolfLib/WolfDec.cpp"})
        # Only additions: the pinned default table and legacy behavior are preserved.
        self.assertEqual(removed, [])
        stripped = [line.strip() for line in added]
        self.assertIn(ACCESSOR_DECL.decode(), stripped)
        self.assertIn("const CryptModes& WolfDec::DefaultCryptModes() noexcept", stripped)
        self.assertIn("return DEFAULT_CRYPT_MODES;", stripped)

    def test_patch_applies_and_preserves_default_table(self) -> None:
        document = self._manifest()
        original = {
            row["member"]: (FIXTURES / row["fixture"]).read_bytes()
            for row in document["files"]
        }
        self.assertNotIn(b"DefaultCryptModes", original["UberWolfLib/WolfDec.h"])
        self.assertNotIn(b"DefaultCryptModes", original["UberWolfLib/WolfDec.cpp"])

        work = Path(tempfile.mkdtemp(prefix="uberwolf-modes-"))
        try:
            for member, data in original.items():
                target = work / member
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)

            subprocess.run(
                ["git", "apply", "--verbose", str(PATCH)],
                cwd=work,
                capture_output=True,
                check=True,
            )

            patched_h = (work / "UberWolfLib/WolfDec.h").read_bytes()
            patched_cpp = (work / "UberWolfLib/WolfDec.cpp").read_bytes()

            self.assertIn(ACCESSOR_DECL, patched_h)

            # The .cpp change must be a single insertion of the accessor body;
            # every other byte (the whole DEFAULT_CRYPT_MODES table) is unchanged.
            fixture_cpp = original["UberWolfLib/WolfDec.cpp"]
            self.assertIn(CPP_ANCHOR, fixture_cpp)
            self.assertEqual(
                patched_cpp,
                fixture_cpp.replace(
                    CPP_ANCHOR, b"};\n\n" + CPP_INSERTION + b"WolfDec::WolfDec", 1
                ),
            )
            self.assertEqual(
                patched_h,
                original["UberWolfLib/WolfDec.h"].replace(
                    b"\tstatic Strings GetEncryptions();\n",
                    b"\tstatic Strings GetEncryptions();\n\n\t" + ACCESSOR_DECL + b"\n",
                    1,
                ),
            )
        finally:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
