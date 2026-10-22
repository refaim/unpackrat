"""Contract test for the coverage diagnostic image upload step in ci-check.yml."""

from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "ci-check.yml"

IMAGE_NAME = "diagnostic-coverage-${{ inputs.arch }}-images"
PINNED = "actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9"
VERIFY_NAME = "- name: Verify one stage for one architecture"
EXPECTED_PATHS = [
    "out/native/bin/${{ inputs.arch }}/Coverage/tests.exe",
    "out/native/bin/${{ inputs.arch }}/Coverage/*.so",
]

_IMAGE_NAME = re.compile(
    r"name:\s*diagnostic-coverage-\$\{\{\s*inputs\.arch\s*\}\}-images"
)
_GUARD = re.compile(
    r"if:\s*(?:\$\{\{\s*)?failure\(\)\s*&&\s*inputs\.stage\s*==\s*'coverage'\s*(?:\}\})?"
)


def _steps(text: str):
    return [block for block in re.split(r"\n(?=      - )", text) if block.strip()]


def _paths(block: str):
    lines = block.splitlines()
    for index, line in enumerate(lines):
        if line.strip() == "path: |":
            base = len(line) - len(line.lstrip())
            paths = []
            for continuation in lines[index + 1:]:
                if not continuation.strip():
                    continue
                if len(continuation) - len(continuation.lstrip()) <= base:
                    break
                paths.append(continuation.strip())
            return paths
    return []


class CoverageImageStepTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.steps = _steps(WORKFLOW.read_text(encoding="utf-8"))
        cls.image_positions = [i for i, s in enumerate(cls.steps) if IMAGE_NAME in s]

    def _image_step(self) -> str:
        self.assertEqual(
            1,
            len(self.image_positions),
            f"expected exactly one '{IMAGE_NAME}' step, found {len(self.image_positions)}",
        )
        return self.steps[self.image_positions[0]]

    def test_exactly_one_image_step(self):
        self._image_step()

    def test_pinned_action_and_name(self):
        step = self._image_step()
        self.assertIn(PINNED, step)
        self.assertRegex(step, _IMAGE_NAME)

    def test_guard(self):
        self.assertRegex(self._image_step(), _GUARD)

    def test_two_explicit_paths_without_pdb(self):
        paths = _paths(self._image_step())
        self.assertEqual(EXPECTED_PATHS, paths)
        for path in paths:
            self.assertNotIn("pdb", path.lower())

    def test_retention_and_no_run(self):
        step = self._image_step()
        self.assertRegex(step, r"retention-days:\s*14(?!\d)")
        self.assertNotIn("continue-on-error", step)
        self.assertIsNone(re.search(r"^\s*run:", step, re.MULTILINE))

    def test_follows_verify_step(self):
        self._image_step()
        verify = [i for i, s in enumerate(self.steps) if VERIFY_NAME in s]
        self.assertEqual(1, len(verify), "verification step not found")
        self.assertEqual(verify[0] + 1, self.image_positions[0])


if __name__ == "__main__":
    unittest.main()
