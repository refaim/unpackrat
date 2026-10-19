# Chainload toolchain used for the UberWolf Fuzz dependency only.
#
# The Fuzz executables already build with the operator's pinned LLVM clang-cl; this file
# makes the ``uberwolf`` dependency build with that same pinned clang-cl so the
# clang-ASan runtime its address instrumentation needs is the one that actually links.
# Every other dependency keeps the pinned MSVC v145 toolset; the executables' own toolset
# is unchanged here.
#
# Contract with the caller (build/vcpkg/triplets/unpackrat-<arch>-windows-static-fuzz.cmake):
#   * VCPKG_PLATFORM_TOOLSET=v145, VCPKG_LOAD_VCVARS_ENV=ON and VCPKG_CRT_LINKAGE=static
#     are already set, and VCPKG_TARGET_ARCHITECTURE is populated from the triplet.
#   * This file is exported as VCPKG_CHAINLOAD_TOOLCHAIN_FILE.
#   * CI has already verified the pinned LLVM 21.1.8 x64 clang-cl/lld-link pair exists.
#
# This file only arranges CMake configuration metadata: it points the configure at the
# pinned clang-cl/lld-link, maps the vcpkg architecture onto the LLVM triple, adds the
# architecture-matched compiler-rt directory to the linker search path, and then defers
# to the stock vcpkg Windows toolchain for CRT/SDK/charset/flag handling.

include_guard(GLOBAL)

# The pinned LLVM root must be supplied through the environment. Without it there is no
# compiler to point at, so fail loudly rather than fall back to a toolset whose
# instrumentation cannot link.
if(NOT DEFINED ENV{UNPACKRAT_LLVM_INSTRUMENTATION} OR "$ENV{UNPACKRAT_LLVM_INSTRUMENTATION}" STREQUAL "")
    message(FATAL_ERROR
        "The Wolf RPG Fuzz dependency builds with the pinned LLVM clang-cl, so "
        "UNPACKRAT_LLVM_INSTRUMENTATION (the paired LLVM root) must be set in the "
        "environment before this chainload toolchain runs.")
endif()
file(TO_CMAKE_PATH "$ENV{UNPACKRAT_LLVM_INSTRUMENTATION}" _unpackrat_llvm_root)

# vcpkg supplies the target architecture from the triplet. Map it onto the LLVM triple
# the pinned clang-cl should emit code for, and refuse anything else.
if(NOT DEFINED VCPKG_TARGET_ARCHITECTURE OR VCPKG_TARGET_ARCHITECTURE STREQUAL "")
    message(FATAL_ERROR
        "VCPKG_TARGET_ARCHITECTURE must be defined by the Fuzz triplet before the "
        "Wolf RPG chainload toolchain runs.")
elseif(VCPKG_TARGET_ARCHITECTURE STREQUAL "x86")
    set(_unpackrat_llvm_triple "i686-pc-windows-msvc")
elseif(VCPKG_TARGET_ARCHITECTURE STREQUAL "x64")
    set(_unpackrat_llvm_triple "x86_64-pc-windows-msvc")
else()
    message(FATAL_ERROR
        "The Wolf RPG Fuzz chainload toolchain only supports the x86 and x64 "
        "architectures, but VCPKG_TARGET_ARCHITECTURE is "
        "'${VCPKG_TARGET_ARCHITECTURE}'.")
endif()

# Point configure at the pinned toolchain. CMake fails on its own if these binaries are
# missing, so no existence probe is needed here.
set(CMAKE_C_COMPILER "${_unpackrat_llvm_root}/x64/bin/clang-cl.exe")
set(CMAKE_CXX_COMPILER "${_unpackrat_llvm_root}/x64/bin/clang-cl.exe")
set(CMAKE_LINKER "${_unpackrat_llvm_root}/x64/bin/lld-link.exe")
set(CMAKE_C_COMPILER_TARGET "${_unpackrat_llvm_triple}")
set(CMAKE_CXX_COMPILER_TARGET "${_unpackrat_llvm_triple}")

