cmake_minimum_required(VERSION 3.28)
project(UmamusumeAss VERSION 0.1.0 LANGUAGES CXX)

# ── C++20 ──────────────────────────────────────────────────────────────────
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# ── MSVC runtime: static linking (MT / MTd) ───────────────────────────────
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# ── Compiler-warning infrastructure (call uma_add_strict_warnings per-target)
include(cmake/warnings.cmake)

# ── Dependencies (pinned via FetchContent) ────────────────────────────────
include(FetchContent)
set(FETCHCONTENT_QUIET OFF)

# nlohmann/json 3.12.0 — full source archive provides the CMake target
FetchContent_Declare(
  nlohmann_json
  URL      https://github.com/nlohmann/json/archive/refs/tags/v3.12.0.tar.gz
  URL_HASH SHA256=4b92eb0c06d10683f7447ce9406cb97cd4b453be18d7279320f7b2f025c10187
)
set(JSON_BuildTests OFF CACHE BOOL "" FORCE)
set(JSON_Install    OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(nlohmann_json)

# Catch2 3.15.2 — shallow clone, test framework for later tasks
FetchContent_Declare(
  Catch2
  GIT_REPOSITORY https://github.com/catchorg/Catch2.git
  GIT_TAG        v3.15.2
  GIT_SHALLOW    TRUE
)
set(CATCH_BUILD_TESTING     OFF CACHE BOOL "" FORCE)
set(CATCH_INSTALL_DOCS      OFF CACHE BOOL "" FORCE)
set(CATCH_INSTALL_HELPERS   OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(Catch2)

# ── Subdirectories ────────────────────────────────────────────────────────
add_subdirectory(src/UmaAssistantCore)

# ── Tests ──────────────────────────────────────────────────────────────────
option(UMA_BUILD_TESTS "Build tests" ON)
if(UMA_BUILD_TESTS)
  include(CTest)
  add_subdirectory(tests)
endif()
