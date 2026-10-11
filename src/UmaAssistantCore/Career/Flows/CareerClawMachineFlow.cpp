#include "CareerClawMachineFlow.hpp"
#include "UmaAssistantCore/Resources/ImageTransforms.hpp"
#include "UmaAssistantCore/TaskEngine/TaskContextInterruption.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <regex>
#include <stdexcept>

namespace UmaAssistant::Career
{
namespace
{
using Json = nlohmann::json;
using namespace TaskEngine;

UmaColorRule Color(int r0, int g0, int b0, int r1, int g1, int b1,
    int rg0 = -255, int rg1 = 255, int gb0 = -255, int gb1 = 255)
{
    UmaColorRule rule{};
    rule.minimum_red = r0; rule.minimum_green = g0; rule.minimum_blue = b0;
    rule.maximum_red = r1; rule.maximum_green = g1; rule.maximum_blue = b1;
    rule.minimum_red_green = rg0; rule.maximum_red_green = rg1;
    rule.minimum_green_blue = gb0; rule.maximum_green_blue = gb1;
    rule.sample_step = 1;
    return rule;
}

UmaColorRule ReadColor(const Json& json, UmaColorRule rule)
{
    if (!json.is_object()) throw std::invalid_argument("Claw color rule must be an object.");
    const auto values = [&](const char* name, int size) -> std::vector<int> {
        if (!json.contains(name)) return {};
        auto result = json.at(name).get<std::vector<int>>();
        if (result.size() != static_cast<size_t>(size))
            throw std::invalid_argument(std::string("Invalid claw color bounds: ") + name);
        return result;
    };
    if (auto v = values("minimumRgb", 3); !v.empty())
    { rule.minimum_red = v[0]; rule.minimum_green = v[1]; rule.minimum_blue = v[2]; }
    if (auto v = values("maximumRgb", 3); !v.empty())
    { rule.maximum_red = v[0]; rule.maximum_green = v[1]; rule.maximum_blue = v[2]; }
    if (json.contains("redMinusGreen") && !json.at("redMinusGreen").is_null())
    { auto v = values("redMinusGreen", 2); rule.minimum_red_green = v[0]; rule.maximum_red_green = v[1]; }
    if (json.contains("greenMinusBlue") && !json.at("greenMinusBlue").is_null())
    { auto v = values("greenMinusBlue", 2); rule.minimum_green_blue = v[0]; rule.maximum_green_blue = v[1]; }
    return rule;
}

ClawBlobRule ReadBlob(const Json& json, ClawBlobRule rule)
{
    rule.minimumAreaRatio = json.value("minimumAreaRatio", rule.minimumAreaRatio);
    rule.maximumAreaRatio = json.value("maximumAreaRatio", rule.maximumAreaRatio);
    rule.minimumWidthRatio = json.value("minimumWidthRatio", rule.minimumWidthRatio);
    rule.maximumWidthRatio = json.value("maximumWidthRatio", rule.maximumWidthRatio);
    rule.minimumHeightRatio = json.value("minimumHeightRatio", rule.minimumHeightRatio);
    rule.maximumHeightRatio = json.value("maximumHeightRatio", rule.maximumHeightRatio);
    rule.minimumAspectRatio = json.value("minimumAspectRatio", rule.minimumAspectRatio);
    rule.maximumAspectRatio = json.value("maximumAspectRatio", rule.maximumAspectRatio);
    return rule;
}

bool Fits(const Vision::GrayImage& frame, const UmaColorBlob& blob, const ClawBlobRule& rule)
{
    const double area = blob.area / (static_cast<double>(frame.width) * frame.height);
    const double width = static_cast<double>(blob.width) / frame.width;
    const double height = static_cast<double>(blob.height) / frame.height;
    const double aspect = static_cast<double>(blob.width) / blob.height;
    return area >= rule.minimumAreaRatio && area <= rule.maximumAreaRatio
        && width >= rule.minimumWidthRatio && width <= rule.maximumWidthRatio
        && height >= rule.minimumHeightRatio && height <= rule.maximumHeightRatio
        && aspect >= rule.minimumAspectRatio && aspect <= rule.maximumAspectRatio;
}

Vision::GrayImage Image(const TaskFrameSnapshot& frame)
{ return {frame.width, frame.height, frame.grayPixels, frame.rgbaPixels}; }

TaskMatchResult SourceMatch(const Vision::MatchResult& match, const TaskFrameSnapshot& frame)
{
    return {match.found, match.score, match.x, match.y, match.width, match.height,
        frame.width, frame.height, frame.frameId, frame.sessionGeneration, frame.geometryGeneration};
}

bool Equal(UmaImageRect a, UmaImageRect b)
{ return a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height; }

class PollWindow
{
public:
    PollWindow(int milliseconds, int interval)
        : remaining((milliseconds + interval - 1) / interval),
          deadline(std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds)) {}
    bool Next() { return remaining-- > 0 && std::chrono::steady_clock::now() < deadline; }
private:
    int remaining;
    std::chrono::steady_clock::time_point deadline;
};

CareerClawFlowResult Failed(ITaskExecutionContext& context, std::string reason)
{ return {context.IsCancelled() ? ActionExecutionStatus::Cancelled : ActionExecutionStatus::Failed, std::move(reason)}; }

struct Ready
{
    int credit;
    UmaImageRect control;
    std::shared_ptr<const TaskFrameSnapshot> source;
};
struct NextClaw { bool isResult{}; std::optional<Ready> ready; };

std::optional<NextClaw> WaitForNext(ITaskExecutionContext& context,
    const Vision::GrayImage& game, double gameThreshold,
    const Vision::GrayImage& result, double resultThreshold,
    const Vision::GrayImage& creditLabel, const CareerClawMachineSettings& settings,
    std::optional<int> previousCredit, int timeout)
{
    std::optional<Ready> previous;
    int stable = 0, resultStable = 0;
    PollWindow window(timeout, settings.pollIntervalMs);
    while (window.Next() && !context.IsCancelled())
    {
        std::optional<Ready> current;
        if (context.CaptureScreen())
        {
            const auto source = context.GetCurrentFrame();
            if (!source) return {};
            const auto image = Image(*source);
            const auto marker = CareerClawVision::MatchMarker(image, game, settings, gameThreshold);
            if (!marker.found)
            {
                const auto resultMarker = CareerClawVision::MatchMarker(image, result, settings, resultThreshold);
                resultStable = resultMarker.found ? resultStable + 1 : 0;
                if (resultStable >= settings.stableSamples) return NextClaw{true, {}};
            }
            else
            {
                resultStable = 0;
                const auto label = CareerClawVision::MatchMarker(image, creditLabel, settings, settings.creditLabelThreshold, false);
                if (label.found && label.y + label.height < marker.y)
                {
                    const int padding = static_cast<int>(std::ceil(label.height * settings.creditOcrPaddingLabelRatio));
                    const int left = (std::max)(0, label.x - padding);
                    const int top = (std::max)(0, label.y - padding);
                    const int bottom = (std::min)(marker.y, label.y + label.height + padding);
                    auto crop = Resources::crop_image(image, left, top, image.width - left, bottom - top);
                    crop = Resources::resize_image(crop, (std::max)(1, static_cast<int>(crop.width * settings.ocrScale)),
                        (std::max)(1, static_cast<int>(crop.height * settings.ocrScale)));
                    crop = Vision::binarize_color(crop, settings.ocrTextColor);
                    const auto credit = CareerClawMachineFlow::ParseCredit(context.RecognizeImageText(crop, "en-US"));
                    if (context.GetCurrentFrame() != source) return {}; // mixed-frame evidence
                    if (credit && *credit > 0 && (!previousCredit || *credit < *previousCredit))
                        if (auto control = CareerClawVision::FindControl(image, marker, settings))
                            current = Ready{*credit, *control, source};
                }
            }
        }
        else resultStable = 0;
        if (current)
        {
            const auto tolerance = current->control.width * settings.stableToleranceControlRatio;
            stable = previous && previous->credit == current->credit
                && std::abs(previous->control.x - current->control.x) <= tolerance
                && std::abs(previous->control.y - current->control.y) <= tolerance ? stable + 1 : 1;
            if (stable >= settings.stableSamples) return NextClaw{false, current};
        }
        else stable = 0;
        previous = std::move(current);
        context.Sleep(settings.pollIntervalMs);
    }
    return {};
}

CareerClawFlowResult CloseResult(ITaskExecutionContext& context, const Vision::GrayImage& templ,
    double threshold, const CareerClawMachineSettings& settings)
{
    int stable = 0;
    std::optional<UmaImageRect> previous;
    PollWindow window(settings.resultTimeoutMs, settings.pollIntervalMs);
    while (window.Next() && !context.IsCancelled())
    {
        std::optional<UmaImageRect> button;
        std::shared_ptr<const TaskFrameSnapshot> source;
        if (context.CaptureScreen() && (source = context.GetCurrentFrame()))
        {
            const auto image = Image(*source);
            const auto marker = CareerClawVision::MatchMarker(image, templ, settings, threshold);
            const auto buttons = CareerClawVision::FindResultButtons(image, marker, settings);
            if (buttons.size() == 1) button = buttons.front();
        }
        stable = button && previous && Equal(*button, *previous) ? stable + 1 : button ? 1 : 0;
        previous = button;
        if (stable >= settings.stableSamples && source && button)
        {
            Vision::MatchResult detected{true, 1.0, button->x, button->y, button->width, button->height};
            const auto match = SourceMatch(detected, *source);
            if (!context.TapMatch(match.CenterX(), match.CenterY(), match))
                return Failed(context, "Claw result OK input failed.");
            int missing = 0;
            PollWindow exit(settings.exitTimeoutMs, settings.pollIntervalMs);
            while (exit.Next() && !context.IsCancelled())
            {
                if (context.CaptureScreen() && (source = context.GetCurrentFrame()))
                {
                    missing = CareerClawVision::MatchMarker(Image(*source), templ, settings, threshold).found ? 0 : missing + 1;
                    if (missing >= settings.stableSamples) return {ActionExecutionStatus::Success, {}};
                }
                context.Sleep(settings.pollIntervalMs);
            }
            return Failed(context, "Claw result OK did not close the result screen.");
        }
        context.Sleep(settings.pollIntervalMs);
    }
    return Failed(context, "Claw result was not stable or did not provide exactly one OK button.");
}
} // namespace

