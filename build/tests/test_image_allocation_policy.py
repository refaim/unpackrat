"""Compile the real image allocation policy with both pinned Clang Windows targets.

The rejection flag is observed only by the x64 stack-unwinding test. Clang 23 rejects
an x86 definition that is merely assigned, while the shared allocation-index policy
must still compile on both architectures.
"""

from pathlib import Path
import subprocess
import unittest

from test_coverage_counter_updates import PLATFORM_TRIPLES, _probe_compiler
from test_wolfrpg_keycreate_scratch import _vcvars_environment


SOURCE = Path(__file__).resolve().parents[2] / "src/tests/unit/image_codecs.cpp"


class ImageAllocationPolicyCompileTests(unittest.TestCase):
    def test_the_real_debug_policy_is_warning_clean_for_both_architectures(self):
        text = SOURCE.read_text(encoding="utf-8")
        start = text.index("    thread_local std::size_t allocation_count = 0;")
        stop = text.index("    enum class png_outcome", start)
        policy = text[start:stop]
        # Retain the actual x64-only observation that feeds CHECK_FALSE(rejected).
        # The shared observation below exercises the real index-based rejection state.
        observation = "        rejected = target_rejected;"
        self.assertIn(observation, text)
        self.assertIn("    CHECK_FALSE(rejected);", text)
        subject = (
            "#include <array>\n#include <cstddef>\n#include <crtdbg.h>\n#include <windows.h>\n"
            "namespace {\n" + policy + "}\n"
            "int allocation_policy_probe() {\n"
            "    scoped_allocation_policy policy(1);\n"
            "    if (allocation_hook(_HOOK_ALLOC, nullptr, 16, _NORMAL_BLOCK, 0, nullptr, 0) != 0 ||\n"
            "        rejected_at != 1) return 1;\n"
            "#ifdef _WIN64\n"
            "    bool rejected = false;\n" + observation + "\n"
            "    if (rejected) return 2;\n"
            "#endif\n"
            "    return 0;\n}\n"
        )
        environment, _ = _vcvars_environment()
        compiler = _probe_compiler()
        for platform, triple in PLATFORM_TRIPLES.items():
            with self.subTest(platform=platform):
                result = subprocess.run(
                    [str(compiler), "/nologo", "/W4", "/WX", "/Zs", "/EHsc", "/D_DEBUG",
                     "/clang:-std=c++23", f"/clang:--target={triple}", "/TP", "-"],
                    input=subject, env=environment, capture_output=True, text=True, encoding="utf-8",
                )
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
