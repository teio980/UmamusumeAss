#pragma once

#include "CareerState.hpp"
#include "ActionIntent.hpp"
#include "IScenarioModule.hpp"
#include "TrainingCandidateEvidence.hpp"
#include <string>
#include <vector>

namespace UmaAssistant::CareerDomain
{

class ICareerStrategy
{
public:
    virtual ~ICareerStrategy() = default;

    [[nodiscard]] virtual ActionIntent Choose(
        const CareerState& state,
        const IScenarioModule* scenario) = 0;

    // Strategies that score observed training cards opt in through this
    // capability rather than requiring runtime code to know a concrete type.
    [[nodiscard]] virtual bool RequiresTrainingCandidateScan() const noexcept { return false; }

    // Lets scenario-specific strategies request a visual candidate scan only
    // after their own priority and safety rules say training is actionable.
    [[nodiscard]] virtual bool ShouldScanTrainingCandidates(
        const CareerState& state,
        const IScenarioModule* scenario) const
    {
        (void)state;
        (void)scenario;
        return false;
    }

    virtual void SetTrainingCandidateEvidence(
        const std::vector<TrainingCandidateEvidence>& candidates)
    {
        (void)candidates;
    }

    // Clears per-turn visual evidence after interruption/recovery while
    // retaining durable strategy history such as a confirmed ratio cursor.
    virtual void ClearTrainingCandidateEvidence() noexcept {}

    virtual void ConfirmTraining(
        CareerState& state,
        const std::string& trainingType)
    {
        (void)state;
        (void)trainingType;
    }
};

} // namespace UmaAssistant::CareerDomain