# The architecture-matched compiler-rt lives beside the headers under
# <root>/<arch>/lib/clang/<major>/lib/windows. The LLVM resource major is confirmed by
# the existing CI preflight. Require the directory so a mismatched pin fails clearly.
set(_unpackrat_llvm_runtime_dir
    "${_unpackrat_llvm_root}/${VCPKG_TARGET_ARCHITECTURE}/lib/clang/21/lib/windows")
if(NOT IS_DIRECTORY "${_unpackrat_llvm_runtime_dir}")
    message(FATAL_ERROR
        "The pinned LLVM root has no architecture-matched runtime library directory at "
        "'${_unpackrat_llvm_runtime_dir}'. The Wolf RPG Fuzz dependency needs it on the "
        "linker search path.")
endif()

# Preserve any incoming linker flags and add the runtime directory, so both CMake's own
# compiler checks and the final target link resolve clang_rt from the matching arch.
set(_unpackrat_llvm_runtime_linkflag "\"/LIBPATH:${_unpackrat_llvm_runtime_dir}\"")
if(DEFINED VCPKG_LINKER_FLAGS AND NOT VCPKG_LINKER_FLAGS STREQUAL "")
    string(APPEND VCPKG_LINKER_FLAGS " ${_unpackrat_llvm_runtime_linkflag}")
else()
    set(VCPKG_LINKER_FLAGS "${_unpackrat_llvm_runtime_linkflag}")
endif()

# Locate the stock vcpkg Windows toolchain instead of copying it, so CRT/SDK/charset and
# flag handling stay owned by vcpkg. Z_VCPKG_ROOT_DIR is not yet defined this early (the
# integration includes this chainload before initializing it), so derive <vcpkg>/scripts
# from CMAKE_TOOLCHAIN_FILE and fall back to $VCPKG_ROOT.
set(_unpackrat_stock_windows_toolchain "")
if(DEFINED CMAKE_TOOLCHAIN_FILE AND NOT CMAKE_TOOLCHAIN_FILE STREQUAL "")
    # <vcpkg>/scripts/buildsystems/vcpkg.cmake -> <vcpkg>/scripts
    get_filename_component(_unpackrat_tc_dir "${CMAKE_TOOLCHAIN_FILE}" DIRECTORY)
    get_filename_component(_unpackrat_scripts_dir "${_unpackrat_tc_dir}" DIRECTORY)
    set(_unpackrat_candidate "${_unpackrat_scripts_dir}/toolchains/windows.cmake")
    if(EXISTS "${_unpackrat_candidate}")
        set(_unpackrat_stock_windows_toolchain "${_unpackrat_candidate}")
    endif()
endif()

if(NOT _unpackrat_stock_windows_toolchain AND DEFINED ENV{VCPKG_ROOT} AND NOT "$ENV{VCPKG_ROOT}" STREQUAL "")
    file(TO_CMAKE_PATH "$ENV{VCPKG_ROOT}" _unpackrat_vcpkg_root)
    set(_unpackrat_candidate "${_unpackrat_vcpkg_root}/scripts/toolchains/windows.cmake")
    if(EXISTS "${_unpackrat_candidate}")
        set(_unpackrat_stock_windows_toolchain "${_unpackrat_candidate}")
    endif()
endif()

if(NOT _unpackrat_stock_windows_toolchain)
    message(FATAL_ERROR
        "The Wolf RPG Fuzz chainload toolchain could not locate the stock vcpkg Windows "
        "toolchain (scripts/toolchains/windows.cmake). It looked beside "
        "CMAKE_TOOLCHAIN_FILE ('${CMAKE_TOOLCHAIN_FILE}') and under $ENV{VCPKG_ROOT}.")
endif()

# The compiler, target and runtime path are set above so the stock toolchain's
# clang-aware flag handling observes a clang-cl configuration.
include("${_unpackrat_stock_windows_toolchain}")
