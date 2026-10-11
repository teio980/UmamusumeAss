#include "ActionRegistry.hpp"

namespace UmaAssistant::TaskEngine
{

ActionRegistry::ActionRegistry()
{
    RegisterBuiltinHandlers();
}

void ActionRegistry::RegisterHandler(const std::string& actionName, ActionHandler handler)
{
    m_handlers[actionName] = std::move(handler);
}

bool ActionRegistry::HasHandler(const std::string& actionName) const noexcept
{
    return m_handlers.find(actionName) != m_handlers.end();
}

ActionResult ActionRegistry::ExecuteAction(
    const HachimiPipelineTask& task,
    const TaskMatchResult& matchResult,
    ITaskExecutionContext& context) const
{
    auto it = m_handlers.find(task.action);
    if (it != m_handlers.end())
    {
        return it->second(task, matchResult, context);
    }

    context.Log("ERROR", "Unknown action requested: " + task.action);
    return ActionResult::Failed;
}

void ActionRegistry::RegisterBuiltinHandlers()
{
    for (const auto* action : {"DoNothing", "JustReturn"})
    {
        RegisterHandler(action, [](const HachimiPipelineTask&, const TaskMatchResult&, ITaskExecutionContext&) {
            return ActionResult::Success;
        });
    }

    RegisterHandler("ClickSelf", [](const HachimiPipelineTask& task, const TaskMatchResult& match, ITaskExecutionContext& context) {
        if (!match.found && !task.fallbackClickOffset)
        {
            context.Log("WARN", "ClickSelf executed without a valid match position");
            return ActionResult::Failed;
        }

        int targetX = match.CenterX();
        int targetY = match.CenterY();
        if (task.clickVerification && task.clickAnchor && task.clickAnchor->size() >= 2)
        {
            targetX = (*task.clickAnchor)[0];
            targetY = (*task.clickAnchor)[1];
        }
        else if (task.clickOffset && task.clickOffset->size() >= 2)
        {
            targetX += (*task.clickOffset)[0];
            targetY += (*task.clickOffset)[1];
        }
        else if (task.fallbackClickOffset && task.fallbackClickOffset->size() >= 2)
        {
            targetX += (*task.fallbackClickOffset)[0];
            targetY += (*task.fallbackClickOffset)[1];
        }

        bool tapped = context.TapMatch(targetX, targetY, match);
        return tapped ? ActionResult::Success : ActionResult::Failed;
    });

    RegisterHandler("ClickSpecificRect", [](const HachimiPipelineTask& task, const TaskMatchResult&, ITaskExecutionContext& context) {
        if (!task.specificRect || task.specificRect->size() < 4)
        {
            context.Log("ERROR", "ClickSpecificRect failed: specificRect not specified or invalid");
            return ActionResult::Failed;
        }

        const auto& rect = *task.specificRect;
        int targetX = rect[0] + rect[2] / 2;
        int targetY = rect[1] + rect[3] / 2;

        TaskMatchResult coordinateFrame;
        coordinateFrame.referenceWidth = task.referenceWidth;
        coordinateFrame.referenceHeight = task.referenceHeight;
        bool tapped = context.TapMatch(targetX, targetY, coordinateFrame);
        return tapped ? ActionResult::Success : ActionResult::Failed;
    });

    RegisterHandler("ClickText", [](const HachimiPipelineTask& task, const TaskMatchResult& match, ITaskExecutionContext& context) {
        if (!match.found)
        {
            context.Log("WARN", "ClickText executed without a valid OCR match");
            return ActionResult::Failed;
        }

        int left = match.x;
        int top = match.y;
        int right = match.x + (std::max)(0, match.width);
        int bottom = match.y + (std::max)(0, match.height);
        if (task.rowExpansion && task.rowExpansion->size() >= 4)
        {
            const auto& expansion = *task.rowExpansion;
            left = (std::max)(0, left - (std::max)(0, expansion[0]));
            top = (std::max)(0, top - (std::max)(0, expansion[1]));
            right = (std::max)(left, right + (std::max)(0, expansion[2]));
            bottom = (std::max)(top, bottom + (std::max)(0, expansion[3]));
        }

        int targetX = left + (right - left) / 2;
        int targetY = top + (bottom - top) / 2;
        if (task.clickAnchor && !task.clickAnchor->empty())
        {
            targetX = (*task.clickAnchor)[0];
            if (task.clickAnchor->size() >= 2)
                targetY = (*task.clickAnchor)[1];
        }
        else if (task.clickOffset && task.clickOffset->size() >= 2)
        {
            targetX += (*task.clickOffset)[0];
            targetY += (*task.clickOffset)[1];
        }
        return context.TapMatch(targetX, targetY, match)
            ? ActionResult::Success : ActionResult::Failed;
    });

    for (const auto* action : {"FindText", "WaitForText", "ScrollFindText"})
    {
        RegisterHandler(action, [](const HachimiPipelineTask&, const TaskMatchResult& match, ITaskExecutionContext&) {
            return match.found ? ActionResult::Success : ActionResult::Failed;
        });
    }

    for (const auto* action : {"DetectText", "Detect"})
    {
        RegisterHandler(action, [](const HachimiPipelineTask&, const TaskMatchResult&, ITaskExecutionContext&) {
            return ActionResult::Success;
        });
    }

    RegisterHandler("Wait", [](const HachimiPipelineTask& task, const TaskMatchResult&, ITaskExecutionContext& context) {
        if (task.waitMilliseconds > 0)
        {
            context.Sleep(task.waitMilliseconds);
        }
        return ActionResult::Success;
    });

    RegisterHandler("Input", [](const HachimiPipelineTask& task, const TaskMatchResult&, ITaskExecutionContext& context) {
        if (!task.inputText)
        {
            context.Log("ERROR", "Input action requested with no inputText specified");
            return ActionResult::Failed;
        }
        bool ok = context.InputText(*task.inputText);
        return ok ? ActionResult::Success : ActionResult::Failed;
    });

    for (const auto* action : {"Key", "KeyEvent"})
    {
        RegisterHandler(action, [](const HachimiPipelineTask& task, const TaskMatchResult&, ITaskExecutionContext& context) {
            if (!task.keyCode)
            {
                context.Log("ERROR", "Key action requested with no keyCode specified");
                return ActionResult::Failed;
            }
            bool ok = context.SendKey(*task.keyCode);
            return ok ? ActionResult::Success : ActionResult::Failed;
        });
    }

    RegisterHandler("Swipe", [](const HachimiPipelineTask& task, const TaskMatchResult&, ITaskExecutionContext& context) {
        if (!task.swipe || task.swipe->size() < 4)
        {
            context.Log("ERROR", "Swipe action requested without at least 4 coordinates");
            return ActionResult::Failed;
        }
        const auto& s = *task.swipe;
        int duration = s.size() >= 5 ? s[4] : 250;
        bool ok = context.Swipe(s[0], s[1], s[2], s[3], duration);
        return ok ? ActionResult::Success : ActionResult::Failed;
    });

    RegisterHandler("ClickRect", [](const HachimiPipelineTask& task, const TaskMatchResult& match, ITaskExecutionContext& context) {
        if (task.specificRect && task.specificRect->size() >= 4)
        {
            const auto& rect = *task.specificRect;
            int targetX = rect[0] + rect[2] / 2;
            int targetY = rect[1] + rect[3] / 2;
            return context.TapMatch(targetX, targetY, match) ? ActionResult::Success : ActionResult::Failed;
        }
        if (match.found)
        {
            return context.TapMatch(match.CenterX(), match.CenterY(), match)
                ? ActionResult::Success : ActionResult::Failed;
        }
        context.Log("ERROR", "ClickRect failed: neither specificRect nor match position available");
        return ActionResult::Failed;
    });

    RegisterHandler("Back", [](const HachimiPipelineTask&, const TaskMatchResult&, ITaskExecutionContext& context) {
        context.Log("INFO", "Executing Back action (KEYCODE_BACK)");
        return context.SendKey("4") ? ActionResult::Success : ActionResult::Failed;
    });

    RegisterHandler("Stop", [](const HachimiPipelineTask&, const TaskMatchResult&, ITaskExecutionContext& context) {
        context.Log("INFO", "Executing Stop action");
        return ActionResult::Success;
    });

    // The graph runner resolves task.pipeline before dispatching this action.
    // No domain policy lives here, but a missing path must fail closed.
    RegisterHandler("RunPipeline", [](const HachimiPipelineTask& task, const TaskMatchResult&, ITaskExecutionContext&) {
        return task.pipeline && !task.pipeline->empty() ? ActionResult::Success : ActionResult::Failed;
    });
    RegisterHandler("ClickSelfUntilTransition", [](const HachimiPipelineTask& task, const TaskMatchResult& match, ITaskExecutionContext& context) {
        if (!match.found)
            return ActionResult::Failed;
        int targetX = match.CenterX();
        int targetY = match.CenterY();
        if (task.clickVerification && task.clickAnchor && task.clickAnchor->size() >= 2)
        {
            targetX = (*task.clickAnchor)[0];
            targetY = (*task.clickAnchor)[1];
        }
        else if (task.clickOffset && task.clickOffset->size() >= 2)
        {
            targetX += (*task.clickOffset)[0];
            targetY += (*task.clickOffset)[1];
        }
        return context.TapMatch(targetX, targetY, match)
            ? ActionResult::Success : ActionResult::Failed;
    });
}

} // namespace UmaAssistant::TaskEngine
