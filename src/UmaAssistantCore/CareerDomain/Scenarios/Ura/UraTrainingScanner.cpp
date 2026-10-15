#include "UraTrainingScanner.hpp"

#include "UmaAssistantCore/CareerDomain/TrainingStrategies.hpp"

#include <algorithm>
#include <cmath>
#include <iterator>
#include <utility>

namespace UmaAssistant::CareerDomain::Ura
{

namespace
{

constexpr const char* kTrainingTypes[] = {"speed", "stamina", "power", "guts", "wit"};

bool IsValidConfidence(double value)
{
    return std::isfinite(value) && value >= 0.0 && value <= 1.0;
}

bool SameScoringValues(const TrainingCandidate& first, const TrainingCandidate& second)
{
    return first.speedGain == second.speedGain && first.staminaGain == second.staminaGain
        && first.powerGain == second.powerGain && first.gutsGain == second.gutsGain
        && first.witGain == second.witGain && first.skillPointGain == second.skillPointGain
        && first.failureRatePercent == second.failureRatePercent
        && first.unbondedSupportCount == second.unbondedSupportCount && first.hintCount == second.hintCount;
}

} // namespace

std::optional<std::string> UraTrainingScanner::SelectedTrainingType() const
{
    if (!m_decision.chosenCandidate.has_value())
        return std::nullopt;
    return m_decision.chosenCandidate->trainingType;
}

UraTrainingScannerStage UraTrainingScanner::BeginCandidateScan(
    const UraTrainingMarkerEvidence& markers,
    RaceDistanceBand distance,
    std::string careerStage,
    std::optional<int> energyPercent,
    std::optional<int> turnIndex)
{
    Reset();
    if (!HasValidFiveMarkers(markers))
    {
        Fail("Training selection screen did not provide five distinct, reliable card markers.");
        return m_stage;
    }

    m_distance = distance;
    m_careerStage = std::move(careerStage);
    m_energyPercent = energyPercent;
    m_startTurnIndex = turnIndex;
    m_candidateSamples.resize(std::size(kTrainingTypes));
    m_stage = UraTrainingScannerStage::SamplingCandidates;
    return m_stage;
}

bool UraTrainingScanner::SubmitCandidateSample(const TrainingCandidate& sample)
{
    if (m_stage != UraTrainingScannerStage::SamplingCandidates)
        return false;

    std::string normalized;
    if (!TrainingTypeCatalog::TryNormalize(sample.trainingType, normalized))
    {
        Fail("A candidate sample had an unsupported training type.");
        return false;
    }
    const auto type = std::find(std::begin(kTrainingTypes), std::end(kTrainingTypes), normalized);
    if (type == std::end(kTrainingTypes))
    {
        Fail("A candidate sample was not one of the five required cards.");
        return false;
    }
    auto& reading = m_candidateSamples[static_cast<std::size_t>(type - std::begin(kTrainingTypes))];
    if (reading)
    {
        Fail("A card was submitted more than once in one scan.");
        return false;
    }
    reading = sample;
    reading->trainingType = normalized;

    const bool allComplete = std::all_of(m_candidateSamples.begin(), m_candidateSamples.end(),
        [](const auto& item) { return item.has_value(); });
    if (!allComplete)
        return true;

    m_candidates.clear();
    m_candidates.reserve(m_candidateSamples.size());
    for (const auto& item : m_candidateSamples)
        m_candidates.push_back(*item);
    m_candidateSamples.clear();
    ScoreCandidates();
    return m_stage != UraTrainingScannerStage::Fallback;
}

void UraTrainingScanner::ScoreCandidates()
{
    m_decision = UraSmartTrainingScorer::Choose(m_candidates, m_distance, m_careerStage, m_energyPercent);
    if (!m_decision.IsSafe())
    {
        Fail("No safe training candidate: " + m_decision.decisionReason);
        return;
    }
    m_stage = UraTrainingScannerStage::VerifyingSelection;
}

bool UraTrainingScanner::VerifySelectedCandidate(const TrainingCandidate& sample)
{
    if (m_stage != UraTrainingScannerStage::VerifyingSelection || !m_decision.chosenCandidate)
        return false;
    std::string normalized;
    if (!TrainingTypeCatalog::TryNormalize(sample.trainingType, normalized)
        || normalized != m_decision.chosenCandidate->trainingType)
    {
        Fail("The fresh candidate did not match the selected training.");
        return false;
    }
    const auto before = *m_decision.chosenCandidate;
    const auto candidate = std::find_if(m_candidates.begin(), m_candidates.end(), [&](const auto& item) {
        return item.trainingType == normalized;
    });
    if (candidate == m_candidates.end()) return false;
    *candidate = sample;
    candidate->trainingType = normalized;
    ScoreCandidates();
    if (m_stage == UraTrainingScannerStage::Fallback) return false;
    if (SameScoringValues(before, *candidate) && candidate->HasReliableCoreValues()
        && candidate->HasReliableFailureRate() && SelectedTrainingType() == normalized)
        m_stage = UraTrainingScannerStage::ReadyToSelect;
    return true;
}

bool UraTrainingScanner::PrepareTrainingTap()
{
    if (m_stage != UraTrainingScannerStage::ReadyToSelect || !m_decision.chosenCandidate.has_value())
        return false;
    m_stage = UraTrainingScannerStage::ReadyToTap;
    return true;
}

bool UraTrainingScanner::MarkTrainingTapSent() noexcept
{
    if (m_stage != UraTrainingScannerStage::ReadyToTap)
        return false;
    m_stage = UraTrainingScannerStage::AwaitingOutcome;
    return true;
}

std::optional<UraTrainingCommit> UraTrainingScanner::ObserveOutcome(
    CareerScreenKind screenKind,
    const ObservedValue<int>& turnIndexEvidence)
{
    if (m_stage != UraTrainingScannerStage::AwaitingOutcome)
        return std::nullopt;

    const bool resultObserved = screenKind == CareerScreenKind::TrainingResult;
    const bool turnAdvanced = m_startTurnIndex.has_value() && turnIndexEvidence.IsReliable() &&
                              turnIndexEvidence.Value() > *m_startTurnIndex;
    if (!resultObserved && !turnAdvanced)
        return std::nullopt;

    const auto selected = SelectedTrainingType();
    if (!selected.has_value())
    {
        Fail("Training outcome was observed without a selected candidate.");
        return std::nullopt;
    }
    m_stage = UraTrainingScannerStage::Committed;
    return UraTrainingCommit{*selected,
        turnIndexEvidence.IsReliable() ? std::optional<int>{turnIndexEvidence.Value()} : m_startTurnIndex};
}

void UraTrainingScanner::Reset() noexcept
{
    m_stage = UraTrainingScannerStage::Idle;
    m_distance = RaceDistanceBand::Unknown;
    m_careerStage.clear();
    m_energyPercent.reset();
    m_startTurnIndex.reset();
    m_failureReason.clear();
    m_candidates.clear();
    m_decision = {};
    m_candidateSamples.clear();
}

bool UraTrainingScanner::HasValidFiveMarkers(const UraTrainingMarkerEvidence& markers)
{
    if (!markers.trainingSelectionScreen || markers.matches.size() != 5)
        return false;

    std::vector<std::string> seen;
    seen.reserve(5);
    for (const auto& marker : markers.matches)
    {
        std::string normalized;
        if (!marker.found || !IsValidConfidence(marker.confidence) ||
            marker.confidence < MinimumMarkerConfidence ||
            !TrainingTypeCatalog::TryNormalize(marker.trainingType, normalized) ||
            std::find(seen.begin(), seen.end(), normalized) != seen.end())
            return false;
        seen.push_back(std::move(normalized));
    }
    return seen.size() == 5;
}

void UraTrainingScanner::Fail(std::string reason)
{
    m_failureReason = std::move(reason);
    m_stage = UraTrainingScannerStage::Fallback;
}

} // namespace UmaAssistant::CareerDomain::Ura
