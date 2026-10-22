"""Contract tests for the unpackrat Wolf RPG fuzz dependency toolset wiring.

The Fuzz triplets keep the outer v145 toolset and, only for the ``uberwolf`` port,
select the pinned clang-cl through ``VCPKG_CHAINLOAD_TOOLCHAIN_FILE`` plus the loaded
vcvars environment.  The chainload helper at
``build/vcpkg/toolchains/wolfrpg-fuzz-clangcl.cmake`` then points configure at the
pinned clang-cl/lld-link, maps the vcpkg architecture onto the LLVM triple and adds the
architecture-matched compiler-rt directory to the linker search path, together with the
architecture-matched ASan import library and its ``/WHOLEARCHIVE`` static runtime thunk
under that same directory, before deferring to the stock vcpkg Windows toolchain.

These are metadata-only checks.  CI's preflight verifies the real LLVM tools, and CMake
itself validates the compilers when it configures a real build, so the fixtures here do
not need (and must not fake) compiler executables.

The Fuzz triplets must also track the ``UNPACKRAT_LLVM_INSTRUMENTATION`` environment
variable that the chainload helper reads.  vcpkg on Windows builds ports inside a cleaned
environment, so a value only survives when the triplet lists it in
``VCPKG_ENV_PASSTHROUGH``.  The harness below seeds that list with a sentinel entry (a
variable that really exists in the environment) and exports the final list, which pins
both the added entry and the preservation of whatever the caller already passed through.
"""

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TRIPLETS = ROOT / "build" / "vcpkg" / "triplets"
HELPER = ROOT / "build" / "vcpkg" / "toolchains" / "wolfrpg-fuzz-clangcl.cmake"
FUZZ_TRIPLETS = tuple(f"unpackrat-{arch}-windows-static-fuzz.cmake" for arch in ("x86", "x64"))
X64_FUZZ_TRIPLET = "unpackrat-x64-windows-static-fuzz.cmake"
FUZZ_PORT = "uberwolf"
OTHER_PORTS = ("zlib", "nlohmann-json")
INSTRUMENTATION_ENV = "UNPACKRAT_LLVM_INSTRUMENTATION"
TARGET_RUNTIME_PREFIX = "lib/clang/21/lib/windows"
LLVM_TOOLSET = "v145"
# A real environment variable used as the sentinel: vcpkg keeps passthrough entries that
# exist in the process environment, so this survives verbatim unless a triplet clobbers
# the list instead of appending to it.
PASSTHROUGH_SENTINEL = "PATH"

TRIPLET_HARNESS = """set(PORT "@PORT@")
set(VCPKG_ENV_PASSTHROUGH "@SENTINEL@")
include("@TRIPLET@")
file(WRITE "@TOOLSET@" "${VCPKG_PLATFORM_TOOLSET}")
file(WRITE "@CRT@" "${VCPKG_CRT_LINKAGE}")
file(WRITE "@CHAINLOAD@" "${VCPKG_CHAINLOAD_TOOLCHAIN_FILE}")
file(WRITE "@VCVARS@" "${VCPKG_LOAD_VCVARS_ENV}")
file(WRITE "@PASSTHROUGH@" "${VCPKG_ENV_PASSTHROUGH}")
"""

# The helper is included as the REAL project file, after a fixture preamble sets the
# vcpkg metadata the caller would otherwise provide.  One delimiter-joined output line
# keeps parsing simple and side-steps CMake's quoting of paths and flags.
HELPER_HARNESS = """@PREAMBLE@
include("@HELPER@")
file(WRITE "@OUT@" "${CMAKE_C_COMPILER}|${CMAKE_CXX_COMPILER}|${CMAKE_LINKER}|${CMAKE_C_COMPILER_TARGET}|${CMAKE_CXX_COMPILER_TARGET}|${VCPKG_LINKER_FLAGS}|${FAKE_STOCK_INCLUDED}|${FAKE_STOCK_ARCH}|${FAKE_STOCK_CRT}|${CMAKE_TRY_COMPILE_CONFIGURATION}")
"""

# Minimal stand-in for vcpkg's scripts/toolchains/windows.cmake.  It records that it was
# included and the inputs it observed, which is enough to prove the helper defers to it.
# It substitutes the external vcpkg boundary only; it does not prove native linking.
STOCK_TOOLCHAIN = """# Minimal stand-in for vcpkg's scripts/toolchains/windows.cmake.
set(FAKE_STOCK_INCLUDED TRUE)
set(FAKE_STOCK_ARCH "${VCPKG_TARGET_ARCHITECTURE}")
set(FAKE_STOCK_CRT "${VCPKG_CRT_LINKAGE}")
set(CMAKE_SYSTEM_NAME Windows)
"""


