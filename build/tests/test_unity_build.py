"""Behavioural contract for the opt-in Unity normal-build route.

``build.ps1 build -Module unity -Arch x86,x64 -Config Release`` must route through
``build/main.py`` to a pinned AssetRipper preparation, the MSVC Observer static library,
an ordinary SDK NativeAOT publish with the vcpkg YY-Thunks XP object, and a final stage of
one ``unity.so`` and ``unity.pdb`` per architecture under ``out/native/bin/<arch>/Release``.
The default build and the five native modules stay untouched.

Every external boundary is a double: the checked ``main.run`` records each tool argv, the
binary ``gh`` zipball download is faked through ``subprocess.run`` and the tool lookups are
patched, so no real MSBuild, vcpkg, dotnet, gh or network is ever touched.  No test inspects
source text or mirrors an implementation string.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile


BUILD_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BUILD_ROOT))

import main  # noqa: E402


UPSTREAM_REPOSITORY = "AssetRipper/AssetRipper"
UPSTREAM_REVISION = "beeb50ee1c223e856c860caa338e5309404956a3"
UPSTREAM_FILE = "src/modules/unity/upstream.json"
PATCH_FILE = "src/modules/unity/patches/assetripper-stream-ownership.patch"
PATCH_BYTES = b"fixture stream-ownership patch\n"
OBSERVER_LIB_BYTES = b"observer static library bytes\n"
YY_THUNKS_BYTES = b"xp thunks object bytes\n"


def _publish_dll(architecture: str) -> bytes:
    return f"native unity shared library {architecture}\n".encode("ascii")


class UnitySourceLayoutTests(unittest.TestCase):
    def test_build_uses_the_shipped_module_source_directory(self) -> None:
        self.assertEqual(main.UNITY_UPSTREAM, UPSTREAM_FILE)
        self.assertEqual(main.UNITY_PATCH, PATCH_FILE)
        self.assertEqual(main.UNITY_OBSERVER_PROJECT,
                         "src/modules/unity/observer/UnityObserver.vcxproj")
        self.assertEqual(main.UNITY_PROBE_PROJECT,
                         "src/modules/unity/UnityProbe/UnityProbe.csproj")

    def test_module_keeps_its_backend_adapter_and_repository_fixtures(self) -> None:
        root = BUILD_ROOT.parent / "src/modules/unity"
        for relative in (
            "UnityProbe/UnitySession.cs", "UnityProbe/UnityProbe.csproj",
            "observer/UnityExtractor.cpp", "observer/UnityObserver.vcxproj",
            "native/unity_backend.h", "patches/assetripper-stream-ownership.patch",
            "UnityProbe.Tests/UnityProbe.Tests.csproj", "fixtures/text-only.assets",
        ):
            with self.subTest(relative=relative):
                self.assertTrue((root / relative).is_file(), relative)


class RecordedCall:
    def __init__(self, argv, cwd, env) -> None:
        self.argv = tuple(str(item) for item in argv)
        self.cwd = cwd
        self.env = env


def _option(argv: tuple[str, ...], prefix: str) -> str | None:
    for item in argv:
        if item.startswith(prefix):
            return item[len(prefix):]
    return None


def _basename(argv: tuple[str, ...]) -> str:
    return os.path.basename(argv[0]).lower() if argv else ""


class UnityRun:
    """A checked ``main.run`` double that fakes only the outputs the route verifies.

    MSBuild emits ``UnityObserver.lib`` under the requested ``OutDir`` and ``dotnet publish``
    writes the NativeAOT ``.dll``/``.pdb`` under the requested ``PublishDir``; every other
    command (vcpkg restore, ``git apply``) is recorded and does nothing.  ``observer_library``,
    ``publish``, ``dll`` and ``pdb`` let a test suppress one output to pin the fail-closed path.
    """

    def __init__(self, *, observer_library=True, publish=True, dll=True, pdb=True, fail_when=None):
        self.calls: list[RecordedCall] = []
        self.observer_library = observer_library
        self.publish = publish
        self.dll = dll
        self.pdb = pdb
        self._fail_when = fail_when

    def __call__(self, argv, cwd=None, env=None, capture=False):
        argv = tuple(str(item) for item in argv)
        self.calls.append(RecordedCall(argv, cwd, env))
        if self._fail_when is not None and self._fail_when(argv):
            raise subprocess.CalledProcessError(3, list(argv), "")
        name = _basename(argv)
        if name == "msbuild.exe":
            out = _option(argv, "/p:OutDir=")
            if self.observer_library and out is not None:
                Path(out).mkdir(parents=True, exist_ok=True)
                (Path(out) / "UnityObserver.lib").write_bytes(OBSERVER_LIB_BYTES)
        elif name == "dotnet.exe":
            out = _option(argv, "-p:PublishDir=")
            rid = _option(argv, "win-")
            if self.publish and out is not None and rid is not None:
                directory = Path(out)
                directory.mkdir(parents=True, exist_ok=True)
                if self.dll:
                    (directory / f"{main.UNITY_ASSEMBLY}.dll").write_bytes(
                        _publish_dll("x86" if rid.endswith("x86") else "x64"))
                if self.pdb:
                    (directory / f"{main.UNITY_ASSEMBLY}.pdb").write_bytes(b"publish symbols\n")
        return subprocess.CompletedProcess(list(argv), 0, "" if capture else None)

    def with_project(self, needle: str) -> list[RecordedCall]:
        return [call for call in self.calls if any(needle in item for item in call.argv)]


class GhDownload:
    """A ``subprocess.run`` double for the binary ``gh api .../zipball`` download."""

    def __init__(self, payload: bytes = b"", *, returncode: int = 0) -> None:
        self.payload = payload
        self.returncode = returncode
        self.calls: list[tuple[str, ...]] = []

    def __call__(self, argv, **kwargs):
        argv = tuple(str(item) for item in argv)
        self.calls.append(argv)
        if self.returncode != 0:
            raise subprocess.CalledProcessError(self.returncode, list(argv))
        stream = kwargs.get("stdout")
        if stream is not None and self.payload:
            stream.write(self.payload)
        return subprocess.CompletedProcess(list(argv), 0)


def zipball(*entries: str) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        for entry in entries:
            archive.writestr(entry, f"# {entry}\n")
    return buffer.getvalue()


class UnityCase(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        self.gh = self.root / "gh.exe"
        self.dotnet = self.root / "dotnet.exe"
        self.msbuild = self.root / "MSBuild.exe"
        self.vcpkg = self.root / "vcpkg.exe"
        self.vs = self.root / "vs"
        cl = self.vs / "VC" / "Tools" / "MSVC" / "14.51.36231" / "bin" / "Hostx64" / "x64" / "cl.exe"
        cl.parent.mkdir(parents=True, exist_ok=True)
        cl.write_bytes(b"x")

    def experiment(self, repository: str = UPSTREAM_REPOSITORY, revision: str = UPSTREAM_REVISION) -> None:
        path = self.repo / UPSTREAM_FILE
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({
            "sourceRepository": repository,
            "sourceRevision": revision,
            "releaseWatchBaseline": "2.0.0",
        }), encoding="utf-8")
        patch = self.repo / PATCH_FILE
        patch.parent.mkdir(parents=True, exist_ok=True)
        patch.write_bytes(PATCH_BYTES)

    def yy_thunks(self, architecture: str) -> Path:
        triplet = f"unpackrat-{architecture}-windows-static"
        path = (self.repo / "out" / "native" / "vcpkg_installed" / architecture
                / triplet / "share" / "yy-thunks" / "objs" / architecture
                / "YY_Thunks_for_WinXP.obj")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(YY_THUNKS_BYTES)
        return path

    def _resolve(self, name: str) -> Path:
        if name == "gh":
            return self.gh
        if name == "dotnet":
            return self.dotnet
        raise FileNotFoundError(f"{name} was not found on PATH")

    def patched(self, run: UnityRun, download: GhDownload | None = None, *, require_tool=None) -> None:
        stack = mock.patch.multiple(
            main,
            run=run,
            _native_tools=mock.Mock(return_value=(self.msbuild, self.vcpkg)),
            _vs_installation=mock.Mock(return_value=self.vs),
            _require_tool=mock.Mock(side_effect=require_tool or self._resolve),
        )
        stack.__enter__()
        self.addCleanup(stack.__exit__, None, None, None)
        if download is not None:
            patcher = mock.patch.object(subprocess, "run", download)
            patcher.start()
            self.addCleanup(patcher.stop)

    def unity(self, run: UnityRun, download: GhDownload | None = None, *,
              architectures=("x86", "x64"), configurations=("Release",), require_tool=None):
        self.patched(run, download, require_tool=require_tool)
        return main.unity_build(architectures, configurations, repository=self.repo)

    def vendor(self) -> Path:
        return self.repo / main.UNITY_VENDOR


class CliRouteTests(UnityCase):
    def test_build_module_unity_is_accepted_by_the_parser(self) -> None:
        args = main._parser().parse_args(
            ["build", "-Module", "unity", "-Arch", "x86,x64", "-Config", "Release"])
        self.assertEqual(args.module, ("unity",))
        self.assertEqual(args.architecture, ("x86", "x64"))
        self.assertEqual(args.configuration, ("Release",))

    def test_build_without_module_keeps_the_default_parser_shape(self) -> None:
        args = main._parser().parse_args(["build", "-Arch", "x64", "-Config", "Debug"])
        self.assertIsNone(args.module)

    def test_main_routes_the_unity_selection_to_the_unity_build(self) -> None:
        seen: list[tuple[tuple[str, ...], tuple[str, ...]]] = []
        with mock.patch.object(main, "unity_build", side_effect=lambda a, c, **_: seen.append((a, c))):
            code = main.main(["build", "-Module", "unity", "-Arch", "x86,x64", "-Config", "Release"])
        self.assertEqual(code, 0)
        self.assertEqual(seen, [(("x86", "x64"), ("Release",))])

    def test_main_keeps_the_default_native_suite_without_the_module_option(self) -> None:
        with mock.patch.object(main, "build") as handler, \
                mock.patch.object(main, "unity_build") as unity:
            self.assertEqual(main.main(["build", "-Arch", "x64", "-Config", "Debug"]), 0)
        handler.assert_called_once()
        unity.assert_not_called()

    def test_build_module_rejects_any_non_unity_selection(self) -> None:
        with self.assertRaises(SystemExit):
            main.main(["build", "-Module", "renpy", "-Arch", "x64", "-Config", "Release"])

    def test_unity_build_rejects_debug_before_any_heavy_work(self) -> None:
        run = UnityRun()
        self.patched(run)
        with self.assertRaisesRegex(main.BuildError, "Release"):
            main.unity_build(("x64",), ("Debug",), repository=self.repo)
        self.assertEqual(run.calls, [])


class UnityRouteTests(UnityCase):
    def test_unity_publish_uses_the_vcpkg_triplet_yy_thunks_path(self) -> None:
        self.experiment()
        yy_thunks = self.yy_thunks("x64")
        self.assertTrue(yy_thunks.is_file())
        run = UnityRun()

        self.unity(run, GhDownload(zipball("AssetRipper-1234567/Source/marker.txt")),
                   architectures=("x64",), configurations=("Release",))

        publish = run.with_project("UnityProbe.csproj")
        self.assertEqual(len(publish), 1)
        self.assertEqual(_option(publish[0].argv, "-p:UnityYYThunksObject="), str(yy_thunks))

    def test_unity_build_prepares_vendor_builds_publishes_and_stages(self) -> None:
        self.experiment()
        yys = {arch: self.yy_thunks(arch) for arch in ("x86", "x64")}
        run = UnityRun()
        download = GhDownload(zipball("AssetRipper-1234567/Source/marker.txt"))
        staged = self.unity(run, download)

        self.assertEqual(download.calls, [
            (str(self.gh), "api", f"repos/{UPSTREAM_REPOSITORY}/zipball/{UPSTREAM_REVISION}")])
        patch_calls = [call for call in run.calls if "apply" in call.argv]
        forward = [call for call in patch_calls if "--reverse" not in call.argv]
        self.assertEqual(len(forward), 1)
        self.assertIn(str(self.repo / PATCH_FILE), forward[0].argv)
        # The applied bytes are confirmed with a real reverse check before any receipt is trusted.
        self.assertTrue(
            any("--reverse" in call.argv and "--check" in call.argv for call in patch_calls))
        self.assertTrue((self.vendor() / "Source" / "marker.txt").is_file())
        receipt = json.loads((self.vendor() / main.UNITY_VENDOR_RECEIPT).read_text(encoding="utf-8"))
        self.assertEqual(receipt["sourceRepository"], UPSTREAM_REPOSITORY)
        self.assertEqual(receipt["sourceRevision"], UPSTREAM_REVISION)
        self.assertEqual(receipt["patchSha256"], hashlib.sha256(PATCH_BYTES).hexdigest())

        observer = run.with_project("UnityObserver.vcxproj")
        self.assertEqual(len(observer), 2)
        self.assertEqual({_option(call.argv, "/p:Platform=") for call in observer}, {"Win32", "x64"})
        for call in observer:
            self.assertIn("/p:Configuration=Release", call.argv)

        publish = run.with_project("UnityProbe.csproj")
        self.assertEqual(len(publish), 2)
        self.assertEqual({_option(call.argv, "win-") for call in publish}, {"x86", "x64"})
        for call in publish:
            rid = _option(call.argv, "win-")
            self.assertIn("-m:1", call.argv)
            library = _option(call.argv, "-p:UnityObserverLibrary=")
            self.assertTrue(library is not None and Path(library).name == "UnityObserver.lib")
            self.assertEqual(_option(call.argv, "-p:UnityYYThunksObject="),
                             str(yys["x86" if rid.endswith("x86") else "x64"]))

        self.assertEqual(sorted(path.name for path in staged), ["unity.so", "unity.so"])
        for architecture in ("x86", "x64"):
            stage = main.bin_directory(self.repo, architecture, "Release")
            self.assertEqual((stage / "unity.so").read_bytes(), _publish_dll(architecture))
            self.assertEqual((stage / "unity.pdb").read_bytes(), b"publish symbols\n")

    def test_unity_build_fails_closed_without_the_observer_library(self) -> None:
        self.experiment()
        self.yy_thunks("x64")
        run = UnityRun(observer_library=False)
        with self.assertRaises(main.BuildError):
            self.unity(run, GhDownload(zipball("AssetRipper-1234567/Source/marker.txt")),
                       architectures=("x64",), configurations=("Release",))
        self.assertEqual(run.with_project("UnityProbe.csproj"), [])

    def test_unity_build_fails_closed_without_the_published_dll(self) -> None:
        self.experiment()
        self.yy_thunks("x64")
        run = UnityRun(dll=False)
        with self.assertRaises(main.BuildError):
            self.unity(run, GhDownload(zipball("AssetRipper-1234567/Source/marker.txt")),
                       architectures=("x64",), configurations=("Release",))
        self.assertFalse((main.bin_directory(self.repo, "x64", "Release") / "unity.so").exists())

    def test_unity_build_fails_closed_without_the_published_pdb(self) -> None:
        self.experiment()
        self.yy_thunks("x64")
        run = UnityRun(pdb=False)
        with self.assertRaises(main.BuildError):
            self.unity(run, GhDownload(zipball("AssetRipper-1234567/Source/marker.txt")),
                       architectures=("x64",), configurations=("Release",))

    def test_unity_build_fails_closed_without_the_vcpkg_yy_thunks_object(self) -> None:
        self.experiment()
        run = UnityRun()
        with self.assertRaises(main.BuildError):
            self.unity(run, GhDownload(zipball("AssetRipper-1234567/Source/marker.txt")),
                       architectures=("x64",), configurations=("Release",))
        self.assertEqual(run.with_project("UnityProbe.csproj"), [])

    def test_unity_build_propagates_a_tool_failure(self) -> None:
        self.experiment()
        run = UnityRun(fail_when=lambda argv: _basename(argv) == "msbuild.exe")
        with self.assertRaises(subprocess.CalledProcessError):
            self.unity(run, GhDownload(zipball("AssetRipper-1234567/Source/marker.txt")),
                       architectures=("x64",), configurations=("Release",))

    def test_unity_build_requires_the_gh_cli(self) -> None:
        self.experiment()
        self.yy_thunks("x64")

        def missing(name: str) -> Path:
            if name == "dotnet":
                return self.dotnet
            raise FileNotFoundError(f"{name} was not found on PATH")

        run = UnityRun()
        with self.assertRaises(FileNotFoundError):
            self.unity(run, GhDownload(b""), architectures=("x64",),
                       configurations=("Release",), require_tool=missing)


class VendorPreparationTests(UnityCase):
    def _receipt(self, document: dict) -> None:
        path = self.vendor()
        path.mkdir(parents=True, exist_ok=True)
        (path / main.UNITY_VENDOR_RECEIPT).write_text(json.dumps(document), encoding="utf-8")

    def test_existing_vendor_with_a_matching_receipt_is_reused(self) -> None:
        self.experiment()
        self.yy_thunks("x64")
        self._receipt({
            "sourceRepository": UPSTREAM_REPOSITORY,
            "sourceRevision": UPSTREAM_REVISION,
            "patchSha256": hashlib.sha256(PATCH_BYTES).hexdigest(),
        })
        run = UnityRun()
        download = GhDownload(b"")
        self.unity(run, download, architectures=("x64",), configurations=("Release",))
        self.assertEqual(download.calls, [])
        for call in [entry for entry in run.calls if "apply" in entry.argv]:
            self.assertIn("--reverse", call.argv, "a reused tree is only re-checked, never re-patched")

    def test_existing_vendor_with_a_stale_receipt_fails_without_downloading(self) -> None:
        self.experiment()
        self._receipt({
            "sourceRepository": UPSTREAM_REPOSITORY,
            "sourceRevision": "0" * 40,
            "patchSha256": "0" * 64,
        })
        run = UnityRun()
        download = GhDownload(b"")
        with self.assertRaises(main.BuildError):
            self.unity(run, download, architectures=("x64",), configurations=("Release",))
        self.assertEqual(download.calls, [])

    def test_existing_vendor_directory_without_a_receipt_fails(self) -> None:
        self.experiment()
        self.vendor().mkdir(parents=True)
        run = UnityRun()
        download = GhDownload(b"")
        with self.assertRaises(main.BuildError):
            self.unity(run, download, architectures=("x64",), configurations=("Release",))
        self.assertEqual(download.calls, [])

    def test_existing_vendor_path_that_is_a_file_fails(self) -> None:
        self.experiment()
        self.vendor().parent.mkdir(parents=True, exist_ok=True)
        self.vendor().write_bytes(b"not a directory")
        run = UnityRun()
        download = GhDownload(b"")
        with self.assertRaises(main.BuildError):
            self.unity(run, download, architectures=("x64",), configurations=("Release",))
        self.assertEqual(download.calls, [])

    def test_unreadable_pin_file_fails_clearly(self) -> None:
        self.yy_thunks("x64")
        run = UnityRun()
        with self.assertRaises(main.BuildError):
            self.unity(run, GhDownload(b""), architectures=("x64",), configurations=("Release",))

    def test_malformed_pin_file_fails_clearly(self) -> None:
        self.experiment()
        (self.repo / UPSTREAM_FILE).write_text("{ not json", encoding="utf-8")
        self.yy_thunks("x64")
        run = UnityRun()
        with self.assertRaises(main.BuildError):
            self.unity(run, GhDownload(b""), architectures=("x64",), configurations=("Release",))

    def test_incomplete_pin_file_fails_clearly(self) -> None:
        self.experiment()
        (self.repo / UPSTREAM_FILE).write_text(
            json.dumps({"sourceRepository": UPSTREAM_REPOSITORY}), encoding="utf-8")
        self.yy_thunks("x64")
        run = UnityRun()
        with self.assertRaises(main.BuildError):
            self.unity(run, GhDownload(b""), architectures=("x64",), configurations=("Release",))

    def test_gh_download_failure_propagates(self) -> None:
        self.experiment()
        self.yy_thunks("x64")
        run = UnityRun()
        with self.assertRaises(subprocess.CalledProcessError):
            self.unity(run, GhDownload(b"", returncode=9), architectures=("x64",),
                       configurations=("Release",))
        self.assertFalse(self.vendor().exists())

    def test_patch_failure_propagates_without_a_receipt(self) -> None:
        self.experiment()
        self.yy_thunks("x64")
        run = UnityRun(fail_when=lambda argv: _basename(argv) == "git")
        with self.assertRaises(subprocess.CalledProcessError):
            self.unity(run, GhDownload(zipball("AssetRipper-1234567/Source/marker.txt")),
                       architectures=("x64",), configurations=("Release",))
        self.assertTrue((self.vendor() / "Source").is_dir())
        self.assertFalse((self.vendor() / main.UNITY_VENDOR_RECEIPT).exists())

    def test_an_empty_archive_fails_clearly(self) -> None:
        self.experiment()
        self.yy_thunks("x64")
        run = UnityRun()
        with self.assertRaises(main.BuildError):
            self.unity(run, GhDownload(zipball()), architectures=("x64",),
                       configurations=("Release",))
        self.assertFalse(self.vendor().exists())

    def test_an_archive_without_a_source_tree_fails_clearly(self) -> None:
        self.experiment()
        self.yy_thunks("x64")
        run = UnityRun()
        with self.assertRaises(main.BuildError):
            self.unity(run, GhDownload(zipball("AssetRipper-1234567/README.md")),
                       architectures=("x64",), configurations=("Release",))

    def test_a_zip_slip_member_cannot_escape_the_extraction_root(self) -> None:
        self.experiment()
        self.yy_thunks("x64")
        run = UnityRun()
        with self.assertRaises(main.BuildError):
            self.unity(run, GhDownload(zipball("../../escape.txt")), architectures=("x64",),
                       configurations=("Release",))
        self.assertFalse((self.repo / "escape.txt").exists())
        self.assertFalse((self.root / "escape.txt").exists())
        self.assertFalse(self.vendor().exists())


class RealGitPatchTests(unittest.TestCase):
    """The patch route is exercised against a real local Git work tree.

    ``git apply`` resolves patch paths against the top of the enclosing repository and silently
    skips anything outside the directory it runs from, so a vendor tree nested under the repository
    is the regression a mocked ``run`` cannot see: the applied bytes must actually change, an
    unpatched cached tree that carries a forged matching receipt must be refused, and a genuinely
    patched cached tree must be reused.  Only ``git`` runs here; nothing is downloaded or faked.
    """

    PATCH_BODY = (
        "diff --git a/Source/A.cs b/Source/A.cs\n"
        "index 1111111..2222222 100644\n"
        "--- a/Source/A.cs\n"
        "+++ b/Source/A.cs\n"
        "@@ -1,3 +1,3 @@\n"
        " alpha\n"
        "-beta\n"
        "+PATCHED\n"
        " gamma\n"
    )
    ORIGINAL = b"alpha\nbeta\ngamma\n"
    PATCHED = b"alpha\nPATCHED\ngamma\n"

    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        self.vendor = self.repo / main.UNITY_VENDOR
        source = self.vendor / main.UNITY_VENDOR_SOURCE
        source.mkdir(parents=True)
        (source / "A.cs").write_bytes(self.ORIGINAL)
        # The fixture owns its line-ending contract instead of inheriting host Git config.
        (self.repo / ".gitattributes").write_text(
            "* text=auto eol=lf\n", encoding="utf-8")
        (self.repo / main.UNITY_PATCH).parent.mkdir(parents=True)
        (self.repo / main.UNITY_PATCH).write_bytes(self.PATCH_BODY.encode("ascii"))
        (self.repo / main.UNITY_UPSTREAM).write_text(json.dumps({
            "sourceRepository": UPSTREAM_REPOSITORY,
            "sourceRevision": UPSTREAM_REVISION,
        }), encoding="utf-8")
        subprocess.run(["git", "init", "-q"], cwd=self.repo, check=True)
        # Reproduce the hosted runner's EOL policy inside this isolated fixture.
        subprocess.run(
            ["git", "config", "--local", "core.autocrlf", "true"],
            cwd=self.repo, check=True,
        )

    def _write_receipt(self) -> None:
        (self.vendor / main.UNITY_VENDOR_RECEIPT).write_text(json.dumps({
            "sourceRepository": UPSTREAM_REPOSITORY,
            "sourceRevision": UPSTREAM_REVISION,
            "patchSha256": hashlib.sha256((self.repo / main.UNITY_PATCH).read_bytes()).hexdigest(),
        }), encoding="utf-8")

    def _bytes(self) -> bytes:
        return (self.vendor / main.UNITY_VENDOR_SOURCE / "A.cs").read_bytes()

    def test_the_patch_changes_the_nested_vendor_bytes(self) -> None:
        main._unity_patch(self.repo, self.vendor)
        self.assertEqual(self._bytes(), self.PATCHED)

    def test_an_unpatched_cached_tree_with_a_forged_receipt_is_refused(self) -> None:
        self._write_receipt()
        with self.assertRaises(main.BuildError):
            main._unity_vendor(self.repo)
        self.assertEqual(self._bytes(), self.ORIGINAL)

    def test_a_patched_cached_tree_with_a_matching_receipt_is_reused(self) -> None:
        main._unity_patch(self.repo, self.vendor)
        self._write_receipt()
        self.assertEqual(main._unity_vendor(self.repo), self.vendor)
        self.assertEqual(self._bytes(), self.PATCHED)


if __name__ == "__main__":
    unittest.main()
