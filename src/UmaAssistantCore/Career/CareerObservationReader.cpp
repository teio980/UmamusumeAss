#include "CareerObservationReader.hpp"
#include "UmaAssistantCore/Career/Flows/CareerClawMachineFlow.hpp"

#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/Resources/ImageTransforms.hpp"
#include "UmaAssistantCore/TaskEngine/RecognitionPipeline.hpp"
#include "UmaAssistantCore/Vision/TemplateMatcher.hpp"
#include "UmaAssistantCore/Vision/ColorAnalysis.hpp"

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cmath>
#include <fstream>
#include <limits>
#include <regex>
#include <string>
#include <string_view>
#include <unordered_set>
#include <utility>

namespace UmaAssistant::Career
{

namespace
{

struct CoordinateScope
{
    TaskEngine::ITaskExecutionContext& context;
    std::pair<int, int> prior;

    explicit CoordinateScope(TaskEngine::ITaskExecutionContext& executionContext)
        : context(executionContext), prior(executionContext.GetCoordinateReference())
    {
        context.SetCoordinateReference(900, 1600);
    }

    ~CoordinateScope()
    {
        context.SetCoordinateReference(prior.first, prior.second);
    }
};

std::string Fold(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

std::string NormalizeText(const std::string& value)
{
    std::string normalized;
    normalized.reserve(value.size());
    for (unsigned char c : value)
    {
        if (std::isalnum(c))
            normalized.push_back(static_cast<char>(std::tolower(c)));
    }
    return normalized;
}

std::optional<std::uint64_t> PageSignature(
    const TaskEngine::TaskFrameSnapshot& frame, const TaskEngine::TaskRoi& roi)
{
    if (frame.width <= 0 || frame.height <= 0
        || frame.grayPixels.size() != static_cast<std::size_t>(frame.width) * frame.height
        || roi.x < 0 || roi.y < 0 || roi.width <= 0 || roi.height <= 0
        || roi.x >= 900 || roi.y >= 1600 || roi.width > 900 - roi.x || roi.height > 1600 - roi.y)
        return {};
    const int x = static_cast<int>(std::lround(roi.x * frame.width / 900.0));
    const int y = static_cast<int>(std::lround(roi.y * frame.height / 1600.0));
    const int width = static_cast<int>(std::lround(roi.width * frame.width / 900.0));
    const int height = static_cast<int>(std::lround(roi.height * frame.height / 1600.0));
    if (width <= 0 || height <= 0 || x + width > frame.width || y + height > frame.height) return {};
    std::uint64_t hash = 14695981039346656037ull;
    for (int row = y; row < y + height; ++row)
        for (int column = x; column < x + width; ++column)
        {
            hash ^= frame.grayPixels[static_cast<std::size_t>(row) * frame.width + column];
            hash *= 1099511628211ull;
        }
    return hash;
}

std::uint64_t EventSignature(const std::vector<TaskEngine::TaskOcrDetection>& detections)
{
    std::vector<TaskEngine::TaskOcrDetection> ordered;
    std::unordered_set<std::string> uniqueText;
    ordered.reserve(detections.size());
    for (const auto& detection : detections)
    {
        const auto normalized = NormalizeText(detection.text);
        if (!normalized.empty() && std::isfinite(detection.confidence)
            && detection.confidence >= 0.5)
        {
            ordered.push_back(detection);
            uniqueText.insert(normalized);
        }
    }
    if (uniqueText.size() < 2)
        return 0;
    std::stable_sort(ordered.begin(), ordered.end(), [](const auto& left, const auto& right) {
        if (left.y != right.y) return left.y < right.y;
        return left.x < right.x;
    });
    if (ordered.empty())
        return 0;
    std::uint64_t hash = 14695981039346656037ull;
    for (const auto& detection : ordered)
    {
        const auto normalized = NormalizeText(detection.text);
        for (const unsigned char value : normalized)
        {
            hash ^= value;
            hash *= 1099511628211ull;
        }
        hash ^= 0xff;
        hash *= 1099511628211ull;
    }
    return hash == 0 ? 1 : hash;
}

bool ContainsExpectedText(
    const std::vector<TaskEngine::TaskOcrDetection>& detections,
    const std::string& expected)
{
    const auto needle = NormalizeText(expected);
    if (needle.empty())
        return true;
    return std::any_of(detections.begin(), detections.end(), [&](const auto& detection) {
        return NormalizeText(detection.text).find(needle) != std::string::npos;
    });
}

TaskEngine::TaskRoi ToRoi(const std::vector<int>& bounds)
{
    return {bounds[0], bounds[1], bounds[2], bounds[3]};
}

bool SameFrame(
    TaskEngine::ITaskExecutionContext& context,
    const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& expected)
{
    const auto current = context.GetCurrentFrame();
    return current && expected && current.get() == expected.get()
        && current->frameId == expected->frameId;
}

std::optional<TaskEngine::TaskMatchResult> MatchCatalogAsset(
    TaskEngine::ITaskExecutionContext& context,
    const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& frame,
    const CareerVisualResources& resources,
    const std::string& assetId,
    const std::string& regionId)
{
    const auto* asset = resources.FindAsset(assetId);
    const auto* region = resources.FindRegion(regionId);
    if (!asset || !region || !region->is_object() || !region->contains("roi")
        || !(*region)["roi"].is_array() || (*region)["roi"].size() < 4)
        return std::nullopt;
    TaskEngine::TaskRoi roi{};
    try
    {
        roi = {(*region)["roi"][0].get<int>(), (*region)["roi"][1].get<int>(),
            (*region)["roi"][2].get<int>(), (*region)["roi"][3].get<int>()};
    }
    catch (...) { return std::nullopt; }
    const auto match = context.MatchTemplateAdvanced(
        asset->path, asset->threshold, roi, "MatchTemplateColor", 900, 1600, {});
    if (!SameFrame(context, frame)
        || (match.frameId != 0 && frame->frameId != 0 && match.frameId != frame->frameId))
        return std::nullopt;
    return match;
}

Vision::GrayImage ToVisionImage(const TaskEngine::TaskFrameSnapshot& frame)
{
    return {frame.width, frame.height, frame.grayPixels, frame.rgbaPixels};
}

bool HasMatchingGlyphColors(const TaskEngine::TaskFrameSnapshot& frame, const Vision::GrayImage& marker,
    const TaskEngine::TaskMatchResult& match, double threshold)
{
    if (frame.rgbaPixels.size() < static_cast<std::size_t>(frame.width) * frame.height * 4
        || marker.rgba.size() < static_cast<std::size_t>(marker.width) * marker.height * 4) return false;
    double error = 0;
    int count = 0;
    for (int y = 0; y < marker.height; ++y)
        for (int x = 0; x < marker.width; ++x)
        {
            auto const offset = (static_cast<std::size_t>(y) * marker.width + x) * 4;
            int const r = marker.rgba[offset], g = marker.rgba[offset + 1], b = marker.rgba[offset + 2];
            if (marker.rgba[offset + 3] < 128 || std::max({r, g, b}) - std::min({r, g, b}) < 40) continue;
            int const sx = static_cast<int>((match.x + (x + 0.5) * match.width / marker.width) * frame.width / 900.0);
            int const sy = static_cast<int>((match.y + (y + 0.5) * match.height / marker.height) * frame.height / 1600.0);
            if (sx < 0 || sy < 0 || sx >= frame.width || sy >= frame.height) return false;
            auto const pixel = (static_cast<std::size_t>(sy) * frame.width + sx) * 4;
            error += std::abs(static_cast<int>(frame.rgbaPixels[pixel]) - r)
                + std::abs(static_cast<int>(frame.rgbaPixels[pixel + 1]) - g)
                + std::abs(static_cast<int>(frame.rgbaPixels[pixel + 2]) - b);
            ++count;
        }
    return count > 0 && 1 - error / (count * 765.0) >= threshold;
}

std::optional<Vision::MatchResult> MatchClawMarker(
    TaskEngine::ITaskExecutionContext& context,
    const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& frame,
    const CareerVisualScreenDefinition& screen,
    const CareerClawMachineSettings& settings)
{
    if (!frame || screen.templates.empty())
        return std::nullopt;
    const auto image = ToVisionImage(*frame);
    for (const auto& definition : screen.templates)
    {
        const auto marker = context.LoadTemplateImage(definition.path);
        if (!marker)
            continue;
        auto match = CareerClawVision::MatchMarker(
            image, *marker, settings, definition.threshold, true, 900, 1600);
        if (!SameFrame(context, frame))
            return std::nullopt;
        if (match.found)
            return match;
    }
    return std::nullopt;
}

CareerDomain::CareerScreenKind ClassifyScreen(const CareerVisualScreenDefinition& screen)
{
    const auto id = Fold(screen.screenId);
    const auto flow = Fold(screen.flow);
    if (id == "career_skills") return CareerDomain::CareerScreenKind::SkillLearning;
    if (id == "career_main") return CareerDomain::CareerScreenKind::MainTurn;
    if (id == "training_selection") return CareerDomain::CareerScreenKind::TrainingSelect;
    if (id == "training_result") return CareerDomain::CareerScreenKind::TrainingResult;
    if (id == "infirmary_confirmation" || id == "infirmary")
        return CareerDomain::CareerScreenKind::Infirmary;
    if (id == "rest_confirmation" || id == "summer_rest_confirmation")
        return CareerDomain::CareerScreenKind::Rest;
    if (id.find("claw_machine") != std::string::npos)
        return CareerDomain::CareerScreenKind::ClawMachine;
    if (id.find("recreation") != std::string::npos)
        return CareerDomain::CareerScreenKind::Recreation;
    if (id == "goal_objective_complete"
        || id.find("goal_update") != std::string::npos
        || id.find("goal_complete") != std::string::npos
        || id.find("goal_incomplete") != std::string::npos)
        return CareerDomain::CareerScreenKind::GoalUpdate;
    if (id == "career_complete" || id == "career_complete_close"
        || flow == "settlement")
        return CareerDomain::CareerScreenKind::Settlement;
    if (flow == "event")
        return CareerDomain::CareerScreenKind::EventChoice;
    if (flow == "race")
    {
        if (id.find("runner_result") != std::string::npos || id == "race_result"
            || id.find("reward") != std::string::npos || id.find("trophy") != std::string::npos)
            return CareerDomain::CareerScreenKind::RaceResult;
        if (id.find("runner") != std::string::npos || id.find("playback") != std::string::npos
            || id.find("live") != std::string::npos || id.find("attributes") != std::string::npos)
            return CareerDomain::CareerScreenKind::RaceExecution;
        if (id.find("race") != std::string::npos)
            return CareerDomain::CareerScreenKind::RaceEntry;
    }
    if (id.find("confirmation") != std::string::npos || id.find("warning") != std::string::npos)
        return CareerDomain::CareerScreenKind::ConfirmationDialog;
    return CareerDomain::CareerScreenKind::Unknown;
}

bool IsHomeScreen(const std::string& screenId)
{
    const auto id = Fold(screenId);
    return id == "home" || id == "home_unselected";
}

bool IsReturningHomeScreen(const std::string& screenId)
{
    const auto id = Fold(screenId);
    return id == "career_complete" || id == "career_complete_close"
        || id == "career_story_unlocked" || id == "career_story_unlocked_compact"
        || id == "career_story_unlocked_to_home" || id == "home_unselected";
}

bool IsEligibleAfterSettlement(const CareerVisualScreenDefinition& screen, bool returningHome)
{
    if (returningHome)
    {
        const auto id = Fold(screen.screenId);
        return IsHomeScreen(screen.screenId)
            || id == "career_complete" || id == "career_complete_close"
            || id == "career_story_unlocked" || id == "career_story_unlocked_compact"
            || id == "career_story_unlocked_to_home";
    }
    return Fold(screen.flow) == "settlement";
}

std::optional<TaskEngine::TaskMatchResult> MatchGoalCompleteHeader(
    TaskEngine::ITaskExecutionContext& context,
    const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& frame,
    const CareerVisualScreenDefinition& screen)
{
    if (!frame || frame->width <= 0 || frame->height <= 0
        || screen.templates.empty() || screen.requiredTemplates.empty())
        return std::nullopt;
    const auto& cropDefinition = screen.requiredTemplates.front();
    if (!cropDefinition.roi)
        return std::nullopt;
    const auto& referenceRoi = *cropDefinition.roi;

    try
    {
        for (const auto& templateDefinition : screen.templates)
        {
            const auto loadedTemplate = context.LoadTemplateImage(templateDefinition.path);
            if (!loadedTemplate)
                continue;
            const auto& fullTemplate = *loadedTemplate;
            const double scaleX = static_cast<double>(fullTemplate.width) / 900.0;
            const double scaleY = static_cast<double>(fullTemplate.height) / 1600.0;
            const int x = static_cast<int>(std::lround(referenceRoi.x * scaleX));
            const int y = static_cast<int>(std::lround(referenceRoi.y * scaleY));
            const int width = static_cast<int>(std::lround(referenceRoi.width * scaleX));
            const int height = static_cast<int>(std::lround(referenceRoi.height * scaleY));
            if (x < 0 || y < 0 || width <= 0 || height <= 0
                || x + width > fullTemplate.width || y + height > fullTemplate.height
                || fullTemplate.gray.size() < static_cast<std::size_t>(fullTemplate.width) * fullTemplate.height)
                continue;

            Vision::GrayImage crop;
            crop.width = width;
            crop.height = height;
            crop.gray.resize(static_cast<std::size_t>(width) * height);
            if (fullTemplate.rgba.size() >= static_cast<std::size_t>(fullTemplate.width) * fullTemplate.height * 4)
                crop.rgba.resize(static_cast<std::size_t>(width) * height * 4);
            for (int row = 0; row < height; ++row)
            {
                const auto source = static_cast<std::size_t>(y + row) * fullTemplate.width + x;
                const auto destination = static_cast<std::size_t>(row) * width;
                std::copy_n(fullTemplate.gray.begin() + static_cast<std::ptrdiff_t>(source), width,
                    crop.gray.begin() + static_cast<std::ptrdiff_t>(destination));
                if (!crop.rgba.empty())
                {
                    std::copy_n(fullTemplate.rgba.begin() + static_cast<std::ptrdiff_t>(source * 4),
                        static_cast<std::size_t>(width) * 4,
                        crop.rgba.begin() + static_cast<std::ptrdiff_t>(destination * 4));
                }
            }

            Vision::GrayImage captured;
            captured.width = frame->width;
            captured.height = frame->height;
            captured.gray = frame->grayPixels;
            captured.rgba = frame->rgbaPixels;
            const auto match = Vision::find_color(
                captured, crop,
                std::array<std::int32_t, 4>{
                    referenceRoi.x, referenceRoi.y, referenceRoi.width, referenceRoi.height},
                cropDefinition.threshold, 900, 1600);
            if (!SameFrame(context, frame))
                return std::nullopt;

            TaskEngine::TaskMatchResult result;
            result.found = match.found;
            result.score = match.score;
            result.x = match.x;
            result.y = match.y;
            result.width = match.width;
            result.height = match.height;
            result.frameId = frame->frameId;
            if (result.found)
                return result;
        }
    }
    catch (...)
    {
        return std::nullopt;
    }
    return std::nullopt;
}

std::string JoinText(const std::vector<TaskEngine::TaskOcrDetection>& detections)
{
    std::vector<TaskEngine::TaskOcrDetection> ordered = detections;
    std::stable_sort(ordered.begin(), ordered.end(), [](const auto& left, const auto& right) {
        if (left.y != right.y) return left.y < right.y;
        return left.x < right.x;
    });
    std::string text;
    for (const auto& detection : ordered)
    {
        if (detection.text.empty()) continue;
        if (!text.empty()) text.push_back(' ');
        text += detection.text;
    }
    return text;
}

double DetectionConfidence(const std::vector<TaskEngine::TaskOcrDetection>& detections)
{
    double confidence = 1.0;
    bool found = false;
    for (const auto& detection : detections)
    {
        if (detection.text.empty() || !std::isfinite(detection.confidence)) continue;
        confidence = std::min(confidence, std::clamp(detection.confidence, 0.0, 1.0));
        found = true;
    }
    return found ? confidence : 0.0;
}

std::optional<std::pair<int, double>> ReadCountdownFallback(
    TaskEngine::ITaskExecutionContext& context,
    const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& frame,
    const CareerOcrRegionDefinition& region)
{
    if (!frame || frame->width <= 0 || frame->height <= 0
        || frame->grayPixels.size() < static_cast<std::size_t>(frame->width) * frame->height)
        return std::nullopt;
    const auto image = ToVisionImage(*frame);
    const auto cropRegion = [&](const TaskEngine::TaskRoi& roi) {
        return Resources::crop_image(image,
            static_cast<int>(std::lround(roi.x * frame->width / 900.0)),
            static_cast<int>(std::lround(roi.y * frame->height / 1600.0)),
            static_cast<int>(std::lround(roi.width * frame->width / 900.0)),
            static_cast<int>(std::lround(roi.height * frame->height / 1600.0)));
    };
    const auto readText = [&](const Vision::GrayImage& crop) -> std::optional<std::pair<int, double>> {
        const auto enlarged = Resources::resize_image(crop, (std::max)(1, crop.width * 4),
            (std::max)(1, crop.height * 4));
        const auto detections = context.RecognizeImageText(enlarged, "en-US");
        if (!SameFrame(context, frame) || context.IsCancelled()) return std::nullopt;
        const auto text = JoinText(detections);
        if (const auto parsed = CareerObservationReader::ParseObjectiveTurnsLeft(text))
            return std::pair<int, double>{*parsed, DetectionConfidence(detections)};
        static const std::regex isolatedNumber(R"((^|[^0-9])([0-9]+)(?![0-9]))");
        std::smatch match;
        if (!std::regex_search(text, match, isolatedNumber)) return std::nullopt;
        const auto firstValue = std::stoi(match.str(2));
        const auto suffix = match.suffix().str();
        if (std::regex_search(suffix, isolatedNumber)) return std::nullopt;
        return std::pair<int, double>{firstValue, DetectionConfidence(detections)};
    };
    try
    {
        if (const auto value = readText(cropRegion(region.roi))) return value;
        if (!region.numericRoi || !SameFrame(context, frame) || context.IsCancelled()) return std::nullopt;
        const auto rawCrop = cropRegion(*region.numericRoi);
        UmaColorRule whiteDigits{};
        whiteDigits.minimum_red = whiteDigits.minimum_green = whiteDigits.minimum_blue = 220;
        whiteDigits.maximum_red = whiteDigits.maximum_green = whiteDigits.maximum_blue = 255;
        whiteDigits.minimum_red_green = whiteDigits.minimum_green_blue = -35;
        whiteDigits.maximum_red_green = whiteDigits.maximum_green_blue = 35;
        const auto crop = Vision::binarize_color(rawCrop, whiteDigits);
        if (const auto value = readText(crop)) return value;
        if (!SameFrame(context, frame) || context.IsCancelled()) return std::nullopt;
        // main used its numeric OCR fallback here. Windows text OCR alone
        // cannot reliably read the large white countdown digits on blue.
        const auto readings = context.RecognizeNumericImages({
            {"objective.turns_left", crop, (std::numeric_limits<int>::max)(), false,
                Recognition::NumericOcrFormat::UnsignedInteger},
            {"objective.turns_left_card", cropRegion(region.roi), (std::numeric_limits<int>::max)(), false,
                Recognition::NumericOcrFormat::UnsignedInteger}});
        if (!SameFrame(context, frame) || context.IsCancelled()) return std::nullopt;
        const auto found = readings.find("objective.turns_left");
        const auto card = readings.find("objective.turns_left_card");
        if (found != readings.end() && card != readings.end() && found->second.value && card->second.value
            && found->second.value != card->second.value)
            return std::nullopt;
        if (found != readings.end() && found->second.value && *found->second.value >= 0)
            return std::pair<int, double>{*found->second.value, 0.82};
        if (card != readings.end() && card->second.value && *card->second.value >= 0)
            return std::pair<int, double>{*card->second.value, 0.82};
        context.Log("DEBUG", "Career countdown numeric fallback remained unreadable: digits="
            + (found != readings.end() ? found->second.rawText + " " + found->second.failureReason : std::string("unavailable"))
            + "; card=" + (card != readings.end() ? card->second.rawText + " " + card->second.failureReason : std::string("unavailable")));
        return std::nullopt;
    }
    catch (const std::exception& error)
    {
        context.Log("DEBUG", std::string("Career countdown fallback could not read this frame: ") + error.what());
        return std::nullopt;
    }
}

std::optional<CareerDomain::ObservedValue<bool>> ReadInfirmaryAvailability(
    TaskEngine::ITaskExecutionContext& context,
    const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& frame,
    const CareerVisualResources& resources)
{
    const auto* available = resources.FindAsset("career.infirmary.available");
    const auto* unavailable = resources.FindAsset("career.infirmary.unavailable");
    const auto* region = resources.FindRegion("career.infirmary.button");
    if (!available || !unavailable || !region) return std::nullopt;
    try
    {
        const double threshold = region->at("threshold").get<double>();
        const auto& metadata = region->at("metadata");
        const double scoreGap = metadata.at("scoreGap").get<double>();
        const auto& rois = metadata.at("buttonRois");
        if (!std::isfinite(threshold) || threshold < 0 || threshold > 1
            || !std::isfinite(scoreGap) || scoreGap < 0 || !rois.is_array() || rois.empty()) return std::nullopt;
        for (const auto& bounds : rois)
        {
            if (!bounds.is_array() || bounds.size() != 4
                || !std::all_of(bounds.begin(), bounds.end(), [](const auto& value) { return value.is_number_integer(); }))
                return std::nullopt;
            const TaskEngine::TaskRoi roi{bounds[0].get<int>(), bounds[1].get<int>(), bounds[2].get<int>(), bounds[3].get<int>()};
            if (roi.x < 0 || roi.y < 0 || roi.width <= 0 || roi.height <= 0
                || roi.x > 900 - roi.width || roi.y > 1600 - roi.height) return std::nullopt;
            // main compares colored active/inactive lettering in the two
            // authored positions, including the summer-camp shifted button.
            const auto active = context.MatchTemplateAdvanced(available->path, threshold, roi,
                "MatchTemplateColor", 900, 1600, {});
            if (!SameFrame(context, frame)) return std::nullopt;
            if (!active.found) continue;
            const auto inactive = context.MatchTemplateAdvanced(unavailable->path, 0, roi,
                "MatchTemplateColor", 900, 1600, {});
            if (!SameFrame(context, frame)) return std::nullopt;
            if (active.score >= inactive.score + scoreGap)
                return CareerDomain::ObservedValue<bool>::Make(true, active.score,
                    CareerDomain::ObservationSource::TemplateMatch, "authored colored infirmary button score gap");
        }
        return CareerDomain::ObservedValue<bool>::Make(false, threshold,
            CareerDomain::ObservationSource::TemplateMatch, "no active infirmary button in its authored regions");
    }
    catch (const std::exception& error)
    {
        context.Log("DEBUG", std::string("Infirmary visual policy is unavailable: ") + error.what());
        return std::nullopt;
    }
}

} // namespace

CareerScreenObserver::CareerScreenObserver()
    : CareerScreenObserver(CareerRuntimeConfig{})
{
}

CareerScreenObserver::CareerScreenObserver(std::shared_ptr<CareerVisualResources> resources)
    : m_resources(std::move(resources))
{
    if (!m_resources)
        return;
    try
    {
        auto const* source = m_resources->GetScenarioDocument("events");
        if (!source) return;
        auto const& document = *source;
        if (!document.is_object() || !document.contains("events") || !document["events"].is_array())
            return;
        for (const auto& event : document["events"])
        {
            if (!event.is_object() || !event.contains("eventId") || !event["eventId"].is_string()
                || !event.contains("title") || !event["title"].is_string())
                continue;
            const auto title = NormalizeText(event["title"].get<std::string>());
            const auto id = event["eventId"].get<std::string>();
            if (!title.empty() && !id.empty())
            {
                m_eventIdsByNormalizedTitle.emplace(title, id);
                if (event.contains("ocrTitle") && event["ocrTitle"].is_object())
                {
                    auto const& recognition = event["ocrTitle"];
                    auto const screenId = recognition.value("screenId", std::string{});
                    auto const actionId = recognition.value("actionId", std::string{});
                    if (!screenId.empty() && !actionId.empty())
                        m_eventTitles.push_back({id, title, screenId, actionId});
                }
            }
        }
    }
    catch (...)
    {
        // The event catalog is optional metadata. Missing or malformed data
        // leaves the event transition recognizer on its visual signature.
    }
}

CareerScreenObserver::CareerScreenObserver(const CareerRuntimeConfig& config)
    : m_resources(std::make_shared<CareerVisualResources>(config))
{
}

void CareerScreenObserver::ResetForEntry() noexcept
{
    m_mainAnchorHint.reset();
    m_careerStarted = false;
    m_settlementInProgress = false;
    m_returningHome = false;
    m_goalCompletionProbeArmed = false;
    m_raceReplayFlowCompleted = false;
    m_raceRunnerResultExpected = false;
    m_mainTextCache.Clear();
    m_lastScreenId.clear();
}

void CareerScreenObserver::RememberObservation(const CareerDomain::CareerObservation& observation)
{
    auto const* screen = m_resources ? m_resources->FindScreen(observation.screenId) : nullptr;
    if (!screen) return;
    auto const flow = Fold(screen->flow);
    if (flow == "main" || flow == "turn" || flow == "event" || flow == "race"
        || flow == "settlement" || flow == "skill") m_careerStarted = true;
    if (flow == "settlement") m_settlementInProgress = true;
    if (m_careerStarted && IsReturningHomeScreen(screen->screenId)) m_returningHome = true;
    if (Fold(screen->screenId) != "career_main" || Fold(m_lastScreenId) != "career_main")
        m_mainTextCache.Clear();
    m_lastScreenId = screen->screenId;
}

std::optional<int> CareerObservationReader::ParseObjectiveTurnsLeft(const std::string& text)
{
    if (text.empty())
        return std::nullopt;
    static const std::regex withUnit(
        R"((^|[^0-9])([0-9]+)\s*(turn|turns)(\s*\(s\))?\b)", std::regex::icase);
    static const std::regex standalone(R"(^\s*([0-9]+)\s*$)");
    std::smatch match;
    bool unitMatch = std::regex_search(text, match, withUnit);
    if (!unitMatch)
    {
        if (!std::regex_match(text, match, standalone))
            return std::nullopt;
    }
    try
    {
        const auto value = std::stoi(match.str(unitMatch ? 2 : 1));
        return value >= 0 ? std::optional<int>(value) : std::nullopt;
    }
    catch (...) { return std::nullopt; }
}

std::optional<int> CareerObservationReader::ParseFansToGoal(const std::string& text)
{
    if (text.empty())
        return std::nullopt;
    static const std::regex fansToGo(
        R"((^|[^0-9])([0-9][0-9,]{0,6})\s*fan(s)?(\s*\(s\))?\s*to\s*go\b)",
        std::regex::icase);
    std::smatch match;
    if (!std::regex_search(text, match, fansToGo))
        return std::nullopt;
    auto digits = match.str(2);
    digits.erase(std::remove(digits.begin(), digits.end(), ','), digits.end());
    try
    {
        return std::clamp(std::stoi(digits), 0, 9999999);
    }
    catch (...) { return std::nullopt; }
}

CareerDomain::CareerObservation CareerScreenObserver::Observe(
    TaskEngine::ITaskExecutionContext& context)
{
    return ObserveScreens(context, {});
}

CareerDomain::CareerObservation CareerScreenObserver::ObserveCareer(TaskEngine::ITaskExecutionContext& context)
{
    std::vector<std::string> screens;
    if (m_resources)
        for (auto const& screen : m_resources->GetScreens())
        {
            auto const flow = Fold(screen.flow);
            if ((flow != "entry" && flow != "normal" && flow != "independent" && !IsHomeScreen(screen.screenId))
                || (m_returningHome && IsHomeScreen(screen.screenId)))
                screens.push_back(screen.screenId);
        }
    screens.push_back("career_skills");
    return ObserveScreens(context, screens, !m_careerStarted);
}

CareerDomain::CareerObservation CareerScreenObserver::ObserveScreens(
    TaskEngine::ITaskExecutionContext& context, const std::vector<std::string>& screenIds, bool allowResume)
{
    return ObserveScreensCore(context, screenIds, allowResume, {}, false);
}

CareerDomain::CareerObservation CareerScreenObserver::ObserveTransition(
    TaskEngine::ITaskExecutionContext& context, const CareerTransitionContext& transition,
    std::shared_ptr<const TaskEngine::TaskFrameSnapshot> seed)
{
    if (transition.candidates.empty()) return {};
    return ObserveScreensCore(context, transition.candidates, false, std::move(seed), true);
}

CareerDomain::CareerObservation CareerScreenObserver::ObserveScreensCore(
    TaskEngine::ITaskExecutionContext& context, const std::vector<std::string>& screenIds, bool allowResume,
    std::shared_ptr<const TaskEngine::TaskFrameSnapshot> seed, bool minimumDetails)
{
    CareerDomain::CareerObservation observation;
    if (!m_resources || !m_resources->IsReady())
    {
        observation.screenId = "resource_error";
        if (!m_diagnosticsLogged)
        {
            context.Log("ERROR", "Career visual package is unavailable.");
            if (m_resources)
            {
                for (const auto& diagnostic : m_resources->GetLoadDiagnostics())
                    context.Log("ERROR", diagnostic);
            }
            m_diagnosticsLogged = true;
        }
        return observation;
    }

    struct Sample
    {
        std::shared_ptr<const TaskEngine::TaskFrameSnapshot> frame;
        const CareerVisualScreenDefinition* screen{nullptr};
        double score{0.0};
        bool frameChanged{false};
        std::optional<std::string> eventId;
        std::optional<std::string> eventTitle;
        std::optional<std::string> eventActionId;
        std::chrono::steady_clock::time_point capturedAt;
    };
    CareerVisualScreenDefinition skillRecoveryScreen;
    skillRecoveryScreen.screenId = "career_skills";
    skillRecoveryScreen.flow = "skill";
    skillRecoveryScreen.stable = true;

    const auto isEligible = [&](const CareerVisualScreenDefinition& screen) {
        const auto id = Fold(screen.screenId);
        if (!screenIds.empty() && std::none_of(screenIds.begin(), screenIds.end(),
            [&](auto const& allowed) { return Fold(allowed) == id; }))
            return false;
        if (m_returningHome)
            return IsEligibleAfterSettlement(screen, true);
        if (m_settlementInProgress && !IsEligibleAfterSettlement(screen, false))
            return false;
        if (m_careerStarted && IsHomeScreen(screen.screenId))
            return false;
        if ((id == "follow_trainer_limit" || id == "rewards_collected"
                || id == "career_epithet" || id == "career_rating_record_updated")
            && !m_settlementInProgress && !allowResume)
            return false;
        if (id == "career_epithet"
            && !allowResume
            && Fold(m_lastScreenId) != "career_result_close"
            && Fold(m_lastScreenId) != "career_rating_record_updated"
            && Fold(m_lastScreenId) != "career_epithet")
            return false;
        if (id == "career_rating_record_updated"
            && !allowResume
            && Fold(m_lastScreenId) != "career_result_close"
            && Fold(m_lastScreenId) != "career_rating_record_updated")
            return false;
        if (m_careerStarted && !allowResume)
        {
            const bool resumedRaceCompleted = m_raceReplayFlowCompleted && Fold(m_lastScreenId) == "race_runner";
            if (id == "goal_objective_complete")
            {
                if (!m_goalCompletionProbeArmed && !resumedRaceCompleted)
                    return false;
            }
            else if (id == "goal_update")
            {
                if (Fold(m_lastScreenId) != "goal_objective_complete")
                    return false;
            }
            else if (id == "goal_complete")
            {
                if (!m_goalCompletionProbeArmed && !resumedRaceCompleted
                    && Fold(m_lastScreenId) != "goal_objective_complete"
                    && Fold(m_lastScreenId) != "goal_update")
                    return false;
            }
        }
        return true;
    };

    const CareerVisualScreenDefinition* firstWinner = nullptr;
    const auto captureAndRecognize = [&]() -> Sample {
        Sample sample;
        const bool reuseSeed = seed && SameFrame(context, seed);
        if (!reuseSeed && !context.CaptureScreen())
        {
            sample.frameChanged = true;
            return sample;
        }
        sample.frame = reuseSeed ? std::move(seed) : context.GetCurrentFrame();
        seed.reset();
        sample.capturedAt = std::chrono::steady_clock::now();
        if (!sample.frame || sample.frame->width <= 0 || sample.frame->height <= 0)
        {
            sample.frameChanged = true;
            context.Log("ERROR", "Career observation capture did not expose a stable frame snapshot.");
            return sample;
        }

        CoordinateScope coordinates(context);
        std::optional<CareerClawMachineSettings> clawSettings;
        try
        {
            clawSettings = CareerClawMachineSettings::FromJson(m_resources->GetProfileOptions());
        }
        catch (const std::exception& error)
        {
            context.Log("ERROR", std::string("Claw profile settings are invalid: ") + error.what());
        }
        // Skill pages and purchase dialogs are catalog-backed overlays rather
        // than static career profile screens. Recognize them for resume recovery.
        for (const auto* marker : {"career.skill.learned.title", "career.skill.confirmation.title",
                 "career.skill.exit.title"})
        {
            if (!isEligible(skillRecoveryScreen)) break;
            if (const auto match = MatchCatalogAsset(
                    context, sample.frame, *m_resources, marker, marker);
                match && match->found)
            {
                // "Confirmation" is shared with Final Confirmation. It only
                // identifies a skill purchase together with its Learn button.
                if (std::string_view(marker) == "career.skill.confirmation.title")
                {
                    const auto learn = MatchCatalogAsset(context, sample.frame, *m_resources,
                        "career.skill.confirmation.learn", "career.skill.confirmation.learn");
                    if (!SameFrame(context, sample.frame))
                    {
                        sample.frameChanged = true;
                        return sample;
                    }
                    if (!learn || !learn->found) continue;
                    const auto* learnRegion = m_resources->FindRegion("career.skill.confirmation.learn");
                    if (!learnRegion || !learnRegion->contains("roi")) continue;
                    const auto bounds = (*learnRegion)["roi"].get<std::vector<int>>();
                    if (bounds.size() != 4) continue;
                    const auto text = context.RecognizeText("en-US", ToRoi(bounds));
                    if (!SameFrame(context, sample.frame))
                    {
                        sample.frameChanged = true;
                        return sample;
                    }
                    if (std::none_of(text.begin(), text.end(), [](auto const& detection) {
                        return NormalizeText(detection.text) == "learn";
                    })) continue;
                }
                sample.screen = &skillRecoveryScreen;
                sample.score = match->score;
                return sample;
            }
            if (!SameFrame(context, sample.frame))
            {
                sample.frameChanged = true;
                return sample;
            }
        }
        std::vector<const CareerVisualScreenDefinition*> orderedScreens;
        orderedScreens.reserve(m_resources->GetScreens().size());
        for (const auto& screen : m_resources->GetScreens())
            orderedScreens.push_back(&screen);
        std::stable_sort(orderedScreens.begin(), orderedScreens.end(), [](const auto* left, const auto* right) {
            return left->priority < right->priority;
        });
        for (const auto* screenPointer : orderedScreens)
        {
            const auto& screen = *screenPointer;
            if (!isEligible(screen) || screen.templates.empty())
                continue;
            if (firstWinner && screen.priority > firstWinner->priority)
                continue;
            sample.eventId.reset();
            sample.eventTitle.reset();
            sample.eventActionId.reset();
            double screenScore = 0.0;
            bool matched = false;

            if (Fold(screen.screenId) == "goal_complete")
            {
                if (const auto match = MatchGoalCompleteHeader(context, sample.frame, screen))
                {
                    matched = match->found;
                    screenScore = match->score;
                }
                if (!SameFrame(context, sample.frame))
                {
                    sample.frameChanged = true;
                    return sample;
                }
            }
            else if (Fold(screen.screenId).find("claw_machine") != std::string::npos && clawSettings)
            {
                if (const auto clawMatch = MatchClawMarker(context, sample.frame, screen, *clawSettings))
                {
                    matched = clawMatch->found;
                    screenScore = clawMatch->score;
                }
                if (!SameFrame(context, sample.frame))
                {
                    sample.frameChanged = true;
                    return sample;
                }
            }
            else
            {
                for (const auto& templ : screen.templates)
                {
                    const auto algorithm = templ.requiresAlphaMatching
                        ? std::string("MatchTemplateColor") : templ.algorithm;
                    const auto match = context.MatchTemplateAdvanced(
                        templ.path, templ.threshold, templ.roi, algorithm, 900, 1600, {});
                    if (!SameFrame(context, sample.frame)
                        || (match.frameId != 0 && sample.frame->frameId != 0
                            && match.frameId != sample.frame->frameId))
                    {
                        sample.frameChanged = true;
                        return sample;
                    }
                    bool markerVerified = match.found;
                    if (markerVerified && Fold(screen.screenId) == "event_choice")
                    {
                        // The Effects crop has a large white backing. A high
                        // correlation alone can match unrelated white panels.
                        const auto marker = context.LoadTemplateImage(templ.path);
                        markerVerified = marker && HasMatchingGlyphColors(
                            *sample.frame, *marker, match, templ.threshold);
                    }
                    if (markerVerified && (!matched || match.score > screenScore))
                    {
                        matched = true;
                        screenScore = match.score;
                    }
                    if (matched && screenScore >= 0.985)
                        break;
                }
            }
            if (!matched && Fold(screen.flow) == "event")
            {
                const auto titleRegion = std::find_if(screen.ocrRegions.begin(), screen.ocrRegions.end(),
                    [](auto const& region) { return Fold(region.semanticId) == "event.title"; });
                const bool hasCatalogTitles = std::any_of(m_eventTitles.begin(), m_eventTitles.end(),
                    [&](auto const& definition) { return Fold(definition.screenId) == Fold(screen.screenId); });
                const auto title = titleRegion != screen.ocrRegions.end() && hasCatalogTitles
                    ? JoinText(context.RecognizeText("en-US", titleRegion->roi)) : std::string{};
                if (!SameFrame(context, sample.frame)) { sample.frameChanged = true; return sample; }
                const auto normalizedTitle = NormalizeText(title);
                for (auto const& definition : m_eventTitles)
                {
                    if (Fold(definition.screenId) != Fold(screen.screenId)) continue;
                    if (normalizedTitle.empty() || normalizedTitle != definition.title) continue;
                    auto const* action = m_resources->FindAction(screen.screenId, definition.actionId);
                    auto const* task = action ? m_resources->FindExecutionTask(action->taskName) : nullptr;
                    if (!task || !task->templatePath || !task->roi || task->roi->size() < 4) continue;
                    // Match the authored colored option glyph, excluding a
                    // grayscale-only correlation with dialogue clothing.
                    auto const match = context.MatchTemplateAdvanced(*task->templatePath, task->templateThreshold,
                        ToRoi(*task->roi), "MatchTemplateColor", 900, 1600, {});
                    if (!SameFrame(context, sample.frame)) { sample.frameChanged = true; return sample; }
                    if (!match.found) continue;
                    auto const marker = context.LoadTemplateImage(*task->templatePath);
                    if (!marker || !HasMatchingGlyphColors(*sample.frame, *marker, match, task->templateThreshold)) continue;
                    matched = true;
                    screenScore = match.score;
                    sample.eventId = definition.eventId;
                    sample.eventTitle = title;
                    sample.eventActionId = definition.actionId;
                    break;
                }
            }
            // View Results can bypass the Replay label. Match main's fallback
            // only for a pending race whose configured runner was just seen;
            // the shared Next button alone cannot identify arbitrary screens.
            if (!matched && Fold(screen.screenId) == "race_runner_result"
                && m_raceRunnerResultExpected && Fold(m_lastScreenId) == "race_runner"
                && !screen.requiredTemplates.empty())
            {
                const auto& marker = screen.requiredTemplates.front();
                const auto next = context.MatchTemplateAdvanced(marker.path, marker.threshold,
                    marker.roi, marker.requiresAlphaMatching ? "MatchTemplateColor" : marker.algorithm,
                    900, 1600, {});
                if (!SameFrame(context, sample.frame)
                    || (next.frameId != 0 && sample.frame->frameId != 0 && next.frameId != sample.frame->frameId))
                {
                    sample.frameChanged = true;
                    return sample;
                }
                matched = next.found;
                screenScore = next.score;
            }
            if (!matched)
                continue;

            // Scenario/trainee/legacy selection retain the selected Home tab.
            // The tab alone is not proof that the Career entry button is still
            // available. Probe its authored primary/alternative templates on
            // this frame before routing back into the Home entry action.
            if (Fold(screen.screenId) == "home")
            {
                const auto* entry = m_resources->FindAction("home", "home.career");
                const auto entryMatch = entry
                    ? TaskEngine::RecognitionPipeline::EvaluateSameFrameMonitors(
                        m_resources->GetExecutionDefinition(), {entry->taskName}, context)
                    : std::nullopt;
                if (!SameFrame(context, sample.frame))
                {
                    sample.frameChanged = true;
                    return sample;
                }
                if (!entryMatch)
                    continue;
            }

            bool requiredTemplatesMatch = true;
            if (Fold(screen.screenId) != "goal_complete")
            {
                for (const auto& templ : screen.requiredTemplates)
                {
                    const auto algorithm = templ.requiresAlphaMatching
                        ? std::string("MatchTemplateColor") : templ.algorithm;
                    TaskEngine::TaskMatchResult required;
                    // The main Training button stays in place across turns.
                    // This is a search hint, not cached visual proof: rematch
                    // each frame and fall back to the full authored ROI on miss.
                    if (Fold(screen.screenId) == "career_main" && templ.roi && m_mainAnchorHint
                        && m_mainAnchorHint->path == templ.path
                        && m_mainAnchorHint->width == sample.frame->width && m_mainAnchorHint->height == sample.frame->height
                        && m_mainAnchorHint->session == sample.frame->sessionGeneration
                        && m_mainAnchorHint->geometry == sample.frame->geometryGeneration
                        && m_mainAnchorHint->match.referenceWidth == 900 && m_mainAnchorHint->match.referenceHeight == 1600)
                    {
                        const auto& old = m_mainAnchorHint->match;
                        const int left = (std::max)(templ.roi->x, old.x - 8);
                        const int top = (std::max)(templ.roi->y, old.y - 8);
                        const int right = (std::min)(templ.roi->x + templ.roi->width, old.x + old.width + 8);
                        const int bottom = (std::min)(templ.roi->y + templ.roi->height, old.y + old.height + 8);
                        if (old.width > 0 && old.height > 0 && right > left && bottom > top)
                            required = context.MatchTemplateAdvanced(templ.path, templ.threshold,
                                TaskEngine::TaskRoi{left, top, right - left, bottom - top}, algorithm, 900, 1600, {});
                    }
                    if (!required.found && SameFrame(context, sample.frame))
                        required = context.MatchTemplateAdvanced(
                            templ.path, templ.threshold, templ.roi, algorithm, 900, 1600, {});
                    if (!SameFrame(context, sample.frame)
                        || (required.frameId != 0 && sample.frame->frameId != 0
                            && required.frameId != sample.frame->frameId))
                    {
                        sample.frameChanged = true;
                        return sample;
                    }
                    if (!required.found)
                    {
                        requiredTemplatesMatch = false;
                        break;
                    }
                    if (Fold(screen.screenId) == "career_main")
                        m_mainAnchorHint = MainAnchorHint{templ.path, required, sample.frame->width, sample.frame->height,
                            sample.frame->sessionGeneration, sample.frame->geometryGeneration};
                }
            }
            if (!requiredTemplatesMatch)
                continue;

            // career_main remains eligible only when its header and action
            // button are visible together; this rejects race and overlay frames
            // which retain the shared Career title.
            if (Fold(screen.screenId) == "career_main" && screen.requiredTemplates.empty())
                continue;

            if (screen.verifyRequiredText && !screen.requiredText.empty()
                && Fold(screen.screenId) != "goal_complete" && !sample.eventId)
            {
                const auto detections = context.RecognizeText("en-US");
                if (!SameFrame(context, sample.frame))
                {
                    sample.frameChanged = true;
                    return sample;
                }
                if (Fold(screen.screenId) != "career_main"
                    && !std::all_of(screen.requiredText.begin(), screen.requiredText.end(),
                        [&](const auto& text) { return ContainsExpectedText(detections, text); }))
                    continue;
            }

            sample.screen = &screen;
            sample.score = screenScore;
            return sample;
        }
        if (!SameFrame(context, sample.frame))
            sample.frameChanged = true;
        return sample;
    };

    auto first = captureAndRecognize();
    if (first.frameChanged)
    {
        observation.screenId = "capture_failed";
        return observation;
    }
    firstWinner = first.screen;
    context.Sleep(120);
    if (context.IsCancelled())
    {
        observation.screenId = "capture_cancelled";
        return observation;
    }
    auto second = captureAndRecognize();
    if (second.frameChanged)
    {
        observation.screenId = "capture_failed";
        return observation;
    }
    if (!second.screen)
        return observation;
    const auto isTurnConfirmation = [](const std::string& screenId) {
        const auto id = Fold(screenId);
        return id == "recreation_selection" || id == "recreation_confirmation" || id == "infirmary_confirmation"
            || id == "rest_confirmation" || id == "summer_rest_confirmation";
    };
    if (isTurnConfirmation(second.screen->screenId)
        && (second.screen->stable || second.screen->flow == "event")
        && (!first.screen || Fold(first.screen->screenId) != Fold(second.screen->screenId)
            || first.eventId != second.eventId))
    {
        // Keep the newly recognized sample. One more independent capture can
        // confirm B/C without discarding B and recapturing a complete C/D pair.
        first = std::move(second);
        firstWinner = first.screen;
        context.Sleep(120);
        if (context.IsCancelled())
        {
            observation.screenId = "capture_cancelled";
            return observation;
        }
        second = captureAndRecognize();
        if (second.frameChanged)
        {
            observation.screenId = "capture_failed";
            return observation;
        }
        if (!second.screen || !isTurnConfirmation(second.screen->screenId)) return observation;
    }
    if ((second.screen->stable || second.screen->flow == "event")
        && (!first.screen || Fold(first.screen->screenId) != Fold(second.screen->screenId)
            || first.eventId != second.eventId))
    {
        context.Log("WARNING", "Career screen did not remain stable across the two-frame confirmation window: "
            + (first.screen ? first.screen->screenId : "unknown") + " -> " + second.screen->screenId + ".");
        return observation;
    }

    if (isTurnConfirmation(second.screen->screenId)
        && (!first.frame || !second.frame || first.frame == second.frame
            || (first.frame->frameId != 0 && first.frame->frameId == second.frame->frameId)
            || first.frame->width != second.frame->width || first.frame->height != second.frame->height
            || first.frame->orientation != second.frame->orientation
            || first.frame->sessionGeneration != second.frame->sessionGeneration
            || first.frame->geometryGeneration != second.frame->geometryGeneration))
    {
        context.Log("WARNING", "Turn confirmation requires independent frames with unchanged session and geometry.");
        return observation;
    }

    const auto frame = second.frame;
    const auto* bestScreen = second.screen;
    const auto bestScore = second.score;
    if (!SameFrame(context, frame))
    {
        observation.screenId = "unknown";
        context.Log("WARNING", "Discarded career observation because its visual evidence spans frames.");
        return observation;
    }

    observation.screenId = bestScreen->screenId;
    observation.score = bestScore;
    observation.screenKind = ClassifyScreen(*bestScreen);
    observation.stableFrameCount = first.screen
        && Fold(first.screen->screenId) == Fold(bestScreen->screenId) ? 2 : 1;
    observation.consecutiveFramesIdentical = first.frame
        && first.frame->width == frame->width && first.frame->height == frame->height
        && first.frame->grayPixels.size() == static_cast<std::size_t>(frame->width) * frame->height
        && first.frame->grayPixels == frame->grayPixels
        && first.frame->rgbaPixels == frame->rgbaPixels;
    if (bestScreen->stateSignatureRoi && first.frame && first.screen
        && Fold(first.screen->screenId) == Fold(bestScreen->screenId)
        && first.frame->width == frame->width && first.frame->height == frame->height
        && first.frame->sessionGeneration == frame->sessionGeneration
        && first.frame->geometryGeneration == frame->geometryGeneration)
    {
        const auto firstSignature = PageSignature(*first.frame, *bestScreen->stateSignatureRoi);
        const auto secondSignature = PageSignature(*frame, *bestScreen->stateSignatureRoi);
        if (firstSignature && firstSignature == secondSignature)
            observation.pageStateEvidence = CareerDomain::CareerPageStateEvidence{
                *secondSignature, frame->width, frame->height, frame->sessionGeneration, frame->geometryGeneration};
    }
    const auto id = Fold(bestScreen->screenId);
    const auto flow = Fold(bestScreen->flow);
    observation.eventId = second.eventId;
    observation.eventTitle = second.eventTitle;
    observation.eventActionId = second.eventActionId;
    if (id == "goal_objective_complete" || id == "goal_complete")
    {
        observation.objectiveCompleted = CareerDomain::ObservedValue<bool>::Make(
            true, bestScore, CareerDomain::ObservationSource::TemplateMatch,
            "recognized explicit career goal completion screen");
    }

    for (const auto& region : bestScreen->ocrRegions)
    {
        const auto& roi = region.roi;
        if (roi.width <= 0 || roi.height <= 0) continue;
        const auto semantic = Fold(region.semanticId);
        if (minimumDetails && id != "career_main" && semantic != "event.title" && semantic != "race.day_status")
            continue;
        std::string text;
        double confidence = 0.0;
        const bool cacheEligible = id == "career_main"
            && (semantic == "scenario.phase" || semantic == "objective.turns_left"
                || semantic == "objective.title" || semantic == "mood.current");
        if (!cacheEligible || !m_mainTextCache.TryGet(semantic, *frame, roi, text, &confidence))
        {
            auto detections = context.RecognizeText("en-US", roi);
            if (!SameFrame(context, frame))
            {
                context.Log("WARNING", "Discarded career OCR evidence because the frame changed during recognition.");
                return CareerDomain::CareerObservation{};
            }
            text = JoinText(detections);
            confidence = DetectionConfidence(detections);
            if (cacheEligible && confidence > 0.0 && !text.empty())
            {
                m_mainTextCache.Remember(semantic, *frame, roi, text, confidence);
            }
        }
        if (semantic == "scenario.phase")
        {
            if (confidence > 0.0)
            {
                if (auto parsed = CareerObservationReader::ParseTurnPosition(text))
                {
                    observation.turnPositionText = parsed->label;
                    observation.turnIndexEvidence = CareerDomain::ObservedValue<int>::Make(
                        parsed->turnIndex, confidence, CareerDomain::ObservationSource::Ocr, text);
                }
            }
        }
        else if (semantic == "objective.turns_left")
        {
            if (confidence > 0.0)
            {
                if (auto value = CareerObservationReader::ParseObjectiveTurnsLeft(text))
                {
                    observation.turnsToGoal = *value;
                    observation.turnsToGoalEvidence = CareerDomain::ObservedValue<int>::Make(
                        *value, confidence, CareerDomain::ObservationSource::Ocr, text);
                }
            }
        }
        else if (semantic == "race.day_status" && id == "race_list")
        {
            // Quick mode may arrive directly at the mandatory Race List.
            // Its Race Day card proves the countdown has reached zero; a
            // generic optional Race List must not supply this evidence.
            if (confidence > 0.0 && NormalizeText(text) == "raceday")
            {
                observation.turnsToGoal = 0;
                observation.turnsToGoalEvidence = CareerDomain::ObservedValue<int>::Make(
                    0, confidence, CareerDomain::ObservationSource::Ocr, text);
            }
        }
        else if (semantic == "objective.title" || semantic == "race.objective")
        {
            if (!text.empty() && confidence > 0.0)
            {
                observation.goalText = text;
                observation.goalTextEvidence = CareerDomain::ObservedValue<std::string>::Make(
                    text, confidence, CareerDomain::ObservationSource::Ocr);
                if (const auto fans = CareerObservationReader::ParseFansToGoal(text))
                {
                    observation.fansToGoal = *fans;
                    observation.fansToGoalEvidence = CareerDomain::ObservedValue<int>::Make(
                        *fans, confidence, CareerDomain::ObservationSource::Ocr, text);
                }
            }
        }
        else if (semantic == "event.title")
        {
            if (!text.empty() && confidence > 0.0)
            {
                observation.eventTitle = text;
                const auto found = m_eventIdsByNormalizedTitle.find(NormalizeText(text));
                if (found != m_eventIdsByNormalizedTitle.end())
                    observation.eventId = found->second;
            }
        }
        else if (semantic == "mood.current")
        {
            if (confidence > 0.0)
            {
                if (auto mood = CareerObservationReader::ParseMood(text))
                {
                    observation.moodText = *mood;
                    observation.moodEvidence = CareerDomain::ObservedValue<std::string>::Make(
                        *mood, confidence, CareerDomain::ObservationSource::Ocr, text);
                }
            }
        }
    }
    if (id != "career_main")
        m_mainTextCache.Clear();

    if (observation.screenKind == CareerDomain::CareerScreenKind::MainTurn
        && !observation.turnsToGoalEvidence.IsReliable())
    {
        const auto region = std::find_if(bestScreen->ocrRegions.begin(), bestScreen->ocrRegions.end(),
            [](const auto& item) { return Fold(item.semanticId) == "objective.turns_left"; });
        if (region != bestScreen->ocrRegions.end())
        {
            std::optional<std::pair<int, double>> fallback;
            std::string cached;
            double cachedConfidence = 0.0;
            if (m_mainTextCache.TryGet("objective.turns_left.fallback", *frame, region->roi, cached, &cachedConfidence))
            {
                if (const auto value = CareerObservationReader::ParseObjectiveTurnsLeft(cached))
                    fallback = std::pair<int, double>{*value, cachedConfidence};
            }
            else
                fallback = ReadCountdownFallback(context, frame, *region);
            if (fallback && fallback->second > 0.0)
            {
                observation.turnsToGoal = fallback->first;
                observation.turnsToGoalEvidence = CareerDomain::ObservedValue<int>::Make(
                    fallback->first, fallback->second, CareerDomain::ObservationSource::Ocr,
                    "processed crop of the dedicated objective countdown region");
                m_mainTextCache.Remember("objective.turns_left.fallback", *frame, region->roi,
                    std::to_string(fallback->first), fallback->second);
            }
        }
    }

    if (flow == "event")
    {
        const auto eventText = context.RecognizeText("en-US");
        if (!SameFrame(context, frame))
        {
            context.Log("WARNING", "Discarded event-state OCR because the frame changed during recognition.");
            return CareerDomain::CareerObservation{};
        }
        const auto signature = EventSignature(eventText);
        if (signature != 0)
            observation.eventStateSignature = signature;
        if (!observation.eventTitle)
        {
            const auto combined = JoinText(eventText);
            if (!combined.empty())
            {
                observation.eventTitle = combined;
                const auto found = m_eventIdsByNormalizedTitle.find(NormalizeText(combined));
                if (found != m_eventIdsByNormalizedTitle.end())
                    observation.eventId = found->second;
            }
        }
    }

    if (bestScreen->energyBar && frame->rgbaPixels.size() > 0)
    {
        const auto& bar = *bestScreen->energyBar;
        const auto measure = CareerObservationReader::MeasureEnergyBar(
            frame->rgbaPixels.data(), frame->width, frame->height,
            bar.roi, bar.innerInset, 900, 1600,
            bar.fillSaturationMin, bar.fillValueMin, bar.columnMatchRatio,
            frame->rgbaPixels.size());
        if (measure && measure->confidence >= bar.minimumConfidence)
        {
            observation.energyPercent = CareerDomain::ObservedValue<int>::Make(
                measure->percent, measure->confidence, CareerDomain::ObservationSource::ColorProbe,
                "profile-defined career energy bar");
        }
    }

    if (observation.screenKind == CareerDomain::CareerScreenKind::MainTurn)
    {
        const auto available = ReadInfirmaryAvailability(context, frame, *m_resources);
        if (!SameFrame(context, frame))
            return CareerDomain::CareerObservation{};
        if (available)
        {
            observation.infirmaryAvailable = available->Value();
            observation.infirmaryEvidence = *available;
        }
    }

    if (!SameFrame(context, frame))
    {
        context.Log("WARNING", "Discarded career observation because its frame changed before completion.");
        return CareerDomain::CareerObservation{};
    }
    const bool independentSourceFrames = first.frame && first.frame != frame
        && first.frame->frameId != frame->frameId && first.frame->width == frame->width
        && first.frame->height == frame->height && first.frame->orientation == frame->orientation
        && first.frame->sessionGeneration == frame->sessionGeneration
        && first.frame->geometryGeneration == frame->geometryGeneration;
    if ((isTurnConfirmation(observation.screenId) || id == "career_main")
        && observation.stableFrameCount >= 2 && frame->frameId != 0 && independentSourceFrames)
    {
        observation.visualFrameEvidence = CareerDomain::CareerObservation::VisualFrameEvidence{
            frame->frameId, frame->sessionGeneration, frame->geometryGeneration,
            frame->width, frame->height, frame->orientation, second.capturedAt};
    }
    RememberObservation(observation);
    return observation;
}

} // namespace UmaAssistant::Career