class _CmakeScript(unittest.TestCase):
    def setUp(self):
        self._dir = tempfile.TemporaryDirectory()
        self.addCleanup(self._dir.cleanup)
        self.tmp = Path(self._dir.name)

    def _clean_env(self):
        env = {key: value for key, value in os.environ.items() if key != INSTRUMENTATION_ENV}
        # Keep the fallback deterministic: only the tests that opt in expose VCPKG_ROOT.
        env.pop("VCPKG_ROOT", None)
        return env

    def _run(self, harness, env):
        executable = shutil.which("cmake") or "cmake"
        return subprocess.run(
            [executable, "-P", str(harness)],
            cwd=str(self.tmp),
            env=env,
            capture_output=True,
            text=True,
            shell=executable.lower().endswith((".bat", ".cmd")),
        )


class FuzzTripletTests(_CmakeScript):
    @staticmethod
    def _passthrough_entries(path):
        raw = path.read_text(encoding="utf-8").strip()
        return raw.split(";") if raw else []

    def _evaluate(self, triplet, port):
        toolset = self.tmp / "toolset.txt"
        crt = self.tmp / "crt.txt"
        chainload = self.tmp / "chainload.txt"
        vcvars = self.tmp / "vcvars.txt"
        passthrough = self.tmp / "passthrough.txt"
        harness = self.tmp / f"harness-{Path(triplet).stem}-{port}.cmake"
        for stale in (toolset, crt, chainload, vcvars, passthrough):
            stale.unlink(missing_ok=True)
        text = TRIPLET_HARNESS.replace("@PORT@", port)
        text = text.replace("@SENTINEL@", PASSTHROUGH_SENTINEL)
        text = text.replace("@TRIPLET@", (TRIPLETS / triplet).as_posix())
        text = text.replace("@TOOLSET@", toolset.as_posix())
        text = text.replace("@CRT@", crt.as_posix())
        text = text.replace("@CHAINLOAD@", chainload.as_posix())
        text = text.replace("@VCVARS@", vcvars.as_posix())
        text = text.replace("@PASSTHROUGH@", passthrough.as_posix())
        harness.write_text(text, encoding="utf-8")
        completed = self._run(harness, self._clean_env())
        return completed, toolset, crt, chainload, vcvars, passthrough

    def test_fuzz_triplets_keep_v145_with_static_crt(self):
        for triplet in FUZZ_TRIPLETS:
            with self.subTest(triplet=triplet):
                completed, toolset, crt, _, _, _ = self._evaluate(triplet, FUZZ_PORT)
                self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
                self.assertEqual(LLVM_TOOLSET, toolset.read_text(encoding="utf-8").strip())
                self.assertEqual("static", crt.read_text(encoding="utf-8").strip())

    def test_only_uberwolf_selects_the_real_chainload_and_vcvars(self):
        for triplet in FUZZ_TRIPLETS:
            with self.subTest(triplet=triplet):
                completed, _, _, chainload, vcvars, _ = self._evaluate(triplet, FUZZ_PORT)
                self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
                chainload_path = Path(chainload.read_text(encoding="utf-8").strip())
                self.assertTrue(chainload_path.is_file(), f"{chainload_path} is missing")
                self.assertEqual(HELPER.resolve(), chainload_path.resolve())
                self.assertEqual("ON", vcvars.read_text(encoding="utf-8").strip())

    def test_uberwolf_triplets_pass_through_the_llvm_instrumentation_env(self):
        self.assertIn(PASSTHROUGH_SENTINEL, self._clean_env())
        for triplet in FUZZ_TRIPLETS:
            with self.subTest(triplet=triplet):
                completed, _, _, _, _, passthrough = self._evaluate(triplet, FUZZ_PORT)
                self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
                entries = self._passthrough_entries(passthrough)
                self.assertIn(PASSTHROUGH_SENTINEL, entries)
                self.assertIn(INSTRUMENTATION_ENV, entries)

    def test_other_ports_keep_v145_without_chainload(self):
        for port in OTHER_PORTS:
            with self.subTest(port=port):
                completed, toolset, crt, chainload, vcvars, passthrough = self._evaluate(
                    X64_FUZZ_TRIPLET, port
                )
                self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
                self.assertEqual(LLVM_TOOLSET, toolset.read_text(encoding="utf-8").strip())
                self.assertEqual("static", crt.read_text(encoding="utf-8").strip())
                self.assertEqual("", chainload.read_text(encoding="utf-8").strip())
                self.assertNotEqual("ON", vcvars.read_text(encoding="utf-8").strip())
                self.assertEqual([PASSTHROUGH_SENTINEL], self._passthrough_entries(passthrough))


