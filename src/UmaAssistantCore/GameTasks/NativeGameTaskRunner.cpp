#include "NativeGameTaskRunner.hpp"
#include "NativeCareerTaskRunner.hpp"

#include "DailyRaceRunnerSelector.hpp"
#include "GameTaskActionExtensions.hpp"
#include "Common/PathEncoding.hpp"
#include "Resources/ResourceSnapshot.hpp"
#include "ShopItemSelector.hpp"
#include "StartGameMonitor.hpp"
#include "TaskEngine/HachimiPipelineLoader.hpp"
#include "TaskEngine/TaskGraphRunner.hpp"
#include "TaskEngine/TaskContextInterruption.hpp"
#include "UmaAssistant/Connection.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <chrono>
#include <exception>
#include <filesystem>
#include <optional>
#include <set>
#include <string>
#include <utility>
#include <vector>

namespace UmaAssistant::GameTasks
{

namespace
{

using Json = nlohmann::json;
using namespace TaskEngine;

constexpr int kInvalidArgument = static_cast<int>(ConnectionErrorCode::InvalidArgument);
constexpr int kCommandFailed = static_cast<int>(ConnectionErrorCode::CommandFailed);
constexpr int kCanceled = static_cast<int>(ConnectionErrorCode::Canceled);
constexpr int kDeviceDisconnected = static_cast<int>(ConnectionErrorCode::DeviceDisconnected);
constexpr int kDeviceUnauthorized = static_cast<int>(ConnectionErrorCode::DeviceUnauthorized);
constexpr int kDeviceOffline = static_cast<int>(ConnectionErrorCode::DeviceOffline);
constexpr int kDeviceUnavailable = static_cast<int>(ConnectionErrorCode::DeviceUnavailable);

bool IsTaskId(std::string_view actual, std::string_view expected)
{
    return CaseInsensitiveEqual{}(std::string(actual), std::string(expected));
}

std::optional<std::string> GetString(Json const& value, char const* key)
{
    auto const it = value.find(key);
    if (it == value.end() || it->is_null()) return std::nullopt;
    if (!it->is_string()) throw std::invalid_argument(std::string("Settings field '") + key + "' must be a string.");
    return it->get<std::string>();
}

int GetInt(Json const& value, char const* key, int defaultValue)
{
    auto const it = value.find(key);
    if (it == value.end() || it->is_null()) return defaultValue;
    if (!it->is_number_integer()) throw std::invalid_argument(std::string("Settings field '") + key + "' must be an integer.");
    return it->get<int>();
}

bool GetBool(Json const& value, char const* key, bool defaultValue)
{
    auto const it = value.find(key);
    if (it == value.end() || it->is_null()) return defaultValue;
    if (!it->is_boolean()) throw std::invalid_argument(std::string("Settings field '") + key + "' must be a boolean.");
    return it->get<bool>();
}

std::optional<std::filesystem::path> DefinitionPath(Json const& settings)
{
    auto const path = GetString(settings, "definitionPath");
    if (!path || path->empty()) return std::nullopt;
    auto value = Paths::FromUtf8(*path);
    if (!value.is_absolute()) return std::nullopt;
    return value.lexically_normal();
}

ShopPurchaseOptions ParseShop(Json const& settings)
{
    ShopPurchaseOptions options;
    auto const shop = settings.find("shop");
    if (shop == settings.end() || shop->is_null()) return options;
    if (!shop->is_object()) throw std::invalid_argument("Settings field 'shop' must be an object.");
    options.enabled = GetBool(*shop, "enabled", true);
    options.selectAll = GetBool(*shop, "selectAll", false);
    options.buyStarPieces = GetBool(*shop, "buyStarPieces", false);
    options.buyAlarmClock = GetBool(*shop, "buyAlarmClock", false);
    options.buyPleasingParfait = GetBool(*shop, "buyPleasingParfait", false);
    options.buyShoes = GetBool(*shop, "buyShoes", false);
    options.buySupportPoints = GetBool(*shop, "buySupportPoints", false);
    options.buyFlags = GetBool(*shop, "buyFlags", false);
    return options;
}

using RecoveryInterruption = TaskEngine::TaskContextInterruption;

struct RecoveryState
{
    bool pendingShop{false};
    bool confirmedShop{false};
    int pendingRaceUnits{0};
    int confirmedRaceUnits{0};
};

bool IsOneOf(std::string const& value, std::initializer_list<std::string_view> candidates)
{
    return std::any_of(candidates.begin(), candidates.end(), [&value](std::string_view candidate) {
        return CaseInsensitiveEqual{}(value, std::string(candidate));
    });
}

class GuardedTaskContext final : public ITaskExecutionContext
{
public:
    GuardedTaskContext(
        ITaskExecutionContext& inner,
        Resources::ResourceSnapshot const* resources,
        std::filesystem::path resourceRoot,
        IGameTaskPackageOperations* packageOperations,
        std::string packageId,
        std::string activityName,
        std::string rootPipelineName,
        RecoveryState& state,
        int requestedRaceUnits)
        : m_inner(inner), m_resources(resources), m_resourceRoot(std::move(resourceRoot)),
          m_packageOperations(packageOperations), m_packageId(std::move(packageId)),
          m_activityName(std::move(activityName)), m_rootPipelineName(std::move(rootPipelineName)), m_state(state),
          m_requestedRaceUnits(requestedRaceUnits)
    {
    }

