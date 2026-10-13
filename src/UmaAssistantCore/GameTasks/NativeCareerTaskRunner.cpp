#include "NativeCareerTaskRunner.hpp"

#include "Common/PathEncoding.hpp"
#include "CareerEntryFlow.hpp"
#include "NativeIndependentTrainingFlow.hpp"
#include "NativeNormalCareerStartup.hpp"
#include "Career/CareerActionExecutor.hpp"
#include "Career/CareerFlowCoordinator.hpp"
#include "Career/CareerObservationReader.hpp"
#include "Career/CareerPersistenceStore.hpp"
#include "Career/CareerTrainingFlowRegistry.hpp"
#include "Career/Scenarios/Ura/UraTrainingFlowAdapter.hpp"
#include "CareerDomain/Scenarios/Ura/UraScenarioModule.hpp"
#include "CareerDomain/Scenarios/Ura/UraStrategyRegistry.hpp"
#include "CareerDomain/CareerCalendar.hpp"
#include "GameData/CareerSelectionCatalog.hpp"
#include "GameData/UmaGameDatabase.hpp"
#include "UmaAssistant/Connection.hpp"
#include "TaskEngine/TaskContextInterruption.hpp"
#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <limits>
#include <mutex>
#include <set>
#include <stdexcept>
#include <string_view>

