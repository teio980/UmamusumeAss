#pragma once

#include "UmaAssistantCore/CareerDomain/CareerObservation.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraSmartTrainingScorer.hpp"

#include <optional>
#include <string>
#include <vector>

namespace UmaAssistant::CareerDomain::Ura
{

enum class UraTrainingScannerStage
{
    Idle,
    SamplingCandidates,
    VerifyingSelection,
    ReadyToSelect,
    ReadyToTap,
    AwaitingOutcome,
    Committed,
    Fallback
};

struct UraTrainingMarkerMatch
{
    std::string trainingType;
    bool found{false};
    double confidence{0.0};
};

// A value-only summary of marker/OCR work performed by the runtime. No frame,
// image, OCR engine, or device handle crosses this domain boundary.
struct UraTrainingMarkerEvidence
{
    bool trainingSelectionScreen{false};
    std::vector<UraTrainingMarkerMatch> matches;
    std::optional<std::string> raisedTrainingType;
};

struct UraTrainingCommit
{
    std::string trainingType;
    std::optional<int> turnIndex;
};

// Pure scan/action state machine. Five single-frame candidates are scored,
// and the selected card must pass a fresh value check before authorization.
class UraTrainingScanner
{
public:
    // Training logo tasks use a profile threshold of 0.70; the visual layer
    // supplies the actual matched score after that configured threshold passes.
    static constexpr double MinimumMarkerConfidence = 0.70;

    [[nodiscard]] UraTrainingScannerStage Stage() const noexcept { return m_stage; }
    [[nodiscard]] const std::string& FailureReason() const noexcept { return m_failureReason; }
    [[nodiscard]] const std::vector<TrainingCandidate>& Candidates() const noexcept { return m_candidates; }
    [[nodiscard]] const SmartTrainingDecision& Decision() const noexcept { return m_decision; }
    [[nodiscard]] std::optional<std::string> SelectedTrainingType() const;
    [[nodiscard]] bool CanTapTraining() const noexcept
    {
        return m_stage == UraTrainingScannerStage::ReadyToTap;
    }

    UraTrainingScannerStage BeginCandidateScan(
        const UraTrainingMarkerEvidence& markers,
        RaceDistanceBand distance,
        std::string careerStage,
        std::optional<int> energyPercent,
        std::optional<int> turnIndex = std::nullopt);

    // Submit one final reading per card, after any bounded unreadable retry.
    bool SubmitCandidateSample(const TrainingCandidate& sample);

    // Replace the current winner with its fresh reading and score again. A
    // changed reading/winner stays unverified until another fresh check agrees.
    bool VerifySelectedCandidate(const TrainingCandidate& sample);

    bool PrepareTrainingTap();

    // Call only once the executor has actually sent the authorized tap.
    bool MarkTrainingTapSent() noexcept;

    // Repeated or unrelated observations cannot commit a training action.
    [[nodiscard]] std::optional<UraTrainingCommit> ObserveOutcome(
        CareerScreenKind screenKind,
        const ObservedValue<int>& turnIndexEvidence);

    void Reset() noexcept;

private:
    static bool HasValidFiveMarkers(const UraTrainingMarkerEvidence& markers);
    void ScoreCandidates();
    void Fail(std::string reason);

    UraTrainingScannerStage m_stage{UraTrainingScannerStage::Idle};
    RaceDistanceBand m_distance{RaceDistanceBand::Unknown};
    std::string m_careerStage;
    std::optional<int> m_energyPercent;
    std::optional<int> m_startTurnIndex;
    std::string m_failureReason;
    std::vector<TrainingCandidate> m_candidates;
    SmartTrainingDecision m_decision;
    std::vector<std::optional<TrainingCandidate>> m_candidateSamples;
};

} // namespace UmaAssistant::CareerDomain::Ura
