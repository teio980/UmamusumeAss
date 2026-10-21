#pragma once

#include <filesystem>
#include <string>
#include <unordered_map>
#include <vector>

namespace UmaAssistant::GameData
{

struct UmaSupportCardRecord
{
    int supportCardId{0};
    int featuredCharacterId{0};
    std::string nameEn;
    std::string featuredCharacterNameEn;
    std::string rarity;
    std::string type;
    bool available{false};
};

struct UmaIndependentSkillRecord
{
    int skillId{0};
    std::string skillName;
    int rarity{0};
    int needSkillPoint{0};
    std::string otherTiers;
    std::string searchText;
    bool gameSearchMapped{false};
    int searchResultRow{0};
    int searchResultPage{0};
    int searchResultPickerRow{0};
    bool singleModeEnabled{false};
    bool availableInGlobal{false};
    bool isGeneralSkill{false};
};

struct UmaIndependentRaceRecord
{
    std::string raceName;
    std::string year;
    std::string turn;
    int raceId{0};
    std::string grade;
    std::string type;
    std::string location;
    std::string length;
    std::string gameTrack;
    int gameDistance{0};
    std::string gameGround;
    std::string pickerHeaderTarget;
    bool gameAvailable{false};
    int gameOrder{-1};
};

/// Read-only access to packaged selection metadata used by native Career
/// entry and Independent Training setup. It intentionally does not load
/// trainee records; UmaGameDatabase remains the single trainee catalog.
class CareerSelectionCatalog final
{
public:
    explicit CareerSelectionCatalog(std::filesystem::path resourceRoot);

    [[nodiscard]] bool Load(std::string& errorMessage);
    [[nodiscard]] UmaSupportCardRecord const* TryGetSupportCard(int id) const noexcept;
    [[nodiscard]] UmaIndependentSkillRecord const* TryGetSkill(int id) const noexcept;
    [[nodiscard]] UmaIndependentRaceRecord const* TryGetRace(
        std::string const& year, std::string const& turn, std::string const& raceName) const noexcept;
    [[nodiscard]] std::filesystem::path GetSupportCardTemplatePath(int id) const;
    [[nodiscard]] std::vector<UmaSupportCardRecord> const& SupportCards() const noexcept
    {
        return m_supportCards;
    }
    [[nodiscard]] std::vector<UmaIndependentSkillRecord> const& Skills() const noexcept
    {
        return m_skills;
    }
    [[nodiscard]] std::vector<UmaIndependentRaceRecord> const& Races() const noexcept
    {
        return m_races;
    }

private:
    std::filesystem::path m_resourceRoot;
    std::vector<UmaSupportCardRecord> m_supportCards;
    std::vector<UmaIndependentSkillRecord> m_skills;
    std::vector<UmaIndependentRaceRecord> m_races;
    std::unordered_map<int, std::size_t> m_supportCardById;
    std::unordered_map<int, std::size_t> m_skillById;
};

} // namespace UmaAssistant::GameData
