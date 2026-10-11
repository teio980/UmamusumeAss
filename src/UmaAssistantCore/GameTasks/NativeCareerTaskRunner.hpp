#pragma once

#include "Career/CareerRuntimeConfig.hpp"
#include "GameTaskResult.hpp"
#include "TaskEngine/ITaskExecutionContext.hpp"

#include <filesystem>
#include <nlohmann/json_fwd.hpp>
#include <optional>
#include <string>

namespace UmaAssistant::GameTasks
{

// Settings conversion belongs to game automation, never the pure domain target.
class NativeCareerTaskRunner final
{
public:
    [[nodiscard]] static std::optional<Career::CareerRuntimeConfig> ParseSettings(
        const nlohmann::json& settings, std::string& errorMessage);

    [[nodiscard]] static std::filesystem::path ResolveManifestPath(
        const Career::CareerRuntimeConfig& config,
        const std::filesystem::path& resourceRoot);

    [[nodiscard]] static NativeGameTaskResult Run(
        const Career::CareerRuntimeConfig& config,
        TaskEngine::ITaskExecutionContext& context,
        const std::filesystem::path& resourceRoot);
};

} // namespace UmaAssistant::GameTasks
