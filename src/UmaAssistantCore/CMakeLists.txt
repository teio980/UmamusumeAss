set(UmaAssistantConnection_SOURCES
  Connection/ConnectionProfile.cpp
  Connection/AdbCommandLine.cpp
  Connection/AdbCommandRunnerWin32.cpp
  Connection/EmulatorConnector.cpp
  Connection/EmulatorConnector_ResolveTarget.cpp
  Connection/SmokeCliParser.cpp
)
if(WIN32)
  list(APPEND UmaAssistantConnection_SOURCES Connection/RealWin32Process.cpp)
else()
  list(APPEND UmaAssistantConnection_SOURCES Connection/PosixProcess.cpp)
endif()

add_library(UmaAssistantConnection STATIC ${UmaAssistantConnection_SOURCES})

target_include_directories(UmaAssistantConnection
  PUBLIC
    ${CMAKE_SOURCE_DIR}/include
  PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
)

target_link_libraries(UmaAssistantConnection
  PUBLIC
    nlohmann_json::nlohmann_json
)

uma_add_strict_warnings(UmaAssistantConnection)

add_library(UmaAssistantDevice STATIC
  Session/DeviceSession.cpp
)

target_include_directories(UmaAssistantDevice
  PUBLIC
    ${CMAKE_SOURCE_DIR}/include
  PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
)

if(WIN32)
  target_link_libraries(UmaAssistantDevice
    PUBLIC
      UmaAssistantConnection
      windowscodecs
      windowsapp
      ZLIB::ZLIBSTATIC
  )
else()
  target_link_libraries(UmaAssistantDevice
    PUBLIC
      UmaAssistantConnection
      UmaAssistantResources
      "-framework CoreFoundation"
      "-framework CoreGraphics"
      "-framework ImageIO"
      "-framework Accelerate"
      ZLIB::ZLIBSTATIC
  )
endif()

uma_add_strict_warnings(UmaAssistantDevice)

add_library(UmaAssistantApi STATIC
  Api/CallbackSender.cpp
)

target_include_directories(UmaAssistantApi
  PUBLIC
    ${CMAKE_SOURCE_DIR}/include
  PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
)

target_link_libraries(UmaAssistantApi
  PUBLIC
    nlohmann_json::nlohmann_json
)

uma_add_strict_warnings(UmaAssistantApi)

add_library(UmaAssistantVision STATIC
  Vision/TemplateMatcher.cpp
  Vision/ColorAnalysis.cpp
  Vision/NumericImageProcessing.cpp
)

target_include_directories(UmaAssistantVision
  PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/Vision
    ${CMAKE_SOURCE_DIR}/include
)

target_link_libraries(UmaAssistantVision
  PUBLIC
    nlohmann_json::nlohmann_json
    Threads::Threads
)

uma_add_strict_warnings(UmaAssistantVision)

add_executable(UmaNumericOcrWorker Recognition/NumericOcrWorker.cpp)
target_link_libraries(UmaNumericOcrWorker PRIVATE nlohmann_json::nlohmann_json libtesseract)
target_compile_definitions(UmaNumericOcrWorker PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
if(MINGW)
  target_link_options(UmaNumericOcrWorker PRIVATE -municode)
endif()
uma_add_strict_warnings(UmaNumericOcrWorker)
install(TARGETS UmaNumericOcrWorker RUNTIME DESTINATION .)

add_library(UmaAssistantOcr STATIC
  Recognition/WindowsOcrRecognizer.cpp
  Recognition/NumericOcrBatch.cpp
)

target_include_directories(UmaAssistantOcr
  PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/Recognition
  PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
)

target_link_libraries(UmaAssistantOcr
  PUBLIC
    UmaAssistantVision
    UmaAssistantConnection
    libtesseract
)

target_compile_definitions(UmaAssistantOcr
  PRIVATE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
)

uma_add_strict_warnings(UmaAssistantOcr)
add_dependencies(UmaAssistantOcr UmaNumericOcrWorker)

set(UmaAssistantResources_SOURCES
  Resources/PipelineMetadataSchema.cpp
  Resources/ResourceSnapshot.cpp
  Resources/ImageTransforms.cpp
)
if(WIN32)
  list(APPEND UmaAssistantResources_SOURCES Resources/WicImageDecoder.cpp)
else()
  list(APPEND UmaAssistantResources_SOURCES Resources/ImageIOImageDecoder.cpp)
endif()

add_library(UmaAssistantResources STATIC ${UmaAssistantResources_SOURCES})

target_include_directories(UmaAssistantResources
  PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_SOURCE_DIR}/include
)

