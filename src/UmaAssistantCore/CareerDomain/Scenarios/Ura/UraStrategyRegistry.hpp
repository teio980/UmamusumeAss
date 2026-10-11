#pragma once

#include "UmaAssistantCore/CareerDomain/TrainingStrategies.hpp"

#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace UmaAssistant::CareerDomain
{

struct UraTrainingStrategyOption
{
    std::string value;
    std::string label;
};

class UraStrategyRegistry
{
public:
    using Factory = std::function<std::shared_ptr<ICareerStrategy>(
        TrainingRatio ratio,
        int restThreshold,
        int witEnergyThreshold)>;

    // The available built-ins mirror the persisted managed strategy IDs.
    // Additional scenario-local strategies can be registered at startup.
    static void Register(
        std::string strategyId,
        std::string label,
        Factory factory);

    [[nodiscard]] static bool IsRegistered(const std::string& strategyId);
    [[nodiscard]] static std::vector<UraTrainingStrategyOption> AvailableStrategies();
    [[nodiscard]] static std::shared_ptr<ICareerStrategy> Create(
        const std::string& strategyId,
        TrainingRatio ratio = TrainingRatio{2, 1, 0, 0, 0},
        int restThreshold = 50,
        int witEnergyThreshold = 35);
};

} // namespace UmaAssistant::CareerDomain
