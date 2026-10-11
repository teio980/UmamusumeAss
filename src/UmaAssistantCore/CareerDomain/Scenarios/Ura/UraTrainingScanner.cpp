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

template <typename T>
std::optional<T> MergeValue(const std::optional<T>& first, const std::optional<T>& second)
{
    if (first.has_value() && second.has_value() && *first == *second)
        return first;
    return std::nullopt;
}

ObservationSource MergeSource(ObservationSource first, ObservationSource second)
{
    return first == second ? first : ObservationSource::Derived;
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
    m_trainingTypes.assign(std::begin(kTrainingTypes), std::end(kTrainingTypes));
    m_candidateSamples.resize(m_trainingTypes.size());
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
    const auto type = std::find(m_trainingTypes.begin(), m_trainingTypes.end(), normalized);
    if (type == m_trainingTypes.end())
    {
        Fail("A candidate sample was not one of the five required cards.");
        return false;
    }
    auto& pair = m_candidateSamples[static_cast<size_t>(type - m_trainingTypes.begin())];
    TrainingCandidate normalizedSample = sample;
    normalizedSample.trainingType = normalized;
    if (!pair.first.has_value())
    {
        pair.first = std::move(normalizedSample);
        return true;
    }
    if (!pair.second.has_value())
    {
        pair.second = std::move(normalizedSample);
    }
    else
    {
        Fail("A card was sampled more than twice in one scan.");
        return false;
    }

    const bool allComplete = std::all_of(m_candidateSamples.begin(), m_candidateSamples.end(),
        [](const SamplePair& item) { return item.first.has_value() && item.second.has_value(); });
    if (!allComplete)
        return true;

    m_candidates.clear();
    m_candidates.reserve(m_candidateSamples.size());
    for (const auto& item : m_candidateSamples)
    {
        const auto merged = MergeStable(*item.first, *item.second);
        if (!merged.has_value())
        {
            Fail("A card changed type between samples.");
            return false;
        }
        m_candidates.push_back(*merged);
    }

    m_decision = UraSmartTrainingScorer::Choose(m_candidates, m_distance, m_careerStage, m_energyPercent);
    if (!m_decision.IsSafe())
    {
        Fail("No safe, stable training candidate: " + m_decision.decisionReason);
        return false;
    }
    m_stage = UraTrainingScannerStage::ReadyToSelect;
    return true;
}

bool UraTrainingScanner::BeginWinnerConfirmation(const UraTrainingMarkerEvidence& markers)
{
    if (m_stage != UraTrainingScannerStage::ReadyToSelect || !m_decision.chosenCandidate.has_value())
        return false;
    if (!HasValidFiveMarkers(markers))
    {
        Fail("Fresh training markers were incomplete or unreliable before selection confirmation.");
        return false;
    }
    if (!markers.raisedTrainingType.has_value())
    {
        Fail("The selected candidate was not visibly raised before the training tap.");
        return false;
    }
    std::string raised;
    if (!TrainingTypeCatalog::TryNormalize(*markers.raisedTrainingType, raised) ||
        raised != m_decision.chosenCandidate->trainingType)
    {
        Fail("The raised training card did not match the scored winner.");
        return false;
    }

    m_scannedWinner = m_decision.chosenCandidate;
    m_winnerSamples = {};
    m_stage = UraTrainingScannerStage::SamplingWinnerConfirmation;
    return true;
}

