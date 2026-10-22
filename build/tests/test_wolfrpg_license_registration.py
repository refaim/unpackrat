"""Focused regression for the WolfRPG license registration.

The new ``wolfrpg`` Observer module links the pinned UberWolf 0.6.4 Wolf RPG archive
reader, so the reviewed inventory has to register the ``uberwolf`` overlay component,
the ``wolfrpg`` module notice route and the nlohmann-json scope change before the native
ORT audit can pass.  These assertions bind the exact upstream identity, the verified
archive hashes and the reviewed build configuration, so a source or build-option swap
fails closed until the inventory is re-reviewed.
"""
from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
INVENTORY = ROOT / "build" / "license-inventory.json"
CONFIG_DIR = ROOT / ".ort" / "package-configurations"

UBERWOLF_REVISION = "f4191375d289171b1f29dbc5ca320133b2b4b96a"
UBERWOLF_URL = (
    "https://github.com/Sinflower/UberWolf/archive/" + UBERWOLF_REVISION + ".tar.gz"
)
UBERWOLF_SHA512 = (
    "28105836e9fb6af1b380d72066b57007316aa28c20480e0e1583a2252bdb27e3420d6d8b134332838"
    "68dd60236d629818453208836b1e7824eeffa143240a725"
)
UBERWOLF_SHA256 = "db7bcad1c6d9f550a31cd2d0c90c52f5c43a8d4576370565b658e78dabbcaf57"
UBERWOLF_ARCHIVE_PREFIX = "UberWolf-" + UBERWOLF_REVISION + "/"
UBERWOLF_RECIPE = "build/vcpkg/ports/uberwolf/portfile.cmake"
UBERWOLF_MANIFEST = "build/vcpkg/ports/uberwolf/vcpkg.json"
UBERWOLF_PATCHES = (
    "build/vcpkg/ports/uberwolf/version.patch",
    "build/vcpkg/ports/uberwolf/x86-size-overloads.patch",
    "build/vcpkg/ports/uberwolf/wolfx-file-size.patch",
    "build/vcpkg/ports/uberwolf/default-crypt-modes.patch",
    "build/vcpkg/ports/uberwolf/mode-selection.patch",
    "build/vcpkg/ports/uberwolf/archive-callbacks.patch",
    "build/vcpkg/ports/uberwolf/archive-entry-plan.patch",
    "build/vcpkg/ports/uberwolf/bounded-huffman.patch",
    "build/vcpkg/ports/uberwolf/keycreate-scratch.patch",
    "build/vcpkg/ports/uberwolf/recovery-e1-contract.patch",
    "build/vcpkg/ports/uberwolf/encoding-aware-legacy.patch",
)
WOLFRPG_PACKAGING = (
    "Observer.txt",
    "GPL-3.0.txt",
    "uberwolf.txt",
    "json.txt",
    "YY-Thunks.txt",
)
# The reviewed build-option digests of the uberwolf port as the release checkout
# materialises them: working-tree text is CRLF while ``*.patch`` stays LF.
UBERWOLF_BUILD_CONFIGURATION = {
    UBERWOLF_RECIPE: "055789a5a1f1ac00b43a9e7737a623eb787268047eba20a1fbe7e73759915e0b",
    UBERWOLF_MANIFEST: "0ec31ae686c0663128c15452029a53b8d9da4e4e0fd06c3e8c07107aab760c6d",
    "build/vcpkg/ports/uberwolf/CMakeLists.txt":
        "59513260ba03b3e353ec84c003d1aed9e92655b96339bcd42dae3d1f4873d927",
    "build/vcpkg/ports/uberwolf/version.patch":
        "abbca5dacfdc9958e412a9cb8f10f68e6c25c59d0d1d1ae2c38d8b687ccc6fb0",
    "build/vcpkg/ports/uberwolf/x86-size-overloads.patch":
        "aacb54eac5ac955931c7dd5882513561212b0df201e977ebd408737ddf525fa1",
    "build/vcpkg/ports/uberwolf/wolfx-file-size.patch":
        "7b2e16e8349de77f3d4a3e0f5d66e7b99c883c0a4f64855217288d29c7df264c",
    "build/vcpkg/ports/uberwolf/default-crypt-modes.patch":
        "e910148483ffa52eec70b0d119086c6c6bb7754b025202f6884914d41686b185",
    "build/vcpkg/ports/uberwolf/mode-selection.patch":
        "0339d430cb952a869541f6eeb7f3efdbbe78159e87e9063b76df74a312532ea5",
    "build/vcpkg/ports/uberwolf/archive-callbacks.patch":
        "8c21558fe8c9a0231b9f966749293d60d6fd2ec4c79c736e9ec016affbad553e",
    "build/vcpkg/ports/uberwolf/archive-entry-plan.patch":
        "28ff4bd63ab28a1fc18e901d821bd496735ef09fc290fabf6546cde0dcf036ef",
    "build/vcpkg/ports/uberwolf/bounded-huffman.patch":
        "d9ee5722e550da98b449cad48b67414ea4dbc27d854fabd1a2048ee1d6938e7e",
    "build/vcpkg/ports/uberwolf/keycreate-scratch.patch":
        "f42c803589179f2bb1a5d6a65cdf2508ba93b03f95e1bb4a66eea7200cf80a43",
    "build/vcpkg/ports/uberwolf/recovery-e1-contract.patch":
        "ab39cd7266793d0d28df9e7c68836aae9fa9e62f5183944d466ef4f2fbc02511",
    "build/vcpkg/ports/uberwolf/encoding-aware-legacy.patch":
        "f8eb10d561d47bde96c108902fbe1b25f0d30f58eee26680ac72a805cd290737",
    "build/vcpkg/triplets/unpackrat-x86-windows-static.cmake":
        "7f706840bc922781120cbd607e0f5a6008f0562e2178f9127f7bc8ea7d5754ab",
    "build/vcpkg/triplets/unpackrat-x64-windows-static.cmake":
        "e198af9bb1ccf802c74357faf1b90c2c808694112007dc71cc27fe10f21be6d7",
}


