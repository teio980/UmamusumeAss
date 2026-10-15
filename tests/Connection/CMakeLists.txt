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
    UmaAssistantConnection
    ws2_32
)

target_compile_definitions(ConnectionProfileTests
  PRIVATE
    CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
    UMA_FAKE_ADB_PATH="$<TARGET_FILE:UmaFakeAdb>"
)

add_dependencies(ConnectionProfileTests UmaFakeAdb)

uma_add_strict_warnings(ConnectionProfileTests)





add_executable(UmaCallerTests
  UmaCallerTests.cpp
  ImageProcessingApiTests.cpp
  Phase3ApiTests.cpp
)

add_executable(CoreRuntimeResourceTests
  CoreRuntimeResourceTests.cpp
)

add_executable(ApiCallbackTests
  ApiCallbackTests.cpp
)

target_include_directories(ApiCallbackTests
  PRIVATE
    ${CMAKE_SOURCE_DIR}/include
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore
)

target_link_libraries(ApiCallbackTests
  PRIVATE
    Catch2::Catch2WithMain
    UmaAssistantApi
)

uma_add_strict_warnings(ApiCallbackTests)

target_include_directories(CoreRuntimeResourceTests
  PRIVATE
    ${CMAKE_SOURCE_DIR}/include
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore
)

target_link_libraries(CoreRuntimeResourceTests
  PRIVATE
    Catch2::Catch2WithMain
    UmaAssistantCoreRuntime
    windowsapp
)

target_compile_definitions(CoreRuntimeResourceTests
  PRIVATE
    CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
)

uma_add_strict_warnings(CoreRuntimeResourceTests)

add_executable(UmaFakeAdb
  FakeAdb.cpp
)

target_compile_definitions(UmaFakeAdb
  PRIVATE
    UMA_FAKE_PNG_PATH="${CMAKE_SOURCE_DIR}/testdata/hachimi/ura/captures/career_skill_race_day_490_sample.png"
)

target_link_libraries(UmaFakeAdb PRIVATE ZLIB::ZLIBSTATIC)

uma_add_strict_warnings(UmaFakeAdb)

target_include_directories(UmaCallerTests
  PRIVATE
    ${CMAKE_SOURCE_DIR}/include
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore
)

target_link_libraries(UmaCallerTests
  PRIVATE
    Catch2::Catch2WithMain
    UmaAssistantCore
    nlohmann_json::nlohmann_json
    windowscodecs
    windowsapp
)

target_compile_definitions(UmaCallerTests
  PRIVATE
    CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
    UMA_FAKE_ADB_PATH="$<TARGET_FILE:UmaFakeAdb>"
)

add_dependencies(UmaCallerTests UmaFakeAdb)

uma_add_strict_warnings(UmaCallerTests)

add_executable(NativeTaskContextTests NativeTaskContextTests.cpp)
target_link_libraries(NativeTaskContextTests PRIVATE Catch2::Catch2WithMain UmaAssistantGameTasks)
target_compile_definitions(NativeTaskContextTests PRIVATE
  CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
  UMA_FAKE_ADB_PATH="$<TARGET_FILE:UmaFakeAdb>"
)
add_dependencies(NativeTaskContextTests UmaFakeAdb)
uma_add_strict_warnings(NativeTaskContextTests)
catch_discover_tests(NativeTaskContextTests DISCOVERY_MODE PRE_TEST)





add_executable(UmaCallerCConsumer
  UmaCallerCConsumer.c
)

target_include_directories(UmaCallerCConsumer
  PRIVATE
    ${CMAKE_SOURCE_DIR}/include
)

target_link_libraries(UmaCallerCConsumer
  PRIVATE
    UmaAssistantCore
)





add_library(UmaUnexpectedExportFixture SHARED
  UnexpectedExportFixture.c
)

set_target_properties(UmaUnexpectedExportFixture PROPERTIES
  PREFIX ""
)

foreach(uma_consumer IN ITEMS UmaCallerTests UmaCallerCConsumer)
  add_custom_command(TARGET ${uma_consumer} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      $<TARGET_FILE:UmaAssistantCore>
      $<TARGET_FILE_DIR:${uma_consumer}>
  )
endforeach()

target_compile_definitions(UmaCallerCConsumer
  PRIVATE
    CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
)


if(MSVC)
  target_compile_options(UmaCallerCConsumer PRIVATE
    /W4 /WX /utf-8
  )
endif()






function(uma_discover_catch2_tests TARGET)
  if(NOT MSVC)
    get_filename_component(_uma_compiler_dir "${CMAKE_CXX_COMPILER}" DIRECTORY)
    catch_discover_tests(${TARGET}
      DISCOVERY_MODE PRE_TEST
      DL_PATHS "${_uma_compiler_dir}"
    )
  else()
    catch_discover_tests(${TARGET}
      DISCOVERY_MODE PRE_TEST
    )
  endif()
endfunction()

uma_discover_catch2_tests(ConnectionProfileTests)
uma_discover_catch2_tests(UmaCallerTests)
uma_discover_catch2_tests(CoreRuntimeResourceTests)
uma_discover_catch2_tests(ApiCallbackTests)
add_test(NAME UmaCallerCConsumer COMMAND UmaCallerCConsumer)
add_test(
  NAME UmaExportVerification
  COMMAND UmaExportVerification "$<TARGET_FILE:UmaAssistantCore>"
)
add_test(
  NAME UmaExportVerificationRejectsUnexpectedExport
  COMMAND UmaExportVerification "$<TARGET_FILE:UmaUnexpectedExportFixture>"
)
set_tests_properties(UmaExportVerificationRejectsUnexpectedExport PROPERTIES
  WILL_FAIL TRUE
)





add_library(UmaWrongNameExportFixture SHARED
  WrongNameExportFixture.c
)

set_target_properties(UmaWrongNameExportFixture PROPERTIES
  PREFIX ""
)

add_test(
  NAME UmaExportVerificationRejectsWrongName
  COMMAND UmaExportVerification "$<TARGET_FILE:UmaWrongNameExportFixture>"
)
set_tests_properties(UmaExportVerificationRejectsWrongName PROPERTIES
  WILL_FAIL TRUE
)
