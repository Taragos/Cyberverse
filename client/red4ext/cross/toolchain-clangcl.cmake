# Cross-compiles for x64 Windows (MSVC ABI) from Linux with clang-cl and lld-link. The MSVC CRT and Windows SDK
# headers and libraries come from xwin ("xwin splat"), found in $XWIN_DIR (default /opt/xwin).
# MinGW is not an option: RED4ext.SDK mirrors the game's MSVC-compiled classes, so the plugin needs the MSVC ABI.
include_guard(GLOBAL)

set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_VERSION 10.0)
set(CMAKE_SYSTEM_PROCESSOR AMD64)

if(DEFINED ENV{XWIN_DIR})
    set(XWIN_DIR "$ENV{XWIN_DIR}")
else()
    set(XWIN_DIR "/opt/xwin")
endif()
if(DEFINED ENV{LLVM_SUFFIX})
    set(LLVM_SUFFIX "$ENV{LLVM_SUFFIX}")
else()
    set(LLVM_SUFFIX "")
endif()

set(CMAKE_C_COMPILER "clang-cl${LLVM_SUFFIX}")
set(CMAKE_CXX_COMPILER "clang-cl${LLVM_SUFFIX}")
set(CMAKE_LINKER "lld-link${LLVM_SUFFIX}")
set(CMAKE_AR "llvm-lib${LLVM_SUFFIX}")
set(CMAKE_RC_COMPILER "llvm-rc${LLVM_SUFFIX}")
set(CMAKE_MT "llvm-mt${LLVM_SUFFIX}")
# CMake asks the linker to embed manifests through mt.exe; lld-link does that on its own.
set(CMAKE_LINKER_TYPE LLD)

set(_xwin_includes
    "${XWIN_DIR}/crt/include"
    "${XWIN_DIR}/sdk/include/ucrt"
    "${XWIN_DIR}/sdk/include/um"
    "${XWIN_DIR}/sdk/include/shared"
    "${XWIN_DIR}/sdk/include/winrt"
)
set(_compile_flags "--target=x86_64-pc-windows-msvc -fms-compatibility-version=19.44")
set(_rc_flags "")
foreach(_dir IN LISTS _xwin_includes)
    string(APPEND _compile_flags " /imsvc \"${_dir}\"")
    string(APPEND _rc_flags " -I \"${_dir}\"")
endforeach()

set(_link_flags "/libpath:\"${XWIN_DIR}/crt/lib/x86_64\" /libpath:\"${XWIN_DIR}/sdk/lib/um/x86_64\" /libpath:\"${XWIN_DIR}/sdk/lib/ucrt/x86_64\" /manifest:no")

set(CMAKE_C_FLAGS_INIT "${_compile_flags}")
set(CMAKE_CXX_FLAGS_INIT "${_compile_flags}")
set(CMAKE_RC_FLAGS_INIT "${_rc_flags}")
set(CMAKE_EXE_LINKER_FLAGS_INIT "${_link_flags}")
set(CMAKE_SHARED_LINKER_FLAGS_INIT "${_link_flags}")
set(CMAKE_MODULE_LINKER_FLAGS_INIT "${_link_flags}")

# xwin only ships the release CRT (no msvcrtd.lib), so CMake's compiler checks must link as Release too.
set(CMAKE_TRY_COMPILE_CONFIGURATION Release)

set(CMAKE_FIND_ROOT_PATH "${XWIN_DIR}")
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
