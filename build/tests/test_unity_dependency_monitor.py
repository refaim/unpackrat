"""AssetRipper release monitoring stays a notification under the existing manual-approval policy."""

from __future__ import annotations

import json
from pathlib import Path
import re
import subprocess
import unittest


REPOSITORY = Path(__file__).resolve().parents[2]

# The workspace mirror keeps the registration as a flat ``renovate.json``; the
# integrated repository keeps it at ``.github/renovate.json``.
CONFIGURATION_CANDIDATES = (
    REPOSITORY / ".github/renovate.json",
    REPOSITORY / "renovate.json",
)

METADATA = REPOSITORY / "src/modules/unity/upstream.json"

# The upstream vendor delta is stored as a plain patch and applied with ``git apply``,
# which cannot parse empty context lines after a CRLF conversion.
OWNERSHIP_PATCH = REPOSITORY / "src/modules/unity/patches/assetripper-stream-ownership.patch"

RELEASE_WATCH_DEP_NAME = "assetripper"
PACKAGE_NAME = "AssetRipper/AssetRipper"

# The repository identity is a fixed contract. The live source pin and the
# notification baseline are read from the metadata at test time so that an
# approved metadata-only change (a new published release) stays valid; only the
# synthetic fixtures below embed literal versions.

# Managers that predate the AssetRipper registration and must remain untouched.
EXISTING_DEP_NAMES = {
    "yy-thunks", "catch2", "nlohmann-json", "xxhash", "zlib", "libspng",
    "libogg", "brotli", "zstd", "fastlz", "zydis", "mbedtls", "bcdec",
    "drlibs", "qoa",
}


def expression(pattern: str) -> re.Pattern[str]:
    """Run the shared ECMAScript/RE2 subset with Python named-group syntax."""
    return re.compile(re.sub(r"\(\?<([A-Za-z][A-Za-z0-9_]*)>", r"(?P<\1>", pattern))


def configuration_path() -> Path:
    for candidate in CONFIGURATION_CANDIDATES:
        if candidate.is_file():
            return candidate
    raise AssertionError(f"no Renovate configuration found in {REPOSITORY}")


