#pragma once

#include <filesystem>
#include <map>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

namespace UmaAssistant::GameData
{

struct UmaCareerObjectiveRecord
{
    std::string objectiveId;
    int order{0};
    std::string kind;
    int turn{0};
    std::vector<std::string> raceIds;
};

struct UmaCareerRaceRecord
{
    std::string raceId;
    std::string nameEn;
    std::string grade;
    int distanceMeters{0};
    std::string distanceBand;
};

struct UmaTraineeRecord
{
    int traineeId{0};
    int baseCharacterId{0};
    std::string nameEn;
    bool available{false};
    std::map<std::string, std::string, std::less<>> surfaceAptitudes;
    std::map<std::string, std::string, std::less<>> distanceAptitudes;
    std::map<std::string, std::string, std::less<>> strategyAptitudes;
    std::vector<UmaCareerObjectiveRecord> careerObjectives;
};

/// <summary>Read-only loader for the unchanged packaged UMA database JSON.</summary>
class UmaGameDatabase final
{
public:
    explicit UmaGameDatabase(std::filesystem::path resourceRoot);

    [[nodiscard]] bool Load(std::string& errorMessage);
    [[nodiscard]] UmaTraineeRecord const* TryGetTrainee(int traineeId) const noexcept;
    [[nodiscard]] UmaCareerRaceRecord const* TryGetCareerRace(
        std::string const& raceId) const noexcept;
    [[nodiscard]] std::vector<std::filesystem::path> GetTraineeImagePaths(
        UmaTraineeRecord const& trainee) const;
    [[nodiscard]] std::unordered_map<int, UmaTraineeRecord> const& Trainees() const noexcept
    {
        return m_trainees;
    }
    [[nodiscard]] std::unordered_map<std::string, UmaCareerRaceRecord> const& CareerRaces() const noexcept
    {
        return m_careerRaces;
    }

private:
    std::filesystem::path m_resourceRoot;
    std::unordered_map<int, UmaTraineeRecord> m_trainees;
    std::unordered_map<std::string, UmaCareerRaceRecord> m_careerRaces;
};

} // namespace UmaAssistant::GameTasks
