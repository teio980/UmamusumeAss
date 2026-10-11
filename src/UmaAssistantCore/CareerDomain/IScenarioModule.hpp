#pragma once

#include "CareerState.hpp"
#include "CareerObservation.hpp"
#include <algorithm>
#include <cctype>
#include <functional>
#include <mutex>
#include <string>
#include <memory>
#include <optional>
#include <stdexcept>
#include <unordered_map>
#include <utility>
#include <vector>

namespace UmaAssistant::CareerDomain
{

struct CareerRaceScheduleEntry
{
    int turnIndex{0};
    std::string grade;
    bool topCardAvailable{false};
};

// Data-only race distance used by training policies. It deliberately carries
// no race/catalog or scenario-specific type so scanners and strategies can ask
// the same scenario for the same scoring context.
struct TrainingRaceDistance
{
    std::string distanceBand;
    std::optional<int> meters;
};

struct CareerTrainingRaceDistanceEntry
{
    int turnIndex{0};
    int objectiveOrder{0};
    int raceOrder{0};
    TrainingRaceDistance distance;
    // Optional ID from the global trainee-race catalog. It lets a scenario
    // prefer an explicitly current global race before resolving the next
    // trainee objective, without importing GameData types into the domain.
    std::string raceId;

    CareerTrainingRaceDistanceEntry() = default;
    CareerTrainingRaceDistanceEntry(
        int turn, int objective, int race, TrainingRaceDistance value, std::string id = {})
        : turnIndex(turn), objectiveOrder(objective), raceOrder(race),
          distance(std::move(value)), raceId(std::move(id))
    {
    }
};

// Scenario-neutral snapshots for a scenario-owned race objective chain. IDs
// and kind strings are supplied by the active scenario package; generic
// Career state and runtime code do not interpret scenario-specific types.
struct ScenarioObjectiveDefinition
{
    std::string objectiveId;
    int order{0};
    std::string kind;
    std::optional<std::string> raceId;
    std::vector<std::string> observedRaceIds;
    std::optional<std::string> nextObjectiveId;
    std::optional<int> targetPlacement;
    std::optional<int> targetPlacementAtMost;
    std::optional<int> targetMinimum;
    std::optional<int> targetCount;
    std::optional<std::string> targetRaceGrade;
};

struct ScenarioRaceDefinition
{
    std::string raceId;
    std::string grade;
    std::string distanceBand;
    std::optional<int> distanceMeters;
    std::optional<int> rewardFans;
    // Semantic ID in the shared visual catalog, not a file path.
    std::string runtimeFrameCatalogId;
    std::optional<int> expectedPlacement;
    double resultConfidence{0.95};
    bool retryEnabled{false};
    int maxRetryCount{0};
};

struct CareerRaceResultExpectation
{
    std::string raceId;
    std::string runtimeFrameCatalogId;
    int expectedPlacement{0};
    double confidence{0.0};
};

struct CareerRaceResultObservation
{
    std::string raceId;
    int placement{0};
    double confidence{0.0};
};

struct CareerRaceResultApplication
{
    bool applied{false};
    bool retryPending{false};
    std::string failureReason;
};

struct ScenarioInitialization
{
    std::vector<CareerRaceScheduleEntry> raceSchedule;
    std::vector<CareerTrainingRaceDistanceEntry> trainingRaceDistances;
    // Resource-derived fallback for a scenario's own current race (for
    // example the URA Finale stage) after trainee objectives are exhausted.
    std::optional<TrainingRaceDistance> scenarioTrainingRaceDistanceFallback;
    std::vector<ScenarioObjectiveDefinition> scenarioObjectives;
    std::vector<ScenarioRaceDefinition> scenarioRaces;
    std::vector<std::string> finalSeriesStages;
};

enum class RaceStreakWarningResolution
{
    ConfirmRace,
    CancelRace
};

class IScenarioModule
{
public:
    virtual ~IScenarioModule() = default;

    [[nodiscard]] virtual std::string GetScenarioId() const = 0;
    [[nodiscard]] virtual std::string GetDisplayName() const = 0;

    virtual void Initialize(const ScenarioInitialization& initialization)
    {
        (void)initialization;
    }

    virtual void OnRunStart(CareerState& state, bool continueExistingCareer = false)
    {
        (void)state;
        (void)continueExistingCareer;
    }

