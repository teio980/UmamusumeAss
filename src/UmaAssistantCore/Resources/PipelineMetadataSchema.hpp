#pragma once

#include <nlohmann/json_fwd.hpp>

#include <optional>
#include <string>

namespace UmaAssistant::Resources
{

// The subset of the Hachimi v1 document header consumed by the generic task
// graph loader. Legacy game-selection/template/timing keys are validated by
// the schema helper but intentionally are not retained here.
struct PipelineMetadata
{
    int schemaVersion{1};
    std::string name;
    std::optional<std::string> start;
    std::string description;
    int referenceWidth{900};
    int referenceHeight{1600};
    int pollIntervalMilliseconds{300};
};

[[nodiscard]] std::optional<PipelineMetadata> ReadHachimiV1PipelineMetadata(
    const nlohmann::ordered_json& root);

} // namespace UmaAssistant::Resources
