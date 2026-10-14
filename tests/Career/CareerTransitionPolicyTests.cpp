#include "UmaAssistantCore/Career/CareerTransitionPolicy.hpp"
#include "UmaAssistantCore/Career/CareerVisualResources.hpp"
#include <catch2/catch_test_macros.hpp>
#include <algorithm>
#include <filesystem>
#include <iostream>
#include <nlohmann/json.hpp>

using namespace UmaAssistant::Career;

namespace
{
bool Has(const CareerTransitionContext& context, const std::string& id)
{
    return std::find(context.candidates.begin(), context.candidates.end(), id) != context.candidates.end();
}
}

TEST_CASE("Turn entries restrict recognition to their own dialogs", "[CareerTransition]")
{
    for (auto const& [action, destination] : std::vector<std::pair<std::string, std::string>>{
        {"action.training", "training_selection"}, {"action.rest", "rest_confirmation"},
        {"action.summer_rest", "summer_rest_confirmation"}, {"action.infirmary", "infirmary_confirmation"},
        {"action.recreation", "recreation_confirmation"}})
    {
        const auto context = CareerTransitionPolicy::AfterAction({}, "career_main", action);
        CAPTURE(action);
        CHECK(Has(context, destination));
        CHECK_FALSE(Has(context, "career_skills"));
        CHECK_FALSE(Has(context, "career_epithet"));
        CHECK_FALSE(Has(context, "race_runner"));
        CHECK(context.candidates.size() <= 3);
    }
}

TEST_CASE("Continuous events retain the submitted turn and outing continuation", "[CareerTransition]")
{
    auto training = CareerTransitionPolicy::AfterAction({}, "training_selection", "training.guts");
    for (auto const* id : {"training_result", "event_choice", "scenario_event", "inheritance_event", "event_choice"})
    {
        training = CareerTransitionPolicy::AfterAction(training, id, "event.advance");
        CHECK(training.continuation == CareerTransitionPhase::TrainingOutcome);
        CHECK(Has(training, "career_main"));
        CHECK(Has(training, "race_list"));
        CHECK_FALSE(Has(training, "career_epithet"));
        CHECK_FALSE(Has(training, "race_runner"));
    }
    auto outing = CareerTransitionPolicy::AfterAction({}, "recreation_confirmation", "recreation.confirm");
    outing = CareerTransitionPolicy::AfterAction(outing, "event_choice", "event.choice_first");
    CHECK(Has(outing, "claw_machine"));
    CHECK_FALSE(Has(outing, "training_result"));
    auto race = CareerTransitionPolicy::AfterAction({}, "race_runner", "entry.view_results", "race.replay.completed");
    race = CareerTransitionPolicy::AfterAction(race, "event_choice", "event.choice_first");
    CHECK(race.continuation == CareerTransitionPhase::RaceOutcome);
    CHECK(Has(race, "reward_support"));
    CHECK_FALSE(Has(race, "training_selection"));
}

TEST_CASE("Every runtime resource action has a local continuation contract", "[CareerTransition][CareerPackage]")
{
    CareerRuntimeConfig config;
    config.resourceDirectory = std::filesystem::path(CMAKE_SOURCE_DIR) / "resource";
    CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    int actions = 0;
    for (auto const& screen : resources.GetScreens())
    {
        if (screen.flow == "entry" || screen.flow == "normal" || screen.flow == "independent") continue;
        for (auto const& [key, action] : screen.actions)
        {
            (void)key;
            if (action.semanticId == "goal.details") continue; // main declares but never calls this action.
            CAPTURE(screen.screenId, action.semanticId);
            const auto transition = CareerTransitionPolicy::AfterAction({}, screen.screenId, action.semanticId);
            CHECK(transition.phase != CareerTransitionPhase::Unknown);
            CHECK_FALSE(transition.candidates.empty());
            for (auto const& destination : transition.candidates)
                CHECK((destination == "career_skills" || resources.FindScreen(destination) != nullptr));
            ++actions;
        }
    }
    CHECK(actions > 60);
}

