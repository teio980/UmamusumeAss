#include "GameTasks/NativeCareerTaskRunner.hpp"
#include <catch2/catch_test_macros.hpp>
#include <nlohmann/json.hpp>
#include <limits>

namespace
{
using UmaAssistant::GameTasks::NativeCareerTaskRunner;
using Json = nlohmann::json;

Json Profile()
{
    return {{"manifestPath", "resource/hachimi/ura/manifest.json"},
        {"traineeId", 1001}, {"careerMode", "normal"}, {"runCount", 2},
        {"strategyId", "custom-ratio"},
        {"normalTrainingRatio", {{"speed", 100}, {"stamina", 20}, {"power", 0}, {"guts", 0}, {"wit", 10}}},
        {"normalLineupStrategy", "late"}, {"normalEventHandling", "default"},
        {"normalSkillIds", {101, 102}}, {"retryFailedRaceWithAlarmClock", true},
        {"independentTrainingFocus", "stamina"}, {"independentLineupStrategy", "end"},
        {"independentSkillIds", {201}}, {"independentAgendaSelections", {"senior|late_august|Test Race"}},
        {"deleteExistingCareerData", true}, {"continueExistingCareer", false},
        {"supportDeckMode", "selected"}, {"supportDeckPreset", "custom"},
        {"supportCardIds", {1, 2, 3, 4, 5}}, {"friendSupportCardId", 6},
        {"pauseOnUnknownOutcome", false}, {"allowOptionalRaces", true},
        {"legacySelectionMode", "manual"}, {"useLegacyGuest", false}, {"useCachedLegacy", false},
        {"legacyAttributeSparks", {"Speed", "Wit"}}, {"legacyAptitudeSparks", {"Turf", "Mile"}}};
}
}

TEST_CASE("Career profile keeps existing five-field ratio and both mode settings", "[GameTasks][Career]")
{
    std::string error;
    auto const config = NativeCareerTaskRunner::ParseSettings(Profile(), error);
    REQUIRE(config);
    CHECK(error.empty());
    CHECK(config->normalTrainingRatio.speed == 100);
    CHECK(config->normalTrainingRatio.stamina == 20);
    CHECK(config->normalTrainingRatio.wit == 10);
    CHECK(config->runCount == 2);
    CHECK(config->normalSkillIds == std::vector<int>{101, 102});
    CHECK(config->independentSkillIds == std::vector<int>{201});
    CHECK(config->independentAgendaSelections == std::vector<std::string>{"senior|late_august|Test Race"});
    CHECK(config->friendSupportCardId == 6);
    CHECK(config->supportCardIds == std::vector<int>{1, 2, 3, 4, 5});
    CHECK(config->legacyAttributeSparks == std::vector<std::string>{"Speed", "Wit"});
    CHECK(config->legacyAptitudeSparks == std::vector<std::string>{"Turf", "Mile"});
    CHECK_FALSE(config->pauseOnUnknownOutcome);
    CHECK(config->allowOptionalRaces);
    CHECK(config->deleteExistingCareerData);
    CHECK_FALSE(config->continueExistingCareer);
}

TEST_CASE("Career modern delete setting overrides its legacy inverse", "[GameTasks][Career]")
{
    auto settings = Profile();
    settings["continueExistingCareer"] = true;
    std::string error;
    auto config = NativeCareerTaskRunner::ParseSettings(settings, error);
    REQUIRE(config);
    CHECK_FALSE(config->continueExistingCareer);
    settings.erase("deleteExistingCareerData");
    config = NativeCareerTaskRunner::ParseSettings(settings, error);
    REQUIRE(config);
    CHECK(config->continueExistingCareer);
    CHECK_FALSE(config->deleteExistingCareerData);
}

TEST_CASE("Career parser rejects settings that draft execution silently ignored", "[GameTasks][Career]")
{
    auto settings = Profile();
    std::string error;
    SECTION("Legacy single-character training ratio is not accepted") { settings["normalTrainingRatio"] = "2-1-0-0-0"; }
    SECTION("Unknown normal strategy cannot select a default strategy") { settings["strategyId"] = "imaginary"; }
    SECTION("Ratio outside the UI range") { settings["normalTrainingRatio"]["speed"] = 101; }
    SECTION("All zero ratio") { settings["normalTrainingRatio"] = {{"speed",0},{"stamina",0},{"power",0},{"guts",0},{"wit",0}}; }
    SECTION("Boolean run count") { settings["runCount"] = true; }
    SECTION("Zero run count") { settings["runCount"] = 0; }
    SECTION("Unsigned overflow cannot become a valid ID") { settings["traineeId"] = (std::numeric_limits<std::uint64_t>::max)(); }
    SECTION("No trainee") { settings["traineeId"] = nullptr; }
    SECTION("Unknown mode") { settings["careerMode"] = "other"; }
    SECTION("Duplicate support cards") { settings["supportCardIds"] = {1,1,2,3,4}; }
    SECTION("Incomplete support deck") { settings["supportCardIds"] = {1,2}; }
    SECTION("Wrong boolean field type") { settings["deleteExistingCareerData"] = "false"; }
    SECTION("Malformed independent agenda") { settings["careerMode"] = "independent"; settings["independentAgendaSelections"] = {"missing-parts"}; }
    CHECK_FALSE(NativeCareerTaskRunner::ParseSettings(settings, error));
    CHECK_FALSE(error.empty());
}

TEST_CASE("Independent Career validates its own settings without requiring a normal strategy", "[GameTasks][Career]")
{
    auto settings = Profile();
    settings["careerMode"] = "independent";
    settings["strategyId"] = "unused-normal-strategy";
    std::string error;
    auto config = NativeCareerTaskRunner::ParseSettings(settings, error);
    REQUIRE(config);
    CHECK(config->careerMode == "independent");
    settings["independentTrainingFocus"] = "unknown";
    CHECK_FALSE(NativeCareerTaskRunner::ParseSettings(settings, error));
}

TEST_CASE("Career manifest relative path resolves against the pinned resource root", "[GameTasks][Career]")
{
    std::string error;
    auto config = NativeCareerTaskRunner::ParseSettings(Profile(), error);
    REQUIRE(config);
    std::filesystem::path const root = "C:/immutable-view/resource";
    CHECK(NativeCareerTaskRunner::ResolveManifestPath(*config, root) == root / "hachimi/ura/manifest.json");
    config->manifestPath = "hachimi/ura/manifest.json";
    CHECK(NativeCareerTaskRunner::ResolveManifestPath(*config, root) == root / "hachimi/ura/manifest.json");
    config->manifestPath = "C:/selected/custom/manifest.json";
    CHECK(NativeCareerTaskRunner::ResolveManifestPath(*config, root) == std::filesystem::path(config->manifestPath));
}
