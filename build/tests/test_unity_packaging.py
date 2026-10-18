"""The experimental unity module is packageable on its own, without joining the native gates.

These tests pin the packaging/metadata contract only: ``unity`` is a selectable package module,
its archives carry exactly the staged ``unity.so`` plus the registration and changelog files, its
``LICENSES.txt`` labels the combined binary as a GPL-3.0 work instead of an MIT-only module, and a
missing staged binary fails with a clear prerequisite error. No C++, native or MSBuild path runs.
"""

from __future__ import annotations

import configparser
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile


BUILD_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(BUILD_ROOT))

import main  # noqa: E402


UNITY = "unity"
UNITY_VERSION = "0.3.1"
MODULES_UNDER_TEST = ("renpy", "rpgmaker", "zanzarah", "godot")

UNITY_SO_X86 = b"\x7fELF unity shared object x86 fixture\n"
UNITY_SO_X64 = b"\x7fELF unity shared object x64 fixture\n"
UNITY_PDB_X86 = b"unity x86 symbols fixture\n"
UNITY_PDB_X64 = b"unity x64 symbols fixture\n"
UNITY_OBSERVER_INI = (
    b"[Modules]\n"
    b"Unity=modules\\unity.so\n"
    b"\n"
    b"[Filters]\n"
    b"Unity=*.assets;*.unity3d;*.bundle\n"
)
UNITY_CHANGELOG_EN = b"Version 0.1.0\n-------------\n * First packaged Unity module.\n"
UNITY_CHANGELOG_RU = "\u0412\u0435\u0440\u0441\u0438\u044f 0.1.0\n-------------\n * \u041f\u0435\u0440\u0432\u044b\u0439 \u043f\u0430\u043a\u0435\u0442 \u043c\u043e\u0434\u0443\u043b\u044f Unity.\n".encode("utf-8")

FIXTURE_REPO_LICENSE = b"Fixture unpackrat MIT grant\n"
FIXTURE_LICENSES = {
    "Observer.txt": b"Fixture Observer LGPLv3 notice\n",
    "GPL-3.0.txt": b"Fixture GPLv3 text\n",
    "YY-Thunks.txt": b"Fixture YY-Thunks notice\n",
    "Unity.txt": b"Fixture Unity GPL-3.0 AssetRipper and .NET notices\n",
}

# The MIT-only sentence the default preamble carries; the unity document must not repeat it.
MIT_ONLY_CLAIM = b"unpackrat is published under the MIT license"


class UnityFixtureCase(unittest.TestCase):
    """Builds a tiny repository with every selectable module's files and staged binaries."""

    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.root = Path(self._temporary.name)
        self.repo = self.root / "repo"
        self.output = self.root / "packages"

    def _write_module(self, module: str, version: str) -> None:
        directory = self.repo / "src" / "modules" / module
        directory.mkdir(parents=True)
        (directory / "VERSION").write_text(version + "\n", encoding="utf-8")
        if module == UNITY:
            (directory / "observer_user.ini").write_bytes(UNITY_OBSERVER_INI)
            (directory / "ChangeLog.en.txt").write_bytes(UNITY_CHANGELOG_EN)
            (directory / "ChangeLog.ru.txt").write_bytes(UNITY_CHANGELOG_RU)
        else:
            (directory / "observer_user.ini").write_bytes(
                f"[Modules]\n{module}=modules\\{module}.so\n".encode("utf-8")
            )
            (directory / "ChangeLog.en.txt").write_bytes(b"Version note EN.\n")
            (directory / "ChangeLog.ru.txt").write_bytes(b"Version note RU.\n")

    def make_repository(self, modules: tuple[str, ...] = MODULES_UNDER_TEST) -> Path:
        (self.repo).mkdir(parents=True, exist_ok=True)
        (self.repo / "LICENSE.txt").write_bytes(FIXTURE_REPO_LICENSE)
        licenses = self.repo / "licenses"
        licenses.mkdir()
        # Every notice a licensed module can request must exist in the fixture repository, so a
        # packaging run that assembles any module's document finds all of its inputs.
        for name in sorted({name for names in main.LICENSES.values() for name in names}):
            body = FIXTURE_LICENSES.get(name, f"Fixture {name} notice\n".encode("utf-8"))
            (licenses / name).write_bytes(body)
        for module in (*modules, UNITY):
            self._write_module(module, UNITY_VERSION if module == UNITY else "1.2.3")
            for architecture, binary, symbol in (
                ("x86", UNITY_SO_X86, UNITY_PDB_X86),
                ("x64", UNITY_SO_X64, UNITY_PDB_X64),
            ):
                stage = self.repo / "out" / "native" / "bin" / architecture / "Release"
                stage.mkdir(parents=True, exist_ok=True)
                if module == UNITY:
                    (stage / f"{module}.so").write_bytes(binary)
                    (stage / f"{module}.pdb").write_bytes(symbol)
                else:
                    (stage / f"{module}.so").write_bytes(f"{module}-{architecture}.so\n".encode())
                    (stage / f"{module}.pdb").write_bytes(f"{module}-{architecture}.pdb\n".encode())
        return self.repo

    def read_zip(self, path: Path) -> dict[str, bytes]:
        with zipfile.ZipFile(path) as opened:
            return {name: opened.read(name) for name in opened.namelist()}


