"""Unity experiment fixtures must survive a Git checkout byte for byte."""

from __future__ import annotations

from pathlib import Path
import subprocess
import unittest


REPOSITORY = Path(__file__).resolve().parents[2]
BINARY_FIXTURES = (
    "src/modules/unity/fixtures/audio/pcm16.fsb",
    "src/modules/unity/fixtures/audio/short_vorbis.fsb",
    "src/modules/unity/fixtures/text-only.assets",
)
TEXT_FIXTURES = ("src/modules/unity/fixtures/text-only.expected.txt",)


def _git(*arguments: str, input_bytes: bytes | None = None) -> bytes:
    result = subprocess.run(
        [
            "git",
            "-c",
            f"safe.directory={REPOSITORY.as_posix()}",
            "-C",
            str(REPOSITORY),
            *arguments,
        ],
        input=input_bytes,
        capture_output=True,
        check=True,
    )
    return result.stdout


class UnityFixtureCheckoutTests(unittest.TestCase):
    def test_checkout_attributes_preserve_fixture_bytes(self) -> None:
        paths = (*BINARY_FIXTURES, *TEXT_FIXTURES)
        payload = b"".join(path.encode("utf-8") + b"\0" for path in paths)

        tracked = _git("ls-files", "-z", "--", *paths).split(b"\0")
        self.assertEqual([item.decode("utf-8") for item in tracked if item], list(paths))

        fields = _git(
            "check-attr", "-z", "--stdin", "text", "eol", input_bytes=payload
        ).decode("utf-8").rstrip("\0").split("\0")
        self.assertEqual(len(fields), len(paths) * 2 * 3)

        attributes: dict[str, dict[str, str]] = {path: {} for path in paths}
        for index in range(0, len(fields), 3):
            attributes[fields[index]][fields[index + 1]] = fields[index + 2]

        for path in BINARY_FIXTURES:
            with self.subTest(path=path):
                self.assertEqual(attributes[path]["text"], "unset")
        for path in TEXT_FIXTURES:
            with self.subTest(path=path):
                self.assertEqual(attributes[path], {"text": "set", "eol": "lf"})


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
