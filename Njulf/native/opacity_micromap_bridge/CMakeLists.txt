cmake_minimum_required(VERSION 3.24)
project(njulf_omm_bridge LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(NJULF_OMM_SDK_ROOT "" CACHE PATH "Pinned NVIDIA OMM SDK source root")
if(NOT NJULF_OMM_SDK_ROOT)
    message(FATAL_ERROR "Set NJULF_OMM_SDK_ROOT to the reviewed NVIDIA OMM SDK root")
endif()

if(NOT EXISTS "${NJULF_OMM_SDK_ROOT}/CMakeLists.txt" OR
   NOT EXISTS "${NJULF_OMM_SDK_ROOT}/libraries/omm-lib/include/omm.h")
    message(FATAL_ERROR
        "NJULF_OMM_SDK_ROOT must be the reviewed OMM SDK source checkout. "
        "A shared/import-library install is rejected because its dependent SDK "
        "binary would not be covered by the bridge provenance digest.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "The frozen Njulf OMM bridge ABI supports 64-bit hosts only")
endif()

file(READ "${NJULF_OMM_SDK_ROOT}/libraries/omm-lib/include/omm.h" NJULF_OMM_HEADER)
foreach(NJULF_VERSION_PART MAJOR MINOR BUILD)
    string(REGEX MATCH
        "#define[ \t]+OMM_VERSION_${NJULF_VERSION_PART}[ \t]+([0-9]+)"
        NJULF_OMM_VERSION_MATCH
        "${NJULF_OMM_HEADER}")
    if(NOT NJULF_OMM_VERSION_MATCH)
        message(FATAL_ERROR "Could not read OMM_VERSION_${NJULF_VERSION_PART} from omm.h")
    endif()
    set(NJULF_OMM_VERSION_${NJULF_VERSION_PART} "${CMAKE_MATCH_1}")
endforeach()
set(NJULF_OMM_VERSION
    "${NJULF_OMM_VERSION_MAJOR}.${NJULF_OMM_VERSION_MINOR}.${NJULF_OMM_VERSION_BUILD}")
if(NOT NJULF_OMM_VERSION STREQUAL "1.9.2")
    message(FATAL_ERROR
        "Unsupported NVIDIA OMM SDK ${NJULF_OMM_VERSION}; the reviewed version is 1.9.2")
endif()

# Embed the audited SDK and its pinned third-party code into the one bridge
# binary authenticated by AssetTool.  Do not create an unhashed runtime
# dependency on omm-lib.dll/libomm-lib.so.
set(OMM_STATIC_LIBRARY ON CACHE BOOL "Build the pinned OMM SDK statically" FORCE)
set(OMM_ENABLE_TESTS OFF CACHE BOOL "Disable SDK unit tests" FORCE)
set(OMM_BUILD_VIEWER OFF CACHE BOOL "Disable the SDK viewer" FORCE)
set(OMM_BUILD_OMM_GPU_NVRHI OFF CACHE BOOL "Disable the GPU baker" FORCE)
set(OMM_ENABLE_PRECOMPILED_SHADERS_DXIL OFF CACHE BOOL "CPU bridge needs no DXIL" FORCE)
set(OMM_ENABLE_PRECOMPILED_SHADERS_SPIRV OFF CACHE BOOL "CPU bridge needs no SPIR-V" FORCE)
set(OMM_ENABLE_OPENMP OFF CACHE BOOL "Use the SDK internal deterministic worker path" FORCE)
set(OMM_ENABLE_FAST_MATH OFF CACHE BOOL "Preserve audited cutoff arithmetic" FORCE)
set(OMM_LIB_INSTALL OFF CACHE BOOL "Do not install a separate SDK runtime" FORCE)
# ShaderMake is added by the SDK even when both shader outputs are disabled.
# Its default is to download DXC during configure, which is unnecessary for
# this CPU-only target and would make an otherwise pinned build depend on the
# network and an unauthenticated archive.
set(SHADERMAKE_FIND_COMPILERS OFF CACHE BOOL
    "CPU-only bridge must not discover or download shader compilers" FORCE)
set(SHADERMAKE_FIND_DXC OFF CACHE BOOL "Do not download DXC" FORCE)
set(SHADERMAKE_FIND_DXC_VK OFF CACHE BOOL "Do not discover Vulkan SDK DXC" FORCE)
set(SHADERMAKE_FIND_FXC OFF CACHE BOOL "Do not discover Windows SDK FXC" FORCE)
set(SHADERMAKE_FIND_SLANG OFF CACHE BOOL "Do not download Slang" FORCE)
set(OMM_LIB_TARGET_NAME njulf_omm_sdk_static CACHE STRING
    "Private pinned SDK target name" FORCE)
add_subdirectory(
    "${NJULF_OMM_SDK_ROOT}"
    "${CMAKE_CURRENT_BINARY_DIR}/pinned-omm-sdk"
    EXCLUDE_FROM_ALL)

add_library(njulf_omm_bridge SHARED njulf_omm_bridge.cpp)
target_compile_definitions(njulf_omm_bridge PRIVATE NJULF_OMM_BRIDGE_BUILD=1)
target_include_directories(njulf_omm_bridge PRIVATE
    "${NJULF_OMM_SDK_ROOT}/libraries/omm-lib/include")
target_link_libraries(njulf_omm_bridge PRIVATE njulf_omm_sdk_static)

if(MSVC)
    target_compile_options(njulf_omm_bridge PRIVATE
        /W4 /WX /permissive- /guard:cf /GS /sdl)
    target_link_options(njulf_omm_bridge PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT)
else()
    target_compile_options(njulf_omm_bridge PRIVATE
        -Wall -Wextra -Wpedantic -Werror -fvisibility=hidden
        -fstack-protector-strong)
endif()
