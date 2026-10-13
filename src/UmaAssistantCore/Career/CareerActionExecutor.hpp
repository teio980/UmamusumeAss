#pragma once

#include "ICareerActionExecutor.hpp"
#include "CareerObservationReader.hpp"
#include "CareerRuntimeConfig.hpp"
#include <memory>
#include <optional>
#include <chrono>

namespace UmaAssistant::Career
{

class CareerSkillLearningFlow;

class CareerActionExecutor : public ICareerActionExecutor
{
public:
    CareerActionExecutor();
    explicit CareerActionExecutor(std::shared_ptr<CareerVisualResources> resources);
    CareerActionExecutor(
        const CareerRuntimeConfig& config,
        std::shared_ptr<CareerVisualResources> resources);
    CareerActionExecutor(
        const CareerRuntimeConfig& config,
        TaskEngine::ActionRegistry actionRegistry = {});

    ActionExecutionStatus Execute(
        const CareerDomain::ActionIntent& intent,
        TaskEngine::ITaskExecutionContext& context) override;

    ActionExecutionStatus ExecuteForObservation(
        const CareerDomain::ActionIntent& intent,
        const CareerDomain::CareerObservation& observation,
        TaskEngine::ITaskExecutionContext& context) override;

    [[nodiscard]] std::shared_ptr<CareerVisualResources> GetCareerVisualResources() const override
    {
        return m_resources;
    }

    std::optional<CareerDomain::CareerRaceResultObservation> MatchRaceResult(
        const CareerDomain::CareerRaceResultExpectation& expectation,
        TaskEngine::ITaskExecutionContext& context) override;
    std::optional<bool> TakeRaceRetryDeclined() override;
    std::optional<std::string> TakeLastActionOutcome() override;
    void SetTransitionContext(const CareerTransitionContext& transition) override { m_transitionContext = transition; }
    std::optional<CareerActionHandoff> TakeActionHandoff() override;

    ActionExecutionStatus ExecuteAction(
        const std::string& screenId,
        const std::string& semanticId,
        TaskEngine::ITaskExecutionContext& context,
        const TaskEngine::TaskGraphRunOptions& options = {});

    bool ConfirmCompletion(
        const CareerDomain::ActionIntent& intent,
        TaskEngine::ITaskExecutionContext& context) override;

    bool ConfirmCompletionFromObservation(
        const CareerDomain::ActionIntent& intent,
        const CareerDomain::CareerObservation& observation,
        TaskEngine::ITaskExecutionContext& context) override;

    std::optional<CareerDomain::CareerObservation> TakeConfirmedObservation() override;
    void ResetAfterRecovery() noexcept override;
    void SetGoalObservationPolicy(bool armed, bool replayCompleted) noexcept override;
    void SetRaceRunnerResultExpected(bool expected) noexcept override;
    void ClearLearnedSkills(const std::string& deviceKey) override;

    [[nodiscard]] const std::shared_ptr<CareerVisualResources>& GetResources() const noexcept
    {
        return m_resources;
    }

private:
    [[nodiscard]] std::optional<std::string> ResolveSemanticAction(
        const CareerDomain::ActionIntent& intent,
        const CareerDomain::CareerObservation& observation) const;
    std::shared_ptr<CareerVisualResources> m_resources;
    std::shared_ptr<CareerSkillLearningFlow> m_skillLearningFlow;
    std::shared_ptr<CareerScreenObserver> m_observer;
    CareerRuntimeConfig m_config;
    std::string m_lastSourceScreen;
    std::optional<CareerDomain::CareerObservation> m_confirmedObservation;
    std::optional<bool> m_raceRetryDeclined;
    std::optional<std::string> m_lastActionOutcome;
    bool m_raceStrategyConfigured{false};
    CareerTransitionContext m_transitionContext;
    std::optional<CareerActionHandoff> m_actionHandoff;
    std::chrono::steady_clock::time_point m_handoffCreated;
};

} // namespace UmaAssistant::Career