class ChainloadHelperTests(_CmakeScript):
    def _llvm_root(self, archs=("x86", "x64")):
        root = self.tmp / "llvm"
        for arch in archs:
            runtime = root / arch / TARGET_RUNTIME_PREFIX
            runtime.mkdir(parents=True)
        return root

    def _vcpkg_layout(self):
        vcpkg = self.tmp / "vcpkg"
        (vcpkg / "scripts" / "buildsystems").mkdir(parents=True)
        (vcpkg / "scripts" / "buildsystems" / "vcpkg.cmake").write_text("# stub\n", encoding="utf-8")
        stock = vcpkg / "scripts" / "toolchains" / "windows.cmake"
        stock.parent.mkdir(parents=True, exist_ok=True)
        stock.write_text(STOCK_TOOLCHAIN, encoding="utf-8")
        return vcpkg

    def _abi_vcpkg_layout(self):
        vcpkg = self._vcpkg_layout()
        wrapper = vcpkg / "scripts" / "buildsystems" / "vcpkg.cmake"
        wrapper.write_text(
            "# Harmless stand-in for vcpkg's ABI try_compile include.\n"
            "unset(CMAKE_TOOLCHAIN_FILE)\n"
            f'include("{HELPER.as_posix()}")\n',
            encoding="utf-8",
        )
        return vcpkg

    def _run_helper(self, preamble, root, vcpkg_root=None, include_path=HELPER):
        out = self.tmp / "helper-out.txt"
        out.unlink(missing_ok=True)
        harness = self.tmp / "helper-harness.cmake"
        text = HELPER_HARNESS.replace("@PREAMBLE@", preamble)
        text = text.replace("@HELPER@", include_path.as_posix())
        text = text.replace("@OUT@", out.as_posix())
        harness.write_text(text, encoding="utf-8")
        env = self._clean_env()
        if root is not None:
            env[INSTRUMENTATION_ENV] = str(root)
        if vcpkg_root is not None:
            env["VCPKG_ROOT"] = str(vcpkg_root)
        completed = self._run(harness, env)
        return completed, out

    @staticmethod
    def _fields(out):
        values = out.read_text(encoding="utf-8").split("|")
        keys = (
            "cc",
            "cxx",
            "ld",
            "ctarget",
            "cxxtarget",
            "flags",
            "stock",
            "stockarch",
            "stockcrt",
            "tryconfig",
        )
        return dict(zip(keys, values))

    def _assert_asan_runtime_bindings(self, flags, runtime, suffix):
        # /LIBPATH alone routes clang_rt discovery but never triggers the link, so
        # lld-link reports undefined __asan_init. The link line must name the dynamic
        # ASan import library and force-load the static runtime thunk, both from the
        # architecture-matched runtime directory that /LIBPATH already points at.
        dynamic = f"{runtime}/clang_rt.asan_dynamic-{suffix}.lib"
        thunk = f"{runtime}/clang_rt.asan_static_runtime_thunk-{suffix}.lib"
        self.assertIn(f'"{dynamic}"', flags)
        self.assertIn(f'/WHOLEARCHIVE:"{thunk}"', flags)

    def _preamble(self, arch, *, vcpkg=None, unset_toolchain=False, linker_flags=None):
        lines = [f"set(VCPKG_TARGET_ARCHITECTURE {arch})", "set(VCPKG_CRT_LINKAGE static)"]
        if unset_toolchain:
            lines.append("unset(CMAKE_TOOLCHAIN_FILE)")
        elif vcpkg is not None:
            lines.append(f'set(CMAKE_TOOLCHAIN_FILE "{vcpkg.as_posix()}/scripts/buildsystems/vcpkg.cmake")')
        if linker_flags is not None:
            lines.append(f'set(VCPKG_LINKER_FLAGS "{linker_flags}")')
        return "\n".join(lines)

    def test_helper_points_configure_at_the_pinned_x64_tools(self):
        vcpkg = self._vcpkg_layout()
        root = self._llvm_root()
        completed, out = self._run_helper(
            self._preamble("x64", vcpkg=vcpkg, linker_flags="/INCREMENTAL:NO"), root
        )
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        fields = self._fields(out)
        self.assertEqual(root.as_posix() + "/x64/bin/clang-cl.exe", fields["cc"])
        self.assertEqual(fields["cc"], fields["cxx"])
        self.assertEqual(root.as_posix() + "/x64/bin/lld-link.exe", fields["ld"])
        self.assertEqual("x86_64-pc-windows-msvc", fields["ctarget"])
        self.assertEqual(fields["ctarget"], fields["cxxtarget"])
        runtime = f"{root.as_posix()}/x64/{TARGET_RUNTIME_PREFIX}"
        self.assertIn('"/LIBPATH:' + runtime + '"', fields["flags"])
        self._assert_asan_runtime_bindings(fields["flags"], runtime, "x86_64")
        self.assertTrue(fields["flags"].startswith("/INCREMENTAL:NO"), fields["flags"])
        self.assertEqual("TRUE", fields["stock"])
        self.assertEqual("x64", fields["stockarch"])
        self.assertEqual("static", fields["stockcrt"])
        self.assertEqual("Release", fields["tryconfig"])

    def test_helper_maps_the_x86_architecture_to_its_runtime(self):
        vcpkg = self._vcpkg_layout()
        root = self._llvm_root()
        completed, out = self._run_helper(self._preamble("x86", vcpkg=vcpkg), root)
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        fields = self._fields(out)
        self.assertEqual("i686-pc-windows-msvc", fields["ctarget"])
        self.assertEqual("i686-pc-windows-msvc", fields["cxxtarget"])
        runtime = f"{root.as_posix()}/x86/{TARGET_RUNTIME_PREFIX}"
        self.assertIn('"/LIBPATH:' + runtime + '"', fields["flags"])
        self._assert_asan_runtime_bindings(fields["flags"], runtime, "i386")
        self.assertEqual("TRUE", fields["stock"])
        self.assertEqual("Release", fields["tryconfig"])

    def test_helper_falls_back_to_the_vcpkg_root_environment(self):
        vcpkg = self._vcpkg_layout()
        root = self._llvm_root()
        completed, out = self._run_helper(
            self._preamble("x64", unset_toolchain=True), root, vcpkg_root=vcpkg
        )
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual("TRUE", self._fields(out)["stock"])

    def test_helper_requires_the_llvm_root_environment(self):
        completed, _ = self._run_helper(self._preamble("x64", unset_toolchain=True), None)
        self.assertNotEqual(0, completed.returncode)
        self.assertIn(INSTRUMENTATION_ENV, completed.stdout + completed.stderr)

    def test_helper_requires_the_matching_runtime_directory(self):
        root = self.tmp / "llvm-no-runtime"
        (root / "x64" / "bin").mkdir(parents=True)
        completed, _ = self._run_helper(self._preamble("x64", unset_toolchain=True), root)
        self.assertNotEqual(0, completed.returncode)
        self.assertIn("runtime library directory", completed.stdout + completed.stderr)

    def test_helper_rejects_an_unsupported_architecture(self):
        root = self._llvm_root()
        completed, _ = self._run_helper(self._preamble("arm64", unset_toolchain=True), root)
        self.assertNotEqual(0, completed.returncode)
        self.assertIn("only supports", completed.stdout + completed.stderr)

    def test_helper_requires_the_stock_windows_toolchain(self):
        vcpkg = self.tmp / "empty-vcpkg"
        (vcpkg / "scripts" / "buildsystems").mkdir(parents=True)
        (vcpkg / "scripts" / "buildsystems" / "vcpkg.cmake").write_text("# stub\n", encoding="utf-8")
        root = self._llvm_root()
        completed, _ = self._run_helper(self._preamble("x64", vcpkg=vcpkg), root)
        self.assertNotEqual(0, completed.returncode)
        diagnostic = " ".join((completed.stdout + completed.stderr).split())
        self.assertIn("stock vcpkg Windows toolchain", diagnostic)

    def test_helper_locates_the_stock_toolchain_from_its_abi_parent_wrapper(self):
        vcpkg = self._abi_vcpkg_layout()
        wrapper = vcpkg / "scripts" / "buildsystems" / "vcpkg.cmake"
        root = self._llvm_root()
        completed, out = self._run_helper(
            self._preamble("x64", vcpkg=vcpkg),
            root,
            include_path=wrapper,
        )
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        fields = self._fields(out)
        self.assertEqual(root.as_posix() + "/x64/bin/clang-cl.exe", fields["cc"])
        self.assertEqual(fields["cc"], fields["cxx"])
        self.assertEqual(root.as_posix() + "/x64/bin/lld-link.exe", fields["ld"])
        self.assertEqual("x86_64-pc-windows-msvc", fields["ctarget"])
        self.assertEqual(fields["ctarget"], fields["cxxtarget"])
        runtime = f"{root.as_posix()}/x64/{TARGET_RUNTIME_PREFIX}"
        self.assertIn(f'"/LIBPATH:{runtime}"', fields["flags"])
        self._assert_asan_runtime_bindings(fields["flags"], runtime, "x86_64")
        self.assertEqual("TRUE", fields["stock"])
        self.assertEqual("x64", fields["stockarch"])
        self.assertEqual("static", fields["stockcrt"])


if __name__ == "__main__":
    unittest.main()
