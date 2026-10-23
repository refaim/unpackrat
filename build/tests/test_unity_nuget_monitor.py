"""The lazy Unity reader's actual package pin is monitored under manual approval."""

from __future__ import annotations

import json
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET


REPOSITORY = Path(__file__).resolve().parents[2]
PROJECT = "src/modules/unity/UnityProbe/UnityProbe.csproj"
PACKAGE = "AssetsTools.NET"
MIT_NOTICE = """MIT License

Copyright (c) 2020 nesrak1

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE."""


def expression(pattern: str) -> re.Pattern[str]:
    """Execute the actual manager's shared ECMAScript/RE2 syntax in Python."""
    return re.compile(re.sub(r"\(\?<([A-Za-z][A-Za-z0-9_]*)>", r"(?P<\1>", pattern))


class UnityNugetMonitorTests(unittest.TestCase):
    def setUp(self) -> None:
        self.configuration = json.loads(
            (REPOSITORY / ".github/renovate.json").read_text(encoding="utf-8"))

    def manager(self) -> dict:
        selected = [item for item in self.configuration["customManagers"]
                    if item.get("depNameTemplate") == PACKAGE]
        self.assertEqual(len(selected), 1, "the actual NuGet pin needs one watcher")
        return selected[0]

    def matches(self, contents: str) -> list[dict[str, str]]:
        return [match.groupdict() for pattern in self.manager()["matchStrings"]
                for match in expression(pattern).finditer(contents)]

    def test_uses_nuget_identity_and_versioning(self) -> None:
        manager = self.manager()
        self.assertEqual(manager["customType"], "regex")
        self.assertEqual(manager["packageNameTemplate"], PACKAGE)
        self.assertEqual(manager["datasourceTemplate"], "nuget")
        self.assertEqual(manager["versioningTemplate"], "nuget")

    def test_scans_only_the_production_unity_project(self) -> None:
        file_pattern, = self.manager()["managerFilePatterns"]
        matcher = expression(file_pattern[1:-1])
        self.assertIsNotNone(matcher.search(PROJECT))
        for unrelated in (
            "vcpkg.json", "src/modules/unity/upstream.json",
            PROJECT + ".bak", "copy/" + PROJECT,
            "src/modules/unity/UnityProbe.Tests/UnityProbe.Tests.csproj",
            PROJECT.replace("UnityProbe.csproj", "UnityProbeXcsproj"),
        ):
            with self.subTest(path=unrelated):
                self.assertIsNone(matcher.search(unrelated))

    def test_extracts_current_value_and_tracks_an_approved_version_change(self) -> None:
        for contents, version in (
            ('<PackageReference Include="AssetsTools.NET" Version="3.0.5" />', "3.0.5"),
            ('<PackageReference PrivateAssets="none"\n Include = "AssetsTools.NET"\n Version = "9.12.34" />', "9.12.34"),
        ):
            with self.subTest(contents=contents):
                self.assertEqual(self.matches(contents), [{"currentValue": version}])

    def test_does_not_extract_other_packages_ranges_or_adjacent_references(self) -> None:
        for contents in (
            '<PackageReference Include="AssetsTools.NET.Texture" Version="3.0.5" />',
            '<PackageReference Include="Other" Version="3.0.5" />',
            '<PackageReference Include="AssetsTools.NET" /><PackageReference Include="Other" Version="3.0.5" />',
            '<PackageReference Include="AssetsTools.NET" VersionOverride="3.0.5" />',
            '<PackageReference Include="AssetsTools.NET" Version="[3.0.5,4.0.0)" />',
            '<PackageReference Include="AssetsTools.NET" Version="3.*" />',
        ):
            with self.subTest(contents=contents):
                self.assertEqual(self.matches(contents), [])

    def test_extracts_the_actual_package_reference(self) -> None:
        path = REPOSITORY / PROJECT
        reference, = [element for element in ET.parse(path).getroot().iter("PackageReference")
                      if element.get("Include") == PACKAGE]
        version = reference.attrib["Version"]
        self.assertRegex(version, r"^\d+\.\d+\.\d+$")
        self.assertEqual(self.matches(path.read_text(encoding="utf-8")),
                         [{"currentValue": version}])

    def test_inherits_manual_dashboard_draft_and_no_automerge_policy(self) -> None:
        self.assertEqual(self.configuration["enabledManagers"], ["custom.regex"])
        self.assertTrue(self.configuration["dependencyDashboardApproval"])
        self.assertTrue(self.configuration["dependencyDashboard"])
        self.assertTrue(self.configuration["draftPR"])
        self.assertFalse(self.configuration["automerge"])
        for key in ("dependencyDashboardApproval", "draftPR", "automerge",
                    "postUpgradeTasks", "lockFileMaintenance"):
            self.assertNotIn(key, self.manager())

    def test_shipped_notice_reproduces_upstream_mit_and_attribution(self) -> None:
        notice = (REPOSITORY / "licenses/Unity.txt").read_text(encoding="utf-8")
        for attribution in (
            "AssetsTools.NET 3.0.5",
            "https://www.nuget.org/packages/AssetsTools.NET/3.0.5",
            "https://github.com/nesrak1/AssetsTools.NET",
            "GPL-3.0 combined work", "StbImageSharp 2.30.15",
        ):
            with self.subTest(attribution=attribution):
                self.assertTrue(attribution in notice, "missing attribution: " + attribution)
        self.assertTrue(MIT_NOTICE in notice, "the complete upstream MIT notice must ship")


if __name__ == "__main__":
    unittest.main()
