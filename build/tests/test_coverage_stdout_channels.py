"""Coverage export output channels must keep machine data separate from diagnostics.

The real llvm-cov export writes JSON or LCOV to stdout and may write warnings to
stderr.  ``main._coverage`` parses stdout as data, so stderr must stay out of the
captured machine stream even while remaining visible to a human.
"""

from __future__ import annotations

import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest import mock


BUILD_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BUILD_ROOT))
sys.path.insert(0, str(BUILD_ROOT / "tests"))

import main  # noqa: E402
import test_main_cli  # noqa: E402


_MISMATCH_WARNING = "warning: 4 functions have mismatched data\n"
_MACHINE_PAYLOAD = '{"machine": true}\n'
_HUMAN_DIAGNOSTIC = "warning: inherited diagnostic\n"


class CoverageExportBoundary:
    """A ``subprocess.run`` boundary that preserves the requested stream routing."""

    def __init__(self, json_payload: str, lcov_payload: str, warning: str = _MISMATCH_WARNING):
        self.json_payload = json_payload
        self.lcov_payload = lcov_payload
        self.warning = warning
        self.calls: list[tuple[tuple[str, ...], dict[str, object]]] = []
        self.inherited_warnings: list[str] = []

    def _payload(self, argv: tuple[str, ...]) -> str:
        if any(argument == "--format=lcov" for argument in argv):
            return self.lcov_payload
        return self.json_payload

    def __call__(self, argv, **kwargs):
        command = tuple(str(item) for item in argv)
        self.calls.append((command, kwargs))
        if "llvm-cov" not in os.path.basename(command[0]).lower():
            return subprocess.CompletedProcess(command, 0, None, None)

        payload = self._payload(command)
        if kwargs.get("stdout") is subprocess.PIPE:
            if kwargs.get("stderr") is subprocess.STDOUT:
                return subprocess.CompletedProcess(command, 0, self.warning + payload, None)
            if kwargs.get("stderr") is None:
                self.inherited_warnings.append(self.warning)
                sys.stderr.write(self.warning)
                sys.stderr.flush()
                return subprocess.CompletedProcess(command, 0, payload, None)
            raise AssertionError(f"unexpected captured stderr routing: {kwargs.get('stderr')!r}")
        return subprocess.CompletedProcess(command, 0, None, None)


class RoutingFakeRun(test_main_cli.FakeRun):
    """Fake build steps, but let real ``main.run`` execute every llvm-cov call."""

    def __init__(self, real_run):
        super().__init__()
        self.real_run = real_run
        self.export_merge_flags: list[bool] = []

    def __call__(self, argv, cwd=None, env=None, capture=False, *, merge_stderr=True):
        command = tuple(str(item) for item in argv)
        if "llvm-cov" in os.path.basename(command[0]).lower():
            self.calls.append(test_main_cli.Recorded(command, cwd, env, capture))
            self.export_merge_flags.append(merge_stderr)
            if merge_stderr:
                return self.real_run(command, cwd=cwd, env=env, capture=capture)
            return self.real_run(
                command, cwd=cwd, env=env, capture=capture, merge_stderr=False
            )
        return super().__call__(argv, cwd=cwd, env=env, capture=capture)


class StreamRoutingBoundary:
    """A faithful harmless child boundary for the ``main.run`` routing table."""

    def __init__(self):
        self.calls: list[dict[str, object]] = []

    def __call__(self, argv, **kwargs):
        command = tuple(str(item) for item in argv)
        self.calls.append(
            {
                "argv": command,
                "stdout": kwargs.get("stdout"),
                "stderr": kwargs.get("stderr"),
            }
        )
        if kwargs.get("stdout") is subprocess.PIPE:
            if kwargs.get("stderr") is subprocess.STDOUT:
                return subprocess.CompletedProcess(
                    command, 0, _HUMAN_DIAGNOSTIC + _MACHINE_PAYLOAD, None
                )
            if kwargs.get("stderr") is None:
                sys.stderr.write(_HUMAN_DIAGNOSTIC)
                sys.stderr.flush()
                return subprocess.CompletedProcess(command, 0, _MACHINE_PAYLOAD, None)
            raise AssertionError(f"unexpected captured stderr routing: {kwargs.get('stderr')!r}")
        if kwargs.get("stdout") is None and kwargs.get("stderr") is None:
            sys.stdout.write(_MACHINE_PAYLOAD)
            sys.stderr.write(_HUMAN_DIAGNOSTIC)
            sys.stdout.flush()
            sys.stderr.flush()
            return subprocess.CompletedProcess(command, 0, None, None)
        raise AssertionError(f"unexpected inherited stream routing: {kwargs!r}")