def _component(document, name):
    return next(
        component
        for component in document["components"]
        if isinstance(component, dict) and component.get("name") == name
    )


class UberwolfRuntimeDependency(unittest.TestCase):
    """The pinned UberWolf reader is a reviewed runtime dependency of the wolfrpg module."""

    def setUp(self) -> None:
        self.document = json.loads(INVENTORY.read_text(encoding="utf-8"))

    def test_the_uberwolf_runtime_component_is_registered_for_wolfrpg(self) -> None:
        component = _component(self.document, "uberwolf")
        self.assertEqual(component["kind"], "vcpkg-package")
        self.assertEqual(component["scope"], "runtime")
        self.assertIs(component["incorporated"], True)
        self.assertIs(component["packaged"], True)
        self.assertEqual(component["appliesTo"], ["wolfrpg"])
        # vcpkg canonicalises the overlay manifest's license expression into SPDX order before
        # it writes ``licenseConcluded``, and ``licenses.py`` compares that emitted expression
        # against this approved value exactly.  The canonical SPDX order is what the installed
        # producer emits, so the inventory records it verbatim.
        self.assertEqual(component["declaredExpression"], "BSD-2-Clause AND MIT AND Unlicense")
        self.assertEqual(component["version"], "0.6.4")
        self.assertEqual(component["vcpkgName"], "uberwolf")
        self.assertEqual(component["features"], [])
        self.assertTrue(component["notes"].strip())

    def test_the_reviewed_source_is_the_pinned_sinflower_archive(self) -> None:
        source = _component(self.document, "uberwolf")["source"]
        self.assertEqual(source["origin"], "overlay")
        self.assertEqual(source["identity"], "github.com/sinflower/uberwolf")
        self.assertEqual(source["recipe"], UBERWOLF_RECIPE)
        self.assertEqual(source["url"], UBERWOLF_URL)
        self.assertEqual(source["sha512"], [UBERWOLF_SHA512])
        self.assertEqual(source["sha256"], [UBERWOLF_SHA256])
        self.assertEqual(source["patches"], list(UBERWOLF_PATCHES))

    def test_the_reviewed_build_configuration_binds_the_recipe_patches_and_triplets(self) -> None:
        guard = _component(self.document, "uberwolf")["buildConfiguration"]
        self.assertEqual(guard, UBERWOLF_BUILD_CONFIGURATION)

    def test_the_uberwolf_notice_is_retained_and_shipped_to_wolfrpg(self) -> None:
        component = _component(self.document, "uberwolf")
        self.assertEqual(component["notices"], ["licenses/uberwolf.txt"])
        self.assertEqual(self.document["packaging"]["wolfrpg"], list(WOLFRPG_PACKAGING))
        self.assertTrue((ROOT / "licenses" / "uberwolf.txt").is_file())


