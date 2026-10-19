# Standalone libFuzzer executables are never unloaded and keep the static CRT their
# supplied LLVM fuzzer archive is built with. Reuse the address-instrumented ASan recipe
# for the toolset pins and the same /fsanitize=address flags, then restore the static CRT.
include("${CMAKE_CURRENT_LIST_DIR}/unpackrat-x64-windows-static-asan.cmake")
set(VCPKG_CRT_LINKAGE static)

# The Fuzz executables are built with the pinned LLVM clang-cl and link that toolchain's
# clang_rt.asan_dynamic runtime. MSVC's /fsanitize=address instrumentation emits CRT
# interceptor hooks (__asan_wrap_wcscat, __asan_wrap_wcscpy, __asan_wrap_wcsncpy) for the
# UberWolf DXLib translation units; only MSVC's ASan runtime defines them, so an MSVC-built
# Wolf dependency leaves them unresolved in the clang-ASan link. Build just that one
# dependency with the same ClangCL toolset the executable uses, keeping every other
# dependency and its address instrumentation on the pinned v145 toolset.
if(PORT STREQUAL "uberwolf")
    set(VCPKG_PLATFORM_TOOLSET ClangCL)
    # The recipe can only honour ``-T<toolset>`` through the VS generator, and the VS ClangCL
    # toolset needs the operator's exact pinned LLVM root. Fail loudly instead of quietly
    # falling back to a toolset whose instrumentation cannot link.
    if(NOT DEFINED ENV{UNPACKRAT_LLVM_INSTRUMENTATION})
        message(FATAL_ERROR
            "The Fuzz uberwolf dependency needs UNPACKRAT_LLVM_INSTRUMENTATION (the paired "
            "LLVM pin root) to build with the matching ClangCL toolset.")
    endif()
    file(GLOB _unpackrat_llvm_clang_dirs
        "$ENV{UNPACKRAT_LLVM_INSTRUMENTATION}/x64/lib/clang/*")
    if(NOT _unpackrat_llvm_clang_dirs)
        message(FATAL_ERROR
            "No pinned LLVM headers under "
            "$ENV{UNPACKRAT_LLVM_INSTRUMENTATION}/x64/lib/clang.")
    endif()
    list(GET _unpackrat_llvm_clang_dirs -1 _unpackrat_llvm_clang_dir)
    get_filename_component(_unpackrat_llvm_tools_version "${_unpackrat_llvm_clang_dir}" NAME)
    list(APPEND VCPKG_CMAKE_CONFIGURE_OPTIONS
        "-DCMAKE_VS_GLOBALS=LLVMInstallDir=$ENV{UNPACKRAT_LLVM_INSTRUMENTATION}/x64\\;LLVMToolsVersion=${_unpackrat_llvm_tools_version}")
endif()
