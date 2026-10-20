"""Release integration contracts: current production inputs and master dependencies."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[2]
NS = {"m": "http://schemas.microsoft.com/developer/msbuild/2003"}
CORE = ('archive.cpp', 'chacha_crypto.cpp', 'crypto.cpp', 'game_context.cpp', 'huffman_reader.cpp', 'legacy_archive.cpp', 'legacy_crypto.cpp', 'lz_reader.cpp', 'modern_crypto.cpp', 'pro_keys.cpp', 'text_encoding.cpp', 'wolfx_archive.cpp', 'wolfx_candidates.cpp', 'wolfx_game_metadata.cpp', 'wolfx_metadata.cpp')

class WolfReleaseIntegration(unittest.TestCase):
    def test_fuzz_project_compiles_every_current_core_and_platform_source(self):
        root = ET.parse(ROOT / "build/projects/fuzz-wolfrpg.vcxproj").getroot()
        units = {e.get("Include", "").replace(chr(92), "/")
                 for e in root.findall(".//m:ClCompile", NS)}
        required = {"$(RepositoryRoot)src/core/wolfrpg/" + name for name in CORE}
        required.add("$(RepositoryRoot)src/platform/win32/system_codepage.cpp")
        self.assertEqual(sorted(required - units), [])
        includes = root.findall(".//m:AdditionalIncludeDirectories", NS)
        self.assertTrue(any("include/uberwolf" in (e.text or "").replace(chr(92), "/")
                            for e in includes))

    def test_test_link_preserves_master_crypto_and_winsock_dependencies(self):
        root = ET.parse(ROOT / "build/projects/tests.vcxproj").getroot()
        links = root.findall(".//m:Link/m:AdditionalDependencies", NS)
        self.assertEqual(len(links), 2)
        for link in links:
            dependencies = (link.text or "").split(";")
            for expected in ("uberwolf.lib", "tfpsacrypto.lib", "bcrypt.lib", "ws2_32.lib"):
                self.assertIn(expected, dependencies)
            self.assertNotIn("mbedcrypto.lib", dependencies)