CareerClawMachineSettings::CareerClawMachineSettings()
    : whiteControlColor(Color(235, 220, 235, 255, 255, 255)),
      pinkControlColor(Color(210, 0, 140, 255, 229, 255, 20, 255, -255, -15)),
      resultButtonColor(Color(60, 150, 0, 190, 255, 120)), ocrTextColor(Color(235, 235, 235, 255, 255, 255)) {}

CareerClawMachineSettings CareerClawMachineSettings::FromJson(const Json& options)
{
    CareerClawMachineSettings s;
    if (!options.contains("clawMachine")) return s;
    const auto& j = options.at("clawMachine");
    if (!j.is_object()) throw std::invalid_argument("clawMachine must be a settings object.");
#define UMA_CLAW_OPTION(name) s.name = j.value(#name, s.name)
    UMA_CLAW_OPTION(markerSearchScale); UMA_CLAW_OPTION(markerCandidateThreshold); UMA_CLAW_OPTION(creditLabelThreshold);
    UMA_CLAW_OPTION(ocrScale); UMA_CLAW_OPTION(creditOcrPaddingLabelRatio); UMA_CLAW_OPTION(controlBorderPaddingRatio);
    UMA_CLAW_OPTION(stableToleranceControlRatio); UMA_CLAW_OPTION(controlPinkPixelRatio);
    UMA_CLAW_OPTION(readyTimeoutMs); UMA_CLAW_OPTION(nextAttemptTimeoutMs); UMA_CLAW_OPTION(resultTimeoutMs); UMA_CLAW_OPTION(exitTimeoutMs);
    UMA_CLAW_OPTION(pollIntervalMs); UMA_CLAW_OPTION(holdDurationMs); UMA_CLAW_OPTION(stableSamples);
#undef UMA_CLAW_OPTION
    for (auto value : {s.markerSearchScale, s.markerCandidateThreshold, s.creditLabelThreshold, s.ocrScale,
        s.creditOcrPaddingLabelRatio, s.controlBorderPaddingRatio, s.stableToleranceControlRatio, s.controlPinkPixelRatio})
        if (!std::isfinite(value) || value < 0) throw std::invalid_argument("Invalid claw numeric setting.");
    if (s.pollIntervalMs <= 0 || s.readyTimeoutMs <= 0 || s.nextAttemptTimeoutMs <= 0 || s.resultTimeoutMs <= 0
        || s.exitTimeoutMs <= 0 || s.holdDurationMs < 0 || s.holdDurationMs > 60000 || s.stableSamples <= 0
        || s.ocrScale < .1 || s.ocrScale > 10)
        throw std::invalid_argument("Invalid claw timing or OCR setting.");
    if (j.contains("whiteControlColor")) s.whiteControlColor = ReadColor(j.at("whiteControlColor"), s.whiteControlColor);
    if (j.contains("pinkControlColor")) s.pinkControlColor = ReadColor(j.at("pinkControlColor"), s.pinkControlColor);
    if (j.contains("resultButtonColor")) s.resultButtonColor = ReadColor(j.at("resultButtonColor"), s.resultButtonColor);
    if (j.contains("ocrTextColor")) s.ocrTextColor = ReadColor(j.at("ocrTextColor"), s.ocrTextColor);
    if (j.contains("controlShape")) s.controlShape = ReadBlob(j.at("controlShape"), s.controlShape);
    if (j.contains("resultButtonShape")) s.resultButtonShape = ReadBlob(j.at("resultButtonShape"), s.resultButtonShape);
    return s;
}

