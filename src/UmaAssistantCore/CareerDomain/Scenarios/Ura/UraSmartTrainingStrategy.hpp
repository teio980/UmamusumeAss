#pragma once

#include "UmaAssistantCore/CareerDomain/ICareerStrategy.hpp"
#include "UraSmartTrainingScorer.hpp"

#include <memory>
#include <string>
#include <vector>

namespace UmaAssistant::CareerDomain
{

class UraSmartTrainingStrategy : public ICareerStrategy
{
public:
    explicit UraSmartTrainingStrategy(
        Ura::RaceDistanceBand distance = Ura::RaceDistanceBand::Unknown,
        int restThreshold = 50);

    void SetLatestCandidates(std::vector<Ura::TrainingCandidate> candidates);
    [[nodiscard]] bool RequiresTrainingCandidateScan() const noexcept override { return true; }
    [[nodiscard]] bool ShouldScanTrainingCandidates(
        const CareerState& state,
        const IScenarioModule* scenario) const override;
    void SetTrainingCandidateEvidence(
        const std::vector<TrainingCandidateEvidence>& candidates) override;
    void ClearTrainingCandidateEvidence() noexcept override { ClearLatestCandidates(); }
    void ClearLatestCandidates() noexcept;
    [[nodiscard]] const std::vector<Ura::TrainingCandidate>& LatestCandidates() const noexcept
    {
        return m_latestCandidates;
    }

    [[nodiscard]] Ura::SmartTrainingDecision LastDecision() const { return m_lastDecision; }

    ActionIntent Choose(
        const CareerState& state,
        const IScenarioModule* scenario) override;
    void ConfirmTraining(CareerState& state, const std::string& trainingType) override;

private:
    Ura::RaceDistanceBand m_distanceBand;
    int m_restThreshold;
    std::vector<Ura::TrainingCandidate> m_latestCandidates;
    Ura::SmartTrainingDecision m_lastDecision;
};

} // namespace UmaAssistant::CareerDomain
