list(APPEND CMAKE_MODULE_PATH "${catch2_SOURCE_DIR}/extras")
include(Catch)

add_executable(RecognitionTests
  RecognitionTests.cpp
  NumericOcrBatchTests.cpp
  NumericOcrWorkerTests.cpp
)

target_include_directories(RecognitionTests
  PRIVATE
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore/Recognition
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore/Vision
)

target_link_libraries(RecognitionTests
  PRIVATE
    Catch2::Catch2WithMain
    UmaAssistantOcr
    UmaAssistantResources
    nlohmann_json::nlohmann_json
    ZLIB::ZLIBSTATIC
    bcrypt
)

target_compile_definitions(RecognitionTests
  PRIVATE
    CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    UMA_NUMERIC_OCR_WORKER_PATH="$<TARGET_FILE:UmaNumericOcrWorker>"
)

uma_add_strict_warnings(RecognitionTests)

function(uma_discover_recognition_tests TARGET)
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

uma_discover_recognition_tests(RecognitionTests)
