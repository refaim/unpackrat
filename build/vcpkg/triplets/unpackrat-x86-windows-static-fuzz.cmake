# Standalone libFuzzer executables are never unloaded and keep the static CRT their
# supplied LLVM fuzzer archive is built with. Reuse the address-instrumented ASan recipe
# for the toolset pins and the same /fsanitize=address flags, then restore the static CRT.
include("${CMAKE_CURRENT_LIST_DIR}/unpackrat-x86-windows-static-asan.cmake")
set(VCPKG_CRT_LINKAGE static)

# The Fuzz executables are built with the pinned LLVM clang-cl and link that toolchain's
# clang_rt.asan_dynamic runtime. MSVC's /fsanitize=address instrumentation emits CRT
# interceptor hooks (__asan_wrap_wcscat, __asan_wrap_wcscpy, __asan_wrap_wcsncpy) for the
# UberWolf DXLib translation units; only MSVC's ASan runtime defines them, so an MSVC-built
# Wolf dependency leaves them unresolved in the clang-ASan link. Build just that one
# dependency with the same pinned clang-cl the executable uses, keeping every other
# dependency and its address instrumentation on the pinned v145 toolset. The chainload
# helper selects the compiler, target triple and LLVM runtime /LIBPATH; the outer toolset
# stays v145 with the vcvars environment loaded so the SDK/CRT headers resolve.
if(PORT STREQUAL "uberwolf")
    set(VCPKG_CHAINLOAD_TOOLCHAIN_FILE
        "${CMAKE_CURRENT_LIST_DIR}/../toolchains/wolfrpg-fuzz-clangcl.cmake")
    set(VCPKG_LOAD_VCVARS_ENV ON)
    # vcpkg builds ports in a cleaned environment; the chainload helper reads this
    # variable, so keep it tracked without discarding any caller-provided entries.
    list(APPEND VCPKG_ENV_PASSTHROUGH "UNPACKRAT_LLVM_INSTRUMENTATION")
endif()
