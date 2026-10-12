#include "TrainingStrategies.hpp"

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <stdexcept>
#include <utility>

namespace UmaAssistant::CareerDomain
{

bool TrainingTypeCatalog::TryNormalize(const std::string& value, std::string& normalized)
{
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos)
    {
        normalized.clear();
        return false;
    }

    const auto last = value.find_last_not_of(" \t\r\n");
    normalized = value.substr(first, last - first + 1);
    std::transform(normalized.begin(), normalized.end(), normalized.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return normalized == "speed" || normalized == "stamina" || normalized == "power" ||
           normalized == "guts" || normalized == "wit";
}

bool TrainingTypeCatalog::IsSupported(const std::string& value)
{
    std::string normalized;
    return TryNormalize(value, normalized);
}

namespace
{

int ClampPercent(int value) noexcept
{
    return std::clamp(value, 0, 100);
}

ActionIntent InfirmaryIntent(const CareerState& state)
{
    const bool reliable = state.infirmaryEvidence.IsReliable();
    return ActionIntent::MakeInfirmary(
        "Infirmary is available to cure status",
        reliable ? state.infirmaryEvidence.confidence : 1.0,
        reliable ? state.infirmaryEvidence.source : ObservationSource::Derived);
}

std::optional<ActionIntent> ScenarioOverride(
    const CareerState& state,
    const IScenarioModule* scenario,
    int restThreshold,
    bool smartStrategy)
{
    return scenario == nullptr
        ? std::nullopt
        : scenario->GetTrainingOverride(state, restThreshold, smartStrategy);
}

bool IsRacePriority(const std::optional<ActionIntent>& intent) noexcept
{
    return intent.has_value() && (intent->kind == ActionIntentKind::Race || intent->kind == ActionIntentKind::DoNothing);
}

ActionIntent LowEnergyRest(const CareerState& state, int threshold)
{
    const auto energy = state.energy.Value();
    return ActionIntent::MakeRest(
        "Energy (" + std::to_string(energy) +
            "%) is below threshold (" + std::to_string(threshold) + "%)",
        state.energy.confidence,
        state.energy.source);
}

} // namespace

int TrainingRatio::Gcd(int a, int b) noexcept
{
    while (b != 0)
    {
        int t = b;
        b = a % b;
        a = t;
    }
    return std::abs(a);
}

bool TrainingRatio::IsValid() const noexcept
{
    return speed >= 0 && speed <= 100 &&
           stamina >= 0 && stamina <= 100 &&
           power >= 0 && power <= 100 &&
           guts >= 0 && guts <= 100 &&
           wit >= 0 && wit <= 100 &&
           (speed + stamina + power + guts + wit) > 0;
}

TrainingRatio TrainingRatio::Reduce() const
{
    if (!IsValid())
    {
        throw std::invalid_argument("TrainingRatio must have non-negative values and sum > 0");
    }

    int d = Gcd(Gcd(Gcd(Gcd(speed, stamina), power), guts), wit);
    if (d <= 1)
        return *this;

    return TrainingRatio{speed / d, stamina / d, power / d, guts / d, wit / d};
}

std::vector<std::string> TrainingRatio::BuildCycle() const
{
    auto r = Reduce();
    std::vector<std::string> cycle;
    cycle.reserve(r.speed + r.stamina + r.power + r.guts + r.wit);

    for (int i = 0; i < r.speed; ++i) cycle.push_back("speed");
    for (int i = 0; i < r.stamina; ++i) cycle.push_back("stamina");
    for (int i = 0; i < r.power; ++i) cycle.push_back("power");
    for (int i = 0; i < r.guts; ++i) cycle.push_back("guts");
    for (int i = 0; i < r.wit; ++i) cycle.push_back("wit");

    return cycle;
}

DefaultTrainingStrategy::DefaultTrainingStrategy(std::string trainingType, int restThreshold)
    : m_restThreshold(ClampPercent(restThreshold))
{
    if (!TrainingTypeCatalog::TryNormalize(trainingType, m_trainingType))
        throw std::invalid_argument("Unsupported training type: " + trainingType);
}

ActionIntent DefaultTrainingStrategy::Choose(
    const CareerState& state,
    const IScenarioModule* scenario)
{
    auto overrideIntent = ScenarioOverride(state, scenario, m_restThreshold, false);
    if (IsRacePriority(overrideIntent))
        return *overrideIntent;

    if (state.infirmaryAvailable)
        return InfirmaryIntent(state);

    if (overrideIntent.has_value())
        return *overrideIntent;

    if (state.energy.IsReliable() && state.energy.Value() < m_restThreshold)
        return LowEnergyRest(state, m_restThreshold);

    return ActionIntent::MakeTraining(m_trainingType, "Default single training strategy");
}

RatioTrainingStrategy::RatioTrainingStrategy(TrainingRatio ratio, int restThreshold)
    : m_cycle(ratio.BuildCycle())
    , m_currentIndex(0)
    , m_restThreshold(ClampPercent(restThreshold))
{
}

ActionIntent RatioTrainingStrategy::Choose(
    const CareerState& state,
    const IScenarioModule* scenario)
{
    m_pendingTrainingType.reset();
    auto overrideIntent = ScenarioOverride(state, scenario, m_restThreshold, false);
    if (IsRacePriority(overrideIntent))
        return *overrideIntent;

    if (state.infirmaryAvailable)
        return InfirmaryIntent(state);

    if (overrideIntent.has_value())
        return *overrideIntent;

    if (state.energy.IsReliable() && state.energy.Value() < m_restThreshold)
        return LowEnergyRest(state, m_restThreshold);

    if (m_cycle.empty())
        return ActionIntent::MakeTraining("speed", "Fallback training");

    const std::string& nextTraining = m_cycle[m_currentIndex];
    m_pendingTrainingType = nextTraining;
    return ActionIntent::MakeTraining(nextTraining,
        "Ratio cycle step #" + std::to_string(m_currentIndex));
}

void RatioTrainingStrategy::ConfirmTraining(CareerState&, const std::string& trainingType)
{
    std::string normalized;
    if (m_pendingTrainingType.has_value() &&
        TrainingTypeCatalog::TryNormalize(trainingType, normalized) &&
        normalized == *m_pendingTrainingType && normalized == m_cycle[m_currentIndex])
    {
        ConfirmCurrentStep();
    }
}

void RatioTrainingStrategy::ConfirmCurrentStep()
{
    if (!m_cycle.empty())
        m_currentIndex = (m_currentIndex + 1) % m_cycle.size();
    m_pendingTrainingType.reset();
}

WitRatioTrainingStrategy::WitRatioTrainingStrategy(
    TrainingRatio ratio,
    int restThreshold,
    int witEnergyThreshold)
    : m_innerRatio(ratio, restThreshold)
    , m_restThreshold(ClampPercent(restThreshold))
    , m_witEnergyThreshold(ClampPercent(witEnergyThreshold))
{
}

ActionIntent WitRatioTrainingStrategy::Choose(
    const CareerState& state,
    const IScenarioModule* scenario)
{
    if (state.energy.IsReliable() &&
        state.energy.Value() >= m_witEnergyThreshold &&
        state.energy.Value() < m_restThreshold)
    {
        auto testIntent = m_innerRatio.Choose(state, scenario);
        if (testIntent.kind == ActionIntentKind::Rest)
        {
            m_witOverridePending = true;
            return ActionIntent::MakeTraining(
                "wit", "Wit training recovers energy at moderate threshold",
                state.energy.confidence, state.energy.source);
        }
    }

    m_witOverridePending = false;
    return m_innerRatio.Choose(state, scenario);
}

void WitRatioTrainingStrategy::ConfirmTraining(
    CareerState& state,
    const std::string& trainingType)
{
    std::string normalized;
    if (m_witOverridePending && TrainingTypeCatalog::TryNormalize(trainingType, normalized) &&
        normalized == "wit")
    {
        m_innerRatio.ConfirmCurrentStep();
        m_witOverridePending = false;
        return;
    }
    m_witOverridePending = false;
    m_innerRatio.ConfirmTraining(state, trainingType);
}

} // namespace UmaAssistant::CareerDomain
