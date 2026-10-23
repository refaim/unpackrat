"""Property-evaluation contract for the permanent Unity NativeAOT size defaults.

Set UNITY_PROBE_PROJECT to the UnityProbe.csproj under test. When unset, the
canonical repository-relative project path is resolved from this file's
repository. Set DOTNET to override the dotnet executable.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import unittest


PROJECT_RELATIVE = Path(
    "src/modules/unity/UnityProbe/UnityProbe.csproj"
)


def _project_path() -> Path:
    configured = os.environ.get("UNITY_PROBE_PROJECT")
    if configured:
        return Path(configured).expanduser().resolve()
    return (Path(__file__).resolve().parents[2] / PROJECT_RELATIVE).resolve()


def _dotnet_path() -> str:
    configured = os.environ.get("DOTNET")
    if configured:
        return configured
    canonical = Path(r"C:\Program Files\dotnet\dotnet.exe")
    if canonical.is_file():
        return str(canonical)
    found = shutil.which("dotnet")
    if found:
        return found
    raise RuntimeError("dotnet was not found; set DOTNET to its executable")


def _json_properties(output: str) -> dict[str, str]:
    """Read the JSON property document emitted by a multi-property query."""

    document = json.loads(output)
    properties = document.get("Properties") if isinstance(document, dict) else None
    if not isinstance(properties, dict):
        raise AssertionError(f"MSBuild output was not a property document:\n{output}")
    return {str(name): str(value) for name, value in properties.items()}


def evaluate_native_size_properties(
    project: Path, properties: dict[str, str]
) -> dict[str, str]:
    command = [
        _dotnet_path(),
        "msbuild",
        str(project),
        "-nologo",
        "-verbosity:quiet",
        "-getProperty:StackTraceSupport,OptimizationPreference,IlcDehydrate",
    ]
    command.extend(f"-p:{key}={value}" for key, value in properties.items())
    completed = subprocess.run(
        command,
        cwd=project.parent,
        check=False,
        capture_output=True,
        text=True,
        timeout=120,
    )
    if completed.returncode != 0:
        raise AssertionError(
            f"MSBuild property evaluation failed ({completed.returncode}).\n"
            f"stdout:\n{completed.stdout}\nstderr:\n{completed.stderr}"
        )
    evaluated = _json_properties(completed.stdout)
    required = {
        "StackTraceSupport",
        "OptimizationPreference",
        "IlcDehydrate",
    }
    missing = required.difference(evaluated)
    if missing:
        raise AssertionError(
            f"MSBuild output did not contain {sorted(missing)}:\n{completed.stdout}"
        )
    return {name: evaluated[name] for name in required}


NATIVE_RELEASE = {
    "Configuration": "Release",
    "PublishAot": "true",
    "NativeLib": "Shared",
}

DEFAULT_NATIVE_WINDOWS = {
    "StackTraceSupport": "false",
    "OptimizationPreference": "Size",
    "IlcDehydrate": "true",
}

UNCHANGED_NATIVE_DEFAULTS = {
    "StackTraceSupport": "",
    "OptimizationPreference": "",
    "IlcDehydrate": "",
}


class UnitySizeConfigurationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.project = _project_path()
        if not cls.project.is_file():
            raise RuntimeError(
                f"UnityProbe project not found at {cls.project}; "
                "set UNITY_PROBE_PROJECT"
            )

    def evaluate(self, **properties: str) -> dict[str, str]:
        return evaluate_native_size_properties(
            self.project, {**NATIVE_RELEASE, **properties}
        )

    def test_default_paths_and_version_resolve_from_the_module_directory(self) -> None:
        completed = subprocess.run(
            [_dotnet_path(), "msbuild", str(self.project), "-nologo", "-verbosity:quiet",
             "-getProperty:VendorRoot,BaseIntermediateOutputPath,BaseOutputPath,Version"],
            cwd=self.project.parent, capture_output=True, text=True, timeout=120,
        )
        self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
        properties = _json_properties(completed.stdout)
        repository = Path(__file__).resolve().parents[2]
        self.assertEqual(Path(properties["VendorRoot"]),
                         repository / "out/unity-nativeaot/vendor/AssetRipper/Source")
        self.assertEqual(Path(properties["BaseIntermediateOutputPath"]),
                         repository / "out/unity-nativeaot/obj/UnityProbe")
        self.assertEqual(Path(properties["BaseOutputPath"]),
                         repository / "out/unity-nativeaot/bin/UnityProbe")
        self.assertEqual(properties["Version"],
                         (repository / "src/modules/unity/VERSION").read_text().strip())

    def test_release_windows_x64_shared_aot_uses_size_defaults(self) -> None:
        self.assertEqual(
            DEFAULT_NATIVE_WINDOWS,
            self.evaluate(RuntimeIdentifier="win-x64"),
        )

    def test_release_windows_x86_shared_aot_uses_size_defaults(self) -> None:
        self.assertEqual(
            DEFAULT_NATIVE_WINDOWS,
            self.evaluate(RuntimeIdentifier="win-x86"),
        )

    def test_explicit_stack_trace_support_true_keeps_other_defaults(self) -> None:
        self.assertEqual(
            {**DEFAULT_NATIVE_WINDOWS, "StackTraceSupport": "true"},
            self.evaluate(
                RuntimeIdentifier="win-x64", StackTraceSupport="true"
            ),
        )

    def test_explicit_stack_trace_support_false_keeps_other_defaults(self) -> None:
        self.assertEqual(
            DEFAULT_NATIVE_WINDOWS,
            self.evaluate(
                RuntimeIdentifier="win-x64", StackTraceSupport="false"
            ),
        )

    def test_explicit_optimization_speed_keeps_other_defaults(self) -> None:
        self.assertEqual(
            {**DEFAULT_NATIVE_WINDOWS, "OptimizationPreference": "Speed"},
            self.evaluate(
                RuntimeIdentifier="win-x64", OptimizationPreference="Speed"
            ),
        )

    def test_explicit_optimization_size_keeps_other_defaults(self) -> None:
        self.assertEqual(
            DEFAULT_NATIVE_WINDOWS,
            self.evaluate(
                RuntimeIdentifier="win-x64", OptimizationPreference="Size"
            ),
        )

    def test_explicit_ilc_dehydrate_true_keeps_other_defaults(self) -> None:
        self.assertEqual(
            {**DEFAULT_NATIVE_WINDOWS, "IlcDehydrate": "true"},
            self.evaluate(RuntimeIdentifier="win-x64", IlcDehydrate="true"),
        )

    def test_explicit_ilc_dehydrate_false_keeps_other_defaults(self) -> None:
        self.assertEqual(
            {**DEFAULT_NATIVE_WINDOWS, "IlcDehydrate": "false"},
            self.evaluate(RuntimeIdentifier="win-x64", IlcDehydrate="false"),
        )

    def test_debug_native_windows_build_is_unchanged(self) -> None:
        self.assertEqual(
            UNCHANGED_NATIVE_DEFAULTS,
            self.evaluate(Configuration="Debug", RuntimeIdentifier="win-x64"),
        )

    def test_non_windows_native_release_is_unchanged(self) -> None:
        self.assertEqual(
            UNCHANGED_NATIVE_DEFAULTS,
            self.evaluate(RuntimeIdentifier="linux-x64"),
        )

    def test_managed_release_without_native_identity_is_unchanged(self) -> None:
        self.assertEqual(
            UNCHANGED_NATIVE_DEFAULTS,
            evaluate_native_size_properties(
                self.project,
                {"Configuration": "Release", "PublishAot": "false"},
            ),
        )

    def test_static_native_release_is_unchanged(self) -> None:
        self.assertEqual(
            UNCHANGED_NATIVE_DEFAULTS,
            self.evaluate(RuntimeIdentifier="win-x64", NativeLib="Static"),
        )


if __name__ == "__main__":
    unittest.main()
