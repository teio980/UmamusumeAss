#pragma once

#include <optional>
#include <filesystem>
#include <string>
#include <vector>

namespace UmaAssistant::Career
{

struct CareerTrainingRatio
{
    int speed{2};
    int stamina{1};
    int power{0};
    int guts{0};
    int wit{0};
};

// Native counterpart of CareerTaskSettingsSerializer's flat settings object.
// The runner should validate the settings JSON before constructing this value.
struct CareerRuntimeConfig
{
    std::filesystem::path resourceDirectory{"resource"};
    std::filesystem::path checkpointDirectory;
    std::string resourceVersion;
    int maxSteps{1200};
    std::string manifestPath;
    std::optional<int> traineeId;
    std::string careerMode{"normal"};
    int runCount{1};
    std::string normalLineupStrategy{"pace"};
    CareerTrainingRatio normalTrainingRatio;
    bool retryFailedRaceWithAlarmClock{false};
    std::string normalEventHandling{"default"};
    std::vector<int> normalSkillIds;
    std::string independentTrainingFocus{"balanced"};
    std::string independentLineupStrategy{"pace"};
    std::vector<std::string> independentAgendaSelections;
    std::vector<int> independentSkillIds;
    bool deleteExistingCareerData{false};
    bool continueExistingCareer{true};
    std::string supportDeckMode{"auto"};
    std::string supportDeckPreset;
    std::optional<int> friendSupportCardId;
    std::vector<int> supportCardIds;
    std::string strategyId{"default-speed-medium"};
    bool pauseOnUnknownOutcome{true};
    bool allowOptionalRaces{false};
    std::string legacySelectionMode{"auto"};
    bool useLegacyGuest{true};
    bool useCachedLegacy{true};
    std::vector<std::string> legacyAttributeSparks;
    std::vector<std::string> legacyAptitudeSparks;
};

} // namespace UmaAssistant::Career
