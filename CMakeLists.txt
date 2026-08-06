cmake_minimum_required(VERSION 3.28)
project(UmamusumeAss VERSION 0.1.0 LANGUAGES C CXX)


set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)


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
  Catch2
  URL      https://github.com/catchorg/Catch2/archive/refs/tags/v3.15.2.tar.gz
  URL_HASH SHA256=acfae120892c2b67a74142d36d060c0caa96f1c3aaa8aabd96e19961163d0420
)
set(CATCH_BUILD_TESTING     OFF CACHE BOOL "" FORCE)
set(CATCH_INSTALL_DOCS      OFF CACHE BOOL "" FORCE)
set(CATCH_INSTALL_HELPERS   OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(Catch2)


add_subdirectory(src/UmaAssistantCore)
add_subdirectory(tools)


option(UMA_BUILD_TESTS "Build tests" ON)
if(UMA_BUILD_TESTS)
  include(CTest)
  add_subdirectory(tests)
endif()