if(WIN32)
  target_link_libraries(UmaAssistantResources
    PUBLIC
      UmaAssistantVision
      nlohmann_json::nlohmann_json
      windowscodecs
      windowsapp
  )
else()
  target_link_libraries(UmaAssistantResources
    PUBLIC
      UmaAssistantVision
      nlohmann_json::nlohmann_json
      "-framework CoreFoundation"
      "-framework CoreGraphics"
      "-framework ImageIO"
      "-framework Accelerate"
  )
endif()

uma_add_strict_warnings(UmaAssistantResources)

add_library(UmaAssistantTaskEngine STATIC
  TaskEngine/ActionRegistry.cpp
  TaskEngine/HachimiPipelineLoader.cpp
  TaskEngine/RecognitionPipeline.cpp
  TaskEngine/TaskGraphRunner.cpp
  TaskEngine/TransitionValidator.cpp
  TaskEngine/NativeTaskExecutionContext.cpp
)
target_include_directories(UmaAssistantTaskEngine PUBLIC
  ${CMAKE_SOURCE_DIR}/src
  ${CMAKE_SOURCE_DIR}/include
  ${CMAKE_CURRENT_SOURCE_DIR}
)
target_link_libraries(UmaAssistantTaskEngine PUBLIC
  UmaAssistantDevice
  UmaAssistantResources
  UmaAssistantVision
  UmaAssistantOcr
  UmaAssistantApi
  nlohmann_json::nlohmann_json
)
uma_add_strict_warnings(UmaAssistantTaskEngine)

add_library(UmaAssistantGameData STATIC
  GameData/UmaGameDatabase.cpp
  GameData/CareerSelectionCatalog.cpp
  GameData/UmaCatalogJson.cpp
)
target_include_directories(UmaAssistantGameData PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(UmaAssistantGameData PUBLIC nlohmann_json::nlohmann_json)
uma_add_strict_warnings(UmaAssistantGameData)

add_library(UmaAssistantRuntime STATIC
  Runtime/TaskQueueRuntime.cpp
)
target_include_directories(UmaAssistantRuntime PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}
  ${CMAKE_SOURCE_DIR}/include
)
target_link_libraries(UmaAssistantRuntime PUBLIC
  UmaAssistantApi
  nlohmann_json::nlohmann_json
)
uma_add_strict_warnings(UmaAssistantRuntime)

add_library(UmaAssistantCareerDomain STATIC
  CareerDomain/TrainingStrategies.cpp
)
target_include_directories(UmaAssistantCareerDomain PUBLIC ${CMAKE_SOURCE_DIR}/src)
uma_add_strict_warnings(UmaAssistantCareerDomain)

add_library(UmaAssistantUraScenario STATIC
  CareerDomain/Scenarios/Ura/UraScenarioModule.cpp
  CareerDomain/Scenarios/Ura/UraSmartTrainingStrategy.cpp
  CareerDomain/Scenarios/Ura/UraTrainingScanner.cpp
)
target_link_libraries(UmaAssistantUraScenario PUBLIC UmaAssistantCareerDomain)
uma_add_strict_warnings(UmaAssistantUraScenario)

