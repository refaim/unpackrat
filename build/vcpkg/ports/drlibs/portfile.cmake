# Keep both adapters on the exact tested dr_libs revision.
vcpkg_download_distfile(DR_MP3_HEADER
    URLS "https://raw.githubusercontent.com/mackron/dr_libs/e381981afa52800b9a3864df548353ff67252fbc/dr_mp3.h"
    FILENAME "dr_libs-e381981afa52800b9a3864df548353ff67252fbc-dr_mp3.h"
    SHA512 7ce97e1adacb140fa2e017e4cd0bbd92ce30c5c9f7c5fd588fe4840b7cdb17d29029fbbffbdbbd1cfb6ec3f42434bebdc02de07c3b0a8cb7b24eae533e9ae51e
)
vcpkg_download_distfile(DR_WAV_HEADER
    URLS "https://raw.githubusercontent.com/mackron/dr_libs/e381981afa52800b9a3864df548353ff67252fbc/dr_wav.h"
    FILENAME "dr_libs-e381981afa52800b9a3864df548353ff67252fbc-dr_wav.h"
    SHA512 7881f7db87815abb132b53af7e2b85549079c13601e393fadcaaa9db3f6edb6bd644c2ecca077815e603bbe3cfecd55e8c4f69e1be5ea98b2e44aacea5c4c1cb
)

set(SOURCE_PATH "${CURRENT_BUILDTREES_DIR}/source")
file(MAKE_DIRECTORY "${SOURCE_PATH}")
file(COPY_FILE "${DR_MP3_HEADER}" "${SOURCE_PATH}/dr_mp3.h")
file(COPY_FILE "${DR_WAV_HEADER}" "${SOURCE_PATH}/dr_wav.h")
file(COPY_FILE "${CMAKE_CURRENT_LIST_DIR}/dr_mp3.c" "${SOURCE_PATH}/dr_mp3.c")
file(COPY_FILE "${CMAKE_CURRENT_LIST_DIR}/dr_wav.c" "${SOURCE_PATH}/dr_wav.c")
file(COPY_FILE "${CMAKE_CURRENT_LIST_DIR}/CMakeLists.txt" "${SOURCE_PATH}/CMakeLists.txt")

vcpkg_cmake_configure(SOURCE_PATH "${SOURCE_PATH}")
vcpkg_cmake_install()
vcpkg_copy_pdbs()
file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/debug/include" "${CURRENT_PACKAGES_DIR}/debug/share")
vcpkg_install_copyright(FILE_LIST "${CMAKE_CURRENT_LIST_DIR}/copyright")
