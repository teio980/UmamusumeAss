#include "CareerTransitionPolicy.hpp"
#include <algorithm>
#include <cctype>
#include <initializer_list>
#include <unordered_map>

namespace UmaAssistant::Career
{
namespace
{
using Phase = CareerTransitionPhase;
std::string Fold(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}
void Add(std::vector<std::string>& screens, std::initializer_list<const char*> values)
{
    for (auto const* value : values)
        if (std::find(screens.begin(), screens.end(), value) == screens.end()) screens.emplace_back(value);
}
void TurnDestinations(CareerTransitionContext& result)
{
    // EventHandle advances passive training/support events. Observe the next
    // supported page instead of competing with it by looking for Skip.
    Add(result.candidates, {"event_choice", "scenario_event", "inheritance_event"});
    if (result.continuation == Phase::TrainingOutcome || result.resumedEvent) Add(result.candidates, {"training_result"});
    if (result.semanticActionId == "recreation.confirm" || result.sourceScreenId == "claw_machine"
        || result.sourceScreenId == "claw_machine_result")
        Add(result.candidates, {"claw_machine", "claw_machine_result"});
    Add(result.candidates, {"goal_objective_complete", "goal_update", "goal_complete",
        "race_day", "career_races_ready", "career_main", "race_list", "race_list_empty",
        "race_recommendations", "race_streak_warning", "goal_incomplete", "complete_career_entry"});
    if (result.continuation == Phase::RaceOutcome)
        Add(result.candidates, {"reward", "reward_support", "race_result", "race_retry_dialog",
            "complete_career", "career_complete", "career_complete_close"});
    if (result.resumedEvent)
        Add(result.candidates, {"claw_machine", "claw_machine_result", "reward", "reward_support"});
}
const std::unordered_map<std::string, std::vector<std::string>>& SettlementEdges()
{
    static const std::unordered_map<std::string, std::vector<std::string>> edges{
        {"goal_incomplete", {"complete_career_entry", "complete_career"}},
        {"complete_career_entry", {"complete_career", "career_rank"}},
        {"complete_career", {"career_rank", "sparks", "career_result_close", "career_result"}},
        {"career_rank", {"sparks", "sparks_confirmation", "career_result_close", "career_result"}},
        {"sparks", {"sparks_confirmation", "career_result_close", "career_result"}},
        {"sparks_confirmation", {"career_result_close", "career_result"}},
        {"career_result", {"rewards", "event_reward", "rewards_collected", "follow_trainer_limit", "career_complete", "career_complete_close"}},
        {"career_result_close", {"career_rating_record_updated", "career_epithet", "career_result", "follow_trainer_limit", "rewards", "event_reward", "career_complete", "career_complete_close"}},
        {"career_rating_record_updated", {"career_epithet", "career_result", "follow_trainer_limit", "rewards", "event_reward", "career_complete", "career_complete_close"}},
        {"career_epithet", {"career_result", "rewards", "event_reward", "follow_trainer_limit", "career_complete", "career_complete_close"}},
        {"follow_trainer_limit", {"career_result_close", "career_rating_record_updated", "career_epithet", "career_result", "rewards", "event_reward", "career_complete", "career_complete_close"}},
        {"rewards", {"rewards", "event_reward", "rewards_collected", "all_rewards_earned", "follow_trainer_limit", "career_complete", "career_complete_close"}},
        {"event_reward", {"rewards", "event_reward", "rewards_collected", "all_rewards_earned", "career_complete", "career_complete_close"}},
        {"rewards_collected", {"rewards", "event_reward", "all_rewards_earned", "career_complete", "career_complete_close"}},
        {"all_rewards_earned", {"rewards", "event_reward", "rewards_collected", "follow_trainer_limit", "career_complete", "career_complete_close"}}
    };
    return edges;
}
} // namespace

bool CareerTransitionPolicy::IsEvent(const std::string& screenId)
{
    const auto id = Fold(screenId);
    return id == "event_choice" || id == "scenario_event"
        || id == "inheritance_event" || id == "training_result"
        || id == "claw_machine" || id == "claw_machine_result";
}

CareerTransitionContext CareerTransitionPolicy::AfterAction(const CareerTransitionContext& previous,
    const std::string& source, const std::string& semantic, const std::string& outcome)
{
    CareerTransitionContext result;
    result.sourceScreenId = Fold(source);
    result.semanticActionId = Fold(semantic);
    result.actionOutcome = Fold(outcome);
    result.continuation = previous.continuation;
    result.resumedEvent = previous.resumedEvent;
    const auto& id = result.sourceScreenId;
    const auto& action = result.semanticActionId;
    // Keep the source for unchanged-page evidence, never as a successful
    // destination merely because its header still matches.
    result.candidates = {id};
    auto select = [&](Phase phase, std::initializer_list<const char*> screens) {
        result.phase = phase;
        Add(result.candidates, screens);
    };
    if (id == "career_main")
    {
        result.resumedEvent = false;
        if (action == "action.training") select(Phase::TrainingSelection, {"training_selection"});
        else if (action == "action.rest") select(Phase::TurnOutcome, {"rest_confirmation"});
        else if (action == "action.summer_rest") select(Phase::TurnOutcome, {"summer_rest_confirmation"});
        else if (action == "action.infirmary") select(Phase::TurnOutcome, {"infirmary_confirmation"});
        else if (action == "action.recreation") select(Phase::TurnOutcome, {"recreation_selection", "recreation_confirmation"});
        else if (action == "action.races" || action == "action.finale_races")
            select(Phase::RaceEntry, {"race_day", "career_races_ready", "race_streak_warning", "race_recommendations", "race_list", "race_list_empty"});
    }
    else if (id == "training_selection" && action == "training.back") select(Phase::Main, {"career_main"});
    else if (id == "training_selection" || id == "rest_confirmation" || id == "summer_rest_confirmation"
        || id == "infirmary_confirmation" || id == "recreation_confirmation" || IsEvent(id))
    {
        if (id == "training_selection") { result.continuation = Phase::TrainingOutcome; result.resumedEvent = false; }
        else if (id == "rest_confirmation" || id == "summer_rest_confirmation" || id == "infirmary_confirmation"
            || id == "recreation_confirmation") { result.continuation = Phase::TurnOutcome; result.resumedEvent = false; }
        result.phase = result.continuation;
        TurnDestinations(result);
        // An outing's claw branch remains reachable through intervening events.
        if (std::find(previous.candidates.begin(), previous.candidates.end(), "claw_machine") != previous.candidates.end())
            Add(result.candidates, {"claw_machine", "claw_machine_result"});
    }
    else if (id == "recreation_selection") select(Phase::TurnOutcome, {"recreation_confirmation"});
    else if (id == "race_day" && action == "skills.open") select(Phase::RaceEntry, {"career_skills"});
    else if (id == "career_skills") select(Phase::RaceEntry, {"race_day"});
    else if (id == "race_day" || id == "career_races_ready" || id == "race_recommendations")
        select(Phase::RaceEntry, {"race_streak_warning", "race_recommendations", "race_list", "race_list_empty",
            "race_day", "career_races_ready", "race_details", "race_runner"});
    else if (id == "race_list") select(Phase::RaceEntry, {"race_streak_warning", "race_details", "race_runner"});
    else if (id == "race_list_empty" || (id == "race_streak_warning" && action == "race.streak.cancel"))
        select(Phase::Main, {"career_main", "race_day", "race_list"});
    else if (id == "race_streak_warning") select(Phase::RaceEntry, {"race_recommendations", "race_list", "race_details", "race_runner"});
    else if (id == "race_details")
        select(Phase::RaceEntry, action == "race.cancel"
            ? std::initializer_list<const char*>{"race_list"}
            : std::initializer_list<const char*>{"race_runner"});
    else if (id == "race_runner_result" || id == "race_result" || id == "reward" || id == "reward_support"
        || result.actionOutcome == "race.replay.completed")
    {
        result.phase = result.continuation = Phase::RaceOutcome;
        TurnDestinations(result);
    }
    else if (id == "race_retry_dialog")
    {
        if (result.actionOutcome == "race.retry.declined" || action == "cancel")
            select(Phase::RaceOutcome, {"race_runner_result", "race_result", "goal_incomplete", "complete_career_entry"});
        else select(Phase::RaceExecution, {"race_runner", "race_runner_result", "race_result", "race_playback_start", "race_retry_dialog"});
    }
    else if (id == "race_runner" || id == "race_playback"
        || id == "race_playback_start" || id == "race_playback_settings" || id == "race_live" || id == "race_trophy_won")
        select(Phase::RaceExecution, {"race_runner", "race_playback", "race_playback_start", "race_playback_settings",
            "race_live", "race_runner_result", "race_result", "race_trophy_won", "race_retry_dialog"});
    else if (id == "goal_objective_complete" || id == "goal_update" || id == "goal_complete")
    {
        select(Phase::Goal, {"goal_update", "goal_complete", "career_main", "career_races_ready", "race_day",
            "event_choice", "scenario_event", "inheritance_event", "reward", "reward_support",
            "goal_incomplete", "complete_career_entry"});
    }
    else if (id == "career_intro_event")
        select(Phase::NormalStartup, {"normal_quick_mode_settings", "career_main", "career_races_ready"});
    else if (id == "career_complete" || id == "career_complete_close" || id.starts_with("career_story_unlocked")
        || id == "event_home" || id == "home_unselected")
    {
        select(Phase::ReturnHome, {"career_complete", "career_complete_close", "career_story_unlocked",
            "career_story_unlocked_compact", "career_story_unlocked_to_home", "event_home", "home", "home_unselected"});
    }
    else if (auto found = SettlementEdges().find(id); found != SettlementEdges().end())
    {
        result.phase = Phase::Settlement;
        for (const auto& destination : found->second)
            if (std::find(result.candidates.begin(), result.candidates.end(), destination) == result.candidates.end())
                result.candidates.push_back(destination);
    }
    if (result.phase == Phase::RaceEntry && (id == "career_main" || id == "career_races_ready"
        || id == "race_day" || id == "race_recommendations" || id == "race_list"))
    {
        result.retryUnchangedRaceEntry = true;
        result.timeoutMs = 30000;
    }
    if (result.phase == Phase::Settlement || result.phase == Phase::ReturnHome) result.timeoutMs = 60000;
    return result;
}

CareerTransitionContext CareerTransitionPolicy::FromObservation(const std::string& screenId,
    const CareerTransitionContext& previous)
{
    if (!previous.candidates.empty()) return previous;
    auto const id = Fold(screenId);
    if (id == "career_main")
    {
        CareerTransitionContext result;
        result.phase = Phase::Main;
        result.candidates = {"career_main"};
        return result;
    }
    // A resumed screen has no triggering action. Construct only its local
    // continuation; an event may subsequently return to a turn/race boundary.
    auto resumed = previous;
    resumed.resumedEvent = IsEvent(id);
    auto result = AfterAction(resumed, id, "");
    if (id == "training_selection") result = AfterAction(previous, "career_main", "action.training");
    return result;
}
} // namespace UmaAssistant::Career
