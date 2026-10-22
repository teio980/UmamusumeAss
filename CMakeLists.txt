cmake_minimum_required(VERSION 3.28)
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/version.txt" UMA_VERSION_RAW)
string(STRIP "${UMA_VERSION_RAW}" UMA_VERSION)
if(NOT UMA_VERSION MATCHES "^[0-9]+\\.[0-9]+\\.[0-9]+$")
  message(FATAL_ERROR "version.txt must contain a stable semantic version")
endif()
project(UmamusumeAss VERSION ${UMA_VERSION} LANGUAGES C CXX)
find_package(Threads REQUIRED)

if(MSVC)
  string(REPLACE "ole32.lib" "" CMAKE_C_STANDARD_LIBRARIES "${CMAKE_C_STANDARD_LIBRARIES}")
  string(REPLACE "ole32.lib" "" CMAKE_CXX_STANDARD_LIBRARIES "${CMAKE_CXX_STANDARD_LIBRARIES}")
endif()

configure_file(
  "${CMAKE_CURRENT_SOURCE_DIR}/cmake/UmaVersion.hpp.in"
  "${CMAKE_CURRENT_BINARY_DIR}/generated/UmaVersion.hpp"
  @ONLY)


set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# The macOS 15 runner defaults to an older libc++ without stop_token/jthread.
# Check the selected toolchain before downloading and compiling dependencies.
include(CheckCXXSourceCompiles)
set(CMAKE_REQUIRED_LIBRARIES Threads::Threads)
check_cxx_source_compiles([[
  #include <stop_token>
  #include <thread>
  int main() {
    std::stop_source source;
    std::jthread worker([](std::stop_token token) { (void)token.stop_requested(); });
    source.request_stop();
    worker.request_stop();
    worker.join();
  }
]] UMA_HAS_CXX20_STOP_TOKEN_AND_JTHREAD)
unset(CMAKE_REQUIRED_LIBRARIES)
if(NOT UMA_HAS_CXX20_STOP_TOKEN_AND_JTHREAD)
  message(FATAL_ERROR
    "The selected C++ standard library must support std::stop_token and std::jthread. "
    "On macOS, select Xcode 26 or newer via DEVELOPER_DIR and configure a fresh build directory.")
endif()


set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")


include(cmake/warnings.cmake)


include(FetchContent)
set(FETCHCONTENT_QUIET OFF)


