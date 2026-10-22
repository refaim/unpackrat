"""Tests-first contract for the WolfRPG units wired into ``build/projects/tests.vcxproj``.

The native test project must compile every repository-owned WolfRPG/UberWolf unit
translation unit that exists on disk, must not reference a WolfRPG test source that is
missing, and must give every WolfRPG test translation unit a distinct effective object
output name so no two objects collide inside one ``IntDir``.

The tests below only parse the project XML and the working tree; no build or tool is
launched.  ``ObjectFileName`` is read straight from the element, and the MSBuild default
``$(IntDir)<basename>.obj`` is reproduced when an entry carries no override, so the
fixture-pair override is required behaviour rather than cosmetic.
"""

from __future__ import annotations

from pathlib import Path
import unittest
import xml.etree.ElementTree as ET


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
PROJECT = REPOSITORY_ROOT / "build" / "projects" / "tests.vcxproj"
NS = {"msb": "http://schemas.microsoft.com/developer/msbuild/2003"}
REPOSITORY_ROOT_TOKEN = "$(RepositoryRoot)"

# The unit translation units this change wires into the native test project.
REQUIRED_WOLFRPG_UNITS = (
    "src/tests/unit/uberwolf_classic_context.cpp",
    "src/tests/unit/uberwolf_file_access.cpp",
    "src/tests/unit/uberwolf_mode_selection.cpp",
    "src/tests/unit/uberwolf_modern_archive_api.cpp",
    "src/tests/unit/uberwolf_modes.cpp",
    "src/tests/unit/uberwolf_shared_crypto.cpp",
    "src/tests/unit/uberwolf_wolfx_file_size.cpp",
    "src/tests/unit/wolfrpg_adapter.cpp",
    "src/tests/unit/wolfrpg_archive_source.cpp",
    "src/tests/unit/wolfrpg_bounded_lz.cpp",
    "src/tests/unit/wolfrpg_chacha_crypto.cpp",
    "src/tests/unit/wolfrpg_crypto.cpp",
    "src/tests/unit/wolfrpg_default_modes.cpp",
    "src/tests/unit/wolfrpg_fixtures.cpp",
    "src/tests/unit/wolfrpg_fuzz_sink.cpp",
    "src/tests/unit/wolfrpg_game_context_boundary.cpp",
    "src/tests/unit/wolfrpg_huffman.cpp",
    "src/tests/unit/wolfrpg_lz.cpp",
    "src/tests/unit/wolfrpg_modern_archives.cpp",
    "src/tests/unit/wolfrpg_modern_crypto.cpp",
    "src/tests/unit/wolfrpg_module.cpp",
    "src/tests/unit/wolfrpg_pro_keys.cpp",
    "src/tests/unit/wolfrpg_pro_vendor_api.cpp",
    "src/tests/unit/wolfrpg_pro_wolfpro.cpp",
    "src/tests/unit/wolfrpg_recovery_contract.cpp",
    "src/tests/unit/wolfrpg_recovery_collector.cpp",
    "src/tests/unit/wolfrpg_recovery_archive_contract.cpp",
    "src/tests/unit/wolfrpg_recovery_failure_contract.cpp",
    "src/tests/unit/wolfrpg_recovery_legacy.cpp",
    "src/tests/unit/wolfrpg_recovery_metadata_io.cpp",
    "src/tests/unit/wolfrpg_recovery_vendor_contract.cpp",
    "src/tests/unit/wolfrpg_text_encoding.cpp",
    "src/tests/unit/wolfrpg_wolfx.cpp",
)

# The unit fixture translation unit must not share the support fixture's default name.
WOLFRPG_FIXTURE_UNIT = "src/tests/unit/wolfrpg_fixtures.cpp"
WOLFRPG_FIXTURE_UNIT_OBJECT = "$(IntDir)wolfrpg_fixtures_unit.obj"


def _repository_path(include: str) -> str:
    """Return the repository-relative POSIX path denoted by an ``Include`` attribute."""

    relative = include[len(REPOSITORY_ROOT_TOKEN):] if include.startswith(REPOSITORY_ROOT_TOKEN) else include
    return relative.replace("\\", "/")


def _compiled_units() -> dict[str, ET.Element]:
    """Map every repository-relative ``ClCompile`` path to its project element."""

    root = ET.parse(PROJECT).getroot()
    return {
        _repository_path(element.get("Include", "")): element
        for element in root.findall(".//msb:ClCompile", NS)
    }


def _declared_items() -> list[str]:
    """Return every repository-relative ``ClCompile`` and ``ClInclude`` path."""

    root = ET.parse(PROJECT).getroot()
    return [
        _repository_path(element.get("Include", ""))
        for tag in ("ClCompile", "ClInclude")
        for element in root.findall(f".//msb:{tag}", NS)
    ]


