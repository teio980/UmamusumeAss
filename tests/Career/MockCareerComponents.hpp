#pragma once

#include "UmaAssistantCore/Career/ICareerObserver.hpp"
#include "UmaAssistantCore/Career/ICareerActionExecutor.hpp"
#include <deque>
#include <vector>

namespace UmaAssistant::Career::Tests
{

class MockCareerObserver : public ICareerObserver
{
public:
    CareerDomain::CareerObservation Observe(TaskEngine::ITaskExecutionContext& /*context*/) override
    {
        if (!observationSequence.empty())
        {
            auto obs = observationSequence.front();
            if (observationSequence.size() > 1)
            {
                observationSequence.pop_front();
            }
            return obs;
        }
        CareerDomain::CareerObservation fallback;
        fallback.screenKind = CareerDomain::CareerScreenKind::MainTurn;
        return fallback;
    }

    std::deque<CareerDomain::CareerObservation> observationSequence;
};

class MockCareerActionExecutor : public ICareerActionExecutor
{
public:
    ActionExecutionStatus Execute(
        const CareerDomain::ActionIntent& intent,
        TaskEngine::ITaskExecutionContext& /*context*/) override
    {
        executedIntents.push_back(intent);
        return returnStatus;
    }

    bool ConfirmCompletion(
        const CareerDomain::ActionIntent& /*intent*/,
        TaskEngine::ITaskExecutionContext& /*context*/) override
    {
        confirmCompletionCalledCount++;
        return confirmationResult;
    }

    ActionExecutionStatus returnStatus{ActionExecutionStatus::Success};
    bool confirmationResult{true};
    int confirmCompletionCalledCount{0};
    std::vector<CareerDomain::ActionIntent> executedIntents;
};

} // namespace UmaAssistant::Career::Tests
