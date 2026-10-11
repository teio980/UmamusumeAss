list(APPEND CMAKE_MODULE_PATH "${catch2_SOURCE_DIR}/extras")
include(Catch)

add_executable(ResourceTests ResourceTests.cpp)
target_link_libraries(ResourceTests PRIVATE Catch2::Catch2WithMain UmaAssistantResources ZLIB::ZLIBSTATIC)
target_compile_definitions(ResourceTests PRIVATE CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}" NOMINMAX WIN32_LEAN_AND_MEAN)
uma_add_strict_warnings(ResourceTests)

if(NOT MSVC)
  get_filename_component(_uma_compiler_dir "${CMAKE_CXX_COMPILER}" DIRECTORY)
  catch_discover_tests(ResourceTests DISCOVERY_MODE PRE_TEST DL_PATHS "${_uma_compiler_dir}")
else()
  catch_discover_tests(ResourceTests DISCOVERY_MODE PRE_TEST)
endif()
