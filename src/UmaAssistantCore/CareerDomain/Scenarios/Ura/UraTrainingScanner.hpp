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
    ReadyToSelect,
    SamplingWinnerConfirmation,
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

// Pure scan/confirmation state machine. It accepts only stable double samples,
// requires the selected winner to be re-read and visibly raised before a tap,
// and emits one commit only after a result screen or observed turn advancement.
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

    // Call once for each independently captured card sample. Each of the five
    // cards must produce two samples; changing or missing values stay unknown.
    bool SubmitCandidateSample(const TrainingCandidate& sample);

    // Re-read all five markers after selection and require the chosen card to
    // be the raised card, then capture that card twice again.
    bool BeginWinnerConfirmation(const UraTrainingMarkerEvidence& markers);
    bool SubmitWinnerConfirmationSample(const TrainingCandidate& sample);

    // Call only once the executor has actually sent the authorized tap.
    bool MarkTrainingTapSent() noexcept;

    // Repeated or unrelated observations cannot commit a training action.
    [[nodiscard]] std::optional<UraTrainingCommit> ObserveOutcome(
        CareerScreenKind screenKind,
        const ObservedValue<int>& turnIndexEvidence);

    void Reset() noexcept;

private:
    struct SamplePair
    {
        std::optional<TrainingCandidate> first;
        std::optional<TrainingCandidate> second;
    };

    static bool HasValidFiveMarkers(const UraTrainingMarkerEvidence& markers);
    static std::optional<TrainingCandidate> MergeStable(
        const TrainingCandidate& first,
        const TrainingCandidate& second);
    static bool MatchesWinnerEvidence(
        const TrainingCandidate& scanned,
        const TrainingCandidate& confirmed);
    void Fail(std::string reason);

    UraTrainingScannerStage m_stage{UraTrainingScannerStage::Idle};
    RaceDistanceBand m_distance{RaceDistanceBand::Unknown};
    std::string m_careerStage;
    std::optional<int> m_energyPercent;
    std::optional<int> m_startTurnIndex;
    std::string m_failureReason;
    std::vector<std::string> m_trainingTypes;
    std::vector<TrainingCandidate> m_candidates;
    SmartTrainingDecision m_decision;
    std::vector<SamplePair> m_candidateSamples;
    SamplePair m_winnerSamples;
    std::optional<TrainingCandidate> m_scannedWinner;
};

} // namespace UmaAssistant::CareerDomain::Ura
