"""Tests-first contract for WolfRPG registration in the minimal build driver.

This module is written before WolfRPG is wired into ``build/main.py`` and the
MSBuild projects, so every test below is expected to fail (RED) against the
current tree.  The behaviour it pins is the *observable* integration contract:

* WolfRPG is the fifth and last module of the default selection that drives the
  native audit, coverage enumeration and packaging routes
  (``main.MODULES`` / ``main.MODULE_BINARIES`` / ``main.LICENSES``);
* the default packaging route ships ``wolfrpg-1.0.1-bin-x64.zip``,
  ``wolfrpg-1.0.1-bin-x86.zip`` and ``wolfrpg-1.0.1-pdb.zip`` with the shipped
  payload, registration and notices;
* ``build/projects/tests.vcxproj`` references ``wolfrpg.vcxproj`` with the fixed
  project GUID and ``LinkLibraryDependencies=false``;
* ``build/projects/wolfrpg.vcxproj`` builds a ``wolfrpg.so`` dynamic library
  through the shared lifecycle translation units and a project-owned adapter that
  isolates the pinned UberWolf library;
* the native release audit and the standalone analyzer set gate the WolfRPG
  module like every other shipped module;
* the README publishes the WolfRPG row, its bilingual change logs and the
  UberWolf dependency attribution.

The tests assert produced bytes, parsed XML fields and recorded invocations,
never re-imported source text, so they cannot pass by mirroring an internal
constant.  ``GuardedCase`` forbids any real native subprocess.
"""

from __future__ import annotations

import configparser
from pathlib import Path, PureWindowsPath
import subprocess
import unittest
from unittest import mock
import xml.etree.ElementTree as ET
import zipfile

from test_minimal_build import GuardedCase, RecordingRun

import main  # noqa: E402  (the build root is added to sys.path by test_minimal_build)


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
PROJECTS = REPOSITORY_ROOT / "build" / "projects"
NS = {"msb": "http://schemas.microsoft.com/developer/msbuild/2003"}

WOLFRPG = "wolfrpg"
WOLFRPG_VERSION = "1.0.1"
WOLFRPG_GUID = "{709C4932-B3B2-47D3-B035-B5AFE7AB7A34}"
WOLFRPG_FUZZ_MAX_LENGTH = 1048576

# Independent fixture bytes: the shipped payloads are compared against these
# literals, never against a value rederived from the same source they came from.
WOLFRPG_SO_X64 = b"fake wolfrpg module x64 payload\n"
WOLFRPG_SO_X86 = b"fake wolfrpg module x86 payload\n"
WOLFRPG_PDB_X64 = b"fake wolfrpg x64 program database\n"
WOLFRPG_PDB_X86 = b"fake wolfrpg x86 program database\n"
WOLFRPG_OBSERVER_INI = b"[Modules]\nWolfRPG=modules\\wolfrpg.so\n\n[Filters]\nWolfRPG=*.wolf;*.wolfx\n"
WOLFRPG_CHANGELOG_EN = b"Version 1.0.0\n-------------\n + WolfRPG module added.\n"
WOLFRPG_CHANGELOG_RU = b"Version 1.0.0\n-------------\n + WolfRPG module added.\n"

# Required notices for the WolfRPG module: the project's own grant, the Observer
# API LGPLv3 notice with the GPLv3 text it incorporates, and the pinned UberWolf
# library notice the port worker supplies.
REQUIRED_WOLFRPG_NOTICES = ("Observer.txt", "GPL-3.0.txt", "uberwolf.txt")

# Every license file a fixture for the whole default selection may reference.
FIXTURE_LICENSE_NAMES = (
    "Observer.txt", "GPL-3.0.txt", "rpatool.txt", "serde-pickle.txt",
    "rgssad.txt", "godot.txt", "libspng.txt", "libwebp.txt", "libogg.txt", "brotli.txt", "zstd.txt",
    "fastlz.txt", "zlib-ng.txt", "bcdec.txt", "dr_libs.txt", "qoa.txt", "zydis.txt",
    "zycore.txt", "mbedcrypto.txt", "uberwolf.txt", "json.txt", "YY-Thunks.txt",
)


