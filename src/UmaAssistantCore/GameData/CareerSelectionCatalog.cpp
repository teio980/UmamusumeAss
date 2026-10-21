#include "GameData/CareerSelectionCatalog.hpp"

#include "Common/PathEncoding.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cctype>
#include <fstream>
#include <stdexcept>
#include <utility>

namespace UmaAssistant::GameData
{

namespace
{

using Json = nlohmann::json;

std::string String(Json const& value, char const* key)
{
    auto const item = value.find(key);
    if (item == value.end() || item->is_null()) return {};
    if (!item->is_string())
        throw std::runtime_error(std::string("field '") + key + "' must be a string");
    return item->get<std::string>();
}

int Int(Json const& value, char const* key, int fallback = 0)
{
    auto const item = value.find(key);
    if (item == value.end() || item->is_null()) return fallback;
    if (!item->is_number_integer())
        throw std::runtime_error(std::string("field '") + key + "' must be an integer");
    return item->get<int>();
}

bool Bool(Json const& value, char const* key)
{
    auto const item = value.find(key);
    if (item == value.end() || item->is_null()) return false;
    if (!item->is_boolean())
        throw std::runtime_error(std::string("field '") + key + "' must be a boolean");
    return item->get<bool>();
}

Json ReadJson(std::filesystem::path const& path)
{
    std::ifstream input(path, std::ios::binary);
    if (!input)
        throw std::runtime_error("could not open " + Paths::ToUtf8(path));
    return Json::parse(input);
}

std::string Normalize(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

} // namespace

CareerSelectionCatalog::CareerSelectionCatalog(std::filesystem::path resourceRoot)
    : m_resourceRoot(std::move(resourceRoot))
{
}

bool CareerSelectionCatalog::Load(std::string& errorMessage)
{
    m_supportCards.clear();
    m_skills.clear();
    m_races.clear();
    m_supportCardById.clear();
    m_skillById.clear();
    errorMessage.clear();

    try
    {
        auto const databaseRoot = m_resourceRoot / "uma" / "database" / "global";
        auto const cardsJson = ReadJson(databaseRoot / "support_cards.json");
        if (!cardsJson.is_array())
            throw std::runtime_error("support_cards.json must contain a JSON array");
        for (auto const& item : cardsJson)
        {
            if (!item.is_object()) continue;
            UmaSupportCardRecord card;
            card.supportCardId = Int(item, "support_card_id");
            card.featuredCharacterId = Int(item, "featured_character_id");
            card.nameEn = String(item, "name_en");
            card.featuredCharacterNameEn = String(item, "featured_character_name_en");
            card.rarity = String(item, "rarity");
            card.type = String(item, "type");
            card.available = Bool(item, "available");
            if (card.supportCardId <= 0)
                throw std::runtime_error("support card has a nonpositive support_card_id");
            if (!m_supportCardById.emplace(card.supportCardId, m_supportCards.size()).second)
                throw std::runtime_error("support_cards.json contains a duplicate support card ID");
            m_supportCards.push_back(std::move(card));
        }

        auto const skillsJson = ReadJson(databaseRoot / "skills.json");
        if (!skillsJson.is_object() || !skillsJson.contains("skills")
            || !skillsJson["skills"].is_array())
            throw std::runtime_error("skills.json must contain a skills array");
        for (auto const& item : skillsJson["skills"])
        {
            if (!item.is_object()) continue;
            UmaIndependentSkillRecord skill;
            skill.skillId = Int(item, "skillId");
            skill.skillName = String(item, "skillName");
            skill.rarity = Int(item, "rarity");
            skill.needSkillPoint = Int(item, "needSkillPoint");
            skill.otherTiers = String(item, "otherTiers");
            skill.searchText = String(item, "searchText");
            skill.gameSearchMapped = Bool(item, "gameSearchMapped");
            skill.searchResultRow = Int(item, "searchResultRow");
            skill.searchResultPage = std::max(0, skill.searchResultRow - 1) / 8;
            skill.searchResultPickerRow = std::max(0, skill.searchResultRow - 1) % 8;
            skill.singleModeEnabled = Bool(item, "singleModeEnabled");
            skill.availableInGlobal = Bool(item, "availableInGlobal");
            auto const general = item.find("isGeneralSkill");
            skill.isGeneralSkill = general != item.end() && general->is_boolean() && general->get<bool>();
            if (skill.skillId <= 0)
                throw std::runtime_error("skill has a nonpositive skillId");
            if (!m_skillById.emplace(skill.skillId, m_skills.size()).second)
                throw std::runtime_error("skills.json contains a duplicate skill ID");
            m_skills.push_back(std::move(skill));
        }

        auto const racesPath = m_resourceRoot / "hachimi" / "ura"
            / "independent_training" / "races.global.json";
        auto const racesJson = ReadJson(racesPath);
        if (!racesJson.is_object() || !racesJson.contains("races")
            || !racesJson["races"].is_array())
            throw std::runtime_error("races.global.json must contain a races array");
        for (auto const& item : racesJson["races"])
        {
            if (!item.is_object()) continue;
            UmaIndependentRaceRecord race;
            race.raceName = String(item, "raceName");
            race.year = String(item, "year");
            race.turn = String(item, "turn");
            race.raceId = Int(item, "raceId");
            race.grade = String(item, "grade");
            race.type = String(item, "type");
            race.location = String(item, "location");
            race.length = String(item, "length");
            race.gameTrack = String(item, "gameTrack");
            race.gameDistance = Int(item, "gameDistance");
            race.gameGround = String(item, "gameGround");
            race.gameAvailable = Bool(item, "gameAvailable");
            race.gameOrder = Int(item, "gameOrder", -1);
            // Independent agenda OCR identifies a race by its rendered
            // header only when that header uniquely maps to a game race in
            // the requested year/turn slot.
            std::string const direction = race.location.find("⇒") != std::string::npos
                ? "Right"
                : race.location.find("⇐") != std::string::npos ? "Left" : "";
            if (!race.gameTrack.empty() && race.gameDistance > 0 && !direction.empty())
            {
                auto const& ground = race.gameGround.empty() ? race.type : race.gameGround;
                race.pickerHeaderTarget = race.gameTrack + " " + ground + " "
                    + std::to_string(race.gameDistance) + "m"
                    + (race.length.empty() ? "" : " (" + race.length + ")")
                    + " " + direction;
            }
            m_races.push_back(std::move(race));
        }
        return true;
    }
    catch (std::exception const& exception)
    {
        errorMessage = "Could not load Career selection catalogs: " + std::string(exception.what());
        m_supportCards.clear();
        m_skills.clear();
        m_races.clear();
        m_supportCardById.clear();
        m_skillById.clear();
        return false;
    }
}

UmaSupportCardRecord const* CareerSelectionCatalog::TryGetSupportCard(int id) const noexcept
{
    auto const found = m_supportCardById.find(id);
    return found == m_supportCardById.end() ? nullptr : &m_supportCards[found->second];
}

UmaIndependentSkillRecord const* CareerSelectionCatalog::TryGetSkill(int id) const noexcept
{
    auto const found = m_skillById.find(id);
    return found == m_skillById.end() ? nullptr : &m_skills[found->second];
}

UmaIndependentRaceRecord const* CareerSelectionCatalog::TryGetRace(
    std::string const& year, std::string const& turn, std::string const& raceName) const noexcept
{
    auto const normalizedYear = Normalize(year);
    auto const normalizedTurn = Normalize(turn);
    auto const normalizedName = Normalize(raceName);
    auto const found = std::find_if(m_races.begin(), m_races.end(), [&](auto const& race) {
        return race.gameAvailable && Normalize(race.year) == normalizedYear
            && Normalize(race.turn) == normalizedTurn && Normalize(race.raceName) == normalizedName;
    });
    return found == m_races.end() ? nullptr : &*found;
}

std::filesystem::path CareerSelectionCatalog::GetSupportCardTemplatePath(int id) const
{
    if (TryGetSupportCard(id) == nullptr) return {};
    auto const path = m_resourceRoot / "uma" / "assets" / "templates" / "global"
        / "support_cards" / std::to_string(id) / "card.png";
    std::error_code error;
    return std::filesystem::is_regular_file(path, error) ? path : std::filesystem::path{};
}

} // namespace UmaAssistant::GameData