Vision::MatchResult CareerClawVision::MatchMarker(const Vision::GrayImage& frame,
    const Vision::GrayImage& marker, const CareerClawMachineSettings& s,
    double threshold, bool colorText, int referenceWidth, int referenceHeight)
{
    if (referenceWidth <= 0 || referenceHeight <= 0 || frame.width <= 0 || frame.height <= 0) return {};
    if (frame.rgba.empty() || marker.rgba.empty())
        return Vision::find(frame, marker, {}, threshold, referenceWidth, referenceHeight);
    const auto templ = Resources::resize_image(marker, (std::max)(1, marker.width * frame.width / referenceWidth),
        (std::max)(1, marker.height * frame.height / referenceHeight), true);
    const double scale = std::clamp(s.markerSearchScale, .1, 1.0);
    const auto reducedFrame = Resources::resize_image(frame, (std::max)(1, static_cast<int>(frame.width * scale)),
        (std::max)(1, static_cast<int>(frame.height * scale)), true);
    const auto reducedTemplate = Resources::resize_image(templ, (std::max)(1, static_cast<int>(templ.width * scale)),
        (std::max)(1, static_cast<int>(templ.height * scale)), true);
    const auto candidate = Vision::find_color(reducedFrame, reducedTemplate, {}, s.markerCandidateThreshold,
        reducedFrame.width, reducedFrame.height);
    if (!candidate.found) return {};
    const int padding = static_cast<int>(std::ceil(2 / scale));
    const int left = (std::max)(0, static_cast<int>(candidate.x / scale) - padding);
    const int top = (std::max)(0, static_cast<int>(candidate.y / scale) - padding);
    const int right = (std::min)(frame.width, static_cast<int>((candidate.x + candidate.width) / scale) + padding);
    const int bottom = (std::min)(frame.height, static_cast<int>((candidate.y + candidate.height) / scale) + padding);
    return Vision::find_color(frame, templ, std::array<int32_t, 4>{left, top, right - left, bottom - top},
        threshold, frame.width, frame.height, colorText);
}