class UnityPackageSelectionTests(UnityFixtureCase):
    def test_unity_joins_the_package_selection_but_not_the_default_module_selection(self) -> None:
        self.assertIn(UNITY, main.PACKAGE_MODULES, "unity must be a selectable package module")
        self.assertNotIn(UNITY, main.MODULES, "unity must stay out of the native coverage/analyzer set")
        self.assertEqual(main.MODULES, MODULES_UNDER_TEST, "the four shipped modules must be unchanged")
        self.assertEqual(main.MODULE_BINARIES.get(UNITY), "unity.so")

    def test_unity_registers_its_gpl_and_third_party_notices(self) -> None:
        self.assertIn(UNITY, main.LICENSES, "unity must register its license notices")
        notices = tuple(main.LICENSES[UNITY])
        self.assertEqual(notices[:2], ("Observer.txt", "GPL-3.0.txt"))
        self.assertIn("YY-Thunks.txt", notices)
        self.assertIn("Unity.txt", notices)


class UnityLicenseDocumentTests(UnityFixtureCase):
    def test_unity_document_labels_the_combined_binary_as_gpl3(self) -> None:
        self.make_repository()
        document = main._licenses_document(self.repo, UNITY)
        self.assertIn(FIXTURE_REPO_LICENSE, document)
        self.assertIn(FIXTURE_LICENSES["GPL-3.0.txt"], document)
        self.assertIn(FIXTURE_LICENSES["Unity.txt"], document)
        self.assertIn(b"GPL-3.0", document)
        self.assertNotIn(
            MIT_ONLY_CLAIM, document,
            "the unity document must not present the module as an MIT-only module",
        )

    def test_existing_modules_keep_the_default_preamble(self) -> None:
        self.make_repository()
        document = main._licenses_document(self.repo, "renpy")
        self.assertIn(MIT_ONLY_CLAIM, document)
        self.assertNotIn(b"GPL-3.0 combined work", document)


class UnityArchiveTests(UnityFixtureCase):
    def test_unity_packages_one_shared_object_per_architecture(self) -> None:
        repository = self.make_repository()
        created = main.package(repository=repository, output=self.output, modules=(UNITY,))
        names = sorted(Path(path).name for path in created)
        self.assertEqual(
            names,
            [
                f"{UNITY}-{UNITY_VERSION}-bin-x64.zip",
                f"{UNITY}-{UNITY_VERSION}-bin-x86.zip",
                f"{UNITY}-{UNITY_VERSION}-pdb.zip",
            ],
        )
        for architecture, binary in (("x86", UNITY_SO_X86), ("x64", UNITY_SO_X64)):
            members = self.read_zip(self.output / f"{UNITY}-{UNITY_VERSION}-bin-{architecture}.zip")
            self.assertEqual(
                sorted(members),
                sorted(["unity.so", "observer_user.ini", "ChangeLog.en.txt", "ChangeLog.ru.txt",
                        "LICENSES.txt"]),
            )
            self.assertEqual(members["unity.so"], binary)
            self.assertEqual(members["observer_user.ini"], UNITY_OBSERVER_INI)
            self.assertEqual(members["ChangeLog.en.txt"], UNITY_CHANGELOG_EN)
            self.assertEqual(members["ChangeLog.ru.txt"], UNITY_CHANGELOG_RU)
            self.assertNotIn(MIT_ONLY_CLAIM, members["LICENSES.txt"])
        symbols = self.read_zip(self.output / f"{UNITY}-{UNITY_VERSION}-pdb.zip")
        self.assertEqual(symbols, {"x86/unity.pdb": UNITY_PDB_X86, "x64/unity.pdb": UNITY_PDB_X64})

    def test_package_reports_a_missing_unity_binary_prerequisite(self) -> None:
        repository = self.make_repository()
        (repository / "out" / "native" / "bin" / "x64" / "Release" / "unity.so").unlink()
        with self.assertRaisesRegex(main.BuildError, r"unity\.so"):
            main.package(repository=repository, output=self.output, modules=(UNITY,))

    def test_package_reports_a_missing_unity_symbol_prerequisite(self) -> None:
        repository = self.make_repository()
        (repository / "out" / "native" / "bin" / "x86" / "Release" / "unity.pdb").unlink()
        with self.assertRaisesRegex(main.BuildError, r"unity\.pdb"):
            main.package(repository=repository, output=self.output, modules=(UNITY,))


