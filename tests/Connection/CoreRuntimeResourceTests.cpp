#include "CoreRuntime.hpp"

#include <catch2/catch_test_macros.hpp>
#include <nlohmann/json.hpp>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <string>

namespace {

struct TemporaryResources
{
    TemporaryResources()
    {
        auto const tick = std::chrono::steady_clock::now().time_since_epoch().count();
        path = std::filesystem::temp_directory_path()
            / ("uma-runtime-resource-" + std::to_string(tick));
        std::filesystem::create_directories(path / "resource");
    }

    ~TemporaryResources()
    {
        std::error_code ignored;
        std::filesystem::remove_all(path, ignored);
    }

    std::filesystem::path path;
};

void write_profile(std::filesystem::path const& root, std::string const& marker)
{
    std::filesystem::create_directories(root / "resource");
    nlohmann::json const document = {
        {"connection", nlohmann::json::array({nlohmann::json{
            {"configName", "General"},
            {"commands", {{"marker", nlohmann::json::array({marker})}}},
        }})},
    };
    std::ofstream output(root / "resource" / "connection.json", std::ios::binary);
    output << document.dump();
}

struct RestoreSourceResources
{
    ~RestoreSourceResources()
    {
        static_cast<void>(UmaAssistant::CoreRuntime::instance().load_resource(CMAKE_SOURCE_DIR));
    }
};

}

TEST_CASE("resource reload commits only valid snapshots and preserves held versions", "[CoreRuntime][resource]")
{
    TemporaryResources valid_v1;
    TemporaryResources invalid;
    TemporaryResources valid_v2;
    RestoreSourceResources restore;
    auto& runtime = UmaAssistant::CoreRuntime::instance();

    write_profile(valid_v1.path, "version-one");
    std::filesystem::copy_file(
        std::filesystem::path(CMAKE_SOURCE_DIR) / "resource/uma/system_reference/100101.webp",
        valid_v1.path / "resource/template.webp");
    REQUIRE(runtime.load_resource(valid_v1.path.string()));
    auto const bundle_one = runtime.resource_bundle();
    auto const version_one = bundle_one.profile;
    auto const resources_one = bundle_one.resources;
    REQUIRE(version_one != nullptr);
    REQUIRE(resources_one != nullptr);
    REQUIRE(resources_one->root() == (valid_v1.path / "resource").lexically_normal());
    auto const old_invocation = version_one->expand("General", "marker", "adb", "serial");
    REQUIRE(old_invocation.has_value());
    REQUIRE(old_invocation->arguments == std::vector<std::string>{"version-one"});

    std::ofstream(invalid.path / "resource" / "connection.json", std::ios::binary)
        << "{not-json";
    REQUIRE_FALSE(runtime.load_resource(invalid.path.string()));
    auto const failed_bundle = runtime.resource_bundle();
    REQUIRE(failed_bundle.profile == version_one);
    REQUIRE(failed_bundle.resources == resources_one);
    REQUIRE(runtime.is_resource_loaded());

    write_profile(valid_v2.path, "version-two");
    std::filesystem::copy_file(
        std::filesystem::path(CMAKE_SOURCE_DIR) / "resource/uma/system_reference/100201.webp",
        valid_v2.path / "resource/template.webp");
    REQUIRE(runtime.load_resource(valid_v2.path.string()));
    auto const bundle_two = runtime.resource_bundle();
    auto const version_two = bundle_two.profile;
    auto const resources_two = bundle_two.resources;
    REQUIRE(version_two != nullptr);
    REQUIRE(resources_two != nullptr);
    REQUIRE(version_two != version_one);
    REQUIRE(resources_two != resources_one);
    REQUIRE(resources_two->root() == (valid_v2.path / "resource").lexically_normal());
    auto const retained_template = resources_one->load_image("template.webp");
    auto const current_template = resources_two->load_image("template.webp");
    REQUIRE(retained_template != nullptr);
    REQUIRE(current_template != nullptr);
    CHECK(retained_template->rgba != current_template->rgba);

    auto const retained_invocation = version_one->expand("General", "marker", "adb", "serial");
    auto const new_invocation = version_two->expand("General", "marker", "adb", "serial");
    REQUIRE(retained_invocation.has_value());
    REQUIRE(new_invocation.has_value());
    REQUIRE(retained_invocation->arguments == std::vector<std::string>{"version-one"});
    REQUIRE(new_invocation->arguments == std::vector<std::string>{"version-two"});
}
