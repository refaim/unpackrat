#!/usr/bin/env python3
"""Opt-in GC contract checks against the actual installed NativeAOT SDK targets.

These tests evaluate the production UnityProbe.csproj, resolve installed framework
references, and run the SDK's SetupOSSpecificProps / WriteIlcRspFileForCompilation.
They never compile, publish, invoke ILC/link.exe, restore, or install packages.
Only the compiler input is a tiny dummy file; the GC library selection, IlcArg
items and response-file contents all come from the real SDK implementation.

UNPACKRAT_RUN_NATIVE_GC_TESTS=1 python experiments/unity-nativeaot/tests/test_native_gc.py
"""

import json
import os
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PROJECT = ROOT / "experiments" / "unity-nativeaot" / "UnityProbe" / "UnityProbe.csproj"
WORK_ROOT = ROOT / "out" / "unity-speed" / "gc-sdk-contract" / f"run-{os.getpid()}"
OPT_IN = os.environ.get("UNPACKRAT_RUN_NATIVE_GC_TESTS") == "1"
GC_POLICY_OPTIONS = {
    "System.GC.HeapCount",
    "System.GC.MaxHeapCount",
    "System.GC.DynamicAdaptationMode",
    "System.GC.ConserveMemory",
}

# Supply only an input/response destination to the real compiler-response target.
# No GC settings, compiler argument transforms, or runtime-library rules are copied.
PROBE_INPUT = """<Project>
  <ItemGroup>
    <AssetsFilePackageFolder Include="$(NuGetPackageRoot)" />
  </ItemGroup>
  <Target Name="UnityGcContractInput" BeforeTargets="WriteIlcRspFileForCompilation">
    <ItemGroup>
      <ManagedBinary Include="$(UnityGcProbeBinary)">
        <IlcRspFile>$(UnityGcProbeResponseFile)</IlcRspFile>
      </ManagedBinary>
      <IlcCompileInput Include="@(ManagedBinary)" />
    </ItemGroup>
  </Target>
</Project>
"""


def sdk_probe(case: str, rid: str, *, native: bool = True, response: bool = True, library: str = "Shared"):
    directory = WORK_ROOT / case
    directory.mkdir(parents=True, exist_ok=True)
    fixture = directory / "probe-input.targets"
    fixture.write_text(PROBE_INPUT, encoding="utf-8")
    binary = directory / "input.dll"
    binary.write_bytes(b"SDK response-generation input; never compiled or executed\n")
    rsp = directory / "gc-contract.ilc.rsp"
    rsp.unlink(missing_ok=True)
    props = {
        "RuntimeIdentifier": rid,
        "PublishAot": str(native).lower(),
        "NativeLib": library,
        "Configuration": "Release",
        "IlcUseEnvironmentalTools": "true",
        # Remove Compile and PrepareForILLink prerequisites. The real SDK targets
        # under test still execute; only code generation/linking is excluded.
        "IlcCompileDependsOn": "SetupOSSpecificProps",
        # Query cached framework/compiler packs directly, independently of whichever
        # single RID the last full production restore wrote to project.assets.json.
        "ImportRuntimeIlcPackageTargetDependsOn": "ResolveFrameworkReferences",
        "SkipResolvePackageAssets": "true",
        "CustomAfterMicrosoftCommonTargets": str(fixture),
        "NativeIntermediateOutputPath": directory.as_posix() + "/",
        "UnityGcProbeBinary": str(binary),
        "UnityGcProbeResponseFile": str(rsp),
    }
    args = [
        "dotnet", "msbuild", str(PROJECT), "-nologo", "-nodeReuse:false",
        "-getItem:NativeLibrary,IlcArg,RuntimeHostConfigurationOption",
        "-getProperty:ServerGarbageCollection,FullRuntimeName,RuntimeIdentifier,PublishAot",
    ]
    if response:
        args.append("-t:ResolveFrameworkReferences;WriteIlcRspFileForCompilation")
    args.extend(f"-p:{name}={value}" for name, value in props.items())
    env = dict(os.environ)
    env.update(MSBUILDDISABLENODEREUSE="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", DOTNET_NOLOGO="1")
    completed = subprocess.run(args, cwd=ROOT, env=env, capture_output=True, text=True, timeout=90)
    (directory / "command.json").write_text(json.dumps(args, indent=2), encoding="utf-8")
    (directory / "sdk-output.json").write_text(completed.stdout, encoding="utf-8")
    (directory / "sdk-error.log").write_text(completed.stderr, encoding="utf-8")
    if completed.returncode:
        raise AssertionError(completed.stdout + completed.stderr)
    data = json.loads(completed.stdout)
    if response and not rsp.is_file():
        raise AssertionError(f"actual SDK target did not emit {rsp}")
    return data, rsp.read_text(encoding="utf-8-sig").splitlines() if response else []


