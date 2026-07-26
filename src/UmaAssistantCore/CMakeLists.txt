add_library(UmaAssistantCore STATIC
  Core.cpp
  Connection/ConnectionProfile.cpp
  Connection/AdbCommandLine.cpp
  Connection/RealWin32Process.cpp
  Connection/AdbCommandRunnerWin32.cpp
  Connection/EmulatorConnector.cpp
  Connection/EmulatorConnector_ResolveTarget.cpp
  Connection/SmokeCliParser.cpp
)

target_link_libraries(UmaAssistantCore PUBLIC
  nlohmann_json::nlohmann_json
)

target_include_directories(UmaAssistantCore
  PUBLIC
    ${CMAKE_SOURCE_DIR}/include
  PRIVATE
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore
)

uma_add_strict_warnings(UmaAssistantCore)
