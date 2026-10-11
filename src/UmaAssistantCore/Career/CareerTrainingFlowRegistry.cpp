#include "CareerTrainingFlowRegistry.hpp"

#include <algorithm>
#include <cctype>
#include <stdexcept>

namespace UmaAssistant::Career
{

namespace
{

std::string NormalizeScenarioId(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

} // namespace

void CareerTrainingFlowRegistry::Register(std::string scenarioId, Factory factory)
{
    scenarioId = NormalizeScenarioId(std::move(scenarioId));
    if (scenarioId.empty() || !factory)
        throw std::invalid_argument("A scenario ID and career training flow factory are required.");
    m_factories[std::move(scenarioId)] = std::move(factory);
}

std::shared_ptr<ICareerTrainingFlow> CareerTrainingFlowRegistry::Create(
    const std::string& scenarioId,
    std::shared_ptr<CareerVisualResources> resources) const
{
    const auto found = m_factories.find(NormalizeScenarioId(scenarioId));
    if (found == m_factories.end())
        return {};
    return found->second(std::move(resources));
}

} // namespace UmaAssistant::Career