def _parse(path: Path) -> ET.Element:
    return ET.parse(path).getroot()


def _project_references(root: ET.Element) -> list[ET.Element]:
    return root.findall("msb:ItemGroup/msb:ProjectReference", NS)


def _imported_projects(path: Path) -> list[str]:
    """Return the import targets declared by an MSBuild project, by file name."""

    return [
        Path(element.get("Project", "")).name
        for element in _parse(path).findall("msb:Import", NS)
    ]


def _link_dependencies(path: Path) -> list[str]:
    """Return every ``Link/AdditionalDependencies`` value in a project file."""

    root = _parse(path)
    found: list[str] = []
    for group in root.findall("msb:ItemDefinitionGroup", NS):
        for link in group.findall("msb:Link", NS):
            for element in link.findall("msb:AdditionalDependencies", NS):
                if element.text and element.text not in found:
                    found.append(element.text)
    return found


def _compile_units(path: Path) -> list[str]:
    return [
        element.get("Include", "")
        for element in _parse(path).findall("msb:ItemGroup/msb:ClCompile", NS)
    ]


def _property(root: ET.Element, name: str) -> str | None:
    element = root.find(f".//msb:{name}", NS)
    return None if element is None else (element.text or "").strip()


class WolfRpgRegistration(GuardedCase):
    """The default Python selection must carry WolfRPG as its last module."""

    def _repository(self) -> Path:
        modules = {
            "renpy": "3.1.0",
            "rpgmaker": "1.1.0",
            "zanzarah": "2.1.0",
            "godot": "1.0.0",
            WOLFRPG: WOLFRPG_VERSION,
        }
        (self.repo / "LICENSE.txt").write_bytes(b"Fixture MIT license text.\n")
        licenses = self.repo / "licenses"
        licenses.mkdir()
        for name in FIXTURE_LICENSE_NAMES:
            (licenses / name).write_bytes(f"Fixture notice: {name}\n".encode("utf-8"))
        for module, version in modules.items():
            directory = self.repo / "src" / "modules" / module
            directory.mkdir(parents=True)
            (directory / "VERSION").write_text(version + "\n", encoding="utf-8")
            if module == WOLFRPG:
                (directory / "observer_user.ini").write_bytes(WOLFRPG_OBSERVER_INI)
                (directory / "ChangeLog.en.txt").write_bytes(WOLFRPG_CHANGELOG_EN)
                (directory / "ChangeLog.ru.txt").write_bytes(WOLFRPG_CHANGELOG_RU)
            else:
                (directory / "observer_user.ini").write_bytes(
                    f"[Modules]\n{module}=modules\\{module}.so\n".encode("utf-8")
                )
                (directory / "ChangeLog.en.txt").write_bytes(b"Version note EN.\n")
                (directory / "ChangeLog.ru.txt").write_bytes(b"Version note RU.\n")
        for architecture, binary, symbol in (
            ("x64", WOLFRPG_SO_X64, WOLFRPG_PDB_X64),
            ("x86", WOLFRPG_SO_X86, WOLFRPG_PDB_X86),
        ):
            stage = self.repo / "out" / "native" / "bin" / architecture / "Release"
            stage.mkdir(parents=True)
            (stage / "wolfrpg.so").write_bytes(binary)
            (stage / "wolfrpg.pdb").write_bytes(symbol)
            for module in modules:
                if module == WOLFRPG:
                    continue
                (stage / f"{module}.so").write_bytes(f"fake {module} .so {architecture}\n".encode())
                (stage / f"{module}.pdb").write_bytes(f"fake {module} .pdb {architecture}\n".encode())
        return self.repo

    def test_wolfrpg_is_the_last_default_module(self) -> None:
        self.assertIn(WOLFRPG, main.MODULES, "the default module selection must include wolfrpg")
        self.assertEqual(main.MODULES[-1], WOLFRPG, "wolfrpg must be the last default module")
        self.assertEqual(
            set(main.MODULES),
            {"renpy", "rpgmaker", "zanzarah", "godot", WOLFRPG},
            "wolfrpg must join the four existing modules exactly once",
        )
        self.assertEqual(main.MODULE_BINARIES.get(WOLFRPG), "wolfrpg.so")

    def test_shipped_registration_associates_wolf_and_wolfx_archives(self) -> None:
        registration = configparser.ConfigParser(interpolation=None)
        registration.optionxform = str
        with (REPOSITORY_ROOT / "src/modules/wolfrpg/observer_user.ini").open(encoding="utf-8") as source:
            registration.read_file(source)
        self.assertEqual(dict(registration["Modules"]), {"WolfRPG": r"modules\wolfrpg.so"})
        masks = registration["Filters"]["WolfRPG"].split(";")
        self.assertEqual(masks, ["*.wolf", "*.wolfx"])
        for filename, expected in (("Data.wolf", True), ("DATA.WOLFX", True), ("Data.wolfx.bak", False)):
            with self.subTest(filename=filename):
                self.assertEqual(any(PureWindowsPath(filename).match(mask) for mask in masks), expected)

    def test_wolfrpg_notice_set_is_registered(self) -> None:
        self.assertIn(WOLFRPG, main.LICENSES, "wolfrpg must register its license notices")
        notices = tuple(main.LICENSES[WOLFRPG])
        self.assertEqual(notices[:2], ("Observer.txt", "GPL-3.0.txt"))
        missing = [name for name in REQUIRED_WOLFRPG_NOTICES if name not in notices]
        self.assertEqual(missing, [], f"missing required wolfrpg notices: {missing}")
        self.assertEqual(
            notices, ("Observer.txt", "GPL-3.0.txt", "uberwolf.txt", "json.txt", "YY-Thunks.txt"),
            "wolfrpg must ship its own third-party notices, never a blanket set",
        )

    def test_default_packaging_route_ships_wolfrpg_archives(self) -> None:
        repository = self._repository()
        self.assertIn(WOLFRPG, main.MODULES, "the default packaging route must include wolfrpg")
        created = main.package(repository=repository, output=repository / "out" / "packages")
        names = {Path(path).name for path in created}
        expected = {
            "wolfrpg-1.0.1-bin-x64.zip",
            "wolfrpg-1.0.1-bin-x86.zip",
            "wolfrpg-1.0.1-pdb.zip",
        }
        self.assertTrue(
            expected <= names,
            f"default packaging must ship the wolfrpg archives; created: {sorted(names)}",
        )

    def test_wolfrpg_archives_carry_shipped_payload_and_registration(self) -> None:
        repository = self._repository()
        self.assertIn(WOLFRPG, main.LICENSES, "wolfrpg must be packageable before its archives can ship")
        main.package(repository=repository, output=repository / "out" / "packages", modules=(WOLFRPG,))
        packages = repository / "out" / "packages"

        for architecture, binary in (("x64", WOLFRPG_SO_X64), ("x86", WOLFRPG_SO_X86)):
            archive = packages / f"wolfrpg-{WOLFRPG_VERSION}-bin-{architecture}.zip"
            with zipfile.ZipFile(archive) as opened:
                members = {name: opened.read(name) for name in opened.namelist()}
            expected = {
                "wolfrpg.so": binary,
                "observer_user.ini": WOLFRPG_OBSERVER_INI,
                "ChangeLog.en.txt": WOLFRPG_CHANGELOG_EN,
                "ChangeLog.ru.txt": WOLFRPG_CHANGELOG_RU,
            }
            self.assertEqual(sorted(members), sorted([*expected, "LICENSES.txt"]))
            for name, content in expected.items():
                self.assertEqual(members[name], content, f"{architecture}:{name}")
            notices = members["LICENSES.txt"]
            self.assertTrue(notices.strip(), "LICENSES.txt must not be empty")
            self.assertIn(b"Fixture notice: uberwolf.txt", notices)

        with zipfile.ZipFile(packages / f"wolfrpg-{WOLFRPG_VERSION}-pdb.zip") as opened:
            symbols = {name: opened.read(name) for name in opened.namelist()}
        self.assertEqual(symbols, {"x64/wolfrpg.pdb": WOLFRPG_PDB_X64, "x86/wolfrpg.pdb": WOLFRPG_PDB_X86})


