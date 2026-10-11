#pragma once

#include "TaskEngine/ITaskExecutionContext.hpp"

#include <chrono>
#include <filesystem>
#include <functional>
#include <string>

namespace UmaAssistant::GameTasks
{

struct StartGameMonitorResult
{
    bool succeeded{false};
    bool homeDetected{false};
    std::string message;
};

/// <summary>Executes the startup monitor definition through its same-frame priority/trigger chain.</summary>
class StartGameMonitor final
{
public:
    using Clock = std::function<std::chrono::steady_clock::time_point()>;

    [[nodiscard]] static StartGameMonitorResult Run(
        TaskEngine::ITaskExecutionContext& context,
        const std::filesystem::path& resourceRoot,
        const Clock& clock = {});
};

} // namespace UmaAssistant::GameTasks
