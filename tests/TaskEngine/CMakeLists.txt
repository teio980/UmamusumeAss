include(Catch)

add_executable(TaskEngineTests TaskEngineTests.cpp)
target_link_libraries(TaskEngineTests PRIVATE Catch2::Catch2WithMain UmaAssistantTaskEngine)
target_compile_definitions(TaskEngineTests PRIVATE UMA_SOURCE_ROOT="${CMAKE_SOURCE_DIR}")
uma_add_strict_warnings(TaskEngineTests)
catch_discover_tests(TaskEngineTests DISCOVERY_MODE PRE_TEST)
