# Keep both adapters on the exact tested dr_libs revision.
vcpkg_download_distfile(DR_MP3_HEADER
    URLS "https://raw.githubusercontent.com/mackron/dr_libs/f12e197fd462e4c22873fbd70d11b1d2f2c1dc3c/dr_mp3.h"
    FILENAME "dr_libs-f12e197fd462e4c22873fbd70d11b1d2f2c1dc3c-dr_mp3.h"
    SHA512 d31c0161928d7e3c2158b9a79a8db2b1ac37b8c81d4087ea499bb37f6b2591e6922e1288ae12047907da8c91ee828832f968b7cff44f8fd81e681a7b69ccbde9
)
vcpkg_download_distfile(DR_WAV_HEADER
    URLS "https://raw.githubusercontent.com/mackron/dr_libs/f12e197fd462e4c22873fbd70d11b1d2f2c1dc3c/dr_wav.h"
    FILENAME "dr_libs-f12e197fd462e4c22873fbd70d11b1d2f2c1dc3c-dr_wav.h"
    SHA512 88a622fa07f7bea5a4467340743ee20c7987170a2dba7579a5f44aff2d5e24ce73d9294cf591df8740df4bba6b64dc9ee18e5a21173b54ba72e649175c91e255
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
