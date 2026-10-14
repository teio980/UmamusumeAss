#include "UraTrainingFlowAdapter.hpp"

#include <nlohmann/json.hpp>
#include <algorithm>
#include <array>
#include <cctype>
#include <chrono>

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

void EmitSmartTrainingScan(
    TaskEngine::ITaskExecutionContext& context,
    const CareerDomain::CareerState& state,
    const CareerDomain::Ura::UraTrainingScanner& scanner,
    const std::optional<std::string>& chosenType)
{
    nlohmann::json root;
    if (state.turnPositionText.has_value() && !state.turnPositionText->empty())
        root["date"] = *state.turnPositionText;
    else
        root["date"] = state.phaseId.empty() ? "Unknown" : state.phaseId;

    if (state.energy.IsReliable())
        root["energy"] = state.energy.Value();
    else
        root["energy"] = nullptr;

    if (chosenType.has_value() && !chosenType->empty())
        root["chosen"] = *chosenType;
    else
        root["chosen"] = nullptr;

    nlohmann::json candidatesJson = nlohmann::json::array();
    const auto& decision = scanner.Decision();
    if (!decision.allEvaluations.empty())
    {
        for (const auto& eval : decision.allEvaluations)
        {
            const auto& cand = eval.first;
            const auto& score = eval.second;
            nlohmann::json item;
            item["type"] = cand.trainingType;
            item["speed"] = cand.speedGain ? nlohmann::json(*cand.speedGain) : nlohmann::json(nullptr);
            item["stamina"] = cand.staminaGain ? nlohmann::json(*cand.staminaGain) : nlohmann::json(nullptr);
            item["power"] = cand.powerGain ? nlohmann::json(*cand.powerGain) : nlohmann::json(nullptr);
            item["guts"] = cand.gutsGain ? nlohmann::json(*cand.gutsGain) : nlohmann::json(nullptr);
            item["wit"] = cand.witGain ? nlohmann::json(*cand.witGain) : nlohmann::json(nullptr);
            item["skill_points"] = cand.skillPointGain ? nlohmann::json(*cand.skillPointGain) : nlohmann::json(nullptr);
            item["failure_rate"] = cand.failureRatePercent ? nlohmann::json(*cand.failureRatePercent) : nlohmann::json(nullptr);
            if (score.IsExcluded())
            {
                item["score"] = nullptr;
                item["exclusion_reason"] = score.exclusionReason;
            }
            else
            {
                item["score"] = score.totalScore;
                item["exclusion_reason"] = nullptr;
            }
            candidatesJson.push_back(item);
        }
    }
    else
    {
        for (const auto& cand : scanner.Candidates())
        {
            nlohmann::json item;
            item["type"] = cand.trainingType;
            item["speed"] = cand.speedGain ? nlohmann::json(*cand.speedGain) : nlohmann::json(nullptr);
            item["stamina"] = cand.staminaGain ? nlohmann::json(*cand.staminaGain) : nlohmann::json(nullptr);
            item["power"] = cand.powerGain ? nlohmann::json(*cand.powerGain) : nlohmann::json(nullptr);
            item["guts"] = cand.gutsGain ? nlohmann::json(*cand.gutsGain) : nlohmann::json(nullptr);
            item["wit"] = cand.witGain ? nlohmann::json(*cand.witGain) : nlohmann::json(nullptr);
            item["skill_points"] = cand.skillPointGain ? nlohmann::json(*cand.skillPointGain) : nlohmann::json(nullptr);
            item["failure_rate"] = cand.failureRatePercent ? nlohmann::json(*cand.failureRatePercent) : nlohmann::json(nullptr);
            item["score"] = nullptr;
            item["exclusion_reason"] = "no_data";
            candidatesJson.push_back(item);
        }
    }
    root["candidates"] = candidatesJson;

    TaskEngine::TaskExecutionEvent event{};
    event.type = TaskEngine::TaskEventType::RecognitionEvaluated;
    event.pipelineName = "career-training";
    event.taskName = "smart_training_scan";
    event.succeeded = true;
    event.score = 1.0;
    event.message = root.dump();
    event.timestampMs = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    context.EmitEvent(event);
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
    return m_hasScannedTurn && turnIndex && *turnIndex == m_scannedTurnIndex
        && state.confirmedTurnActions == m_scannedTurnActions
        && state.turnsToGoal.value == m_scannedTurnsToGoal;
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

    for (auto const& candidate : m_scanner.Candidates())
    {
        auto number = [](auto const& value) { return value ? std::to_string(*value) : std::string("unknown"); };
        context.Log("INFO", "Smart training candidate " + candidate.trainingType
            + ": gains=" + number(candidate.speedGain) + "/" + number(candidate.staminaGain)
            + "/" + number(candidate.powerGain) + "/" + number(candidate.gutsGain)
            + "/" + number(candidate.witGain) + ", skill=" + number(candidate.skillPointGain)
            + ", failure=" + number(candidate.failureRatePercent)
            + "%, core confidence=" + std::to_string(candidate.coreConfidence)
            + ", failure confidence=" + std::to_string(candidate.failureRateConfidence));
    }

    if (m_scanner.Stage() == CareerDomain::Ura::UraTrainingScannerStage::Fallback
        && m_scanner.Candidates().size() == 5)
    {
        strategy.SetTrainingCandidateEvidence(ToEvidence(m_scanner.Candidates()));
        m_hasScannedTurn = true;
        m_scannedTurnIndex = CurrentTurnIndex(state).value_or(state.turnIndex);
        m_scannedTurnActions = state.confirmedTurnActions;
        m_scannedTurnsToGoal = state.turnsToGoal.value;
        context.Log("WARNING", "Smart training scan found no safe candidate: " + scan.detail);
        EmitSmartTrainingScan(context, state, m_scanner, std::nullopt);
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
    m_scannedTurnActions = state.confirmedTurnActions;
    m_scannedTurnsToGoal = state.turnsToGoal.value;

    result.intent = strategy.Choose(state, &scenario);
    if (result.intent.kind != CareerDomain::ActionIntentKind::Training)
    {
        EmitSmartTrainingScan(context, state, m_scanner, std::nullopt);
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

    EmitSmartTrainingScan(context, state, m_scanner, winner);

    if (context.IsCancelled())
    {
        result.status = CareerTrainingSelectionStatus::Cancelled;
        result.error = "URA training selection was cancelled.";
        return result;
    }
    if (!m_scanner.PrepareTrainingTap())
    {
        result.error = "URA selected training could not be prepared for execution.";
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
    m_scannedTurnActions = 0;
    m_scannedTurnsToGoal.reset();
}

void RegisterTrainingFlow(CareerTrainingFlowRegistry& registry)
{
    registry.Register("ura", [](std::shared_ptr<CareerVisualResources> resources) {
        return std::make_shared<UraTrainingFlowAdapter>(std::move(resources));
    });
}

} // namespace UmaAssistant::Career::Ura