class UberwolfNoticeProvenance(unittest.TestCase):
    """The retained notice reproduces the exact pinned revision, not a stale copy."""

    def setUp(self) -> None:
        self.text = (ROOT / "licenses" / "uberwolf.txt").read_text(encoding="utf-8")

    def test_the_notice_names_the_pinned_revision_and_every_compiled_component(self) -> None:
        self.assertIn("Sinflower", self.text)
        self.assertIn(UBERWOLF_REVISION, self.text)
        for expected in ("LZ4", "BSD 2-Clause", "Unlicense", "nlohmann/json"):
            with self.subTest(expected=expected):
                self.assertIn(expected, self.text)

    def test_the_notice_references_the_dxlib_terms_by_official_url(self) -> None:
        # The pinned revision carries no verbatim DXLib license file, so the terms are
        # referenced by the official URL instead of being paraphrased or invented.
        self.assertIn("https://dxlib.xsrv.jp/dxlicense.html", self.text)

    def test_the_notice_does_not_keep_the_stale_wolfcrypt_attribution(self) -> None:
        # The 0.6.4 tree carries the SHA-512 helper under Sinflower's MIT copyright, so the
        # earlier revision's third-party attribution must not survive.
        self.assertNotIn("Filip Dobrosavljevic", self.text)


class WolfrpgModuleScope(unittest.TestCase):
    """The wolfrpg module is added to every reviewed scope that covers all modules."""

    def setUp(self) -> None:
        self.document = json.loads(INVENTORY.read_text(encoding="utf-8"))

    def test_wolfrpg_joins_every_whole_module_scope(self) -> None:
        for name in ("yy-thunks", "observer-api", "unpackrat-source"):
            with self.subTest(component=name):
                self.assertIn("wolfrpg", _component(self.document, name)["appliesTo"])

    def test_nlohmann_json_becomes_a_packaged_runtime_dependency_for_wolfrpg(self) -> None:
        component = _component(self.document, "nlohmann-json")
        self.assertEqual(component["scope"], "runtime")
        self.assertIs(component["packaged"], True)
        self.assertIn("wolfrpg", component["appliesTo"])

    def test_the_nlohmann_configuration_comment_no_longer_calls_json_test_only(self) -> None:
        config = json.loads((CONFIG_DIR / "nlohmann-json.yml").read_text(encoding="utf-8"))
        joined = " ".join(
            exclude.get("comment", "") for exclude in config.get("path_excludes", [])
        )
        self.assertIn("wolfrpg", joined.lower() + json.dumps(config).lower())
        self.assertNotIn("for its own tests only", json.dumps(config))


class UberwolfPackageConfiguration(unittest.TestCase):
    """The uberwolf ORT configuration excludes only the not-compiled upstream tree."""

    def setUp(self) -> None:
        path = CONFIG_DIR / "uberwolf.yml"
        self.assertTrue(path.is_file(), "uberwolf package configuration is missing")
        self.config = json.loads(path.read_text(encoding="utf-8"))

    def test_the_configuration_targets_the_content_addressed_archive(self) -> None:
        self.assertEqual(self.config["id"], "SpdxDocumentFile:vcpkg:uberwolf:0.6.4")
        self.assertEqual(
            self.config["source_artifact_url"],
            "http://127.0.0.1:8765/sources/" + UBERWOLF_SHA256 + ".tar.gz",
        )

    def test_the_excludes_are_scoped_to_the_uncompiled_trees(self) -> None:
        patterns = {exclude["pattern"] for exclude in self.config["path_excludes"]}
        for scope in ("UberWolf", "UberWolfCli", "3rdParty/CLI11", "3rdParty/nlohmann"):
            with self.subTest(scope=scope):
                self.assertIn(UBERWOLF_ARCHIVE_PREFIX + scope + "/**", patterns)
        for exclude in self.config["path_excludes"]:
            with self.subTest(pattern=exclude["pattern"]):
                self.assertTrue(exclude["pattern"].startswith(UBERWOLF_ARCHIVE_PREFIX))
                self.assertTrue(exclude["comment"].strip())

    def test_the_compiled_trees_stay_scanned(self) -> None:
        patterns = [exclude["pattern"] for exclude in self.config["path_excludes"]]
        for compiled in (
            UBERWOLF_ARCHIVE_PREFIX + "UberWolfLib/UberWolfLib.cpp",
            UBERWOLF_ARCHIVE_PREFIX + "3rdParty/DXLib/DXArchive.cpp",
            UBERWOLF_ARCHIVE_PREFIX + "3rdParty/lz4/lz4.c",
        ):
            with self.subTest(compiled=compiled):
                self.assertFalse(_ant_match(patterns, compiled))


def _ant_match(patterns, path):
    import re

    def one(pattern):
        regex = ""
        i = 0
        while i < len(pattern):
            if pattern.startswith("**", i):
                regex += ".*"
                i += 2
            elif pattern[i] == "*":
                regex += "[^/]*"
                i += 1
            elif pattern[i] == "?":
                regex += "[^/]"
                i += 1
            else:
                regex += re.escape(pattern[i])
                i += 1
        return re.fullmatch(regex, path) is not None

    return any(one(pattern) for pattern in patterns)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
