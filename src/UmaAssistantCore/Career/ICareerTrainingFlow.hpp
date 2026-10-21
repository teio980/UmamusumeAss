#pragma once

#include "UmaAssistantCore/CareerDomain/CareerState.hpp"
#include "UmaAssistantCore/CareerDomain/ICareerStrategy.hpp"
#include "UmaAssistantCore/CareerDomain/IScenarioModule.hpp"

#include <optional>
#include <string>

namespace UmaAssistant::TaskEngine { class ITaskExecutionContext; }

namespace UmaAssistant::Career
{

enum class CareerTrainingSelectionStatus
{
    Ready,
    BackOut,
    Failed,
    Cancelled
};

struct CareerTrainingSelectionResult
{
    CareerTrainingSelectionStatus status{CareerTrainingSelectionStatus::Failed};
    CareerDomain::ActionIntent intent;
    std::string error;
};

// Scenario-specific visual preparation behind a scenario-neutral coordinator
// contract. Implementations must only authorize training after fresh evidence.
class ICareerTrainingFlow
{
public:
    virtual ~ICareerTrainingFlow() = default;

    [[nodiscard]] virtual bool HasCandidatesForCurrentTurn(
        const CareerDomain::CareerState& state) const noexcept = 0;

    virtual CareerTrainingSelectionResult Select(
        TaskEngine::ITaskExecutionContext& context,
        const CareerDomain::CareerState& state,
        const CareerDomain::IScenarioModule& scenario,
        CareerDomain::ICareerStrategy& strategy) = 0;

    [[nodiscard]] virtual bool HasPendingConfirmedTap() const noexcept = 0;
    [[nodiscard]] virtual bool HasPendingOutcome() const noexcept = 0;
    virtual bool MarkTrainingTapSent() noexcept = 0;

    // Emits one confirmed training type when a result or reliable turn change
    // proves that the previously authorized tap committed.
    virtual std::optional<std::string> ObserveOutcome(
        const CareerDomain::CareerObservation& observation) = 0;

    virtual void Reset() noexcept = 0;
};

} // namespace UmaAssistant::Career
