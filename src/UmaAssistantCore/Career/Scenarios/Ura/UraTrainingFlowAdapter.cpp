#include "UraTrainingFlowAdapter.hpp"

#include <algorithm>
#include <array>
#include <cctype>

namespace UmaAssistant::Career::Ura
{

namespace
{

CareerDomain::ObservedValue<int> FromOptional(
    const std::optional<int>& value,
    double confidence,
    CareerDomain::ObservationSource source)
{
    if (!value)
        return CareerDomain::ObservedValue<int>::Unknown();
    return CareerDomain::ObservedValue<int>::Make(*value, confidence, source);
}

CareerDomain::TrainingCandidateEvidence ToEvidence(
    const CareerDomain::Ura::TrainingCandidate& candidate)
{
    CareerDomain::TrainingCandidateEvidence evidence;
    evidence.trainingType = candidate.trainingType;
    evidence.statGains = {
        FromOptional(candidate.speedGain, candidate.coreConfidence, candidate.coreSource),
        FromOptional(candidate.staminaGain, candidate.coreConfidence, candidate.coreSource),
        FromOptional(candidate.powerGain, candidate.coreConfidence, candidate.coreSource),
        FromOptional(candidate.gutsGain, candidate.coreConfidence, candidate.coreSource),
        FromOptional(candidate.witGain, candidate.coreConfidence, candidate.coreSource)};
    evidence.skillPointGain = FromOptional(
        candidate.skillPointGain, candidate.coreConfidence, candidate.coreSource);
    evidence.failureRatePercent = FromOptional(
        candidate.failureRatePercent, candidate.failureRateConfidence, candidate.failureRateSource);
    evidence.trainingLevel = FromOptional(
        candidate.trainingLevel, candidate.auxiliaryConfidence, candidate.auxiliarySource);
    evidence.unbondedSupportCount = FromOptional(
        candidate.unbondedSupportCount, candidate.auxiliaryConfidence, candidate.auxiliarySource);
    evidence.hintCount = FromOptional(
        candidate.hintCount, candidate.auxiliaryConfidence, candidate.auxiliarySource);
    return evidence;
}

std::vector<CareerDomain::TrainingCandidateEvidence> ToEvidence(
    const std::vector<CareerDomain::Ura::TrainingCandidate>& candidates)
{
    std::vector<CareerDomain::TrainingCandidateEvidence> evidence;
    evidence.reserve(candidates.size());
    for (const auto& candidate : candidates)
        evidence.push_back(ToEvidence(candidate));
    return evidence;
}

std::optional<int> CurrentTurnIndex(const CareerDomain::CareerState& state)
{
    if (state.turnIndexEvidence.IsReliable())
        return state.turnIndexEvidence.Value();
    return state.turnIndex;
}

} // namespace

UraTrainingFlowAdapter::UraTrainingFlowAdapter(std::shared_ptr<CareerVisualResources> resources)
    : m_resources(std::move(resources))
    , m_visualScanner(m_resources)
{
}

bool UraTrainingFlowAdapter::HasCandidatesForCurrentTurn(
    const CareerDomain::CareerState& state) const noexcept
{
    const auto turnIndex = CurrentTurnIndex(state);
    return m_hasScannedTurn && turnIndex && *turnIndex == m_scannedTurnIndex;
}

CareerTrainingSelectionResult UraTrainingFlowAdapter::Select(
    TaskEngine::ITaskExecutionContext& context,
    const CareerDomain::CareerState& state,
    const CareerDomain::IScenarioModule& scenario,
    CareerDomain::ICareerStrategy& strategy)
{
    CareerTrainingSelectionResult result;
    if (!m_resources || !m_resources->IsReady())
    {
        result.error = "URA candidate scan has no ready career resource package.";
        return result;
    }
    m_scanner.Reset();

    const auto raceDistance = scenario.GetCurrentTrainingRaceDistance(state);
    const auto distanceBand = raceDistance
        ? CareerDomain::Ura::UraSmartTrainingScorer::ParseDistanceBand(raceDistance->distanceBand, raceDistance->meters)
        : CareerDomain::Ura::RaceDistanceBand::Unknown;
    const auto scan = m_visualScanner.ScanCandidates(
        context,
        m_scanner,
        distanceBand,
        state.turnPositionText.value_or(state.phaseId),
        state.energy.IsReliable() ? std::optional<int>(state.energy.Value()) : std::nullopt,
        CurrentTurnIndex(state));

    if (context.IsCancelled())
    {
        result.status = CareerTrainingSelectionStatus::Cancelled;
        result.error = "URA candidate scan was cancelled.";
        return result;
    }

    if (m_scanner.Stage() == CareerDomain::Ura::UraTrainingScannerStage::Fallback
        && m_scanner.Candidates().size() == 5)
    {
        strategy.SetTrainingCandidateEvidence(ToEvidence(m_scanner.Candidates()));
        m_hasScannedTurn = true;
        m_scannedTurnIndex = CurrentTurnIndex(state).value_or(state.turnIndex);
        m_scanner.Reset();
        result.status = CareerTrainingSelectionStatus::BackOut;
        result.intent = CareerDomain::ActionIntent::MakeAdvance(
            "Return to Main after the stable scan found no safe training candidate.");
        return result;
    }

    if (!scan.IsReadyToSelect())
    {
        result.error = scan.detail.empty()
            ? m_scanner.FailureReason()
            : scan.detail;
        if (result.error.empty())
            result.error = "URA candidate scan did not reach a stable selection.";
        return result;
    }

    strategy.SetTrainingCandidateEvidence(ToEvidence(m_scanner.Candidates()));
    m_hasScannedTurn = true;
    m_scannedTurnIndex = CurrentTurnIndex(state).value_or(state.turnIndex);

    result.intent = strategy.Choose(state, &scenario);
    if (result.intent.kind != CareerDomain::ActionIntentKind::Training)
    {
        m_scanner.Reset();
        result.status = CareerTrainingSelectionStatus::BackOut;
        result.intent = CareerDomain::ActionIntent::MakeAdvance(
            "Return to Main because no scanned training candidate was selected.");
        return result;
    }

    const auto winner = m_scanner.SelectedTrainingType();
    auto fold = [](std::string value) {
        std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
            return static_cast<char>(std::tolower(c));
        });
        return value;
    };
    if (!winner || fold(*winner) != fold(result.intent.trainingType))
    {
        result.error = "URA strategy choice did not match the scanned winner.";
        return result;
    }

    const auto confirmed = m_visualScanner.ConfirmWinner(context, m_scanner);
    if (!confirmed.IsReadyToTap() || !m_scanner.CanTapTraining())
    {
        result.status = context.IsCancelled()
            ? CareerTrainingSelectionStatus::Cancelled
            : CareerTrainingSelectionStatus::Failed;
        result.error = confirmed.detail.empty()
            ? "URA winner failed fresh visual confirmation."
            : confirmed.detail;
        return result;
    }

    result.status = CareerTrainingSelectionStatus::Ready;
    return result;
}

