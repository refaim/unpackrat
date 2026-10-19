"""Focused regression for the UberWolfLib ``DXArchive::KeyCreate`` scratch patch.

The pinned ``3rdParty/DXLib/DXArchive.cpp`` snapshot is a repository-owned fixture whose
SHA-256 is recorded in ``fixture-manifest.json``.  The port patch
``keycreate-scratch.patch`` replaces the unchecked ``malloc``/``free`` scratch with an owned
``std::vector<char>`` without changing the derived key, so an allocation failure unwinds as
``std::bad_alloc`` instead of writing through a null pointer.

The check is behavioural: the ``KeyCreate`` body that the *port actually builds* is extracted
from the pinned fixture and compiled with the pinned ``HashCRC32`` primitive into a throwaway
child process.  The subject tracks the port wiring.  While ``keycreate-scratch.patch`` is not
listed by ``portfile.cmake`` the subject is the raw, unpatched body, so
``test_allocation_failure_unwinds_as_bad_alloc_after_the_patch`` fails with the Windows access
violation ``0xC0000005`` instead of the expected ``std::bad_alloc`` (42); once the port applies
the patch the same test, unchanged, passes.  The oracle-key and stack-path tests pass in both
states, which isolates the allocation contract as the only behaviour the patch changes.

Both subjects run under one fault shim: a runtime flag that makes the overridden ``operator
new`` throw ``std::bad_alloc`` and makes the ``KeyCreate``-scoped ``malloc`` shim return
``nullptr``.  The flag is armed only *after* the driver has allocated its source material, and
the ``malloc`` shim is defined around the ``KeyCreate`` body and undefined before the allocator
replacement, so no standard header and no allocator implementation ever sees the macro.  The
tiny ``CL_*`` string helpers are reached only by the sub-4-byte fallback that lengths >= 1024
never take; the key derivation and the CRC table are the genuine pinned code.  The expected keys
are the frozen literals already asserted by ``src/tests/unit/wolfrpg_crypto.cpp``.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main  # noqa: E402  (reuse the production Visual Studio / toolset discovery)


REPOSITORY = Path(__file__).resolve().parents[2]
PORT = REPOSITORY / "build" / "vcpkg" / "ports" / "uberwolf"
PORTFILE = PORT / "portfile.cmake"
FIXTURES = REPOSITORY / "build" / "tests" / "fixtures" / "uberwolf"
MANIFEST = FIXTURES / "fixture-manifest.json"
PATCH = PORT / "keycreate-scratch.patch"

MEMBER = "3rdParty/DXLib/DXArchive.cpp"
FIXTURE = FIXTURES / "3rdParty" / "DXLib" / "DXArchive.cpp.fixture"

KEYCREATE_SIGNATURE = "void DXArchive::KeyCreate(const char *Source, size_t SourceBytes, u8 *Key)"
KEYCREATE_TERMINATOR = "void DXArchive::KeyConv("
HASHCRC32_SIGNATURE = "u32 DXArchive::HashCRC32(const void *SrcData, size_t SrcDataSize)"
HASHCRC32_TERMINATOR = "int DXArchive::EncodeArchiveOneDirectoryWolf("


# Frozen keys produced by the real pinned KeyCreate; identical to the literals in
# src/tests/unit/wolfrpg_crypto.cpp (lengths 1024, 1025 and 2039).
ORACLE_KEYS = {
    1024: bytes((0x96, 0x87, 0x56, 0xD0, 0xE3, 0xFD, 0x63)),
    1025: bytes((0xC1, 0xDE, 0xB7, 0x52, 0xE3, 0xFD, 0x63)),
    2039: bytes((0xD1, 0xB9, 0x86, 0xD0, 0x61, 0x1B, 0xCA)),
}


def _pinned_text() -> str:
    """Return the pinned DXArchive.cpp bytes after proving their recorded provenance."""
    expected = json.loads(MANIFEST.read_text(encoding="utf-8"))["files"][MEMBER]["sha256"]
    raw = FIXTURE.read_bytes()
    actual = hashlib.sha256(raw).hexdigest()
    if actual != expected:
        raise AssertionError(f"fixture hash mismatch for {MEMBER}: {actual}")
    return raw.decode("utf-8")


def _apply_patch(source: str) -> str:
    """Apply the real port patch to the pinned member text with ``git apply``."""
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        target = root / MEMBER
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(source, encoding="utf-8", newline="")
        patch = root / "port.patch"
        patch.write_bytes(PATCH.read_bytes())
        result = subprocess.run(
            ["git", "-c", "core.autocrlf=false", "apply", "-p1", str(patch)],
            cwd=str(root), capture_output=True, text=True,
        )
        if result.returncode != 0:
            raise RuntimeError(f"git apply failed ({result.returncode}): {result.stderr.strip()}")
        return target.read_text(encoding="utf-8")


def _function(text: str, signature: str, terminator: str) -> str:
    """Return one complete member definition, from its signature to its closing brace."""
    start = text.index(signature)
    stop = text.index(terminator, start)
    end = text.rindex("\n}\n", start, stop)
    return text[start:end + len("\n}\n")]


def _scratch_patch_is_wired() -> bool:
    """Whether ``portfile.cmake`` feeds ``keycreate-scratch.patch`` to ``vcpkg_from_github``."""
    text = PORTFILE.read_text(encoding="utf-8")
    return re.search(r"(?m)^\s*keycreate-scratch\.patch\s*$", text) is not None


def _keycreate_subject() -> tuple[str, bool]:
    """Return the ``KeyCreate`` body the port builds and whether the scratch patch is applied.

    The subject is derived from the production wiring, not hard-coded: while the port does not
    list the scratch patch the subject is the raw pinned body, which is exactly the behaviour
    running before the fix.
    """
    pinned = _pinned_text()
    if _scratch_patch_is_wired():
        patched = _apply_patch(pinned)
        return _function(patched, KEYCREATE_SIGNATURE, KEYCREATE_TERMINATOR), True
    return _function(pinned, KEYCREATE_SIGNATURE, KEYCREATE_TERMINATOR), False


def _vcvars_environment() -> tuple[dict[str, str], Path]:
    """Return the MSVC x64 environment and the absolute ``cl.exe`` for the pinned 14.44 toolset.

    ``CreateProcess`` resolves the program image from the *parent* environment, never the
    child environment handed to ``subprocess``, so ``cl`` has to be looked up explicitly on
    the PATH that ``vcvars64.bat`` captured.  The batch also aborts before ``set`` when the
    ``call`` fails, otherwise the trailing ``set`` would mask the failure with exit code 0.
    """
    installation = main._vs_installation()
    toolset = main._msvc_toolset_version(installation)
    vcvars = installation / "VC" / "Auxiliary" / "Build" / "vcvars64.bat"
    with tempfile.TemporaryDirectory() as tmp:
        batch = Path(tmp) / "capture.cmd"
        batch.write_text(
            "@echo off\r\n"
            f'call "{vcvars}" -vcvars_ver={toolset} >nul\r\n'
            "if errorlevel 1 exit /b 1\r\n"
            "set\r\n",
            encoding="utf-8",
        )
        result = subprocess.run(["cmd", "/c", str(batch)], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"vcvars64 failed ({result.returncode}): {result.stderr.strip()}")
    environment = dict(os.environ)
    for line in result.stdout.splitlines():
        if "=" in line:
            name, value = line.rstrip("\r").split("=", 1)
            environment[name] = value
    compiler = shutil.which("cl.exe", path=environment.get("PATH", ""))
    if compiler is None:
        raise RuntimeError("vcvars64 PATH did not expose cl.exe")
    return environment, Path(compiler)


_PREAMBLE = """\
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <new>
#include <vector>

