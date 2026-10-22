"""Storage-hygiene regressions for the owned WolfRPG fixture and fuzz seed.

These checks pin three invariants:

* the owned ``classic-dx8-payload.wolf`` fixture is declared binary and not
  subject to text/EOL normalisation, so its on-disk bytes survive checkout;
* the owned ``official-3396/payload.bin`` extraction oracle is likewise
  declared binary and pinned by its exact size and digest;
* the fixture content digest is exactly the reviewed golden value;
* the ``minimal-header.hex`` fuzz seed decodes to those same golden bytes.

The tests only read repository state; they never mutate the Git index.
"""

import hashlib
import pathlib
import subprocess
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURE_REL = "src/tests/fixtures/wolfrpg/classic-dx8-payload.wolf"
FIXTURE = REPO_ROOT / FIXTURE_REL
SEED = REPO_ROOT / "src/fuzz/corpus/wolfrpg/minimal-header.hex"

FIXTURE_SHA256 = "9ABB708F20B26CD54AFE8597680700CC3474E9319B1A65CF7845BA92C9ED3521"
FIXTURE_SIZE = 966

OFFICIAL_PAYLOAD_REL = "src/tests/fixtures/wolfrpg/official-3396/payload.bin"
OFFICIAL_PAYLOAD = REPO_ROOT / OFFICIAL_PAYLOAD_REL
OFFICIAL_PAYLOAD_SHA256 = (
    "40AFF2E9D2D8922E47AFD4648E6967497158785FBD1DA870E7110266BF944880"
)
OFFICIAL_PAYLOAD_SIZE = 256


class WolfrpgFixtureStorageTests(unittest.TestCase):
    def test_fixture_is_binary_and_not_text(self):
        result = subprocess.run(
            ["git", "check-attr", "binary", "text", "--", FIXTURE_REL],
            cwd=REPO_ROOT,
            capture_output=True,
            text=True,
            check=True,
        )
        attrs = {}
        for line in result.stdout.splitlines():
            _, attribute, value = line.rsplit(":", 2)
            attrs[attribute.strip()] = value.strip()

        self.assertEqual(attrs.get("binary"), "set", result.stdout)
        self.assertEqual(attrs.get("text"), "unset", result.stdout)

    def test_fixture_digest_is_exact(self):
        data = FIXTURE.read_bytes()
        self.assertEqual(len(data), FIXTURE_SIZE)
        self.assertEqual(hashlib.sha256(data).hexdigest().upper(), FIXTURE_SHA256)

    def test_fuzz_seed_decodes_to_fixture(self):
        expected = FIXTURE.read_bytes()
        seed_text = "".join(SEED.read_text(encoding="ascii").split())
        decoded = bytes.fromhex(seed_text)
        self.assertEqual(decoded, expected)


class Official3396PayloadStorageTests(unittest.TestCase):
    def test_payload_is_binary_and_not_text(self):
        result = subprocess.run(
            ["git", "check-attr", "binary", "text", "--", OFFICIAL_PAYLOAD_REL],
            cwd=REPO_ROOT,
            capture_output=True,
            text=True,
            check=True,
        )
        attrs = {}
        for line in result.stdout.splitlines():
            _, attribute, value = line.rsplit(":", 2)
            attrs[attribute.strip()] = value.strip()

        self.assertEqual(attrs.get("binary"), "set", result.stdout)
        self.assertEqual(attrs.get("text"), "unset", result.stdout)

    def test_payload_digest_is_exact(self):
        data = OFFICIAL_PAYLOAD.read_bytes()
        self.assertEqual(len(data), OFFICIAL_PAYLOAD_SIZE)
        self.assertEqual(
            hashlib.sha256(data).hexdigest().upper(), OFFICIAL_PAYLOAD_SHA256
        )


if __name__ == "__main__":
    unittest.main()