class UnityDependencyMonitorTests(unittest.TestCase):
    def setUp(self) -> None:
        self.configuration = json.loads(configuration_path().read_text(encoding="utf-8"))
        self.managers = self.configuration["customManagers"]

    def assetripper_managers(self) -> list[dict[str, object]]:
        return [manager for manager in self.managers
                if manager.get("depNameTemplate") == RELEASE_WATCH_DEP_NAME]

    def matches(self, manager: dict[str, object], contents: str) -> list[dict[str, str]]:
        return [match.groupdict() for pattern in manager["matchStrings"]
                for match in expression(pattern).finditer(contents)]

    def test_registers_exactly_one_assetripper_release_manager(self) -> None:
        managers = self.assetripper_managers()
        self.assertEqual(len(managers), 1, "exactly one AssetRipper manager is registered")
        manager = managers[0]
        self.assertEqual(manager["packageNameTemplate"], PACKAGE_NAME)
        self.assertEqual(manager["datasourceTemplate"], "github-releases")
        self.assertEqual(manager["versioningTemplate"], "semver")

    def test_manager_only_scans_the_exact_metadata_file(self) -> None:
        manager = self.assetripper_managers()[0]
        file_pattern, = manager["managerFilePatterns"]
        pattern = expression(file_pattern[1:-1])
        self.assertIsNotNone(pattern.search("src/modules/unity/upstream.json"))
        for unrelated in (
            "src/modules/unity/Directory.Build.props",
            "src/modules/unity/UnityProbe/Manifest.cs",
            "vcpkg.json",
            "src/modules/unity/upstream.json.bak",
        ):
            self.assertIsNone(pattern.search(unrelated), unrelated)

    def test_matches_the_release_watch_baseline_in_the_metadata(self) -> None:
        manager = self.assetripper_managers()[0]
        metadata = json.loads(METADATA.read_text(encoding="utf-8"))
        baseline = metadata["releaseWatchBaseline"]
        # The notification baseline is a stable SemVer and is expected to move over time.
        self.assertIsInstance(baseline, str)
        self.assertIsNotNone(re.fullmatch(r"\d+\.\d+\.\d+", baseline), baseline)
        matches = self.matches(manager, METADATA.read_text(encoding="utf-8"))
        self.assertEqual(matches, [{"currentValue": baseline}])
        # currentValue must be a required named group, not a positional capture.
        self.assertTrue(all("currentValue" in expression(pattern).groupindex
                            for pattern in manager["matchStrings"]))
        # Synthetic fixtures: any approved baseline is extracted, no fixed version is required.
        self.assertEqual(self.matches(manager, json.dumps({"releaseWatchBaseline": "9.9.9"})),
                         [{"currentValue": "9.9.9"}])
        self.assertEqual(self.matches(manager, json.dumps({"sourceRevision": "f" * 40})), [])
        self.assertEqual(
            self.matches(manager, json.dumps({"sourceRevision": metadata["sourceRevision"]})), [])

    def test_metadata_is_valid_json_with_an_honest_source_pin(self) -> None:
        metadata = json.loads(METADATA.read_text(encoding="utf-8"))
        self.assertEqual(metadata["sourceRepository"], PACKAGE_NAME)
        self.assertRegex(metadata["sourceRevision"], r"^[a-f0-9]{40}$")
        self.assertRegex(metadata["releaseWatchBaseline"], r"^\d+\.\d+\.\d+$")
        # The source pin and the notification baseline are distinct concepts and never equal.
        self.assertNotEqual(metadata["sourceRevision"], metadata["releaseWatchBaseline"])
        # No automatic acquisition or upgrade keys may creep into the metadata.
        self.assertEqual(set(metadata),
                         {"sourceRepository", "sourceRevision", "releaseWatchBaseline"})

    def test_existing_manual_approval_policy_is_preserved(self) -> None:
        configuration = self.configuration
        self.assertEqual(configuration["enabledManagers"], ["custom.regex"])
        self.assertTrue(configuration["dependencyDashboard"])
        self.assertTrue(configuration["dependencyDashboardApproval"])
        self.assertFalse(configuration["automerge"])
        self.assertTrue(configuration["draftPR"])
        self.assertNotIn("extends", configuration)
        self.assertNotIn("postUpgradeTasks", configuration)
        self.assertEqual(configuration.get("packageRules"), [{
            "description": "Keep Mbed TLS on the supported 4.1 LTS series.",
            "matchPackageNames": ["Mbed-TLS/mbedtls"],
            "allowedVersions": ">=4.1.0 <4.2.0",
        }])
        names = [manager["depNameTemplate"] for manager in self.managers]
        self.assertEqual(names.count(RELEASE_WATCH_DEP_NAME), 1)
        self.assertTrue(EXISTING_DEP_NAMES.issubset(set(names)))
        self.assertEqual(len(names), len(set(names)), "dep names stay unique")

    def test_registration_adds_no_automatic_upgrade_behaviour(self) -> None:
        manager = self.assetripper_managers()[0]
        for forbidden in ("automerge", "automergeType", "postUpgradeTasks",
                          "updateLockFiles", "lockFileMaintenance"):
            self.assertNotIn(forbidden, manager)


class OwnershipPatchCheckoutTests(unittest.TestCase):
    """The vendor ownership patch must survive a checkout with its LF bytes intact."""

    def test_ownership_patch_is_checked_out_with_lf(self) -> None:
        relative = OWNERSHIP_PATCH.relative_to(REPOSITORY).as_posix()
        result = subprocess.run(
            ["git", "check-attr", "eol", "--", relative],
            cwd=REPOSITORY,
            capture_output=True,
            text=True,
            check=True,
        )
        self.assertEqual(result.stdout.strip(), f"{relative}: eol: lf",
                         "the .gitattributes checkout rule must keep this patch at LF")
        self.assertNotIn(b"\r\n", OWNERSHIP_PATCH.read_bytes())


if __name__ == "__main__":
    unittest.main()
