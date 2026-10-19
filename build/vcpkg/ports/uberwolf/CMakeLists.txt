cmake_minimum_required(VERSION 3.20)
project(uberwolf LANGUAGES C CXX)

find_package(nlohmann_json CONFIG REQUIRED)

# The pinned UberWolfLib, built from the exact source files the Wolf RPG module needs.
# Header-only WolfCrypt/WolfX helpers arrive through the UberWolfLib include path.
set(UBERWOLF_SOURCES
    "3rdParty/DXLib/CharCode.cpp"
    "3rdParty/DXLib/CharCodeTable.cpp"
    "3rdParty/DXLib/DXArchive.cpp"
    "3rdParty/DXLib/DXArchiveVer5.cpp"
    "3rdParty/DXLib/DXArchiveVer6.cpp"
    "3rdParty/DXLib/FileLib.cpp"
    "3rdParty/DXLib/Huffman.cpp"
    "3rdParty/lz4/lz4.c"
    "UberWolfLib/Localizer.cpp"
    "UberWolfLib/GameContext.cpp"
    "UberWolfLib/UberLog.cpp"
    "UberWolfLib/UberWolfLib.cpp"
    "UberWolfLib/WolfDec.cpp"
    "UberWolfLib/WolfPro.cpp"
    "UberWolfLib/WolfUtils.cpp"
    "UberWolfLib/WolfXWrapper.cpp"
)

add_library(uberwolf STATIC ${UBERWOLF_SOURCES})
add_library(uberwolf::uberwolf ALIAS uberwolf)

target_compile_features(uberwolf PUBLIC cxx_std_20)

target_compile_definitions(uberwolf PUBLIC
    UNICODE
    _UNICODE
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
    _SILENCE_CXX17_CODECVT_HEADER_DEPRECATION_WARNING
)

# Bracket includes such as <DXLib/DXArchive.h> and the ../UberWolfLib/... fallbacks
# resolve from the build tree; the installed tree mirrors the same layout under the
# namespaced include/uberwolf prefix.
target_include_directories(uberwolf
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty"
        "${CMAKE_CURRENT_SOURCE_DIR}/UberWolfLib"
    PUBLIC
        "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}>"
        "$<INSTALL_INTERFACE:include>"
        "$<INSTALL_INTERFACE:include/uberwolf>"
)

# The imported JSON package must precede the upstream bundled3.11 header.
# Imported SYSTEM directories otherwise follow all ordinary /I paths in MSVC.
set_property(TARGET uberwolf PROPERTY NO_SYSTEM_FROM_IMPORTED TRUE)
target_include_directories(uberwolf BEFORE PRIVATE
    "$<TARGET_PROPERTY:nlohmann_json::nlohmann_json,INTERFACE_INCLUDE_DIRECTORIES>")
target_link_libraries(uberwolf PUBLIC nlohmann_json::nlohmann_json)

if(MSVC)
    target_compile_options(uberwolf PRIVATE "$<$<COMPILE_LANGUAGE:CXX>:/EHsc>")
endif()

install(TARGETS uberwolf ARCHIVE DESTINATION lib)

install(DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/UberWolfLib/"
    DESTINATION include/uberwolf
    FILES_MATCHING
        PATTERN "*.h"
        PATTERN "*.hpp"
)

install(DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/"
    DESTINATION include/uberwolf/DXLib
    FILES_MATCHING
        PATTERN "*.h"
)

# FileCoder.hpp among the installed headers includes <lz4/lz4.h>, so the vendored lz4
# header ships beside it and the original relative include keeps resolving.
install(FILES "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/lz4/lz4.h"
    DESTINATION include/uberwolf/lz4
)

# Test-only source bundle for the maintained FILE-fault harness.
#
# That harness recompiles the EXACT patched 3rdParty/DXLib/DXArchive.cpp so the FILE-side CRT
# success arms of DXArchive::OpenArchiveFileMem become reachable through a scoped forced-include
# shim; the sibling legacy test workers recompile the patched DXArchiveVer5.cpp / DXArchiveVer6.cpp
# so their classic DX1..DX6 kernels are reachable too. The bundle is installed from this same
# SOURCE_PATH (never a transient build tree) beside the library's own archive, and it ships only
# those vendor translation units plus the headers they include by quote or by <UberWolfLib/...> -
# never the unrelated vendor tree. The normal uberwolf.lib build and the shipped module are
# untouched by this bundle.
set(UBERWOLF_TEST_SOURCE_ROOT "share/uberwolf/test-source")

install(FILES
    "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/DXArchive.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/DXArchive.h"
    "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/DXArchiveVer5.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/DXArchiveVer5.h"
    "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/DXArchiveVer6.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/DXArchiveVer6.h"
    "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/CharCode.h"
    "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/DataType.h"
    "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/FileLib.h"
    "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/DXLib/Huffman.h"
    DESTINATION "${UBERWOLF_TEST_SOURCE_ROOT}/3rdParty/DXLib"
)

# The header-only Archive/WolfCrypt helpers the vendor unit includes by <UberWolfLib/...>; the
# whole UberWolfLib header set ships so the include root mirrors the build tree layout exactly.
install(DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/UberWolfLib/"
    DESTINATION "${UBERWOLF_TEST_SOURCE_ROOT}/UberWolfLib"
    FILES_MATCHING
        PATTERN "*.h"
        PATTERN "*.hpp"
)

# DXArchive's FileLib include chain resolves the vendored lz4 header through 3rdParty/lz4, so the
# test-source include root carries the same relative path.
install(FILES "${CMAKE_CURRENT_SOURCE_DIR}/3rdParty/lz4/lz4.h"
    DESTINATION "${UBERWOLF_TEST_SOURCE_ROOT}/3rdParty/lz4"
)
