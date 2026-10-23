#!/usr/bin/env python3
"""Opt-in MSBuild dependency-contract regression for Directory.Build.targets.

This is NOT a NativeAOT build and NOT a unit test of the four native modules.
It is a focused dependency-contract check: it instantiates a tiny project that
imports the REAL production src/modules/unity/Directory.Build.targets,
supplies a dummy SetupOSSpecificProps mirroring the SDK seam those targets hook
(Microsoft.NETCore.Native.Windows.targets expands @(NativeLibrary->'"%(Identity)"')
into @(LinkerArg)), and a LinkNative target that uses the SDK's exact incremental
input contract, read from the installed primaries:

    Microsoft.NETCore.Native.targets
    <Target Name="LinkNative" Inputs="$(NativeObject);@(NativeLibrary)"
            Outputs="$(NativeBinary)">

The contract under test: changing an external native input (the Observer static
library or the YY-Thunks object) must refresh the linked output, and an unchanged
input must not. The real both-arch publish and the large-game qualification stay
with ROOT; this harness only proves the MSBuild dependency wiring.

Opt-in (needs an installed dotnet SDK):
    UNPACKRAT_RUN_NATIVE_LINK_TESTS=1 python src/modules/unity/tests/test_native_link.py
"""

import os
import shutil
import subprocess
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
PRODUCTION_TARGETS = ROOT / "src" / "modules" / "unity" / "Directory.Build.targets"
WORK_ROOT = ROOT / "out" / "unity-features" / "incremental-link"

OPT_IN = os.environ.get("UNPACKRAT_RUN_NATIVE_LINK_TESTS") == "1"

# The dummy SetupOSSpecificProps reproduces the SDK's NativeLibrary -> LinkerArg
# expansion; LinkNative carries the SDK's verbatim Inputs/Outputs contract. Both
# the input registration records and a per-run tick make "did the link run" a
# behavioral observation, not a source-text check.
PROJECT_TEMPLATE = """<Project>
  <Target Name="SetupOSSpecificProps">
    <ItemGroup>
      <LinkerArg Include="@(NativeLibrary->'&quot;%(Identity)&quot;')" />
    </ItemGroup>
  </Target>

  <Target Name="LinkNative"
          DependsOnTargets="SetupOSSpecificProps"
          Inputs="$(NativeObject);@(NativeLibrary)"
          Outputs="$(NativeBinary)">
    <ItemGroup>
      <_LinkRecord Include="object=$(NativeObject)" />
      <_LinkRecord Include="@(NativeLibrary->'native-library=%(Identity)')" />
      <_LinkRecord Include="@(LinkerArg->'linker-arg=%(Identity)')" />
      <_LinkRecord Include="run=$([System.DateTime]::UtcNow.Ticks)" />
    </ItemGroup>
    <WriteLinesToFile File="$(NativeBinary)" Lines="@(_LinkRecord)" Overwrite="true" />
    <WriteLinesToFile File="$(NativeBinary).runs" Lines="run" Overwrite="false" />
  </Target>

  <Import Project="{production}" />
</Project>
"""


