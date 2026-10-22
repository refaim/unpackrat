"""Reviewed registration for the two WolfRPG first-party test sources.

The two test files are MIT build tooling.  Each must be isolated as its own
content-addressed component, bound to the same reviewed curation row in the
package configuration, and the existing ``build/main.py`` review must cover the
new WolfRPG notice line.  License values are read from the reviewed table so
this test source does not restate them.
"""

from __future__ import annotations

import json
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
INVENTORY = REPOSITORY_ROOT / "build" / "license-inventory.json"
FIXTURE = (
    REPOSITORY_ROOT
    / "build"
    / "tests"
    / "fixtures"
    / "license_package_configurations"
    / "firstparty_artifacts.json"
)
CONFIG_DIR = REPOSITORY_ROOT / ".ort" / "package-configurations"
LOOPBACK_PREFIX = "http://127.0.0.1:8765/sources/"
OWN_NOTICE = "LICENSE.txt"
MAIN_COMPONENT = "unpackrat-build-main"
MAIN_MEMBER = "build/main.py"
MAIN_REVIEW_ANCHORS = {78, 81, 83, 84, 86}
MAIN_WOLFRPG_LINE = 90
CURATION_KEYS = (
    "path",
    "start_lines",
    "line_count",
    "detected_license",
    "reason",
    "concluded_license",
)
WOLFRPG_COMPONENTS = {
    "unpackrat-build-tests-wolfrpg-registration": (
        "build/tests/test_wolfrpg_registration.py"
    ),
    "unpackrat-build-tests-wolfrpg-license-registration": (
        "build/tests/test_wolfrpg_license_registration.py"
    ),
}


def _load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _start_lines(value) -> set[int]:
    if isinstance(value, int):
        return {value}
    return {int(part) for part in str(value).split(",") if part.strip()}


class WolfRpgInventoryRegistration(unittest.TestCase):
    """Each WolfRPG test source owns one reviewed adopted-source component."""

    def setUp(self) -> None:
        self.components = _load(INVENTORY)["components"]

    def test_each_wolfrpg_test_source_is_its_own_adopted_build_component(self) -> None:
        for name, member in WOLFRPG_COMPONENTS.items():
            with self.subTest(component=name):
                matches = [
                    component
                    for component in self.components
                    if isinstance(component, dict) and component.get("name") == name
                ]
                self.assertEqual(len(matches), 1, f"{name} is missing or duplicated")
                component = matches[0]
                self.assertEqual(component["kind"], "adopted-source")
                self.assertEqual(component["scope"], "build")
                self.assertEqual(component["source"]["origin"], "repository")
                self.assertIs(component["incorporated"], True)
                self.assertIs(component["packaged"], False)
                self.assertEqual(component["declaredExpression"], "MIT")
                self.assertEqual(component["notices"], [OWN_NOTICE])
                self.assertEqual(component["appliesTo"], [])
                self.assertEqual(component["members"], [member])
                self.assertTrue(component["notes"].strip())

                owners = sorted(
                    other["name"]
                    for other in self.components
                    if isinstance(other, dict)
                    and member in other.get("members", [])
                )
                self.assertEqual(owners, [name])


class WolfRpgPackageConfigurationRegistration(unittest.TestCase):
    """The reviewed fixture rows and their package configurations are identical."""

    def setUp(self) -> None:
        self.rows = _load(FIXTURE)["components"]

    def test_each_wolfrpg_member_has_one_fixture_row_and_matching_configuration(self) -> None:
        for name, member in WOLFRPG_COMPONENTS.items():
            with self.subTest(component=name):
                rows = [row for row in self.rows if row.get("name") == name]
                self.assertEqual(len(rows), 1, f"{name} is missing or duplicated")
                row = rows[0]
                self.assertEqual(row["member"], member)
                self.assertEqual(row["version"], row["sha256"])
                self.assertEqual(row["artifact_filename"], row["sha256"] + ".tar")
                self.assertEqual(row["extension"], ".tar")
                self.assertTrue(row["curations"])

                config_path = CONFIG_DIR / f"{name}.yml"
                self.assertTrue(
                    config_path.is_file(), f"{name} package configuration is missing"
                )
                config = _load(config_path)
                parts = config["id"].split(":")
                self.assertEqual(len(parts), 4)
                self.assertEqual(parts[1], "vcpkg")
                self.assertEqual(parts[2], name)
                self.assertEqual(parts[3], row["sha256"])
                self.assertEqual(
                    config["source_artifact_url"],
                    LOOPBACK_PREFIX + row["sha256"] + ".tar",
                )

                for curation in row["curations"]:
                    for key in CURATION_KEYS:
                        self.assertIn(key, curation)
                    self.assertEqual(curation["path"], member)
                for curation in config["license_finding_curations"]:
                    for key in CURATION_KEYS:
                        self.assertIn(key, curation)
                    self.assertEqual(curation["path"], member)

                expected = [
                    {key: curation[key] for key in CURATION_KEYS}
                    for curation in row["curations"]
                ]
                actual = [
                    {key: curation[key] for key in CURATION_KEYS}
                    for curation in config["license_finding_curations"]
                ]
                self.assertEqual(expected, actual)


class WolfRpgMainCurationRegistration(unittest.TestCase):
    """The reviewed main.py row covers the new WolfRPG notice line."""

    def setUp(self) -> None:
        self.rows = _load(FIXTURE)["components"]
        self.config = _load(CONFIG_DIR / f"{MAIN_COMPONENT}.yml")

    def _reviewed_rows(self, curations: list[dict]) -> list[dict]:
        return [
            curation
            for curation in curations
            if curation.get("path") == MAIN_MEMBER
            and MAIN_REVIEW_ANCHORS <= _start_lines(curation["start_lines"])
        ]

    def test_fixture_and_configuration_cover_the_wolfrpg_notice_line(self) -> None:
        rows = [row for row in self.rows if row.get("name") == MAIN_COMPONENT]
        self.assertEqual(len(rows), 1, f"{MAIN_COMPONENT} is missing or duplicated")
        fixture_curations = rows[0]["curations"]
        config_curations = self.config["license_finding_curations"]

        for source, curations in (
            ("fixture", fixture_curations),
            ("configuration", config_curations),
        ):
            with self.subTest(source=source):
                reviewed = self._reviewed_rows(curations)
                self.assertEqual(
                    len(reviewed),
                    1,
                    f"the reviewed {MAIN_MEMBER} row is missing or duplicated",
                )
                self.assertIn(
                    MAIN_WOLFRPG_LINE,
                    _start_lines(reviewed[0]["start_lines"]),
                    f"the reviewed row must cover {MAIN_MEMBER}:{MAIN_WOLFRPG_LINE}",
                )


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
