"""Every MSVC and ClangCL build uses the v145 14.52 toolset from a VS 18.x install.

The build deliberately moves off the legacy 14.44 (v143) tools.  The driver must
select a VS 18.x installation that carries the standard C++ x86/x64 component,
forward the numeric-maximum 14.52 servicing version for both the MSVC and ClangCL
configurations, and fail closed rather than silently falling back to the old
toolset.

The tests drive the real ``_msbuild_command`` and ``_vs_installation`` against an
on-disk installation shaped like ``<root>/VC/Tools/MSVC/<version>`` so the numeric
servicing order (14.52.10 must win over 14.52.9) is exercised for real.
"""

from __future__ import annotations

import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main


VS_TOOLSET_COMPONENT = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
TOOLSET_FAMILY = "14.52"
SELECTED_VERSION = "14.52.10"
OLD_VERSION = "14.44.35207"


class VsLookupTests(unittest.TestCase):
    def test_vswhere_selects_the_stable_vs18_range_and_requires_the_cpp_component(self) -> None:
        with mock.patch.dict(os.environ, {"ProgramFiles(x86)": r"C:\PF86"}, clear=True), \
             mock.patch.object(
                 main, "run",
                 return_value=subprocess.CompletedProcess([], 0, r"C:\VS18"),
             ) as invoked:
            installation = main._vs_installation()

        self.assertEqual(installation, Path(r"C:\VS18"))
        argv = invoked.call_args.args[0]
        self.assertIn("-latest", argv)
        self.assertIn("-version", argv)
        self.assertIn("[18.0,19.0)", argv)
        self.assertIn(VS_TOOLSET_COMPONENT, argv)
        self.assertEqual(argv[argv.index("-property") + 1], "installationPath")
        self.assertIn("-requires", argv)


class ToolsetForwardingTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.installation = self.root / "vs"
        self.msbuild = self.installation / "MSBuild" / "Current" / "Bin" / "MSBuild.exe"
        self.msbuild.parent.mkdir(parents=True)
        self.msbuild.write_bytes(b"m")
        self.vcpkg = self.root / "vcpkg.exe"
        self.repository = self.root / "repo"
        self.project = self.repository / "build" / "projects" / "tests.vcxproj"

    def _toolset(self, version: str) -> None:
        binary = self.installation / "VC" / "Tools" / "MSVC" / version / "bin" / "Hostx64" / "x64" / "cl.exe"
        binary.parent.mkdir(parents=True, exist_ok=True)
        binary.write_bytes(b"c")

    def _entry(self, name: str, *, directory: bool) -> None:
        """Create a sibling of the real toolset: a plain file or an empty directory."""

        path = self.installation / "VC" / "Tools" / "MSVC" / name
        if directory:
            path.mkdir(parents=True, exist_ok=True)
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"x")

    def _command(self, *, clang: bool = False) -> tuple[str, ...]:
        llvm = None
        if clang:
            runtime = self.root / "llvm" / "lib" / "clang" / "23.1.6" / "lib" / "windows"
            runtime.mkdir(parents=True, exist_ok=True)
            llvm = main.Llvm(
                self.root / "llvm", runtime, self.root / "cov.exe", self.root / "prof.exe",
            )
        with mock.patch.object(main, "_vs_installation", return_value=self.installation), \
             mock.patch.object(main, "_clang_tidy", return_value=self.root / "tidy.exe"):
            return main._msbuild_command(
                self.repository, self.project, "x64", "Release", None,
                self.msbuild, self.vcpkg, llvm=llvm,
            )

    def test_forwards_the_v145_servicing_version_for_msvc_and_clangcl(self) -> None:
        self._toolset(SELECTED_VERSION)
        for label, clang in (("msvc", False), ("clangcl", True)):
            with self.subTest(configured=label):
                self.assertIn(
                    f"/p:VCToolsVersion={SELECTED_VERSION}", self._command(clang=clang)
                )

    def test_numeric_servicing_order_prefers_14_52_10_over_14_52_9(self) -> None:
        self._toolset("14.52.9")
        self._toolset(SELECTED_VERSION)
        self.assertIn(f"/p:VCToolsVersion={SELECTED_VERSION}", self._command())

    def test_the_v145_family_wins_over_the_old_toolset(self) -> None:
        self._toolset(OLD_VERSION)
        self._toolset(SELECTED_VERSION)
        self.assertIn(f"/p:VCToolsVersion={SELECTED_VERSION}", self._command())

    def test_a_missing_v145_toolset_fails_closed_instead_of_using_the_old_one(self) -> None:
        self._toolset(OLD_VERSION)
        with self.assertRaises((FileNotFoundError, main.BuildError)):
            self._command()

    def test_a_file_with_a_numeric_version_name_never_displaces_the_real_toolset(self) -> None:
        # The family glob ``14.52.*`` also matches a stray file such as the backup artifact
        # ``14.52.99999``. A file is not a toolset directory, so the real 14.52.36231 must still
        # win even though the file's numeric suffix sorts higher.
        self._toolset("14.52.36231")
        self._entry("14.52.99999", directory=False)
        self.assertIn("/p:VCToolsVersion=14.52.36231", self._command())

    def test_a_backup_suffixed_directory_never_breaks_or_wins_the_selection(self) -> None:
        # ``14.52.36231.backup`` is a real directory the glob matches, but its name is not a fully
        # numeric servicing number; it must be ignored rather than crashing the numeric sort or
        # displacing the valid 14.52.36231 toolset.
        self._toolset("14.52.36231")
        self._entry("14.52.36231.backup", directory=True)
        self.assertIn("/p:VCToolsVersion=14.52.36231", self._command())

    def test_only_a_backup_suffixed_directory_is_no_valid_toolset(self) -> None:
        self._entry("14.52.36231.backup", directory=True)
        with self.assertRaises(FileNotFoundError):
            self._command()


