"""Derived test expectations are distinct from preserved scanner observations."""
from __future__ import annotations

from copy import deepcopy
import hashlib
import json
from pathlib import Path
import unittest

from native_dependency_expectations import derive_native_findings, expectation_path_for_raw_record
from test_license_package_configurations import curation_applies

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = Path(__file__).parent / "fixtures/license_package_configurations"


class NativeDependencyExpectations(unittest.TestCase):
    def setUp(self) -> None:
        self.raw_bytes = (FIXTURES / "native_scan_findings.json").read_bytes()
        self.raw = json.loads(self.raw_bytes)
        self.updates = json.loads((FIXTURES / "native_dependency_expectations.json").read_text(encoding="utf-8"))

    def test_exact_three_scoped_records_preserve_all_nonidentity_fields(self) -> None:
        before = deepcopy(self.raw)
        result = derive_native_findings(self.raw, self.updates)
        self.assertEqual(self.raw, before)
        self.assertEqual(hashlib.sha256(self.raw_bytes).hexdigest(), self.updates["historical_fixture_sha256"])
        self.assertEqual(len(self.updates["mappings"]), 3)
        self.assertEqual(result["evidence_kind"], "test-only-derived-expectations-not-a-scan")
        for original, actual in zip(self.raw["findings"], result["findings"], strict=True):
            mapping = next((row for row in self.updates["mappings"] if row["historical"] == original), None)
            expected = dict(original, **mapping["target"]) if mapping else original
            self.assertEqual(actual, expected)

    def test_only_brotli_guard_paths_follow_the_new_source_prefix(self) -> None:
        result = derive_native_findings(self.raw, self.updates)
        self.assertEqual(result["prefixes"], dict(self.raw["prefixes"], brotli="brotli-1.2.1/"))
        for original, actual in zip(self.raw["protected"], result["protected"], strict=True):
            expected = dict(original)
            if original["package"] == "brotli":
                expected["path"] = original["path"].replace("brotli-1.2.0/", "brotli-1.2.1/", 1)
            self.assertEqual(actual, expected)

    def test_unknown_license_path_or_line_is_never_retargeted_or_curated(self) -> None:
        base = self.updates["mappings"][1]["historical"]
        config = json.loads((ROOT / ".ort/package-configurations/drlibs.yml").read_text(encoding="utf-8"))
        mutations = ({"license": self.updates["negative_control_license"]},
                     {"path": base["path"] + ".new"}, {"start_lines": 3, "end_lines": 3})
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                unexpected = dict(base, **mutation)
                raw = deepcopy(self.raw)
                raw["findings"].append(unexpected)
                result = derive_native_findings(raw, self.updates)
                self.assertEqual(result["findings"][-1], unexpected)
        current = dict(base, **self.updates["mappings"][1]["target"])
        shape = dict(path=current["path"], start_lines=current["start_lines"],
                     line_count=current["end_lines"] - current["start_lines"] + 1,
                     detected_license=current["license"])
        self.assertTrue(any(curation_applies(row, shape) for row in config["license_finding_curations"]))
        for mutation in ({"detected_license": self.updates["negative_control_license"]},
                         {"path": shape["path"] + ".new"}, {"start_lines": 3}):
            with self.subTest(current_mutation=mutation):
                unknown = dict(shape, **mutation)
                self.assertFalse(any(curation_applies(row, unknown) for row in config["license_finding_curations"]))

    def test_a_changed_or_duplicate_scoped_observation_requires_review(self) -> None:
        for duplicate in (False, True):
            with self.subTest(duplicate=duplicate):
                raw = deepcopy(self.raw)
                original = self.updates["mappings"][0]["historical"]
                if duplicate:
                    raw["findings"].append(deepcopy(original))
                else:
                    next(row for row in raw["findings"] if row == original)["start_lines"] += 1
                with self.assertRaisesRegex(ValueError, "exact historical observation"):
                    derive_native_findings(raw, self.updates)

    def test_mapping_cannot_change_a_nonidentity_field(self) -> None:
        updates = deepcopy(self.updates)
        updates["mappings"][0]["target"]["license"] = updates["negative_control_license"]
        with self.assertRaisesRegex(ValueError, "package and path"):
            derive_native_findings(self.raw, updates)

    def test_changed_kind_count_or_protected_prefix_requires_review(self) -> None:
        for changed in ("kind", "count", "prefix", "guard-path"):
            with self.subTest(changed=changed):
                updates = deepcopy(self.updates)
                raw = deepcopy(self.raw)
                if changed == "kind":
                    updates["evidence_kind"] = "observed-scan"
                elif changed == "count":
                    updates["mappings"].pop()
                elif changed == "prefix":
                    updates["protected_prefixes"][0]["historical"] = "unknown/"
                else:
                    next(row for row in raw["protected"] if row["package"] == "brotli")["path"] = "unknown/file.c"
                with self.assertRaises(ValueError):
                    derive_native_findings(raw, updates)

    def test_source_proofs_bind_the_active_real_artifact_identities(self) -> None:
        artifacts = json.loads((FIXTURES / "expected_artifacts.json").read_text(encoding="utf-8"))
        by_name = {row["name"]: row for row in artifacts["components"]}
        self.assertEqual(set(self.updates["source_proofs"]), {"brotli-contributing", "mp3-line2", "wav-line2"})
        for proof in self.updates["source_proofs"].values():
            with self.subTest(proof=proof["new_source"]):
                actual = by_name[proof["package"]]
                self.assertEqual(actual["version"], proof["new_version"])
                self.assertEqual(actual["sha256"], proof["new_artifact_sha256"])
                self.assertRegex(proof["scoped_text_sha256"], r"^[0-9a-f]{64}$")
                self.assertTrue(proof["proof_report"].startswith("outputs/dependency-updates-20261112/"))

    def test_raw_expression_records_keep_every_field_and_retarget_only_reviewed_text(self) -> None:
        records = json.loads((FIXTURES / "scancode_evidence.json").read_text(encoding="utf-8"))["records"]
        for record in records:
            before = deepcopy(record)
            matching = next((row for row in self.updates["mappings"]
                             if row["historical"]["package"] == record["package"]
                             and row["historical"]["path"] == record["path"]), None)
            expected = matching["target"]["path"] if matching else record["path"]
            self.assertEqual(expectation_path_for_raw_record(record, self.updates), expected)
            self.assertEqual(record, before)
        original = next(row for row in records if row["package"] == "drlibs")
        for mutation in ({"raw_expression": self.updates["negative_control_license"]},
                         {"path": original["path"] + ".new"}, {"start_lines": 3}):
            with self.subTest(mutation=mutation):
                unknown = dict(original, **mutation)
                self.assertEqual(expectation_path_for_raw_record(unknown, self.updates), unknown["path"])


if __name__ == "__main__":
    unittest.main()