class WolfRpgProjectContract(GuardedCase):
    """The MSBuild half: the test project references a WolfRPG module project."""

    def test_link_dependency_parser_preserves_nonempty_unique_declarations_in_order(self) -> None:
        root = ET.fromstring(
            '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">'
            '<ItemDefinitionGroup><Link><AdditionalDependencies />'
            '<AdditionalDependencies>first.lib</AdditionalDependencies>'
            '<AdditionalDependencies>first.lib</AdditionalDependencies>'
            '<AdditionalDependencies>second.lib</AdditionalDependencies>'
            '</Link></ItemDefinitionGroup></Project>'
        )
        with mock.patch(__name__ + "._parse", return_value=root):
            self.assertEqual(_link_dependencies(Path("fixture.vcxproj")),
                             ["first.lib", "second.lib"])

    def test_tests_project_references_the_wolfrpg_project(self) -> None:
        root = _parse(PROJECTS / "tests.vcxproj")
        references = _project_references(root)
        includes = sorted(Path(ref.get("Include", "")).name for ref in references)
        self.assertEqual(
            includes,
            ["godot.vcxproj", "mode-selection-trial-helper.vcxproj", "renpy.vcxproj",
             "rpgmaker.vcxproj", "wolfrpg.vcxproj", "zanzarah.vcxproj"],
            "the suite must build exactly the five module projects and its process-trial helper",
        )
        wolfrpg = next(
            ref for ref in references if Path(ref.get("Include", "")).name == "wolfrpg.vcxproj"
        )
        self.assertEqual(_property(wolfrpg, "Project"), WOLFRPG_GUID)
        self.assertEqual((_property(wolfrpg, "LinkLibraryDependencies") or "").lower(), "false")
        self.assertEqual((_property(wolfrpg, "ReferenceOutputAssembly") or "").lower(), "false")

    def test_wolfrpg_project_definition_matches_module_contract(self) -> None:
        project = PROJECTS / "wolfrpg.vcxproj"
        self.assertTrue(project.is_file(), f"missing WolfRPG module project: {project}")
        root = _parse(project)
        self.assertEqual(_property(root, "unpackratConfigurationType"), "DynamicLibrary")
        self.assertEqual(_property(root, "unpackratModuleName"), WOLFRPG)
        self.assertEqual(_property(root, "TargetName"), WOLFRPG)
        self.assertEqual(_property(root, "TargetExt"), ".so")
        self.assertEqual(_property(root, "VCProjectVersion"), "17.0")
        self.assertIn(
            r"src\modules\wolfrpg\wolfrpg.def",
            (_property(root, "ModuleDefinitionFile") or "").replace("/", "\\"),
            "the WolfRPG module must export through its own .def",
        )
        imported = _imported_projects(project)
        self.assertIn(
            "unpackrat-module-version.props", imported,
            "the WolfRPG module must take its version from the shared module version props",
        )
        self.assertIn(
            "unpackrat-project.props", imported,
            "the WolfRPG module must inherit the shared project policy",
        )
        self.assertLess(
            imported.index("unpackrat-project.props"),
            imported.index("unpackrat-module-version.props"),
            "the shared project policy must import before the module version props",
        )
        units = [unit.replace("\\", "/") for unit in _compile_units(project)]
        self.assertTrue(any(unit.endswith("src/dll.cpp") for unit in units))
        self.assertTrue(any(unit.endswith("src/archive.cpp") for unit in units))
        self.assertTrue(any(unit.endswith("src/core/io/bounded_stream.cpp") for unit in units))
        self.assertTrue(
            any(unit.endswith("src/modules/wolfrpg/wolfrpg.cpp") for unit in units),
            "the WolfRPG project must compile its own module adapter translation unit",
        )
        for core_unit in (
            "src/core/wolfrpg/archive.cpp",
            "src/core/wolfrpg/lz_reader.cpp",
            "src/core/wolfrpg/huffman_reader.cpp",
        ):
            self.assertTrue(
                any(unit.endswith(core_unit) for unit in units),
                f"the WolfRPG project must compile its project-owned core unit {core_unit}",
            )
        self.assertTrue(
            any("uberwolf.lib" in value for value in _link_dependencies(project)),
            "the WolfRPG module must link the pinned UberWolf static library",
        )

    def test_module_project_set_declares_the_wolfrpg_identity(self) -> None:
        project = _parse(PROJECTS / "wolfrpg.vcxproj")
        name = project.find(".//msb:unpackratModuleName", NS)
        self.assertIsNotNone(name)
        self.assertEqual(name.text, WOLFRPG)
        def_file = PROJECTS.parent.parent / "src" / "modules" / WOLFRPG / "wolfrpg.def"
        self.assertTrue(def_file.is_file(), f"missing WolfRPG export definition: {def_file}")
        exports = {
            line.strip()
            for line in def_file.read_text(encoding="ascii").splitlines()
            if line.strip() and not line.strip().startswith(("LIBRARY", ";"))
        }
        self.assertEqual(exports, {"EXPORTS", "LoadSubModule", "UnloadSubModule"})

    def test_tests_project_links_uberwolf_for_both_configurations(self) -> None:
        # vcpkg.props disables the MSVC auto-link library dirs for the ClangCL toolset that the
        # Coverage and UBSan stages use, so the suite must name uberwolf.lib explicitly in both
        # the Debug and the Release link list; the native MSVC configs resolve it the same way.
        dependencies = _link_dependencies(PROJECTS / "tests.vcxproj")
        self.assertEqual(len(dependencies), 2, dependencies)
        debug = next((value for value in dependencies if "Catch2d.lib" in value), None)
        release = next(
            (value for value in dependencies if "Catch2.lib" in value and "Catch2d.lib" not in value),
            None,
        )
        self.assertIsNotNone(debug, "the Debug link list must be present")
        self.assertIsNotNone(release, "the Release link list must be present")
        for configuration, value in (("Debug", debug), ("Release", release)):
            with self.subTest(configuration=configuration):
                self.assertIn(
                    "uberwolf.lib", value,
                    f"the {configuration} suite must link the pinned UberWolf static library",
                )


