#pragma once

#include "UmaAssistantCore/CareerDomain/ActionIntent.hpp"

#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace UmaAssistant::CareerDomain::Ura
{

enum class RaceDistanceBand
{
    Unknown,
    Sprint,
    Mile,
    Middle,
    Extended
};

// Pure data from a single training card. Missing values remain missing so
// the scorer cannot mistake an unreadable number for zero gains or no risk.
struct TrainingCandidate
{
    std::string trainingType;
    std::optional<int> speedGain;
    std::optional<int> staminaGain;
    std::optional<int> powerGain;
    std::optional<int> gutsGain;
    std::optional<int> witGain;
    std::optional<int> skillPointGain;
    std::optional<int> failureRatePercent;
    std::optional<int> trainingLevel;
    std::optional<int> unbondedSupportCount;
    std::optional<int> hintCount;
    double coreConfidence{1.0};
    double failureRateConfidence{1.0};
    double auxiliaryConfidence{1.0};
    ObservationSource coreSource{ObservationSource::Ocr};
    ObservationSource failureRateSource{ObservationSource::Ocr};
    ObservationSource auxiliarySource{ObservationSource::Ocr};

    [[nodiscard]] bool HasReliableCoreValues() const noexcept;
    [[nodiscard]] bool HasReliableFailureRate() const noexcept;
};

struct TrainingScoreBreakdown
{
    RaceDistanceBand distanceBand{RaceDistanceBand::Unknown};
    double speedContribution{0.0};
    double staminaContribution{0.0};
    double powerContribution{0.0};
    double gutsContribution{0.0};
    double witContribution{0.0};
    double skillPointContribution{0.0};
    double successProbability{1.0};
    double baseScore{0.0};
    double bondBonus{0.0};
    double hintBonus{0.0};
    double lowEnergyWitBonus{0.0};
    double totalScore{0.0};
    std::string exclusionReason;

    [[nodiscard]] bool IsExcluded() const noexcept { return !exclusionReason.empty(); }
};

struct SmartTrainingDecision
{
    std::optional<TrainingCandidate> chosenCandidate;
    std::optional<TrainingScoreBreakdown> score;
    std::vector<std::pair<TrainingCandidate, TrainingScoreBreakdown>> allEvaluations;
    std::string decisionReason;

    [[nodiscard]] bool IsSafe() const noexcept
    {
        return chosenCandidate.has_value() && score.has_value() && !score->IsExcluded();
    }
};

class UraSmartTrainingScorer
{
public:
    static constexpr int MaximumFailureRatePercent = 5;
    static constexpr double MinimumCoreConfidence = 0.80;
    static constexpr double MinimumFailureRateConfidence = 0.80;
    static constexpr double MinimumAuxiliaryConfidence = 0.80;

    [[nodiscard]] static RaceDistanceBand ParseDistanceBand(
        const std::string& bandText,
        std::optional<int> meters = std::nullopt);

    [[nodiscard]] static TrainingScoreBreakdown Score(
        const TrainingCandidate& candidate,
        RaceDistanceBand distance,
        const std::string& careerStage,
        std::optional<int> energyPercent);

    [[nodiscard]] static SmartTrainingDecision Choose(
        const std::vector<TrainingCandidate>& candidates,
        RaceDistanceBand distance,
        const std::string& careerStage,
        std::optional<int> energyPercent);
};

} // namespace UmaAssistant::CareerDomain::Ura
