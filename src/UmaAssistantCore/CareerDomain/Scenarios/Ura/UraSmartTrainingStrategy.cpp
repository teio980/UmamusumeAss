#include "UraSmartTrainingStrategy.hpp"

#include "UraScenarioModule.hpp"
#include "UraStrategyRegistry.hpp"
#include "UmaAssistantCore/CareerDomain/TrainingStrategies.hpp"

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <map>
#include <mutex>
#include <stdexcept>
#include <utility>

namespace UmaAssistant::CareerDomain::Ura
{

namespace
{

constexpr double kDistanceWeights[5][5] = {
    {1.4, 0.5, 1.0, 0.3, 0.8}, // Sprint
    {1.4, 0.8, 1.0, 0.3, 0.8}, // Mile
    {1.4, 1.2, 1.0, 0.3, 0.8}, // Middle
    {1.4, 1.6, 1.0, 0.3, 0.8}, // Extended
    {1.4, 1.2, 1.0, 0.3, 0.8}, // Unknown: middle-distance defaults
};

std::string Lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

std::string NormalizeId(const std::string& value)
{
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos)
        return {};
    const auto last = value.find_last_not_of(" \t\r\n");
    return Lower(value.substr(first, last - first + 1));
}

int TieBreakRank(const std::string& trainingType) noexcept
{
    const auto type = Lower(trainingType);
    if (type == "speed") return 0;
    if (type == "stamina") return 1;
    if (type == "power") return 2;
    if (type == "wit") return 3;
    if (type == "guts") return 4;
    return 5;
}

const double* WeightsFor(RaceDistanceBand distance) noexcept
{
    switch (distance)
    {
    case RaceDistanceBand::Sprint: return kDistanceWeights[0];
    case RaceDistanceBand::Mile: return kDistanceWeights[1];
    case RaceDistanceBand::Middle: return kDistanceWeights[2];
    case RaceDistanceBand::Extended: return kDistanceWeights[3];
    default: return kDistanceWeights[4];
    }
}

bool IsValidConfidence(double value) noexcept
{
    return std::isfinite(value) && value >= 0.0 && value <= 1.0;
}

std::optional<int> MonthFromText(const std::string& text)
{
    const auto lower = Lower(text);
    if (lower.find("month 7") != std::string::npos || lower.find("july") != std::string::npos ||
        lower.find("jul ") != std::string::npos || lower.find("7月") != std::string::npos)
        return 7;
    if (lower.find("month 8") != std::string::npos || lower.find("august") != std::string::npos ||
        lower.find("aug ") != std::string::npos || lower.find("8月") != std::string::npos)
        return 8;
    if (lower.find("month 6") != std::string::npos || lower.find("june") != std::string::npos ||
        lower.find("jun ") != std::string::npos || lower.find("6月") != std::string::npos)
        return 6;
    return std::nullopt;
}

bool IsLateJune(const CareerState& state)
{
    if (!state.turnPositionText.has_value())
        return false;
    const auto text = Lower(*state.turnPositionText);
    const bool eligibleYear = text.find("classic") != std::string::npos ||
                              text.find("senior") != std::string::npos;
    return eligibleYear && MonthFromText(text) == 6 && text.find("late") != std::string::npos;
}

ActionIntent UnknownEvidenceRest(const std::string& reason)
{
    return ActionIntent::MakeRest(reason, 0.5, ObservationSource::DefaultFallback);
}

} // namespace

bool TrainingCandidate::HasReliableCoreValues() const noexcept
{
    return speedGain.has_value() && *speedGain >= 0 &&
           staminaGain.has_value() && *staminaGain >= 0 &&
           powerGain.has_value() && *powerGain >= 0 &&
           gutsGain.has_value() && *gutsGain >= 0 &&
           witGain.has_value() && *witGain >= 0 &&
           skillPointGain.has_value() && *skillPointGain >= 0 &&
           IsValidConfidence(coreConfidence) &&
           coreConfidence >= UraSmartTrainingScorer::MinimumCoreConfidence;
}

bool TrainingCandidate::HasReliableFailureRate() const noexcept
{
    return failureRatePercent.has_value() &&
           *failureRatePercent >= 0 && *failureRatePercent <= 100 &&
           IsValidConfidence(failureRateConfidence) &&
           failureRateConfidence >= UraSmartTrainingScorer::MinimumFailureRateConfidence;
}