class WolfRpgAuditContract(GuardedCase):
    """The native release audit and the analyzer set must gate every shipped module."""

    def test_release_audit_inspects_the_wolfrpg_module(self) -> None:
        calls: list[tuple[str, ...]] = []

        def fake_run(argv, cwd=None, env=None, capture=False):
            recorded = tuple(str(item) for item in argv)
            calls.append(recorded)
            if recorded[1] == "/headers":
                machine = "14C machine (x86)" if "x86" in recorded[2] else "8664 machine (x64)"
                return subprocess.CompletedProcess(list(recorded), 0, machine)
            if recorded[1] == "/dependents":
                return subprocess.CompletedProcess(list(recorded), 0, "    KERNEL32.dll")
            exports = "   1    0 00001000 LoadSubModule\n   2    0 00001010 UnloadSubModule"
            return subprocess.CompletedProcess(list(recorded), 0, exports)

        with mock.patch.object(main, "run", fake_run), mock.patch.object(
            main, "_find_dumpbin", return_value=self.repo / "dumpbin.exe"
        ):
            for architecture in ("x64", "x86"):
                main._audit(self.repo, architecture)

        inspected = {
            (architecture, Path(recorded[2]).name)
            for recorded in calls
            if len(recorded) > 2 and recorded[1] == "/headers"
            for architecture in ("x64", "x86")
            if architecture in Path(recorded[2]).parts
        }
        self.assertTrue(
            {("x64", "wolfrpg.so"), ("x86", "wolfrpg.so")} <= inspected,
            f"the release audit must gate both WolfRPG binaries; inspected: {sorted(inspected)}",
        )

    def test_standalone_analyzer_set_includes_the_wolfrpg_project(self) -> None:
        x64 = [path.relative_to(self.repo).as_posix() for path in main._analysis_projects(self.repo, "x64")]
        self.assertIn("build/projects/wolfrpg.vcxproj", x64)
        x86 = [path.relative_to(self.repo).as_posix() for path in main._analysis_projects(self.repo, "x86")]
        self.assertIn("build/projects/wolfrpg.vcxproj", x86)