    bool CaptureScreen() override
    {
        if (!m_inner.CaptureScreen()) return false;
        auto first = DetectDialog(m_inner);
        if (first.kind == DialogKind::none) return true;
        if (first.kind == DialogKind::missingTemplates)
            throw RecoveryInterruption("Date-change recovery references could not be loaded.");

        m_inner.Sleep(120);
        if (!m_inner.CaptureScreen())
            throw RecoveryInterruption("Could not confirm a possible game recovery dialog.");
        auto confirmed = DetectDialog(m_inner);
        if (confirmed.kind != first.kind)
            return true;

        // A graph retry lease belongs to the interrupted screen. Release it
        // before dismissing the confirmed dialog on its current frame.
        m_inner.ReleaseRetainedMatch();
        if (!DismissDialog(confirmed.match))
            throw RecoveryInterruption("The recovery dialog did not disappear within ten seconds (at most three taps).");

        if (m_recoveringStartup)
            return true;

        if (!RecoverToHome())
            throw RecoveryInterruption(m_recoveryError.empty()
                ? "Game recovery could not restore Home." : m_recoveryError);
        throw RecoveryInterruption("A recovery dialog interrupted the task; Home was restored.", true);
    }

    TaskMatchResult MatchTemplate(
        const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi = std::nullopt,
        const std::string& algorithm = "MatchTemplate") override
    {
        return m_inner.MatchTemplate(path, threshold, roi, algorithm);
    }

    TaskMatchResult MatchTemplateAdvanced(
        const std::string& path, double threshold, const std::optional<TaskRoi>& roi,
        const std::string& algorithm, int referenceWidth, int referenceHeight,
        const std::vector<double>& scales) override
    {
        return m_inner.MatchTemplateAdvanced(path, threshold, roi, algorithm,
            referenceWidth, referenceHeight, scales);
    }

    TaskTemplateCandidatesResult MatchTemplateCandidates(
        const std::vector<std::string>& paths, double threshold,
        const std::optional<TaskRoi>& roi, const std::string& algorithm,
        int referenceWidth, int referenceHeight, const std::vector<double>& scales,
        double minimumScoreGap) override
    {
        return m_inner.MatchTemplateCandidates(paths, threshold, roi, algorithm,
            referenceWidth, referenceHeight, scales, minimumScoreGap);
    }

    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return m_inner.GetCurrentFrame(); }
    std::string GetDeviceSerial() const override { return m_inner.GetDeviceSerial(); }
    std::shared_ptr<const TaskFrameSnapshot> GetPreviousFrame() const override { return m_inner.GetPreviousFrame(); }
    std::shared_ptr<const Vision::GrayImage> LoadTemplateImage(const std::string& path) override
    { return m_inner.LoadTemplateImage(path); }
    void SetCoordinateReference(int width, int height) override { m_inner.SetCoordinateReference(width, height); }
    std::pair<int, int> GetCoordinateReference() const override { return m_inner.GetCoordinateReference(); }
    std::string GetTemplateBaseDirectory() const override { return m_inner.GetTemplateBaseDirectory(); }
    void SetTemplateBaseDirectory(const std::string& directory) override { m_inner.SetTemplateBaseDirectory(directory); }
    int GetLastErrorCode() const override { return m_inner.GetLastErrorCode(); }
    bool CheckImageChange(const TaskMatchResult& match) override { return m_inner.CheckImageChange(match); }
    std::vector<TaskOcrDetection> RecognizeText(
        const std::string& language, const std::optional<TaskRoi>& roi = std::nullopt) override
    {
        return m_inner.RecognizeText(language, roi);
    }
    std::vector<TaskOcrDetection> RecognizeImageText(const Vision::GrayImage& image, const std::string& language) override
    { return m_inner.RecognizeImageText(image, language); }
    bool PrepareHeldTouch() override { return m_inner.PrepareHeldTouch(); }
    bool TouchDownMatch(int x, int y, const TaskMatchResult& source) override
    {
        auto const frame = m_inner.GetCurrentFrame();
        if (!frame || !source.found || source.frameId != frame->frameId
            || source.sessionGeneration != frame->sessionGeneration || source.geometryGeneration != frame->geometryGeneration)
            return false;
        // Held input starts on the exact frame checked by CaptureScreen. An
        // extra capture here would invalidate the matched control provenance.
        if (!CheckBeforeInput(false)) return false;
        return m_inner.TouchDownMatch(x, y, source);
    }
    bool TouchUp() override { return m_inner.TouchUp(); }
    bool TouchCancel() override { return m_inner.TouchCancel(); }
    bool ProbeHsv(int x, int y, int radius, double hMin, double hMax,
        double sMin, double sMax, double vMin, double vMax, double minRatio) override
    {
        return m_inner.ProbeHsv(x, y, radius, hMin, hMax, sMin, sMax, vMin, vMax, minRatio);
    }

