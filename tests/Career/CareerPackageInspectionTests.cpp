#include <catch2/catch_test_macros.hpp>

#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace
{

using Json = nlohmann::json;
using UmaAssistant::Career::CareerJsonOverride;
using UmaAssistant::Career::InspectCareerPackage;

struct TemporaryPackage
{
    TemporaryPackage()
    {
        const auto id = std::to_string(std::chrono::steady_clock::now().time_since_epoch().count());
        resourceRoot = std::filesystem::temp_directory_path() / ("uma-career-inspection-" + id);
        const auto career = resourceRoot / "hachimi" / "career";
        std::filesystem::create_directories(career / "templates");
        manifestPath = resourceRoot / "hachimi" / "scenario" / "manifest.json";
        std::filesystem::create_directories(manifestPath.parent_path());

        Write(career / "templates" / "main.png", "test image placeholder");
        Write(career / "profile.json", Json{
            {"scenarioId", "inspection-test"},
            {"screens", Json::array({Json{
                {"screenId", "career_main"},
                {"recognition", {{"template", "templates/main.png"}}},
                {"actions", Json::array({Json{{"semanticId", "begin"}, {"task", "start"}}})}
            }})}
        }.dump(2));
        Write(career / "execution.json", Execution(17).dump(2));
        Write(career / "catalog.json", Json{{"schemaVersion", 1}, {"assets", Json::object()}}.dump(2));
        Write(manifestPath, Json{
            {"schemaVersion", 2},
            {"scenarioId", "inspection-test"},
            {"dataVersion", "test-v1"},
            {"screens", Json::array({"../career/profile.json"})},
            {"execution", Json::array({"../career/execution.json"})},
            {"resourceCatalog", "../career/catalog.json"}
        }.dump(2));
    }

    ~TemporaryPackage()
    {
        std::error_code error;
        std::filesystem::remove_all(resourceRoot, error);
    }

    static Json Execution(int waitMs)
    {
        return Json{
            {"name", "inspection-test"},
            {"schemaVersion", 1},
            {"description", "inspection test"},
            {"referenceWidth", 900},
            {"referenceHeight", 1600},
            {"tasks", {{"start", {{"action", "JustReturn"}, {"waitMs", waitMs}}}}}
        };
    }

    static void Write(const std::filesystem::path& path, const std::string& content)
    {
        std::filesystem::create_directories(path.parent_path());
        std::ofstream output(path, std::ios::binary);
        output << content;
    }

    std::filesystem::path resourceRoot;
    std::filesystem::path manifestPath;
};

std::string Utf8(const std::filesystem::path& path)
{
    return UmaAssistant::Paths::ToUtf8(std::filesystem::absolute(path).lexically_normal());
}

} // namespace

TEST_CASE("Career package inspection uses the production parser and returns source documents", "[CareerPackageInspection]")
{
    TemporaryPackage package;
    const auto inspected = InspectCareerPackage(package.manifestPath, package.resourceRoot);

    REQUIRE(inspected.at("version") == 1);
    CHECK(inspected.at("valid") == true);
    CHECK(inspected.at("ready") == true);
    CHECK(inspected.at("manifest").at("scenarioId") == "inspection-test");
    CHECK(inspected.at("execution").at("definition").at("tasks").at("start").at("waitMs") == 17);
    CHECK(inspected.at("execution").at("task_order") == Json::array({"start"}));
    CHECK(inspected.at("execution").at("fragment_paths") == Json::array({Utf8(package.resourceRoot / "hachimi" / "career" / "execution.json")}));

    std::vector<std::string> sourcePaths;
    for (const auto& file : inspected.at("files"))
        sourcePaths.push_back(file.at("path").get<std::string>());
    CHECK(std::find(sourcePaths.begin(), sourcePaths.end(), Utf8(package.manifestPath)) != sourcePaths.end());
    CHECK(std::find(sourcePaths.begin(), sourcePaths.end(), Utf8(package.resourceRoot / "hachimi" / "career" / "profile.json")) != sourcePaths.end());
    CHECK(std::find(sourcePaths.begin(), sourcePaths.end(), Utf8(package.resourceRoot / "hachimi" / "career" / "catalog.json")) != sourcePaths.end());
}