bool UraTrainingFlowAdapter::HasPendingConfirmedTap() const noexcept
{
    return m_scanner.CanTapTraining();
}

bool UraTrainingFlowAdapter::HasPendingOutcome() const noexcept
{
    return m_scanner.Stage() == CareerDomain::Ura::UraTrainingScannerStage::AwaitingOutcome;
}

bool UraTrainingFlowAdapter::MarkTrainingTapSent() noexcept
{
    return m_scanner.MarkTrainingTapSent();
}

std::optional<std::string> UraTrainingFlowAdapter::ObserveOutcome(
    const CareerDomain::CareerObservation& observation)
{
    if (m_scanner.Stage() != CareerDomain::Ura::UraTrainingScannerStage::AwaitingOutcome)
        return std::nullopt;
    auto commit = m_scanner.ObserveOutcome(observation.screenKind, observation.turnIndexEvidence);
    if (!commit)
        return std::nullopt;
    m_scanner.Reset();
    m_hasScannedTurn = false;
    return commit->trainingType;
}

void UraTrainingFlowAdapter::Reset() noexcept
{
    m_scanner.Reset();
    m_hasScannedTurn = false;
    m_scannedTurnIndex = 0;
}

void RegisterTrainingFlow(CareerTrainingFlowRegistry& registry)
{
    registry.Register("ura", [](std::shared_ptr<CareerVisualResources> resources) {
        return std::make_shared<UraTrainingFlowAdapter>(std::move(resources));
    });
}

} // namespace UmaAssistant::Career::Ura
