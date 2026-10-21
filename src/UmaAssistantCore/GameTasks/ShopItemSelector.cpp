#include "ShopItemSelector.hpp"

#include "Common/PathEncoding.hpp"
#include "Resources/ImageTransforms.hpp"
#include "Resources/ResourceSnapshot.hpp"
#include "Vision/TemplateMatcher.hpp"

#include <algorithm>
#include <array>
#include <chrono>
#include <cctype>
#include <cmath>
#include <filesystem>
#include <optional>
#include <set>
#include <string>
#include <vector>

namespace UmaAssistant::GameTasks
{

namespace
{

constexpr int kReferenceWidth = 900;
constexpr int kReferenceHeight = 1600;
constexpr int kCheckboxX = 765;
constexpr int kCheckboxBelowName = 42;
constexpr int kMaximumScrolls = 30;
constexpr std::array<int, 4> kListTextRoi{175, 585, 520, 565};
constexpr std::array<int, 5> kScrollDown{450, 1050, 450, 850, 450};
constexpr std::array<int, 5> kScrollUp{450, 650, 450, 1050, 450};
constexpr std::array<double, 4> kCheckboxScales{0.78, 0.85, 0.92, 1.0};

enum class Category
{
    star_pieces,
    alarm_clock,
    pleasing_parfait,
    shoes,
    support_points,
    flags
};

struct CategoryDescriptor
{
    Category category;
    std::string_view name;
};

constexpr std::array<CategoryDescriptor, 6> kCategories{{
    {Category::star_pieces, "starPieces"},
    {Category::alarm_clock, "alarmClock"},
    {Category::pleasing_parfait, "pleasingParfait"},
    {Category::shoes, "shoes"},
    {Category::support_points, "supportPoints"},
    {Category::flags, "flags"},
}};

int RoundToEven(double value)
{
    return static_cast<int>(std::nearbyint(value));
}

int ToReference(int value, int actualSize, int referenceSize)
{
    return RoundToEven(value * static_cast<double>(referenceSize)
        / std::max(1, actualSize));
}

int ToActual(int value, int actualSize, int referenceSize)
{
    return std::clamp(
        RoundToEven(value * static_cast<double>(actualSize) / std::max(1, referenceSize)),
        0,
        std::max(0, actualSize - 1));
}

Vision::GrayImage ToVisionImage(TaskEngine::TaskFrameSnapshot const& frame)
{
    return Vision::GrayImage{frame.width, frame.height, frame.grayPixels, frame.rgbaPixels};
}

std::string Normalize(std::string_view text)
{
    std::string result;
    result.reserve(text.size());
    for (unsigned char character : text)
    {
        if ((character >= 'A' && character <= 'Z')
            || (character >= 'a' && character <= 'z')
            || (character >= '0' && character <= '9'))
        {
            result.push_back(static_cast<char>(std::tolower(character)));
        }
    }
    return result;
}

bool IsSelected(ShopPurchaseOptions const& options, Category category)
{
    switch (category)
    {
    case Category::star_pieces: return options.buyStarPieces;
    case Category::alarm_clock: return options.buyAlarmClock;
    case Category::pleasing_parfait: return options.buyPleasingParfait;
    case Category::shoes: return options.buyShoes;
    case Category::support_points: return options.buySupportPoints;
    case Category::flags: return options.buyFlags;
    }
    return false;
}

bool HasIndividualSelections(ShopPurchaseOptions const& options)
{
    return options.buyStarPieces || options.buyAlarmClock || options.buyPleasingParfait
        || options.buyShoes || options.buySupportPoints || options.buyFlags;
}

std::optional<Category> ClassifyCategory(std::string_view text)
{
    auto const normalized = Normalize(text);
    if (normalized.find("starpiece") != std::string::npos
        || normalized.find("startpiece") != std::string::npos)
        return Category::star_pieces;
    if (normalized.find("alarmclock") != std::string::npos)
        return Category::alarm_clock;
    if (normalized.find("pleasingparfait") != std::string::npos)
        return Category::pleasing_parfait;
    if (normalized.find("racingshoes") != std::string::npos)
        return Category::shoes;
    if (normalized.find("supportpoints") != std::string::npos)
        return Category::support_points;
    if (normalized.find("winnerssash") != std::string::npos
        || normalized.find("winningflag") != std::string::npos)
        return Category::flags;
    return std::nullopt;
}

std::optional<std::array<int, 2>> ExpectedCheckbox(
    TaskEngine::TaskOcrDetection const& detection)
{
    auto const nameY = detection.y + detection.height / 2;
    auto const nameX = detection.x;
    if (nameX < 165 || nameX > 670 || nameY < 592 || nameY > 1060)
        return std::nullopt;
    return std::array<int, 2>{kCheckboxX, nameY + kCheckboxBelowName};
}

std::string BuildRowKey(Category category, std::string_view text, int y)
{
    return std::to_string(static_cast<int>(category)) + ":" + Normalize(text) + ":"
        + std::to_string(y / 32);
}

bool IsSameList(TaskEngine::TaskFrameSnapshot const& before, TaskEngine::TaskFrameSnapshot const& after)
{
    if (before.width != after.width || before.height != after.height
        || before.width <= 0 || before.height <= 0)
        return false;
    auto const pixelCount = static_cast<std::uint64_t>(before.width) * before.height;
    if (pixelCount > before.grayPixels.size() || pixelCount > after.grayPixels.size())
        return false;

    std::uint64_t listDifference = 0;
    int listSamples = 0;
    for (int referenceY = 610; referenceY <= 1100; referenceY += 12)
    {
        auto const y = ToActual(referenceY, before.height, kReferenceHeight);
        for (int referenceX = 190; referenceX <= 650; referenceX += 12)
        {
            auto const x = ToActual(referenceX, before.width, kReferenceWidth);
            auto const index = static_cast<std::size_t>(y) * before.width + x;
            listDifference += static_cast<std::uint64_t>(std::abs(
                static_cast<int>(before.grayPixels[index]) - after.grayPixels[index]));
            ++listSamples;
        }
    }

    std::uint64_t scrollbarDifference = 0;
    int scrollbarSamples = 0;
    auto const scrollbarX = ToActual(876, before.width, kReferenceWidth);
    for (int referenceY = 610; referenceY <= 1130; referenceY += 6)
    {
        auto const y = ToActual(referenceY, before.height, kReferenceHeight);
        auto const index = static_cast<std::size_t>(y) * before.width + scrollbarX;
        scrollbarDifference += static_cast<std::uint64_t>(std::abs(
            static_cast<int>(before.grayPixels[index]) - after.grayPixels[index]));
        ++scrollbarSamples;
    }

    return listSamples > 0 && scrollbarSamples > 0
        && static_cast<double>(listDifference) / listSamples < 3.0
        && static_cast<double>(scrollbarDifference) / scrollbarSamples < 5.0;
}

bool Capture(TaskEngine::ITaskExecutionContext& context)
{
    context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
    return !context.IsCancelled() && context.CaptureScreen()
        && static_cast<bool>(context.GetCurrentFrame());
}

bool TapReferencePoint(
    TaskEngine::ITaskExecutionContext& context,
    int x,
    int y)
{
    context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
    return context.Tap(x, y);
}

bool TapMatch(TaskEngine::ITaskExecutionContext& context, TaskEngine::TaskMatchResult const& match)
{
    if (!match.found) return false;
    return context.TapMatch(match.CenterX(), match.CenterY(), match);
}

bool SwipeReference(
    TaskEngine::ITaskExecutionContext& context,
    std::array<int, 5> const& swipe,
    std::string const& eventName)
{
    context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
    context.Log("INFO", eventName);
    return context.Swipe(swipe[0], swipe[1], swipe[2], swipe[3], swipe[4]);
}

bool IsCheckedOnCurrentFrame(
    TaskEngine::ITaskExecutionContext& context,
    int referenceX,
    int referenceY)
{
    if (!context.GetCurrentFrame()) return false;
    context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
    return context.ProbeHsv(referenceX, referenceY, 22, 70, 160, 0.38, 1, 0.3, 1, 0.10);
}

bool IsChecked(
    TaskEngine::ITaskExecutionContext& context,
    int referenceX,
    int referenceY,
    int timeoutMilliseconds)
{
    auto const started = std::chrono::steady_clock::now();
    do
    {
        if (!Capture(context)) return false;
        if (IsCheckedOnCurrentFrame(context, referenceX, referenceY)) return true;
        if (timeoutMilliseconds <= 0) return false;
        context.Sleep(120);
    } while (std::chrono::duration_cast<std::chrono::milliseconds>(
                 std::chrono::steady_clock::now() - started).count() < timeoutMilliseconds);
    return false;
}

void Log(TaskEngine::ITaskExecutionContext& context, std::string const& message)
{
    context.Log("INFO", "shopSelectItems: " + message);
}

} // namespace

std::string_view ShopItemSelector::Classify(std::string_view text) noexcept
{
    auto const category = ClassifyCategory(text);
    if (!category) return {};
    return kCategories[static_cast<std::size_t>(*category)].name;
}

ShopSelectionResult ShopItemSelector::Select(
    TaskEngine::ITaskExecutionContext& context,
    TaskEngine::HachimiPipelineDefinition const& definition,
    ShopPurchaseOptions const& options,
    const Resources::ResourceSnapshot* resourceSnapshot)
{
    if (!options.enabled)
    {
        return {true, false, 0, 0, "Shop purchases are disabled in Settings."};
    }
    if (!HasIndividualSelections(options))
    {
        Log(context, "No individual shop items are enabled in Settings.");
        return {false, false, 0, 0, "No individual shop selections were configured."};
    }

    auto const pipelineDirectory = Paths::FromUtf8(definition.baseDirectory);
    if (resourceSnapshot == nullptr)
    {
        Log(context, "The run resource snapshot is unavailable; skipping purchase.");
        return {false, false, 0, 1, "The run resource snapshot is unavailable."};
    }
    auto const checkboxPath = pipelineDirectory / "templates" / "shop" / "item_checkbox.png";
    auto const disabledConfirmPath = pipelineDirectory / "templates" / "shop" / "confirm_disabled.png";
    auto const checkboxPathUtf8 = Paths::ToUtf8(checkboxPath);
    auto const disabledConfirmPathUtf8 = Paths::ToUtf8(disabledConfirmPath);
    auto const checkboxTemplate = resourceSnapshot->load_template(checkboxPathUtf8, Resources::TemplatePreparation::original);
    auto const disabledConfirmTemplate = resourceSnapshot->load_template(
        disabledConfirmPathUtf8, Resources::TemplatePreparation::original);
    if (!checkboxTemplate || !disabledConfirmTemplate)
    {
        Log(context, "Shop selection references are unavailable; skipping purchase.");
        return {false, false, 0, 1, "Shop selection references are unavailable."};
    }

    context.Log("INFO", "shopSelectItems: clearing any previous manual selection.");
    if (!Capture(context) || !TapReferencePoint(context, 770, 1350))
        return {false, false, 0, 1, "Could not reset previous shop selections."};
    context.Sleep(250);

    if (!Capture(context))
        return {false, false, 0, 1, "Could not capture the shop list after reset."};
    auto frame = context.GetCurrentFrame();
    auto resetMatch = context.MatchTemplateAdvanced(
        disabledConfirmPathUtf8, 0.80,
        TaskEngine::TaskRoi{250, 1250, 430, 240},
        "MatchTemplate", kReferenceWidth, kReferenceHeight, {});
    if (!resetMatch.found)
    {
        context.Sleep(350);
        if (!Capture(context))
            return {false, false, 0, 1, "Could not verify shop selection reset."};
        frame = context.GetCurrentFrame();
        resetMatch = context.MatchTemplateAdvanced(
            disabledConfirmPathUtf8, 0.80,
            TaskEngine::TaskRoi{250, 1250, 430, 240},
            "MatchTemplate", kReferenceWidth, kReferenceHeight, {});
        if (!resetMatch.found)
        {
            Log(context, "Reset did not clear previous shop selections; skipping purchase.");
            return {false, false, 0, 1, "The shop selection reset could not be verified."};
        }
    }

    bool reachedTop = false;
    for (int rewind = 0; rewind < kMaximumScrolls; ++rewind)
    {
        if (!Capture(context) || !SwipeReference(context, kScrollUp, "rewind shop list"))
            break;
        context.Sleep(350);
        auto const before = context.GetCurrentFrame();
        if (!Capture(context)) break;
        auto const after = context.GetCurrentFrame();
        if (before && after && IsSameList(*before, *after))
        {
            reachedTop = true;
            break;
        }
    }
    if (!reachedTop)
    {
        Log(context, "Could not establish the top of the shop list; skipping purchase.");
        return {false, false, 0, 1, "Could not establish the top of the shop list."};
    }

    int selected = 0;
    int skipped = 0;
    bool hasSelection = false;
    std::set<Category> observed;
    bool reachedBottom = false;
    for (int page = 0; page <= kMaximumScrolls; ++page)
    {
        if (context.IsCancelled())
            return {false, hasSelection, selected, skipped, "Shop item selection was canceled."};

        std::set<std::string> attemptedRows;
        bool reobservePage = true;
        while (reobservePage)
        {
            reobservePage = false;
            auto current = context.GetCurrentFrame();
            if (!current)
            {
                if (!Capture(context))
                {
                    ++skipped;
                    Log(context, "Could not capture the current shop list; skipped visible rows.");
                    break;
                }
                current = context.GetCurrentFrame();
            }
            context.SetCoordinateReference(kReferenceWidth, kReferenceHeight);
            auto const recognized = context.RecognizeText(
                "en-US", TaskEngine::TaskRoi{
                    kListTextRoi[0], kListTextRoi[1], kListTextRoi[2], kListTextRoi[3]});
            if (recognized.empty())
            {
                ++skipped;
                Log(context, "Page " + std::to_string(page + 1) + ": OCR unavailable; skipped visible rows.");
            }
            else
            {
                auto detections = recognized;
                std::stable_sort(detections.begin(), detections.end(), [](auto const& left, auto const& right) {
                    return left.y < right.y;
                });
                for (auto const& detection : detections)
                {
                    auto const category = ClassifyCategory(detection.text);
                    if (!category || !IsSelected(options, *category)) continue;
                    // OCR detections are expressed in the active task's
                    // reference coordinates, like pipeline ROIs and actions.
                    auto const expected = ExpectedCheckbox(detection);
                    if (!expected) continue;
                    observed.insert(*category);
                    auto const x = (*expected)[0];
                    auto const y = (*expected)[1];
                    auto const rowKey = BuildRowKey(*category, detection.text, y);
                    if (attemptedRows.contains(rowKey)) continue;

                    if (IsCheckedOnCurrentFrame(context, x, y))
                    {
                        hasSelection = true;
                        continue;
                    }

                    std::array<int, 4> checkboxRoi{x - 55, y - 55, 110, 110};
                    auto const currentImage = ToVisionImage(*current);
                    Vision::ScaledOptions scaledOptions;
                    scaledOptions.scales.assign(kCheckboxScales.begin(), kCheckboxScales.end());
                    auto const visionMatch = Vision::find_scaled(
                        currentImage,
                        checkboxTemplate->image,
                        checkboxRoi,
                        0.76,
                        kReferenceWidth,
                        kReferenceHeight,
                        scaledOptions);
                    auto const foundXRef = ToReference(
                        visionMatch.x + visionMatch.width / 2, current->width, kReferenceWidth);
                    auto const foundYRef = ToReference(
                        visionMatch.y + visionMatch.height / 2, current->height, kReferenceHeight);
                    if (!visionMatch.found || std::abs(foundXRef - x) > 20 || std::abs(foundYRef - y) > 24)
                    {
                        ++skipped;
                        Log(context, "Skipped '" + detection.text + "': adjacent checkbox was not uniquely located.");
                        continue;
                    }

                    TaskEngine::TaskMatchResult match;
                    match.found = true;
                    match.score = visionMatch.score;
                    match.x = ToReference(visionMatch.x, current->width, kReferenceWidth);
                    match.y = ToReference(visionMatch.y, current->height, kReferenceHeight);
                    match.width = ToReference(visionMatch.width, current->width, kReferenceWidth);
                    match.height = ToReference(visionMatch.height, current->height, kReferenceHeight);
                    match.referenceWidth = kReferenceWidth;
                    match.referenceHeight = kReferenceHeight;
                    match.frameId = current->frameId;
                    match.sessionGeneration = current->sessionGeneration;
                    match.geometryGeneration = current->geometryGeneration;

                    attemptedRows.insert(rowKey);
                    if (!TapMatch(context, match))
                    {
                        ++skipped;
                        Log(context, "Skipped '" + detection.text + "': checkbox tap was rejected.");
                        return {false, hasSelection, selected, skipped, "A shop checkbox could not be tapped safely."};
                    }

                    if (IsChecked(context, x, y, 1200))
                    {
                        ++selected;
                        hasSelection = true;
                        Log(context, "Selected '" + detection.text + "' at ("
                            + std::to_string(match.CenterX()) + "," + std::to_string(match.CenterY()) + ").");
                        if (!Capture(context))
                        {
                            return {false, hasSelection, selected, skipped,
                                "Could not recapture the shop list after selecting an item."};
                        }
                        reobservePage = true;
                        break;
                    }

                    ++skipped;
                    if (!Capture(context))
                    {
                        return {false, hasSelection, selected, skipped,
                            "Checkbox state was uncertain and the shop list could not be captured."};
                    }
                    auto const afterTap = context.GetCurrentFrame();
                    auto const afterMatch = afterTap ? Vision::find_scaled(
                        ToVisionImage(*afterTap), checkboxTemplate->image,
                        checkboxRoi,
                        0.76, kReferenceWidth, kReferenceHeight, scaledOptions) : Vision::MatchResult{};
                    if (!afterTap || !afterMatch.found)
                    {
                        static_cast<void>(Capture(context));
                        static_cast<void>(TapReferencePoint(context, 770, 1350));
                        Log(context, "Purchase canceled because checkbox state was uncertain.");
                        return {false, hasSelection, selected, skipped,
                            "Purchase canceled because a selected checkbox could not be verified."};
                    }

                    Log(context, "Skipped '" + detection.text + "': checkbox remained unchecked after tap.");
                    if (!Capture(context))
                        return {false, hasSelection, selected, skipped, "Could not reobserve the shop list."};
                    reobservePage = true;
                    break;
                }
            }
        }

        if (page == kMaximumScrolls) break;
        if (!Capture(context) || !SwipeReference(context, kScrollDown, "scan next shop page"))
            break;
        context.Sleep(350);
        auto const before = context.GetCurrentFrame();
        if (!Capture(context)) break;
        auto const after = context.GetCurrentFrame();
        if (!before || !after) break;
        auto const atBottom = IsSameList(*before, *after);
        if (atBottom)
        {
            reachedBottom = true;
            break;
        }
    }

    if (!reachedBottom)
    {
        static_cast<void>(Capture(context));
        static_cast<void>(TapReferencePoint(context, 770, 1350));
        Log(context, "Shop scan did not reach the bottom; selection was reset and purchase skipped.");
        return {false, false, selected, skipped + 1,
            "Shop scan did not reach the bottom; purchase was canceled."};
    }

    for (auto const& descriptor : kCategories)
    {
        if (IsSelected(options, descriptor.category) && !observed.contains(descriptor.category))
        {
            ++skipped;
            Log(context, "No OCR match for configured " + std::string(descriptor.name)
                + " in the scanned shop list; skipped.");
        }
    }

    Log(context, "Shop OCR finished: " + std::to_string(selected) + " verified row(s), "
        + std::to_string(skipped) + " skipped row(s).");
    return {true, hasSelection, selected, skipped, "Shop item selection completed."};
}

} // namespace UmaAssistant::GameTasks