class WolfRpgWorkflowContract(GuardedCase):
    """The tag-gated release pipeline must publish the WolfRPG lineage like every module."""

    def test_push_tags_and_release_condition_cover_the_wolfrpg_lineage(self) -> None:
        workflow = (REPOSITORY_ROOT / ".github" / "workflows" / "main.yml").read_text(encoding="utf-8")
        self.assertIn(
            "tags: ['renpy/v*', 'rpgmaker/v*', 'zanzarah/v*', 'godot/v*', 'wolfrpg/v*']",
            workflow,
            "the push tag filter must accept the wolfrpg release lineage",
        )
        self.assertIn(
            "startsWith(github.ref, 'refs/tags/wolfrpg/v')",
            workflow,
            "the release job must publish the wolfrpg tag lineage",
        )


class WolfRpgFuzzContract(GuardedCase):
    """WolfRPG is a bounded libFuzzer target with its own project and seed corpus."""

    SEED_HEX = "776f6c66726770"  # b"wolfrgp"

    def _seed_every_target(self) -> Path:
        for target in main.FUZZ_TARGETS:
            directory = self.repo / "src" / "fuzz" / "corpus" / target
            directory.mkdir(parents=True)
            (directory / "seed.hex").write_text("414243", encoding="ascii")
        wolfrpg = self.repo / "src" / "fuzz" / "corpus" / WOLFRPG
        (wolfrpg / "minimal-header.hex").write_text(self.SEED_HEX, encoding="ascii")
        return wolfrpg

    def test_wolfrpg_is_a_registered_bounded_fuzz_target(self) -> None:
        self.assertEqual(
            main.FUZZ_TARGETS,
            ("pickle", "renpy", "rpgmaker", "zanzarah", WOLFRPG),
            "wolfrpg must join the four existing fuzz targets exactly once, last",
        )
        self.assertEqual(set(main.FUZZ_MAX_LENGTH), set(main.FUZZ_TARGETS))
        self.assertEqual(main.FUZZ_MAX_LENGTH[WOLFRPG], WOLFRPG_FUZZ_MAX_LENGTH)
        self.assertGreater(main.FUZZ_MAX_LENGTH[WOLFRPG], 0)

    def test_fuzz_wolfrpg_project_matches_the_module_contract(self) -> None:
        project = PROJECTS / "fuzz-wolfrpg.vcxproj"
        self.assertTrue(project.is_file(), f"missing WolfRPG fuzz project: {project}")
        root = _parse(project)
        self.assertEqual(_property(root, "unpackratConfigurationType"), "Application")
        self.assertEqual(_property(root, "unpackratModuleName"), WOLFRPG)
        self.assertEqual(_property(root, "TargetName"), "fuzz-wolfrpg")
        self.assertEqual(_property(root, "VCProjectVersion"), "17.0")
        imported = _imported_projects(project)
        self.assertIn("unpackrat-module-version.props", imported)
        self.assertIn("unpackrat-fuzz.props", imported)
        self.assertIn("unpackrat-project.props", imported)
        self.assertLess(
            imported.index("unpackrat-project.props"),
            imported.index("unpackrat-module-version.props"),
            "the shared project policy must import before the module version props",
        )
        units = [unit.replace("\\", "/") for unit in _compile_units(project)]
        for expected in (
            "src/fuzz/wolfrpg.cpp",
            "src/core/wolfrpg/archive.cpp",
            "src/modules/wolfrpg/wolfrpg.cpp",
            "src/core/io/bounded_stream.cpp",
        ):
            self.assertTrue(
                any(unit.endswith(expected) for unit in units),
                f"the WolfRPG fuzz project must compile {expected}",
            )
        self.assertTrue(
            any("uberwolf.lib" in value for value in _link_dependencies(project)),
            "the WolfRPG fuzz target must link the pinned UberWolf static library",
        )

    def test_fuzz_wolfrpg_build_run_and_seed_selection_are_recorded(self) -> None:
        self._seed_every_target()
        run = RecordingRun()
        llvm = main.Llvm(
            directory=self.root / "llvm",
            runtime=self.root / "llvm" / "lib" / "clang" / "19" / "lib" / "windows",
            cov=self.root / "llvm" / "bin" / "llvm-cov.exe",
            profdata=self.root / "llvm" / "bin" / "llvm-profdata.exe",
        )
        llvm.runtime.mkdir(parents=True, exist_ok=True)
        for runtime_name in main._ASAN_RUNTIMES.values():
            (llvm.runtime / runtime_name).write_bytes(b"matched-llvm21")
        with self.tools(run), mock.patch.object(main, "_restore"), mock.patch.object(
            main, "_vs_installation", return_value=self.vs_install
        ):
            main._fuzz(self.repo, "x64", self.msbuild_exe, self.vcpkg_exe, llvm)

        builds = [
            call for call in run.by_tool("msbuild.exe")
            if any("fuzz-wolfrpg.vcxproj" in item for item in call.argv)
        ]
        self.assertEqual(len(builds), 1, "the fuzz stage must build fuzz-wolfrpg once per architecture")
        self.assertIn("/p:Configuration=Fuzz", builds[0].argv)
        self.assertIn("/p:Platform=x64", builds[0].argv)

        work = main.artifacts_root(self.repo) / "fuzz" / "x64" / WOLFRPG
        seeded = work / "seeds"
        self.assertEqual(
            (seeded / "minimal-header").read_bytes(),
            bytes.fromhex(self.SEED_HEX),
            "the codeword seed must be decoded from its hex source before the replay",
        )
        executable = next(
            call for call in run.calls
            if call.argv and Path(call.argv[0]).name == "fuzz-wolfrpg.exe"
        )
        self.assertIn(str(seeded), executable.argv)
        self.assertIn(f"-max_len={WOLFRPG_FUZZ_MAX_LENGTH}", executable.argv)
        self.assertTrue(any("-max_total_time=" in item for item in executable.argv))


