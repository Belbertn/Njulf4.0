# Install script for directory: D:/Code/C#/Njulf4.0-Simplified/Njulf/.codex-tmp/omm-sdk-audit/external

# Set the install prefix
if(NOT DEFINED CMAKE_INSTALL_PREFIX)
  set(CMAKE_INSTALL_PREFIX "D:/Code/C#/Njulf4.0-Simplified/Njulf/.codex-tmp/omm-bridge-pinned-build/install")
endif()
string(REGEX REPLACE "/$" "" CMAKE_INSTALL_PREFIX "${CMAKE_INSTALL_PREFIX}")

# Set the install configuration name.
if(NOT DEFINED CMAKE_INSTALL_CONFIG_NAME)
  if(BUILD_TYPE)
    string(REGEX REPLACE "^[^A-Za-z0-9_]+" ""
           CMAKE_INSTALL_CONFIG_NAME "${BUILD_TYPE}")
  else()
    set(CMAKE_INSTALL_CONFIG_NAME "Release")
  endif()
  message(STATUS "Install configuration: \"${CMAKE_INSTALL_CONFIG_NAME}\"")
endif()

# Set the component getting installed.
if(NOT CMAKE_INSTALL_COMPONENT)
  if(COMPONENT)
    message(STATUS "Install component: \"${COMPONENT}\"")
    set(CMAKE_INSTALL_COMPONENT "${COMPONENT}")
  else()
    set(CMAKE_INSTALL_COMPONENT)
  endif()
endif()

# Is this installation the result of a crosscompile?
if(NOT DEFINED CMAKE_CROSSCOMPILING)
  set(CMAKE_CROSSCOMPILING "FALSE")
endif()

if(NOT CMAKE_INSTALL_LOCAL_ONLY)
  # Include the install script for each subdirectory.
  include("D:/Code/C#/Njulf4.0-Simplified/Njulf/.codex-tmp/omm-bridge-pinned-build/pinned-omm-sdk/external/glm/cmake_install.cmake")
  include("D:/Code/C#/Njulf4.0-Simplified/Njulf/.codex-tmp/omm-bridge-pinned-build/pinned-omm-sdk/external/lz4/build/cmake/cmake_install.cmake")
  include("D:/Code/C#/Njulf4.0-Simplified/Njulf/.codex-tmp/omm-bridge-pinned-build/pinned-omm-sdk/external/ShaderMake/cmake_install.cmake")
  include("D:/Code/C#/Njulf4.0-Simplified/Njulf/.codex-tmp/omm-bridge-pinned-build/pinned-omm-sdk/external/xxHash/build/cmake/cmake_install.cmake")

endif()

string(REPLACE ";" "\n" CMAKE_INSTALL_MANIFEST_CONTENT
       "${CMAKE_INSTALL_MANIFEST_FILES}")
if(CMAKE_INSTALL_LOCAL_ONLY)
  file(WRITE "D:/Code/C#/Njulf4.0-Simplified/Njulf/.codex-tmp/omm-bridge-pinned-build/pinned-omm-sdk/external/install_local_manifest.txt"
     "${CMAKE_INSTALL_MANIFEST_CONTENT}")
endif()
