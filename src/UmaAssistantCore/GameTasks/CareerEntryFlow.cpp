#include "CareerEntryFlow.hpp"

#include "Common/PathEncoding.hpp"
#include "TaskEngine/ITaskExecutionContext.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <chrono>
#include <cctype>
#include <cmath>
#include <fstream>
#include <string_view>

namespace UmaAssistant::GameTasks
{
namespace
{
using Json = nlohmann::json;
constexpr int kReferenceWidth = 900;
constexpr int kReferenceHeight = 1600;

struct VisualCatalog
{
    Json json;
    std::filesystem::path careerRoot;
    bool Load(const Career::CareerRuntimeConfig& config)
    {
        careerRoot = config.resourceDirectory / "hachimi" / "career";
        std::ifstream input(careerRoot / "catalog.json", std::ios::binary);
        if (!input) return false;
        try { input >> json; }
        catch (...) { return false; }
        return json.is_object() && json.value("schemaVersion", 0) == 1
            && json.contains("assets") && json["assets"].is_object()
            && json.contains("regions") && json["regions"].is_object();
    }
    std::filesystem::path Asset(std::string_view id) const
    {
        const auto& assets = json["assets"];
        auto it = assets.find(std::string(id));
        if (it == assets.end() || !it->is_object() || !it->contains("path") || !(*it)["path"].is_string())
            return {};
        auto path = careerRoot / Paths::FromUtf8((*it)["path"].get<std::string>());
        return std::filesystem::is_regular_file(path) ? path : std::filesystem::path{};
    }
    double Threshold(std::string_view id, double fallback = 0.78) const
    {
        const auto& assets = json["assets"];
        auto it = assets.find(std::string(id));
        return it != assets.end() && it->is_object() && (*it).contains("threshold")
            && (*it)["threshold"].is_number() ? (*it)["threshold"].get<double>() : fallback;
    }
    std::optional<TaskEngine::TaskRoi> Roi(std::string_view id, bool focused = false) const
    {
        const auto& regions = json["regions"];
        auto it = regions.find(std::string(id));
        if (it == regions.end() || !it->is_object()) return {};
        Json const* roi = nullptr;
        auto const direct = it->find(focused ? "focusedRoi" : "roi");
        if (direct != it->end()) roi = &*direct;
        else if (focused && it->contains("metadata") && (*it)["metadata"].is_object())
        {
            auto const& metadata = (*it)["metadata"];
            auto const badge = metadata.find("badgeRoi");
            if (badge != metadata.end()) roi = &*badge;
        }
        if (!roi || !roi->is_array() || roi->size() < 4) return {};
        try { return TaskEngine::TaskRoi{(*roi)[0].get<int>(), (*roi)[1].get<int>(),
            (*roi)[2].get<int>(), (*roi)[3].get<int>()}; }
        catch (...) { return {}; }
    }
    std::vector<int> IntArray(std::string_view id, std::string_view key) const
    {
        const auto& regions = json["regions"];
        auto it = regions.find(std::string(id));
        if (it == regions.end() || !it->is_object() || !it->contains(std::string(key))) return {};
        auto const& value = (*it)[std::string(key)];
        if (!value.is_array()) return {};
        std::vector<int> result;
        for (auto const& item : value)
        {
            if (!item.is_number_integer()) return {};
            result.push_back(item.get<int>());
        }
        return result;
    }
};

bool MatchAsset(TaskEngine::ITaskExecutionContext& context, VisualCatalog const& visual,
    std::string_view id, std::string_view region, TaskEngine::TaskMatchResult& match,
    double threshold = -1.0)
{
    auto const path = visual.Asset(id);
    if (path.empty()) return false;
    auto const roi = region.empty() ? std::optional<TaskEngine::TaskRoi>{} : visual.Roi(region);
    if (!region.empty() && !roi) return false;
    context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
    match = context.MatchTemplateAdvanced(Paths::ToUtf8(path), threshold >= 0.0 ? threshold : visual.Threshold(id),
        roi, "MatchTemplate", kReferenceWidth, kReferenceHeight, {});
    return match.found;
}

bool WaitTapAsset(TaskEngine::ITaskExecutionContext& context, VisualCatalog const& visual,
    std::string_view asset, std::string_view region, int timeoutMs = 8000,
    std::string_view logLabel = {})
{
    if (visual.Asset(asset).empty() || (!region.empty() && !visual.Roi(region)))
    {
        context.Log("ERROR", "Career visual catalog is missing control metadata: "
            + std::string(asset) + " / " + std::string(region));
        return false;
    }
    auto const start = std::chrono::steady_clock::now();
    do
    {
        if (context.IsCancelled() || !context.CaptureScreen()) return false;
        TaskEngine::TaskMatchResult match;
        if (MatchAsset(context, visual, asset, region, match))
        {
            auto const tapped = context.TapMatch(match.CenterX(), match.CenterY(), match);
            if (tapped && !logLabel.empty()) context.Log("INFO", std::string(logLabel));
            if (tapped) context.Sleep(350);
            return tapped;
        }
        context.Sleep(200);
    } while (std::chrono::steady_clock::now() - start < std::chrono::milliseconds(timeoutMs));
    context.Log("ERROR", "Career entry could not verify visual control: " + std::string(asset));
    return false;
}

bool TapRegion(TaskEngine::ITaskExecutionContext& context, VisualCatalog const& visual,
    std::string_view region)
{
    auto const roi = visual.Roi(region);
    if (!roi || !context.CaptureScreen()) return false;
    context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
    TaskEngine::TaskMatchResult frame;
    frame.referenceWidth = kReferenceWidth;
    frame.referenceHeight = kReferenceHeight;
    auto const x = roi->x + roi->width / 2;
    auto const y = roi->y + roi->height / 2;
    return context.TapMatch(x, y, frame);
}

bool TapOcrDetection(TaskEngine::ITaskExecutionContext& context, TaskEngine::TaskOcrDetection const& detection)
{
    auto const frame = context.GetCurrentFrame();
    if (!frame || frame->width <= 0 || frame->height <= 0) return false;
    TaskEngine::TaskMatchResult match;
    match.found = true;
    match.x = detection.x;
    match.y = detection.y;
    match.width = detection.width;
    match.height = detection.height;
    match.referenceWidth = kReferenceWidth;
    match.referenceHeight = kReferenceHeight;
    match.frameId = frame->frameId;
    match.sessionGeneration = frame->sessionGeneration;
    match.geometryGeneration = frame->geometryGeneration;
    return context.TapMatch(match.CenterX(), match.CenterY(), match);
}

std::optional<std::string> AptitudeBest(std::map<std::string, std::string, std::less<>> const& aptitudes,
    std::string_view key)
{
    auto const found = aptitudes.find(key);
    if (found == aptitudes.end() || found->second.empty()) return {};
    std::string value = found->second;
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::toupper(ch));
    });
    return value;
}

