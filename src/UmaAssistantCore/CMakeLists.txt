add_library(UmaAssistantCore STATIC
  Core.cpp
)

target_link_libraries(UmaAssistantCore PUBLIC
  nlohmann_json::nlohmann_json
)

target_include_directories(UmaAssistantCore PUBLIC
  ${CMAKE_SOURCE_DIR}/include
)

uma_add_strict_warnings(UmaAssistantCore)
