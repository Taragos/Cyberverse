add_library(${CMAKE_PROJECT_NAME} SHARED "")

set(PROJECT_HEADER_FILES
  Main.h
  NetworkGameSystem.h
  Utils.h
  PlayerActionTracker.h
        PlayerSync/InterpolationData.h
        PlayerSync/RemoteLocomotion.h
        PlayerSync/RemoteMotion.h
        PlayerSync/LookParts.h
        PlayerSync/LookTypes.h
        PlayerSync/LookTable.h
        CommandLine.h
)

set(PROJECT_SRC_FILES
  Main.cpp
  NetworkGameSystem.cpp
  PlayerActionTracker.cpp
  PlayerSync/RemoteLocomotion.cpp
  PlayerSync/LookParts.cpp
  PlayerSync/LookTable.cpp
)

source_group(TREE "${CMAKE_CURRENT_SOURCE_DIR}" FILES ${PROJECT_HEADER_FILES} ${PROJECT_SRC_FILES})

target_include_directories(${CMAKE_PROJECT_NAME} PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_sources(${CMAKE_PROJECT_NAME} PRIVATE ${PROJECT_HEADER_FILES} ${PROJECT_SRC_FILES})

target_link_libraries(${CMAKE_PROJECT_NAME} PRIVATE RED4ext::SDK)
target_link_libraries(${CMAKE_PROJECT_NAME} PRIVATE zpp_bits)
target_link_libraries(${CMAKE_PROJECT_NAME} PRIVATE GameNetworkingSockets::shared)
target_link_libraries(${CMAKE_PROJECT_NAME} PRIVATE RedLib)
target_link_libraries(${CMAKE_PROJECT_NAME} PRIVATE protocol)
# ShellExecuteW, to open the login link in the browser.
target_link_libraries(${CMAKE_PROJECT_NAME} PRIVATE shell32)

# vcpkg copies the dependencies' DLLs next to the plugin with PowerShell, which doesn't exist when cross-compiling from
# Linux (VCPKG_APPLOCAL_DEPS is off there), so copy them with CMake instead.
if(CMAKE_HOST_UNIX)
  add_custom_command(TARGET ${CMAKE_PROJECT_NAME} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_RUNTIME_DLLS:${CMAKE_PROJECT_NAME}> $<TARGET_FILE_DIR:${CMAKE_PROJECT_NAME}>
    COMMAND_EXPAND_LISTS)
endif()

# CYBERVERSE_VERSION (the mod version sent at login), from git describe.
include("${CMAKE_CURRENT_SOURCE_DIR}/../../../shared/version/CyberverseVersion.cmake")
cyberverse_version_header(${CMAKE_PROJECT_NAME})