FetchContent_Declare(
  nlohmann_json
  URL      https://github.com/nlohmann/json/archive/refs/tags/v3.12.0.tar.gz
  URL_HASH SHA256=4b92eb0c06d10683f7447ce9406cb97cd4b453be18d7279320f7b2f025c10187
)
set(JSON_BuildTests OFF CACHE BOOL "" FORCE)
set(JSON_Install    OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(nlohmann_json)

FetchContent_Declare(
  zlib
  URL      https://www.zlib.net/zlib-1.3.2.tar.gz
  URL_HASH SHA256=bb329a0a2cd0274d05519d61c667c062e06990d72e125ee2dfa8de64f0119d16
)
set(ZLIB_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(ZLIB_BUILD_SHARED OFF CACHE BOOL "" FORCE)
set(ZLIB_BUILD_STATIC ON CACHE BOOL "" FORCE)
set(ZLIB_INSTALL OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(zlib)


FetchContent_Declare(
  Catch2
  URL      https://github.com/catchorg/Catch2/archive/refs/tags/v3.15.2.tar.gz
  URL_HASH SHA256=acfae120892c2b67a74142d36d060c0caa96f1c3aaa8aabd96e19961163d0420
)
set(CATCH_BUILD_TESTING     OFF CACHE BOOL "" FORCE)
set(CATCH_INSTALL_DOCS      OFF CACHE BOOL "" FORCE)
set(CATCH_INSTALL_HELPERS   OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(Catch2)

# Leptonica and Tesseract default to SW on Windows. Use the dependencies
# configured here instead of requiring an external SW package manager.
set(SW_BUILD OFF CACHE BOOL "" FORCE)
set(BUILD_PROG OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(ENABLE_ZLIB OFF CACHE BOOL "" FORCE)
set(ENABLE_PNG OFF CACHE BOOL "" FORCE)
set(ENABLE_GIF OFF CACHE BOOL "" FORCE)
set(ENABLE_JPEG OFF CACHE BOOL "" FORCE)
set(ENABLE_TIFF OFF CACHE BOOL "" FORCE)
set(ENABLE_WEBP OFF CACHE BOOL "" FORCE)
set(ENABLE_OPENJPEG OFF CACHE BOOL "" FORCE)

FetchContent_Declare(
  leptonica
  URL      https://github.com/DanBloomberg/leptonica/archive/refs/tags/1.85.0.tar.gz
  URL_HASH SHA256=c01376bce0379d4ea4bc2ec5d5cbddaa49e2f06f88242619ab8c059e21adf233
  EXCLUDE_FROM_ALL
)
FetchContent_MakeAvailable(leptonica)
target_include_directories(leptonica PUBLIC
  $<BUILD_INTERFACE:${leptonica_SOURCE_DIR}/src>
  $<BUILD_INTERFACE:${leptonica_BINARY_DIR}/src>
)

# Workaround for Leptonica 1.85.0 install bug when consumed via FetchContent
file(MAKE_DIRECTORY "${CMAKE_BINARY_DIR}/src")
file(WRITE "${CMAKE_BINARY_DIR}/src/endianness.h" "/* Generated placeholder for Leptonica install */\n")

file(WRITE "${CMAKE_BINARY_DIR}/cmake/Leptonica/LeptonicaConfig.cmake" "
set(Leptonica_VERSION \"1.85.0\")
set(Leptonica_FOUND TRUE)
set(Leptonica_LIBRARIES leptonica)
set(Leptonica_INCLUDE_DIRS \"${leptonica_SOURCE_DIR}/src;${leptonica_BINARY_DIR}/src\")
")
file(WRITE "${CMAKE_BINARY_DIR}/cmake/Leptonica/LeptonicaConfigVersion.cmake" "
set(PACKAGE_VERSION \"1.85.0\")
set(PACKAGE_VERSION_COMPATIBLE TRUE)
set(PACKAGE_VERSION_EXACT TRUE)
")
set(Leptonica_DIR "${CMAKE_BINARY_DIR}/cmake/Leptonica" CACHE PATH "" FORCE)

set(BUILD_TRAINING_TOOLS OFF CACHE BOOL "" FORCE)
set(BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(GRAPHICS_DISABLED ON CACHE BOOL "" FORCE)
set(DISABLE_TIFF ON CACHE BOOL "" FORCE)
set(DISABLE_ARCHIVE ON CACHE BOOL "" FORCE)
set(DISABLE_CURL ON CACHE BOOL "" FORCE)
set(INSTALL_CONFIGS OFF CACHE BOOL "" FORCE)

FetchContent_Declare(
  tesseract
  URL      https://github.com/tesseract-ocr/tesseract/archive/refs/tags/5.5.0.tar.gz
  URL_HASH SHA256=f2fb34ca035b6d087a42875a35a7a5c4155fa9979c6132365b1e5a28ebc3fc11
  EXCLUDE_FROM_ALL
)
FetchContent_MakeAvailable(tesseract)
target_include_directories(libtesseract PUBLIC
  $<BUILD_INTERFACE:${tesseract_SOURCE_DIR}/include>
  $<BUILD_INTERFACE:${tesseract_BINARY_DIR}/include>
)


add_subdirectory(src/UmaAssistantCore)
if(WIN32)
  add_subdirectory(src/UmamusumeAssUpdater)
endif()
add_subdirectory(tools)


option(UMA_BUILD_TESTS "Build tests" ON)
if(UMA_BUILD_TESTS)
  include(CTest)
  add_subdirectory(tests)
endif()
