"""Focused regression for the resolved DXLib licensing classification.

The WolfRPG release links the pinned UberWolf 0.6.4 reader, whose compiled
``3rdParty/DXLib`` translation units ship a Japanese creator comment but no license
file.  The reviewed inventory must record that licensing question as *resolved* by the
owner against the authoritative DX Library license page, keep the compiled DXLib trees
inside the scanned ORT scope, and leave any real ScanCode detection to the first
observed CI run instead of pre-writing a curation, exclusion or waiver for it.
"""
from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
INVENTORY = ROOT / "build" / "license-inventory.json"
CONFIG_DIR = ROOT / ".ort" / "package-configurations"
NOTICE = ROOT / "licenses" / "uberwolf.txt"
PORT_COPYRIGHT = ROOT / "build" / "vcpkg" / "ports" / "uberwolf" / "copyright"

DXLIB_URL = "https://dxlib.xsrv.jp/dxlicense.html"
DXLIB_PAGE_SHA256 = "03c3a813b1ea992c81932a3e701946048489d67b355cbe9455a7cb688eaa7d6c"
DXLIB_CREDIT = "DX Library Copyright (C) 2001-2026 Takumi Yamada."


def _component(document, name):
    return next(
        component
        for component in document["components"]
        if isinstance(component, dict) and component.get("name") == name
    )


def _dxlib_section(text):
    return text.split("3rdParty/DXLib", 1)[1].split("3rdParty/lz4", 1)[0]


class DxlibInventoryClassification(unittest.TestCase):
    """The inventory records the resolved licensing, not an open owner question."""

    def setUp(self) -> None:
        self.document = json.loads(INVENTORY.read_text(encoding="utf-8"))
        self.note = _component(self.document, "uberwolf")["notes"]
        self.config = json.loads((CONFIG_DIR / "uberwolf.yml").read_text(encoding="utf-8"))

    def test_the_note_records_the_resolved_dxlib_license(self) -> None:
        self.assertIn(DXLIB_URL, self.note)
        self.assertIn("resolved", self.note.lower())

    def test_the_note_no_longer_frames_dxlib_as_an_open_owner_question(self) -> None:
        self.assertNotIn("must resolve", self.note)
        self.assertNotIn("audit can pass", self.note)

    def test_the_note_distinguishes_the_reviewed_classification_from_a_ci_result(self) -> None:
        self.assertIn("deferred", self.note.lower())
        self.assertNotIn("unknown finding", self.note.lower())

    def test_the_note_keeps_the_compiled_dxlib_trees_scanned(self) -> None:
        self.assertIn("scanned", self.note.lower())

    def test_the_configuration_excludes_or_curates_no_compiled_dxlib_path(self) -> None:
        entries = [exclude["pattern"] for exclude in self.config.get("path_excludes", [])]
        entries += [
            curation["path"]
            for curation in self.config.get("license_finding_curations", [])
        ]
        for entry in entries:
            with self.subTest(entry=entry):
                self.assertNotIn("3rdParty/DXLib", entry)


class DxlibNoticeProof(unittest.TestCase):
    """The shipped notice carries the reproducible authoritative DXLib terms."""

    def setUp(self) -> None:
        self.notice = NOTICE.read_text(encoding="utf-8")
        self.section = _dxlib_section(self.notice)

    def test_the_notice_carries_the_authoritative_page_and_its_digest(self) -> None:
        self.assertIn(DXLIB_URL, self.section)
        self.assertIn(DXLIB_PAGE_SHA256, self.section)

    def test_the_notice_reproduces_the_exact_redistribution_credit(self) -> None:
        self.assertIn(DXLIB_CREDIT, self.section)

    def test_the_notice_does_not_assert_an_spdx_dxlib_identifier(self) -> None:
        self.assertNotIn("MIT", self.section)

    def test_the_port_copyright_stays_byte_identical_to_the_shipped_notice(self) -> None:
        self.assertEqual(PORT_COPYRIGHT.read_bytes(), NOTICE.read_bytes())


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