std::optional<UmaImageRect> CareerClawVision::FindControl(const Vision::GrayImage& frame,
    const Vision::MatchResult& marker, const CareerClawMachineSettings& s)
{
    if (!marker.found) return {};
    const int top = marker.y + marker.height;
    const auto blobs = Vision::find_color_blobs(frame, s.whiteControlColor, {0, top, frame.width, frame.height - top});
    std::optional<UmaImageRect> best;
    for (const auto& blob : blobs)
    {
        if (!Fits(frame, blob, s.controlShape) || blob.y <= top) continue;
        const int padding = (std::max)(1, static_cast<int>(blob.width * s.controlBorderPaddingRatio));
        const int left = (std::max)(0, blob.x - padding), upper = (std::max)(0, blob.y - padding);
        const int right = (std::min)(frame.width, blob.x + blob.width + padding);
        const int bottom = (std::min)(frame.height, blob.y + blob.height + padding);
        const UmaImageRect bounds{blob.x, blob.y, blob.width, blob.height};
        const auto [matched, sampled] = Vision::count_color(frame, s.pinkControlColor, {left, upper, right - left, bottom - upper}, &bounds);
        if (sampled == 0 || static_cast<double>(matched) < static_cast<double>(sampled) * s.controlPinkPixelRatio) continue;
        if (!best || blob.y + blob.height > best->y + best->height) best = bounds;
    }
    return best;
}

std::vector<UmaImageRect> CareerClawVision::FindResultButtons(const Vision::GrayImage& frame,
    const Vision::MatchResult& marker, const CareerClawMachineSettings& s)
{
    std::vector<UmaImageRect> result;
    if (!marker.found) return result;
    const int top = marker.y + marker.height;
    for (const auto& blob : Vision::find_color_blobs(frame, s.resultButtonColor, {0, top, frame.width, frame.height - top}))
        if (Fits(frame, blob, s.resultButtonShape)) result.push_back({blob.x, blob.y, blob.width, blob.height});
    return result;
}

CareerClawMachineFlow::CareerClawMachineFlow(std::shared_ptr<CareerVisualResources> resources)
    : m_resources(std::move(resources)) {}