    bool TapMatch(int x, int y, const TaskMatchResult& match) override
    {
        // Coordinate-only actions (for example a configured ClickSpecificRect)
        // have no recognition provenance to retain. Check the current screen
        // and let NativeTaskExecutionContext map the reference point there.
        if (!match.found || match.frameId == 0)
        {
            if (!CheckBeforeInput()) return false;
            MarkActionAttemptPending();
            return m_inner.TapMatch(x, y, match);
        }

        auto const ownsLease = !m_inner.IsMatchRetained(match);
        if (ownsLease && !m_inner.RetainMatchForRetry(match, 1, 30000, x, y))
            return false;
        try
        {
            if (!CheckBeforeInput())
            {
                if (ownsLease) m_inner.ReleaseRetainedMatch();
                return false;
            }
            MarkActionAttemptPending();
            auto const tapped = m_inner.TapMatch(x, y, match);
            if (ownsLease) m_inner.ReleaseRetainedMatch();
            return tapped;
        }
        catch (...)
        {
            if (ownsLease) m_inner.ReleaseRetainedMatch();
            throw;
        }
    }

    bool HasRetainedMatch() const override { return m_inner.HasRetainedMatch(); }
    bool IsMatchRetained(const TaskMatchResult& match) const override { return m_inner.IsMatchRetained(match); }
    bool RetainMatchForRetry(const TaskMatchResult& match, int maxTapAttempts = 1,
        int lifetimeMilliseconds = 30000, int fixedReferenceX = -1, int fixedReferenceY = -1) override
    {
        return m_inner.RetainMatchForRetry(match, maxTapAttempts, lifetimeMilliseconds,
            fixedReferenceX, fixedReferenceY);
    }
    void ReleaseRetainedMatch() override { m_inner.ReleaseRetainedMatch(); }

    bool Tap(int x, int y) override
    {
        if (!CheckBeforeInput()) return false;
        MarkActionAttemptPending();
        return m_inner.Tap(x, y);
    }

    bool Swipe(int x1, int y1, int x2, int y2, int durationMs) override
    {
        if (!CheckBeforeInput()) return false;
        MarkActionAttemptPending();
        return m_inner.Swipe(x1, y1, x2, y2, durationMs);
    }

    bool InputText(const std::string& text) override
    {
        if (!CheckBeforeInput()) return false;
        MarkActionAttemptPending();
        return m_inner.InputText(text);
    }

    bool SendKey(const std::string& keyCode) override
    {
        if (!CheckBeforeInput()) return false;
        MarkActionAttemptPending();
        return m_inner.SendKey(keyCode);
    }

    void Sleep(int milliseconds) override { m_inner.Sleep(milliseconds); }
    bool IsCancelled() const override { return m_inner.IsCancelled(); }
    void Log(const std::string& level, const std::string& message) override { m_inner.Log(level, message); }

