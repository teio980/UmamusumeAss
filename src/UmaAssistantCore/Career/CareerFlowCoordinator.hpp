#pragma once

#include "ICareerObserver.hpp"
#include "ICareerActionExecutor.hpp"
#include "ICareerTrainingFlow.hpp"
#include "CareerRuntimeConfig.hpp"
#include "UmaAssistantCore/CareerDomain/CareerState.hpp"
#include "UmaAssistantCore/CareerDomain/IScenarioModule.hpp"
#include "UmaAssistantCore/CareerDomain/ICareerStrategy.hpp"
#include <memory>
#include <optional>

namespace UmaAssistant::Career
{

enum class CareerRunStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
};

struct CareerRunResult
{
    CareerRunStatus status{CareerRunStatus::Completed};
    int totalTurnsExecuted{0};
    std::string failureReason;
};

class CareerFlowCoordinator
{
public:
    CareerFlowCoordinator(
        std::shared_ptr<ICareerObserver> observer,
        std::shared_ptr<ICareerActionExecutor> actionExecutor,
        std::shared_ptr<CareerDomain::IScenarioModule> scenario,
        std::shared_ptr<CareerDomain::ICareerStrategy> strategy,
        CareerRuntimeConfig config = {},
        std::shared_ptr<ICareerTrainingFlow> trainingFlow = {});

    CareerRunResult Step(TaskEngine::ITaskExecutionContext& context);

    // Preserve the confirmed startup/resume frame across the runtime handoff.
    // Domain observation is applied by Step before choosing the first action.
    void SetConfirmedObservation(const CareerDomain::CareerObservation& observation);

    // Resets only transient visual/run state after the managed date-change
    // recovery flow. Confirmed career state and strategy history are retained.
    void ResetAfterRecovery() noexcept;

    CareerRunResult RunToCompletion(
        TaskEngine::ITaskExecutionContext& context,
        int maxSteps = 1200);

    [[nodiscard]] const CareerDomain::CareerState& GetState() const noexcept { return m_state; }

private:
    std::shared_ptr<ICareerObserver> m_observer;
    std::shared_ptr<ICareerActionExecutor> m_actionExecutor;
    std::shared_ptr<CareerDomain::IScenarioModule> m_scenario;
    std::shared_ptr<CareerDomain::ICareerStrategy> m_strategy;
    std::shared_ptr<ICareerTrainingFlow> m_trainingFlow;
    CareerRuntimeConfig m_config;
    CareerDomain::CareerState m_state;
    std::optional<CareerDomain::CareerObservation> m_nextConfirmedObservation;
    std::optional<CareerDomain::ActionIntent> m_pendingTraining;
    std::optional<int> m_pendingTrainingBaseline;
    // Keep the strategy's target while the same race traverses its entry,
    // warning, runner, playback, and result screens. Intermediate OCR from
    // those screens must not reclassify the objective.
    std::optional<CareerDomain::ActionIntent> m_pendingRaceIntent;
    bool m_terminalScreenSeen{false};
    int m_unknownObservations{0};
};

} // namespace UmaAssistant::Career
