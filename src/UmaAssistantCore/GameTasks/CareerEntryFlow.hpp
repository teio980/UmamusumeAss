#pragma once

#include "Career/CareerRuntimeConfig.hpp"
#include "Career/CareerVisualResources.hpp"
#include "Career/ICareerObserver.hpp"
#include "GameData/CareerSelectionCatalog.hpp"
#include "GameData/UmaGameDatabase.hpp"
#include "TaskEngine/ActionRegistry.hpp"

namespace UmaAssistant::GameTasks
{

struct CareerEntryResult
{
    bool succeeded{false};
    bool cancelled{false};
    std::string error;
    std::string lastScreenId{"unknown"};
    int actionsCompleted{0};
};

/// Owns Home -> Final Confirmation routing. It deliberately stops before
/// pressing Start so normal and independent setup have distinct safe paths.
class CareerEntryFlow final
{
public:
    static void RegisterActions(
        Career::CareerRuntimeConfig const& config,
        GameData::UmaGameDatabase const& trainees,
        GameData::CareerSelectionCatalog const& catalogs,
        TaskEngine::ActionRegistry& registry);

    [[nodiscard]] static CareerEntryResult Run(
        Career::CareerRuntimeConfig const& config,
        Career::CareerVisualResources& resources,
        Career::ICareerObserver& observer,
        TaskEngine::ITaskExecutionContext& context);
};

} // namespace UmaAssistant::GameTasks
