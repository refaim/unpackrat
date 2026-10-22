"""Coverage instrumentation must update profile counters atomically.

The ordinary filename tests open the archive concurrently.  Non-atomic profile
counters can lose increments under that contention, so the Coverage build must
compile with Clang's atomic profile updates.
"""

from pathlib import Path
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main


ROOT = Path(__file__).resolve().parents[2]
PROBE_SOURCE = """\
int probe(int value) { return value == 7 ? 1 : 0; }
int main() { return probe(1); }
"""
PROFILE_OPTIONS = (
    "/clang:-fprofile-instr-generate",
    "/clang:-fcoverage-mapping",
)
ATOMIC_UPDATE_OPTION = "/clang:-fprofile-update=atomic"
PLATFORM_TRIPLES = {
    "Win32": "i686-pc-windows-msvc",
    "x64": "x86_64-pc-windows-msvc",
}


def _probe_compiler() -> Path:
    """Select the pinned clang-cl from its override or the pinned LLVM tree."""

    version_pattern = (
        rf"(?m)^\s*clang version {re.escape(main.LLVM_VERSION)}"
        r"(?:\s+\([^)\n]*\))?\s*$"
    )
    if not os.environ.get("UNPACKRAT_CLANG_CL"):
        os.environ["UNPACKRAT_CLANG_CL"] = str(
            main._clang_format().with_name("clang-cl.exe")
        )
        try:
            return main._standalone_tool(
                "UNPACKRAT_CLANG_CL", "clang-cl.exe", version_pattern
            )
        finally:
            del os.environ["UNPACKRAT_CLANG_CL"]
    return main._standalone_tool("UNPACKRAT_CLANG_CL", "clang-cl.exe", version_pattern)


