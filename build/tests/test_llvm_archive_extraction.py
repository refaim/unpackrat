"""Run the workflow's exact LLVM extraction code against synthetic archives."""

import io
from compression import zstd
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import textwrap
import unittest
from unittest.mock import patch


WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/ci-check.yml"
PREFIX = "clang+llvm-23.1.6-x86_64-pc-windows-msvc/"
TOOLS = ("bin/clang-tidy.exe", "bin/clang-format.exe", "bin/clang-cl.exe",
         "bin/run-clang-tidy")
HEADERS = tuple(f"lib/clang/23/include/header{number}.h" for number in range(9))


class LLVMArchiveExtractionTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        workflow = WORKFLOW.read_text(encoding="utf-8")
        step = workflow.split("      - name: Provision the pinned LLVM 23.1.6 tools\n", 1)[1]
        step = step.split("\n      - name: ", 1)[0]
        self.step = textwrap.dedent(step.split("        run: |\n", 1)[1])
        self.script = textwrap.dedent(step.split("          @'\n", 1)[1]
                                      .split("          '@", 1)[0])

    def archive(self, names: tuple[str, ...]) -> Path:
        path = self.root / "fixture.tar.zst"
        with tarfile.open(path, "w:zst") as bundle:
            for name in names:
                member = tarfile.TarInfo(name)
                if name.endswith("/"):
                    member.type = tarfile.DIRTYPE
                    bundle.addfile(member)
                else:
                    payload = f"fixture:{name}".encode("utf-8")
                    member.size = len(payload)
                    bundle.addfile(member, io.BytesIO(payload))
        return path

    def extract(self, archive: Path) -> Path:
        output = self.root / "extracted"
        with patch.dict(os.environ, {"UNPACKRAT_LLVM_ARCHIVE": str(archive),
                                     "UNPACKRAT_LLVM_ROOT": str(output)}):
            exec(compile(self.script, "<llvm-archive-extraction>", "exec"), {})
        return output

    def high_window_archive(self, names: tuple[str, ...], window_log: int) -> Path:
        archive = self.archive(names)
        payload = zstd.decompress(archive.read_bytes())
        compressor = zstd.ZstdCompressor(options={
            zstd.CompressionParameter.window_log: window_log,
            zstd.CompressionParameter.content_size_flag: 0,
        })
        # Flush a block before ending the frame so the standard compressor preserves
        # the requested streaming window even for this tiny archive.
        frame = compressor.compress(payload, mode=zstd.ZstdCompressor.FLUSH_BLOCK)
        frame += compressor.flush(mode=zstd.ZstdCompressor.FLUSH_FRAME)
        self.assertEqual(frame[:4], bytes.fromhex("28b52ffd"))
        self.assertEqual(frame[5], (window_log - 10) << 3)
        archive.write_bytes(frame)
        return archive

    def test_complete_archive_keeps_exact_tools_and_headers_only(self) -> None:
        selected = TOOLS + HEADERS
        ignored = ("other-release/bin/clang-cl.exe", PREFIX + "bin/unwanted.exe",
                   PREFIX + "lib/clang/23/include/directory/")
        output = self.extract(self.archive(tuple(PREFIX + name for name in selected) + ignored))
        actual = {path.relative_to(output).as_posix() for path in output.rglob("*") if path.is_file()}
        self.assertEqual(actual, set(selected))
        for name in selected:
            self.assertEqual((output / name).read_bytes(), f"fixture:{PREFIX}{name}".encode("utf-8"))
        self.assertFalse((output / "lib/clang/23/include/directory").exists())

    def test_workflow_verifies_and_streams_the_official_zstd_asset(self) -> None:
        self.assertIn("$asset = 'clang+llvm-23.1.6-x86_64-pc-windows-msvc.tar.zst'", self.step)
        self.assertIn("7623f582089983c1cb51030cf1996514b19464e5669099571b399e8543770155", self.step)
        self.assertIn("Get-FileHash -LiteralPath $archive -Algorithm SHA256", self.step)
        self.assertIn("ZstdFile(", self.script)
        self.assertIn("options={DecompressionParameter.window_log_max: 30}", self.script)
        self.assertIn("fileobj=decoded, mode='r|'", self.script)
        self.assertNotIn("mode='r|xz'", self.script)

    def test_official_one_gibibyte_window_extracts_the_selected_bytes(self) -> None:
        selected = TOOLS + HEADERS
        archive = self.high_window_archive(tuple(PREFIX + name for name in selected), 30)
        with self.assertRaisesRegex(zstd.ZstdError, "Frame requires too much memory"):
            zstd.decompress(archive.read_bytes())
        output = self.extract(archive)
        self.assertEqual({path.relative_to(output).as_posix() for path in output.rglob("*")
                          if path.is_file()}, set(selected))
        for name in selected:
            self.assertEqual((output / name).read_bytes(), f"fixture:{PREFIX}{name}".encode("utf-8"))

    def test_larger_than_official_window_is_rejected_before_creating_output(self) -> None:
        archive = self.high_window_archive(tuple(PREFIX + name for name in TOOLS + HEADERS), 31)
        with self.assertRaisesRegex(zstd.ZstdError, "Frame requires too much memory"):
            self.extract(archive)
        self.assertFalse((self.root / "extracted").exists())

    def test_cold_cache_step_uses_existing_build_python(self) -> None:
        selected = TOOLS + HEADERS
        archive = self.archive(tuple(PREFIX + name for name in selected))
        workspace = Path(sys.executable).resolve().parents[3]
        self.assertEqual(
            Path(sys.executable).resolve(),
            (workspace / "build/.venv/Scripts/python.exe").resolve(),
        )
        fakes = textwrap.dedent("""\
            $ErrorActionPreference = 'Stop'
            function gh {
              if (($args[0..4] -join ' ') -ne 'release download llvmorg-23.1.6 --repo llvm/llvm-project') {
                throw 'Expected the pinned LLVM release download.'
              }
              $destination = Join-Path $args[8] $args[6]
              Copy-Item -LiteralPath $env:FIXTURE_ARCHIVE -Destination $destination
              $global:LASTEXITCODE = 0
            }
            function Get-FileHash {
              param([string] $LiteralPath, [string] $Algorithm)
              if (-not (Test-Path -LiteralPath $LiteralPath) -or $Algorithm -ne 'SHA256') {
                throw 'Expected verification of the downloaded fixture archive.'
              }
              return @{ Hash = '7623f582089983c1cb51030cf1996514b19464e5669099571b399e8543770155' }
            }
            """)
        with patch.dict(os.environ, {"RUNNER_TEMP": str(self.root),
                                     "GITHUB_WORKSPACE": str(workspace),
                                     "FIXTURE_ARCHIVE": str(archive)}):
            completed = subprocess.run(
                ["pwsh", "-NoProfile", "-NonInteractive", "-Command", fakes + self.step],
                capture_output=True, text=True, encoding="utf-8", check=False,
            )
        self.assertEqual(completed.returncode, 0, completed.stderr)
        output = self.root / "unpackrat/tools/llvm/23.1.6"
        self.assertEqual({path.relative_to(output).as_posix() for path in output.rglob("*")
                          if path.is_file()}, set(selected))
        for name in selected:
            self.assertEqual((output / name).read_bytes(), f"fixture:{PREFIX}{name}".encode("utf-8"))

    def test_short_archive_and_each_missing_tool_are_rejected(self) -> None:
        for missing in (None, *TOOLS):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as temporary:
                self.root = Path(temporary)
                selected = tuple(name for name in TOOLS + HEADERS if name != missing)
                if missing is None:
                    selected = selected[:11]
                archive = self.archive(tuple(PREFIX + name for name in selected))
                with self.assertRaisesRegex(ValueError, "Incomplete LLVM extraction"):
                    self.extract(archive)

    def test_unsafe_header_is_rejected_before_writing_outside_root(self) -> None:
        archive = self.archive((PREFIX + "lib/clang/23/include/../../../../../escaped.h",))
        with self.assertRaisesRegex(ValueError, "Unsafe LLVM archive member"):
            self.extract(archive)
        self.assertFalse((self.root / "escaped.h").exists())
        self.assertFalse((self.root / "extracted").exists())


if __name__ == "__main__":
    unittest.main()