TEST_CASE("Empty races and declined retries have separate return contracts", "[CareerTransition]")
{
    const auto empty = CareerTransitionPolicy::AfterAction({}, "race_list_empty", "back");
    CHECK(Has(empty, "career_main"));
    CHECK_FALSE(Has(empty, "race_runner"));
    const auto declined = CareerTransitionPolicy::AfterAction({}, "race_retry_dialog", "retry_with_alarm_clock", "race.retry.declined");
    CHECK(Has(declined, "race_runner_result"));
    CHECK(Has(declined, "goal_incomplete"));
    CHECK_FALSE(Has(declined, "race_runner"));
    const auto accepted = CareerTransitionPolicy::AfterAction({}, "race_retry_dialog", "retry_with_alarm_clock", "race.retry.accepted");
    CHECK(Has(accepted, "race_runner"));
    CHECK_FALSE(Has(accepted, "career_main"));
}

TEST_CASE("A resumed event retains unknown turn origins within its local continuation", "[CareerTransition]")
{
    auto resumed = CareerTransitionPolicy::FromObservation("event_choice");
    CHECK(resumed.resumedEvent);
    resumed = CareerTransitionPolicy::AfterAction(resumed, "event_choice", "event.choice_first");
    CHECK(Has(resumed, "training_result"));
    CHECK(Has(resumed, "claw_machine"));
    CHECK(Has(resumed, "reward_support"));
    CHECK_FALSE(Has(resumed, "career_epithet"));
    CHECK_FALSE(Has(resumed, "race_runner"));
    resumed = CareerTransitionPolicy::AfterAction(resumed, "career_main", "action.rest");
    CHECK_FALSE(resumed.resumedEvent);
    CHECK_FALSE(Has(resumed, "training_result"));
    CHECK(resumed.candidates.size() == 2);
}

TEST_CASE("Settlement transitions cover all valid consecutive screens", "[CareerTransition]")
{
    const auto sparks = CareerTransitionPolicy::AfterAction({}, "sparks", "sparks.confirm");
    CHECK(Has(sparks, "sparks_confirmation"));
    CHECK(Has(sparks, "career_result_close"));

    const auto sparksConfirm = CareerTransitionPolicy::AfterAction({}, "sparks_confirmation", "sparks.keep");
    CHECK(Has(sparksConfirm, "career_result_close"));

    const auto resultClose = CareerTransitionPolicy::AfterAction({}, "career_result_close", "career.close");
    CHECK(Has(resultClose, "career_rating_record_updated"));
    CHECK(Has(resultClose, "career_result"));

    const auto ratingUpdated = CareerTransitionPolicy::AfterAction({}, "career_rating_record_updated", "career.rating_record.next");
    CHECK(Has(ratingUpdated, "career_epithet"));
    CHECK(Has(ratingUpdated, "career_result"));
}

TEST_CASE("Career exports the composed action continuation inventory", "[CareerTransitionAudit]")
{
    CareerRuntimeConfig config;
    config.resourceDirectory = std::filesystem::path(CMAKE_SOURCE_DIR) / "resource";
    CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    for (auto const& screen : resources.GetScreens())
    {
        if (screen.actions.empty())
            std::cout << "CAREER_CONTRACT " << nlohmann::json{{"screen", screen.screenId}, {"flow", screen.flow}, {"action", nullptr}}.dump() << '\n';
        for (auto const& [key, action] : screen.actions)
        {
            (void)key;
            const auto transition = CareerTransitionPolicy::AfterAction({}, screen.screenId, action.semanticId);
            std::cout << "CAREER_CONTRACT " << nlohmann::json{
                {"screen", screen.screenId}, {"flow", screen.flow}, {"action", action.semanticId},
                {"task", action.taskName}, {"candidates", transition.candidates},
                {"phase", static_cast<int>(transition.phase)}, {"timeoutMs", transition.timeoutMs}}.dump() << '\n';
        }
    }
}