std::vector<std::string> DesiredAptitudeLabels(GameData::UmaTraineeRecord const& trainee)
{
    static const std::array<std::string_view, 8> grades{"S", "A", "B", "C", "D", "E", "F", "G"};
    auto bestKeys = [&](auto const& values) {
        std::vector<std::string> result;
        int bestRank = static_cast<int>(grades.size());
        for (auto const& [key, grade] : values)
        {
            std::string normalized = grade;
            std::transform(normalized.begin(), normalized.end(), normalized.begin(), [](unsigned char ch) {
                return static_cast<char>(std::toupper(ch));
            });
            auto it = std::find(grades.begin(), grades.end(), normalized);
            if (it == grades.end()) continue;
            auto const rank = static_cast<int>(it - grades.begin());
            if (rank < bestRank) { bestRank = rank; result.clear(); }
            if (rank == bestRank) result.push_back(key);
        }
        return result;
    };
    std::vector<std::string> labels;
    for (auto const& key : bestKeys(trainee.surfaceAptitudes))
        labels.push_back(key == "turf" || key == "dirt" ? key : key);
    for (auto const& key : bestKeys(trainee.distanceAptitudes))
    {
        if (key == "short") labels.emplace_back("sprint");
        else labels.push_back(key);
    }
    for (auto const& key : bestKeys(trainee.strategyAptitudes)) labels.push_back(key);
    return labels;
}

bool SelectTrainee(const Career::CareerRuntimeConfig& config,
    GameData::UmaGameDatabase const& trainees, TaskEngine::ITaskExecutionContext& context)
{
    VisualCatalog visual;
    if (!visual.Load(config)) { context.Log("ERROR", "Career visual catalog is invalid for trainee selection."); return false; }
    auto const trainee = config.traineeId ? trainees.TryGetTrainee(*config.traineeId) : nullptr;
    if (!trainee || !trainee->available)
    {
        context.Log("ERROR", "Configured Career trainee is missing or unavailable.");
        return false;
    }
    auto const templates = trainees.GetTraineeImagePaths(*trainee);
    if (templates.empty()) { context.Log("ERROR", "No identity templates exist for the configured trainee."); return false; }

    if (!WaitTapAsset(context, visual, "career.entry.trainee.display_button", "career.entry.trainee.display_button")
        || !WaitTapAsset(context, visual, "career.entry.trainee.filter_tab", "career.entry.trainee.filter_tab"))
        return false;

    // Reset first, then apply every category's highest aptitude, matching the
    // managed Career selector's persisted filter semantics.
    if (!TapRegion(context, visual, "career.entry.trainee.filter_reset"))
    {
        context.Log("ERROR", "Career trainee filter reset region is missing.");
        return false;
    }
    context.Sleep(250);
    auto const labels = DesiredAptitudeLabels(*trainee);
    if (labels.empty()) { context.Log("ERROR", "Configured trainee has no usable aptitude filter metadata."); return false; }
    for (auto const& label : labels)
    {
        std::string const semantic = "career.entry.trainee.filter.aptitude." + label;
        auto const option = visual.Asset(semantic);
        auto const roi = visual.Roi(semantic);
        auto const checkbox = visual.Asset("career.entry.trainee.filter_checkbox");
        if (option.empty() || checkbox.empty() || !roi)
        {
            context.Log("ERROR", "Career trainee filter metadata is incomplete for " + label);
            return false;
        }
        TaskEngine::TaskMatchResult optionMatch;
        auto const scales = std::vector<double>{0.7, 0.8, 0.9, 1.0, 1.1, 1.2};
        if (!context.CaptureScreen()) return false;
        optionMatch = context.MatchTemplateAdvanced(Paths::ToUtf8(option), visual.Threshold(semantic), roi,
            "MatchTemplateScaled", kReferenceWidth, kReferenceHeight, scales);
        if (!optionMatch.found)
        {
            context.Log("ERROR", "Configured trainee's aptitude filter is not visible: " + label);
            return false;
        }
        auto checkboxMatch = context.MatchTemplateAdvanced(Paths::ToUtf8(checkbox),
            visual.Threshold("career.entry.trainee.filter_checkbox"), roi,
            "MatchTemplateScaled", kReferenceWidth, kReferenceHeight, scales);
        if (!checkboxMatch.found || !context.TapMatch(checkboxMatch.CenterX(), checkboxMatch.CenterY(), checkboxMatch))
        {
            context.Log("ERROR", "Could not verify/toggle trainee aptitude checkbox: " + label);
            return false;
        }
        context.Sleep(150);
    }
    if (!WaitTapAsset(context, visual, "career.entry.trainee.filter_confirm", "career.entry.trainee.filter_confirm"))
        return false;

    auto const policyId = "career.entry.trainee.card.matching_policy";
    auto const& policy = visual.json["regions"][policyId].value("metadata", Json::object());
    const int maxScrolls = std::clamp(policy.value("maxScrolls", 16), 0, 32);
    const double threshold = std::clamp(policy.value("minimumMatchScore", 0.70), 0.0, 1.0);
    auto swipe = policy.value("swipe", std::vector<int>{760, 1150, 760, 850, 550});
    if (swipe.size() < 5) { context.Log("ERROR", "Trainee matching policy has invalid scroll settings."); return false; }
    auto const cellPrefix = std::string("career.entry.trainee.card.cell.");
    std::vector<TaskEngine::TaskRoi> cells;
    for (int index = 1; index <= 15; ++index)
    {
        auto const cell = visual.Roi(cellPrefix + std::to_string(index));
        if (cell) cells.push_back(*cell);
    }
    if (cells.empty()) { context.Log("ERROR", "Trainee card-cell regions are missing."); return false; }
    auto scales = policy.value("sampleScaleCandidates", std::vector<double>{0.32, 0.4, 0.48, 0.56, 0.64, 0.72, 0.8, 0.9, 1.0});
    double bestScore = 0.0;
    for (int page = 0; page <= maxScrolls; ++page)
    {
        if (context.IsCancelled() || !context.CaptureScreen()) return false;
        for (auto const& cell : cells)
        {
            TaskEngine::TaskMatchResult best;
            for (auto const& path : templates)
            {
                auto match = context.MatchTemplateAdvanced(Paths::ToUtf8(path), threshold,
                    cell, "MatchTemplateScaled", kReferenceWidth, kReferenceHeight, scales);
                if (match.found && match.score > best.score) best = std::move(match);
            }
            bestScore = std::max(bestScore, best.score);
            if (best.found && best.score >= threshold)
            {
                if (context.TapMatch(best.CenterX(), best.CenterY(), best))
                {
                    context.Log("INFO", "Selected configured Career trainee by matching identity template.");
                    return true;
                }
                context.Log("ERROR", "Matched configured trainee but tap evidence expired before selection.");
                return false;
            }
        }
        if (page == maxScrolls) break;
        context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
        if (!context.Swipe(swipe[0], swipe[1], swipe[2], swipe[3], swipe[4])) return false;
        context.Sleep(std::clamp(policy.value("scrollDelayMs", 350), 50, 5000));
    }
    context.Log("ERROR", "Configured trainee identity was not found; best template score=" + std::to_string(bestScore));
    return false;
}

