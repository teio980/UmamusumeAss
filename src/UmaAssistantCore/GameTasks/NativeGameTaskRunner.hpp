#pragma once

#include "GameTaskResult.hpp"
#include "IGameTaskPackageOperations.hpp"
#include "TaskEngine/ITaskExecutionContext.hpp"

#include <filesystem>
#include <nlohmann/json_fwd.hpp>
#include <string>
#include <string_view>

namespace UmaAssistant::Resources
{
class ResourceSnapshot;
}

namespace UmaAssistant::GameTasks
{

/// <summary>Runs one registered Uma game task on the generic task engine.</summary>
class NativeGameTaskRunner final
{
public:
    [[nodiscard]] static bool SupportsTask(std::string_view taskId) noexcept;

    /// <summary>
    /// Validates the immutable settings snapshot before an async operation is
    /// accepted. Unknown task IDs and malformed required values must be rejected
    /// synchronously by the C ABI caller.
    /// </summary>
    [[nodiscard]] static bool ValidateSettings(
        std::string_view taskId,
        const nlohmann::json& settings,
        std::string& errorMessage);

    [[nodiscard]] static NativeGameTaskResult Run(
        std::string_view taskId,
        const nlohmann::json& settings,
        TaskEngine::ITaskExecutionContext& context,
        const std::filesystem::path& resourceRoot,
        IGameTaskPackageOperations* packageOperations = nullptr,
        const Resources::ResourceSnapshot* resourceSnapshot = nullptr);
};

} // namespace UmaAssistant::GameTasks
