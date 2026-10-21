#pragma once

#include "UmaAssistantCore/TaskEngine/ActionRegistry.hpp"

namespace UmaAssistant::GameTasks
{

/// <summary>
/// Registers Uma-specific game task action handlers into the generic TaskEngine's ActionRegistry.
/// Follows MAA architectural boundary: generic engine does not know about game tasks,
/// game tasks extend the generic engine via registration.
/// </summary>
class GameTaskActionExtensions
{
public:
    static void RegisterAll(TaskEngine::ActionRegistry& registry);

    static void RegisterShopActions(TaskEngine::ActionRegistry& registry);
    static void RegisterDailyRaceActions(TaskEngine::ActionRegistry& registry);
    static void RegisterStartGameActions(TaskEngine::ActionRegistry& registry);
    static void RegisterRewardCollectionActions(TaskEngine::ActionRegistry& registry);
};

} // namespace UmaAssistant::GameTasks
