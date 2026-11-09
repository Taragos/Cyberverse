# x64 Windows, MSVC ABI, built on Linux with clang-cl + lld-link + xwin (see ../toolchain-clangcl.cmake).
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE dynamic)
# GameNetworkingSockets can only be a DLL; its dependencies (protobuf, abseil, utf8-range) are linked into it
# statically. protobuf 5.29 doesn't compile as a DLL with clang-cl (dllexport of DynamicMapField instantiates
# Map<MapKey, MapValueRef> while MapValueRef is incomplete), and this way the plugin ships one DLL fewer.
set(VCPKG_LIBRARY_LINKAGE static)
if(PORT STREQUAL "gamenetworkingsockets")
    set(VCPKG_LIBRARY_LINKAGE dynamic)
endif()
# No VCPKG_CMAKE_SYSTEM_NAME: vcpkg only sets VCPKG_TARGET_IS_WINDOWS when it is empty ("Windows" doesn't count), and
# ports rely on that. The chainloaded toolchain sets CMAKE_SYSTEM_NAME itself.
set(VCPKG_CHAINLOAD_TOOLCHAIN_FILE "${CMAKE_CURRENT_LIST_DIR}/../toolchain-clangcl.cmake")
# Only release: the plugin ships as Release, and xwin's debug CRT would add another download.
set(VCPKG_BUILD_TYPE release)
set(VCPKG_ENV_PASSTHROUGH XWIN_DIR LLVM_SUFFIX)
