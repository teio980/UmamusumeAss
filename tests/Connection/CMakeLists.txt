list(APPEND CMAKE_MODULE_PATH "${catch2_SOURCE_DIR}/extras")
include(Catch)

add_executable(ConnectionProfileTests
  ConnectionProfileTests.cpp
  AdbCommandRunnerTests.cpp
  EmulatorConnectorTests.cpp
  SmokeCliParserTests.cpp
)

target_include_directories(ConnectionProfileTests
  PRIVATE
    ${CMAKE_SOURCE_DIR}/include
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore
)

target_link_libraries(ConnectionProfileTests
  PRIVATE
    Catch2::Catch2WithMain
    UmaAssistantCore
)

# Provide CMAKE_SOURCE_DIR as a preprocessor define for test_resource_path()
target_compile_definitions(ConnectionProfileTests
  PRIVATE
    CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
)

uma_add_strict_warnings(ConnectionProfileTests)

if(NOT MSVC)
  get_filename_component(_uma_compiler_dir "${CMAKE_CXX_COMPILER}" DIRECTORY)
  catch_discover_tests(ConnectionProfileTests
    DISCOVERY_MODE PRE_TEST
    DL_PATHS "${_uma_compiler_dir}"
  )
else()
  catch_discover_tests(ConnectionProfileTests
    DISCOVERY_MODE PRE_TEST
  )
endif()

# On MinGW, CTest discovery needs MSYS2 UCRT bin in PATH.
# Run tests directly from a shell with the correct PATH, or set:
#   set(CTEST_ENVIRONMENT "PATH=C:/msys64/ucrt64/bin;$ENV{PATH}")
