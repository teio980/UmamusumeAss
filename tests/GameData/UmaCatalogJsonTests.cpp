#include "GameData/UmaCatalogJson.hpp"

#include <catch2/catch_test_macros.hpp>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <string>

namespace {

class CatalogFixture final
{
public:
    CatalogFixture()
    {
        auto const suffix = std::chrono::steady_clock::now().time_since_epoch().count();
        root = std::filesystem::temp_directory_path()
            / ("uma-catalog-json-" + std::to_string(suffix));
        write("uma/database/global/trainees.json", R"([{"trainee_id":1001,"name_en":"Test Uma"}])");
        write("uma/database/global/base_characters.json", R"([{"character_id":1,"name_en":"Test"}])");
        write("uma/database/global/races.json", R"([{"race_id":2,"name_en":"Test Race"}])");
        write("uma/database/global/support_cards.json", R"([{"support_card_id":3,"name_en":"Test Card"}])");
        write("uma/database/global/meta.json", R"({"version":"fixture","locale":"en"})");
        write("uma/database/global/skills.json", R"({"version":"fixture","skills":[{"skill_id":4,"name_en":"Test Skill"}]})");
        write("hachimi/ura/independent_training/races.global.json",
            R"({"scenarioId":"ura","revision":2,"races":[{"id":"agenda-race","name":"Agenda Race"}]})");
        write("hachimi/ura/events/events.json",
            R"({"$schema":"uma.scenario.events.v1","scenarioId":"ura","events":[{"eventId":"event-1","title":"Test Event"}]})");
    }

    ~CatalogFixture()
    {
        std::error_code ignored;
        std::filesystem::remove_all(root, ignored);
    }

    void write(std::string const& relative, std::string const& content)
    {
        auto const path = root / std::filesystem::path(relative);
        std::filesystem::create_directories(path.parent_path());
        std::ofstream output(path, std::ios::binary);
        output << content;
        if (!output) throw std::runtime_error("Could not write catalog fixture.");
    }

    std::filesystem::path root;
};

}

TEST_CASE("Catalog returns complete source DTO categories and keeps compatibility aliases")
{
    CatalogFixture fixture;
    auto strategies = nlohmann::json::array();
    strategies.push_back({
        {"id", "default-speed-medium"},
        {"value", "default-speed-medium"},
        {"label", "Speed"},
    });

    auto const all = UmaAssistant::GameData::BuildCatalogJson(fixture.root, "all", strategies);
    REQUIRE(all.is_object());
    REQUIRE(all.at("characters").at(0).at("trainee_id") == 1001);
    REQUIRE(all.at("base_characters").at(0).at("character_id") == 1);
    REQUIRE(all.at("races").at(0).at("race_id") == 2);
    REQUIRE(all.at("independent_races").at(0).at("id") == "agenda-race");
    REQUIRE(all.at("skills").at(0).at("skill_id") == 4);
    REQUIRE(all.at("cards") == all.at("support_cards"));
    REQUIRE(all.at("events").at(0).at("eventId") == "event-1");
    REQUIRE(all.at("strategies").at(0).at("value") == "default-speed-medium");
    REQUIRE(all.at("database_meta").at("version") == "fixture");
    REQUIRE(all.at("skill_metadata").at("version") == "fixture");
    REQUIRE(all.at("independent_race_metadata").at("revision") == 2);
    REQUIRE(all.at("event_metadata").at("$schema") == "uma.scenario.events.v1");

    REQUIRE(UmaAssistant::GameData::BuildCatalogJson(fixture.root, "cards", strategies)
        == all.at("support_cards"));
    REQUIRE(UmaAssistant::GameData::BuildCatalogJson(fixture.root, "card", strategies)
        == all.at("support_cards"));
    REQUIRE(UmaAssistant::GameData::BuildCatalogJson(fixture.root, "events", strategies)
        == all.at("events"));
    REQUIRE(UmaAssistant::GameData::BuildCatalogJson(fixture.root, "event_metadata", strategies)
        == all.at("event_metadata"));
    REQUIRE(UmaAssistant::GameData::BuildCatalogJson(fixture.root, "strategies", strategies)
        == strategies);
}

TEST_CASE("Catalog reports missing or malformed required resources")
{
    CatalogFixture fixture;
    auto const strategies = nlohmann::json::array();

    std::filesystem::remove(fixture.root / "uma/database/global/races.json");
    REQUIRE_THROWS(UmaAssistant::GameData::BuildCatalogJson(fixture.root, "all", strategies));

    fixture.write("uma/database/global/races.json", R"({"wrong":"shape"})");
    REQUIRE_THROWS(UmaAssistant::GameData::BuildCatalogJson(fixture.root, "races", strategies));
    fixture.write("uma/database/global/trainees.json", R"([null])");
    REQUIRE_THROWS(UmaAssistant::GameData::BuildCatalogJson(fixture.root, "characters", strategies));
    REQUIRE_THROWS_AS(
        UmaAssistant::GameData::BuildCatalogJson(fixture.root, "not-a-category", strategies),
        std::invalid_argument);
}