def _effective_object_name(path: str, element: ET.Element) -> str:
    """Return the resolved object name, reproducing the MSBuild default when absent."""

    override = element.find("msb:ObjectFileName", NS)
    if override is not None and override.text:
        return override.text.strip()
    return f"$(IntDir){Path(path).stem}.obj"


class WolfRpgProjectSourcesTests(unittest.TestCase):
    """The native suite must compile the repository-owned WolfRPG test units."""

    def test_required_wolfrpg_unit_translation_units_are_compiled(self) -> None:
        compiled = _compiled_units()
        missing = [unit for unit in REQUIRED_WOLFRPG_UNITS if unit not in compiled]
        self.assertEqual(missing, [], f"native test project omits WolfRPG units: {missing}")

    def test_no_referenced_wolfrpg_test_source_is_missing(self) -> None:
        missing = [
            path
            for path in _declared_items()
            if path.startswith("src/tests/") and not (REPOSITORY_ROOT / path).is_file()
        ]
        self.assertEqual(missing, [], f"native test project references missing test sources: {missing}")

    def test_wolfrpg_test_units_have_distinct_object_names(self) -> None:
        names: dict[str, list[str]] = {}
        for path, element in _compiled_units().items():
            if not path.startswith("src/tests/"):
                continue
            if "wolf" not in Path(path).name.lower():
                continue
            names.setdefault(_effective_object_name(path, element), []).append(path)
        collisions = {name: units for name, units in names.items() if len(units) > 1}
        self.assertEqual(collisions, {}, f"WolfRPG test units share an object name: {collisions}")

    def test_unit_fixture_translation_unit_overrides_its_object_name(self) -> None:
        element = _compiled_units()[WOLFRPG_FIXTURE_UNIT]
        self.assertEqual(
            _effective_object_name(WOLFRPG_FIXTURE_UNIT, element),
            WOLFRPG_FIXTURE_UNIT_OBJECT,
        )


    def test_all_wolf_core_translation_units_are_compiled(self) -> None:
        compiled = _compiled_units()
        required = tuple(path.relative_to(REPOSITORY_ROOT).as_posix()
                         for path in sorted((REPOSITORY_ROOT / "src/core/wolfrpg").glob("*.cpp")))
        self.assertTrue(required)
        self.assertEqual([path for path in required if path not in compiled], [])
        self.assertIn("src/platform/win32/system_codepage.cpp", compiled)

    def test_wolf_adapter_has_the_vendor_source_header_include_root(self) -> None:
        root = ET.parse(PROJECT).getroot()
        paths = root.findall(".//msb:ItemDefinitionGroup/msb:ClCompile/msb:AdditionalIncludeDirectories", NS)
        self.assertTrue(any("include/uberwolf" in (node.text or "").replace("\\", "/") for node in paths))

    def test_mode_selection_helper_is_built_without_linking_its_output(self) -> None:
        root = ET.parse(PROJECT).getroot()
        references = root.findall("msb:ItemGroup/msb:ProjectReference", NS)
        helper = [item for item in references
                  if item.get("Include") == "mode-selection-trial-helper.vcxproj"]
        self.assertEqual(len(helper), 1, "the native suite must build its process-trial helper")
        self.assertEqual(helper[0].findtext("msb:Project", namespaces=NS),
                         "{0A1E9D6F-DB16-421C-B228-151A47D54B32}")
        self.assertEqual(helper[0].findtext("msb:LinkLibraryDependencies", namespaces=NS), "false")
        self.assertEqual(helper[0].findtext("msb:ReferenceOutputAssembly", namespaces=NS), "false")

    def test_mode_selection_helper_shares_the_native_suite_output_directory(self) -> None:
        helper = ET.parse(PROJECT.with_name("mode-selection-trial-helper.vcxproj")).getroot()
        suite = ET.parse(PROJECT).getroot()
        shared_props = "../unpackrat-project.props"
        for project in (helper, suite):
            imports = [item.get("Project", "").replace("\\", "/")
                       for item in project.findall("msb:Import", NS)]
            self.assertIn(shared_props, imports)
            self.assertIsNone(project.find(".//msb:OutDir", NS))
        props = ET.parse(PROJECT.parents[1] / "unpackrat-project.props").getroot()
        self.assertEqual(props.findtext(".//msb:OutDir", namespaces=NS),
                         "$(ArtifactsRoot)bin\\$(PlatformMoniker)\\$(Configuration)\\")


if __name__ == "__main__":
    unittest.main()
