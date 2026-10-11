#include "NativeIndependentTrainingFlow.hpp"

#include "Common/PathEncoding.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <fstream>
#include <iomanip>
#include <optional>
#include <sstream>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace UmaAssistant::GameTasks
{
namespace
{
using Json = nlohmann::json;
constexpr int kCheckpointVersion = 2;
constexpr std::string_view kStartIssued = "independent_start_issued";

struct Checkpoint
{
    int version{kCheckpointVersion};
    std::string stage{"SelectIndependentMode"};
    int agendaIndex{0};
    int skillIndex{0};
    std::optional<int> currentSkillId;
    std::string lastConfirmedScreen{"unknown"};
    bool completionVerified{false};
    int traineeId{0};
    std::string adbSerial;
    std::string configurationFingerprint;
    std::string resourceVersion;
};

std::string Key(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    value.erase(std::remove_if(value.begin(), value.end(), [](unsigned char ch) {
        return ch == '_' || ch == '-' || std::isspace(ch);
    }), value.end());
    return value;
}

Json const* FindProperty(Json const& json, std::string_view name)
{
    if (!json.is_object()) return nullptr;
    for (auto it = json.begin(); it != json.end(); ++it)
        if (Key(it.key()) == Key(std::string(name))) return &it.value();
    return nullptr;
}

std::optional<std::string> StringProperty(Json const& json, std::string_view name)
{
    auto const* value = FindProperty(json, name);
    return value && value->is_string() ? std::optional<std::string>(value->get<std::string>()) : std::nullopt;
}

std::optional<int> IntProperty(Json const& json, std::string_view name)
{
    auto const* value = FindProperty(json, name);
    if (!value || !value->is_number_integer()) return {};
    try { return value->get<int>(); } catch (...) { return {}; }
}

std::optional<bool> BoolProperty(Json const& json, std::string_view name)
{
    auto const* value = FindProperty(json, name);
    return value && value->is_boolean() ? std::optional<bool>(value->get<bool>()) : std::nullopt;
}

std::optional<std::string> MigrateLegacyStage(Json const& json)
{
    static constexpr std::array<std::string_view, 8> oldFlags{
        "IndependentModeSelected", "IndependentLineupConfigured", "IndependentTrainingFocusConfigured",
        "IndependentAgendaConfigured", "IndependentSkillsConfigured", "IndependentLineupCollapsed",
        "IndependentStrategyConfigured", "IndependentSetupCompleted"};
    bool hasLegacyFlag = false;
    for (auto const flag : oldFlags) hasLegacyFlag = hasLegacyFlag || FindProperty(json, flag) != nullptr;
    if (hasLegacyFlag)
    {
        static constexpr std::array<std::string_view, 8> firstMissingStages{
            "SelectIndependentMode", "ExpandLineup", "ConfigureFocus", "ConfigureAgenda",
            "ConfigureSkills", "CollapseLineup", "ConfigureStrategy", "StartTraining"};
        for (std::size_t index = 0; index < oldFlags.size(); ++index)
        {
            auto const selected = BoolProperty(json, oldFlags[index]);
            if (!selected.value_or(false)) return std::string(firstMissingStages[index]);
        }
        return std::string("Completed");
    }

    auto const oldStage = IntProperty(json, "stage");
    auto const configurationStep = IntProperty(json, "configurationStep");
    if (!oldStage && !configurationStep) return {};
    auto const step = configurationStep.value_or(0);
    if (oldStage.value_or(0) <= 0) return std::string("SelectIndependentMode");
    if (*oldStage == 1)
    {
        if (step <= 0) return std::string("SelectIndependentMode");
        if (step == 1) return std::string("ExpandLineup");
        if (step == 2) return std::string("ConfigureFocus");
        if (step == 3) return std::string("ConfigureAgenda");
        if (step == 4) return std::string("ConfigureSkills");
        if (step == 5 || step == 6) return std::string("CollapseLineup");
        return std::string("StartTraining");
    }
    if (*oldStage == 2) return std::string("StartTraining");
    if (step >= 7) return std::string("Completed");
    return std::string("ConfigureStrategy");
}

std::optional<std::string> NormalizeCheckpointStage(std::string stage)
{
    auto const key = Key(stage);
    // Entry progress in the managed format is revalidated by the shared
    // CareerEntryFlow before Independent Training is called.
    static constexpr std::array<std::string_view, 7> entryStages{
        "entercareer", "handleexistingcareer", "selectscenario", "selecttrainee",
        "selectlegacy", "selectsupportdeck", "openfinalconfirmation"};
    if (std::find(entryStages.begin(), entryStages.end(), key) != entryStages.end())
        return std::string("SelectIndependentMode");
    static constexpr std::array<std::string_view, 11> stages{
        "selectindependentmode", "expandlineup", "configurefocus", "configureagenda", "configureskills",
        "collapselineup", "configurestrategy", "starttraining", "handlepoststartdialog", "returnhome", "completed"};
    auto const found = std::find(stages.begin(), stages.end(), key);
    if (found == stages.end()) return {};
    auto const normalized = std::string(*found);
    if (normalized == "selectindependentmode") return std::string("SelectIndependentMode");
    if (normalized == "expandlineup") return std::string("ExpandLineup");
    if (normalized == "configurefocus") return std::string("ConfigureFocus");
    if (normalized == "configureagenda") return std::string("ConfigureAgenda");
    if (normalized == "configureskills") return std::string("ConfigureSkills");
    if (normalized == "collapselineup") return std::string("CollapseLineup");
    if (normalized == "configurestrategy") return std::string("ConfigureStrategy");
    if (normalized == "starttraining") return std::string("StartTraining");
    if (normalized == "handlepoststartdialog") return std::string("HandlePostStartDialog");
    if (normalized == "returnhome") return std::string("ReturnHome");
    return std::string("Completed");
}

std::string Lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

std::string Fingerprint(Career::CareerRuntimeConfig const& config)
{
    // Stable FNV-1a over canonical setting JSON. It detects accidental resume
    // across changed settings; serial and packaged resource version are
    // compared separately and never trusted from checkpoint payload.
    Json input = {
        {"careerMode", config.careerMode}, {"traineeId", config.traineeId.value_or(0)},
        {"runCount", config.runCount}, {"normalLineupStrategy", config.normalLineupStrategy},
        {"normalTrainingRatio", {{"speed", config.normalTrainingRatio.speed}, {"stamina", config.normalTrainingRatio.stamina},
            {"power", config.normalTrainingRatio.power}, {"guts", config.normalTrainingRatio.guts}, {"wit", config.normalTrainingRatio.wit}}},
        {"retryFailedRaceWithAlarmClock", config.retryFailedRaceWithAlarmClock},
        {"normalEventHandling", config.normalEventHandling}, {"normalSkillIds", config.normalSkillIds},
        {"independentTrainingFocus", config.independentTrainingFocus},
        {"independentLineupStrategy", config.independentLineupStrategy},
        {"independentAgendaSelections", config.independentAgendaSelections},
        {"independentSkillIds", config.independentSkillIds},
        {"supportDeckMode", config.supportDeckMode}, {"supportDeckPreset", config.supportDeckPreset},
        {"friendSupportCardId", config.friendSupportCardId}, {"supportCardIds", config.supportCardIds},
        {"strategyId", config.strategyId}, {"pauseOnUnknownOutcome", config.pauseOnUnknownOutcome},
        {"allowOptionalRaces", config.allowOptionalRaces}, {"legacySelectionMode", config.legacySelectionMode},
        {"useLegacyGuest", config.useLegacyGuest}, {"useCachedLegacy", config.useCachedLegacy},
        {"legacyAttributeSparks", config.legacyAttributeSparks}, {"legacyAptitudeSparks", config.legacyAptitudeSparks},
    };
    auto const bytes = input.dump();
    std::uint64_t hash = 14695981039346656037ull;
    for (unsigned char byte : bytes) { hash ^= byte; hash *= 1099511628211ull; }
    std::ostringstream out;
    out << std::hex << std::setfill('0') << std::setw(16) << hash;
    return out.str();
}

std::filesystem::path CheckpointPath(Career::CareerRuntimeConfig const& config)
{
    if (config.checkpointDirectory.empty() || !config.traineeId) return {};
    return config.checkpointDirectory / ("independent-" + std::to_string(*config.traineeId) + ".json");
}

Json ToJson(Checkpoint const& state)
{
    Json result = {
        {"version", state.version}, {"stage", state.stage}, {"agendaIndex", state.agendaIndex},
        {"skillIndex", state.skillIndex}, {"lastConfirmedScreen", state.lastConfirmedScreen},
        {"completionVerified", state.completionVerified}, {"traineeId", state.traineeId},
        {"adbSerial", state.adbSerial}, {"configurationFingerprint", state.configurationFingerprint},
        {"resourceVersion", state.resourceVersion},
    };
    if (state.currentSkillId) result["currentSkillId"] = *state.currentSkillId;
    else result["currentSkillId"] = nullptr;
    return result;
}

bool SaveCheckpoint(Checkpoint& state, std::filesystem::path const& path, std::string& error)
{
    if (path.empty()) return true;
    try
    {
        std::filesystem::create_directories(path.parent_path());
        auto temporary = path;
        temporary += ".tmp";
        {
            std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
            if (!output) throw std::runtime_error("could not create temporary checkpoint");
            output << ToJson(state).dump(2);
            output.flush();
            if (!output) throw std::runtime_error("could not flush temporary checkpoint");
        }
#ifdef _WIN32
        if (!MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        {
            auto const errorCode = static_cast<int>(GetLastError());
            throw std::system_error(errorCode, std::system_category(), "replace checkpoint atomically");
        }
#else
        std::error_code moveError;
        std::filesystem::rename(temporary, path, moveError);
        if (moveError) throw std::filesystem::filesystem_error("replace checkpoint", temporary, path, moveError);
#endif
        return true;
    }
    catch (std::exception const& exception)
    {
        error = "Could not save Independent Training checkpoint: " + std::string(exception.what());
        return false;
    }
}

bool LoadCheckpoint(Career::CareerRuntimeConfig const& config, TaskEngine::ITaskExecutionContext& context,
    Checkpoint& state, std::string& error, bool resumeAfterRecovery = false)
{
    auto const path = CheckpointPath(config);
    if (path.empty()) return true;
    if (!context.GetDeviceSerial().empty()) state.adbSerial = context.GetDeviceSerial();
    if (state.adbSerial.empty())
    { error = "Independent checkpointing requires the connected device serial."; return false; }
    if (config.resourceVersion.empty())
    { error = "Independent checkpointing requires the packaged resource version."; return false; }
    state.traineeId = config.traineeId.value_or(0);
    state.configurationFingerprint = Fingerprint(config);
    state.resourceVersion = config.resourceVersion;

    if (!config.continueExistingCareer && !resumeAfterRecovery)
    {
        std::error_code removeError;
        std::filesystem::remove(path, removeError);
        if (removeError) { error = "Could not clear previous Independent checkpoint: " + removeError.message(); return false; }
        return true;
    }

    auto checkpointToLoad = path;
    std::error_code existsError;
    bool hasNewCheckpoint = std::filesystem::exists(path, existsError) && std::filesystem::is_regular_file(path, existsError);
    if (!hasNewCheckpoint)
    {
        auto legacyPath = path.parent_path() / ("ura-" + std::to_string(config.traineeId.value_or(0)) + ".json");
        existsError.clear();
        if (!std::filesystem::exists(legacyPath, existsError) || !std::filesystem::is_regular_file(legacyPath, existsError))
        {
            return true;
        }
        checkpointToLoad = std::move(legacyPath);
    }

    std::ifstream input(checkpointToLoad, std::ios::binary);
    if (!input) { error = "Could not open Independent checkpoint: " + Paths::ToUtf8(checkpointToLoad); return false; }
    try
    {
        Json json = Json::parse(input);
        if (!json.is_object()) throw std::runtime_error("checkpoint root must be an object");
        int const version = IntProperty(json, "version").value_or(1);
        if (version > kCheckpointVersion)
            throw std::runtime_error("checkpoint version " + std::to_string(version) + " is newer than supported version 2");
        bool hasIdentity = false;
        bool hasAllIdentity = true;
        for (auto const key : {"adbSerial", "traineeId", "configurationFingerprint", "resourceVersion"})
        {
            auto const* value = FindProperty(json, key);
            hasIdentity = hasIdentity || value != nullptr;
            hasAllIdentity = hasAllIdentity && value != nullptr;
        }
        if (hasIdentity && !hasAllIdentity)
            throw std::runtime_error("checkpoint has incomplete identity metadata");
        if (hasAllIdentity)
        {
            auto const storedSerial = StringProperty(json, "adbSerial");
            auto const storedTrainee = IntProperty(json, "traineeId");
            auto const storedFingerprint = StringProperty(json, "configurationFingerprint");
            auto const storedVersion = StringProperty(json, "resourceVersion");
            if (!storedSerial || !storedTrainee || !storedFingerprint || !storedVersion)
                throw std::runtime_error("checkpoint identity metadata has invalid types");
            if (*storedSerial != state.adbSerial) throw std::runtime_error("checkpoint belongs to a different connected device");
            if (*storedTrainee != state.traineeId) throw std::runtime_error("checkpoint belongs to a different trainee");
            if (*storedFingerprint != state.configurationFingerprint) throw std::runtime_error("checkpoint settings no longer match the submitted immutable snapshot");
            if (*storedVersion != state.resourceVersion) throw std::runtime_error("checkpoint belongs to a different packaged resource version");
        }

        auto stage = StringProperty(json, "stage");
        if (!stage) stage = MigrateLegacyStage(json);
        if (!stage) throw std::runtime_error("checkpoint has no recognized stage or legacy progress flags");
        auto normalizedStage = NormalizeCheckpointStage(*stage);
        if (!normalizedStage) throw std::runtime_error("checkpoint has an unknown Independent Training stage");
        state.version = kCheckpointVersion;
        state.stage = std::move(*normalizedStage);
        state.agendaIndex = std::max(0, IntProperty(json, "agendaIndex").value_or(0));
        state.skillIndex = std::max(0, IntProperty(json, "skillIndex").value_or(0));
        state.lastConfirmedScreen = StringProperty(json, "lastConfirmedScreen").value_or(
            StringProperty(json, "lastScreenId").value_or("unknown"));
        state.completionVerified = BoolProperty(json, "completionVerified").value_or(false);
        if (auto const skill = IntProperty(json, "currentSkillId")) state.currentSkillId = *skill;
        else state.currentSkillId.reset();

        if (state.stage == "ConfigureAgenda")
        {
            state.agendaIndex = std::min(state.agendaIndex, static_cast<int>(config.independentAgendaSelections.size()));
        }
        else if (state.stage == "ConfigureSkills")
        {
            state.skillIndex = std::min(state.skillIndex, static_cast<int>(config.independentSkillIds.size()));
            if (state.currentSkillId && (state.skillIndex >= static_cast<int>(config.independentSkillIds.size())
                || config.independentSkillIds[static_cast<std::size_t>(state.skillIndex)] != *state.currentSkillId))
                throw std::runtime_error("checkpoint current skill does not match the submitted skill settings");
        }
        else
        {
            state.currentSkillId.reset();
        }
        if (state.lastConfirmedScreen.empty()) state.lastConfirmedScreen = "unknown";
        return true;
    }
    catch (std::exception const& exception)
    {
        error = "Could not safely resume Independent Training checkpoint: " + std::string(exception.what());
        return false;
    }
}

bool Execute(CareerEntryResult& result, Career::CareerVisualResources& resources,
    TaskEngine::ITaskExecutionContext& context, std::string const& semantic,
    TaskEngine::TaskGraphRunOptions const& options = {})
{
    if (context.IsCancelled()) { result.cancelled = true; return false; }
    auto const action = resources.RunAction("career_final_confirmation", semantic, context, options);
    if (action.status == TaskEngine::TaskGraphStatus::Cancelled || context.IsCancelled())
    { result.cancelled = true; return false; }
    if (action.status != TaskEngine::TaskGraphStatus::Completed && action.status != TaskEngine::TaskGraphStatus::Success)
    {
        result.error = action.errorMessage.empty() ? "Independent Training action failed: " + semantic : action.errorMessage;
        return false;
    }
    ++result.actionsCompleted;
    return true;
}

bool TemplateVisible(Career::CareerRuntimeConfig const& config, TaskEngine::ITaskExecutionContext& context,
    std::string const& relativePath, TaskEngine::TaskRoi roi, double threshold)
{
    auto const path = config.resourceDirectory / "hachimi" / "career" / relativePath;
    if (!std::filesystem::is_regular_file(path) || !context.CaptureScreen()) return false;
    context.SetCoordinateReference(900, 1600);
    return context.MatchTemplateAdvanced(Paths::ToUtf8(path), threshold, roi,
        "MatchTemplate", 900, 1600, {}).found;
}

std::string Slug(std::string value)
{
    value = Lower(std::move(value));
    for (char& ch : value) if (!std::isalnum(static_cast<unsigned char>(ch))) ch = '_';
    while (value.find("__") != std::string::npos) value.replace(value.find("__"), 2, "_");
    while (!value.empty() && value.front() == '_') value.erase(value.begin());
    while (!value.empty() && value.back() == '_') value.pop_back();
    return value;
}

struct AgendaSelection
{
    std::string year;
    std::string turn;
    std::string race;
};

bool HasUniqueRacePickerHeader(GameData::CareerSelectionCatalog const& catalog,
    GameData::UmaIndependentRaceRecord const& race)
{
    if (race.pickerHeaderTarget.empty()) return false;
    auto const key = Lower(race.pickerHeaderTarget);
    return std::none_of(catalog.Races().begin(), catalog.Races().end(), [&](auto const& other) {
        return other.gameAvailable && other.raceId != race.raceId
            && Lower(other.year) == Lower(race.year) && Lower(other.turn) == Lower(race.turn)
            && Lower(other.pickerHeaderTarget) == key;
    });
}

bool ParseAgenda(std::string const& value, AgendaSelection& selection)
{
    auto const first = value.find('|');
    auto const second = first == std::string::npos ? first : value.find('|', first + 1);
    if (first == std::string::npos || second == std::string::npos || first == 0 || second == first + 1 || second + 1 >= value.size()) return false;
    selection = {value.substr(0, first), value.substr(first + 1, second - first - 1), value.substr(second + 1)};
    return true;
}

void StoreLastScreen(Checkpoint& state, std::string const& screen)
{
    state.lastConfirmedScreen = screen.empty() ? "unknown" : screen;
}

bool Persist(Checkpoint& state, std::filesystem::path const& path, CareerEntryResult& result)
{
    if (SaveCheckpoint(state, path, result.error)) return true;
    return false;
}

} // namespace

void NativeIndependentTrainingFlow::RegisterActions(Career::CareerRuntimeConfig const&,
    GameData::CareerSelectionCatalog const&, TaskEngine::ActionRegistry&)
{
    // All visual work is intentionally delegated to manifest-owned action
    // graphs, so the independent flow registers no coordinate action handler.
}

bool NativeIndependentTrainingFlow::NeedsEntry(Career::CareerRuntimeConfig const& config,
    TaskEngine::ITaskExecutionContext& context, bool& needsEntry, std::string& error,
    bool resumeAfterRecovery)
{
    error.clear();
    needsEntry = true;
    if (config.careerMode != "independent" || (!config.continueExistingCareer && !resumeAfterRecovery)
        || CheckpointPath(config).empty())
        return true;

    Checkpoint state;
    if (!LoadCheckpoint(config, context, state, error, resumeAfterRecovery)) return false;
    if (state.stage == "Completed")
    {
        // Completion is historical data, not proof of the currently visible
        // Home screen; a new run must use the shared entry path.
        needsEntry = true;
        return true;
    }
    needsEntry = !(state.stage == "HandlePostStartDialog" || state.stage == "ReturnHome"
        || (state.stage == "StartTraining" && state.lastConfirmedScreen == kStartIssued));
    return true;
}

CareerEntryResult NativeIndependentTrainingFlow::Run(Career::CareerRuntimeConfig const& config,
    Career::CareerVisualResources& resources, Career::ICareerObserver& observer,
    TaskEngine::ITaskExecutionContext& context, bool resumeAfterRecovery)
{
    CareerEntryResult result;
    if (config.careerMode != "independent") { result.error = "Independent flow received non-independent settings."; return result; }
    GameData::CareerSelectionCatalog catalog(config.resourceDirectory);
    std::string catalogError;
    if (!catalog.Load(catalogError)) { result.error = catalogError; return result; }

    Checkpoint state;
    auto const checkpointPath = CheckpointPath(config);
    std::string checkpointError;
    if (!LoadCheckpoint(config, context, state, checkpointError, resumeAfterRecovery))
    { result.error = std::move(checkpointError); return result; }
    if (state.stage == "Completed")
    {
        auto const serial = state.adbSerial;
        auto const trainee = state.traineeId;
        auto const fingerprint = state.configurationFingerprint;
        auto const resource = state.resourceVersion;
        state = Checkpoint{};
        state.adbSerial = serial;
        state.traineeId = trainee;
        state.configurationFingerprint = fingerprint;
        state.resourceVersion = resource;
    }
    if (!checkpointPath.empty() && (!Persist(state, checkpointPath, result))) return result;

    auto save = [&]() { return checkpointPath.empty() || Persist(state, checkpointPath, result); };
    auto run = [&](std::string const& semantic, TaskEngine::TaskGraphRunOptions const& options = {}) {
        return Execute(result, resources, context, semantic, options);
    };
    auto stageIs = [&](std::string_view stage) { return state.stage == stage; };

    auto const observed = observer.Observe(context);
    result.lastScreenId = observed.screenId;
    if (context.IsCancelled()) { result.cancelled = true; return result; }
    // The root-owned shared entry flow runs immediately before this function.
    // It must have delivered final confirmation, unless a saved Start marker
    // proves the independent run already entered its post-start route.
    if (stageIs("StartTraining") && state.lastConfirmedScreen == kStartIssued)
        state.stage = "HandlePostStartDialog";
    else if (stageIs("StartTraining") && observed.screenId != "career_final_confirmation")
    { result.error = "Independent Start has no durable submission marker and the screen is not Final Confirmation; refusing to guess whether Start was tapped."; return result; }
    else if (!stageIs("HandlePostStartDialog") && !stageIs("ReturnHome")
        && observed.screenId != "career_final_confirmation")
    {
        result.error = "Independent setup requires a verified career_final_confirmation screen; observed " + observed.screenId + ".";
        return result;
    }

    if (stageIs("SelectIndependentMode"))
    {
        if (!run("independent.select_mode")) return result;
        state.stage = "ExpandLineup"; StoreLastScreen(state, "career_final_confirmation"); if (!save()) return result;
    }
    if (stageIs("ExpandLineup"))
    {
        if (!run("independent.lineup.expand") || !run("independent.lineup.scroll.top")) return result;
        state.stage = "ConfigureFocus"; StoreLastScreen(state, "career_final_confirmation"); if (!save()) return result;
    }
    if (stageIs("ConfigureFocus"))
    {
        auto const focus = Lower(config.independentTrainingFocus);
        std::string const semantic = focus == "balanced" ? "independent.focus.balanced"
            : focus == "stamina" ? "independent.focus.stamina" : focus == "sprint" ? "independent.focus.sprint" : "";
        if (semantic.empty()) { result.error = "Unsupported Independent Training focus: " + config.independentTrainingFocus; return result; }
        if (!run(semantic)) return result;
        state.stage = "ConfigureAgenda"; StoreLastScreen(state, "career_final_confirmation"); state.agendaIndex = 0; if (!save()) return result;
    }
    if (stageIs("ConfigureAgenda"))
    {
        if (config.independentAgendaSelections.empty())
        {
            if (!run("independent.agenda.open") || !run("independent.agenda.close")) return result;
            state.stage = "ConfigureSkills"; state.skillIndex = 0; state.lastConfirmedScreen = "agenda-empty-closed"; if (!save()) return result;
        }
        else
        {
            bool const prepared = state.lastConfirmedScreen == "agenda-reset-complete"
                || state.lastConfirmedScreen.rfind("agenda-saved-", 0) == 0
                || state.lastConfirmedScreen.rfind("agenda-selected-", 0) == 0;
            if (!prepared)
            {
                if (!run("independent.agenda.open") || !run("independent.agenda.reset")
                    || !run("independent.agenda.reset.confirm") || !run("independent.agenda.reset.done")) return result;
                state.lastConfirmedScreen = "agenda-reset-complete";
                if (!save()) return result;
            }
            if (state.lastConfirmedScreen.rfind("agenda-selected-", 0) == 0)
            {
                if (!run("independent.agenda.save")) return result;
                state.agendaIndex = std::max(state.agendaIndex, 1 + std::stoi(state.lastConfirmedScreen.substr(16)));
                state.lastConfirmedScreen = "agenda-saved-" + std::to_string(state.agendaIndex);
                if (!save()) return result;
            }
            for (std::size_t index = static_cast<std::size_t>(state.agendaIndex); index < config.independentAgendaSelections.size(); ++index)
            {
                AgendaSelection selection;
                if (!ParseAgenda(config.independentAgendaSelections[index], selection))
                { result.error = "Independent agenda setting is not a year|turn|race key: " + config.independentAgendaSelections[index]; return result; }
                auto const* race = catalog.TryGetRace(selection.year, selection.turn, selection.race);
                if (!race) { result.error = "Independent agenda race is not available in Global Race catalog: " + selection.race; return result; }
                auto const yearSlug = Slug(selection.year);
                auto const yearAction = "independent.agenda.year." + yearSlug;
                auto const slotAction = "independent.agenda.slot." + yearSlug + "." + Slug(selection.turn);
                if (!run(yearAction) || !run(slotAction)) return result;
                bool selected = false;
                if (HasUniqueRacePickerHeader(catalog, *race))
                {
                    TaskEngine::TaskGraphRunOptions ocr;
                    ocr.targetTextOverrides["independent_agenda_race_find"] = race->pickerHeaderTarget;
                    ocr.targetTextOverrides["independent_agenda_race_verify"] = race->pickerHeaderTarget;
                    if (run("independent.agenda.race", ocr))
                    {
                        if (!run("independent.agenda.race.verify", ocr)) return result;
                        selected = true;
                    }
                    else result.error.clear(); // OCR recognition miss may use the declared race-ID card below.
                }
                if (!selected)
                {
                    auto const path = config.resourceDirectory / "hachimi" / "career" / "modes" / "independent"
                        / "templates" / "independent" / "race_cards" / (std::to_string(race->raceId) + ".png");
                    if (!std::filesystem::is_regular_file(path))
                    { result.error = "Independent agenda race has no verified OCR header or declared Race ID template: " + selection.race; return result; }
                    if (!run("independent.agenda.race.scroll.top")) return result;
                    TaskEngine::TaskGraphRunOptions card;
                    card.templateOverrides["independent_agenda_race_card_find"] = Paths::ToUtf8(path);
                    card.templateOverrides["independent_agenda_race_card_verify"] = Paths::ToUtf8(path);
                    if (!run("independent.agenda.race.card", card) || !run("independent.agenda.race.card.verify", card)) return result;
                }
                state.lastConfirmedScreen = "agenda-selected-" + std::to_string(index);
                if (!save()) return result;
                if (!run("independent.agenda.save")) return result;
                state.agendaIndex = static_cast<int>(index + 1);
                state.lastConfirmedScreen = "agenda-saved-" + std::to_string(index);
                if (!save()) return result;
            }
            if (!run("independent.agenda.close")) return result;
            state.stage = "ConfigureSkills"; state.skillIndex = 0;
            state.lastConfirmedScreen = "agenda-closed"; if (!save()) return result;
        }
    }
    if (stageIs("ConfigureSkills"))
    {
        auto const pendingPrefix = std::string("skill-pending-");
        if (state.lastConfirmedScreen.rfind(pendingPrefix, 0) == 0)
        {
            result.error = "Skill selection was interrupted after its identity checkpoint. It was not repeated to avoid toggling the wrong skill; restart Independent setup to reconcile it.";
            return result;
        }
        if (state.lastConfirmedScreen.rfind("skill-selected-", 0) == 0)
        {
            auto const recoveredId = state.currentSkillId.value_or(0);
            if (recoveredId <= 0 || state.skillIndex < 0
                || state.skillIndex >= static_cast<int>(config.independentSkillIds.size())
                || config.independentSkillIds[static_cast<std::size_t>(state.skillIndex)] != recoveredId)
            { result.error = "Interrupted skill checkpoint does not match the submitted skill list."; return result; }
            if (!run("independent.skills.save")) return result;
            state.skillIndex++;
            state.currentSkillId.reset();
            state.lastConfirmedScreen = "skill-saved-" + std::to_string(state.skillIndex - 1);
            if (!save()) return result;
        }
        const bool prepared = state.lastConfirmedScreen == "skills-reset-complete"
            || state.lastConfirmedScreen.rfind("skill-saved-", 0) == 0
            || state.lastConfirmedScreen.rfind("skill-searching-", 0) == 0;
        if (!prepared)
        {
            if (!run("independent.skills.scroll") || !run("independent.skills.reset")) return result;
            state.lastConfirmedScreen = "skills-reset-complete";
            state.skillIndex = 0; state.currentSkillId.reset();
            if (!save()) return result;
        }
        if (state.skillIndex > static_cast<int>(config.independentSkillIds.size()))
        { result.error = "Independent checkpoint skill cursor exceeds submitted skill settings."; return result; }
        for (std::size_t index = static_cast<std::size_t>(state.skillIndex); index < config.independentSkillIds.size(); ++index)
        {
            auto const id = config.independentSkillIds[index];
            auto const* skill = catalog.TryGetSkill(id);
            if (!skill || !skill->singleModeEnabled || !skill->availableInGlobal || skill->skillName.empty())
            { result.error = "Independent skill is not enabled for Global single mode: " + std::to_string(id); return result; }
            state.currentSkillId = id; state.skillIndex = static_cast<int>(index);
            state.lastConfirmedScreen = "skill-searching-" + std::to_string(id);
            if (!save()) return result;
            if (!run("independent.skills.open") || !run("independent.skills.search.reset")
                || !run("independent.skills.search.focus")) return result;
            TaskEngine::TaskGraphRunOptions input;
            input.inputTextOverrides["independent_skills_search_input"] = skill->searchText.empty() ? skill->skillName : skill->searchText;
            if (!run("independent.skills.search.input", input) || !run("independent.skills.search.submit")) return result;
            if (skill->gameSearchMapped && skill->searchResultRow > 0)
                for (int page = 0; page < skill->searchResultPage; ++page) if (!run("independent.skills.search.scroll")) return result;
            state.lastConfirmedScreen = pendingPrefix + std::to_string(id);
            if (!save()) return result;
            TaskEngine::TaskGraphRunOptions byText;
            byText.targetTextOverrides["independent_skills_search_checkbox_ocr"] = skill->skillName;
            if (!run("independent.skills.search.checkbox", byText))
            {
                if (result.cancelled) return result;
                if (!skill->gameSearchMapped || skill->searchResultRow <= 0)
                { result.error = "Independent skill has no verified Global picker fallback row: " + skill->skillName; return result; }
                result.error.clear();
                auto const row = std::clamp(skill->searchResultPickerRow, 0, 7);
                TaskEngine::TaskGraphRunOptions fallback;
                fallback.roiOverrides["independent_skills_search_checkbox"] = {20, 120 + row * 140, 150, 220};
                if (!run("independent.skills.search.checkbox.fallback", fallback)) return result;
            }
            state.lastConfirmedScreen = "skill-selected-" + std::to_string(id);
            if (!save()) return result;
            if (!run("independent.skills.save")) return result;
            state.skillIndex = static_cast<int>(index + 1); state.currentSkillId.reset();
            state.lastConfirmedScreen = "skill-saved-" + std::to_string(index);
            if (!save()) return result;
        }
        state.stage = "CollapseLineup"; state.lastConfirmedScreen = "skills-configured"; if (!save()) return result;
    }
    if (stageIs("CollapseLineup"))
    {
        if (!run("independent.lineup.scroll.top") || !run("independent.lineup.collapse")) return result;
        state.stage = "ConfigureStrategy"; state.lastConfirmedScreen = "lineup-collapsed"; if (!save()) return result;
    }
    if (stageIs("ConfigureStrategy"))
    {
        auto const strategy = Lower(config.independentLineupStrategy);
        if (strategy != "front" && strategy != "pace" && strategy != "late" && strategy != "end")
        { result.error = "Unsupported Independent lineup strategy: " + config.independentLineupStrategy; return result; }
        if (!run("independent.lineup.closed.verify") || !run("independent.strategy.change")
            || !run("independent.strategy.option." + strategy) || !run("independent.strategy.save")
            || !run("independent.strategy.return")) return result;
        state.stage = "StartTraining"; state.lastConfirmedScreen = "career_final_confirmation"; if (!save()) return result;
    }
    if (stageIs("StartTraining"))
    {
        // Persist a pre-commit marker before issuing Start. A crash or device
        // interruption after the tap never causes an automatic second Start.
        state.lastConfirmedScreen = std::string(kStartIssued);
        if (!save()) return result;
        if (!run("independent.start")) return result;
        state.stage = "HandlePostStartDialog";
        if (!save()) return result;
    }
    if (stageIs("HandlePostStartDialog"))
    {
        auto optionalAction = [&](std::string const& assetId, std::string const& semantic) {
            (void)assetId;
            std::string path;
            TaskEngine::TaskRoi roi{};
            double threshold = 0.82;
            if (semantic == "independent.post_start.ok")
            { path = "modes/normal/templates/support_autofill_confirmation_support_autofill_ok.png"; roi = {430, 850, 440, 400}; threshold = 0.78; }
            else if (semantic == "independent.post_start.menu")
            { path = "modes/independent/templates/independent/independent_post_start_menu.png"; roi = {700, 1400, 200, 200}; }
            else
            { path = "modes/independent/templates/independent/independent_post_start_to_home.png"; roi = {40, 600, 410, 200}; }
            if (!TemplateVisible(config, context, path, roi, threshold)) return true;
            if (!run(semantic)) return false;
            state.lastConfirmedScreen = std::string(kStartIssued);
            return save();
        };
        if (!optionalAction("independent.post_start.ok", "independent.post_start.ok")
            || !optionalAction("independent.post_start.menu", "independent.post_start.menu")
            || !optionalAction("independent.post_start.to_home", "independent.post_start.to_home")) return result;
        state.stage = "ReturnHome"; state.lastConfirmedScreen = std::string(kStartIssued); if (!save()) return result;
    }
    if (stageIs("ReturnHome"))
    {
        if (!run("independent.post_start.home_probe")) return result;
        // The `home_probe` task itself is a JustReturn check of the packaged
        // training_independently_available marker and can only report success
        // after that Home-specific evidence is matched.
        state.completionVerified = true; state.stage = "Completed"; state.lastConfirmedScreen = "home";
        if (!save()) return result;
        result.succeeded = true;
        result.lastScreenId = "home";
        return result;
    }
    result.error = "Independent Training ended without a verified Home completion state.";
    return result;
}

} // namespace UmaAssistant::GameTasks
