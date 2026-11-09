cmake_minimum_required(VERSION 3.12)

# Change the project name here!
project(Cyberverse.Red4Ext LANGUAGES CXX)

# Set this option to ON to be able to use folders / filters in your project when using Visual Studio.
set_property(GLOBAL PROPERTY USE_FOLDERS ON)

# Set the C++ standard to 20 (C++20).
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED YES)

# Add RED4ext.SDK as a dependency.

# Uncomment the following line if you want to use the header-only version of RED4ext.SDK.
#option(RED4EXT_HEADER_ONLY "" ON)

add_subdirectory(deps/red4ext.sdk)
set_target_properties(RED4ext.SDK PROPERTIES FOLDER "Dependencies")

# RED4ext.SDK includes a few headers with the wrong case (e.g. RED4ext/rtti/ for the RED4ext/RTTI/ folder), which only
# works on case-insensitive file systems. When cross-compiling from Linux, give those paths symlinks to the real files.
if(CMAKE_HOST_UNIX)
  set(SDK_CASE_FIX_DIR "${CMAKE_BINARY_DIR}/sdk-case-fix")
  set(SDK_INCLUDE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/deps/red4ext.sdk/include")
  foreach(mapping
      "RED4ext/rtti/ERTTIType.hpp=RED4ext/RTTI/ERTTIType.hpp"
      "RED4ext/rtti/IType.hpp=RED4ext/RTTI/IType.hpp"
      "RED4ext/rtti/IType-inl.hpp=RED4ext/RTTI/IType-inl.hpp"
      "RED4ext/Scripting/Natives/AnimKeyFrames.hpp=RED4ext/Scripting/Natives/animKeyFrames.hpp")
    string(REPLACE "=" ";" mapping "${mapping}")
    list(GET mapping 0 included_as)
    list(GET mapping 1 actual_file)
    get_filename_component(link_dir "${SDK_CASE_FIX_DIR}/${included_as}" DIRECTORY)
    file(MAKE_DIRECTORY "${link_dir}")
    file(CREATE_LINK "${SDK_INCLUDE_DIR}/${actual_file}" "${SDK_CASE_FIX_DIR}/${included_as}" SYMBOLIC)
  endforeach()
  target_include_directories(RED4ext.SDK PUBLIC "$<BUILD_INTERFACE:${SDK_CASE_FIX_DIR}>")
endif()

# This will mark the following RED4ext variables as advanced. They won't be displayed in CMake's GUI.
mark_as_advanced(
  RED4EXT_BUILD_EXAMPLES
  RED4EXT_HEADER_ONLY
)

# Include zpp_bits
add_library(zpp_bits INTERFACE)
target_include_directories(zpp_bits INTERFACE "deps/zpp_bits")

# Include Valve's GameNetworkingSockets
find_package(GameNetworkingSockets CONFIG REQUIRED)

# Include red lib
add_compile_definitions(NOMINMAX)
add_subdirectory(deps/red-lib)

# Include our protocol headers
add_library(protocol INTERFACE)
target_include_directories(protocol INTERFACE "../../shared/protocol")

# Include the main project.
add_subdirectory(src)