    void EmitEvent(const TaskExecutionEvent& event) override
    {
        if (event.type == TaskEventType::ActionExecuting)
            m_currentActionName = event.taskName;
        if (event.type == TaskEventType::TaskCompleted)
        {
            if (IsOneOf(event.taskName, {"shopExchangeComplete"}))
            {
                m_state.pendingShop = false;
                m_state.confirmedShop = true;
            }
            if (IsOneOf(event.taskName, {"next", "MiddleNext", "nexttwo", "raceagain",
                    "teamRaceAfterShop", "finalNext", "multiRaceComplete", "previewNext",
                    "racePlaybackResult", "finalNextSupport"}) && m_state.pendingRaceUnits > 0)
            {
                m_state.confirmedRaceUnits += m_state.pendingRaceUnits;
                m_state.pendingRaceUnits = 0;
            }
            m_currentActionName.clear();
        }
        else if (event.type == TaskEventType::TaskFailed)
            m_currentActionName.clear();
        if (CaseInsensitiveEqual{}(m_rootPipelineName, "career-training"))
        {
            m_inner.EmitEvent(event);
            return;
        }
        auto enriched = event;
        auto const total = m_requestedRaceUnits > 0 ? m_requestedRaceUnits : 1;
        enriched.totalUnits = total;
        enriched.completedUnits = (std::clamp)(
            (std::max)(m_state.confirmedRaceUnits, event.completedUnits), 0, total);
        if (event.type == TaskEventType::PipelineCompleted
            && CaseInsensitiveEqual{}(event.pipelineName, m_rootPipelineName))
        {
            if (m_requestedRaceUnits <= 0 || m_state.pendingRaceUnits == 0)
                enriched.completedUnits = total;
        }
        m_inner.EmitEvent(enriched);
    }

    [[nodiscard]] bool HasAmbiguousPendingWork() const noexcept
    {
        return m_state.pendingShop || m_state.pendingRaceUnits > 0;
    }

private:
    void MarkActionAttemptPending()
    {
        if (m_recoveringStartup) return;
        if (IsOneOf(m_currentActionName, {"shopExchangeConfirm"}))
            m_state.pendingShop = true;
        if (IsOneOf(m_currentActionName, {"itemRace", "itemsRace"}))
            m_state.pendingRaceUnits = 1;
        if (IsOneOf(m_currentActionName, {"multiRaceTicketConfirm"}))
            m_state.pendingRaceUnits = (std::max)(
                1, m_requestedRaceUnits - m_state.confirmedRaceUnits);
    }

    enum class DialogKind { none, dateChanged, connectionError, missingTemplates };
    struct DialogMatch
    {
        DialogKind kind{DialogKind::none};
        TaskMatchResult match;
    };

    DialogMatch DetectDialog(ITaskExecutionContext& context)
    {
        if (m_resources == nullptr) return {DialogKind::missingTemplates, {}};
        auto const base = m_resourceRoot / "hachimi" / "common" / "date_changed";
        auto const title = base / "title.png";
        auto const body = base / "body.png";
        auto const ok = base / "ok.png";
        auto const error = base / "err.png";
        auto const titleScreen = base / "titlescreen.png";
        auto load = [this](std::filesystem::path const& path) {
            return m_resources->load_template(Paths::ToUtf8(path), Resources::TemplatePreparation::original);
        };
        auto const titleTemplate = load(title);
        auto const bodyTemplate = load(body);
        auto const okTemplate = load(ok);
        auto const errorTemplate = load(error);
        auto const titleScreenTemplate = load(titleScreen);
        if (!titleTemplate || !bodyTemplate || !okTemplate || !errorTemplate || !titleScreenTemplate)
            return {DialogKind::missingTemplates, {}};

        auto titleMatch = context.MatchTemplateAdvanced(Paths::ToUtf8(title), 0.92,
            TaskRoi{310, 475, 280, 80}, "MatchTemplate", 900, 1600, {});
        if (titleMatch.found)
        {
            auto bodyMatch = context.MatchTemplateAdvanced(Paths::ToUtf8(body), 0.90,
                TaskRoi{325, 730, 250, 85}, "MatchTemplate", 900, 1600, {});
            if (bodyMatch.found)
            {
                auto button = context.MatchTemplateAdvanced(Paths::ToUtf8(ok), 0.92,
                    TaskRoi{250, 975, 400, 130}, "MatchTemplateColor", 900, 1600, {});
                if (button.found) return {DialogKind::dateChanged, button};
            }
        }
        auto errorMatch = context.MatchTemplateAdvanced(Paths::ToUtf8(error), 0.92,
            TaskRoi{300, 485, 300, 55}, "MatchTemplate", 900, 1600, {});
        if (!errorMatch.found) return {};
        auto button = context.MatchTemplateAdvanced(Paths::ToUtf8(titleScreen), 0.92,
            TaskRoi{120, 1000, 260, 80}, "MatchTemplate", 900, 1600, {});
        return button.found ? DialogMatch{DialogKind::connectionError, button} : DialogMatch{};
    }

