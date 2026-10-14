include(Catch)

add_executable(GameTaskTests GameTaskTests.cpp GameRecoveryDialogTests.cpp NativeGameTaskPackageOperationsTests.cpp CareerSettingsTests.cpp CareerEntryReplayTests.cpp
  NativeNormalCareerStartupReplayTests.cpp NativeCareerTaskRunnerReplayTests.cpp)
target_link_libraries(GameTaskTests PRIVATE Catch2::Catch2WithMain UmaAssistantGameTasks)
target_compile_definitions(GameTaskTests PRIVATE CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
  UMA_FAKE_ADB_PATH="$<TARGET_FILE:UmaFakeAdb>")
add_dependencies(GameTaskTests UmaFakeAdb)
uma_add_strict_warnings(GameTaskTests)
catch_discover_tests(GameTaskTests DISCOVERY_MODE PRE_TEST)
