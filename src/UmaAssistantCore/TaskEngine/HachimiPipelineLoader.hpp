#pragma once

#include "HachimiPipelineModel.hpp"
#include <string>
#include <optional>

namespace UmaAssistant::TaskEngine
{

class HachimiPipelineLoader
{
public:
    static std::optional<HachimiPipelineDefinition> LoadFromJsonString(
        const std::string& jsonContent,
        const std::string& baseDirectory = "");

    static std::optional<HachimiPipelineDefinition> LoadFromFile(
        const std::string& filePath);
};

} // namespace UmaAssistant::TaskEngine
