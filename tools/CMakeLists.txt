# ── Smoke CLI executable (moved from src/UmaAssistantCore) ──────────────────
add_executable(uma_connect_smoke
  connect_smoke.cpp
)

target_link_libraries(uma_connect_smoke
  PRIVATE
    UmaAssistantCoreObjects
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