def _cl_compile_options(
    msbuild: Path, project: Path, platform: str, configuration: str
) -> list[str]:
    result = subprocess.run(
        [
            str(msbuild),
            str(project),
            "-nologo",
            "-getItem:ClCompile",
            f"-p:Platform={platform}",
            f"-p:Configuration={configuration}",
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=True,
    )
    item = json.loads(result.stdout)["Items"]["ClCompile"][0]
    return item["AdditionalOptions"].split()


class CoverageCounterUpdateTests(unittest.TestCase):
    def test_real_msbuild_coverage_options_emit_atomic_profile_counters(self):
        msbuild = main._find_msbuild()
        compiler = _probe_compiler()
        with tempfile.TemporaryDirectory() as temporary:
            project = Path(temporary) / "coverage-metadata.proj"
            project.write_text(
                '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">'
                f'<Import Project="{ROOT / "build/unpackrat-project.props"}" />'
                '<ItemGroup><ClCompile Include="probe.cpp" /></ItemGroup></Project>',
                encoding="utf-8",
            )
            for platform, triple in PLATFORM_TRIPLES.items():
                with self.subTest(platform=platform):
                    options = _cl_compile_options(msbuild, project, platform, "Coverage")
                    for option in PROFILE_OPTIONS:
                        self.assertIn(option, options)
                    result = subprocess.run(
                        [
                            str(compiler),
                            "/nologo",
                            "/TP",
                            "-",
                            *options,
                            f"/clang:--target={triple}",
                            "/clang:-S",
                            "/clang:-emit-llvm",
                            "/clang:-o",
                            "/clang:-",
                        ],
                        input=PROBE_SOURCE,
                        capture_output=True,
                        text=True,
                        encoding="utf-8",
                    )
                    self.assertEqual(result.returncode, 0, result.stderr)
                    ir = result.stdout
                    counter_updates = [
                        line.strip()
                        for line in ir.splitlines()
                        if "__profc" in line and "private global" not in line
                    ]
                    self.assertTrue(counter_updates, "no profile counter updates were emitted")
                    for line in counter_updates:
                        self.assertRegex(
                            line,
                            r"^%\d+ = atomicrmw add ptr .*__profc_",
                            f"non-atomic profile counter update:\n" + "\n".join(counter_updates),
                        )

    def test_only_coverage_receives_profile_instrumentation(self):
        msbuild = main._find_msbuild()
        with tempfile.TemporaryDirectory() as temporary:
            project = Path(temporary) / "coverage-metadata.proj"
            project.write_text(
                '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">'
                f'<Import Project="{ROOT / "build/unpackrat-project.props"}" />'
                '<ItemGroup><ClCompile Include="probe.cpp" /></ItemGroup></Project>',
                encoding="utf-8",
            )
            for platform in PLATFORM_TRIPLES:
                for configuration in ("Debug", "Release", "ASan", "Fuzz", "UBSan"):
                    if configuration == "UBSan" and platform != "x64":
                        continue
                    with self.subTest(platform=platform, configuration=configuration):
                        options = _cl_compile_options(msbuild, project, platform, configuration)
                        for option in (*PROFILE_OPTIONS, ATOMIC_UPDATE_OPTION):
                            self.assertNotIn(option, options)


class ProbeCompilerSelectionTests(unittest.TestCase):
    def _version_run(self, output: str) -> mock.Mock:
        run = mock.Mock(return_value=subprocess.CompletedProcess([], 0, output, ""))
        return run

    def test_wrong_compiler_version_at_subprocess_boundary_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            bin_dir = Path(temporary) / "bin"
            bin_dir.mkdir()
            formatter = bin_dir / "clang-format.exe"
            formatter.touch()
            (bin_dir / "clang-cl.exe").touch()
            version = "clang version 23.1.2\nTarget: x86_64-pc-windows-msvc\n"
            with (
                mock.patch.dict(os.environ, {}, clear=True),
                mock.patch.object(main, "_clang_format", return_value=formatter),
                mock.patch.object(
                    main, "_vs_installation", return_value=Path(temporary) / "vs"
                ),
                mock.patch("subprocess.run", self._version_run(version)),
            ):
                with self.assertRaisesRegex(main.BuildError, r"23\.1\.3"):
                    _probe_compiler()

    def test_explicit_override_selects_its_compiler(self):
        with tempfile.TemporaryDirectory() as temporary:
            formatter = Path(temporary) / "pinned" / "clang-format.exe"
            formatter.parent.mkdir()
            formatter.touch()
            (formatter.parent / "clang-cl.exe").touch()
            override = Path(temporary) / "clang-cl.exe"
            override.touch()
            version = "clang version 23.1.3\nTarget: x86_64-pc-windows-msvc\n"
            with (
                mock.patch.dict(
                    os.environ, {"UNPACKRAT_CLANG_CL": str(override)}, clear=True
                ),
                mock.patch.object(main, "_clang_format", return_value=formatter),
                mock.patch.object(
                    main, "_vs_installation", return_value=Path(temporary) / "vs"
                ),
                mock.patch("subprocess.run", self._version_run(version)),
            ):
                self.assertEqual(_probe_compiler(), override.resolve())

    def test_missing_sibling_does_not_fall_back_to_visual_studio(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            formatter = root / "bin" / "clang-format.exe"
            formatter.parent.mkdir()
            formatter.touch()
            vs_compiler = (
                root / "vs" / "VC" / "Tools" / "Llvm" / "x64" / "bin" / "clang-cl.exe"
            )
            vs_compiler.parent.mkdir(parents=True)
            vs_compiler.touch()
            version = "clang version 23.1.3\nTarget: x86_64-pc-windows-msvc\n"
            with (
                mock.patch.dict(os.environ, {}, clear=True),
                mock.patch.object(main, "_clang_format", return_value=formatter),
                mock.patch.object(main, "_vs_installation", return_value=root / "vs"),
                mock.patch("subprocess.run", self._version_run(version)),
            ):
                with self.assertRaisesRegex(FileNotFoundError, "clang-cl.exe"):
                    _probe_compiler()


if __name__ == "__main__":
    unittest.main()