std::optional<int> CareerClawMachineFlow::ParseCredit(const std::vector<TaskOcrDetection>& text)
{
    static const std::regex labelPattern(R"(\bCREDIT\b)", std::regex::icase);
    static const std::regex inlinePattern(R"(\bCREDIT\s+(\d+)\b)", std::regex::icase);
    static const std::regex digits(R"(^\s*\d+\s*$)");
    const TaskOcrDetection* label = nullptr;
    for (const auto& detection : text)
        if (std::regex_search(detection.text, labelPattern))
        { if (label) return {}; label = &detection; }
    if (!label) return {};
    try
    {
        std::smatch match;
        if (std::regex_search(label->text, match, inlinePattern)) return std::stoi(match.str(1));
        const TaskOcrDetection* number = nullptr;
        for (const auto& detection : text)
            if (detection.x >= label->x + label->width && detection.y < label->y + label->height
                && detection.y + detection.height > label->y && std::regex_match(detection.text, digits))
            { if (number) return {}; number = &detection; }
        return number ? std::optional<int>(std::stoi(number->text)) : std::nullopt;
    }
    catch (...) { return {}; }
}

CareerClawFlowResult CareerClawMachineFlow::PressControl(ITaskExecutionContext& context,
    const TaskMatchResult& control, int holdDurationMs)
{
    if (!control.found || control.frameId == 0 || context.IsCancelled())
        return Failed(context, "Claw control requires its current source frame.");
    struct Release
    {
        ITaskExecutionContext& context;
        bool attempted{}, released{};
        ~Release() { if (attempted && !released) { try { static_cast<void>(context.TouchCancel()); } catch (...) {} } }
    } release{context};
    try
    {
        release.attempted = true;
        if (!context.TouchDownMatch(control.CenterX(), control.CenterY(), control))
            return Failed(context, "Claw DOWN failed.");
        context.Sleep(holdDurationMs);
        if (context.IsCancelled()) return Failed(context, "Claw hold was canceled.");
        if (!context.TouchUp()) return Failed(context, "Claw UP failed.");
        release.released = true;
        return {ActionExecutionStatus::Success, {}};
    }
    catch (const TaskEngine::TaskContextInterruption&) { throw; }
    catch (const std::exception& error) { return Failed(context, error.what()); }
}

CareerClawFlowResult CareerClawMachineFlow::Run(ITaskExecutionContext& context, bool resultScreen) const
{
    if (!m_resources || !m_resources->IsReady()) return Failed(context, "Claw resource snapshot is unavailable.");
    try
    {
        const auto* screen = m_resources->FindScreen("claw_machine");
        const auto* result = m_resources->FindScreen("claw_machine_result");
        const auto* label = m_resources->FindAsset("career.turn.claw.credit_label");
        if (!screen || !result || !label || screen->templates.empty() || result->templates.empty())
            return Failed(context, "Claw recognition resources are missing.");
        const auto gameTemplate = context.LoadTemplateImage(screen->templates.front().path);
        const auto resultTemplate = context.LoadTemplateImage(result->templates.front().path);
        const auto creditTemplate = context.LoadTemplateImage(label->path);
        if (!gameTemplate || !resultTemplate || !creditTemplate) return Failed(context, "Claw recognition templates could not be loaded.");
        const auto settings = CareerClawMachineSettings::FromJson(m_resources->GetProfileOptions());
        const auto resultThreshold = result->templates.front().threshold;
        if (resultScreen) return CloseResult(context, *resultTemplate, resultThreshold, settings);
        if (!context.GetCurrentFrame() && !context.CaptureScreen()) return Failed(context, "Claw capture failed.");
        if (!context.PrepareHeldTouch()) return Failed(context, "Held-touch input could not be prepared.");
        std::optional<int> previousCredit;
        int timeout = settings.readyTimeoutMs;
        while (!context.IsCancelled())
        {
            const auto next = WaitForNext(context, *gameTemplate, screen->templates.front().threshold,
                *resultTemplate, resultThreshold, *creditTemplate, settings, previousCredit, timeout);
            if (!next) return Failed(context, "Claw control or decreasing CREDIT count could not be confirmed.");
            if (next->isResult) return CloseResult(context, *resultTemplate, resultThreshold, settings);
            const auto& ready = *next->ready;
            const auto control = SourceMatch({true, 1.0, ready.control.x, ready.control.y, ready.control.width, ready.control.height}, *ready.source);
            const auto pressed = PressControl(context, control, settings.holdDurationMs);
            if (pressed.status != ActionExecutionStatus::Success) return pressed;
            previousCredit = ready.credit;
            timeout = settings.nextAttemptTimeoutMs;
        }
        return Failed(context, "Claw flow canceled.");
    }
    catch (const TaskEngine::TaskContextInterruption&) { throw; }
    catch (const std::exception& error) { return Failed(context, error.what()); }
}

} // namespace UmaAssistant::Career