    bool DismissDialog(TaskMatchResult button)
    {
        auto const started = std::chrono::steady_clock::now();
        int taps = 0;
        bool seen = false;
        int pollCount = 0;
        while (pollCount++ < 40 && std::chrono::steady_clock::now() - started < std::chrono::seconds(10))
        {
            if (m_inner.IsCancelled()) return false;
            if (taps == 0 && button.found)
            {
                if (!m_inner.TapMatch(button.CenterX(), button.CenterY(), button)) return false;
                ++taps;
                seen = true;
                m_inner.Sleep(1000);
            }
            if (!m_inner.CaptureScreen())
            {
                m_inner.Sleep(250);
                continue;
            }
            auto const current = DetectDialog(m_inner);
            if (current.kind == DialogKind::none)
                return seen;
            if (current.kind == DialogKind::missingTemplates) return false;
            seen = true;
            if (taps >= 3)
            {
                m_inner.Sleep(250);
                continue;
            }
            if (!current.match.found
                || !m_inner.TapMatch(current.match.CenterX(), current.match.CenterY(), current.match))
                return false;
            ++taps;
            m_inner.Sleep(1000);
        }
        return false;
    }

    bool CheckBeforeInput(bool capture = true)
    {
        if (m_recoveringStartup) return true;
        if (capture && !m_inner.CaptureScreen()) return false;
        auto const detected = DetectDialog(m_inner);
        if (detected.kind == DialogKind::none) return true;
        if (detected.kind == DialogKind::missingTemplates)
            throw RecoveryInterruption("Date-change recovery references could not be loaded.");
        m_inner.Sleep(120);
        if (!m_inner.CaptureScreen())
            throw RecoveryInterruption("Could not confirm a possible game recovery dialog before input.");
        auto const confirmed = DetectDialog(m_inner);
        if (confirmed.kind != detected.kind) return true;
        m_inner.ReleaseRetainedMatch();
        if (!DismissDialog(confirmed.match))
            throw RecoveryInterruption("The recovery dialog did not disappear within ten seconds (at most three taps).");
        if (m_recoveringStartup) return true;
        if (!RecoverToHome())
            throw RecoveryInterruption(m_recoveryError.empty()
                ? "Game recovery could not restore Home." : m_recoveryError);
        throw RecoveryInterruption("A recovery dialog interrupted input; Home was restored.", true);
    }

    bool RecoverToHome()
    {
        if (m_packageOperations == nullptr || m_packageId.empty())
        {
            m_recoveryError = "Date-change recovery requires package operations and a package ID.";
            return false;
        }
        m_currentActionName.clear();
        auto stopped = m_packageOperations->Stop(m_packageId);
        if (!stopped.succeeded && stopped.errorCode != 0)
        {
            m_recoveryError = "Could not stop the game during date-change recovery: " + stopped.message;
            return false;
        }
        auto started = m_packageOperations->Start(m_packageId, m_activityName);
        if (!started.succeeded || !started.processDetected)
        {
            m_recoveryError = "Could not relaunch the game during date-change recovery: " + started.message;
            return false;
        }
        m_recoveringStartup = true;
        StartGameMonitorResult startup;
        try
        {
            startup = StartGameMonitor::Run(*this, m_resourceRoot);
        }
        catch (...)
        {
            m_recoveringStartup = false;
            throw;
        }
        m_recoveringStartup = false;
        if (!startup.succeeded || !startup.homeDetected)
        {
            m_recoveryError = "Date-change recovery could not confirm Home: " + startup.message;
            return false;
        }
        return true;
    }

    ITaskExecutionContext& m_inner;
    Resources::ResourceSnapshot const* m_resources{};
    std::filesystem::path m_resourceRoot;
    IGameTaskPackageOperations* m_packageOperations{};
    std::string m_packageId;
    std::string m_activityName;
    std::string m_rootPipelineName;
    std::string m_currentActionName;
    RecoveryState& m_state;
    int m_requestedRaceUnits{0};
    bool m_recoveringStartup{false};
    std::string m_recoveryError;
};

int ErrorCodeForContext(ITaskExecutionContext const& context, int fallback)
{
    auto const code = context.GetLastErrorCode();
    return code == kCanceled || code == kDeviceDisconnected
        || code == kDeviceUnauthorized || code == kDeviceOffline || code == kDeviceUnavailable
        ? code : fallback;
}

} // namespace

bool NativeGameTaskRunner::SupportsTask(std::string_view taskId) noexcept
{
    return IsTaskId(taskId, "start-game")
        || IsTaskId(taskId, "mail-collection")
        || IsTaskId(taskId, "mission-collection")
        || IsTaskId(taskId, "daily-race")
        || IsTaskId(taskId, "team-race")
        || IsTaskId(taskId, "shop-purchase")
        || IsTaskId(taskId, "career-training");
}