TEST_CASE("Career package inspection applies overlays before native validation", "[CareerPackageInspection]")
{
    TemporaryPackage package;
    const auto executionPath = package.resourceRoot / "hachimi" / "career" / "execution.json";
    const auto rawExecution = TemporaryPackage::Execution(321).dump(2);
    const auto inspected = InspectCareerPackage(
        package.manifestPath, package.resourceRoot, {{Utf8(executionPath), rawExecution}});

    CHECK(inspected.at("valid") == true);
    CHECK(inspected.at("execution").at("definition").at("tasks").at("start").at("waitMs") == 321);
    const auto& files = inspected.at("files");
    const auto overlay = std::find_if(files.begin(), files.end(), [&](const Json& file) {
        return file.at("path") == Utf8(executionPath);
    });
    REQUIRE(overlay != files.end());
    CHECK(overlay->at("raw_json") == rawExecution);
}

TEST_CASE("Career package inspection diagnoses an unsaved action bound to a missing task", "[CareerPackageInspection]")
{
    TemporaryPackage package;
    const auto profilePath = package.resourceRoot / "hachimi" / "career" / "profile.json";
    const Json profile{
        {"scenarioId", "inspection-test"},
        {"screens", Json::array({Json{
            {"screenId", "career_main"},
            {"recognition", {{"template", "templates/main.png"}}},
            {"actions", Json::array({Json{{"semanticId", "begin"}, {"task", "not_in_graph"}}})}
        }})}
    };
    const auto rawProfile = profile.dump(2);
    const auto inspected = InspectCareerPackage(
        package.manifestPath, package.resourceRoot, {{Utf8(profilePath), rawProfile}});

    CHECK(inspected.at("ready") == true);
    CHECK(inspected.at("valid") == false);
    CHECK(std::any_of(inspected.at("diagnostics").begin(), inspected.at("diagnostics").end(), [](const Json& diagnostic) {
        return diagnostic.is_string() && diagnostic.get<std::string>().find("not_in_graph") != std::string::npos;
    }));
}

TEST_CASE("Career package inspection rejects malformed and escaping overlays", "[CareerPackageInspection]")
{
    TemporaryPackage package;
    const auto executionPath = package.resourceRoot / "hachimi" / "career" / "execution.json";

    SECTION("invalid JSON")
    {
        const auto inspected = InspectCareerPackage(
            package.manifestPath, package.resourceRoot, {{Utf8(executionPath), "{"}});
        CHECK(inspected.at("valid") == false);
        CHECK(std::any_of(inspected.at("diagnostics").begin(), inspected.at("diagnostics").end(), [](const Json& diagnostic) {
            return diagnostic.is_string() && diagnostic.get<std::string>().find("invalid JSON") != std::string::npos;
        }));
    }

    SECTION("path outside resource root")
    {
        const auto outsidePath = std::filesystem::temp_directory_path() / "uma-career-overlay-outside.json";
        const auto inspected = InspectCareerPackage(
            package.manifestPath, package.resourceRoot, {{Utf8(outsidePath), "{}"}});
        CHECK(inspected.at("valid") == false);
        CHECK(std::any_of(inspected.at("diagnostics").begin(), inspected.at("diagnostics").end(), [](const Json& diagnostic) {
            return diagnostic.is_string() && diagnostic.get<std::string>().find("leaves resourceRoot") != std::string::npos;
        }));
    }

    SECTION("duplicate normalized path")
    {
        const auto path = Utf8(executionPath);
        const auto inspected = InspectCareerPackage(package.manifestPath, package.resourceRoot, {
            {path, TemporaryPackage::Execution(1).dump()},
            {path, TemporaryPackage::Execution(2).dump()}});
        CHECK(inspected.at("valid") == false);
        CHECK(std::any_of(inspected.at("diagnostics").begin(), inspected.at("diagnostics").end(), [](const Json& diagnostic) {
            return diagnostic.is_string() && diagnostic.get<std::string>().find("must be unique") != std::string::npos;
        }));
    }
}