add_library(UmaAssistantCareerRuntime STATIC
  Career/CareerActionExecutor.cpp
  Career/CareerFlowCoordinator.cpp
  Career/CareerTransitionPolicy.cpp
  Career/CareerObservationReader.cpp
  Career/CareerObservationValues.cpp
  Career/CareerVisualResources.cpp
  Career/CareerTrainingFlowRegistry.cpp
  Career/Flows/CareerClawMachineFlow.cpp
  Career/Flows/CareerSkillLearningFlow.cpp
)
target_link_libraries(UmaAssistantCareerRuntime PUBLIC
  UmaAssistantCareerDomain
  UmaAssistantTaskEngine
  UmaAssistantGameData
)
uma_add_strict_warnings(UmaAssistantCareerRuntime)

add_library(UmaAssistantUraCareer STATIC
  Career/Scenarios/Ura/UraTrainingVisualScanner.cpp
  Career/Scenarios/Ura/UraTrainingFlowAdapter.cpp
)
target_link_libraries(UmaAssistantUraCareer PUBLIC
  UmaAssistantCareerRuntime
  UmaAssistantUraScenario
)
uma_add_strict_warnings(UmaAssistantUraCareer)

add_library(UmaAssistantGameTasks STATIC
  GameTasks/GameTaskActionExtensions.cpp
  GameTasks/NativeGameTaskPackageOperations.cpp
  GameTasks/NativeGameTaskRunner.cpp
  GameTasks/GameRecoveryDialogDetector.cpp
  GameTasks/NativeCareerTaskRunner.cpp
  GameTasks/CareerEntryFlow.cpp
  GameTasks/NativeIndependentTrainingFlow.cpp
  GameTasks/NativeNormalCareerStartup.cpp
  GameTasks/ShopItemSelector.cpp
  GameTasks/DailyRaceRunnerSelector.cpp
  GameTasks/StartGameMonitor.cpp
)
target_link_libraries(UmaAssistantGameTasks PUBLIC
  UmaAssistantTaskEngine
  UmaAssistantGameData
  UmaAssistantDevice
  UmaAssistantResources
  UmaAssistantVision
  UmaAssistantCareerRuntime
  UmaAssistantUraScenario
  UmaAssistantUraCareer
  nlohmann_json::nlohmann_json
)
uma_add_strict_warnings(UmaAssistantGameTasks)

add_library(UmaAssistantCoreRuntime STATIC
  Core.cpp
  CoreHandle.cpp
  CoreRuntime.cpp
  Utf8Path.cpp
)

target_include_directories(UmaAssistantCoreRuntime
  PUBLIC
    ${CMAKE_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_BINARY_DIR}/generated
)

target_link_libraries(UmaAssistantCoreRuntime
  PUBLIC
    UmaAssistantConnection
    UmaAssistantDevice
    UmaAssistantApi
    UmaAssistantVision
    UmaAssistantOcr
    UmaAssistantResources
    UmaAssistantGameTasks
  UmaAssistantRuntime
    UmaAssistantCareerRuntime
    UmaAssistantUraScenario
    nlohmann_json::nlohmann_json
)

uma_add_strict_warnings(UmaAssistantCoreRuntime)

add_executable(UmaExportVerification
  VerifyExports.cpp
)

uma_add_strict_warnings(UmaExportVerification)

if(MINGW)
  target_link_options(UmaExportVerification PRIVATE -municode)
endif()

add_library(UmaAssistantCore SHARED
  UmaCaller.cpp
)

set_target_properties(UmaAssistantCore PROPERTIES
  OUTPUT_NAME UmamusumeCore
  PREFIX ""
)

target_link_libraries(UmaAssistantCore PRIVATE
  UmaAssistantCoreRuntime
  UmaAssistantConnection
  UmaAssistantDevice
  nlohmann_json::nlohmann_json
)

target_include_directories(UmaAssistantCore
  PUBLIC
    ${CMAKE_SOURCE_DIR}/include
  PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_BINARY_DIR}/generated
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

install(TARGETS UmaAssistantCore
  RUNTIME DESTINATION .
  ARCHIVE DESTINATION lib
)