def _msbuild(project: Path, props: dict) -> subprocess.CompletedProcess:
    args = [
        "dotnet",
        "msbuild",
        str(project),
        "-t:LinkNative",
        "-nologo",
        "-v:m",
        "-nodeReuse:false",
        "-p:UseSharedCompilation=false",
    ]
    for key, value in props.items():
        args.append(f"-p:{key}={value}")
    env = dict(os.environ)
    env["MSBUILDDISABLENODEREUSE"] = "1"
    env["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0"
    env["DOTNET_NOLOGO"] = "1"
    return subprocess.run(args, capture_output=True, text=True, env=env, cwd=str(project.parent))


class LinkCase:
    """One isolated project under out/unity-features/incremental-link with inputs
    whose paths deliberately contain spaces, so command quoting is exercised."""

    def __init__(self, name: str, arch: str):
        self.dir = WORK_ROOT / name / arch
        if self.dir.exists():
            shutil.rmtree(self.dir)
        deps = self.dir / "deps with spaces"
        deps.mkdir(parents=True)
        self.observer = deps / "UnityObserver.lib"
        self.yy = deps / "YY_Thunks_for_WinXP.obj"
        self.observer.write_bytes(b"OBSERVER-v1\n")
        self.yy.write_bytes(b"YY-v1\n")
        self.obj = self.dir / "native object.obj"
        self.obj.write_bytes(b"OBJECT-v1\n")
        self.binary = self.dir / "linked output.bin"
        self.runs = Path(str(self.binary) + ".runs")
        self.project = self.dir / "link-dependency.proj"
        self.project.write_text(
            PROJECT_TEMPLATE.format(production=PRODUCTION_TARGETS), encoding="utf-8"
        )
        self.arch = arch
        self.last = None

    def props(self, **overrides) -> dict:
        props = {
            "PublishAot": "true",
            "_targetOS": "win",
            "_targetArchitecture": self.arch,
            "NativeLib": "Shared",
            "UnityYYThunksObject": str(self.yy),
            "UnityObserverLibrary": str(self.observer),
            "NativeObject": str(self.obj),
            "NativeBinary": str(self.binary),
        }
        props.update(overrides)
        return props

    def run(self, **overrides) -> subprocess.CompletedProcess:
        self.last = _msbuild(self.project, self.props(**overrides))
        return self.last

    def ok(self) -> None:
        assert self.last is not None and self.last.returncode == 0, (
            self.last.stdout if self.last else "no run"
        ) + (self.last.stderr if self.last else "")

    def run_count(self) -> int:
        if not self.runs.exists():
            return 0
        return len([line for line in self.runs.read_text().splitlines() if line.strip()])

    def output(self):
        return self.binary.read_bytes() if self.binary.exists() else None

    def output_text(self) -> str:
        return "" if not self.binary.exists() else self.binary.read_text(errors="replace")


def _settle() -> None:
    """Make a just-written input strictly newer than the previous link output."""
    time.sleep(1.1)


@unittest.skipUnless(OPT_IN, "opt-in: set UNPACKRAT_RUN_NATIVE_LINK_TESTS=1")
class NativeLinkDependencyContractTest(unittest.TestCase):
    maxDiff = None

    def test_changed_observer_library_refreshes_link(self):
        for arch in ("x64", "x86"):
            with self.subTest(arch=arch):
                case = LinkCase("observer-change", arch)
                case.run()
                case.ok()
                first = case.output()
                runs_after_first = case.run_count()
                self.assertIn("UnityObserver.lib", case.output_text())
                self.assertEqual(runs_after_first, 1)

                _settle()
                case.run()  # nothing changed
                case.ok()
                self.assertEqual(case.run_count(), runs_after_first, "unchanged inputs must not relink")
                self.assertEqual(case.output(), first, "unchanged inputs must not rewrite the output")

                _settle()
                case.observer.write_bytes(b"OBSERVER-v2\n")
                case.run()
                case.ok()
                self.assertEqual(
                    case.run_count(), runs_after_first + 1, "a changed Observer library must relink"
                )
                self.assertNotEqual(case.output(), first, "a changed Observer library must refresh the output")

    def test_changed_yy_thunks_object_refreshes_link(self):
        for arch in ("x64", "x86"):
            with self.subTest(arch=arch):
                case = LinkCase("yy-change", arch)
                case.run()
                case.ok()
                first = case.output()
                runs_after_first = case.run_count()
                self.assertIn("YY_Thunks_for_WinXP.obj", case.output_text())

                _settle()
                case.yy.write_bytes(b"YY-v2\n")
                case.run()
                case.ok()
                self.assertEqual(
                    case.run_count(), runs_after_first + 1, "a changed YY-Thunks object must relink"
                )
                self.assertNotEqual(case.output(), first, "a changed YY-Thunks object must refresh the output")

    def test_unchanged_inputs_do_not_relink(self):
        case = LinkCase("no-op", "x64")
        case.run()
        case.ok()
        first = case.output()
        for _ in range(2):
            _settle()
            case.run()
            case.ok()
            self.assertEqual(case.run_count(), 1, "an up-to-date link must not run again")
            self.assertEqual(case.output(), first, "an up-to-date link must not rewrite the output")

    def test_each_input_contributes_exactly_one_linker_arg(self):
        case = LinkCase("no-duplicate-arg", "x64")
        case.run()
        case.ok()
        lines = case.output_text().splitlines()
        # The library path must be registered once as a tracking input and once as
        # a linker argument. Exact-identity comparison keeps the /ENTRY and
        # /ALTERNATENAME flags that merely mention YY_Thunks out of the count.
        self.assertEqual(lines.count(f"native-library={case.observer}"), 1)
        self.assertEqual(lines.count(f"native-library={case.yy}"), 1)
        self.assertEqual(lines.count(f'linker-arg="{case.observer}"'), 1)
        self.assertEqual(lines.count(f'linker-arg="{case.yy}"'), 1)

    def test_scope_guards_leave_inputs_untracked(self):
        guards = {
            "managed": {"PublishAot": "false"},
            "nonwindows": {"_targetOS": "linux"},
            "static-native-lib": {"NativeLib": "Static"},
            "unsupported-arch": {"_targetArchitecture": "arm64"},
        }
        for name, override in guards.items():
            with self.subTest(guard=name):
                case = LinkCase(f"scope-{name}", "x64")
                case.run(**override)
                case.ok()
                text = case.output_text()
                self.assertNotIn("UnityObserver.lib", text)
                self.assertNotIn("YY_Thunks", text)

    def test_missing_input_paths_fail_closed_before_link(self):
        observer_case = LinkCase("missing-observer", "x64")
        observer_case.run(UnityObserverLibrary=str(observer_case.dir / "absent" / "UnityObserver.lib"))
        self.assertNotEqual(observer_case.last.returncode, 0)
        self.assertIn("does not exist", observer_case.last.stdout + observer_case.last.stderr)

        yy_case = LinkCase("missing-yy", "x64")
        yy_case.run(UnityYYThunksObject=str(yy_case.dir / "absent" / "YY_Thunks_for_WinXP.obj"))
        self.assertNotEqual(yy_case.last.returncode, 0)
        self.assertIn("does not exist", yy_case.last.stdout + yy_case.last.stderr)


if __name__ == "__main__":
    if not OPT_IN:
        raise SystemExit(
            "opt-in test: set UNPACKRAT_RUN_NATIVE_LINK_TESTS=1 to run the MSBuild "
            "link dependency-contract check (needs an installed dotnet SDK)"
        )
    WORK_ROOT.mkdir(parents=True, exist_ok=True)
    unittest.main(verbosity=2)
