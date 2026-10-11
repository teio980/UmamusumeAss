#pragma once

#include "ICareerTrainingFlow.hpp"

#include <functional>
#include <memory>
#include <string>
#include <unordered_map>

namespace UmaAssistant::Career
{

class CareerVisualResources;

// Per-run, extensible factory registry. Scenario extensions register their
// factories from the host composition root, keeping the generic runtime free
// of scenario-specific link dependencies.
class CareerTrainingFlowRegistry
{
public:
    using Factory = std::function<std::shared_ptr<ICareerTrainingFlow>(
        std::shared_ptr<CareerVisualResources>)>;

    void Register(std::string scenarioId, Factory factory);
    [[nodiscard]] std::shared_ptr<ICareerTrainingFlow> Create(
        const std::string& scenarioId,
        std::shared_ptr<CareerVisualResources> resources) const;

private:
    std::unordered_map<std::string, Factory> m_factories;
};

} // namespace UmaAssistant::Career