class UnityCliTests(UnityFixtureCase):
    def test_default_package_selection_still_omits_unity(self) -> None:
        repository = self.make_repository()
        code = main.main(["package", "-Repository", str(repository), "-Output", str(self.output)])
        self.assertEqual(code, 0)
        created = sorted(path.name for path in self.output.glob("*.zip"))
        self.assertFalse([name for name in created if name.startswith(f"{UNITY}-")], created)
        self.assertIn("renpy-1.2.3-bin-x64.zip", created)

    def test_cli_selects_unity_by_name(self) -> None:
        repository = self.make_repository()
        code = main.main(
            ["package", "-Repository", str(repository), "-Output", str(self.output), "-Module", UNITY]
        )
        self.assertEqual(code, 0)
        self.assertEqual(
            sorted(path.name for path in self.output.glob("*.zip")),
            [
                f"{UNITY}-{UNITY_VERSION}-bin-x64.zip",
                f"{UNITY}-{UNITY_VERSION}-bin-x86.zip",
                f"{UNITY}-{UNITY_VERSION}-pdb.zip",
            ],
        )


class UnityShippedTreeTests(unittest.TestCase):
    """The real ``src/modules/unity`` registration must match the shipped layout."""

    def test_version_is_the_experimental_patch_release(self) -> None:
        self.assertEqual(main.read_version(REPOSITORY_ROOT, UNITY), UNITY_VERSION)

    def test_observer_registration_uses_the_module_binary_and_a_narrow_filter(self) -> None:
        source = REPOSITORY_ROOT / "src" / "modules" / UNITY / "observer_user.ini"
        config = configparser.ConfigParser(interpolation=None)
        config.read(source, encoding="utf-8")
        self.assertEqual(config.get("Modules", "Unity"), r"modules\unity.so")
        filters = config.get("Filters", "Unity")
        self.assertIn("*.assets", filters)
        self.assertNotIn("*;", filters + ";")
        self.assertNotEqual(filters, "*")
        self.assertFalse(config.has_section("Unity"), "no undocumented per-module settings")

    def test_bilingual_changelogs_document_the_version(self) -> None:
        english = (REPOSITORY_ROOT / "src" / "modules" / UNITY / "ChangeLog.en.txt").read_text("utf-8")
        russian = (REPOSITORY_ROOT / "src" / "modules" / UNITY / "ChangeLog.ru.txt").read_text("utf-8")
        self.assertIn(UNITY_VERSION, english)
        self.assertIn(UNITY_VERSION, russian)

    def test_shipped_unity_notice_bundle_labels_gpl_and_the_third_parties(self) -> None:
        notice = (REPOSITORY_ROOT / "licenses" / "Unity.txt").read_bytes()
        self.assertTrue(notice.strip(), "licenses/Unity.txt must not be empty")
        self.assertIn(b"GNU GENERAL PUBLIC LICENSE", notice)
        self.assertIn(b"AssetRipper", notice)
        self.assertIn(b"beeb50ee1c223e856c860caa338e5309404956a3", notice)
        self.assertIn(b"THIRD-PARTY-NOTICES", notice)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