RaceDistanceBand UraSmartTrainingScorer::ParseDistanceBand(
    const std::string& bandText,
    std::optional<int> meters)
{
    const auto text = Lower(bandText);
    if (text.find("sprint") != std::string::npos || text.find("short") != std::string::npos ||
        text.find("短") != std::string::npos)
        return RaceDistanceBand::Sprint;
    if (text.find("mile") != std::string::npos || text.find("英里") != std::string::npos ||
        text.find("マイル") != std::string::npos)
        return RaceDistanceBand::Mile;
    if (text.find("extended") != std::string::npos || text.find("long") != std::string::npos ||
        text.find("长") != std::string::npos || text.find("長") != std::string::npos)
        return RaceDistanceBand::Extended;
    if (text.find("middle") != std::string::npos || text.find("medium") != std::string::npos ||
        text.find("中") != std::string::npos)
        return RaceDistanceBand::Middle;

    if (meters.has_value() && *meters >= 1000)
    {
        if (*meters <= 1400) return RaceDistanceBand::Sprint;
        if (*meters <= 1800) return RaceDistanceBand::Mile;
        if (*meters <= 2400) return RaceDistanceBand::Middle;
        return RaceDistanceBand::Extended;
    }
    return RaceDistanceBand::Unknown;
}

TrainingScoreBreakdown UraSmartTrainingScorer::Score(
    const TrainingCandidate& candidate,
    RaceDistanceBand distance,
    const std::string& careerStage,
    std::optional<int> energyPercent)
{
    TrainingScoreBreakdown score;
    score.distanceBand = distance;

    std::string normalizedTraining;
    if (!TrainingTypeCatalog::TryNormalize(candidate.trainingType, normalizedTraining))
    {
        score.exclusionReason = "training type is unsupported";
        return score;
    }
    if (!candidate.HasReliableCoreValues())
    {
        score.exclusionReason = "core gains are unknown or below confidence threshold";
        return score;
    }
    if (!candidate.HasReliableFailureRate())
    {
        score.exclusionReason = "failure rate is unknown or below confidence threshold";
        return score;
    }
    if (*candidate.failureRatePercent > MaximumFailureRatePercent)
    {
        score.exclusionReason = "failure rate " + std::to_string(*candidate.failureRatePercent) +
            "% exceeds " + std::to_string(MaximumFailureRatePercent) + "%";
        return score;
    }

    const auto* weights = WeightsFor(distance);
    score.speedContribution = *candidate.speedGain * weights[0];
    score.staminaContribution = *candidate.staminaGain * weights[1];
    score.powerContribution = *candidate.powerGain * weights[2];
    score.gutsContribution = *candidate.gutsGain * weights[3];
    score.witContribution = *candidate.witGain * weights[4];
    score.skillPointContribution = *candidate.skillPointGain * 0.5;
    score.successProbability = 1.0 - *candidate.failureRatePercent / 100.0;
    score.baseScore = (score.speedContribution + score.staminaContribution +
        score.powerContribution + score.gutsContribution + score.witContribution +
        score.skillPointContribution) * score.successProbability;
    if (score.baseScore <= 0.0)
    {
        score.exclusionReason = "expected gains are zero";
        return score;
    }

    const auto stage = Lower(careerStage);
    const int bondPerSupport = stage.find("junior") != std::string::npos ? 12
        : stage.find("classic") != std::string::npos ? 6 : 0;
    if (candidate.unbondedSupportCount.has_value() && *candidate.unbondedSupportCount >= 0 &&
        IsValidConfidence(candidate.auxiliaryConfidence) &&
        candidate.auxiliaryConfidence >= MinimumAuxiliaryConfidence)
    {
        score.bondBonus = *candidate.unbondedSupportCount * bondPerSupport;
    }
    if (candidate.hintCount.has_value() && *candidate.hintCount >= 0 &&
        IsValidConfidence(candidate.auxiliaryConfidence) &&
        candidate.auxiliaryConfidence >= MinimumAuxiliaryConfidence)
    {
        score.hintBonus = *candidate.hintCount * 4.0;
    }
    if (energyPercent.has_value() && *energyPercent < 50 && normalizedTraining == "wit")
        score.lowEnergyWitBonus = 15.0;

    score.totalScore = score.baseScore + score.bondBonus + score.hintBonus + score.lowEnergyWitBonus;
    return score;
}

