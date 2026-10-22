"""Keep the reviewed WebP exclusions narrow and preserve both upstream grants."""

import hashlib
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "build/tests/fixtures/license_package_configurations"


class WebpLicenseReviewTests(unittest.TestCase):
    def test_only_native_nonruntime_findings_are_excluded(self):
        evidence = json.loads((FIXTURES / "scancode_evidence.json").read_text())[
            "webp_review"
        ]
        config = json.loads((ROOT / ".ort/package-configurations/libwebp.yml").read_text())
        self.assertEqual("SpdxDocumentFile:vcpkg:libwebp:1.6.0#3", config["id"])
        self.assertEqual(evidence["source_artifact_url"], config["source_artifact_url"])
        expected = {
            "libwebp-1.6.0/m4/ax_pthread.m4": "BUILD_TOOL_OF",
            "libwebp-1.6.0/CONTRIBUTING.md": "DOCUMENTATION_OF",
        }
        self.assertEqual(2, len(config["path_excludes"]))
        self.assertEqual(expected, {e["pattern"]: e["reason"] for e in config["path_excludes"]})
        self.assertFalse(config.get("license_finding_curations", []))
        findings = {f["location"]["path"]: f for f in evidence["findings"]}
        self.assertEqual((59, 83), tuple(findings[next(iter(expected))]["location"][key]
                                        for key in ("start_line", "end_line")))
        self.assertEqual("NOASSERTION", findings["libwebp-1.6.0/CONTRIBUTING.md"]["license"])
        for path in ("libwebp-1.6.0/PATENTS", "libwebp-1.6.0/COPYING",
                     "libwebp-1.6.0/src/enc/vp8l_enc.c", "libwebp-1.6.0/src/enc/new_gpl.c"):
            with self.subTest(path=path):
                self.assertNotIn(path, expected)
        self.assertIn("LicenseRef-scancode-google-patent-license-webm",
                      findings["libwebp-1.6.0/src/enc/vp8l_enc.c"]["license"])

    def test_distribution_retains_complete_copying_and_patents_text(self):
        evidence = json.loads((FIXTURES / "scancode_evidence.json").read_text())[
            "webp_review"
        ]
        self.assertEqual(
            "http://127.0.0.1:8765/sources/"
            "93a852c2b3efafee3723efd4636de855b46f9fe1efddd607e1f42f60fc8f2136.tar.gz",
            evidence["source_artifact_url"],
        )
        text = (ROOT / "licenses/libwebp.txt").read_text(encoding="utf-8").encode("utf-8")
        copying_size = evidence["upstream_lf_sizes"]["COPYING"]
        self.assertEqual(copying_size + 1 + evidence["upstream_lf_sizes"]["PATENTS"], len(text))
        self.assertEqual(b"\n", text[copying_size:copying_size + 1])
        parts = {"COPYING": text[:copying_size], "PATENTS": text[copying_size + 1:]}
        for name in ("COPYING", "PATENTS"):
            self.assertEqual(evidence["upstream_lf_sha256"][name],
                             hashlib.sha256(parts[name]).hexdigest())

    def test_owner_approved_resolution_matches_only_the_exact_webp_patent_message(self):
        config = json.loads((ROOT / ".ort.yml").read_text(encoding="utf-8"))
        entries = config["resolutions"]["rule_violations"]
        self.assertEqual(1, len(entries))
        entry = entries[0]
        self.assertEqual("PATENT_GRANT_EXCEPTION", entry["reason"])
        package = "SpdxDocumentFile:vcpkg:libwebp:1.6.0#3"
        license_id = "LicenseRef-scancode-google-patent-license-webm"
        message = (f"The dependency '{package}' is licensed under the ScanCode 'patent-license' "
                   f"categorized license {license_id}. This requires approval.")
        self.assertEqual(r"\A" + message.replace(".", r"\.") + r"\Z", entry["message"])
        self.assertIsNotNone(re.fullmatch(entry["message"], message))
        # ORT collapses whitespace before matching; these cases change canonical content.
        for other in (
            message.replace("libwebp", "other-library"),
            message.replace("1.6.0#3", "1.6.0#4"),
            message.replace("1.6.0#3", "1.7.0#3"),
            message.replace(license_id, "LicenseRef-unreviewed-patent"),
            message.replace("patent-license", "copyleft"),
            message.replace(license_id, "GPL-3.0-only"),
            message.replace(license_id, "NOASSERTION"),
            "extra " + message,
            message + " extra",
        ):
            with self.subTest(message=other):
                self.assertIsNone(re.search(entry["message"], other))
        evidence = json.loads((FIXTURES / "scancode_evidence.json").read_text())["webp_review"]
        package_config = json.loads((ROOT / ".ort/package-configurations/libwebp.yml").read_text())
        self.assertEqual(package, package_config["id"])
        self.assertEqual(evidence["source_artifact_url"], package_config["source_artifact_url"])
        self.assertIn(evidence["source_sha256"], entry["comment"])
        self.assertIn("Roma approved on 2026-10-21", entry["comment"])
        self.assertIn("Complete COPYING and PATENTS are retained", entry["comment"])
        self.assertIn("PATENTS and source findings remain scanned", entry["comment"])
