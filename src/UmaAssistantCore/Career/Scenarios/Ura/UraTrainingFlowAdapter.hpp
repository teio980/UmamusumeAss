#pragma once

#include "UmaAssistantCore/Career/ICareerTrainingFlow.hpp"
#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include "UmaAssistantCore/Career/CareerTrainingFlowRegistry.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraTrainingScanner.hpp"
#include "UmaAssistantCore/Career/Scenarios/Ura/UraTrainingVisualScanner.hpp"

#include <memory>

namespace UmaAssistant::Career::Ura
{

class UraTrainingFlowAdapter final : public ICareerTrainingFlow
{
public:
    explicit UraTrainingFlowAdapter(std::shared_ptr<CareerVisualResources> resources);

    [[nodiscard]] bool HasCandidatesForCurrentTurn(
        const CareerDomain::CareerState& state) const noexcept override;

    CareerTrainingSelectionResult Select(
        TaskEngine::ITaskExecutionContext& context,
        const CareerDomain::CareerState& state,
        const CareerDomain::IScenarioModule& scenario,
        CareerDomain::ICareerStrategy& strategy) override;

    [[nodiscard]] bool HasPendingConfirmedTap() const noexcept override;
    [[nodiscard]] bool HasPendingOutcome() const noexcept override;
    bool MarkTrainingTapSent() noexcept override;
    std::optional<std::string> ObserveOutcome(
        const CareerDomain::CareerObservation& observation) override;
    void Reset() noexcept override;

private:
    std::shared_ptr<CareerVisualResources> m_resources;
    UraTrainingVisualScanner m_visualScanner;
    CareerDomain::Ura::UraTrainingScanner m_scanner;
    int m_scannedTurnIndex{0};
    int m_scannedTurnActions{0};
    std::optional<int> m_scannedTurnsToGoal;
    bool m_hasScannedTurn{false};
};

} // namespace UmaAssistant::Career::Ura

namespace UmaAssistant::Career::Ura
{

void RegisterTrainingFlow(CareerTrainingFlowRegistry& registry);

} // namespace UmaAssistant::Career::Ura
