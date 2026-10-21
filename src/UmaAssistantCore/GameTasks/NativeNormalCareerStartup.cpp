#include "NativeNormalCareerStartup.hpp"

namespace UmaAssistant::GameTasks
{
namespace
{
bool IsRuntimeScreen(CareerDomain::CareerObservation const& observation)
{
    return observation.screenKind != CareerDomain::CareerScreenKind::Unknown
        && observation.screenId != "career_final_confirmation"
        && observation.screenId != "normal_final_confirmation_startup"
        && observation.screenId != "career_intro_event"
        && observation.screenId != "normal_intro_event"
        && observation.screenId != "normal_quick_mode_settings";
}

bool Execute(CareerEntryResult& result, Career::CareerVisualResources& resources,
    TaskEngine::ITaskExecutionContext& context, std::string const& screen, std::string const& semantic)
{
    if (context.IsCancelled()) { result.cancelled = true; return false; }
    auto const action = resources.RunAction(screen, semantic, context);
    if (action.status == TaskEngine::TaskGraphStatus::Cancelled || context.IsCancelled())
    {
        result.cancelled = true;
        return false;
    }
    if (action.status != TaskEngine::TaskGraphStatus::Completed
        && action.status != TaskEngine::TaskGraphStatus::Success)
    {
        result.error = action.errorMessage.empty() ? "Normal Career setup failed at " + semantic : action.errorMessage;
        return false;
    }
    ++result.actionsCompleted;
    return true;
}

bool ConfigureQuickMode(CareerEntryResult& result, Career::CareerVisualResources& resources,
    TaskEngine::ITaskExecutionContext& context)
{
    for (auto const* action : {"normal.quick_mode.shorten", "normal.quick_mode.skip", "normal.quick_mode.confirm"})
        if (!Execute(result, resources, context, "normal_quick_mode_settings", action))
            return false;
    return true;
}
}

CareerEntryResult NativeNormalCareerStartup::Run(const Career::CareerRuntimeConfig& config,
    Career::CareerVisualResources& resources, Career::ICareerObserver& observer,
    TaskEngine::ITaskExecutionContext& context, std::string const& confirmedEntryScreen,
    std::optional<CareerDomain::CareerObservation> confirmedEntry)
{
    CareerEntryResult result;
    std::vector<std::string> startupScreens;
    for (auto const& screen : resources.GetScreens())
        if (screen.flow != "entry" && screen.flow != "normal" && screen.flow != "independent"
            && screen.screenId != "home" && screen.screenId != "home_unselected")
            startupScreens.push_back(screen.screenId);
    for (auto const* id : {"career_final_confirmation", "normal_final_confirmation_startup",
        "career_intro_event", "normal_intro_event", "normal_quick_mode_settings", "career_skills"})
        startupScreens.push_back(id);
    bool const newCareer = confirmedEntryScreen == "career_final_confirmation"
        || confirmedEntryScreen == "normal_final_confirmation_startup"
        || confirmedEntryScreen == "career_intro_event" || confirmedEntryScreen == "normal_intro_event"
        || confirmedEntryScreen == "normal_quick_mode_settings";
    auto const initialScreens = confirmedEntryScreen.empty() ? startupScreens
        : std::vector<std::string>{confirmedEntryScreen};
    if (newCareer)
        std::erase_if(startupScreens, [&](auto const& id) {
            auto const* screen = resources.FindScreen(id);
            return screen && screen->flow == "settlement";
        });
    auto initial = confirmedEntry ? std::move(*confirmedEntry)
        : observer.ObserveScreens(context, initialScreens, !newCareer);
    for (int attempt = 0; (initial.screenId == "unknown" || initial.screenId == "capture_failed")
        && attempt < 30 && !context.IsCancelled(); ++attempt)
    {
        context.Sleep(250);
        // A resumed event or dialog may disappear while entry hands control
        // to startup. Retry all runtime candidates instead of pinning every
        // capture to the screen that was visible before the transition.
        initial = observer.ObserveScreens(context, newCareer ? initialScreens : startupScreens, !newCareer);
    }
    result.lastScreenId = initial.screenId;
    if (context.IsCancelled() || initial.screenId == "capture_cancelled") { result.cancelled = true; return result; }
    if (IsRuntimeScreen(initial))
    {
        result.runtimeObservation = initial;
        result.succeeded = true;
        return result;
    }
    bool const finalConfirmation = initial.screenId == "career_final_confirmation"
        || initial.screenId == "normal_final_confirmation_startup";
    bool const pendingOpening = initial.screenId == "career_intro_event"
        || initial.screenId == "normal_intro_event" || initial.screenId == "normal_quick_mode_settings";
    if (!finalConfirmation && !pendingOpening)
    {
        result.error = "Normal Career setup requires confirmed Final Confirmation or a resumed Career screen. Last observed screen: "
            + initial.screenId + ".";
        return result;
    }
    // Match main's careerStartTransitionExpected policy: opening setup must
    // reach its first turn before runtime goal/race/settlement candidates are
    // admitted. A shared marker during the opening animation cannot establish
    // that a new Career has already completed an objective.
    startupScreens = {"career_intro_event", "normal_intro_event", "normal_quick_mode_settings",
        "career_main"};
    std::string const finalPage = "career_final_confirmation";
    if (finalConfirmation)
    {
        for (auto const& action : {std::string("normal.select_mode"), std::string("normal.strategy.change"),
            "normal.strategy.option." + config.normalLineupStrategy, std::string("normal.strategy.save"),
            std::string("normal.strategy.return"), std::string("normal.start")})
            if (!Execute(result, resources, context, finalPage, action)) return result;
    }

    bool introSkipped = false;
    for (int attempt = 0; attempt < 30; ++attempt)
    {
        if (context.IsCancelled()) { result.cancelled = true; return result; }
        auto const observation = attempt == 0 && pendingOpening ? initial : observer.ObserveScreens(context, startupScreens);
        if (context.IsCancelled()) { result.cancelled = true; return result; }
        result.lastScreenId = observation.screenId;
        if (observation.screenId == "capture_cancelled") { result.cancelled = true; return result; }
        if (observation.screenId == "resource_error")
        { result.error = "Normal Career visual resources are unavailable."; return result; }
        if (observation.screenId == "capture_failed") { context.Sleep(400); continue; }
        if (observation.screenId == "career_main")
        {
            result.runtimeObservation = observation;
            result.succeeded = true;
            return result;
        }
        if (observation.screenId == "normal_quick_mode_settings")
        {
            if (!ConfigureQuickMode(result, resources, context)) return result;
        }
        else if (!introSkipped && (observation.screenId == "career_intro_event"
            || observation.screenId == "normal_intro_event"
            || (observation.screenKind == CareerDomain::CareerScreenKind::EventChoice
                && !pendingOpening && finalConfirmation)))
        {
            if (!Execute(result, resources, context, finalPage, "normal.post_start.skip")) return result;
            introSkipped = true;
            if (!ConfigureQuickMode(result, resources, context)) return result;
        }
        else if (!introSkipped && observation.screenKind == CareerDomain::CareerScreenKind::Unknown
            && finalConfirmation)
        {
            // Some packaged versions do not classify the opening event. The
            // resource graph still requires its actual advance marker before
            // it can send input.
            if (!Execute(result, resources, context, finalPage, "normal.post_start.skip")) return result;
            introSkipped = true;
            if (!ConfigureQuickMode(result, resources, context)) return result;
        }
        context.Sleep(400);
    }
    result.cancelled = context.IsCancelled();
    result.error = "Normal Career Start did not reach a confirmed Career screen within the startup budget.";
    return result;
}
}
