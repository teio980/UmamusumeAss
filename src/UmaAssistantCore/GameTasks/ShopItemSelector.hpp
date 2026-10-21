#pragma once

#include "TaskEngine/HachimiPipelineModel.hpp"
#include "TaskEngine/ITaskExecutionContext.hpp"

#include <array>
#include <string>
#include <string_view>

namespace UmaAssistant::Resources
{
class ResourceSnapshot;
}

namespace UmaAssistant::GameTasks
{

struct ShopPurchaseOptions
{
    bool enabled{true};
    bool selectAll{false};
    bool buyStarPieces{false};
    bool buyAlarmClock{false};
    bool buyPleasingParfait{false};
    bool buyShoes{false};
    bool buySupportPoints{false};
    bool buyFlags{false};
};

struct ShopSelectionResult
{
    bool succeeded{false};
    bool hasSelection{false};
    int verifiedRows{0};
    int skippedRows{0};
    std::string message;
};

class ShopItemSelector final
{
public:
    [[nodiscard]] static ShopSelectionResult Select(
        TaskEngine::ITaskExecutionContext& context,
        TaskEngine::HachimiPipelineDefinition const& definition,
        ShopPurchaseOptions const& options,
        const Resources::ResourceSnapshot* resourceSnapshot = nullptr);

    [[nodiscard]] static std::string_view Classify(std::string_view text) noexcept;
};

} // namespace UmaAssistant::GameTasks
