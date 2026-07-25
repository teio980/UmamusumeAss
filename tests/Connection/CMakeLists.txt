list(APPEND CMAKE_MODULE_PATH "${catch2_SOURCE_DIR}/extras")
include(Catch)

add_executable(ConnectionProfileTests
  ConnectionProfileTests.cpp
)

target_include_directories(ConnectionProfileTests
  PRIVATE
    ${CMAKE_SOURCE_DIR}/include
)

target_link_libraries(ConnectionProfileTests
  PRIVATE
    Catch2::Catch2WithMain
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