std::string NormalizeKey(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

bool LegacyAutoSelect(Career::CareerRuntimeConfig const& config, TaskEngine::ITaskExecutionContext& context,
    VisualCatalog const& visual)
{
    TaskEngine::TaskMatchResult cached1, cached2;
    bool const has1 = MatchAsset(context, visual, "career.entry.legacy.legacy1_cached_record", "career.entry.legacy.cached1", cached1);
    bool const has2 = MatchAsset(context, visual, "career.entry.legacy.legacy2_cached_record", "career.entry.legacy.cached2", cached2);
    if (config.useCachedLegacy && has1 && has2)
        return WaitTapAsset(context, visual, "career.entry.legacy.legacy_next_enabled", "career.entry.legacy.next");

    if (!WaitTapAsset(context, visual, "career.entry.legacy.legacy_auto_select", "career.entry.legacy.auto_select")) return false;
    if (config.useLegacyGuest)
    {
        if (!context.CaptureScreen()) return false;
        TaskEngine::TaskMatchResult on;
        if (!MatchAsset(context, visual, "career.entry.legacy.legacy_auto_select_include_guests_on",
                "career.entry.legacy.guest.checkbox", on)
            && !WaitTapAsset(context, visual, "career.entry.legacy.legacy_auto_select_include_guests",
                "career.entry.legacy.guest.checkbox")) return false;
    }
    if (!WaitTapAsset(context, visual, "career.entry.legacy.legacy_auto_select_ok", "career.entry.legacy.auto_select.confirm")) return false;
    const auto start = std::chrono::steady_clock::now();
    bool borrowConfirmed = false;
    while (std::chrono::steady_clock::now() - start < std::chrono::seconds(20))
    {
        if (context.IsCancelled() || !context.CaptureScreen()) return false;
        TaskEngine::TaskMatchResult borrow;
        if (config.useLegacyGuest && MatchAsset(context, visual, "career.entry.legacy.legacy_borrow_confirmation_title",
                "career.entry.legacy.borrow_confirmation.title", borrow))
        {
            if (!borrowConfirmed && !WaitTapAsset(context, visual, "career.entry.legacy.legacy_borrow_confirmation_ok",
                    "career.entry.legacy.borrow_confirmation.ok")) return false;
            borrowConfirmed = true;
        }
        else
        {
            TaskEngine::TaskMatchResult next;
            if (MatchAsset(context, visual, "career.entry.legacy.legacy_next_enabled", "career.entry.legacy.next", next))
                return context.TapMatch(next.CenterX(), next.CenterY(), next);
        }
        context.Sleep(250);
    }
    context.Log("ERROR", "URA Auto-Select did not return to verified Legacy Select.");
    return false;
}

bool LegacyManualSelect(Career::CareerRuntimeConfig const& config, TaskEngine::ITaskExecutionContext& context,
    VisualCatalog const& visual)
{
    if (config.useCachedLegacy)
    {
        TaskEngine::TaskMatchResult one, two;
        if (MatchAsset(context, visual, "career.entry.legacy.legacy1_cached_record", "career.entry.legacy.cached1", one)
            && MatchAsset(context, visual, "career.entry.legacy.legacy2_cached_record", "career.entry.legacy.cached2", two))
            return WaitTapAsset(context, visual, "career.entry.legacy.legacy_next_enabled", "career.entry.legacy.next");
    }
    auto const doSlot = [&](int slot) -> bool {
        std::string const suffix = std::to_string(slot);
        auto const useGuest = slot == 1 && config.useLegacyGuest;
        TaskEngine::TaskMatchResult cached;
        bool opened = false;
        if (MatchAsset(context, visual, "career.entry.legacy.legacy" + suffix + "_cached_record",
                "career.entry.legacy.cached" + suffix, cached))
        {
            if (config.useCachedLegacy) return true;
            if (!context.TapMatch(cached.CenterX(), cached.CenterY(), cached)) return false;
            context.Sleep(350);
            opened = true;
        }
        if (!opened && !WaitTapAsset(context, visual, "career.entry.legacy.legacy" + suffix + "_slot", "")) return false;

        if (slot == 1)
        {
            TaskEngine::TaskMatchResult sparksOff, sparksOn;
            if (MatchAsset(context, visual, "career.entry.legacy.legacy_view_sparks_off", "career.entry.legacy.view_sparks", sparksOff))
            {
                if (!context.TapMatch(sparksOff.CenterX(), sparksOff.CenterY(), sparksOff)) return false;
                context.Sleep(250);
            }
            else if (!MatchAsset(context, visual, "career.entry.legacy.legacy_view_sparks_on", "career.entry.legacy.view_sparks", sparksOn))
                return false;
            if (!WaitTapAsset(context, visual, "career.entry.legacy.legacy_display_button", "career.entry.legacy.display.open")
                || !WaitTapAsset(context, visual, "career.entry.legacy.legacy_display_sparks", "career.entry.legacy.display.sparks")
                || !WaitTapAsset(context, visual, "career.entry.legacy.legacy_display_filter_tab", "career.entry.legacy.display.filter_tab")
                || !WaitTapAsset(context, visual, "career.entry.legacy.legacy_filter_reset", "career.entry.legacy.filter.reset")) return false;
            for (auto const& raw : config.legacyAttributeSparks)
            {
                auto key = NormalizeKey(raw);
                if (key != "speed" && key != "stamina" && key != "power" && key != "guts" && key != "wit") return false;
                if (!TapRegion(context, visual, "career.entry.legacy.filter." + key)) return false;
            }
            for (auto const& raw : config.legacyAptitudeSparks)
            {
                auto key = NormalizeKey(raw);
                if (key == "short") key = "sprint";
                if (key != "turf" && key != "dirt" && key != "sprint" && key != "mile" && key != "medium"
                    && key != "long" && key != "front" && key != "pace" && key != "late" && key != "end") return false;
                if (!TapRegion(context, visual, "career.entry.legacy.filter." + key)) return false;
            }
            if (!WaitTapAsset(context, visual, "career.entry.legacy.legacy_filter_ok", "career.entry.legacy.filter.ok")) return false;
            TaskEngine::TaskMatchResult asc, desc;
            if (MatchAsset(context, visual, "career.entry.legacy.legacy_sort_asc", "career.entry.legacy.sort", asc))
            {
                if (!context.TapMatch(asc.CenterX(), asc.CenterY(), asc)) return false;
                context.Sleep(250);
            }
            else if (!MatchAsset(context, visual, "career.entry.legacy.legacy_sort_desc", "career.entry.legacy.sort", desc)) return false;
        }
        if (useGuest)
        {
            TaskEngine::TaskMatchResult selected;
            if (!MatchAsset(context, visual, "career.entry.legacy.legacy_guests_tab_selected", "career.entry.legacy.guests.tab", selected)
                && !WaitTapAsset(context, visual, "career.entry.legacy.legacy_guests_tab", "career.entry.legacy.guests.tab")) return false;
        }
        std::vector<TaskEngine::TaskRoi> cells;
        for (int cell = 1; cell <= 4; ++cell)
        {
            auto roi = visual.Roi("career.entry.legacy.cell." + std::to_string(cell));
            if (roi) cells.push_back(*roi);
        }
        auto badgePath = visual.Asset("career.entry.legacy.legacy_trainee_badge");
        if (cells.empty() || badgePath.empty()) return false;
        if (!context.CaptureScreen()) return false;
        std::optional<TaskEngine::TaskMatchResult> selectedCell;
        for (auto const& cell : cells)
        {
            auto badgeRoi = visual.Roi("career.entry.legacy.cell." + std::to_string(&cell - cells.data() + 1), true).value_or(cell);
            auto badge = context.MatchTemplateAdvanced(Paths::ToUtf8(badgePath), visual.Threshold("career.entry.legacy.legacy_trainee_badge"),
                badgeRoi, "MatchTemplate", kReferenceWidth, kReferenceHeight, {});
            if (!badge.found) { selectedCell = TaskEngine::TaskMatchResult{}; selectedCell->found = true;
                selectedCell->x = cell.x; selectedCell->y = cell.y; selectedCell->width = cell.width; selectedCell->height = cell.height;
                selectedCell->referenceWidth = kReferenceWidth; selectedCell->referenceHeight = kReferenceHeight; break; }
        }
        if (!selectedCell) { context.Log("ERROR", "All visible URA Legacy cards are marked as trainee cards."); return false; }
        auto const x = selectedCell->x + selectedCell->width / 2;
        auto const y = selectedCell->y + selectedCell->height / 2;
        if (!context.TapMatch(x, y, *selectedCell)) return false;
        context.Sleep(350);
        if (!WaitTapAsset(context, visual, "career.entry.legacy.legacy_confirm_selection", "career.entry.legacy.confirm_selection")) return false;
        if (useGuest)
        {
            auto const start = std::chrono::steady_clock::now();
            bool borrowConfirmed = false;
            while (std::chrono::steady_clock::now() - start < std::chrono::seconds(10))
            {
                if (!context.CaptureScreen()) return false;
                TaskEngine::TaskMatchResult borrow, done;
                if (MatchAsset(context, visual, "career.entry.legacy.legacy_borrow_confirmation_title", "career.entry.legacy.borrow_confirmation.title", borrow))
                {
                    if (!borrowConfirmed && !WaitTapAsset(context, visual, "career.entry.legacy.legacy_borrow_confirmation_ok", "career.entry.legacy.borrow_confirmation.ok")) return false;
                    borrowConfirmed = true;
                }
                else if (borrowConfirmed && MatchAsset(context, visual,
                    "career.entry.legacy.legacy" + suffix + "_cached_record", "career.entry.legacy.cached" + suffix, done)) return true;
                context.Sleep(200);
            }
            return false;
        }
        const auto start = std::chrono::steady_clock::now();
        while (std::chrono::steady_clock::now() - start < std::chrono::seconds(10))
        {
            if (!context.CaptureScreen()) return false;
            TaskEngine::TaskMatchResult done;
            if (MatchAsset(context, visual, "career.entry.legacy.legacy" + suffix + "_cached_record",
                    "career.entry.legacy.cached" + suffix, done)) return true;
            context.Sleep(200);
        }
        return false;
    };
    if (!doSlot(1)) { context.Log("ERROR", "Manual selection of Legacy 1 did not verify."); return false; }
    if (!doSlot(2)) { context.Log("ERROR", "Manual selection of Legacy 2 did not verify."); return false; }
    return WaitTapAsset(context, visual, "career.entry.legacy.legacy_next_enabled", "career.entry.legacy.next");
}

bool SelectLegacy(Career::CareerRuntimeConfig const& config, TaskEngine::ITaskExecutionContext& context)
{
    VisualCatalog visual;
    if (!visual.Load(config)) return false;
    return NormalizeKey(config.legacySelectionMode) == "manual"
        ? LegacyManualSelect(config, context, visual)
        : LegacyAutoSelect(config, context, visual);
}

bool IsRuntimeCareerScreen(std::string const& id)
{
    static const std::array<std::string_view, 10> known{
        "career_main", "career_races_ready", "training_selection", "training_result",
        "event_choice", "race_entry", "race_execution", "race_result", "skill_learning", "career_complete"};
    return std::any_of(known.begin(), known.end(), [&](auto const value) { return NormalizeKey(id) == value; });
}

bool IsEntryScreen(std::string const& id)
{
    static const std::array<std::string_view, 10> known{
        "home", "home_unselected", "career_continue", "scenario_select", "trainee_select",
        "legacy_select", "support_select", "support_autofill_confirmation", "support_ready", "career_tp_restore_prompt"};
    return std::any_of(known.begin(), known.end(), [&](auto const value) { return NormalizeKey(id) == value; });
}

bool RunAction(CareerEntryResult& result, Career::CareerVisualResources& resources,
    TaskEngine::ITaskExecutionContext& context, std::string const& screen,
    std::string const& semantic, TaskEngine::TaskGraphRunOptions const& options = {})
{
    if (context.IsCancelled()) { result.cancelled = true; return false; }
    auto const action = resources.RunAction(screen, semantic, context, options);
    if (action.status != TaskEngine::TaskGraphStatus::Completed && action.status != TaskEngine::TaskGraphStatus::Success)
    {
        result.cancelled = action.status == TaskEngine::TaskGraphStatus::Cancelled || context.IsCancelled();
        result.error = action.errorMessage.empty() ? "Career entry action failed: " + screen + "." + semantic : action.errorMessage;
        return false;
    }
    ++result.actionsCompleted;
    return true;
}

CareerDomain::CareerObservation ObserveStable(Career::ICareerObserver& observer,
    TaskEngine::ITaskExecutionContext& context, CareerEntryResult& result,
    std::vector<std::string> const& screenIds, bool allowResume)
{
    auto observation = observer.ObserveStableScreens(context, screenIds, allowResume);
    result.lastScreenId = observation.screenId;
    result.cancelled = context.IsCancelled() || observation.screenId == "capture_cancelled";
    return observation;
}

bool ConfigureSupport(Career::CareerRuntimeConfig const& config,
    GameData::CareerSelectionCatalog const& catalogs,
    Career::CareerVisualResources& resources, TaskEngine::ITaskExecutionContext& context,
    CareerEntryResult& result)
{
    auto run = [&](std::string const& semantic, TaskEngine::TaskGraphRunOptions const& options = {}) {
        return RunAction(result, resources, context, "support_select", semantic, options);
    };
    if (config.supportDeckMode == "auto") return run("support.auto_fill");

    auto const ownIds = config.supportCardIds;
    std::optional<int> friendId = config.friendSupportCardId;
    if (config.supportDeckMode == "selected")
    {
        if (ownIds.size() != 5 && !(ownIds.size() == 6 && !friendId))
        { result.error = "Selected support deck requires exactly five owned card IDs and one optional guest card."; return false; }
        if (ownIds.size() == 6) friendId = ownIds.back();
        if (!run("support.reset_if_needed")) return false;
        auto selectExact = [&](int id, int slot, bool friendPage) {
            auto const* card = catalogs.TryGetSupportCard(id);
            auto const path = catalogs.GetSupportCardTemplatePath(id);
            if (!card || !card->available || path.empty())
            { result.error = "Support card " + std::to_string(id) + " is unavailable or has no identity template."; return false; }
            std::string const rarity = NormalizeKey(card->rarity) == "3" ? "ssr" : NormalizeKey(card->rarity) == "2" ? "sr" : NormalizeKey(card->rarity) == "1" ? "r" : "";
            auto const type = NormalizeKey(card->type);
            if (rarity.empty() || (type != "speed" && type != "stamina" && type != "power" && type != "guts" && type != "wit" && type != "friend"))
            { result.error = "Support card has unrecognized rarity/type metadata: " + std::to_string(id); return false; }
            if (friendPage)
            {
                TaskEngine::TaskGraphRunOptions open;
                open.searchRoiOverrides["support_select_support_open"] = {{630, 770, 160, 160}};
                if (!run("support.open", open))
                {
                    // A filled guest slot opens its management dialog. Remove
                    // only after OCR verifies the dialog's Remove command.
                    if (!context.CaptureScreen()) return false;
                    auto detections = context.RecognizeText("en-US", TaskEngine::TaskRoi{20, 140, 860, 130});
                    auto remove = std::find_if(detections.begin(), detections.end(), [](auto const& text) {
                        return NormalizeKey(text.text).find("remove") != std::string::npos;
                    });
                    if (remove == detections.end() || !TapOcrDetection(context, *remove))
                    { result.error = "Could not verify the Remove action for an occupied guest support slot."; return false; }
                    result.error.clear();
                    TaskEngine::TaskGraphRunOptions reopen;
                    reopen.searchRoiOverrides["support_select_support_open"] = {{630, 770, 160, 160}};
                    if (!run("support.open", reopen)) return false;
                }
            }
            else
            {
                static const std::array<std::array<int, 4>, 5> slots{{
                    {{90, 420, 160, 160}}, {{360, 420, 160, 160}}, {{630, 420, 160, 160}},
                    {{90, 770, 160, 160}}, {{360, 770, 160, 160}}}};
                if (slot < 0 || slot >= static_cast<int>(slots.size())) return false;
                TaskEngine::TaskGraphRunOptions open;
                auto const& roi = slots[slot];
                open.searchRoiOverrides["support_select_support_open"] = {{roi[0], roi[1], roi[2], roi[3]}};
                if (!run("support.open", open)) return false;
            }
            const std::array<std::string, 6> filterActions{
                "support.ranked.display_settings", "support.ranked.filter_tab",
                "support.ranked.filter_reset", "support.ranked.filter_" + rarity,
                "support.ranked.filter_" + type, "support.ranked.filter_apply"
            };
            for (auto const& action : filterActions)
                if (!run(action)) return false;
            TaskEngine::TaskGraphRunOptions exact;
            exact.templateOverrides["support_select_support_card_exact"] = Paths::ToUtf8(path);
            exact.templateOverrides["support_select_support_card_exact_fallback"] = Paths::ToUtf8(path);
            if (friendPage)
            {
                for (auto const* task : {"support_select_support_card_exact", "support_select_support_card_exact_fallback"})
                {
                    exact.roiOverrides[task] = {25, 270, 190, 1060};
                    exact.scaleCandidatesOverrides[task] = {0.85};
                }
            }
            return run("support.ranked.select_exact_card", exact);
        };
        for (std::size_t i = 0; i < 5; ++i) if (!selectExact(ownIds[i], static_cast<int>(i), false)) return false;
        if (friendId && !selectExact(*friendId, 5, true)) return false;
        return run("support.start");
    }

    if (config.supportDeckMode != "highest-star")
    { result.error = "Unknown supportDeckMode: " + config.supportDeckMode; return false; }
    std::map<std::string, int, std::less<>> required;
    if (config.supportDeckPreset == "speed3-stamina3") required = {{"speed", 3}, {"stamina", 3}};
    else if (config.supportDeckPreset == "speed3-stamina2-wit1") required = {{"speed", 3}, {"stamina", 2}, {"wit", 1}};
    else if (config.supportDeckPreset == "speed2-stamina2-power1-wit1") required = {{"speed", 2}, {"stamina", 2}, {"power", 1}, {"wit", 1}};
    else if (config.supportDeckPreset == "speed2-stamina1-power1-wit1-friend1") required = {{"speed", 2}, {"stamina", 1}, {"power", 1}, {"wit", 1}, {"friend", 1}};
    else { result.error = "Unknown highest-star support deck preset: " + config.supportDeckPreset; return false; }
    std::string guestType;
    if (friendId)
    {
        auto const* friendCard = catalogs.TryGetSupportCard(*friendId);
        if (!friendCard || !friendCard->available)
        { result.error = "Configured friend support card is unavailable."; return false; }
        guestType = NormalizeKey(friendCard->type);
        if (required.erase("friend") == 0)
        {
            auto const type = required.find(guestType);
            if (type == required.end())
            { result.error = "Configured guest support card does not fit the selected highest-star preset."; return false; }
            if (--type->second == 0) required.erase(type);
        }
    }
    else if (required.erase("friend") > 0)
    {
        auto const candidate = std::find_if(catalogs.SupportCards().begin(), catalogs.SupportCards().end(), [](auto const& card) {
            auto const type = NormalizeKey(card.type);
            return card.available && (type == "speed" || type == "stamina" || type == "power"
                || type == "guts" || type == "wit");
        });
        if (candidate == catalogs.SupportCards().end())
        { result.error = "Friend-slot preset has no usable support-card type in the catalog."; return false; }
        guestType = NormalizeKey(candidate->type);
    }
    else
    {
        // All presets describe six cards including the borrowed card. Reserve
        // its type before filling the five owned slots, even without a guest ID.
        guestType = required.contains("speed") ? "speed" : required.begin()->first;
        if (--required[guestType] == 0) required.erase(guestType);
    }
    if (!run("support.reset_if_needed")) return false;
    int slot = 0;
    static const std::array<std::array<int, 4>, 5> highestStarSlots{{
        {{90, 420, 160, 160}}, {{360, 420, 160, 160}}, {{630, 420, 160, 160}},
        {{90, 770, 160, 160}}, {{360, 770, 160, 160}}}};
    for (auto const& [type, count] : required)
    {
        if (type == "friend") continue;
        for (int index = 0; index < count; ++index)
        {
            TaskEngine::TaskGraphRunOptions open;
            if (slot >= static_cast<int>(highestStarSlots.size()))
            { result.error = "Support preset exceeds available owned card slots."; return false; }
            auto const& roi = highestStarSlots[static_cast<std::size_t>(slot++)];
            open.searchRoiOverrides["support_select_support_open"] = {{roi[0], roi[1], roi[2], roi[3]}};
            if (!run("support.open", open)) return false;
            for (auto const& action : {std::string("support.ranked.display_settings"), std::string("support.ranked.filter_tab"),
                std::string("support.ranked.filter_reset"), std::string("support.ranked.filter_sr"),
                std::string("support.ranked.filter_ssr"), "support.ranked.filter_" + type,
                std::string("support.ranked.filter_apply"), std::string("support.ranked.display_settings"),
                std::string("support.ranked.sort_level"), std::string("support.ranked.sort_apply"), std::string("support.ranked.sort_desc")})
                if (!run(action)) return false;
            TaskEngine::TaskGraphRunOptions best;
            auto typeTemplate = resources.FindAsset("career.entry.support.type." + type);
            if (!typeTemplate) { result.error = "Support preset type template is missing: " + type; return false; }
            // Keep already selected cards in front and select the next card of
            // the requested type through the catalog's stable horizontal ROI.
            auto const cardRoi = std::array<int, 4>{35 + index * 166, 130, 165, 220};
            for (auto const* task : {"support_select_support_top_card_ssr", "support_select_support_top_card_sr"})
            {
                best.templateOverrides[task] = typeTemplate->path;
                best.roiOverrides[task] = {cardRoi[0], cardRoi[1], cardRoi[2], cardRoi[3]};
            }
            if (!run("support.ranked.select_highest_card", best)) return false;
        }
    }
    if (friendId)
    {
        auto const* card = catalogs.TryGetSupportCard(*friendId);
        auto const path = catalogs.GetSupportCardTemplatePath(*friendId);
        if (!card || !card->available || path.empty()) { result.error = "Configured friend support card is unavailable."; return false; }
        TaskEngine::TaskGraphRunOptions open;
        open.searchRoiOverrides["support_select_support_open"] = {{630, 770, 160, 160}};
        if (!run("support.open", open))
        {
            if (!context.CaptureScreen()) return false;
            auto detections = context.RecognizeText("en-US", TaskEngine::TaskRoi{20, 140, 860, 130});
            auto remove = std::find_if(detections.begin(), detections.end(), [](auto const& text) {
                return NormalizeKey(text.text).find("remove") != std::string::npos;
            });
            if (remove == detections.end() || !TapOcrDetection(context, *remove))
            { result.error = "Could not verify the Remove action for an occupied guest support slot."; return false; }
            result.error.clear();
            if (!run("support.open", open)) return false;
        }
        std::string const rarity = NormalizeKey(card->rarity) == "3" ? "ssr" : NormalizeKey(card->rarity) == "2" ? "sr" : "r";
        auto const type = NormalizeKey(card->type);
        const std::array<std::string, 6> friendFilterActions{
            "support.ranked.display_settings", "support.ranked.filter_tab",
            "support.ranked.filter_reset", "support.ranked.filter_" + rarity,
            "support.ranked.filter_" + type, "support.ranked.filter_apply"
        };
        for (auto const& action : friendFilterActions)
            if (!run(action)) return false;
        TaskEngine::TaskGraphRunOptions exact;
        exact.templateOverrides["support_select_support_card_exact"] = Paths::ToUtf8(path);
        exact.templateOverrides["support_select_support_card_exact_fallback"] = Paths::ToUtf8(path);
        for (auto const* task : {"support_select_support_card_exact", "support_select_support_card_exact_fallback"})
        {
            exact.roiOverrides[task] = {25, 270, 190, 1060};
            exact.scaleCandidatesOverrides[task] = {0.85};
        }
        if (!run("support.ranked.select_exact_card", exact)) return false;
    }
    else
    {
        TaskEngine::TaskGraphRunOptions open;
        open.searchRoiOverrides["support_select_support_open"] = {{630, 770, 160, 160}};
        if (!run("support.open", open))
        {
            if (!context.CaptureScreen()) return false;
            auto detections = context.RecognizeText("en-US", TaskEngine::TaskRoi{20, 140, 860, 130});
            auto remove = std::find_if(detections.begin(), detections.end(), [](auto const& text) {
                return NormalizeKey(text.text).find("remove") != std::string::npos;
            });
            if (remove == detections.end() || !TapOcrDetection(context, *remove))
            { result.error = "Could not verify the Remove action for an occupied guest support slot."; return false; }
            result.error.clear();
            if (!run("support.open", open)) return false;
        }
        for (auto const& action : {std::string("support.ranked.display_settings"), std::string("support.ranked.filter_tab"),
            std::string("support.ranked.filter_reset"), std::string("support.ranked.filter_sr"),
            std::string("support.ranked.filter_ssr"), "support.ranked.filter_" + guestType,
            std::string("support.ranked.filter_apply"), std::string("support.ranked.display_settings"),
            std::string("support.ranked.friend_sort_level"), std::string("support.ranked.sort_apply"),
            std::string("support.ranked.sort_desc")}) if (!run(action)) return false;
        TaskEngine::TaskGraphRunOptions best;
        auto const* typeTemplate = resources.FindAsset("career.entry.support.type.friend." + guestType);
        if (!typeTemplate) { result.error = "Friend support template is missing for type " + guestType; return false; }
        best.templateOverrides["support_select_support_friend_top_card_ssr"] = typeTemplate->path;
        best.templateOverrides["support_select_support_friend_top_card_sr"] = typeTemplate->path;
        if (!run("support.ranked.select_friend_highest_card", best)) return false;
    }
    return run("support.start");
}

} // namespace

