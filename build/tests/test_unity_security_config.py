"""Select safe NativeAOT packs without restoring or running a compiler.

The installed SDK's real ProcessFrameworkReferences task is exercised against a
stale SDK/package compiler selection. This target only emits package-selection
items; Restore, Compile and Publish are never invoked by these tests.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

from test_unity_size_config import _dotnet_path, _project_path


SAFE_VERSION = "10.0.15"
NATIVE = {
    "Configuration": "Release",
    "PublishAot": "true",
    "NativeLib": "Shared",
}


class UnitySecurityConfigurationTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory(prefix="unity-security-")
        self.addCleanup(temporary.cleanup)
        self.workspace = Path(temporary.name)
        self.project = _project_path()
        # Reproduce either an old SDK or a previously restored compiler package.
        # The project-owned policy must win after evaluation, before selection.
        document = ET.Element("Project")
        ET.SubElement(document, "Import", Project=str(self.project.parents[1] / "Directory.Build.targets"))
        item_group = ET.SubElement(document, "ItemGroup")
        item = ET.SubElement(item_group, "KnownILCompilerPack", Update="Microsoft.DotNet.ILCompiler")
        ET.SubElement(item, "ILCompilerPackVersion").text = "10.0.13"
        self.imports = self.workspace / "stale-compiler.targets"
        ET.ElementTree(document).write(self.imports, encoding="utf-8", xml_declaration=True)

    def select(self, **overrides: str) -> subprocess.CompletedProcess[str]:
        properties = {
            **NATIVE,
            "BaseIntermediateOutputPath": str(self.workspace / "obj") + os.sep,
            "MSBuildProjectExtensionsPath": str(self.workspace / "obj") + os.sep,
            "DirectoryBuildTargetsPath": str(self.imports),
            **overrides,
        }
        command = [
            _dotnet_path(), "msbuild", str(self.project), "-nologo", "-verbosity:quiet",
            "-m:1", "-nr:false", "-target:ProcessFrameworkReferences",
            "-getProperty:RuntimeFrameworkVersion",
            "-getItem:HostILCompilerPack,TargetILCompilerPack",
        ]
        command.extend(f"-p:{name}={value}" for name, value in properties.items())
        return subprocess.run(command, cwd=self.project.parent, text=True, encoding="utf-8",
                              capture_output=True, timeout=120, check=False)

    def selected(self, **properties: str) -> dict:
        result = self.select(**properties)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return json.loads(result.stdout)

    def assert_safe(self, document: dict, arch: str) -> None:
        self.assertEqual(document["Properties"]["RuntimeFrameworkVersion"], SAFE_VERSION)
        host = document["Items"]["HostILCompilerPack"]
        target = document["Items"]["TargetILCompilerPack"]
        self.assertEqual(len(host), 1)
        self.assertEqual(len(target), 1)
        self.assertTrue(host[0]["Identity"].casefold().startswith("runtime."))
        self.assertTrue(host[0]["Identity"].casefold().endswith(".microsoft.dotnet.ilcompiler"))
        self.assertEqual(host[0]["NuGetPackageVersion"], SAFE_VERSION)
        self.assertEqual(target[0]["NuGetPackageVersion"], SAFE_VERSION)
        self.assertEqual(target[0]["Identity"].casefold(),
                         f"Microsoft.NETCore.App.Runtime.NativeAOT.win-{arch}".casefold())

    def test_windows_shared_aot_selects_safe_runtime_and_compiler_on_both_architectures(self) -> None:
        for arch in ("x64", "x86"):
            for configuration in ("Debug", "Release"):
                with self.subTest(architecture=arch, configuration=configuration):
                    self.assert_safe(self.selected(RuntimeIdentifier=f"win-{arch}",
                                                   Configuration=configuration), arch)

    def test_explicit_safe_runtime_remains_accepted(self) -> None:
        for arch in ("x64", "x86"):
            with self.subTest(architecture=arch):
                self.assert_safe(self.selected(RuntimeIdentifier=f"win-{arch}",
                                               RuntimeFrameworkVersion=SAFE_VERSION), arch)

    def test_conflicting_runtime_overrides_fail_before_pack_selection(self) -> None:
        for arch in ("x64", "x86"):
            for version in ("10.0.13", "10.0.14"):
                with self.subTest(architecture=arch, version=version):
                    result = self.select(RuntimeIdentifier=f"win-{arch}", RuntimeFrameworkVersion=version)
                    self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertIn("CVE-2026-111497", result.stdout + result.stderr)
                    self.assertIn(SAFE_VERSION, result.stdout + result.stderr)

    def test_managed_and_other_native_modes_keep_explicit_runtime_configuration(self) -> None:
        cases = (
            {"PublishAot": "false", "RuntimeIdentifier": "win-x64"},
            {"PublishAot": "false"},
            {"NativeLib": "Static", "RuntimeIdentifier": "win-x64"},
            {"RuntimeIdentifier": "linux-x64"},
            {"RuntimeIdentifier": "win-arm64"},
            {"RuntimeIdentifier": ""},
        )
        for properties in cases:
            with self.subTest(properties=properties):
                document = self.selected(**properties, RuntimeFrameworkVersion="10.0.13")
                self.assertEqual(document["Properties"]["RuntimeFrameworkVersion"], "10.0.13")
                for kind in ("HostILCompilerPack", "TargetILCompilerPack"):
                    self.assertTrue(all(item["NuGetPackageVersion"] == "10.0.13"
                                        for item in document["Items"][kind]))


if __name__ == "__main__":
    unittest.main()
