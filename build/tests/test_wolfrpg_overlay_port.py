"""The UberWolf Wolf RPG overlay port pins immutable source and packages UberWolfLib.

The port builds exactly one static library from the pinned UberWolf revision, installs
its headers under a namespaced path and carries five port-local compatibility patches:
``version.patch`` removes the optional auto-updater include so ``GetVersion`` returns the
pinned literal, ``x86-size-overloads.patch`` collapses the file-access overloads that
collide with ``std::size_t`` on 32-bit builds while hardening their bounds checks,
``wolfx-file-size.patch`` widens the WolfX file-size field to ``std::uintmax_t`` so the
aggregate that stores ``std::filesystem::file_size`` no longer narrows on 32-bit builds,
``default-crypt-modes.patch`` exposes the static ``WolfDec::DefaultCryptModes`` accessor
over the unchanged default table, and ``keycreate-scratch.patch`` replaces the unchecked
``malloc``/``free`` scratch in ``DXArchive::KeyCreate`` with an owned ``std::vector<char>``
so an allocation failure unwinds as ``std::bad_alloc`` instead of writing through a null
pointer.
This module checks the port identity, the compiled source list, the packaging layout, the
patch behaviour against repo-owned pinned-source fixtures, the byte-preserving fixture
storage and the combined copyright notice.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main  # noqa: E402  (the source-gate selectors live beside the test package)


REPOSITORY = Path(__file__).resolve().parents[2]
PORT = REPOSITORY / "build" / "vcpkg" / "ports" / "uberwolf"
NOTICE = REPOSITORY / "licenses" / "uberwolf.txt"
FIXTURES = REPOSITORY / "build" / "tests" / "fixtures" / "uberwolf"
FIXTURE_MANIFEST = FIXTURES / "fixture-manifest.json"

# The pinned member bytes are stored with a ``.fixture`` suffix.  The suffix keeps the
# working-tree bytes stable across a Git checkout that would otherwise apply the root
# ``* text eol=crlf`` rule, and it keeps the fixtures out of the source/format gates that
# select files by their ``.cpp``/``.h``/``.hpp``/``.ps1`` suffix.  The manifest keeps the
# real upstream member names as logical keys, so a patch always targets the upstream path.
FIXTURE_SUFFIX = ".fixture"

REF = "f4191375d289171b1f29dbc5ca320133b2b4b96a"
SHA512 = (
    "28105836e9fb6af1b380d72066b57007316aa28c20480e0e1583a2252bdb27e3420d6d8b134332838"
    "68dd60236d629818453208836b1e7824eeffa143240a725"
)
VERSION = "0.6.4"

FILEACCESS_PATH = "UberWolfLib/WolfRPG/FileAccess.hpp"
UBERWOLFLIB_PATH = "UberWolfLib/UberWolfLib.cpp"
WOLFX_TYPES_PATH = "UberWolfLib/WolfX/Types.hpp"
DXARCHIVE_PATH = "3rdParty/DXLib/DXArchive.cpp"

LIBRARY_SOURCES = {
    "3rdParty/DXLib/CharCode.cpp",
    "3rdParty/DXLib/CharCodeTable.cpp",
    "3rdParty/DXLib/DXArchive.cpp",
    "3rdParty/DXLib/DXArchiveVer5.cpp",
    "3rdParty/DXLib/DXArchiveVer6.cpp",
    "3rdParty/DXLib/FileLib.cpp",
    "3rdParty/DXLib/Huffman.cpp",
    "3rdParty/lz4/lz4.c",
    "UberWolfLib/Localizer.cpp",
    "UberWolfLib/GameContext.cpp",
    "UberWolfLib/UberLog.cpp",
    "UberWolfLib/UberWolfLib.cpp",
    "UberWolfLib/WolfDec.cpp",
    "UberWolfLib/WolfPro.cpp",
    "UberWolfLib/WolfUtils.cpp",
    "UberWolfLib/WolfXWrapper.cpp",
}

PATCHES = (
    "version.patch", "x86-size-overloads.patch", "wolfx-file-size.patch",
    "default-crypt-modes.patch", "mode-selection.patch", "archive-callbacks.patch",
    "archive-entry-plan.patch", "bounded-huffman.patch", "keycreate-scratch.patch",
    "recovery-e1-contract.patch", "encoding-aware-legacy.patch",
)

PORT_FILES = {
    "CMakeLists.txt",
    "copyright",
    "default-crypt-modes.patch",
    "keycreate-scratch.patch",
    "portfile.cmake",
    "vcpkg.json",
    "version.patch",
    "wolfx-file-size.patch",
    "x86-size-overloads.patch",
    "mode-selection.patch", "archive-callbacks.patch", "archive-entry-plan.patch",
    "bounded-huffman.patch", "recovery-e1-contract.patch", "encoding-aware-legacy.patch",
}

READ_SIZE_T = "ReadBytes(void* pBuffer, const std::size_t& size)"
READ_UINT32 = "ReadBytes(void* pBuffer, const uint32_t& size)"
WRITE_SIZE_T = "WriteBytes(const void* pBuffer, const std::size_t& size)"


def _expression(pattern: str) -> re.Pattern[str]:
    """Run the shared ECMAScript/RE2 subset with Python named-group syntax."""
    return re.compile(re.sub(r"\(\?<([A-Za-z][A-Za-z0-9_]*)>", r"(?P<\1>", pattern))


def _fixture_backing(member: str) -> Path:
    """Return the on-disk fixture path that stores one pinned upstream member."""
    return FIXTURES / f"{member}{FIXTURE_SUFFIX}"


def _pinned_source(member: str) -> str:
    """Return a repo-owned fixture proven to originate from the pinned revision."""
    path = _fixture_backing(member)
    manifest = json.loads(FIXTURE_MANIFEST.read_text(encoding="utf-8"))
    expected = manifest["files"][member]["sha256"]
    actual = hashlib.sha256(path.read_bytes()).hexdigest()
    if actual != expected:
        raise AssertionError(f"fixture hash mismatch for {member}: {actual}")
    return path.read_text(encoding="utf-8")


def _run_git_apply(patch: Path, workdir: Path) -> None:
    """Apply a unified diff with the real ``git apply`` and fail loudly on a mismatch."""
    result = subprocess.run(
        ["git", "-c", "core.autocrlf=false", "apply", "-p1", str(patch)],
        cwd=str(workdir),
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise RuntimeError(f"git apply failed ({result.returncode}): {result.stderr.strip()}")


def _apply_port_patch(patch_name: str, member: str) -> str:
    """Apply a port patch to the real pinned file and return the patched text."""
    source = _pinned_source(member)
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        target = root / member
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(source, encoding="utf-8", newline="")
        patch = root / "port.patch"
        patch.write_bytes((PORT / patch_name).read_bytes())
        _run_git_apply(patch, root)
        return target.read_text(encoding="utf-8")


class ManifestTests(unittest.TestCase):
    def test_port_manifest_pins_a_static_windows_library(self) -> None:
        descriptor = json.loads((PORT / "vcpkg.json").read_text(encoding="utf-8"))
        self.assertEqual(descriptor["name"], "uberwolf")
        self.assertEqual(descriptor["version-string"], VERSION)
        self.assertEqual(descriptor["homepage"], "https://github.com/Sinflower/UberWolf")
        self.assertEqual(descriptor["supports"], "windows & !uwp")
        self.assertIn("MIT", descriptor["license"])
        self.assertIn("BSD-2-Clause", descriptor["license"])
        dependencies = descriptor["dependencies"]
        self.assertIn({"name": "vcpkg-cmake", "host": True}, dependencies)
        self.assertIn("nlohmann-json", {entry if isinstance(entry, str) else entry["name"] for entry in dependencies})

    def test_root_manifest_declares_the_uberwolf_dependency_once(self) -> None:
        manifest = json.loads((REPOSITORY / "vcpkg.json").read_text(encoding="utf-8"))
        entries = [entry for entry in manifest["dependencies"] if entry["name"] == "uberwolf"]
        self.assertEqual(entries, [{"name": "uberwolf"}])

    def test_renovate_monitors_the_pinned_source_revision(self) -> None:
        configuration = json.loads((REPOSITORY / ".github/renovate.json").read_text(encoding="utf-8"))
        managers = [m for m in configuration["customManagers"] if m.get("depNameTemplate") == "uberwolf"]
        self.assertEqual(len(managers), 1)
        manager = managers[0]
        self.assertEqual(manager["packageNameTemplate"], "https://github.com/Sinflower/UberWolf")
        self.assertEqual(manager["datasourceTemplate"], "git-refs")
        self.assertEqual(manager["versioningTemplate"], "exact")
        patterns = manager["managerFilePatterns"]
        self.assertEqual(len(patterns), 1)
        selected = [
            path
            for path in REPOSITORY.glob("build/vcpkg/ports/*/portfile.cmake")
            if re.search(patterns[0][1:-1], path.relative_to(REPOSITORY).as_posix())
        ]
        self.assertEqual(len(selected), 1)
        contents = selected[0].read_text(encoding="utf-8")
        digests = [
            match.group("currentDigest")
            for pattern in manager["matchStrings"]
            for match in _expression(pattern).finditer(contents)
        ]
        self.assertEqual(digests, [REF])


class RecipeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.recipe = (PORT / "portfile.cmake").read_text(encoding="utf-8")

    def test_recipe_is_pinned_and_patchable(self) -> None:
        self.assertIn("vcpkg_check_linkage(ONLY_STATIC_LIBRARY)", self.recipe)
        self.assertIn("vcpkg_from_github", self.recipe)
        self.assertIn("REPO Sinflower/UberWolf", self.recipe)
        self.assertIn(f"REF {REF}", self.recipe)
        self.assertIn(f"SHA512 {SHA512}", self.recipe)
        self.assertNotIn("HEAD_REF", self.recipe)
        self.assertIn("PATCHES", self.recipe)
        for patch in PATCHES:
            with self.subTest(patch=patch):
                self.assertIn(patch, self.recipe)
                self.assertTrue((PORT / patch).is_file())

    def test_recipe_registers_every_patch_in_the_documented_order(self) -> None:
        block = re.search(r"PATCHES\n(.*?)\n\)", self.recipe, re.S)
        self.assertIsNotNone(block)
        registered = [line.strip() for line in block.group(1).splitlines()]
        self.assertEqual(registered, list(PATCHES))

    def test_recipe_builds_and_packages_without_updater_cli_or_gui(self) -> None:
        self.assertIn("vcpkg_cmake_configure", self.recipe)
        self.assertIn("vcpkg_cmake_install", self.recipe)
        self.assertIn("vcpkg_copy_pdbs", self.recipe)
        self.assertIn('file(COPY "${CMAKE_CURRENT_LIST_DIR}/CMakeLists.txt"', self.recipe)
        self.assertIn('"${CURRENT_PACKAGES_DIR}/debug/include"', self.recipe)
        self.assertIn('"${CURRENT_PACKAGES_DIR}/debug/share"', self.recipe)
        self.assertIn("vcpkg_install_copyright", self.recipe)
        self.assertIn('"${CMAKE_CURRENT_LIST_DIR}/copyright"', self.recipe)
        for forbidden in ("SelfUpdater", "UberWolfCli", "CLI11", ".vcxproj"):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, self.recipe)

    def test_port_directory_only_carries_the_expected_files(self) -> None:
        present = {path.name for path in PORT.iterdir() if path.is_file()}
        self.assertEqual(present, PORT_FILES)


class BuildTests(unittest.TestCase):
    def setUp(self) -> None:
        self.cmake = (PORT / "CMakeLists.txt").read_text(encoding="utf-8")

    def test_build_compiles_the_exact_static_library_source_list(self) -> None:
        self.assertIn("add_library(uberwolf STATIC", self.cmake)
        source_block = re.search(r"set\(UBERWOLF_SOURCES\s+(.*?)\n\)", self.cmake, re.S)
        self.assertIsNotNone(source_block)
        compiled = set(re.findall(r'"([^"]+\.(?:cpp|c))"', source_block.group(1)))
        self.assertEqual(compiled, LIBRARY_SOURCES)
        self.assertEqual({source.split("/")[0] for source in compiled}, {"3rdParty", "UberWolfLib"})

    def test_build_takes_the_provided_json_dependency(self) -> None:
        self.assertIn("find_package(nlohmann_json CONFIG REQUIRED)", self.cmake)
        self.assertIn("nlohmann_json::nlohmann_json", self.cmake)

    def test_build_applies_the_documented_dialect_and_warning_policy(self) -> None:
        self.assertIn("cxx_std_20", self.cmake)
        for definition in (
            "UNICODE",
            "_UNICODE",
            "NOMINMAX",
            "_CRT_SECURE_NO_WARNINGS",
            "_SILENCE_CXX17_CODECVT_HEADER_DEPRECATION_WARNING",
        ):
            with self.subTest(definition=definition):
                self.assertIn(definition, self.cmake)
        self.assertIn("/EHsc", self.cmake)
        for suppression in ("/WX", "/wd", "/MT", "/MD"):
            with self.subTest(suppression=suppression):
                self.assertNotIn(suppression, self.cmake)

    def test_headers_and_archive_install_under_a_namespaced_path(self) -> None:
        self.assertIn("ARCHIVE DESTINATION lib", self.cmake)
        self.assertIn("include/uberwolf", self.cmake)
        self.assertIn("UberWolfLib/", self.cmake)
        self.assertIn("3rdParty/DXLib/", self.cmake)
        self.assertNotIn("UberWolfCli", self.cmake)
        self.assertNotIn("CLI11", self.cmake)
        self.assertNotIn(".vcxproj", self.cmake)


class ProjectTests(unittest.TestCase):
    """The native test project must compile every repository-owned WolfRPG unit."""

    def setUp(self) -> None:
        self.project = (REPOSITORY / "build" / "projects" / "tests.vcxproj").read_text(encoding="utf-8")

    def test_project_compiles_the_native_wolfrpg_regression_units(self) -> None:
        for unit in ("uberwolf_file_access.cpp", "uberwolf_wolfx_file_size.cpp"):
            with self.subTest(unit=unit):
                entry = f'<ClCompile Include="$(RepositoryRoot)src\\tests\\unit\\{unit}" />'
                self.assertIn(entry, self.project)

    def test_project_compiles_the_default_crypt_modes_unit(self) -> None:
        entry = (
            '<ClCompile Include="$(RepositoryRoot)src\\tests\\unit\\uberwolf_modes.cpp" />'
        )
        self.assertIn(entry, self.project)


class PatchTests(unittest.TestCase):
    def test_version_patch_drops_the_updater_include_and_pins_the_literal(self) -> None:
        patched = _apply_port_patch("version.patch", UBERWOLFLIB_PATH)
        self.assertNotIn("SelfUpdater", patched)
        self.assertNotIn("selfUpdater", patched)
        self.assertIn('\treturn TEXT("0.6.4");', patched)
        self.assertIn("tString UberWolfLib::GetVersion()", patched)
        self.assertIn("UWLExitCode UberWolfLib::packData(const tString& dataPath)", patched)

    def test_x86_patch_bounds_the_reader_before_it_copies(self) -> None:
        patched = _apply_port_patch("x86-size-overloads.patch", FILEACCESS_PATH)
        self.assertEqual(patched.count(READ_SIZE_T), 1)
        self.assertNotIn(READ_UINT32, patched)
        self.assertNotIn(
            'if (m_offset + size > m_size)\n\t\t\tthrow(FileWalkerException("ReadBytes:', patched
        )
        self.assertIn("\t\tif (m_offset > m_size || size > m_size - m_offset)\n", patched)

    def test_x86_patch_accepts_zero_reads_without_null_pointer_use(self) -> None:
        patched = _apply_port_patch("x86-size-overloads.patch", FILEACCESS_PATH)
        self.assertIn("\t\tif (size == 0)\n\t\t\treturn;\n", patched)
        self.assertIn("\t\tif (pBuffer == nullptr)\n", patched)

    def test_x86_patch_validates_writer_sizes_before_mutating_state(self) -> None:
        patched = _apply_port_patch("x86-size-overloads.patch", FILEACCESS_PATH)
        self.assertEqual(patched.count(WRITE_SIZE_T), 1)
        self.assertNotIn("\tvoid WriteBytes(const void* pBuffer, const uint32_t& size)\n", patched)
        self.assertNotIn("WriteBytes(pBuffer, static_cast<uint32_t>(size))", patched)
        self.assertIn("writeBytesChecked(pBuffer, size);", patched)
        self.assertIn("throw(FileWriterException(\"WriteBytes: negative size\"));", patched)
        self.assertIn("std::numeric_limits<std::streamsize>::max()", patched)
        self.assertIn("std::numeric_limits<uint64_t>::max() - m_size", patched)
        self.assertIn("m_buffer.max_size() - m_buffer.size()", patched)
        self.assertIn("\t\tif (size == 0)\n\t\t\treturn;\n", patched)
        self.assertIn("\t\tm_size += size;\n", patched)

    def test_x86_patch_checks_forwarded_integer_representability(self) -> None:
        patched = _apply_port_patch("x86-size-overloads.patch", FILEACCESS_PATH)
        self.assertIn("std::in_range<std::size_t>(size)", patched)
        self.assertIn(
            "\t\t\tthrow(FileWriterException(\"WriteBytes: size is not representable as std::size_t\"));\n",
            patched,
        )
        self.assertNotIn("writeBytesChecked(pBuffer, static_cast<std::size_t>(size));", patched)
        self.assertIn("writeBytesChecked(pBuffer, size);", patched)

    def test_x86_patch_keeps_one_unambiguous_forwarding_overload(self) -> None:
        patched = _apply_port_patch("x86-size-overloads.patch", FILEACCESS_PATH)
        self.assertIn(
            "\t\trequires (std::is_integral_v<TSize> && !std::is_same_v<std::remove_cv_t<TSize>, std::size_t>)\n",
            patched,
        )
        self.assertIn("\tvoid WriteBytes(const void* pBuffer, const TSize& size)\n", patched)

    def test_wolfx_patch_widens_the_file_size_field_to_the_filesystem_contract(self) -> None:
        patched = _apply_port_patch("wolfx-file-size.patch", WOLFX_TYPES_PATH)
        self.assertIn("\tstd::uintmax_t fileSize;\n", patched)
        self.assertNotIn("\tstd::size_t fileSize;\n", patched)

    def test_keycreate_patch_replaces_the_unchecked_scratch_with_owned_storage(self) -> None:
        patched = _apply_port_patch("keycreate-scratch.patch", DXARCHIVE_PATH)
        region = patched.split("void DXArchive::KeyCreate(", 1)[1].split("void DXArchive::KeyConv(", 1)[0]
        self.assertIn("\tstd::vector<char> DynamicWorkBuffer;\n", region)
        self.assertIn("\t\tDynamicWorkBuffer.resize(SourceBytes);\n", region)
        self.assertIn("\t\tUseWorkBuffer = DynamicWorkBuffer.data();\n", region)
        self.assertNotIn("malloc", region)
        self.assertNotIn("free(", region)

    def test_patch_application_rejects_a_mismatched_source(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            target = root / FILEACCESS_PATH
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("// unrelated source that the patch cannot match\n", encoding="utf-8")
            patch = root / "port.patch"
            patch.write_bytes((PORT / "x86-size-overloads.patch").read_bytes())
            with self.assertRaises(RuntimeError):
                _run_git_apply(patch, root)

    def test_mandatory_patch_checks_run_without_an_external_archive(self) -> None:
        versioned = _apply_port_patch("version.patch", UBERWOLFLIB_PATH)
        access = _apply_port_patch("x86-size-overloads.patch", FILEACCESS_PATH)
        sizes = _apply_port_patch("wolfx-file-size.patch", WOLFX_TYPES_PATH)

        self.assertIn('\treturn TEXT("0.6.4");', versioned)
        self.assertIn("\t\tif (m_offset > m_size || size > m_size - m_offset)\n", access)
        self.assertIn("std::in_range<std::size_t>(size)", access)
        self.assertIn("\tstd::uintmax_t fileSize;\n", sizes)


class FixtureStorageTests(unittest.TestCase):
    """Pinned members survive checkout byte for byte and stay out of the source gates."""

    def _members(self) -> list[str]:
        manifest = json.loads(FIXTURE_MANIFEST.read_text(encoding="utf-8"))
        return sorted(manifest["files"])

    def test_pinned_members_are_stored_under_a_fixture_suffix(self) -> None:
        members = self._members()
        self.assertTrue(members)
        for member in members:
            with self.subTest(member=member):
                backing = _fixture_backing(member)
                self.assertTrue(backing.is_file(), backing)
                self.assertFalse((FIXTURES / member).exists())
                self.assertEqual(backing.suffix, FIXTURE_SUFFIX)

    def test_git_disables_text_conversion_for_every_fixture(self) -> None:
        paths = [(FIXTURES / f"{member}{FIXTURE_SUFFIX}").relative_to(REPOSITORY).as_posix()
                 for member in self._members()]
        result = subprocess.run(
            ["git", "-c", f"safe.directory={REPOSITORY}", "-C", str(REPOSITORY),
             "check-attr", "-z", "--stdin", "text"],
            input=("\0".join(paths) + "\0").encode(), capture_output=True, check=True,
        )
        fields = result.stdout.decode().rstrip("\0").split("\0")
        self.assertEqual(len(fields), len(paths) * 3)
        for index in range(0, len(fields), 3):
            with self.subTest(path=fields[index]):
                self.assertEqual(fields[index + 1], "text")
                self.assertEqual(fields[index + 2], "unset")

    def test_fixture_backing_files_stay_out_of_the_source_gates(self) -> None:
        suffixes = main._CPP_SUFFIXES + main._PS_SUFFIXES
        discovered = set(main._source_files(REPOSITORY, suffixes))
        self.assertEqual({path for path in discovered if FIXTURES in path.parents}, set())
        for member in self._members():
            with self.subTest(member=member):
                suffix = _fixture_backing(member).suffix
                self.assertNotIn(suffix, main._CPP_SUFFIXES)
                self.assertNotIn(suffix, main._PS_SUFFIXES)


class NoticeTests(unittest.TestCase):
    def test_port_copyright_matches_the_retained_notice(self) -> None:
        self.assertEqual((PORT / "copyright").read_bytes(), NOTICE.read_bytes())

    def test_notices_state_the_dxlib_copyright_inside_the_dxlib_section(self) -> None:
        required = "DX Library Copyright (C) 2001-2026 Takumi Yamada."
        for label, path in (("licenses", NOTICE), ("port", PORT / "copyright")):
            with self.subTest(notice=label):
                notice = path.read_text(encoding="utf-8")
                dxlib_section = notice.split("3rdParty/DXLib", 1)[1].split("3rdParty/lz4", 1)[0]
                self.assertIn(required, dxlib_section)

    def test_notice_reproduces_the_pinned_lz4_notice(self) -> None:
        notice = NOTICE.read_text(encoding="utf-8")
        lz4_notice = _pinned_source("3rdParty/lz4/lz4.h")
        self.assertIn(lz4_notice, notice)
        self.assertNotIn("2011-2020", notice)

    def test_notice_preserves_every_compiled_component_grant(self) -> None:
        notice = NOTICE.read_text(encoding="utf-8")
        for marker in (
            "Sinflower",  # UberWolfLib and WolfCrypt wrappers -- MIT
            "Yann Collet",  # 3rdParty/lz4 -- BSD-2-Clause
            "lz4",
            "WolfSha512",  # WolfCrypt SHA-512 helper -- Sinflower MIT (no stale attribution)
            "tiny-AES-c",  # WolfCrypt AES base source (Unlicense)
            "chacha20-c",  # WolfCrypt ChaCha20 base source (Unlicense)
            "Unlicense",
            "Takumi Yamada",  # 3rdParty/DXLib
            "dxlicense",
            "Niels Lohmann",  # nlohmann-json dependency -- MIT
        ):
            with self.subTest(marker=marker):
                self.assertIn(marker, notice)
        self.assertNotIn("CLI11", notice)


class PatchCheckoutTests(unittest.TestCase):
    """Vendored port patches must check out as LF so ``git apply`` matches the pinned source.

    The root ``* text eol=crlf`` rule would otherwise rewrite every port patch to CRLF, and
    ``git apply`` compares bytes rather than line endings, so the port has to advertise LF for
    its patches and a real checkout has to reproduce those bytes.
    """

    PATCHES = PATCHES

    def test_every_port_patch_declares_line_feed_terminators(self) -> None:
        paths = [(PORT / name).relative_to(REPOSITORY).as_posix() for name in self.PATCHES]
        result = subprocess.run(
            ["git", "-c", f"safe.directory={REPOSITORY}", "-C", str(REPOSITORY),
             "check-attr", "-z", "--stdin", "text", "eol"],
            input=("\0".join(paths) + "\0").encode(), capture_output=True, check=True,
        )
        fields = result.stdout.decode().rstrip("\0").split("\0")
        # ``-z`` emits one ``path\0attribute\0value\0`` record per path and attribute.
        self.assertEqual(len(fields), len(paths) * 2 * 3)
        attributes = {}
        for index in range(0, len(fields), 3):
            path, attribute, value = fields[index:index + 3]
            self.assertIn(path, paths)
            attributes[(path, attribute)] = value
        for path in paths:
            with self.subTest(path=path):
                self.assertEqual(attributes[(path, "text")], "set")
                self.assertEqual(attributes[(path, "eol")], "lf")

    def test_a_checkout_materialises_every_port_patch_without_carriage_returns(self) -> None:
        paths = [f"build/vcpkg/ports/uberwolf/{name}" for name in self.PATCHES]
        with tempfile.TemporaryDirectory() as tmp:
            self._materialise_patches(paths, Path(tmp))
            for relative in paths:
                with self.subTest(path=relative):
                    data = (Path(tmp) / relative).read_bytes()
                    self.assertNotIn(b"\r", data)
                    self.assertIn(b"\n", data)

    def test_an_empty_patch_request_does_not_materialise_unrequested_source(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self._materialise_patches([], root)
            self.assertEqual(list(root.iterdir()), [])

    def test_fixture_integrity_rejects_changed_bytes_before_patch_application(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "changed.fixture"
            source.write_bytes(b"changed upstream member")
            caught = None
            with mock.patch.object(sys.modules[__name__], "_fixture_backing", return_value=source):
                try:
                    _pinned_source(FILEACCESS_PATH)
                except AssertionError as error:
                    caught = str(error)
            self.assertIsNotNone(caught)
            self.assertIn("fixture hash mismatch", caught)

    def test_untracked_patch_bytes_are_materialised_without_an_index_entry(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "new.patch").write_bytes(b"owned patch bytes\n")
            destination = root / "checkout"
            destination.mkdir()
            with mock.patch.object(sys.modules[__name__], "REPOSITORY", root), mock.patch(
                    "subprocess.run", return_value=subprocess.CompletedProcess([], 0, b"")):
                self._materialise_patches(["new.patch"], destination)
            self.assertEqual((destination / "new.patch").read_bytes(), b"owned patch bytes\n")

    @staticmethod
    def _materialise_patches(paths: list[str], destination: Path) -> None:
        """Copy every patch into ``destination`` the way a fresh checkout would.

        ``git checkout-index`` can only materialise paths that are already in the index, so a
        patch that is still an untracked working-tree addition (before integration commits it)
        is taken from the working tree.  Both routes feed the same ``eol=lf`` attribute, which
        the sibling test proves is declared for every patch.
        """
        if not paths:
            return
        tracked = subprocess.run(
            ["git", "-c", f"safe.directory={REPOSITORY}", "-C", str(REPOSITORY),
             "ls-files", "-z", "--", *paths],
            capture_output=True, check=True,
        ).stdout.decode().split("\0")
        tracked = [path for path in tracked if path]
        if tracked:
            prefix = str(destination.resolve()).replace("\\", "/") + "/"
            subprocess.run(
                ["git", "-c", f"safe.directory={REPOSITORY}", "-C", str(REPOSITORY),
                 "checkout-index", "-f", f"--prefix={prefix}", "--", *tracked],
                capture_output=True, check=True,
            )
        for relative in paths:
            if relative in tracked:
                continue
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((REPOSITORY / relative).read_bytes())


if __name__ == "__main__":
    unittest.main()
