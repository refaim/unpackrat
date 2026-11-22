"""The LLVM23 runtime supplement is source-bound and really diagnoses x86 inputs."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from test_ubsan_runtime_provisioning import PREP_WORKFLOW, function_runner


SOURCE_SHA = "d3b86ab29cfb4445a6652e5c2642ccc75e04e38084dde3701d323320f35a31da"
HOST_SHA = "7623f582089983c1cb51030cf1996514b19464e5669099571b399e8543770155"
LIBRARIES = ("clang_rt.profile-i386.lib", "clang_rt.fuzzer-i386.lib",
             "clang_rt.asan_dynamic-i386.lib", "clang_rt.asan_static_runtime_thunk-i386.lib",
             "clang_rt.asan_dynamic-i386.dll")


class RuntimeSupplementContracts(unittest.TestCase):
    def setUp(self) -> None:
        self.workflow = PREP_WORKFLOW.read_text(encoding="utf-8")

    def test_one_official_host_and_verified_source_replace_the_obsolete_win32_package(self) -> None:
        for token in ("clang+llvm-23.1.6-x86_64-pc-windows-msvc.tar.zst", "276226344", HOST_SHA,
                      "llvm-project-23.1.6.src.tar.xz", "179172364", SOURCE_SHA):
            self.assertIn(token, self.workflow)
        self.assertNotIn("win32.exe", self.workflow)
        self.assertNotIn("_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH", self.workflow)

    def test_x86_build_uses_the_unmodified_upstream_crt_choices(self) -> None:
        build = self.workflow.split("- name: Build the x86 LLVM 23.1.6 runtimes", 1)[1].split("\n      - name:", 1)[0]
        for token in ("i686-pc-windows-msvc", "x64_x86", "-DCOMPILER_RT_DEFAULT_TARGET_ONLY=ON",
                      "-DCOMPILER_RT_BUILD_PROFILE=ON", "-DCOMPILER_RT_BUILD_LIBFUZZER=OFF",
                      "-DCOMPILER_RT_SANITIZERS_TO_BUILD=asan", "--target profile asan"):
            self.assertIn(token, build)
        self.assertNotIn("Set-UbsanRuntimeSourcePatch", build)
        self.assertLess(self.workflow.index("Build the x86 LLVM 23.1.6 runtimes"),
                        self.workflow.index("Set-UbsanRuntimeSourcePatch -Path"))

    def test_cached_runtimes_are_rechecked_and_real_profiles_are_merged(self) -> None:
        verify = self.workflow.split("- name: Verify the pinned LLVM 23.1.6 toolchain", 1)[1].split("\n      - name:", 1)[0]
        self.assertNotIn("cache-hit", verify)
        for token in ("Assert-RuntimeSourceProvenance", "Assert-RuntimeMachine", "llvm-profdata.exe",
                      "llvm-cov.exe", "-fprofile-instr-generate", "-fcoverage-mapping",
                      "-fsanitize=address", "-fsanitize=fuzzer,address", "-runs=1"):
            self.assertIn(token, verify)
        self.assertIn("'/c'", verify)
        self.assertIn("& $pairedClang @compileArguments /MTd", verify)
        self.assertIn("& $pairedLld /nologo /WX", verify)
        profile_link = verify.split("& $pairedLld /nologo /WX", 1)[1].split("if ($LASTEXITCODE", 1)[0]
        self.assertIn("clang_rt.profile-i386.lib", profile_link)
        self.assertIn("values.data()[argc + 3]", verify)
        self.assertIn("halt_on_error=1:alloc_dealloc_mismatch=1", verify)
        for probe in ("asan", "fuzzer"):
            self.assertIn(f'& $pairedLld /nologo /WX /DEBUG /INCREMENTAL:NO "/out:${probe}Exe"', verify)

    def test_both_fuzzer_archives_enable_the_documented_static_library_annotations(self) -> None:
        step = self.workflow.split("- name: Build the annotated LLVM 23.1.6 fuzzer runtimes", 1)[1].split("\n      - name:", 1)[0]
        self.assertIn("@('x86', 'x64')", step)
        self.assertIn("'-DCMAKE_CXX_FLAGS=/D_ANNOTATE_STL'", step)
        self.assertIn("--target fuzzer", step)
        self.assertNotIn("_DISABLE_", step)
        self.assertIn("-ExpectedFiles @('clang_rt.fuzzer-x86_64.lib')", self.workflow)
        self.assertIn("-TargetTriple 'x86_64-pc-windows-msvc'", self.workflow)
        self.assertIn("The x64 /MT libFuzzer probe failed to link.", self.workflow)


class RuntimeSupplementProvenance(unittest.TestCase):
    def setUp(self) -> None:
        self.workflow = PREP_WORKFLOW.read_text(encoding="utf-8")
        temporary = tempfile.TemporaryDirectory(prefix="llvm23-provenance-")
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.records = []
        self.expected = LIBRARIES
        for name in LIBRARIES:
            data = name.encode("ascii")
            (self.directory / name).write_bytes(data)
            self.records.append({"name": name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})

    def _check(self, change=None, *, target="i686-pc-windows-msvc"):
        record = {"schema": "unpackrat/runtime-source-provenance", "sourceRef": "llvmorg-23.1.6",
                  "sourceAssetSha256": SOURCE_SHA, "hostAssetSha256": HOST_SHA,
                  "targetTriple": target, "toolsetVersion": "14.52.37025", "fuzzerAnnotations": "_ANNOTATE_STL",
                  "compilerIdentity": "clang version 23.1.6", "files": [dict(row) for row in self.records]}
        if change:
            change(record)
        path = self.directory / "provenance.json"
        path.write_text(json.dumps(record), encoding="utf-8")
        expected = "@(" + ",".join(f"'{name}'" for name in self.expected) + ")"
        return function_runner(self.workflow, "Assert-RuntimeSourceProvenance",
                               f"$record = Get-Content -Raw -LiteralPath '{path}' | ConvertFrom-Json\n"
                               f"Assert-RuntimeSourceProvenance -Record $record -RuntimeDirectory '{self.directory}' "
                               f"-ExpectedFiles {expected} -TargetTriple '{target}' "
                               "-ToolsetVersion '14.52.37025' -CompilerIdentity 'clang version 23.1.6'")

    def test_exact_current_source_compiler_and_files_are_accepted(self) -> None:
        result = self._check()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_mismatched_identity_or_file_inventory_is_rejected(self) -> None:
        for field in ("schema", "sourceRef", "sourceAssetSha256", "hostAssetSha256", "targetTriple",
                      "toolsetVersion", "compilerIdentity", "fuzzerAnnotations"):
            with self.subTest(field=field):
                result = self._check(lambda row: row.update({field: "wrong"}))
                self.assertNotEqual(result.returncode, 0)
        for mutation in (lambda row: row["files"].pop(),
                         lambda row: row["files"][0].update(name="../outside.lib"),
                         lambda row: row["files"][0].update(size=0),
                         lambda row: row["files"][0].update(sha256="00")):
            with self.subTest(mutation=mutation):
                result = self._check(mutation)
                self.assertNotEqual(result.returncode, 0)

    def test_a_missing_runtime_file_is_rejected(self) -> None:
        (self.directory / LIBRARIES[0]).unlink()
        self.assertNotEqual(self._check().returncode, 0)

    def test_x64_fuzzer_has_the_same_source_policy_with_its_own_target(self) -> None:
        name = "clang_rt.fuzzer-x86_64.lib"
        data = name.encode("ascii")
        (self.directory / name).write_bytes(data)
        self.expected = (name,)
        self.records = [{"name": name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()}]
        result = self._check(target="x86_64-pc-windows-msvc")
        self.assertEqual(result.returncode, 0, result.stderr)


class RuntimeSupplementDiagnostics(unittest.TestCase):
    def setUp(self) -> None:
        self.workflow = PREP_WORKFLOW.read_text(encoding="utf-8")

    def test_machine_guard_rejects_empty_wrong_or_mixed_archives(self) -> None:
        for headers, accepted in ((" 14C machine (x86)", True), ("", False),
                                  ("Machine : 14C (x86)", True),
                                  (" 8664 machine (x64)", False),
                                  (" 14C machine (x86)\n 8664 machine (x64)", False),
                                  (" 14C machine (x86)\n Machine : 8664 (x64)", False)):
            with self.subTest(headers=headers):
                result = function_runner(self.workflow, "Assert-RuntimeMachine",
                                         f"Assert-RuntimeMachine -Headers '{headers}' -ExpectedMachine '14C'")
                self.assertEqual(result.returncode == 0, accepted, result.stderr)

    def test_x64_runtime_machine_uses_its_own_expected_coff_value(self) -> None:
        for headers, accepted in (("Machine : 8664 (x64)", True), ("14C machine (x86)", False),
                                  ("Machine : 8664 (x64)\n14C machine (x86)", False)):
            with self.subTest(headers=headers):
                result = function_runner(self.workflow, "Assert-RuntimeMachine",
                                         f"Assert-RuntimeMachine -Headers '{headers}' -ExpectedMachine '8664'")
                self.assertEqual(result.returncode == 0, accepted, result.stderr)

    def test_asan_and_fuzzer_probes_need_the_expected_result(self) -> None:
        rows = (("asan", 1, "AddressSanitizer: heap-buffer-overflow", True),
                ("asan", 0, "AddressSanitizer: heap-buffer-overflow", False),
                ("asan", 1, "process crashed", False),
                ("fuzzer", 0, "Done 1 runs in 0 second(s)", True),
                ("fuzzer", 1, "Done 1 runs in 0 second(s)", False),
                ("fuzzer", 0, "nothing happened", False))
        for kind, status, output, accepted in rows:
            with self.subTest(kind=kind, status=status, output=output):
                result = function_runner(self.workflow, "Assert-X86RuntimeDiagnostic",
                                         f"Assert-X86RuntimeDiagnostic -Kind '{kind}' -ExitCode {status} -Output '{output}'")
                self.assertEqual(result.returncode == 0, accepted, result.stderr)


if __name__ == "__main__":
    unittest.main()