class WolfRpgDocsContract(GuardedCase):
    """The docs publish WolfRPG downloads and notes; packaged notices carry attribution."""

    def test_readme_publishes_wolfrpg_rows_and_notes(self) -> None:
        readme = (REPOSITORY_ROOT / "README.md").read_text(encoding="utf-8")
        self.assertIn("| WolfRPG | `.wolf`, `.wolfx` | [wolfrpg](", readme)
        self.assertIn("src/modules/wolfrpg/ChangeLog.en.txt", readme)
        self.assertIn("src/modules/wolfrpg/ChangeLog.ru.txt", readme)
        self.assertIn("https://github.com/refaim/unpackrat/releases?q=wolfrpg", readme)
        version = (REPOSITORY_ROOT / "src/modules/wolfrpg/VERSION").read_text(encoding="utf-8").strip()
        self.assertEqual(version, WOLFRPG_VERSION)
        self.assertNotIn("<<<<<<<", readme)
        build_system = (REPOSITORY_ROOT / "docs" / "build-system.md").read_text(encoding="utf-8")
        self.assertIn(WOLFRPG, build_system.lower())

    def test_wolfrpg_current_release_keeps_the_reviewed_patch(self) -> None:
        self.assertEqual(main.read_version(REPOSITORY_ROOT, WOLFRPG), WOLFRPG_VERSION)

    def test_readme_defers_uberwolf_attribution_to_packaged_notices(self) -> None:
        readme = (REPOSITORY_ROOT / "README.md").read_text(encoding="utf-8")
        self.assertNotIn("UberWolf", readme)
        self.assertIn("generated `LICENSES.txt` at the archive root.", readme)

    def test_uberwolf_notice_captures_the_bundled_components(self) -> None:
        notice = (REPOSITORY_ROOT / "licenses" / "uberwolf.txt").read_text(encoding="utf-8")
        self.assertIn("https://github.com/Sinflower/UberWolf", notice)
        self.assertIn("DXLib", notice)
        self.assertIn("LZ4", notice)


if __name__ == "__main__":
    unittest.main()