class CoverageStdoutChannelCase(unittest.TestCase):
    def setUp(self) -> None:
        test_main_cli.MainCliCase.setUp(self)

    def _coverage_fixture(self) -> tuple[RoutingFakeRun, CoverageExportBoundary]:
        real_run = main.run
        boundary = CoverageExportBoundary(
            json.dumps(test_main_cli._FULL_COVERAGE), test_main_cli._LCOV
        )
        run = RoutingFakeRun(real_run)
        return run, boundary

    def test_coverage_exports_parse_machine_stdout_and_keep_diagnostics(self) -> None:
        run, boundary = self._coverage_fixture()
        inherited_stderr = io.StringIO()

        with (
            mock.patch.object(subprocess, "run", boundary),
            contextlib.redirect_stderr(inherited_stderr),
            test_main_cli.MainCliCase.tools(self, run),
        ):
            summary = main._coverage(
                self.repo,
                "x64",
                self.root / "MSBuild.exe",
                self.root / "vcpkg.exe",
                self.llvm,
                jobs=2,
            )

        self.assertEqual(
            json.loads(summary.read_text(encoding="utf-8")),
            test_main_cli._FULL_COVERAGE,
        )
        lcov = main.artifacts_root(self.repo) / "coverage" / "x64" / "coverage.lcov"
        self.assertEqual(lcov.read_text(encoding="utf-8"), test_main_cli._LCOV)
        self.assertEqual(run.export_merge_flags, [False, False])
        self.assertEqual(boundary.inherited_warnings, [_MISMATCH_WARNING, _MISMATCH_WARNING])
        self.assertEqual(inherited_stderr.getvalue(), _MISMATCH_WARNING * 2)

    def _run_with_streams(self, **kwargs):
        boundary = StreamRoutingBoundary()
        stdout = io.StringIO()
        stderr = io.StringIO()
        with (
            mock.patch.object(subprocess, "run", boundary),
            contextlib.redirect_stdout(stdout),
            contextlib.redirect_stderr(stderr),
        ):
            completed = main.run(["harmless-child"], **kwargs)
        return completed, boundary, stdout.getvalue(), stderr.getvalue()

    def test_run_merge_stderr_decision_table(self) -> None:
        completed, boundary, stdout, stderr = self._run_with_streams(
            capture=True, merge_stderr=False
        )
        self.assertEqual(completed.stdout, _MACHINE_PAYLOAD)
        self.assertEqual(json.loads(completed.stdout), {"machine": True})
        self.assertEqual(stdout, "")
        self.assertEqual(stderr, _HUMAN_DIAGNOSTIC)
        self.assertIs(boundary.calls[-1]["stdout"], subprocess.PIPE)
        self.assertIsNone(boundary.calls[-1]["stderr"])

        completed, boundary, stdout, stderr = self._run_with_streams(capture=True)
        self.assertEqual(completed.stdout, _HUMAN_DIAGNOSTIC + _MACHINE_PAYLOAD)
        self.assertEqual(stdout, "")
        self.assertEqual(stderr, "")
        self.assertIs(boundary.calls[-1]["stdout"], subprocess.PIPE)
        self.assertIs(boundary.calls[-1]["stderr"], subprocess.STDOUT)

        for merge_stderr in (True, False):
            with self.subTest(capture=False, merge_stderr=merge_stderr):
                completed, boundary, stdout, stderr = self._run_with_streams(
                    capture=False, merge_stderr=merge_stderr
                )
                self.assertIsNone(completed.stdout)
                self.assertEqual(json.loads(stdout), {"machine": True})
                self.assertEqual(stdout, _MACHINE_PAYLOAD)
                self.assertEqual(stderr, _HUMAN_DIAGNOSTIC)
                self.assertIsNone(boundary.calls[-1]["stdout"])
                self.assertIsNone(boundary.calls[-1]["stderr"])


if __name__ == "__main__":
    unittest.main()