    virtual void OnTurnStart(CareerState& state) = 0;
    virtual void OnObserve(CareerState& state, const CareerObservation& observation) = 0;
    // Called only after the executor verifies the action's screen transition.
    // Scenario modules can distinguish an actual turn action from opening a
    // picker or intermediate confirmation page using the source observation.
    virtual void OnTurnActionSubmitted(
        CareerState& state,
        const ActionIntent& intent,
        const CareerObservation& source)
    {
        (void)state;
        (void)intent;
        (void)source;
    }
    [[nodiscard]] virtual std::optional<RaceStreakWarningResolution>
    GetRaceStreakWarningResolution(const CareerState& state) const
    {
        (void)state;
        return std::nullopt;
    }
    [[nodiscard]] virtual std::optional<TrainingRaceDistance>
    GetCurrentTrainingRaceDistance(const CareerState& state) const
    {
        (void)state;
        return std::nullopt;
    }
    [[nodiscard]] virtual std::optional<CareerRaceResultExpectation>
    GetCurrentRaceResultExpectation(const CareerState& state) const
    {
        (void)state;
        return std::nullopt;
    }
    // Only called after runtime matching of the expectation's data-backed
    // result frame. A missing expectation or unsupported application is
    // intentionally fail-closed in the caller.
    virtual CareerRaceResultApplication OnRaceResultConfirmed(
        CareerState& state,
        const CareerRaceResultObservation& result)
    {
        (void)state;
        (void)result;
        return {false, false, "Scenario does not support verified race results."};
    }
    [[nodiscard]] virtual bool IsObjectiveCompleted(const CareerState& state) const = 0;

    // Scenario modules can apply their own turn priorities without coupling
    // the shared strategy implementations to a particular scenario.
    [[nodiscard]] virtual std::optional<ActionIntent> GetTrainingOverride(
        const CareerState& state,
        int restThreshold,
        bool smartStrategy) const
    {
        (void)state;
        (void)restThreshold;
        (void)smartStrategy;
        return std::nullopt;
    }
};

class ScenarioRegistry
{
public:
    using Factory = std::function<std::shared_ptr<IScenarioModule>()>;

    static ScenarioRegistry& Instance()
    {
        static ScenarioRegistry s_instance;
        return s_instance;
    }

    void RegisterModule(std::shared_ptr<IScenarioModule> module)
    {
        if (!module)
            return;

        auto id = Normalize(module->GetScenarioId());
        if (id.empty())
            throw std::invalid_argument("Scenario module ID must not be empty");
        std::lock_guard<std::mutex> lock(m_mutex);
        m_factories.erase(id);
        m_modules[std::move(id)] = std::move(module);
    }

    void RegisterFactory(const std::string& scenarioId, Factory factory)
    {
        if (!factory)
            throw std::invalid_argument("Scenario module factory must not be empty");
        auto id = Normalize(scenarioId);
        if (id.empty())
            throw std::invalid_argument("Scenario module ID must not be empty");
        std::lock_guard<std::mutex> lock(m_mutex);
        m_modules.erase(id);
        m_factories[std::move(id)] = std::move(factory);
    }

    [[nodiscard]] std::shared_ptr<IScenarioModule> GetModule(const std::string& scenarioId) const
    {
        const auto id = Normalize(scenarioId);
        Factory factory;
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            auto it = m_modules.find(id);
            if (it != m_modules.end())
                return it->second;
            auto factoryIt = m_factories.find(id);
            if (factoryIt != m_factories.end())
                factory = factoryIt->second;
        }
        return factory ? factory() : nullptr;
    }

    [[nodiscard]] std::shared_ptr<IScenarioModule> CreateModule(const std::string& scenarioId) const
    {
        const auto id = Normalize(scenarioId);
        Factory factory;
        std::shared_ptr<IScenarioModule> module;
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            auto factoryIt = m_factories.find(id);
            if (factoryIt != m_factories.end())
                factory = factoryIt->second;
            else if (auto moduleIt = m_modules.find(id); moduleIt != m_modules.end())
                module = moduleIt->second;
        }
        return factory ? factory() : module;
    }

    [[nodiscard]] bool HasModule(const std::string& scenarioId) const
    {
        return static_cast<bool>(GetModule(scenarioId));
    }

    [[nodiscard]] std::vector<std::string> RegisteredScenarioIds() const
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        std::vector<std::string> result;
        result.reserve(m_modules.size() + m_factories.size());
        for (const auto& entry : m_modules)
            result.push_back(entry.first);
        for (const auto& entry : m_factories)
            if (m_modules.find(entry.first) == m_modules.end())
                result.push_back(entry.first);
        std::sort(result.begin(), result.end());
        return result;
    }

private:
    ScenarioRegistry() = default;

    static std::string Normalize(const std::string& value)
    {
        auto first = value.find_first_not_of(" \t\r\n");
        if (first == std::string::npos)
            return {};
        auto last = value.find_last_not_of(" \t\r\n");
        std::string result = value.substr(first, last - first + 1);
        std::transform(result.begin(), result.end(), result.begin(),
            [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        return result;
    }

    mutable std::mutex m_mutex;
    std::unordered_map<std::string, std::shared_ptr<IScenarioModule>> m_modules;
    std::unordered_map<std::string, Factory> m_factories;
};

} // namespace UmaAssistant::CareerDomain