bool NativeGameTaskRunner::ValidateSettings(
    std::string_view taskId,
    const nlohmann::json& settings,
    std::string& errorMessage)
{
    errorMessage.clear();
    if (!SupportsTask(taskId))
    {
        errorMessage = "Unknown native game task ID: " + std::string(taskId);
        return false;
    }
    if (!settings.is_object())
    {
        errorMessage = "Game task settings must be a JSON object.";
        return false;
    }
    try
    {
        if (IsTaskId(taskId, "career-training"))
        {
            return NativeCareerTaskRunner::ParseSettings(settings, errorMessage).has_value();
        }
        if (IsTaskId(taskId, "start-game"))
        {
            auto const package = GetString(settings, "packageId");
            (void)GetString(settings, "activityName");
            if (!package || package->empty())
                throw std::invalid_argument("start-game requires a nonempty packageId.");
            return true;
        }
        if (IsTaskId(taskId, "mail-collection") || IsTaskId(taskId, "mission-collection")
            || IsTaskId(taskId, "daily-race") || IsTaskId(taskId, "team-race"))
        {
            auto const definition = DefinitionPath(settings);
            if (!definition || !std::filesystem::is_regular_file(*definition))
                throw std::invalid_argument("definitionPath must name an existing absolute JSON file.");
        }
        if (IsTaskId(taskId, "daily-race"))
        {
            auto const mode = GetString(settings, "mode");
            auto const difficulty = GetString(settings, "difficulty");
            if (mode && mode->empty()) throw std::invalid_argument("Daily Race mode cannot be empty.");
            if (difficulty && difficulty->empty()) throw std::invalid_argument("Daily Race difficulty cannot be empty.");
            (void)GetInt(settings, "raceCount", 1);
            auto const trainee = settings.find("traineeId");
            if (trainee != settings.end() && !trainee->is_null() && !trainee->is_number_integer())
                throw std::invalid_argument("Settings field 'traineeId' must be an integer or null.");
        }
        if (IsTaskId(taskId, "team-race"))
        {
            (void)GetInt(settings, "raceCount", 1);
            (void)GetBool(settings, "stopWhenTicketsEmpty", false);
        }
        (void)GetString(settings, "packageId");
        (void)GetString(settings, "activityName");
        (void)ParseShop(settings);
        return true;
    }
    catch (std::exception const& exception)
    {
        errorMessage = exception.what();
        return false;
    }
}

