include(Catch)

add_executable(CareerTests
  CareerTests.cpp
  CareerDomainParityTests.cpp
  CareerTransitionPolicyTests.cpp
  CareerBehaviorRegressionTests.cpp
  CareerObservationValueTests.cpp
  CareerRuntimeReplayTests.cpp
  CareerTurnConfirmationSpeedTests.cpp
  CareerActionResourceTests.cpp
  CareerRaceIntentReplayTests.cpp
  CareerClawMachineFlowTests.cpp
  CareerPackageInspectionTests.cpp
  UraTrainingScannerTests.cpp
  UraTrainingVisualReplayTests.cpp
)
target_include_directories(CareerTests PRIVATE ${CMAKE_SOURCE_DIR})
target_compile_definitions(CareerTests PRIVATE
  CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
  UMA_NUMERIC_OCR_WORKER_PATH="$<TARGET_FILE:UmaNumericOcrWorker>"
)
target_link_libraries(CareerTests PRIVATE
  Catch2::Catch2WithMain
  UmaAssistantCareerRuntime
  UmaAssistantUraScenario
  UmaAssistantUraCareer
)
add_dependencies(CareerTests UmaNumericOcrWorker)
uma_add_strict_warnings(CareerTests)
catch_discover_tests(CareerTests DISCOVERY_MODE PRE_TEST)
