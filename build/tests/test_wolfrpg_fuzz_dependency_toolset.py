"""Contract tests for the unpackrat fuzz dependency toolset wiring."""

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TRIPLETS = ROOT / "build" / "vcpkg" / "triplets"
PORTFILE_ROOTS = (ROOT / "build" / "vcpkg" / "ports", ROOT / "build" / "ports")
FUZZ_TRIPLETS = tuple(f"unpackrat-{arch}-windows-static-fuzz.cmake" for arch in ("x86", "x64"))
X64_FUZZ_TRIPLET = "unpackrat-x64-windows-static-fuzz.cmake"
FUZZ_PORT = "uberwolf"
LLVM_VERSION = "21.1.8"
INSTRUMENTATION_ENV = "UNPACKRAT_LLVM_INSTRUMENTATION"
LLVM_TOOLSET = "ClangCL"
DEFAULT_TOOLSET = "v145"

HARNESS = """set(PORT "@PORT@")
include("@TRIPLET@")
file(WRITE "@TOOLSET@" "${VCPKG_PLATFORM_TOOLSET}")
file(WRITE "@OPTIONS@" "${VCPKG_CMAKE_CONFIGURE_OPTIONS}")
function(capture)
  file(WRITE "@ARGV_OUT@" "ARGC=${ARGC}\\n")
  foreach(index RANGE 0 9)
    if(index LESS ARGC)
      file(APPEND "@ARGV_OUT@" "ARGV${index}=${ARGV${index}}\\n")
    endif()
  endforeach()
endfunction()
capture(${VCPKG_CMAKE_CONFIGURE_OPTIONS})
"""


def _portfile_candidates():
    for root in PORTFILE_ROOTS:
        if not root.is_dir():
            continue
        for path in sorted(root.rglob("portfile.cmake")):
            text = path.read_text(encoding="utf-8", errors="replace")
            if "WINDOWS_USE_MSBUILD" in text and "VCPKG_PLATFORM_TOOLSET" in text:
                yield path, text


def _normalized(text):
    stripped = text.replace("$", "").replace("{", "").replace("}", "")
    return " ".join(stripped.replace('"', " ").replace("'", " ").split())


class FuzzToolsetTests(unittest.TestCase):
    def setUp(self):
        self._dir = tempfile.TemporaryDirectory()
        self.addCleanup(self._dir.cleanup)
        self.tmp = Path(self._dir.name)

    def _llvm_root(self, create_version=True):
        root = self.tmp / "llvm"
        if create_version:
            (root / "x64" / "lib" / "clang" / LLVM_VERSION).mkdir(parents=True)
        else:
            root.mkdir(parents=True)
        return root

    def _evaluate(self, triplet, port, root):
        toolset = self.tmp / "toolset.txt"
        options = self.tmp / "options.txt"
        argv_out = self.tmp / "argv.txt"
        harness = self.tmp / f"harness-{Path(triplet).stem}-{port}.cmake"
        for stale in (toolset, options, argv_out):
            stale.unlink(missing_ok=True)
        text = HARNESS.replace("@PORT@", port)
        text = text.replace("@TRIPLET@", (TRIPLETS / triplet).as_posix())
        text = text.replace("@TOOLSET@", toolset.as_posix())
        text = text.replace("@OPTIONS@", options.as_posix())
        text = text.replace("@ARGV_OUT@", argv_out.as_posix())
        harness.write_text(text, encoding="utf-8")
        env = {key: value for key, value in os.environ.items() if key != INSTRUMENTATION_ENV}
        if root is not None:
            env[INSTRUMENTATION_ENV] = str(root)
        executable = shutil.which("cmake") or "cmake"
        completed = subprocess.run(
            [executable, "-P", str(harness)],
            cwd=str(self.tmp),
            env=env,
            capture_output=True,
            text=True,
            shell=executable.lower().endswith((".bat", ".cmd")),
        )
        return completed, toolset, options, argv_out

    def _assert_failure(self, triplet, root):
        completed, _, _, _ = self._evaluate(triplet, FUZZ_PORT, root)
        self.assertNotEqual(0, completed.returncode, f"{triplet} accepted an unusable LLVM root")
        return completed.stdout + completed.stderr

    def test_fuzz_triplets_select_llvm_clang_toolset(self):
        root = self._llvm_root()
        for triplet in FUZZ_TRIPLETS:
            with self.subTest(triplet=triplet):
                completed, toolset, options, argv_out = self._evaluate(triplet, FUZZ_PORT, root)
                self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
                toolset_text = toolset.read_text(encoding="utf-8")
                options_text = options.read_text(encoding="utf-8").replace("\\", "/")
                self.assertEqual(LLVM_TOOLSET, toolset_text.strip())
                self.assertIn("LLVMInstallDir=" + (root / "x64").as_posix(), options_text)
                self.assertIn("LLVMToolsVersion=" + LLVM_VERSION, options_text)
                lines = argv_out.read_text(encoding="utf-8").replace("\\", "/").splitlines()
                self.assertIn("ARGC=1", lines)
                argv0 = [line for line in lines if line.startswith("ARGV0=")]
                self.assertEqual(1, len(argv0), lines)
                self.assertIn("LLVMInstallDir=" + (root / "x64").as_posix(), argv0[0])
                self.assertIn("LLVMToolsVersion=" + LLVM_VERSION, argv0[0])

    def test_other_ports_keep_default_toolset(self):
        for port in ("zlib", "nlohmann-json"):
            with self.subTest(port=port):
                completed, toolset, options, _ = self._evaluate(X64_FUZZ_TRIPLET, port, None)
                self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
                self.assertEqual(DEFAULT_TOOLSET, toolset.read_text(encoding="utf-8").strip())
                options_text = options.read_text(encoding="utf-8")
                for token in ("LLVMInstallDir", "VS_GLOBALS", LLVM_TOOLSET):
                    self.assertNotIn(token, options_text)

    def test_missing_instrumentation_env_fails(self):
        for triplet in FUZZ_TRIPLETS:
            with self.subTest(triplet=triplet):
                self.assertIn(INSTRUMENTATION_ENV, self._assert_failure(triplet, None))

    def test_missing_clang_version_directory_fails(self):
        root = self._llvm_root(create_version=False)
        for triplet in FUZZ_TRIPLETS:
            with self.subTest(triplet=triplet):
                self.assertIn("lib/clang", self._assert_failure(triplet, root))

    def test_portfile_uses_msbuild_only_for_clangcl(self):
        candidates = list(_portfile_candidates())
        self.assertTrue(candidates, "no portfile wires WINDOWS_USE_MSBUILD from VCPKG_PLATFORM_TOOLSET")
        for path, text in candidates:
            with self.subTest(portfile=str(path)):
                index = text.find("WINDOWS_USE_MSBUILD")
                self.assertIn("VCPKG_PLATFORM_TOOLSET STREQUAL ClangCL", _normalized(text[:index]))
                end = text.find("endif", index)
                self.assertNotEqual(-1, end, f"{path} closes the MSBuild conditional")
                self.assertNotIn("WINDOWS_USE_MSBUILD", text[end:])


if __name__ == "__main__":
    unittest.main()