NativeGameTaskResult NativeGameTaskRunner::Run(
    std::string_view taskId,
    const nlohmann::json& settings,
    ITaskExecutionContext& context,
    const std::filesystem::path& resourceRoot,
    IGameTaskPackageOperations* packageOperations,
    const Resources::ResourceSnapshot* resourceSnapshot)
{
    NativeGameTaskResult result;
    result.taskId = std::string(taskId);
    std::string validationError;
    if (!ValidateSettings(taskId, settings, validationError))
    {
        result.errorCode = kInvalidArgument;
        result.message = std::move(validationError);
        return result;
    }

    try
    {
        auto const packageId = GetString(settings, "packageId").value_or("");
        auto const activityName = GetString(settings, "activityName").value_or("");
        auto const shop = ParseShop(settings);

        if (IsTaskId(taskId, "career-training"))
        {
            auto const config = NativeCareerTaskRunner::ParseSettings(settings, validationError);
            if (!config)
            {
                result.errorCode = kInvalidArgument;
                result.message = std::move(validationError);
                return result;
            }
            std::unique_ptr<Resources::ResourceSnapshot> localSnapshot;
            if (resourceSnapshot == nullptr)
            {
                localSnapshot = std::make_unique<Resources::ResourceSnapshot>(resourceRoot);
                resourceSnapshot = localSnapshot.get();
            }
            RecoveryState recoveryState;
            GuardedTaskContext guarded(context, resourceSnapshot, resourceRoot,
                packageOperations, packageId, activityName, "career-training", recoveryState, 0);
            return NativeCareerTaskRunner::Run(*config, guarded, resourceRoot);
        }

        if (IsTaskId(taskId, "start-game"))
        {
            result.totalUnits = 1;
            if (packageOperations == nullptr)
            {
                result.errorCode = kInvalidArgument;
                result.message = "start-game requires native package operations.";
                return result;
            }
            auto const launch = packageOperations->Start(packageId, activityName);
            result.processDetected = launch.processDetected;
            if (!launch.succeeded || !launch.processDetected)
            {
                result.errorCode = launch.errorCode == 0 ? kCommandFailed : launch.errorCode;
                result.message = "Could not start the game process: " + launch.message;
                return result;
            }
            auto const startup = StartGameMonitor::Run(context, resourceRoot);
            if (!startup.succeeded || !startup.homeDetected)
            {
                result.errorCode = ErrorCodeForContext(context, kCommandFailed);
                result.message = startup.message;
                return result;
            }
            result.succeeded = true;
            result.completedUnits = 1;
            result.message = startup.message;
            return result;
        }

        if (packageOperations != nullptr && !packageId.empty())
        {
            auto const query = packageOperations->Query(packageId);
            result.processDetected = query.processDetected;
            if (!query.succeeded && (query.errorCode == kCanceled || query.errorCode == kDeviceDisconnected))
            {
                result.errorCode = query.errorCode;
                result.message = query.message;
                return result;
            }
        }

        if (resourceSnapshot == nullptr)
        {
            result.errorCode = kInvalidArgument;
            result.message = "The current immutable resource snapshot was not supplied.";
            return result;
        }
        bool const shopTask = IsTaskId(taskId, "shop-purchase");
        auto const path = shopTask
            ? resourceRoot / "hachimi" / "pipelines" / "shop_task.json"
            : *DefinitionPath(settings);
        auto definition = HachimiPipelineLoader::LoadFromFile(Paths::ToUtf8(path));
        if (!definition)
        {
            result.errorCode = kInvalidArgument;
            result.message = "Could not load the requested Hachimi pipeline definition: " + Paths::ToUtf8(path);
            return result;
        }

        TaskGraphRunOptions options;
        auto const entry = std::string("home");
        bool const dailyRace = IsTaskId(taskId, "daily-race");
        bool const teamRace = IsTaskId(taskId, "team-race");
        int requestedRaces = 0;

        if (!shop.enabled)
            options.maxTimesOverrides["shopProbe"] = 0;
        else if (!shop.selectAll)
            options.maxTimesOverrides["shopSelectAll"] = 0;

        if (dailyRace)
        {
            requestedRaces = std::clamp(GetInt(settings, "raceCount", 1), 1, 6);
            auto mode = GetString(settings, "mode").value_or("monies");
            std::transform(mode.begin(), mode.end(), mode.begin(), [](unsigned char c) {
                return static_cast<char>(std::tolower(c));
            });
            auto difficulty = GetString(settings, "difficulty").value_or("veryhard");
            std::transform(difficulty.begin(), difficulty.end(), difficulty.begin(), [](unsigned char c) {
                return static_cast<char>(std::tolower(c));
            });
            if (difficulty != "hard" && difficulty != "normal" && difficulty != "easy")
                difficulty = "veryhard";
            options.maxTimesOverrides["multiRaceModeGate"] = requestedRaces - 1;
            options.maxTimesOverrides["multiRaceTicketGate"] = requestedRaces - 1;
            options.maxTimesOverrides["multiRaceTicketPlus"] = requestedRaces - 1;
            options.maxTimesOverrides["moniesDifficultyScroll"] = difficulty == "easy" ? 1 : 0;
            options.maxTimesOverrides["supportPointDifficultyScroll"] = difficulty == "easy" ? 1 : 0;
            if (mode == "supportpoint") options.maxTimesOverrides["moniesMode"] = 0;
            std::array<int, 4> difficultyRoi{520, 800, 360, 145};
            if (difficulty == "hard") difficultyRoi = {520, 975, 360, 145};
            else if (difficulty == "normal") difficultyRoi = {520, 1145, 360, 145};
            else if (difficulty == "easy") difficultyRoi = {520, 1060, 360, 170};
            options.roiOverrides["moniesDifficulty"] = {
                difficultyRoi[0], difficultyRoi[1], difficultyRoi[2], difficultyRoi[3]};
            options.roiOverrides["supportPointDifficulty"] = {
                difficultyRoi[0], difficultyRoi[1], difficultyRoi[2], difficultyRoi[3]};
        }
        else if (teamRace)
        {
            requestedRaces = std::clamp(GetInt(settings, "raceCount", 1), 1, 5);
            options.maxTimesOverrides["raceAdvance"] = requestedRaces - 1;
            // The existing Team Race contract retains this setting but does
            // not define a ticket-inspection strategy in its JSON graph.
            static_cast<void>(GetBool(settings, "stopWhenTicketsEmpty", false));
        }

        result.totalUnits = dailyRace || teamRace ? requestedRaces : 1;
        RecoveryState recoveryState;
        GuardedTaskContext guardedContext(context, resourceSnapshot, resourceRoot,
            packageOperations, packageId, activityName, definition->name, recoveryState, requestedRaces);

        TaskGraphRunResult graph;
        bool recovered = false;
        for (int recoveryAttempt = 0; recoveryAttempt <= 3; ++recoveryAttempt)
        {
            try
            {
                TaskGraphRunner runner;
                GameTaskActionExtensions::RegisterAll(runner.GetActionRegistry());
                runner.GetActionRegistry().RegisterHandler("SelectShopItems",
                    [shop, resourceSnapshot](HachimiPipelineTask const&,
                        TaskMatchResult const&, ITaskExecutionContext& actionContext)
                    {
                        HachimiPipelineDefinition currentDefinition;
                        currentDefinition.baseDirectory = actionContext.GetTemplateBaseDirectory();
                        auto const selection = ShopItemSelector::Select(
                            actionContext, currentDefinition, shop, resourceSnapshot);
                        actionContext.Log(selection.succeeded && selection.hasSelection ? "INFO" : "WARN",
                            selection.message);
                        return selection.succeeded && selection.hasSelection
                            ? ActionResult::Success : ActionResult::Failed;
                    });
                runner.GetActionRegistry().RegisterHandler("SelectDailyRaceRunner",
                    [trainee = settings.contains("traineeId") && !settings["traineeId"].is_null()
                            ? std::optional<int>(settings["traineeId"].get<int>()) : std::nullopt,
                     resourceRoot, resourceSnapshot](HachimiPipelineTask const&,
                        TaskMatchResult const&, ITaskExecutionContext& actionContext)
                    {
                        HachimiPipelineDefinition currentDefinition;
                        currentDefinition.baseDirectory = actionContext.GetTemplateBaseDirectory();
                        auto const selection = DailyRaceRunnerSelector::Select(
                            actionContext, currentDefinition, resourceRoot, trainee, resourceSnapshot);
                        actionContext.Log(selection.succeeded ? "INFO" : "ERROR", selection.message);
                        return selection.succeeded ? ActionResult::Success : ActionResult::Failed;
                    });

                if (recoveryState.confirmedShop)
                {
                    options.skipNestedPipelines.insert(Paths::ToUtf8(
                        (resourceRoot / "hachimi" / "pipelines" / "shop.json").lexically_normal()));
                    options.skipNestedPipelines.insert("shop.json");
                }
                if (recoveryState.confirmedRaceUnits > 0)
                {
                    if (teamRace)
                        options.recoveredCountOffsets["raceAdvance"] = recoveryState.confirmedRaceUnits;
                    if (dailyRace)
                    {
                        options.recoveredCountOffsets["multiRaceModeGate"] = recoveryState.confirmedRaceUnits;
                        options.recoveredCountOffsets["multiRaceTicketGate"] = recoveryState.confirmedRaceUnits;
                        options.recoveredCountOffsets["multiRaceTicketPlus"] = recoveryState.confirmedRaceUnits;
                    }
                }
                graph = runner.Run(*definition, entry, guardedContext, options);
                break;
            }
            catch (RecoveryInterruption const& interruption)
            {
                if (!interruption.homeRestored)
                {
                    result.errorCode = ErrorCodeForContext(context, kCommandFailed);
                    result.message = interruption.what();
                    return result;
                }
                recovered = true;
                context.Log("WARN", interruption.what());
                if (recoveryState.pendingShop || recoveryState.pendingRaceUnits > 0)
                {
                    result.completedUnits = recoveryState.confirmedRaceUnits;
                    result.errorCode = ErrorCodeForContext(context, kCommandFailed);
                    result.message = "Home recovered, but the interrupted purchase/race could not be confirmed; it will not be submitted again.";
                    return result;
                }
                if (recoveryState.confirmedRaceUnits >= requestedRaces && requestedRaces > 0)
                {
                    result.succeeded = true;
                    result.completedUnits = requestedRaces;
                    result.message = "Home recovered after all requested races were confirmed.";
                    return result;
                }
                if (recoveryAttempt == 3)
                {
                    result.errorCode = ErrorCodeForContext(context, kCommandFailed);
                    result.message = "Repeated recovery dialogs prevented the task from reaching completion.";
                    return result;
                }
            }
        }

        result.completedUnits = (std::max)(graph.completedUnits, recoveryState.confirmedRaceUnits);
        if (!dailyRace && !teamRace &&
            (graph.status == TaskGraphStatus::Success || graph.status == TaskGraphStatus::Completed))
            result.completedUnits = 1;
        result.succeeded = graph.status == TaskGraphStatus::Success
            || graph.status == TaskGraphStatus::Completed;
        result.errorCode = result.succeeded ? 0
            : ErrorCodeForContext(context, graph.status == TaskGraphStatus::Cancelled ? kCanceled : kCommandFailed);
        result.message = result.succeeded
            ? (recovered ? "Task completed after Home recovery." : "Task completed.")
            : (graph.errorMessage.empty() ? "Task graph did not complete successfully." : graph.errorMessage);
        return result;
    }
    catch (std::exception const& exception)
    {
        result.errorCode = ErrorCodeForContext(context, kCommandFailed);
        result.message = exception.what();
        return result;
    }
}

} // namespace UmaAssistant::GameTasks
