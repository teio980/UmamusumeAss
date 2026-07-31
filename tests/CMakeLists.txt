add_subdirectory(Connection)

find_program(DOTNET_EXECUTABLE dotnet REQUIRED)
add_test(
  NAME ManagedBridgeTests
  COMMAND "${CMAKE_COMMAND}" -E env
    "UMA_NATIVE_INTEGRATION=1"
    "UMA_INTEGRATION_HOST_PATH=${CMAKE_SOURCE_DIR}/tools/Umamusume.CoreBridge.IntegrationHost/bin/Release/net10.0/Umamusume.CoreBridge.IntegrationHost.dll"
    "UMA_FAKE_ADB_PATH=$<TARGET_FILE:UmaFakeAdb>"
    "${DOTNET_EXECUTABLE}" test
    "${CMAKE_SOURCE_DIR}/tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj"
    -c Release
    "--property:NativeDllPath=$<TARGET_FILE:UmaAssistantCore>"
    "--property:FakeAdbPath=$<TARGET_FILE:UmaFakeAdb>"
  WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
)

add_test(
  NAME WpfGuiTests
  COMMAND "${CMAKE_COMMAND}" -E env
    "DOTNET_CLI_TELEMETRY_OPTOUT=1"
    "${DOTNET_EXECUTABLE}" test
    "${CMAKE_SOURCE_DIR}/tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj"
    -c Release
  WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
)