SmartTrainingDecision UraSmartTrainingScorer::Choose(
    const std::vector<TrainingCandidate>& candidates,
    RaceDistanceBand distance,
    const std::string& careerStage,
    std::optional<int> energyPercent)
{
    SmartTrainingDecision decision;
    for (const auto& candidate : candidates)
    {
        if (!TrainingTypeCatalog::IsSupported(candidate.trainingType))
            continue;
        decision.allEvaluations.emplace_back(
            candidate, Score(candidate, distance, careerStage, energyPercent));
    }

    std::vector<std::pair<TrainingCandidate, TrainingScoreBreakdown>> safe;
    for (const auto& evaluated : decision.allEvaluations)
    {
        if (!evaluated.second.IsExcluded())
            safe.push_back(evaluated);
    }
    std::stable_sort(safe.begin(), safe.end(),
        [](const auto& left, const auto& right) {
            if (std::abs(left.second.totalScore - right.second.totalScore) > 1e-4)
                return left.second.totalScore > right.second.totalScore;
            const int leftFailure = left.first.failureRatePercent.value_or(100);
            const int rightFailure = right.first.failureRatePercent.value_or(100);
            if (leftFailure != rightFailure)
                return leftFailure < rightFailure;
            return TieBreakRank(left.first.trainingType) < TieBreakRank(right.first.trainingType);
        });

    if (safe.empty())
    {
        decision.decisionReason = "No training candidate had reliable positive gains and failure rate at or below 5%.";
        return decision;
    }

    decision.chosenCandidate = safe.front().first;
    decision.score = safe.front().second;
    decision.decisionReason = "Selected the highest-scoring safe training candidate.";
    return decision;
}

} // namespace UmaAssistant::CareerDomain::Ura