using u8 = unsigned char;
using u32 = unsigned int;
using DWORD = unsigned long;
using BYTE = unsigned char;

extern void CL_strcpy(int, char *, const char *);
extern void CL_strcat_s(int, char *, std::size_t, const char *);
extern int CL_strlen(int, const char *);
extern const char DefaultKeyString[];

#define CHARCODEFORMAT_ASCII 32765

struct DXArchive
{
    static u32 HashCRC32(const void *, std::size_t);
    static void KeyCreate(const char *, std::size_t, u8 *);
};

// Armed only after the driver's source vector exists; shared by both fault paths below.
bool g_failAlloc = false;

void CL_strcpy(int, char *Dest, const char *Src) { std::strcpy(Dest, Src); }
void CL_strcat_s(int, char *Dest, std::size_t BufferBytes, const char *Src)
{
    (void)BufferBytes;
    std::strcat(Dest, Src);
}
int CL_strlen(int, const char *Str) { return static_cast<int>(std::strlen(Str)); }
// The pinned table is the 8 bytes below; KeyCreate only consults it for sub-4-byte material.
const char DefaultKeyString[] = "\\x44\\x58\\x42\\x44\\x58\\x41\\x52\\x43";
"""

# The malloc shim is scoped to the KeyCreate body: it is defined after every standard header
# and undefined before the allocator replacement, so neither sees a macro.  It routes through
# the runtime flag, so the unpatched subject fails exactly when the patched one would.
_MALLOC_SHIM_BEGIN = "#define malloc(size) (g_failAlloc ? nullptr : std::malloc(size))\n"
_MALLOC_SHIM_END = "#undef malloc\n"

_ALLOCATOR = """\
void *operator new(std::size_t n)
{
    if (g_failAlloc) { throw std::bad_alloc(); }
    void *p = std::malloc(n ? n : 1);
    if (p == nullptr) { throw std::bad_alloc(); }
    return p;
}
void operator delete(void *p) noexcept { std::free(p); }
void operator delete(void *p, std::size_t) noexcept { std::free(p); }
void *operator new[](std::size_t n) { return operator new(n); }
void operator delete[](void *p) noexcept { std::free(p); }
void operator delete[](void *p, std::size_t) noexcept { std::free(p); }
"""

_DRIVER = """\
int main(int argc, char **argv)
{
    if (argc != 3)
    {
        std::printf("usage: %s <mode> <length>\\n", argv[0]);
        return 2;
    }

    const char *mode = argv[1];
    std::size_t length = static_cast<std::size_t>(std::strtoul(argv[2], nullptr, 10));

    std::vector<unsigned char> material(length);
    for (std::size_t i = 0; i < length; ++i)
    {
        material[i] = static_cast<unsigned char>((i * 7u + 3u) & 0xFFu);
    }

    u8 key[7] = {0, 0, 0, 0, 0, 0, 0};
    const char *source = reinterpret_cast<const char *>(material.data());

    if (std::strcmp(mode, "key") == 0)
    {
        DXArchive::KeyCreate(source, length, key);
        for (int i = 0; i < 7; ++i) { std::printf("%02X", key[i]); }
        std::printf("\\n");
        return 0;
    }

    if (std::strcmp(mode, "fail") == 0)
    {
        g_failAlloc = true;
        try
        {
            DXArchive::KeyCreate(source, length, key);
        }
        catch (const std::bad_alloc &)
        {
            std::printf("bad_alloc\\n");
            return 42;
        }
        for (int i = 0; i < 7; ++i) { std::printf("%02X", key[i]); }
        std::printf("\\nreturned\\n");
        return 0;
    }

    std::printf("unknown mode\\n");
    return 2;
}
"""


def _harness(keycreate: str, hashcrc32: str) -> str:
    """Assemble a single translation unit around the real extracted function bodies."""
    return (
        _PREAMBLE
        + "\n"
        + hashcrc32
        + "\n"
        + _MALLOC_SHIM_BEGIN
        + keycreate
        + _MALLOC_SHIM_END
        + "\n"
        + _ALLOCATOR
        + "\n"
        + _DRIVER
    )


def _compile(
    directory: Path,
    source: str,
    name: str,
    environment: dict[str, str],
    compiler: Path,
) -> Path:
    """Compile one harness with the absolute MSVC ``cl`` under /W4 /WX.

    ``_CRT_SECURE_NO_WARNINGS`` mirrors the real port build so the harness's own string
    doubles may call ``std::strcpy``/``std::strcat`` without tripping C4996.
    """
    source_path = directory / f"{name}.cpp"
    source_path.write_text(source, encoding="utf-8", newline="")
    executable = directory / f"{name}.exe"
    result = subprocess.run(
        [
            str(compiler), "/nologo", "/MT", "/std:c++latest", "/W4", "/WX", "/EHsc",
            "/D_CRT_SECURE_NO_WARNINGS",
            f"/Fe:{executable}", source_path.name,
        ],
        cwd=str(directory), env=environment, capture_output=True, text=True,
    )
    if result.returncode != 0:
        raise RuntimeError(f"cl failed ({result.returncode}):\n{result.stdout}\n{result.stderr}")
    return executable


def _run(executable: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(executable), *arguments], capture_output=True, text=True,
    )


class KeyCreateScratchPatchTests(unittest.TestCase):
    """The patch must preserve the derived key and change the failure mode."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.temporary = tempfile.TemporaryDirectory(prefix="keycreate-scratch-")
        root = Path(cls.temporary.name)
        cls.environment, cls.compiler = _vcvars_environment()
        pinned = _pinned_text()
        cls.hashcrc32 = _function(pinned, HASHCRC32_SIGNATURE, HASHCRC32_TERMINATOR)
        cls.subject_body, cls.scratch_patch_wired = _keycreate_subject()
        cls.subject = _compile(
            root,
            _harness(cls.subject_body, cls.hashcrc32),
            "subject",
            cls.environment,
            cls.compiler,
        )

    @classmethod
    def tearDownClass(cls) -> None:
        cls.temporary.cleanup()

    def test_patched_source_removes_the_unchecked_scratch(self) -> None:
        pinned = _pinned_text()
        self.assertIn("(char *)malloc(SourceBytes);", pinned)
        self.assertIn("free(UseWorkBuffer);", pinned)

        patched = _apply_patch(pinned)
        region = _function(patched, KEYCREATE_SIGNATURE, KEYCREATE_TERMINATOR)
        self.assertIn("std::vector<char> DynamicWorkBuffer;", region)
        self.assertIn("DynamicWorkBuffer.resize(SourceBytes);", region)
        self.assertIn("UseWorkBuffer = DynamicWorkBuffer.data();", region)
        self.assertNotIn("malloc", region)
        self.assertNotIn("free(", region)

    def test_patched_key_create_reproduces_the_frozen_oracle_keys(self) -> None:
        for length, expected in ORACLE_KEYS.items():
            with self.subTest(length=length):
                result = _run(self.subject, "key", str(length))
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(bytes.fromhex(result.stdout.strip()), expected)

    def test_allocation_failure_unwinds_as_bad_alloc_after_the_patch(self) -> None:
        for length in (1025, 2039):
            with self.subTest(length=length):
                result = _run(self.subject, "fail", str(length))
                actual = result.returncode & 0xFFFFFFFF
                self.assertEqual(
                    actual,
                    42,
                    f"expected std::bad_alloc(42), actual 0x{actual:08X}; "
                    f"stdout={result.stdout!r} stderr={result.stderr!r}",
                )
                self.assertEqual(result.stdout.strip(), "bad_alloc")

    def test_stack_scratch_path_still_succeeds_under_allocation_failure(self) -> None:
        # Length 1024 stays inside the fixed 1024-byte stack scratch, so forcing the
        # allocator to fail must not change the outcome: the real KeyCreate runs to
        # completion and still returns the frozen 1024-byte key.
        result = _run(self.subject, "fail", "1024")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(
            result.stdout.split(),
            [ORACLE_KEYS[1024].hex().upper(), "returned"],
        )

    def test_unpatched_baseline_writes_through_the_null_scratch(self) -> None:
        # Independent control: the raw pinned body, compiled under the same fault shim as the
        # subject, stores into the null scratch and dies with a Windows access violation
        # (0xC0000005) at length 1025 instead of unwinding.  This documents the pre-patch
        # behaviour independently of the port wiring; it is not the allocation contract test.
        pinned = _pinned_text()
        baseline = _compile(
            Path(self.temporary.name),
            _harness(
                _function(pinned, KEYCREATE_SIGNATURE, KEYCREATE_TERMINATOR),
                self.hashcrc32,
            ),
            "baseline",
            self.environment,
            self.compiler,
        )
        result = _run(baseline, "fail", "1025")
        actual = result.returncode & 0xFFFFFFFF
        self.assertEqual(
            actual,
            0xC0000005,
            f"expected 0xC0000005, actual 0x{actual:08X}; "
            f"stdout={result.stdout!r} stderr={result.stderr!r}",
        )
        self.assertNotIn("returned", result.stdout)
        self.assertNotIn("bad_alloc", result.stdout)


if __name__ == "__main__":
    unittest.main()
