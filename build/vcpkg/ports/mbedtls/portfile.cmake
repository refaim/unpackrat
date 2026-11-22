# Use the complete official LTS release: GitHub snapshots omit TF-PSA-Crypto,
# its nested framework submodule and generated source files.
vcpkg_check_linkage(ONLY_STATIC_LIBRARY)

vcpkg_download_distfile(ARCHIVE
    URLS "https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-${VERSION}/mbedtls-${VERSION}.tar.bz2"
    FILENAME "mbedtls-${VERSION}.tar.bz2"
    SHA512 c9bd7086ab3cbc3948ceb4231772262dcf1322409069d1c0e0a8b75aaba0661dd513b3cbe497b2ca3bb6388dab2432ea600f4d78a6b5431ac91c94e12b2ca6e2
)
vcpkg_extract_source_archive(SOURCE_PATH
    ARCHIVE "${ARCHIVE}"
    PATCHES static-library-name.patch
)

# The MSVC runtime must follow the triplet's VCPKG_CRT_LINKAGE: a static CRT
# selects MSVC_STATIC_RUNTIME=ON and a dynamic CRT selects it OFF.
if(VCPKG_CRT_LINKAGE STREQUAL "static")
    set(MSVC_STATIC_RUNTIME ON)
else()
    set(MSVC_STATIC_RUNTIME OFF)
endif()

vcpkg_cmake_configure(
    SOURCE_PATH "${SOURCE_PATH}"
    OPTIONS
        -DENABLE_TESTING=OFF
        -DENABLE_PROGRAMS=OFF
        -DGEN_FILES=OFF
        -DMBEDTLS_FATAL_WARNINGS=OFF
        -DUSE_STATIC_MBEDTLS_LIBRARY=ON
        -DUSE_SHARED_MBEDTLS_LIBRARY=OFF
        -DMSVC_STATIC_RUNTIME=${MSVC_STATIC_RUNTIME}
    OPTIONS_DEBUG
        -DINSTALL_MBEDTLS_HEADERS=OFF
        -DINSTALL_TF_PSA_CRYPTO_HEADERS=OFF
)
vcpkg_cmake_install()
vcpkg_copy_pdbs()
vcpkg_fixup_pkgconfig()
vcpkg_cmake_config_fixup(DO_NOT_DELETE_PARENT_CONFIG_PATH CONFIG_PATH "lib/cmake/MbedTLS")
vcpkg_cmake_config_fixup(PACKAGE_NAME TF-PSA-Crypto CONFIG_PATH "lib/cmake/TF-PSA-Crypto")

file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/debug/include")
file(INSTALL "${CMAKE_CURRENT_LIST_DIR}/usage" DESTINATION "${CURRENT_PACKAGES_DIR}/share/${PORT}")
vcpkg_install_copyright(FILE_LIST "${SOURCE_PATH}/LICENSE" "${SOURCE_PATH}/tf-psa-crypto/LICENSE")
