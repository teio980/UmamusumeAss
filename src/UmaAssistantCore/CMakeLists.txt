set(UMA_CORE_INTERNAL_SOURCES
  Core.cpp
  CoreHandle.cpp
  CoreRuntime.cpp
  Utf8Path.cpp
  Connection/ConnectionProfile.cpp
  Connection/AdbCommandLine.cpp
  Connection/RealWin32Process.cpp
  Connection/AdbCommandRunnerWin32.cpp
  Connection/EmulatorConnector.cpp
  Connection/EmulatorConnector_ResolveTarget.cpp
  Connection/SmokeCliParser.cpp
)

add_library(UmaAssistantCoreObjects OBJECT
  ${UMA_CORE_INTERNAL_SOURCES}
)

target_link_libraries(UmaAssistantCoreObjects PUBLIC
  nlohmann_json::nlohmann_json
)

target_include_directories(UmaAssistantCoreObjects
  PUBLIC
    ${CMAKE_SOURCE_DIR}/include
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore
)

uma_add_strict_warnings(UmaAssistantCoreObjects)

add_library(UmaAssistantCore SHARED
  UmaCaller.cpp
  $<TARGET_OBJECTS:UmaAssistantCoreObjects>
)

set_target_properties(UmaAssistantCore PROPERTIES
  OUTPUT_NAME UmamusumeCore
)

target_link_libraries(UmaAssistantCore PRIVATE
  nlohmann_json::nlohmann_json
)

target_include_directories(UmaAssistantCore
  PUBLIC
    ${CMAKE_SOURCE_DIR}/include
  PRIVATE
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore
)

target_compile_definitions(UmaAssistantCore PRIVATE
  UMA_DLL_EXPORTS
)

uma_add_strict_warnings(UmaAssistantCore)
