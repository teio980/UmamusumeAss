#pragma once

#include "GameData/UmaGameDatabase.hpp"
#include "TaskEngine/HachimiPipelineModel.hpp"
#include "TaskEngine/ITaskExecutionContext.hpp"

#include <filesystem>
#include <optional>
#include <string>

namespace UmaAssistant::Resources
{
class ResourceSnapshot;
}

namespace UmaAssistant::GameTasks
{

struct DailyRaceRunnerSelectionResult
{
    bool succeeded{false};
    std::string message;
    double cardScore{0.0};
    double detailScore{0.0};
};

/// <summary>
/// Applies the same rating sort and aptitude filters as the managed selector,
/// then selects a configured runner only after identity and detail checks.
/// </summary>
class DailyRaceRunnerSelector final
{
public:
    [[nodiscard]] static DailyRaceRunnerSelectionResult Select(
        TaskEngine::ITaskExecutionContext& context,
        const TaskEngine::HachimiPipelineDefinition& definition,
        const std::filesystem::path& resourceRoot,
        std::optional<int> traineeId,
        const Resources::ResourceSnapshot* resourceSnapshot = nullptr);
};

} // namespace UmaAssistant::GameTasks