namespace UmaAssistant::CareerDomain
{

namespace
{

std::string NormalizeStrategyId(const std::string& value)
{
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos)
        return {};
    const auto last = value.find_last_not_of(" \t\r\n");
    std::string normalized = value.substr(first, last - first + 1);
    std::transform(normalized.begin(), normalized.end(), normalized.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return normalized;
}

std::optional<int> StrategyMonthFromText(const std::string& text)
{
    std::string lower = text;
    std::transform(lower.begin(), lower.end(), lower.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    if (lower.find("month 7") != std::string::npos || lower.find("july") != std::string::npos ||
        lower.find("jul ") != std::string::npos || lower.find("7月") != std::string::npos)
        return 7;
    if (lower.find("month 8") != std::string::npos || lower.find("august") != std::string::npos ||
        lower.find("aug ") != std::string::npos || lower.find("8月") != std::string::npos)
        return 8;
    if (lower.find("month 6") != std::string::npos || lower.find("june") != std::string::npos ||
        lower.find("jun ") != std::string::npos || lower.find("6月") != std::string::npos)
        return 6;
    return std::nullopt;
}

bool IsStrategyLateJune(const CareerState& state)
{
    if (!state.turnPositionText.has_value())
        return false;
    std::string text = *state.turnPositionText;
    std::transform(text.begin(), text.end(), text.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    const bool eligibleYear = text.find("classic") != std::string::npos ||
                              text.find("senior") != std::string::npos;
    return eligibleYear && StrategyMonthFromText(text) == 6 && text.find("late") != std::string::npos;
}

ActionIntent MakeUnknownEvidenceRest(const std::string& reason)
{
    return ActionIntent::MakeRest(reason, 0.5, ObservationSource::DefaultFallback);
}

} // namespace

UraSmartTrainingStrategy::UraSmartTrainingStrategy(Ura::RaceDistanceBand distance, int restThreshold)
    : m_distanceBand(distance)
    , m_restThreshold(std::clamp(restThreshold, 0, 100))
{
}

void UraSmartTrainingStrategy::SetLatestCandidates(std::vector<Ura::TrainingCandidate> candidates)
{
    m_latestCandidates = std::move(candidates);
}

bool UraSmartTrainingStrategy::ShouldScanTrainingCandidates(
    const CareerState& state,
    const IScenarioModule* scenario) const
{
    if (!m_latestCandidates.empty() || state.infirmaryAvailable)
        return false;

    if (scenario != nullptr && scenario->GetTrainingOverride(state, m_restThreshold, true).has_value())
        return false;

    if (!state.energy.IsReliable(0.70))
        return false;

    const int energy = state.energy.Value();
    if (energy < m_restThreshold || (IsStrategyLateJune(state) && energy < 80))
        return false;

    return true;
}

void UraSmartTrainingStrategy::SetTrainingCandidateEvidence(
    const std::vector<TrainingCandidateEvidence>& candidates)
{
    std::vector<Ura::TrainingCandidate> converted;
    converted.reserve(candidates.size());
    for (const auto& evidence : candidates)
    {
        Ura::TrainingCandidate candidate;
        candidate.trainingType = evidence.trainingType;
        candidate.speedGain = evidence.statGains[0].value;
        candidate.staminaGain = evidence.statGains[1].value;
        candidate.powerGain = evidence.statGains[2].value;
        candidate.gutsGain = evidence.statGains[3].value;
        candidate.witGain = evidence.statGains[4].value;
        candidate.skillPointGain = evidence.skillPointGain.value;
        candidate.failureRatePercent = evidence.failureRatePercent.value;
        candidate.trainingLevel = evidence.trainingLevel.value;
        candidate.unbondedSupportCount = evidence.unbondedSupportCount.value;
        candidate.hintCount = evidence.hintCount.value;

        const std::array<const ObservedValue<int>*, 6> coreValues{
            &evidence.statGains[0], &evidence.statGains[1], &evidence.statGains[2],
            &evidence.statGains[3], &evidence.statGains[4], &evidence.skillPointGain};
        bool allCorePresent = true;
        bool hasCoreSource = false;
        candidate.coreConfidence = 1.0;
        candidate.coreSource = ObservationSource::Unknown;
        for (const auto* value : coreValues)
        {
            if (!value->HasValue() || !value->HasValidConfidence())
            {
                allCorePresent = false;
                continue;
            }
            candidate.coreConfidence = std::min(candidate.coreConfidence, value->confidence);
            if (!hasCoreSource)
            {
                candidate.coreSource = value->source;
                hasCoreSource = true;
            }
            else if (candidate.coreSource != value->source)
            {
                candidate.coreSource = ObservationSource::Derived;
            }
        }
        if (!allCorePresent || !hasCoreSource)
            candidate.coreConfidence = 0.0;

        candidate.failureRateConfidence = evidence.failureRatePercent.HasValue() &&
            evidence.failureRatePercent.HasValidConfidence()
            ? evidence.failureRatePercent.confidence : 0.0;
        candidate.failureRateSource = evidence.failureRatePercent.HasValue()
            ? evidence.failureRatePercent.source : ObservationSource::Unknown;

        const std::array<const ObservedValue<int>*, 3> auxiliaryValues{
            &evidence.trainingLevel, &evidence.unbondedSupportCount, &evidence.hintCount};
        candidate.auxiliaryConfidence = 1.0;
        candidate.auxiliarySource = ObservationSource::Unknown;
        bool hasAuxiliarySource = false;
        for (const auto* value : auxiliaryValues)
        {
            if (!value->HasValue() || !value->HasValidConfidence())
                continue;
            candidate.auxiliaryConfidence = std::min(candidate.auxiliaryConfidence, value->confidence);
            if (!hasAuxiliarySource)
            {
                candidate.auxiliarySource = value->source;
                hasAuxiliarySource = true;
            }
            else if (candidate.auxiliarySource != value->source)
            {
                candidate.auxiliarySource = ObservationSource::Derived;
            }
        }
        if (!hasAuxiliarySource)
            candidate.auxiliaryConfidence = 0.0;
        converted.push_back(std::move(candidate));
    }
    SetLatestCandidates(std::move(converted));
}

void UraSmartTrainingStrategy::ClearLatestCandidates() noexcept
{
    m_latestCandidates.clear();
    m_lastDecision = {};
}

ActionIntent UraSmartTrainingStrategy::Choose(
    const CareerState& state,
    const IScenarioModule* scenario)
{
    std::optional<ActionIntent> scenarioOverride;
    if (scenario != nullptr)
    {
        scenarioOverride = scenario->GetTrainingOverride(state, m_restThreshold, true);
        if (scenarioOverride.has_value() && scenarioOverride->kind == ActionIntentKind::DoNothing)
        {
            m_lastDecision = {};
            return *scenarioOverride;
        }
    }

    if (state.infirmaryAvailable)
    {
        const bool reliable = state.infirmaryEvidence.IsReliable();
        return ActionIntent::MakeInfirmary(
            "Infirmary is available to cure bad status",
            reliable ? state.infirmaryEvidence.confidence : 1.0,
            reliable ? state.infirmaryEvidence.source : ObservationSource::Derived);
    }

    if (scenarioOverride.has_value())
    {
        m_lastDecision = {};
        return *scenarioOverride;
    }

    if (state.energy.IsReliable() && state.energy.Value() < m_restThreshold)
    {
        m_lastDecision = {};
        return ActionIntent::MakeRest(
            "Observed energy is below the smart-training safety threshold.",
            state.energy.confidence,
            state.energy.source);
    }

    if (!state.energy.IsReliable(0.70))
    {
        m_lastDecision = {};
        return MakeUnknownEvidenceRest(
            "Energy was not read with sufficient confidence; using the cautious Rest fallback.");
    }

    if (IsStrategyLateJune(state) && state.energy.Value() < 80)
    {
        m_lastDecision = {};
        return ActionIntent::MakeRest(
            "Classic/Senior late June energy is below 80%; prepare for Summer Camp.",
            state.energy.confidence,
            state.energy.source);
    }

    if (m_latestCandidates.empty())
    {
        m_lastDecision = {};
        return MakeUnknownEvidenceRest(
            "Smart training has no candidate observations; using the configured Rest fallback.");
    }

    const auto energy = std::optional<int>{state.energy.Value()};
    const auto stage = state.turnPositionText.value_or(state.phaseId);
    auto distance = m_distanceBand;
    if (scenario != nullptr)
    {
        if (const auto raceDistance = scenario->GetCurrentTrainingRaceDistance(state))
        {
            // An available race record with incomplete distance metadata is
            // still authoritative: the managed resolver would choose that
            // record and score it as Unknown rather than use a different race.
            distance = Ura::UraSmartTrainingScorer::ParseDistanceBand(
                raceDistance->distanceBand, raceDistance->meters);
        }
    }
    m_lastDecision = Ura::UraSmartTrainingScorer::Choose(
        m_latestCandidates, distance, stage, energy);
    if (!m_lastDecision.IsSafe())
    {
        return ActionIntent::MakeRest(
            "No safe smart-training choice: " + m_lastDecision.decisionReason,
            0.75,
            ObservationSource::DefaultFallback);
    }

    const auto& candidate = *m_lastDecision.chosenCandidate;
    std::string normalizedTraining;
    (void)TrainingTypeCatalog::TryNormalize(candidate.trainingType, normalizedTraining);
    const double confidence = std::min(candidate.coreConfidence, candidate.failureRateConfidence);
    const auto source = candidate.coreSource == candidate.failureRateSource
        ? candidate.coreSource
        : ObservationSource::Derived;
    return ActionIntent::MakeTraining(
        normalizedTraining,
        m_lastDecision.decisionReason,
        confidence,
        source);
}

void UraSmartTrainingStrategy::ConfirmTraining(CareerState&, const std::string&)
{
    ClearLatestCandidates();
}

} // namespace UmaAssistant::CareerDomain

namespace UmaAssistant::CareerDomain
{

namespace
{

struct UraRegistryStorage
{
    std::mutex mutex;
    bool builtInsAdded{false};
    std::map<std::string, UraTrainingStrategyOption> options;
    std::map<std::string, UraStrategyRegistry::Factory> factories;
    std::vector<std::string> order;
};

UraRegistryStorage& UraStorage()
{
    static UraRegistryStorage storage;
    return storage;
}

void EnsureUraStrategies()
{
    auto& storage = UraStorage();
    std::lock_guard<std::mutex> lock(storage.mutex);
    if (storage.builtInsAdded)
        return;

    const auto add = [&storage](const char* id, const char* label, UraStrategyRegistry::Factory factory) {
        const std::string key(id);
        storage.options.emplace(key, UraTrainingStrategyOption{key, label});
        storage.factories.emplace(key, std::move(factory));
        storage.order.push_back(key);
    };

    add("smart-balanced-safe", "Smart balanced · safe",
        [](TrainingRatio, int restThreshold, int) {
            return std::make_shared<UraSmartTrainingStrategy>(Ura::RaceDistanceBand::Unknown, restThreshold);
        });
    add("default-speed-medium", "Speed focus",
        [](TrainingRatio, int restThreshold, int) { return std::make_shared<DefaultTrainingStrategy>("speed", restThreshold); });
    add("default-stamina-medium", "Stamina focus",
        [](TrainingRatio, int restThreshold, int) { return std::make_shared<DefaultTrainingStrategy>("stamina", restThreshold); });
    add("default-power-medium", "Power focus",
        [](TrainingRatio, int restThreshold, int) { return std::make_shared<DefaultTrainingStrategy>("power", restThreshold); });
    add("default-guts-medium", "Guts focus",
        [](TrainingRatio, int restThreshold, int) { return std::make_shared<DefaultTrainingStrategy>("guts", restThreshold); });
    add("default-wit-medium", "Wit focus",
        [](TrainingRatio, int restThreshold, int) { return std::make_shared<DefaultTrainingStrategy>("wit", restThreshold); });
    add("custom-ratio", "Custom training ratio",
        [](TrainingRatio ratio, int restThreshold, int) { return std::make_shared<RatioTrainingStrategy>(ratio, restThreshold); });
    storage.builtInsAdded = true;
}

} // namespace

void UraStrategyRegistry::Register(std::string strategyId, std::string label, Factory factory)
{
    EnsureUraStrategies();
    const auto key = NormalizeStrategyId(strategyId);
    if (key.empty() || !factory)
        throw std::invalid_argument("A strategy ID and factory are required");

    auto& storage = UraStorage();
    std::lock_guard<std::mutex> lock(storage.mutex);
    auto option = storage.options.find(key);
    if (option == storage.options.end())
    {
        storage.options.emplace(key, UraTrainingStrategyOption{key, std::move(label)});
        storage.order.push_back(key);
    }
    else
    {
        option->second.label = std::move(label);
    }
    storage.factories[key] = std::move(factory);
}

bool UraStrategyRegistry::IsRegistered(const std::string& strategyId)
{
    EnsureUraStrategies();
    auto key = NormalizeStrategyId(strategyId);
    if (key == "smart" || key == "ura-smart")
        key = "smart-balanced-safe";
    auto& storage = UraStorage();
    std::lock_guard<std::mutex> lock(storage.mutex);
    return storage.factories.find(key) != storage.factories.end();
}

std::vector<UraTrainingStrategyOption> UraStrategyRegistry::AvailableStrategies()
{
    EnsureUraStrategies();
    auto& storage = UraStorage();
    std::lock_guard<std::mutex> lock(storage.mutex);
    std::vector<UraTrainingStrategyOption> result;
    result.reserve(storage.order.size());
    for (const auto& key : storage.order)
        result.push_back(storage.options.at(key));
    return result;
}

std::shared_ptr<ICareerStrategy> UraStrategyRegistry::Create(
    const std::string& strategyId,
    TrainingRatio ratio,
    int restThreshold,
    int witEnergyThreshold)
{
    EnsureUraStrategies();
    auto key = NormalizeStrategyId(strategyId);
    if (key == "smart" || key == "ura-smart")
        key = "smart-balanced-safe";

    Factory factory;
    {
        auto& storage = UraStorage();
        std::lock_guard<std::mutex> lock(storage.mutex);
        const auto found = storage.factories.find(key);
        if (found == storage.factories.end())
            throw std::invalid_argument("URA strategy is not registered: " + strategyId);
        factory = found->second;
    }
    return factory(ratio, std::clamp(restThreshold, 0, 100), std::clamp(witEnergyThreshold, 0, 100));
}

} // namespace UmaAssistant::CareerDomain