void CareerEntryFlow::RegisterActions(Career::CareerRuntimeConfig const& config,
    GameData::UmaGameDatabase const& trainees, GameData::CareerSelectionCatalog const& catalogs,
    TaskEngine::ActionRegistry& registry)
{
    registry.RegisterHandler("SelectUraTrainee", [&config, &trainees](auto const&, auto const&, auto& context) {
        return SelectTrainee(config, trainees, context)
            ? TaskEngine::ActionResult::Success : TaskEngine::ActionResult::Failed;
    });
    registry.RegisterHandler("SelectUraLegacy", [&config](auto const&, auto const&, auto& context) {
        return SelectLegacy(config, context)
            ? TaskEngine::ActionResult::Success : TaskEngine::ActionResult::Failed;
    });
    (void)catalogs;
}

CareerEntryResult CareerEntryFlow::Run(Career::CareerRuntimeConfig const& config,
    Career::CareerVisualResources& resources, Career::ICareerObserver& observer,
    TaskEngine::ITaskExecutionContext& context, const std::vector<std::string>& initialScreens)
{
    CareerEntryResult result;
    std::vector<std::string> runtimeScreens;
    for (auto const& screen : resources.GetScreens())
        if (screen.flow != "entry" && screen.flow != "normal" && screen.flow != "independent"
            && screen.screenId != "home" && screen.screenId != "home_unselected")
            runtimeScreens.push_back(screen.screenId);
    // Opening settings may also be resumed after an interrupted start.
    for (auto const* screen : {"career_final_confirmation", "normal_final_confirmation_startup",
        "career_intro_event", "normal_intro_event", "normal_quick_mode_settings", "career_skills"})
        runtimeScreens.push_back(screen);
    auto eligibleScreens = runtimeScreens;
    for (auto const& screen : resources.GetScreens())
        if (IsEntryScreen(screen.screenId)) eligibleScreens.push_back(screen.screenId);
    if (!initialScreens.empty()) eligibleScreens = initialScreens;
    bool supportConfigured = false;
    int tpRestores = 0;
    bool allowResume = config.continueExistingCareer;
    for (int step = 0; step < 48; ++step)
    {
        if (context.IsCancelled()) { result.cancelled = true; return result; }
        auto const observation = ObserveStable(observer, context, result, eligibleScreens, allowResume);
        allowResume = false;
        if (result.cancelled || context.IsCancelled()) { result.cancelled = true; return result; }
        auto const& screen = observation.screenId;
        if (screen == "career_final_confirmation" || IsRuntimeCareerScreen(screen))
        { result.runtimeObservation = observation; result.succeeded = true; return result; }
        if (screen == "unknown" || screen == "capture_failed" || screen == "resource_error")
        { result.error = "Career entry could not identify the current screen safely (" + screen + ")."; return result; }
        if (screen == "home")
        {
            if (!RunAction(result, resources, context, "home", "home.career")) return result;
            eligibleScreens = {"career_continue", "scenario_select"};
        }
        else if (screen == "home_unselected")
        {
            if (!RunAction(result, resources, context, "home_unselected", "home.select")) return result;
            eligibleScreens = {"home"};
        }
        else if (screen == "career_continue")
        {
            if (!RunAction(result, resources, context, screen,
                config.continueExistingCareer ? "career.continue.resume" : "career.continue.delete")) return result;
            eligibleScreens = config.continueExistingCareer ? runtimeScreens
                : std::vector<std::string>{"home", "home_unselected", "scenario_select"};
            allowResume = config.continueExistingCareer;
        }
        else if (screen == "scenario_select")
        {
            auto const frame = context.GetCurrentFrame();
            auto const selected = resources.MatchAsset("career.entry.scenario.ura_card", context,
                frame ? frame->frameId : 0);
            if (!RunAction(result, resources, context, screen,
                selected.found ? "scenario.next" : "scenario.next_card")) return result;
            eligibleScreens = {"trainee_select"};
        }
        else if (screen == "trainee_select")
        {
            if (!RunAction(result, resources, context, screen, "trainee.pick")) return result;
            // trainee.pick already chains Next in the resource graph.
            eligibleScreens = {"legacy_select"};
        }
        else if (screen == "legacy_select")
        {
            if (!RunAction(result, resources, context, screen, "legacy.choose")) return result;
            eligibleScreens = {"support_select", "support_autofill_confirmation", "support_ready"};
        }
        else if (screen == "support_select" || (screen == "support_ready"
            && !supportConfigured && config.supportDeckMode != "auto"))
        {
            GameData::CareerSelectionCatalog catalogs(config.resourceDirectory);
            std::string catalogError;
            if (!catalogs.Load(catalogError)) { result.error = catalogError; return result; }
            if (!ConfigureSupport(config, catalogs, resources, context, result))
            {
                if (result.error.empty()) result.error = "Support deck setup was not verified.";
                return result;
            }
            supportConfigured = true;
            eligibleScreens = config.supportDeckMode == "auto"
                ? std::vector<std::string>{"support_autofill_confirmation", "support_ready", "career_final_confirmation", "career_tp_restore_prompt"}
                : std::vector<std::string>{"career_final_confirmation", "career_tp_restore_prompt"};
        }
        else if (screen == "support_autofill_confirmation")
        {
            if (!RunAction(result, resources, context, screen, "support.autofill_ok")) return result;
            eligibleScreens = {"support_ready", "career_final_confirmation", "career_tp_restore_prompt"};
        }
        else if (screen == "support_ready")
        {
            if (!RunAction(result, resources, context, screen, "support.start")) return result;
            eligibleScreens = {"career_final_confirmation", "career_tp_restore_prompt"};
        }
        else if (screen == "career_tp_restore_prompt")
        {
            if (!config.autoRestoreTp)
            {
                result.error = "Insufficient TP to start Career; automatic TP restoration is disabled.";
                return result;
            }
            if (tpRestores >= 2)
            {
                result.error = "TP is still insufficient after two Toughness 30 restorations; stopping.";
                return result;
            }
            context.Log("INFO", "Insufficient TP; restoring with one Toughness 30.");
            if (!RunAction(result, resources, context, screen, "tp.restore")) return result;
            ++tpRestores;
            context.Log("INFO", "Toughness 30 confirmed; returning to Career entry.");
            // The recovery graph verifies Close before returning. Re-enter the
            // normal Start action without rebuilding the configured deck.
            eligibleScreens = {"support_ready"};
            supportConfigured = true;
        }
        else if (!IsEntryScreen(screen))
        {
            // Hand off only a screen loaded from the pinned Career resource
            // snapshot. An arbitrary observer ID is not proof that entry is
            // complete and must never become a success-shaped fallback.
            if (resources.FindScreen(screen) == nullptr)
            {
                result.error = "Career entry observed an unregistered screen and cannot safely hand it to the runtime: " + screen;
                return result;
            }
            result.runtimeObservation = observation;
            result.succeeded = true;
            return result;
        }
    }
    result.lastScreenId = "entry_budget_exhausted";
    result.error = "Career entry did not reach Final Confirmation within 48 verified transitions.";
    return result;
}

} // namespace UmaAssistant::GameTasks
