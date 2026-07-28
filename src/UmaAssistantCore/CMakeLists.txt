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

add_executable(UmaExportVerification
  VerifyExports.cpp
)

uma_add_strict_warnings(UmaExportVerification)

if(MINGW)
  target_link_options(UmaExportVerification PRIVATE -municode)
endif()

add_library(UmaAssistantCore SHARED
  UmaCaller.cpp
  $<TARGET_OBJECTS:UmaAssistantCoreObjects>
)

set_target_properties(UmaAssistantCore PROPERTIES
  OUTPUT_NAME UmamusumeCore
  PREFIX ""
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

add_dependencies(UmaAssistantCore UmaExportVerification)

add_custom_command(TARGET UmaAssistantCore POST_BUILD
  COMMAND "$<TARGET_FILE:UmaExportVerification>" "$<TARGET_FILE:UmaAssistantCore>"
  COMMENT "Verifying UmamusumeCore exports"
  VERBATIM
)

uma_add_strict_warnings(UmaAssistantCore)

# ── Install ──────────────────────────────────────────────────────────────────
# The SHARED library (UmamusumeCore.dll) is a RUNTIME artifact — lands at the
# install-prefix root for side-by-side layout beside the managed EXE.  The
# import library (UmamusumeCore.lib) goes to lib/ for development consumption.
install(TARGETS UmaAssistantCore
  RUNTIME DESTINATION .
  ARCHIVE DESTINATION lib
)
