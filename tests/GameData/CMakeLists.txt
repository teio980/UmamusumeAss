add_executable(UmaCatalogJsonTests
  UmaCatalogJsonTests.cpp
)
target_link_libraries(UmaCatalogJsonTests PRIVATE
  Catch2::Catch2WithMain
  UmaAssistantGameData
)
uma_add_strict_warnings(UmaCatalogJsonTests)

catch_discover_tests(UmaCatalogJsonTests DISCOVERY_MODE PRE_TEST)
