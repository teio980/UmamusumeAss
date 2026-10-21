add_executable(TaskQueueRuntimeTests
  TaskQueueRuntimeTests.cpp
)
target_link_libraries(TaskQueueRuntimeTests PRIVATE
  Catch2::Catch2WithMain
  UmaAssistantRuntime
)
uma_add_strict_warnings(TaskQueueRuntimeTests)

catch_discover_tests(TaskQueueRuntimeTests DISCOVERY_MODE PRE_TEST)
