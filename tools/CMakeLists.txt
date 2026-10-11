
add_executable(uma_connect_smoke
  connect_smoke.cpp
)

target_link_libraries(uma_connect_smoke
  PRIVATE
    UmaAssistantConnection
    nlohmann_json::nlohmann_json
)

target_include_directories(uma_connect_smoke
  PRIVATE
    ${CMAKE_SOURCE_DIR}/include
    ${CMAKE_SOURCE_DIR}/src/UmaAssistantCore
)

target_compile_definitions(uma_connect_smoke
  PRIVATE
    CMAKE_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
)

uma_add_strict_warnings(uma_connect_smoke)

# Offline developer replay uses the exact parser, graph runner and registered
# actions compiled into Core. The context consumes recorded evidence and only
# records inputs; it never opens a device session.
add_executable(uma_pipeline_replay pipeline_replay.cpp)
target_link_libraries(uma_pipeline_replay PRIVATE UmaAssistantGameTasks)
if(MINGW)
  target_link_options(uma_pipeline_replay PRIVATE -municode)
endif()
uma_add_strict_warnings(uma_pipeline_replay)
install(TARGETS uma_pipeline_replay RUNTIME DESTINATION .)
