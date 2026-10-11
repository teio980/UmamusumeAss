#include "DailyRaceRunnerSelector.hpp"

#include "Common/PathEncoding.hpp"
#include "Resources/ImageTransforms.hpp"
#include "Resources/ResourceSnapshot.hpp"
#include "Vision/TemplateMatcher.hpp"

#include <algorithm>
#include <array>
#include <chrono>
#include <cctype>
#include <cmath>
#include <map>
#include <set>
#include <string_view>
#include <utility>
#include <vector>

namespace UmaAssistant::GameTasks
{

namespace
{

constexpr int kReferenceWidth = 900;
constexpr int kReferenceHeight = 1600;
constexpr int kMaximumScrolls = 16;
constexpr double kMinimumPortraitScore = 0.70;
constexpr double kMinimumSortScore = 0.30;
constexpr double kMinimumSortMargin = 0.04;
constexpr std::array<double, 18> kCardScales{
    0.32, 0.36, 0.40, 0.44, 0.48, 0.52, 0.56, 0.60, 0.64,
    0.68, 0.72, 0.76, 0.80, 0.84, 0.88, 0.92, 0.96, 1.00};
constexpr std::array<double, 6> kSelectedPortraitScales{1.40, 1.20, 1.60, 1.00, 1.80, 2.00};
constexpr std::array<int, 5> kRunnerSwipe{760, 1150, 760, 850, 550};

struct RunnerCell
{
    int x;
    int y;
    int width;
    int height;
};

constexpr std::array<RunnerCell, 15> kRunnerCells{{
    {20, 800, 160, 190}, {195, 800, 160, 190}, {370, 800, 160, 190},
    {545, 800, 160, 190}, {720, 800, 160, 190},
    {20, 1010, 160, 190}, {195, 1010, 160, 190}, {370, 1010, 160, 190},
    {545, 1010, 160, 190}, {720, 1010, 160, 190},
    {20, 1220, 160, 65}, {195, 1220, 160, 65}, {370, 1220, 160, 65},
    {545, 1220, 160, 65}, {720, 1220, 160, 65}}};

struct FilterTemplate
{
    std::string_view label;
    std::string_view path;
    std::array<int, 4> roi;
};

constexpr std::array<FilterTemplate, 10> kFilterTemplates{{
    {"Turf", "templates/daily_race/runner_filter_turf.png", {20, 280, 280, 180}},
    {"Dirt", "templates/daily_race/runner_filter_dirt.png", {300, 280, 300, 180}},
    {"Sprint", "templates/daily_race/runner_filter_sprint.png", {20, 440, 280, 180}},
    {"Mile", "templates/daily_race/runner_filter_mile.png", {300, 440, 280, 180}},
    {"Medium", "templates/daily_race/runner_filter_medium.png", {580, 440, 300, 180}},
    {"Long", "templates/daily_race/runner_filter_long.png", {20, 550, 280, 180}},
    {"Front", "templates/daily_race/runner_filter_front.png", {20, 700, 280, 180}},
    {"Pace", "templates/daily_race/runner_filter_pace.png", {300, 700, 280, 180}},
    {"Late", "templates/daily_race/runner_filter_late.png", {580, 700, 300, 180}},
    {"End", "templates/daily_race/runner_filter_end.png", {20, 790, 280, 180}}}};

struct RunnerTemplate
{
    std::string path;
    Vision::GrayImage image;
    bool screenshotCrop{false};
};

struct RunnerCellMatch
{
    RunnerCell cell{};
    double score{0.0};
    double requiredScore{kMinimumPortraitScore};
};

int RoundToEven(double value)
{
    return static_cast<int>(std::nearbyint(value));
}

int ScaleCoordinate(int value, int actual, int reference)
{
    return RoundToEven(value * static_cast<double>(std::max(1, actual))
        / std::max(1, reference));
}

std::string Lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

Vision::GrayImage ToVisionImage(TaskEngine::TaskFrameSnapshot const& frame)
{
    return Vision::GrayImage{frame.width, frame.height, frame.grayPixels, frame.rgbaPixels};
}

std::optional<TaskEngine::TaskRoi> ToRoi(std::array<int, 4> const& rect)
{
    return TaskEngine::TaskRoi{rect[0], rect[1], rect[2], rect[3]};
}

void Log(TaskEngine::ITaskExecutionContext& context, std::string const& message)
{
    context.Log("INFO", "dailyRaceSelectRunner: " + message);
}

bool Capture(TaskEngine::ITaskExecutionContext& context)
{
    context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
    return !context.IsCancelled() && context.CaptureScreen()
        && static_cast<bool>(context.GetCurrentFrame());
}

bool TapReference(TaskEngine::ITaskExecutionContext& context, int x, int y)
{
    context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
    return context.Tap(x, y);
}

bool Swipe(TaskEngine::ITaskExecutionContext& context, std::array<int, 5> const& rect, std::string const& name)
{
    context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
    context.Log("INFO", "dailyRaceSelectRunner: " + name);
    return context.Swipe(rect[0], rect[1], rect[2], rect[3], rect[4]);
}

TaskEngine::TaskMatchResult Match(
    TaskEngine::ITaskExecutionContext& context,
    std::string const& path,
    std::array<int, 4> const& roi,
    double threshold,
    int referenceWidth = kReferenceWidth,
    int referenceHeight = kReferenceHeight)
{
    auto match = context.MatchTemplateAdvanced(
        path, threshold, ToRoi(roi), "MatchTemplate", referenceWidth, referenceHeight, {});
    return match;
}

TaskEngine::TaskMatchResult WaitForMatch(
    TaskEngine::ITaskExecutionContext& context,
    std::string const& path,
    std::array<int, 4> const& roi,
    double threshold,
    int timeoutMilliseconds,
    std::string const& eventName)
{
    auto const start = std::chrono::steady_clock::now();
    auto const timeout = std::chrono::milliseconds(std::clamp(timeoutMilliseconds, 0, 600000));
    do
    {
        if (context.IsCancelled() || !Capture(context))
            return {};
        auto match = Match(context, path, roi, threshold);
        if (match.found) return match;
        if (std::chrono::steady_clock::now() - start >= timeout)
            break;
        context.Sleep(250);
    } while (std::chrono::steady_clock::now() - start <= timeout);
    Log(context, "Timed out waiting for " + eventName + ".");
    return {};
}

TaskEngine::TaskMatchResult WaitForSortDirection(
    TaskEngine::ITaskExecutionContext& context,
    std::string const& descendingPath,
    std::string const& ascendingPath,
    bool wantDescending,
    int timeoutMilliseconds,
    std::string const& eventName)
{
    constexpr std::array<int, 4> roi{700, 1120, 200, 180};
    auto const start = std::chrono::steady_clock::now();
    auto const timeout = std::chrono::milliseconds(std::clamp(timeoutMilliseconds, 0, 600000));
    do
    {
        if (context.IsCancelled() || !Capture(context)) return {};
        auto desc = Match(context, descendingPath, roi, 0.0);
        auto asc = Match(context, ascendingPath, roi, 0.0);
        auto const& desired = wantDescending ? desc : asc;
        auto const& other = wantDescending ? asc : desc;
        if (desired.score >= kMinimumSortScore
            && desired.score >= other.score + kMinimumSortMargin)
        {
            auto result = desired;
            result.found = true;
            return result;
        }
        if (std::chrono::steady_clock::now() - start >= timeout) break;
        context.Sleep(250);
    } while (std::chrono::steady_clock::now() - start <= timeout);
    Log(context, "Could not verify " + eventName + ".");
    return {};
}

bool EnsureDescendingSort(
    TaskEngine::ITaskExecutionContext& context,
    std::filesystem::path const& pipelineDirectory)
{
    auto const descending = Paths::ToUtf8(pipelineDirectory / "templates/daily_race/runner_sort_desc.png");
    auto const ascending = Paths::ToUtf8(pipelineDirectory / "templates/daily_race/runner_sort_asc.png");
    auto found = WaitForSortDirection(context, descending, ascending, true, 5000, "Rating descending sort");
    if (found.found) return true;

    auto ascendingMatch = WaitForSortDirection(
        context, descending, ascending, false, 8000, "Rating ascending sort");
    if (!ascendingMatch.found || !context.TapMatch(ascendingMatch.CenterX(), ascendingMatch.CenterY(), ascendingMatch))
        return false;

    context.Sleep(600);
    return WaitForSortDirection(context, descending, ascending, true, 8000,
        "Rating descending sort after toggle").found;
}

bool TapReferenceRect(TaskEngine::ITaskExecutionContext& context, std::array<int, 4> const& rect)
{
    if (!Capture(context)) return false;
    return TapReference(context, rect[0] + rect[2] / 2, rect[1] + rect[3] / 2);
}

bool TapTemplate(
    TaskEngine::ITaskExecutionContext& context,
    std::filesystem::path const& pipelineDirectory,
    std::string_view templatePath,
    std::array<int, 4> const& roi,
    double threshold,
    std::string const& eventName)
{
    auto const path = Paths::ToUtf8(pipelineDirectory / Paths::FromUtf8(templatePath));
    auto match = WaitForMatch(context, path, roi, threshold, 8000, eventName);
    return match.found && context.TapMatch(match.CenterX(), match.CenterY(), match);
}

int GradeRank(std::string const& grade)
{
    auto const value = Lower(grade);
    if (value == "s") return 8;
    if (value == "a") return 7;
    if (value == "b") return 6;
    if (value == "c") return 5;
    if (value == "d") return 4;
    if (value == "e") return 3;
    if (value == "f") return 2;
    if (value == "g") return 1;
    return 0;
}

std::vector<std::string> BestAptitudeKeys(
    std::map<std::string, std::string, std::less<>> const& aptitudes)
{
    auto best = -1;
    for (auto const& [_, grade] : aptitudes)
        best = std::max(best, GradeRank(grade));
    std::vector<std::string> result;
    for (auto const& [name, grade] : aptitudes)
        if (GradeRank(grade) == best) result.push_back(Lower(name));
    return result;
}

std::vector<std::string> DesiredFilterLabels(GameData::UmaTraineeRecord const& trainee)
{
    std::vector<std::string> labels;
    for (auto const& key : BestAptitudeKeys(trainee.surfaceAptitudes))
    {
        if (key == "turf") labels.emplace_back("Turf");
        else if (key == "dirt") labels.emplace_back("Dirt");
    }
    for (auto const& key : BestAptitudeKeys(trainee.distanceAptitudes))
    {
        if (key == "short") labels.emplace_back("Sprint");
        else if (key == "mile") labels.emplace_back("Mile");
        else if (key == "medium") labels.emplace_back("Medium");
        else if (key == "long") labels.emplace_back("Long");
    }
    for (auto const& key : BestAptitudeKeys(trainee.strategyAptitudes))
    {
        if (key == "front") labels.emplace_back("Front");
        else if (key == "pace") labels.emplace_back("Pace");
        else if (key == "late") labels.emplace_back("Late");
        else if (key == "end") labels.emplace_back("End");
    }
    return labels;
}

bool ApplyAptitudeFilters(
    TaskEngine::ITaskExecutionContext& context,
    std::filesystem::path const& pipelineDirectory,
    GameData::UmaTraineeRecord const& trainee)
{
    auto const desired = DesiredFilterLabels(trainee);
    int clicked = 0;
    std::vector<std::string> missing;
    for (auto const& label : desired)
    {
        auto const filter = std::find_if(kFilterTemplates.begin(), kFilterTemplates.end(), [&label](auto const& item) {
            return item.label == label;
        });
        if (filter == kFilterTemplates.end()) continue;
        auto const path = Paths::ToUtf8(pipelineDirectory / std::filesystem::path(filter->path));
        auto match = WaitForMatch(context, path, filter->roi, 0.78, 8000, "runner filter " + label);
        if (!match.found || !context.TapMatch(match.CenterX(), match.CenterY(), match))
            missing.push_back(label);
        else
            ++clicked;
        context.Sleep(150);
    }
    if (clicked == 0 || !missing.empty())
    {
        std::string detail;
        for (auto const& label : missing)
        {
            if (!detail.empty()) detail += ", ";
            detail += label;
        }
        Log(context, "Runner aptitude filter could not be applied completely. Missing: " + detail);
        return false;
    }
    return true;
}

std::vector<RunnerTemplate> LoadRunnerTemplates(
    Resources::ResourceSnapshot const& resources,
    std::vector<std::filesystem::path> const& paths)
{
    std::vector<RunnerTemplate> templates;
    templates.reserve(paths.size());
    for (auto const& path : paths)
    {
        auto const screenshot = Lower(Paths::ToUtf8(path.parent_path().filename())) == "system_reference";
        auto const preparation = screenshot
            ? Resources::TemplatePreparation::original
            : Resources::TemplatePreparation::portrait;
        auto image = resources.load_template(Paths::ToUtf8(path), preparation);
        if (image)
            templates.push_back(RunnerTemplate{Paths::ToUtf8(path), image->image, screenshot});
    }
    return templates;
}

double CompareCell(
    Vision::GrayImage const& screen,
    RunnerTemplate const& templ,
    RunnerCell const& cell)
{
    auto const x = ScaleCoordinate(cell.x, screen.width, kReferenceWidth);
    auto const y = ScaleCoordinate(cell.y, screen.height, kReferenceHeight);
    auto const width = std::max(1, ScaleCoordinate(cell.width, screen.width, kReferenceWidth));
    auto const height = std::max(1, ScaleCoordinate(cell.height, screen.height, kReferenceHeight));
    if (x < 0 || y < 0 || x + width > screen.width || y + height > screen.height)
        return 0.0;

    if (templ.screenshotCrop)
    {
        auto const match = Vision::find_scaled(
            screen, templ.image, std::array<std::int32_t, 4>{x, y, width, height},
            0.0, screen.width, screen.height,
            Vision::ScaledOptions{std::vector<double>(kCardScales.begin(), kCardScales.end())});
        return match.score;
    }

    return Vision::score_masked_region(
        screen, templ.image, std::array<std::int32_t, 4>{x, y, width, height}, 0.0).score;
}

RunnerCellMatch FindBestRunnerCell(
    Vision::GrayImage const& screen,
    std::vector<RunnerTemplate> const& templates)
{
    std::vector<RunnerTemplate const*> systemReferences;
    for (auto const& templ : templates)
        if (templ.screenshotCrop) systemReferences.push_back(&templ);
    bool const useSystemReferences = !systemReferences.empty();

    RunnerCellMatch best{kRunnerCells.front(), 0.0, kMinimumPortraitScore};
    for (auto const& cell : kRunnerCells)
    {
        for (auto const& templ : templates)
        {
            if (useSystemReferences && !templ.screenshotCrop) continue;
            auto const score = CompareCell(screen, templ, cell);
            if (score > best.score)
                best = RunnerCellMatch{cell, score, kMinimumPortraitScore};
            if (score >= kMinimumPortraitScore)
                return RunnerCellMatch{cell, score, kMinimumPortraitScore};
        }
    }
    return best;
}

TaskEngine::TaskMatchResult MakeCellMatch(
    RunnerCellMatch const& best,
    TaskEngine::TaskFrameSnapshot const& frame)
{
    TaskEngine::TaskMatchResult match;
    match.found = best.score >= best.requiredScore;
    match.score = best.score;
    // Cell coordinates are already in the pipeline's reference space.
    // TapMatch uses this provenance to map them to the physical source frame.
    match.x = best.cell.x;
    match.y = best.cell.y;
    match.width = best.cell.width;
    match.height = best.cell.height;
    match.referenceWidth = kReferenceWidth;
    match.referenceHeight = kReferenceHeight;
    match.frameId = frame.frameId;
    match.sessionGeneration = frame.sessionGeneration;
    match.geometryGeneration = frame.geometryGeneration;
    return match;
}

double FindSelectedPortraitScore(
    Vision::GrayImage const& screen,
    std::vector<RunnerTemplate> const& templates)
{
    std::vector<RunnerTemplate const*> screenshotTemplates;
    for (auto const& templ : templates)
        if (templ.screenshotCrop) screenshotTemplates.push_back(&templ);
    if (screenshotTemplates.empty()) return kMinimumPortraitScore;

    auto const regionX = ScaleCoordinate(20, screen.width, kReferenceWidth);
    auto const regionY = ScaleCoordinate(180, screen.height, kReferenceHeight);
    auto const regionWidth = ScaleCoordinate(410, screen.width, kReferenceWidth);
    auto const regionHeight = ScaleCoordinate(470, screen.height, kReferenceHeight);
    auto const referenceScale = std::min(
        screen.width / static_cast<double>(kReferenceWidth),
        screen.height / static_cast<double>(kReferenceHeight));
    double best = 0.0;
    for (auto const* templ : screenshotTemplates)
    {
        for (auto const scale : kSelectedPortraitScales)
        {
            auto const targetWidth = std::max(1, RoundToEven(templ->image.width * referenceScale * scale));
            auto const targetHeight = std::max(1, RoundToEven(templ->image.height * referenceScale * scale));
            if (targetWidth > regionWidth || targetHeight > regionHeight) continue;
            auto const match = Vision::find_screenshot_crop(
                screen, templ->image,
                std::array<std::int32_t, 4>{regionX, regionY, regionWidth, regionHeight},
                targetWidth, targetHeight, 0.0);
            best = std::max(best, match.score);
        }
    }
    return best;
}

} // namespace

DailyRaceRunnerSelectionResult DailyRaceRunnerSelector::Select(
    TaskEngine::ITaskExecutionContext& context,
    const TaskEngine::HachimiPipelineDefinition& definition,
    const std::filesystem::path& resourceRoot,
    std::optional<int> traineeId,
    const Resources::ResourceSnapshot* resourceSnapshot)
{
    try
    {
        auto const pipelineDirectory = Paths::FromUtf8(definition.baseDirectory);
        if (!EnsureDescendingSort(context, pipelineDirectory))
            return {false, "Could not verify Rating descending order on the runner list."};

        if (!traineeId)
        {
            constexpr std::array<int, 4> firstCard{20, 800, 160, 190};
            if (!TapReferenceRect(context, firstCard))
                return {false, "Could not select the highest rated Daily Race runner."};
            return {true, "Selected the highest rated runner after Rating descending sort."};
        }

        GameData::UmaGameDatabase database(resourceSnapshot == nullptr
            ? resourceRoot : resourceSnapshot->root());
        std::string databaseError;
        if (!database.Load(databaseError))
            return {false, databaseError};
        auto const* trainee = database.TryGetTrainee(*traineeId);
        if (trainee == nullptr || !trainee->available)
            return {false, "Configured Daily Race runner ID " + std::to_string(*traineeId)
                + " is missing or unavailable in the UMA database."};

        auto const imagePaths = database.GetTraineeImagePaths(*trainee);
        if (imagePaths.empty())
            return {false, "No local runner image template is available for " + trainee->nameEn
                + " (" + std::to_string(trainee->traineeId) + ")."};

        constexpr std::array<int, 4> displayButtonRoi{400, 1100, 500, 250};
        if (!TapTemplate(context, pipelineDirectory,
                "templates/daily_race/runner_display_button.png", displayButtonRoi,
                0.80, "runner display settings button"))
            return {false, "Could not open the Daily Race runner display settings."};
        context.Sleep(350);

        auto const settingsPath = Paths::ToUtf8(pipelineDirectory / "templates/daily_race/runner_display_settings.png");
        if (!WaitForMatch(context, settingsPath, {0, 0, 900, 800}, 0.78, 8000,
                "runner display settings").found)
            return {false, "The Daily Race runner display settings did not appear."};

        if (!TapReferenceRect(context, {450, 120, 430, 100}))
            return {false, "Could not open runner aptitude filters."};
        context.Sleep(350);
        if (!TapReferenceRect(context, {620, 1280, 260, 110}))
            return {false, "Could not reset runner aptitude filters."};
        context.Sleep(250);
        if (!ApplyAptitudeFilters(context, pipelineDirectory, *trainee))
            return {false, "The configured runner aptitude filters could not be applied completely."};

        if (!TapTemplate(context, pipelineDirectory,
                "templates/daily_race/runner_filter_confirm.png", {300, 1300, 550, 250},
                0.80, "runner filter confirmation"))
            return {false, "Could not confirm the Daily Race runner filters."};
        context.Sleep(700);

        if (resourceSnapshot == nullptr)
            return {false, "The run resource snapshot is unavailable."};
        auto templates = LoadRunnerTemplates(*resourceSnapshot, imagePaths);
        if (templates.empty())
            return {false, "Could not decode any local image template for " + trainee->nameEn + "."};

        std::optional<RunnerCellMatch> bestCandidate;
        double bestObservedScore = 0.0;
        for (int scroll = 0; scroll <= kMaximumScrolls; ++scroll)
        {
            if (context.IsCancelled())
                return {false, "Daily Race runner selection was canceled."};
            if (!Capture(context))
                return {false, "Could not capture the filtered runner list."};
            auto const frame = context.GetCurrentFrame();
            if (!frame) return {false, "Runner-list screenshot evidence expired."};
            auto const best = FindBestRunnerCell(ToVisionImage(*frame), templates);
            bestObservedScore = std::max(bestObservedScore, best.score);
            Log(context, "Runner page " + std::to_string(scroll + 1) + ": score "
                + std::to_string(best.score) + " / required " + std::to_string(best.requiredScore)
                + " at cell (" + std::to_string(best.cell.x) + "," + std::to_string(best.cell.y) + ").");
            if (best.score >= best.requiredScore)
            {
                bestCandidate = best;
                break;
            }
            if (scroll == kMaximumScrolls) break;
            if (!Swipe(context, kRunnerSwipe, "scroll runner list"))
                return {false, "Could not scroll the runner list safely."};
            context.Sleep(350);
        }

        if (!bestCandidate)
            return {false, "Filtered the runner list but could not identify " + trainee->nameEn
                + " (" + std::to_string(trainee->traineeId) + "); best score "
                + std::to_string(bestObservedScore) + " / threshold 0.700."};

        // The first display-priority match stops the scan on its own page, so
        // the selected card is still visible; capture it again before tapping.
        if (!Capture(context))
            return {false, "Found " + trainee->nameEn + " but could not recapture before selection."};
        auto const selectedFrame = context.GetCurrentFrame();
        if (!selectedFrame)
            return {false, "Runner-list screenshot expired before selection."};
        auto const selectedBest = FindBestRunnerCell(ToVisionImage(*selectedFrame), templates);
        if (selectedBest.score < selectedBest.requiredScore)
            return {false, "Found " + trainee->nameEn + " but the runner card could not be reverified."};
        auto const match = MakeCellMatch(selectedBest, *selectedFrame);
        if (!context.TapMatch(match.CenterX(), match.CenterY(), match))
            return {false, "Found " + trainee->nameEn + " but its runner card could not be selected."};

        context.Sleep(500);
        if (!Capture(context))
            return {false, "Selected runner detail could not be captured."};
        auto const detailFrame = context.GetCurrentFrame();
        if (!detailFrame)
            return {false, "Selected runner detail screenshot expired."};
        auto const detailScore = FindSelectedPortraitScore(ToVisionImage(*detailFrame), templates);
        if (detailScore < kMinimumPortraitScore)
            return {false, "Tapped a card for " + trainee->nameEn
                + " but the selected runner detail did not match (score "
                + std::to_string(detailScore) + " / threshold 0.700).", bestCandidate->score, detailScore};

        return {true, "Filtered and selected " + trainee->nameEn + " ("
            + std::to_string(trainee->traineeId) + "); card score "
            + std::to_string(bestCandidate->score) + ", detail score "
            + std::to_string(detailScore) + ".", bestCandidate->score, detailScore};
    }
    catch (std::exception const& exception)
    {
        return {false, "Daily Race runner selection failed: " + std::string(exception.what())};
    }
}

} // namespace UmaAssistant::GameTasks