namespace UmaAssistant::GameTasks
{
namespace
{
using Json = nlohmann::json;

[[noreturn]] void Invalid(std::string_view key, std::string_view detail)
{
    throw std::invalid_argument("Career setting '" + std::string(key) + "' " + std::string(detail) + ".");
}

std::string Normalize(std::string value)
{
    auto const begin = value.find_first_not_of(" \t\r\n");
    if (begin == std::string::npos) return {};
    value = value.substr(begin, value.find_last_not_of(" \t\r\n") - begin + 1);
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

std::string String(Json const& object, char const* key, std::string fallback = {})
{
    auto const it = object.find(key);
    if (it == object.end() || it->is_null()) return fallback;
    if (!it->is_string()) Invalid(key, "must be a string");
    return it->get<std::string>();
}

bool Bool(Json const& object, char const* key, bool fallback)
{
    auto const it = object.find(key);
    if (it == object.end() || it->is_null()) return fallback;
    if (!it->is_boolean()) Invalid(key, "must be a boolean");
    return it->get<bool>();
}

int Integer(Json const& value, char const* key)
{
    if (!value.is_number_integer()) Invalid(key, "must be an integer");
    if (value.is_number_unsigned())
    {
        auto const number = value.get<std::uint64_t>();
        if (number > static_cast<std::uint64_t>((std::numeric_limits<int>::max)()))
            Invalid(key, "is outside the supported integer range");
        return static_cast<int>(number);
    }
    auto const number = value.get<std::int64_t>();
    if (number < (std::numeric_limits<int>::min)() || number > (std::numeric_limits<int>::max)())
        Invalid(key, "is outside the supported integer range");
    return static_cast<int>(number);
}

int Int(Json const& object, char const* key, int fallback)
{
    auto const it = object.find(key);
    return it == object.end() || it->is_null() ? fallback : Integer(*it, key);
}

std::optional<int> OptionalInt(Json const& object, char const* key)
{
    const auto value = object.find(key);
    return value == object.end() || value->is_null() ? std::nullopt
        : std::optional<int>(Integer(*value, key));
}

std::vector<std::string> Strings(Json const& object, char const* key);

void ReadScenarioRules(const Career::CareerVisualResources& resources,
    CareerDomain::ScenarioInitialization& initialization)
{
    if (const auto definition = resources.GetScenarioDocument("definition"))
    {
        const auto series = definition->value("finalSeries", Json::object());
        if (!series.is_object()) throw std::invalid_argument("Scenario finalSeries must be an object.");
        initialization.finalSeriesStages = Strings(series, "stages");
    }
    if (const auto document = resources.GetScenarioDocument("objectives"))
    {
        const auto objectives = document->value("objectives", Json::array());
        if (!objectives.is_array()) throw std::invalid_argument("Scenario objectives must be an array.");
        int order = 0;
        for (const auto& objective : objectives)
        {
            if (!objective.is_object()) throw std::invalid_argument("Scenario objective must be an object.");
            CareerDomain::ScenarioObjectiveDefinition converted;
            converted.objectiveId = String(objective, "objectiveId");
            converted.order = Int(objective, "order", order++);
            converted.kind = String(objective, "kind");
            if (const auto id = String(objective, "raceId"); !id.empty()) converted.raceId = id;
            converted.observedRaceIds = Strings(objective, "observedRaceIds");
            if (const auto next = String(objective, "nextObjectiveId"); !next.empty()) converted.nextObjectiveId = next;
            const auto target = objective.value("target", Json::object());
            if (!target.is_object()) throw std::invalid_argument("Scenario objective target must be an object.");
            converted.targetPlacement = OptionalInt(target, "placement");
            converted.targetPlacementAtMost = OptionalInt(target, "placementAtMost");
            converted.targetMinimum = OptionalInt(target, "minimum");
            converted.targetCount = OptionalInt(target, "count");
            if (const auto grade = String(target, "raceGrade"); !grade.empty()) converted.targetRaceGrade = grade;
            initialization.scenarioObjectives.push_back(std::move(converted));
        }
    }
    if (const auto document = resources.GetScenarioDocument("races"))
    {
        const auto races = document->value("races", Json::array());
        if (!races.is_array()) throw std::invalid_argument("Scenario races must be an array.");
        for (const auto& race : races)
        {
            if (!race.is_object()) throw std::invalid_argument("Scenario race must be an object.");
            CareerDomain::ScenarioRaceDefinition converted;
            converted.raceId = String(race, "raceId");
            converted.grade = String(race, "grade");
            converted.rewardFans = OptionalInt(race, "rewardFans");
            const auto course = race.value("course", Json::object());
            if (!course.is_object()) throw std::invalid_argument("Scenario race course must be an object.");
            converted.distanceBand = String(course, "distanceBand");
            converted.distanceMeters = OptionalInt(course, "distance");
            if (const auto observed = race.find("observedOutcome"); observed != race.end() && !observed->is_null())
            {
                if (!observed->is_object()) throw std::invalid_argument("Scenario observedOutcome must be an object.");
                converted.expectedPlacement = OptionalInt(*observed, "placement");
                converted.resultConfidence = observed->value("confidence", 0.95);
                if (const auto capture = String(*observed, "capture"); !capture.empty())
                    converted.runtimeFrameCatalogId = "career.runtime_frame:"
                        + Paths::ToUtf8(Paths::FromUtf8(capture).stem());
            }
            const auto retry = race.value("retryPolicy", Json::object());
            if (!retry.is_object()) throw std::invalid_argument("Scenario retryPolicy must be an object.");
            converted.retryEnabled = Bool(retry, "enabled", false);
            converted.maxRetryCount = Int(retry, "maxRetryCount", 0);
            initialization.scenarioRaces.push_back(std::move(converted));
        }
    }
    if (!initialization.finalSeriesStages.empty())
        for (const auto& objective : initialization.scenarioObjectives)
            if (objective.objectiveId == initialization.finalSeriesStages.front() && objective.raceId)
                for (const auto& race : initialization.scenarioRaces)
                    if (race.raceId == *objective.raceId)
                        initialization.scenarioTrainingRaceDistanceFallback =
                            CareerDomain::TrainingRaceDistance{race.distanceBand, race.distanceMeters};
}

std::optional<int> PositiveId(Json const& object, char const* key, std::optional<int> fallback = {})
{
    auto const it = object.find(key);
    if (it == object.end()) return fallback;
    if (it->is_null()) return {};
    int const value = Integer(*it, key);
    if (value <= 0) Invalid(key, "must be a positive ID");
    return value;
}

std::vector<int> Ids(Json const& object, char const* key)
{
    auto const it = object.find(key);
    if (it == object.end() || it->is_null()) return {};
    if (!it->is_array()) Invalid(key, "must be an array of IDs");
    std::vector<int> result;
    std::set<int> seen;
    for (auto const& item : *it)
    {
        int const id = Integer(item, key);
        if (id <= 0) Invalid(key, "contains a nonpositive ID");
        if (!seen.insert(id).second) Invalid(key, "contains a duplicate ID");
        result.push_back(id);
    }
    return result;
}

std::vector<std::string> Strings(Json const& object, char const* key)
{
    auto const it = object.find(key);
    if (it == object.end() || it->is_null()) return {};
    if (!it->is_array()) Invalid(key, "must be an array of strings");
    std::vector<std::string> result;
    for (auto const& item : *it)
    {
        if (!item.is_string() || Normalize(item.get<std::string>()).empty())
            Invalid(key, "contains an empty or non-string value");
        result.push_back(item.get<std::string>());
    }
    return result;
}

void OneOf(std::string const& value, char const* key, std::initializer_list<std::string_view> options)
{
    if (std::find(options.begin(), options.end(), value) == options.end())
        Invalid(key, "has an unknown value '" + value + "'");
}

} // namespace

std::optional<Career::CareerRuntimeConfig> NativeCareerTaskRunner::ParseSettings(
    const Json& settings, std::string& errorMessage)
{
    errorMessage.clear();
    try
    {
        if (!settings.is_object()) throw std::invalid_argument("Career settings must be a JSON object.");
        Career::CareerRuntimeConfig config;
        config.manifestPath = String(settings, "manifestPath", "hachimi/ura/manifest.json");
        auto const checkpointDir = String(settings, "checkpointDirectory");
        config.checkpointDirectory = checkpointDir.empty()
            ? (std::filesystem::temp_directory_path() / "uma-career-checkpoints")
            : Paths::FromUtf8(checkpointDir);
        config.traineeId = PositiveId(settings, "traineeId", 1001);
        if (!config.traineeId) Invalid("traineeId", "is required");
        config.careerMode = Normalize(String(settings, "careerMode", "normal"));
        OneOf(config.careerMode, "careerMode", {"normal", "independent"});
        config.runCount = Int(settings, "runCount", 1);
        if (config.runCount <= 0) Invalid("runCount", "must be positive");
        config.maxSteps = Int(settings, "maxSteps", 1200);
        if (config.maxSteps <= 0) Invalid("maxSteps", "must be positive");
        config.strategyId = Normalize(String(settings, "strategyId", "default-speed-medium"));
        config.normalLineupStrategy = Normalize(String(settings, "normalLineupStrategy", "pace"));
        config.independentLineupStrategy = Normalize(String(settings, "independentLineupStrategy", "pace"));
        config.normalEventHandling = Normalize(String(settings, "normalEventHandling", "default"));
        config.retryFailedRaceWithAlarmClock = Bool(settings, "retryFailedRaceWithAlarmClock", false);
        config.pauseOnUnknownOutcome = Bool(settings, "pauseOnUnknownOutcome", true);
        config.allowOptionalRaces = Bool(settings, "allowOptionalRaces", false);
        config.normalSkillIds = Ids(settings, "normalSkillIds");
        config.independentSkillIds = Ids(settings, "independentSkillIds");
        config.independentTrainingFocus = Normalize(String(settings, "independentTrainingFocus", "balanced"));
        config.independentAgendaSelections = Strings(settings, "independentAgendaSelections");
        // The modern destructive-entry flag takes precedence over its legacy inverse.
        bool const hasDelete = settings.contains("deleteExistingCareerData")
            && !settings.at("deleteExistingCareerData").is_null();
        bool const continueExisting = Bool(settings, "continueExistingCareer", true);
        config.deleteExistingCareerData = Bool(settings, "deleteExistingCareerData", !continueExisting);
        config.continueExistingCareer = hasDelete ? !config.deleteExistingCareerData : continueExisting;
        config.supportDeckMode = Normalize(String(settings, "supportDeckMode", "auto"));
        config.supportDeckPreset = Normalize(String(settings, "supportDeckPreset", "custom"));
        config.friendSupportCardId = PositiveId(settings, "friendSupportCardId");
        config.supportCardIds = Ids(settings, "supportCardIds");
        config.legacySelectionMode = Normalize(String(settings, "legacySelectionMode", "auto"));
        config.useLegacyGuest = Bool(settings, "useLegacyGuest", true);
        config.useCachedLegacy = Bool(settings, "useCachedLegacy", true);
        config.legacyAttributeSparks = Strings(settings, "legacyAttributeSparks");
        config.legacyAptitudeSparks = Strings(settings, "legacyAptitudeSparks");

        auto const ratio = settings.find("normalTrainingRatio");
        if (ratio != settings.end() && !ratio->is_null())
        {
            if (!ratio->is_object()) Invalid("normalTrainingRatio", "must be a five-field object");
            config.normalTrainingRatio = {Int(*ratio, "speed", 2), Int(*ratio, "stamina", 1),
                Int(*ratio, "power", 0), Int(*ratio, "guts", 0), Int(*ratio, "wit", 0)};
        }
        OneOf(config.supportDeckMode, "supportDeckMode", {"auto", "highest-star", "selected"});
        OneOf(config.legacySelectionMode, "legacySelectionMode", {"auto", "manual"});
        if (config.supportDeckMode == "selected"
            && config.supportCardIds.size() != 5 && config.supportCardIds.size() != 6)
            Invalid("supportCardIds", "requires five or six cards in selected mode");
        if (config.supportDeckMode == "highest-star" && config.supportDeckPreset == "custom")
            Invalid("supportDeckPreset", "requires a preset for highest-star mode");
        if (config.careerMode == "normal")
        {
            if (!CareerDomain::UraStrategyRegistry::IsRegistered(config.strategyId))
                Invalid("strategyId", "is not registered");
            OneOf(config.normalLineupStrategy, "normalLineupStrategy", {"front", "pace", "late", "end"});
            OneOf(config.normalEventHandling, "normalEventHandling", {"default"});
            if (config.strategyId == "custom-ratio")
            {
                auto const r = config.normalTrainingRatio;
                std::array const weights{r.speed, r.stamina, r.power, r.guts, r.wit};
                if (!std::all_of(weights.begin(), weights.end(), [](int weight) { return weight >= 0 && weight <= 100; })
                    || std::all_of(weights.begin(), weights.end(), [](int weight) { return weight == 0; }))
                    Invalid("normalTrainingRatio", "requires values from 0 to 100 with a nonzero total");
            }
        }
        else
        {
            OneOf(config.independentTrainingFocus, "independentTrainingFocus", {"balanced", "stamina", "sprint"});
            OneOf(config.independentLineupStrategy, "independentLineupStrategy", {"front", "pace", "late", "end"});
            for (auto const& selection : config.independentAgendaSelections)
            {
                auto const first = selection.find('|');
                auto const second = first == std::string::npos ? first : selection.find('|', first + 1);
                if (first == std::string::npos || first == 0 || second == std::string::npos
                    || second == first + 1 || second + 1 >= selection.size())
                    Invalid("independentAgendaSelections", "requires year|turn|race keys");
            }
        }
        return config;
    }
    catch (std::exception const& exception)
    {
        errorMessage = exception.what();
        return {};
    }
}

std::filesystem::path NativeCareerTaskRunner::ResolveManifestPath(
    const Career::CareerRuntimeConfig& config, const std::filesystem::path& resourceRoot)
{
    auto const configured = Paths::FromUtf8(config.manifestPath);
    if (configured.is_absolute()) return configured.lexically_normal();
    auto relative = configured.lexically_normal();
    if (!relative.empty() && *relative.begin() == "resource")
        relative = relative.lexically_relative("resource");
    return (resourceRoot / relative).lexically_normal();
}

NativeGameTaskResult NativeCareerTaskRunner::Run(
    const Career::CareerRuntimeConfig& config, TaskEngine::ITaskExecutionContext& context,
    const std::filesystem::path& resourceRoot)
{
    NativeGameTaskResult result;
    result.taskId = "career-training";
    result.totalUnits = config.runCount;
    auto fail = [&](std::string message, bool cancelled = false) {
        result.succeeded = false;
        int const deviceError = context.GetLastErrorCode();
        result.errorCode = cancelled || context.IsCancelled()
            ? static_cast<int>(ConnectionErrorCode::Canceled)
            : deviceError != 0 ? deviceError : static_cast<int>(ConnectionErrorCode::CommandFailed);
        result.message = std::move(message);
        return result;
    };
    if (context.IsCancelled()) return fail("Career task was canceled before execution.", true);
    try
    {
        auto snapshot = config;
        snapshot.resourceDirectory = resourceRoot;
        snapshot.manifestPath = Paths::ToUtf8(ResolveManifestPath(config, resourceRoot));
        GameData::UmaGameDatabase trainees(resourceRoot);
        GameData::CareerSelectionCatalog catalogs(resourceRoot);
        std::string error;
        if (!trainees.Load(error) || !catalogs.Load(error)) return fail(error);
        auto const trainee = trainees.TryGetTrainee(config.traineeId.value_or(0));
        if (!trainee || !trainee->available) return fail("Configured Career trainee does not exist or is unavailable.");

        TaskEngine::ActionRegistry actions;
        CareerEntryFlow::RegisterActions(snapshot, trainees, catalogs, actions);
        NativeIndependentTrainingFlow::RegisterActions(snapshot, catalogs, actions);
        auto resources = std::make_shared<Career::CareerVisualResources>(snapshot, std::move(actions));
        if (!resources->IsReady())
        {
            std::string diagnostics = "Career resource snapshot could not be loaded.";
            for (auto const& diagnostic : resources->GetLoadDiagnostics()) diagnostics += "\n" + diagnostic;
            return fail(std::move(diagnostics));
        }
        snapshot.resourceVersion = resources->GetResourceVersion();
        // Default modules are registered once; extensions can replace or add a module
        // before a run without introducing scenario branches into TaskEngine.
        static std::once_flag registered;
        std::call_once(registered, [] {
            if (!CareerDomain::ScenarioRegistry::Instance().HasModule("ura"))
                CareerDomain::UraScenarioModule::Register();
        });
        if (!CareerDomain::ScenarioRegistry::Instance().HasModule(resources->GetScenarioId()))
            return fail("No Career scenario module is registered for " + resources->GetScenarioId());
        CareerDomain::ScenarioInitialization scenarioInitialization;
        ReadScenarioRules(*resources, scenarioInitialization);
        for (auto const& objective : trainee->careerObjectives)
            for (std::size_t index = 0; index < objective.raceIds.size(); ++index)
                if (auto const race = trainees.TryGetCareerRace(objective.raceIds[index]))
                    scenarioInitialization.trainingRaceDistances.push_back({objective.turn, objective.order,
                        static_cast<int>(index), {race->distanceBand, race->distanceMeters}, objective.raceIds[index]});
        for (auto const& race : catalogs.Races())
        {
            if (!race.gameAvailable || race.gameOrder != 0) continue;
            if (auto const turn = CareerDomain::CareerCalendar::TryMapRaceSlot(race.year, race.turn))
                scenarioInitialization.raceSchedule.push_back({*turn, race.grade, true});
        }
        Career::CareerTrainingFlowRegistry trainingFlows;
        Career::Ura::RegisterTrainingFlow(trainingFlows);

        auto observer = std::make_shared<Career::CareerScreenObserver>(resources);
        for (int run = 0; run < config.runCount; ++run)
        {
            if (context.IsCancelled()) return fail("Career task was canceled.", true);
            auto scenario = CareerDomain::ScenarioRegistry::Instance().CreateModule(resources->GetScenarioId());
            if (!scenario) return fail("Career scenario factory did not create a module.");
            scenario->Initialize(scenarioInitialization);
            TaskEngine::TaskExecutionEvent started{};
            started.type = TaskEngine::TaskEventType::TaskEntering;
            started.taskName = "career.run";
            started.pipelineName = "career-training";
            started.taskId = "career-training";
            started.stepIndex = run + 1;
            started.completedUnits = result.completedUnits;
            started.totalUnits = result.totalUnits;
            context.EmitEvent(started);
            observer->ResetForEntry();
            // Strategies and confirmed turn state live for the whole run, even
            // when the device guard has relaunched the game after a date reset.
            std::unique_ptr<Career::CareerFlowCoordinator> coordinator;
            if (snapshot.careerMode == "normal")
            {
                auto const ratio = snapshot.normalTrainingRatio;
                auto strategy = CareerDomain::UraStrategyRegistry::Create(snapshot.strategyId,
                    {ratio.speed, ratio.stamina, ratio.power, ratio.guts, ratio.wit});
                auto executor = std::make_shared<Career::CareerActionExecutor>(snapshot, resources);
                if (!snapshot.continueExistingCareer)
                {
                    executor->ClearLearnedSkills(context.GetDeviceSerial());
                    if (snapshot.traineeId)
                    {
                        Career::SmartTrainingConfirmationStore::Clear(
                            snapshot.checkpointDirectory, *snapshot.traineeId, context.GetDeviceSerial());
                    }
                }
                auto trainingFlow = trainingFlows.Create(resources->GetScenarioId(), resources);
                if (strategy->RequiresTrainingCandidateScan() && !trainingFlow)
                    return fail("The selected strategy requires an unregistered scenario training flow.");
                coordinator = std::make_unique<Career::CareerFlowCoordinator>(
                    observer, executor, scenario, strategy, snapshot, std::move(trainingFlow));
            }
            auto entryConfig = snapshot;
            for (int recoveryCount = 0;;)
            {
                try
                {
                    bool needsEntry = true;
                    std::string confirmedEntryScreen;
                    std::optional<CareerDomain::CareerObservation> confirmedEntry;
                    if (snapshot.careerMode == "independent"
                        && !NativeIndependentTrainingFlow::NeedsEntry(snapshot, context, needsEntry, error, recoveryCount > 0))
                        return fail(error);
                    if (needsEntry)
                    {
                        const auto initialScreens = recoveryCount > 0 || run > 0
                            ? std::vector<std::string>{"home", "home_unselected"} : std::vector<std::string>{};
                        auto const entry = CareerEntryFlow::Run(entryConfig, *resources, *observer, context, initialScreens);
                        if (!entry.succeeded)
                            return fail(entry.error.empty() ? "Career entry was not confirmed." : entry.error, entry.cancelled);
                        confirmedEntryScreen = entry.lastScreenId;
                        confirmedEntry = entry.runtimeObservation;
                    }
                    if (snapshot.careerMode == "independent")
                    {
                        auto const independent = NativeIndependentTrainingFlow::Run(
                            snapshot, *resources, *observer, context, recoveryCount > 0, confirmedEntry);
                        if (!independent.succeeded)
                            return fail(independent.error.empty() ? "Independent Training was not confirmed." : independent.error,
                                independent.cancelled);
                    }
                    else
                    {
                        auto const startup = NativeNormalCareerStartup::Run(
                            snapshot, *resources, *observer, context, confirmedEntryScreen, confirmedEntry);
                        if (!startup.succeeded)
                            return fail(startup.error.empty() ? "Normal Career startup was not confirmed." : startup.error,
                                startup.cancelled);
                        if (startup.runtimeObservation)
                            coordinator->SetConfirmedObservation(*startup.runtimeObservation);
                        auto const flow = coordinator->RunToCompletion(context, snapshot.maxSteps);
                        if (flow.status != Career::CareerRunStatus::Completed)
                            return fail(flow.failureReason.empty() ? "Career did not reach confirmed completion." : flow.failureReason,
                                flow.status == Career::CareerRunStatus::Cancelled);
                    }
                    break;
                }
                catch (TaskEngine::TaskContextInterruption const& interruption)
                {
                    if (context.IsCancelled()) return fail("Career recovery was cancelled.", true);
                    if (!interruption.homeRestored || ++recoveryCount > 3)
                        return fail(std::string("Career recovery could not safely resume: ") + interruption.what());
                    // Recovery may resume existing game data, never repeat the
                    // initial destructive entry preference from this run.
                    entryConfig.continueExistingCareer = true;
                    entryConfig.deleteExistingCareerData = false;
                    observer->ResetForRecovery();
                    if (coordinator) coordinator->ResetAfterRecovery();
                    context.Log("WARN", "Career was interrupted; reconciling the confirmed run from Home.");
                }
            }
            if (context.IsCancelled()) return fail("Career task was canceled during completion confirmation.", true);
            ++result.completedUnits;
            auto completed = started;
            completed.type = TaskEngine::TaskEventType::TaskCompleted;
            completed.succeeded = true;
            completed.completedUnits = result.completedUnits;
            context.EmitEvent(completed);
        }
        result.succeeded = true;
        result.message = config.careerMode == "independent" ? "Independent Training completed." : "Career training completed.";
        return result;
    }
    catch (std::exception const& exception)
    {
        return fail(exception.what());
    }
}

} // namespace UmaAssistant::GameTasks
