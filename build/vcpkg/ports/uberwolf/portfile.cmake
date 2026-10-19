vcpkg_check_linkage(ONLY_STATIC_LIBRARY)

# Source recovery candidate wolfrpg-recovery-e1-contract-v2.
# Nine exact original patches precede a source-verified recovery delta.
vcpkg_from_github(
    OUT_SOURCE_PATH SOURCE_PATH
    REPO Sinflower/UberWolf
    REF f4191375d289171b1f29dbc5ca320133b2b4b96a
    SHA512 28105836e9fb6af1b380d72066b57007316aa28c20480e0e1583a2252bdb27e3420d6d8b13433283868dd60236d629818453208836b1e7824eeffa143240a725
    PATCHES
        version.patch
        x86-size-overloads.patch
        wolfx-file-size.patch
        default-crypt-modes.patch
        mode-selection.patch
        archive-callbacks.patch
        archive-entry-plan.patch
        bounded-huffman.patch
        keycreate-scratch.patch
        recovery-e1-contract.patch
        encoding-aware-legacy.patch
)

file(COPY "${CMAKE_CURRENT_LIST_DIR}/CMakeLists.txt" DESTINATION "${SOURCE_PATH}")

# The Fuzz configuration builds this dependency with the pinned LLVM ClangCL toolset so its
# address instrumentation matches the clang_rt runtime the Fuzz executables link (see the
# unpackrat-*-windows-static-fuzz triplets).  ``-T<toolset>`` only reaches the generator when
# the Visual Studio generator is used, so request MSBuild in exactly that case and keep the
# default Ninja generator for the ordinary MSVC Release/Debug/ASan/UBSan configurations.
if(VCPKG_PLATFORM_TOOLSET STREQUAL "ClangCL")
    vcpkg_cmake_configure(
        SOURCE_PATH "${SOURCE_PATH}"
        WINDOWS_USE_MSBUILD
    )
else()
    vcpkg_cmake_configure(SOURCE_PATH "${SOURCE_PATH}")
endif()
vcpkg_cmake_install()
vcpkg_copy_pdbs()

# The public headers install once, so the debug copies vcpkg would normally keep are
# redundant and are removed together with the empty debug share tree.
file(REMOVE_RECURSE
    "${CURRENT_PACKAGES_DIR}/debug/include"
    "${CURRENT_PACKAGES_DIR}/debug/share"
)

vcpkg_install_copyright(FILE_LIST "${CMAKE_CURRENT_LIST_DIR}/copyright")
