"""CodeQL observes the real native and Unity builds, including pinned project references."""

from __future__ import annotations

import os
from pathlib import Path
import subprocess
import textwrap
import unittest

from test_native_job_decomposition import job_blocks, step_block


WORKFLOWS = Path(__file__).resolve().parents[2] / ".github" / "workflows"
CODEQL_REVISION = "24c54180a607b1449ed407dd24f251e4e9147c8d"


class CodeQlWorkflowTests(unittest.TestCase):
    def setUp(self) -> None:
        self.main = (WORKFLOWS / "main.yml").read_text(encoding="utf-8")
        self.check = (WORKFLOWS / "ci-check.yml").read_text(encoding="utf-8")

    def test_scanning_is_opted_into_by_only_source_and_x64_debug_callers(self) -> None:
        self.assertIn("      codeql:\n", self.check)
        declaration = self.check.split("      codeql:\n", 1)[1].split("\nenv:", 1)[0]
        self.assertIn("        type: boolean", declaration)
        self.assertIn("        default: false", declaration)
        callers = job_blocks(self.main)
        selected = [name for name, lines in callers.items() if "      codeql: true" in lines]
        self.assertEqual(selected, ["source-checks", "native-debug-x64"])
        for name, stage, arch in (("source-checks", "source", "none"),
                                  ("native-debug-x64", "native-debug", "x64")):
            with self.subTest(caller=name):
                caller = "\n".join(callers[name])
                self.assertIn("      security-events: write", caller)
                self.assertIn(f"      stage: {stage}", caller)
                self.assertIn(f"      arch: {arch}", caller)

    def test_compiled_init_traces_only_cpp_and_csharp_with_manual_build_mode(self) -> None:
        initialize = step_block(self.check, "Initialize CodeQL for the real builds")
        self.assertIn("          languages: cpp,csharp\n", initialize)
        self.assertIn("if: inputs.codeql && inputs.stage == 'native-debug'", initialize)
        self.assertIn(f"uses: github/codeql-action/init@{CODEQL_REVISION}", initialize)
        self.assertIn("build-mode: manual", initialize)
        self.assertNotIn("python", initialize)
        self.assertNotIn("languages: cpp,csharp,python,actions", self.check)
        self.assertLess(self.check.index(initialize),
                        self.check.index("      - name: Verify one stage for one architecture"))

    def test_interpreted_init_uses_no_build_mode_for_python_and_actions(self) -> None:
        initialize = step_block(self.check, "Initialize CodeQL for Python and Actions")
        self.assertIn("if: inputs.codeql && inputs.stage == 'source'", initialize)
        self.assertIn(f"uses: github/codeql-action/init@{CODEQL_REVISION}", initialize)
        self.assertIn("          languages: python,actions\n", initialize)
        self.assertIn("build-mode: none", initialize)
        self.assertNotIn("build-mode: manual", initialize)
        self.assertLess(self.check.index(initialize),
                        self.check.index("      - name: Verify the repository sources"))
        self.assertLess(self.check.index("      - name: Verify the repository sources"),
                        self.check.index("      - name: Analyze the CodeQL databases"))

    def test_stage_and_architecture_give_each_language_group_a_distinct_analysis_category(self) -> None:
        analyze = step_block(self.check, "Analyze the CodeQL databases")
        # Both caller slices reach the reusable job named "check" with no matrix.
        # Its default category therefore collides even though their languages differ.
        self.assertIn("          category: unpackrat-${{ inputs.stage }}-${{ inputs.arch }}", analyze)

    def test_unity_uses_the_maintained_pinned_source_and_build_route_before_analysis(self) -> None:
        unity = step_block(self.check, "Build Unity with its pinned project references for CodeQL")
        self.assertIn("if: inputs.codeql && inputs.stage == 'native-debug'", unity)
        self.assertIn("GH_TOKEN: ${{ github.token }}", unity)
        self.assertIn("NUGET_PACKAGES: ${{ github.workspace }}/out/unity-nativeaot/nuget/packages", unity)
        self.assertIn("run: ./build.ps1 build -Module unity -Arch x64 -Config Release -Jobs 4", unity)
        self.assertNotIn("--no-dependencies", unity)
        self.assertLess(self.check.index("      - name: Verify one stage for one architecture"),
                        self.check.index(unity))
        analyze = step_block(self.check, "Analyze the CodeQL databases")
        self.assertLess(self.check.index(unity), self.check.index(analyze))
        self.assertIn("if: inputs.codeql", analyze)
        self.assertIn(f"uses: github/codeql-action/analyze@{CODEQL_REVISION}", analyze)
        self.assertNotIn("continue-on-error", analyze)

    def test_dotnet_preflight_uses_the_installed_sdk_without_provisioning(self) -> None:
        preflight = step_block(self.check, "Verify the installed .NET 10 SDK for CodeQL")
        self.assertIn("if: inputs.codeql && inputs.stage == 'native-debug'", preflight)
        self.assertIn("dotnet --list-sdks", preflight)
        self.assertIn(r"^10\.\d+\.\d+ ", preflight)
        self.assertIn("throw", preflight)
        self.assertNotIn("setup-dotnet", preflight)
        self.assertNotIn("dotnet-install", preflight)
        self.assertLess(self.check.index(preflight),
                        self.check.index("      - name: Initialize CodeQL for the real builds"))

    def test_managed_tests_remain_in_the_manual_csharp_analysis(self) -> None:
        tests = step_block(self.check, "Build the managed Unity tests for CodeQL")
        self.assertIn("if: inputs.codeql && inputs.stage == 'native-debug'", tests)
        self.assertIn("NUGET_PACKAGES: ${{ github.workspace }}/out/unity-nativeaot/nuget/packages", tests)
        self.assertIn("dotnet build src/modules/unity/UnityProbe.Tests/UnityProbe.Tests.csproj", tests)
        self.assertIn("--configuration Release -p:PublishAot=false", tests)
        self.assertLess(
            self.check.index("      - name: Build Unity with its pinned project references for CodeQL"),
            self.check.index(tests),
        )
        self.assertLess(self.check.index(tests),
                        self.check.index("      - name: Analyze the CodeQL databases"))

    def test_real_dotnet_preflight_accepts_only_a_successful_installed_net10_sdk(self) -> None:
        preflight = step_block(self.check, "Verify the installed .NET 10 SDK for CodeQL")
        body = textwrap.dedent(preflight.split("        run: |\n", 1)[1])
        script = """$ErrorActionPreference = 'Stop'
function dotnet {
    $global:LASTEXITCODE = [int]$env:FIXTURE_DOTNET_EXIT
    $env:FIXTURE_DOTNET_SDKS -split "`n"
}
""" + body
        cases = (
            ("10.0.100 [C:/dotnet/sdk]", 0, True),
            ("9.0.300 [C:/dotnet/sdk]\n10.0.101 [C:/dotnet/sdk]", 0, True),
            ("11.0.100 [C:/dotnet/sdk]", 0, False),
            ("9.0.300 [C:/dotnet/sdk]", 0, False),
            ("", 0, False),
            ("10.0.100 [C:/dotnet/sdk]", 1, False),
        )
        for sdks, exit_code, accepted in cases:
            with self.subTest(sdks=sdks, exit_code=exit_code):
                completed = subprocess.run(
                    ["pwsh", "-NoProfile", "-NonInteractive", "-Command", script],
                    env={**os.environ, "FIXTURE_DOTNET_SDKS": sdks,
                         "FIXTURE_DOTNET_EXIT": str(exit_code)},
                    check=False, capture_output=True, text=True,
                )
                self.assertEqual(completed.returncode == 0, accepted, completed.stderr)
                if not accepted:
                    self.assertIn("installed .NET 10 SDK", completed.stderr)

    def test_weekly_and_manual_runs_reuse_the_same_native_and_unity_scanning(self) -> None:
        self.assertIn("  workflow_dispatch:", self.main)
        scheduled = WORKFLOWS / "codeql-schedule.yml"
        self.assertTrue(scheduled.is_file(), "weekly CodeQL checks must survive leaving default setup")
        workflow = scheduled.read_text(encoding="utf-8")
        self.assertIn("  schedule:", workflow)
        self.assertIn("cron: '12 14 * * 2'", workflow)
        self.assertIn("  workflow_dispatch:", workflow)
        self.assertNotIn("push:", workflow)
        self.assertNotIn("pull_request:", workflow)
        jobs = job_blocks(workflow)
        self.assertEqual(list(jobs), ["codeql", "codeql-source"])
        for name, stage, arch in (("codeql", "native-debug", "x64"),
                                  ("codeql-source", "source", "none")):
            with self.subTest(caller=name):
                caller = "\n".join(jobs[name])
                self.assertIn("uses: ./.github/workflows/ci-check.yml", caller)
                self.assertIn("      contents: read", caller)
                self.assertIn("      security-events: write", caller)
                self.assertIn(f"      stage: {stage}", caller)
                self.assertIn(f"      arch: {arch}", caller)
                self.assertIn("      codeql: true", caller)
        self.assertIn("      id-token: write", "\n".join(jobs["codeql-source"]))
        self.assertNotIn("id-token:", "\n".join(jobs["codeql"]))


if __name__ == "__main__":
    unittest.main()