@unittest.skipUnless(OPT_IN, "opt-in: set UNPACKRAT_RUN_NATIVE_GC_TESTS=1")
@unittest.skipUnless(os.name == "nt", "Windows NativeAOT SDK contract")
class NativeGcSdkContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if shutil.which("dotnet") is None:
            raise unittest.SkipTest("installed dotnet SDK is required")

    def test_win_x64_links_exactly_the_static_server_gc_runtime(self):
        data, rsp = sdk_probe("win-x64", "win-x64")
        items = data["Items"]
        runtime_libraries = [
            item for item in items["NativeLibrary"]
            if Path(item["Identity"]).name.startswith("Runtime.")
            and "GC.lib" in Path(item["Identity"]).name
        ]
        self.assertEqual(
            [Path(item["Identity"]).name for item in runtime_libraries],
            ["Runtime.ServerGC.lib"],
            "the real SDK must link exactly its static Server GC runtime",
        )
        self.assertTrue(Path(runtime_libraries[0]["Identity"]).is_file(), "installed runtime library missing")
        self.assertEqual(Path(runtime_libraries[0]["DefiningProjectFullPath"]).name, "Microsoft.NETCore.Native.Windows.targets")
        self.assertEqual(data["Properties"]["ServerGarbageCollection"], "true")
        expected = "--runtimeopt:gcServer=1"
        matches = [item for item in items["IlcArg"] if item["Identity"] == expected]
        self.assertEqual(len(matches), 1, expected)
        self.assertEqual(Path(matches[0]["DefiningProjectFullPath"]).name, "Microsoft.NETCore.Native.targets")
        self.assertEqual(rsp.count(expected), 1, "real SDK response file must carry the same flag")

    def test_win_x64_embeds_adaptive_conservation_without_a_fixed_heap_count(self):
        data, rsp = sdk_probe("win-x64-adaptive-conservation", "win-x64")
        items = data["Items"]
        expected_options = {
            "System.GC.MaxHeapCount": "4",
            "System.GC.DynamicAdaptationMode": "1",
            "System.GC.ConserveMemory": "9",
        }
        options = [
            (item["Identity"], item["Value"])
            for item in items["RuntimeHostConfigurationOption"]
            if item["Identity"] in GC_POLICY_OPTIONS
        ]
        self.assertCountEqual(options, expected_options.items(), "a fixed HeapCount disables dynamic adaptation")
        expected_flags = [f"--runtimeknob:{name}={value}" for name, value in expected_options.items()]
        policy_prefixes = tuple(f"--runtimeknob:{name}=" for name in GC_POLICY_OPTIONS)
        self.assertCountEqual(
            [item["Identity"] for item in items["IlcArg"] if item["Identity"].startswith(policy_prefixes)],
            expected_flags,
        )
        self.assertCountEqual([line for line in rsp if line.startswith(policy_prefixes)], expected_flags)
        for expected in expected_flags:
            matches = [item for item in items["IlcArg"] if item["Identity"] == expected]
            self.assertEqual(len(matches), 1, expected)
            self.assertEqual(Path(matches[0]["DefiningProjectFullPath"]).name, "Microsoft.NETCore.Native.targets")
            self.assertEqual(rsp.count(expected), 1, "real SDK response file must carry the same flag")

    def test_win_x86_keeps_workstation_gc_without_heap_override(self):
        data, rsp = sdk_probe("win-x86", "win-x86")
        names = [Path(item["Identity"]).name for item in data["Items"]["NativeLibrary"]]
        self.assertEqual(names.count("Runtime.WorkstationGC.lib"), 1)
        self.assertNotIn("Runtime.ServerGC.lib", names)
        self.assert_not_configured(data, rsp)

    def test_managed_windows_build_keeps_default_gc_configuration(self):
        data, rsp = sdk_probe("managed-win-x64", "win-x64", native=False, response=False)
        self.assert_not_configured(data, rsp)

    def test_static_native_library_keeps_default_gc_configuration(self):
        data, rsp = sdk_probe("static-win-x64", "win-x64", library="Static")
        self.assert_not_configured(data, rsp)

    def test_nonwindows_native_evaluation_keeps_default_gc_configuration(self):
        # Evaluation only: a Windows developer need not acquire a Linux runtime pack.
        data, rsp = sdk_probe("linux-x64", "linux-x64", response=False)
        self.assert_not_configured(data, rsp)

    def assert_not_configured(self, data, rsp):
        self.assertNotEqual(data["Properties"]["ServerGarbageCollection"].lower(), "true")
        self.assertFalse(any(item["Identity"] in GC_POLICY_OPTIONS for item in data["Items"]["RuntimeHostConfigurationOption"]))
        policy_prefixes = tuple(f"--runtimeknob:{name}=" for name in GC_POLICY_OPTIONS)
        self.assertFalse(any(item["Identity"] == "--runtimeopt:gcServer=1" or item["Identity"].startswith(policy_prefixes) for item in data["Items"]["IlcArg"]))
        self.assertFalse(any(line == "--runtimeopt:gcServer=1" or line.startswith(policy_prefixes) for line in rsp))


if __name__ == "__main__":
    if not OPT_IN:
        raise SystemExit("opt-in test: set UNPACKRAT_RUN_NATIVE_GC_TESTS=1 (needs installed Windows dotnet SDK and cached NativeAOT packs)")
    unittest.main(verbosity=2)
