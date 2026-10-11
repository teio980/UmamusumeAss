#pragma once

#include "../CareerRuntimeConfig.hpp"
#include "../CareerVisualResources.hpp"
#include "../ICareerActionExecutor.hpp"
#include "UmaAssistantCore/GameData/CareerSelectionCatalog.hpp"

#include <memory>
#include <string>
#include <unordered_set>

namespace UmaAssistant::Career
{

// Resource-backed normal-mode skill purchase flow. It uses the shared game
// data catalog and verifies every row, price, dialog, and return transition.
class CareerSkillLearningFlow
{
public:
    CareerSkillLearningFlow(
        CareerRuntimeConfig config,
        std::shared_ptr<CareerVisualResources> resources);

    ActionExecutionStatus RunBeforeRace(TaskEngine::ITaskExecutionContext& context);
    bool ReturnToRaceDay(TaskEngine::ITaskExecutionContext& context);

private:
    bool EnsureCatalogLoaded(TaskEngine::ITaskExecutionContext& context);

    CareerRuntimeConfig m_config;
    std::shared_ptr<CareerVisualResources> m_resources;
    GameData::CareerSelectionCatalog m_catalog;
    bool m_catalogLoaded{false};
    std::string m_catalogError;
    // Learned/obtained state is durable for this career run and intentionally
    // survives date-recovery resets of the surrounding executor.
    std::unordered_set<int> m_confirmedLearnedSkillIds;
};

} // namespace UmaAssistant::Career