bool UraTrainingScanner::SubmitWinnerConfirmationSample(const TrainingCandidate& sample)
{
    if (m_stage != UraTrainingScannerStage::SamplingWinnerConfirmation || !m_scannedWinner.has_value())
        return false;

    std::string normalized;
    if (!TrainingTypeCatalog::TryNormalize(sample.trainingType, normalized) ||
        normalized != m_scannedWinner->trainingType)
    {
        Fail("Winner confirmation sample did not match the selected training type.");
        return false;
    }

    TrainingCandidate normalizedSample = sample;
    normalizedSample.trainingType = normalized;
    if (!m_winnerSamples.first.has_value())
    {
        m_winnerSamples.first = std::move(normalizedSample);
        return true;
    }
    if (m_winnerSamples.second.has_value())
    {
        Fail("The winner was sampled more than twice during confirmation.");
        return false;
    }
    m_winnerSamples.second = std::move(normalizedSample);

    const auto merged = MergeStable(*m_winnerSamples.first, *m_winnerSamples.second);
    if (!merged.has_value() || !MatchesWinnerEvidence(*m_scannedWinner, *merged))
    {
        Fail("Fresh winner values were unstable or differed from the scored candidate.");
        return false;
    }
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
    m_trainingTypes.clear();
    m_candidates.clear();
    m_decision = {};
    m_candidateSamples.clear();
    m_winnerSamples = {};
    m_scannedWinner.reset();
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

std::optional<TrainingCandidate> UraTrainingScanner::MergeStable(
    const TrainingCandidate& first,
    const TrainingCandidate& second)
{
    std::string firstType;
    std::string secondType;
    if (!TrainingTypeCatalog::TryNormalize(first.trainingType, firstType) ||
        !TrainingTypeCatalog::TryNormalize(second.trainingType, secondType) || firstType != secondType)
        return std::nullopt;

    TrainingCandidate merged;
    merged.trainingType = firstType;
    merged.speedGain = MergeValue(first.speedGain, second.speedGain);
    merged.staminaGain = MergeValue(first.staminaGain, second.staminaGain);
    merged.powerGain = MergeValue(first.powerGain, second.powerGain);
    merged.gutsGain = MergeValue(first.gutsGain, second.gutsGain);
    merged.witGain = MergeValue(first.witGain, second.witGain);
    merged.skillPointGain = MergeValue(first.skillPointGain, second.skillPointGain);
    merged.failureRatePercent = MergeValue(first.failureRatePercent, second.failureRatePercent);
    merged.trainingLevel = MergeValue(first.trainingLevel, second.trainingLevel);
    merged.unbondedSupportCount = MergeValue(first.unbondedSupportCount, second.unbondedSupportCount);
    merged.hintCount = MergeValue(first.hintCount, second.hintCount);

    if (IsValidConfidence(first.coreConfidence) && IsValidConfidence(second.coreConfidence))
        merged.coreConfidence = std::min(first.coreConfidence, second.coreConfidence);
    else
        merged.coreConfidence = 0.0;
    if (IsValidConfidence(first.failureRateConfidence) && IsValidConfidence(second.failureRateConfidence))
        merged.failureRateConfidence = std::min(first.failureRateConfidence, second.failureRateConfidence);
    else
        merged.failureRateConfidence = 0.0;
    if (IsValidConfidence(first.auxiliaryConfidence) && IsValidConfidence(second.auxiliaryConfidence))
        merged.auxiliaryConfidence = std::min(first.auxiliaryConfidence, second.auxiliaryConfidence);
    else
        merged.auxiliaryConfidence = 0.0;

    merged.coreSource = MergeSource(first.coreSource, second.coreSource);
    merged.failureRateSource = MergeSource(first.failureRateSource, second.failureRateSource);
    merged.auxiliarySource = MergeSource(first.auxiliarySource, second.auxiliarySource);
    return merged;
}

bool UraTrainingScanner::MatchesWinnerEvidence(
    const TrainingCandidate& scanned,
    const TrainingCandidate& confirmed)
{
    if (scanned.trainingType != confirmed.trainingType ||
        !confirmed.HasReliableCoreValues() || !confirmed.HasReliableFailureRate())
        return false;

    const auto sameIfScannedKnown = [](const std::optional<int>& expected, const std::optional<int>& actual) {
        return !expected.has_value() || (actual.has_value() && *expected == *actual);
    };
    return sameIfScannedKnown(scanned.speedGain, confirmed.speedGain) &&
           sameIfScannedKnown(scanned.staminaGain, confirmed.staminaGain) &&
           sameIfScannedKnown(scanned.powerGain, confirmed.powerGain) &&
           sameIfScannedKnown(scanned.gutsGain, confirmed.gutsGain) &&
           sameIfScannedKnown(scanned.witGain, confirmed.witGain) &&
           sameIfScannedKnown(scanned.skillPointGain, confirmed.skillPointGain) &&
           sameIfScannedKnown(scanned.failureRatePercent, confirmed.failureRatePercent) &&
           sameIfScannedKnown(scanned.trainingLevel, confirmed.trainingLevel) &&
           sameIfScannedKnown(scanned.unbondedSupportCount, confirmed.unbondedSupportCount) &&
           sameIfScannedKnown(scanned.hintCount, confirmed.hintCount);
}

void UraTrainingScanner::Fail(std::string reason)
{
    m_failureReason = std::move(reason);
    m_stage = UraTrainingScannerStage::Fallback;
}

} // namespace UmaAssistant::CareerDomain::Ura
