#include "UmaCatalogJson.hpp"

#include <algorithm>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <utility>

namespace UmaAssistant::GameData {
namespace {

using Json = nlohmann::json;

void ValidateObjectRows(Json const& rows, std::filesystem::path const& path)
{
    if (!rows.is_array())
        throw std::runtime_error("Catalog file must contain an array: " + path.string());
    for (auto const& row : rows)
        if (!row.is_object())
            throw std::runtime_error("Catalog entries must be objects: " + path.string());
}

[[nodiscard]] Json ReadJson(std::filesystem::path const& path)
{
    std::ifstream input(path, std::ios::binary);
    if (!input) throw std::runtime_error("Could not open catalog: " + path.string());
    return Json::parse(input);
}

[[nodiscard]] Json ReadArray(
    std::filesystem::path const& path,
    std::string_view array_key = {})
{
    std::error_code error;
    if (!std::filesystem::is_regular_file(path, error))
        throw std::runtime_error("Required catalog is missing: " + path.string());

    auto value = ReadJson(path);
    if (!array_key.empty())
    {
        auto const key = std::string(array_key);
        if (!value.is_object() || !value.contains(key)
            || !value[key].is_array())
            throw std::runtime_error("Catalog file has no expected array: " + path.string());
        ValidateObjectRows(value[key], path);
        return value[key];
    }
    ValidateObjectRows(value, path);
    return value;
}

[[nodiscard]] Json ReadDocument(std::filesystem::path const& path)
{
    std::error_code error;
    if (!std::filesystem::is_regular_file(path, error))
        throw std::runtime_error("Required catalog is missing: " + path.string());
    auto const value = ReadJson(path);
    if (!value.is_object())
        throw std::runtime_error("Catalog document must contain an object: " + path.string());
    return value;
}

[[nodiscard]] Json ArrayFromDocument(
    Json const& document,
    std::filesystem::path const& path,
    std::string_view key)
{
    auto const name = std::string(key);
    if (!document.contains(name) || !document.at(name).is_array())
        throw std::runtime_error("Catalog document has no expected array: " + path.string());
    ValidateObjectRows(document.at(name), path);
    return document.at(name);
}

[[nodiscard]] Json ReadCatalogs(
    std::filesystem::path const& root,
    Json const& strategies)
{
    auto const database = root / "uma" / "database" / "global";
    auto const hachimi_ura = root / "hachimi" / "ura";

    auto const characters = ReadArray(database / "trainees.json");
    auto const base_characters = ReadArray(database / "base_characters.json");
    auto const races = ReadArray(database / "races.json");
    auto const support_cards = ReadArray(database / "support_cards.json");
    auto skill_metadata = ReadDocument(database / "skills.json");
    auto const skills = ArrayFromDocument(
        skill_metadata, database / "skills.json", "skills");
    skill_metadata.erase("skills");
    auto independent_race_metadata = ReadDocument(
        hachimi_ura / "independent_training" / "races.global.json");
    auto const independent_races = ArrayFromDocument(
        independent_race_metadata,
        hachimi_ura / "independent_training" / "races.global.json",
        "races");
    independent_race_metadata.erase("races");
    auto event_metadata = ReadDocument(hachimi_ura / "events" / "events.json");
    auto const events = ArrayFromDocument(
        event_metadata, hachimi_ura / "events" / "events.json", "events");
    event_metadata.erase("events");
    auto const database_metadata = ReadDocument(database / "meta.json");

    return {
        {"database_meta", database_metadata},
        {"base_characters", base_characters},
        {"characters", characters},
        {"races", races},
        {"independent_races", independent_races},
        {"independent_race_metadata", independent_race_metadata},
        {"skills", skills},
        {"skill_metadata", skill_metadata},
        {"cards", support_cards},
        {"support_cards", support_cards},
        {"events", events},
        {"event_metadata", event_metadata},
        {"strategies", strategies},
    };
}

}

Json BuildCatalogJson(
    std::filesystem::path const& resource_root,
    std::string_view catalog_type,
    Json strategies)
{
    if (!strategies.is_array())
        throw std::runtime_error("Strategy catalog must contain an array.");
    for (auto const& strategy : strategies)
        if (!strategy.is_object())
            throw std::runtime_error("Strategy catalog entries must be objects.");

    if (catalog_type.empty() || catalog_type == "all")
        return ReadCatalogs(resource_root, strategies);

    auto key = std::string(catalog_type);
    if (key == "card" || key == "cards") key = "support_cards";
    static constexpr std::string_view supported[] = {
        "base_characters", "characters", "races", "independent_races",
        "support_cards", "skills", "events", "strategies", "database_meta",
        "skill_metadata", "independent_race_metadata", "event_metadata",
    };
    if (std::find(std::begin(supported), std::end(supported), key) == std::end(supported))
    {
        throw std::invalid_argument("Unsupported catalog type.");
    }

    if (key == "base_characters")
        return ReadArray(resource_root / "uma" / "database" / "global" / "base_characters.json");
    if (key == "characters")
        return ReadArray(resource_root / "uma" / "database" / "global" / "trainees.json");
    if (key == "races")
        return ReadArray(resource_root / "uma" / "database" / "global" / "races.json");
    if (key == "support_cards")
        return ReadArray(resource_root / "uma" / "database" / "global" / "support_cards.json");
    if (key == "database_meta")
        return ReadDocument(resource_root / "uma" / "database" / "global" / "meta.json");
    if (key == "skills" || key == "skill_metadata")
    {
        auto document = ReadDocument(resource_root / "uma" / "database" / "global" / "skills.json");
        if (key == "skills") return ArrayFromDocument(document,
            resource_root / "uma" / "database" / "global" / "skills.json", "skills");
        document.erase("skills");
        return document;
    }
    if (key == "independent_races" || key == "independent_race_metadata")
    {
        auto const path = resource_root / "hachimi" / "ura"
            / "independent_training" / "races.global.json";
        auto document = ReadDocument(path);
        if (key == "independent_races")
            return ArrayFromDocument(document, path, "races");
        document.erase("races");
        return document;
    }
    if (key == "events" || key == "event_metadata")
    {
        auto const path = resource_root / "hachimi" / "ura" / "events" / "events.json";
        auto document = ReadDocument(path);
        if (key == "events") return ArrayFromDocument(document, path, "events");
        document.erase("events");
        return document;
    }
    return strategies;
}

}
