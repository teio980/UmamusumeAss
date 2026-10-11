#include "GameTaskActionExtensions.hpp"

namespace UmaAssistant::GameTasks
{

namespace
{

using namespace TaskEngine;

ActionHandler MatchedTap(std::string actionName)
{
    return [actionName = std::move(actionName)](
        HachimiPipelineTask const&,
        TaskMatchResult const& match,
        ITaskExecutionContext& context)
    {
        context.Log("INFO", "Executing game task extension: " + actionName);
        return match.found && context.TapMatch(match.CenterX(), match.CenterY(), match)
            ? ActionResult::Success : ActionResult::Failed;
    };
}

ActionHandler UnboundSelector(std::string actionName)
{
    return [actionName = std::move(actionName)](
        HachimiPipelineTask const&,
        TaskMatchResult const&,
        ITaskExecutionContext& context)
    {
        context.Log("ERROR", actionName + " requires the task-specific selector handler.");
        return ActionResult::Failed;
    };
}

} // namespace

void GameTaskActionExtensions::RegisterShopActions(TaskEngine::ActionRegistry& registry)
{
    registry.RegisterHandler("ShopSelectPurchasable", MatchedTap("ShopSelectPurchasable"));
    registry.RegisterHandler("ShopConfirmPurchase", MatchedTap("ShopConfirmPurchase"));
    registry.RegisterHandler("SelectShopItems", UnboundSelector("SelectShopItems"));
}

void GameTaskActionExtensions::RegisterDailyRaceActions(TaskEngine::ActionRegistry& registry)
{
    registry.RegisterHandler("DailyRaceSelectRunner", UnboundSelector("DailyRaceSelectRunner"));
    registry.RegisterHandler("SelectDailyRaceRunner", UnboundSelector("SelectDailyRaceRunner"));
    registry.RegisterHandler("DailyRaceConfirmStrategy", MatchedTap("DailyRaceConfirmStrategy"));
}

void GameTaskActionExtensions::RegisterStartGameActions(TaskEngine::ActionRegistry& registry)
{
    registry.RegisterHandler("TapToStart", [](
        HachimiPipelineTask const& task,
        TaskMatchResult const& match,
        ITaskExecutionContext& context)
    {
        if (!task.specificRect || task.specificRect->size() < 2)
        {
            context.Log("ERROR", "TapToStart requires specificRect.");
            return ActionResult::Failed;
        }
        auto const& rect = *task.specificRect;
        auto x = rect[0];
        auto y = rect[1];
        if (rect.size() >= 4)
        {
            x += rect[2] / 2;
            y += rect[3] / 2;
        }
        return context.TapMatch(x, y, match) ? ActionResult::Success : ActionResult::Failed;
    });
    registry.RegisterHandler("StartGameTapToTitle", MatchedTap("StartGameTapToTitle"));
    registry.RegisterHandler("DateChangedRecovery", UnboundSelector("DateChangedRecovery"));
}

void GameTaskActionExtensions::RegisterRewardCollectionActions(TaskEngine::ActionRegistry& registry)
{
    registry.RegisterHandler("RewardCollectAll", MatchedTap("RewardCollectAll"));
}

void GameTaskActionExtensions::RegisterAll(TaskEngine::ActionRegistry& registry)
{
    RegisterShopActions(registry);
    RegisterDailyRaceActions(registry);
    RegisterStartGameActions(registry);
    RegisterRewardCollectionActions(registry);
}

} // namespace UmaAssistant::GameTasks
