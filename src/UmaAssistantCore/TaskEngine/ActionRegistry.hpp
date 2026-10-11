#pragma once

#include "HachimiPipelineModel.hpp"
#include "ITaskExecutionContext.hpp"
#include <functional>
#include <string>
#include <unordered_map>

namespace UmaAssistant::TaskEngine
{

enum class ActionResult
{
    Success,
    Failed,
    Skipped
};

using ActionHandler = std::function<ActionResult(
    const HachimiPipelineTask& task,
    const TaskMatchResult& matchResult,
    ITaskExecutionContext& context)>;

class ActionRegistry
{
public:
    ActionRegistry();

    void RegisterHandler(const std::string& actionName, ActionHandler handler);

    [[nodiscard]] bool HasHandler(const std::string& actionName) const noexcept;

    ActionResult ExecuteAction(
        const HachimiPipelineTask& task,
        const TaskMatchResult& matchResult,
        ITaskExecutionContext& context) const;

private:
    void RegisterBuiltinHandlers();

    std::unordered_map<std::string, ActionHandler, CaseInsensitiveHash, CaseInsensitiveEqual> m_handlers;
};

} // namespace UmaAssistant::TaskEngine