class ToolsetReleaseVersionTests(unittest.TestCase):
    def test_build_and_dependency_updates_preserve_the_existing_plugin_releases(self) -> None:
        # Roma requested source/dependency updates only; keep the d5cf614 release metadata.
        releases = {
            "renpy": ("3.3.1", "4eb90a15fb4f26bd5c8639841988211fdaa212224c1da1f999f5ea2cbcf6693c",
                      "ec5a012fff959a530b24197d68809078771f8bbb2da3ce4dc598df824fec8cd1"),
            "rpgmaker": ("1.3.1", "7c9624009e15b24c5bab531ffd5a0441ea2fb7f7ca007fcd40a0a944d559fb6b",
                         "de2276d2bb308894758c8846b33f75a2b91cd76a04b6bf94c8598d2aa964faf1"),
            "zanzarah": ("2.3.1", "54a91bfcf7263b77c542a4bb8e524aec35a7f1c8d7b1d6f05be4b709a1978d02",
                         "c28a1e4262c0e48b644637f50cca390358d3665226352c0130a751622a204325"),
            "godot": ("1.2.2", "e596216a28827f0e92b1b9d6379fe1244e51fd8076ef5d3557b7784aedda30ba",
                      "35583c54544e48bf9800cc728ab58de2a15fd5417d160cedc315aae7c7eaf707"),
            "wolfrpg": ("1.0.1", "cb3227a5bbc854c9dce6b33520284c1fae7071e6b8b1a191e30df7e9c2e04f9a",
                        "760744686fa36a368a81fba0b5c7cf5929ddcc641bedc661e8f9b7254d6935e5"),
            "unity": ("2.0.0", "afbec898f2aab996641672b270ed25947af330abb22238d1f8e5601d86b44260",
                      "e71bc5ed80f325706d6d33c874d5da189f4fc20de28633b3ee6c68196f4bb9e6"),
        }
        for module, (version, english, russian) in releases.items():
            with self.subTest(module=module, member="VERSION"):
                self.assertEqual(main.read_version(main.REPOSITORY, module), version)
            directory = main.REPOSITORY / "src" / "modules" / module
            for language, label, digest in (("en", "Version", english), ("ru", "Версия", russian)):
                with self.subTest(module=module, language=language):
                    notes = (directory / f"ChangeLog.{language}.txt").read_text(encoding="utf-8")
                    self.assertEqual(notes.splitlines()[0], f"{label} {version}")
                    self.assertEqual(hashlib.sha256(notes.encode("utf-8")).hexdigest(), digest)


if __name__ == "__main__":
    unittest.main()
