#include "UraTrainingVisualScanner.hpp"

#include "UmaAssistantCore/CareerDomain/TrainingStrategies.hpp"
#include "UmaAssistantCore/Vision/NumericImageProcessing.hpp"

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <iterator>
#include <limits>
#include <regex>
#include <span>
#include <unordered_map>
#include <utility>

namespace UmaAssistant::Career::Ura
{

namespace
{

constexpr int kReferenceWidth = 900;
constexpr int kReferenceHeight = 1600;
constexpr int kPreviewSettleMilliseconds = 120;
constexpr int kMaximumPreviewAttempts = 3;
constexpr const char* kTrainingTypes[] = {"speed", "stamina", "power", "guts", "wit"};

std::string Lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

std::string NormalizeSemantic(const std::string& value)
{
    std::string normalized;
    normalized.reserve(value.size());
    for (unsigned char ch : value)
        if (std::isalnum(ch)) normalized.push_back(static_cast<char>(std::tolower(ch)));
    return normalized;
}

std::optional<TaskEngine::TaskRoi> ToRoi(const std::optional<std::vector<int>>& value)
{
    if (!value.has_value() || value->size() < 4) return std::nullopt;
    return TaskEngine::TaskRoi{(*value)[0], (*value)[1], (*value)[2], (*value)[3]};
}

std::optional<TaskEngine::TaskRoi> UnionRoi(
    const std::optional<std::vector<int>>& first,
    const std::optional<std::vector<int>>& second)
{
    const auto a = ToRoi(first);
    const auto b = ToRoi(second);
    if (!a || !b || a->width <= 0 || a->height <= 0 || b->width <= 0 || b->height <= 0)
        return std::nullopt;
    const int left = (std::min)(a->x, b->x);
    const int top = (std::min)(a->y, b->y);
    const int right = (std::max)(a->x + a->width, b->x + b->width);
    const int bottom = (std::max)(a->y + a->height, b->y + b->height);
    return TaskEngine::TaskRoi{left, top, right - left, bottom - top};
}

int ScaleBound(int value, int sourceSize, int referenceSize)
{
    if (referenceSize <= 0) return 0;
    return static_cast<int>(std::nearbyint(static_cast<double>(value) * sourceSize / referenceSize));
}

std::optional<UmaAssistant::Vision::GrayImage> MakeFrameImage(const TaskEngine::TaskFrameSnapshot& frame)
{
    if (frame.width <= 0 || frame.height <= 0 || frame.width > 10000 || frame.height > 10000)
        return std::nullopt;
    const auto pixelCount = static_cast<std::size_t>(frame.width) * static_cast<std::size_t>(frame.height);
    UmaAssistant::Vision::GrayImage image;
    image.width = frame.width;
    image.height = frame.height;
    if (frame.grayPixels.size() == pixelCount)
        image.gray = frame.grayPixels;
    else if (frame.rgbaPixels.size() >= pixelCount * 4)
    {
        image.gray.resize(pixelCount);
        for (std::size_t index = 0; index < pixelCount; ++index)
        {
            const auto offset = index * 4;
            const auto red = frame.rgbaPixels[offset];
            const auto green = frame.rgbaPixels[offset + 1];
            const auto blue = frame.rgbaPixels[offset + 2];
            image.gray[index] = static_cast<std::uint8_t>((299 * red + 587 * green + 114 * blue + 500) / 1000);
        }
    }
    else return std::nullopt;
    if (frame.rgbaPixels.size() >= pixelCount * 4)
        image.rgba.assign(frame.rgbaPixels.begin(), frame.rgbaPixels.begin() + pixelCount * 4);
    return image;
}

std::optional<UmaAssistant::Vision::GrayImage> CropReferenceRoi(
    const UmaAssistant::Vision::GrayImage& frame,
    const TaskEngine::TaskRoi& roi)
{
    if (frame.width <= 0 || frame.height <= 0 || roi.width <= 0 || roi.height <= 0)
        return std::nullopt;
    const int left = std::clamp(ScaleBound(roi.x, frame.width, kReferenceWidth), 0, frame.width);
    const int top = std::clamp(ScaleBound(roi.y, frame.height, kReferenceHeight), 0, frame.height);
    const int right = std::clamp(ScaleBound(roi.x + roi.width, frame.width, kReferenceWidth), left, frame.width);
    const int bottom = std::clamp(ScaleBound(roi.y + roi.height, frame.height, kReferenceHeight), top, frame.height);
    const int width = right - left;
    const int height = bottom - top;
    if (width <= 0 || height <= 0) return std::nullopt;

    UmaAssistant::Vision::GrayImage crop;
    crop.width = width;
    crop.height = height;
    crop.gray.resize(static_cast<std::size_t>(width) * height);
    if (!frame.rgba.empty()) crop.rgba.resize(static_cast<std::size_t>(width) * height * 4);
    for (int y = 0; y < height; ++y)
    {
        const auto sourceRow = static_cast<std::size_t>(top + y) * frame.width + left;
        const auto targetRow = static_cast<std::size_t>(y) * width;
        std::copy_n(frame.gray.data() + sourceRow, width, crop.gray.data() + targetRow);
        if (!crop.rgba.empty())
            std::copy_n(frame.rgba.data() + sourceRow * 4, static_cast<std::size_t>(width) * 4,
                crop.rgba.data() + targetRow * 4);
    }
    return crop;
}

UmaAssistant::Vision::GrayImage UpscaleForOcr(const UmaAssistant::Vision::GrayImage& image)
{
    UmaAssistant::Vision::GrayImage scaled;
    if (image.width <= 0 || image.height <= 0 || image.width > 5000 || image.height > 5000)
        return scaled;
    scaled.width = image.width * 2;
    scaled.height = image.height * 2;
    scaled.gray.resize(static_cast<std::size_t>(scaled.width) * scaled.height, 255);
    for (int y = 0; y < scaled.height; ++y)
    {
        const auto sourceRow = static_cast<std::size_t>(y / 2) * image.width;
        const auto targetRow = static_cast<std::size_t>(y) * scaled.width;
        for (int x = 0; x < scaled.width; ++x)
            scaled.gray[targetRow + x] = image.gray[sourceRow + x / 2];
    }
    return scaled;
}

UmaColorRule GainSeedRule()
{
    UmaColorRule rule{};
    rule.minimum_red = 240; rule.minimum_green = 95; rule.minimum_blue = 0;
    rule.maximum_red = 255; rule.maximum_green = 225; rule.maximum_blue = 95;
    rule.minimum_red_green = 25; rule.maximum_red_green = 255;
    rule.minimum_green_blue = -255; rule.maximum_green_blue = 255;
    rule.use_hsv = 0; rule.sample_step = 2;
    return rule;
}

std::pair<std::uint64_t, std::uint64_t> WarmColorCount(
    const UmaAssistant::Vision::GrayImage& frame, const TaskEngine::TaskRoi& roi)
{
    if (frame.rgba.empty()) return {0, 0};
    return UmaAssistant::Vision::count_color(frame, GainSeedRule(),
        UmaImageRect{ScaleBound(roi.x, frame.width, kReferenceWidth),
            ScaleBound(roi.y, frame.height, kReferenceHeight),
            ScaleBound(roi.width, frame.width, kReferenceWidth),
            ScaleBound(roi.height, frame.height, kReferenceHeight)}, nullptr);
}

bool SameFrame(TaskEngine::ITaskExecutionContext& context,
    const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& expected)
{
    const auto current = context.GetCurrentFrame();
    return expected && current && current.get() == expected.get()
        && current->frameId == expected->frameId
        && current->sessionGeneration == expected->sessionGeneration
        && current->geometryGeneration == expected->geometryGeneration;
}

bool MatchBelongsToFrame(const TaskEngine::TaskMatchResult& match, const TaskEngine::TaskFrameSnapshot& frame)
{
    return (match.frameId == 0 || frame.frameId == 0 || match.frameId == frame.frameId)
        && (match.sessionGeneration == 0 || frame.sessionGeneration == 0
            || match.sessionGeneration == frame.sessionGeneration)
        && (match.geometryGeneration == 0 || frame.geometryGeneration == 0
            || match.geometryGeneration == frame.geometryGeneration);
}

std::optional<int> ParseStrictValue(const std::vector<TaskEngine::TaskOcrDetection>& detections,
    bool failureRate, int maximum, double& confidence)
{
    static const std::regex gainPattern(R"(^\s*[+*#]\s*(\d{1,4})\s*$)");
    static const std::regex failurePattern(R"(^\s*(\d{1,3})\s*%\s*$)");
    confidence = 1.0;
    std::vector<int> values;
    bool sawNumeric = false;
    for (const auto& detection : detections)
    {
        std::string text = detection.text;
        if (text.empty()) continue;
        if (failureRate)
        {
            const auto position = Lower(text).find("failure");
            if (position != std::string::npos) text.erase(position, 7);
        }
        std::replace(text.begin(), text.end(), 'O', '0');
        std::replace(text.begin(), text.end(), 'o', '0');
        std::replace(text.begin(), text.end(), 'l', '1');
        std::replace(text.begin(), text.end(), 'I', '1');
        const bool hasDigit = std::any_of(text.begin(), text.end(), [](unsigned char ch) {
            return std::isdigit(ch) != 0;
        });
        if (!hasDigit) continue;
        sawNumeric = true;
        std::smatch match;
        const auto& pattern = failureRate ? failurePattern : gainPattern;
        if (!std::regex_match(text, match, pattern)) return std::nullopt;
        try
        {
            const int value = std::stoi(match.str(1));
            if (value < 0 || value > maximum || !std::isfinite(detection.confidence)) return std::nullopt;
            values.push_back(value);
            confidence = std::min(confidence, std::clamp(detection.confidence, 0.0, 1.0));
        }
        catch (...) { return std::nullopt; }
    }
    if (!sawNumeric || values.empty()) return std::nullopt;
    std::sort(values.begin(), values.end());
    values.erase(std::unique(values.begin(), values.end()), values.end());
    return values.size() == 1 ? std::optional<int>{values.front()} : std::nullopt;
}

std::optional<int> ParseAuxiliaryValue(const std::vector<TaskEngine::TaskOcrDetection>& detections,
    int maximum, double& confidence)
{
    static const std::regex numberPattern(R"(^\s*(?:[A-Za-z]+\s*)?(\d{1,3})\s*$)");
    confidence = 1.0;
    std::vector<int> values;
    for (const auto& detection : detections)
    {
        std::smatch match;
        if (!std::regex_match(detection.text, match, numberPattern)) continue;
        try
        {
            const int value = std::stoi(match.str(1));
            if (value < 0 || value > maximum || !std::isfinite(detection.confidence)) continue;
            values.push_back(value);
            confidence = std::min(confidence, std::clamp(detection.confidence, 0.0, 1.0));
        }
        catch (...) {}
    }
    std::sort(values.begin(), values.end());
    values.erase(std::unique(values.begin(), values.end()), values.end());
    return values.size() == 1 ? std::optional<int>{values.front()} : std::nullopt;
}

CareerDomain::ObservationSource CombineSources(CareerDomain::ObservationSource left,
    CareerDomain::ObservationSource right)
{
    return left == right ? left : CareerDomain::ObservationSource::Derived;
}

} // namespace

struct UraTrainingVisualScanner::SelectionFrame
{
    std::shared_ptr<const TaskEngine::TaskFrameSnapshot> frame;
    CareerDomain::Ura::UraTrainingMarkerEvidence evidence;
    std::unordered_map<std::string, TaskEngine::TaskMatchResult> logoMatches;
    bool screenChanged{false};
    std::string error;
    [[nodiscard]] bool IsValid() const noexcept
    {
        return !screenChanged && error.empty() && evidence.matches.size() == 5;
    }
};

struct UraTrainingVisualScanner::CandidateRead
{
    CareerDomain::Ura::TrainingCandidate candidate;
    bool screenChanged{false};
    std::string error;
};

struct UraTrainingVisualScanner::ScanCache
{
    struct Field
    {
        TaskEngine::TaskRoi roi;
        Vision::GrayImage crop;
        CareerDomain::ObservedValue<int> reading;
    };

    int width{0};
    int height{0};
    int orientation{0};
    std::uint64_t session{0};
    std::uint64_t geometry{0};
    std::unordered_map<std::string, Field> fields;
    std::unordered_map<std::string, TaskEngine::TaskMatchResult> logoHints;

    void BindFrame(const TaskEngine::TaskFrameSnapshot& frame)
    {
        if (width == frame.width && height == frame.height && orientation == frame.orientation
            && session == frame.sessionGeneration && geometry == frame.geometryGeneration)
            return;
        fields.clear();
        logoHints.clear();
        width = frame.width;
        height = frame.height;
        orientation = frame.orientation;
        session = frame.sessionGeneration;
        geometry = frame.geometryGeneration;
    }

    std::optional<CareerDomain::ObservedValue<int>> Find(
        const std::string& key, const TaskEngine::TaskRoi& roi, const Vision::GrayImage& crop) const
    {
        const auto cached = fields.find(key);
        if (cached == fields.end()) return std::nullopt;
        const auto& field = cached->second;
        // Compare original pixels, including color, rather than just processed
        // glyphs or a hash. This is OCR reuse, never cached frame/click evidence.
        if (field.roi.x != roi.x || field.roi.y != roi.y || field.roi.width != roi.width
            || field.roi.height != roi.height || field.crop.width != crop.width
            || field.crop.height != crop.height || field.crop.gray != crop.gray || field.crop.rgba != crop.rgba)
            return std::nullopt;
        return field.reading;
    }

    void Remember(const std::string& key, const TaskEngine::TaskRoi& roi,
        const Vision::GrayImage& crop, const CareerDomain::ObservedValue<int>& reading)
    {
        if (reading.source == CareerDomain::ObservationSource::Ocr
            && reading.IsReliable(CareerDomain::Ura::UraSmartTrainingScorer::MinimumCoreConfidence))
            fields.insert_or_assign(key, Field{roi, crop, reading});
        else
            fields.erase(key);
    }
};

UraTrainingVisualScanner::UraTrainingVisualScanner(std::shared_ptr<CareerVisualResources> resources)
    : m_resources(std::move(resources))
{
}

UraTrainingVisualScanResult UraTrainingVisualScanner::ScanCandidates(
    TaskEngine::ITaskExecutionContext& context,
    CareerDomain::Ura::UraTrainingScanner& scanner,
    CareerDomain::Ura::RaceDistanceBand distance,
    const std::string& careerStage,
    std::optional<int> energyPercent,
    std::optional<int> turnIndex)
{
    // Each scan starts empty even when a subsequent turn renders identical values.
    m_scanCache = std::make_shared<ScanCache>();
    struct ClearCache
    {
        std::shared_ptr<ScanCache>& cache;
        ~ClearCache() { cache.reset(); }
    } clearCache{m_scanCache};
    std::string error;
    auto frame = CurrentFrame(context, error);
    if (!frame)
        return {scanner.Stage(), false, std::move(error)};

    auto selection = DetectSelection(context, frame);
    if (!selection.IsValid())
        return {scanner.Stage(), selection.screenChanged, selection.error.empty()
            ? "The training selection screen could not be verified." : std::move(selection.error)};

    scanner.BeginCandidateScan(selection.evidence, distance, careerStage, energyPercent, turnIndex);
    if (scanner.Stage() != CareerDomain::Ura::UraTrainingScannerStage::SamplingCandidates)
        return {scanner.Stage(), false, scanner.FailureReason()};

    std::vector<std::string> scanOrder;
    if (selection.evidence.raisedTrainingType.has_value())
    {
        std::string selected;
        if (CareerDomain::TrainingTypeCatalog::TryNormalize(
                *selection.evidence.raisedTrainingType, selected))
            scanOrder.push_back(std::move(selected));
    }
    for (const auto* trainingType : kTrainingTypes)
    {
        if (std::none_of(scanOrder.begin(), scanOrder.end(), [&](const auto& current) {
                return NormalizeSemantic(current) == NormalizeSemantic(trainingType);
            }))
            scanOrder.emplace_back(trainingType);
    }

    for (const auto& trainingType : scanOrder)
    {
        if (!SelectPreview(context, selection, trainingType, error))
            return {scanner.Stage(), selection.screenChanged, std::move(error)};

        const auto selected = selection.logoMatches.find(trainingType);
        if (selected == selection.logoMatches.end())
            return {scanner.Stage(), false, "The selected training card marker is missing."};
        auto first = ReadCandidate(context, selection.frame, trainingType, selected->second);
        if (first.screenChanged)
            return {scanner.Stage(), true, "The training selection screen changed during candidate sampling."};
        if (!scanner.SubmitCandidateSample(first.candidate))
            return {scanner.Stage(), false, scanner.FailureReason()};

        context.Sleep(kPreviewSettleMilliseconds);
        auto secondFrame = CaptureFreshFrame(context, selection.frame, error);
        if (!secondFrame)
            return {scanner.Stage(), false, std::move(error)};
        auto secondSelection = DetectSelection(context, secondFrame);
        if (!secondSelection.IsValid())
            return {scanner.Stage(), secondSelection.screenChanged, secondSelection.error.empty()
                ? "The training selection screen changed during candidate sampling."
                : std::move(secondSelection.error)};

        CareerDomain::Ura::TrainingCandidate secondCandidate = UnknownCandidate(trainingType);
        if (secondSelection.evidence.raisedTrainingType.has_value() &&
            NormalizeSemantic(*secondSelection.evidence.raisedTrainingType) == NormalizeSemantic(trainingType))
        {
            const auto secondLogo = secondSelection.logoMatches.find(trainingType);
            if (secondLogo != secondSelection.logoMatches.end())
            {
                auto second = ReadCandidate(context, secondFrame, trainingType, secondLogo->second);
                if (second.screenChanged)
                    return {scanner.Stage(), true, "The training selection screen changed during candidate sampling."};
                secondCandidate = std::move(second.candidate);
            }
        }
        if (!scanner.SubmitCandidateSample(secondCandidate))
            return {scanner.Stage(), false, scanner.FailureReason()};
        selection = std::move(secondSelection);
        if (scanner.Stage() != CareerDomain::Ura::UraTrainingScannerStage::SamplingCandidates)
            return {scanner.Stage(), false, scanner.FailureReason()};
    }

    return {scanner.Stage(), false, scanner.FailureReason()};
}

std::shared_ptr<const TaskEngine::TaskFrameSnapshot> UraTrainingVisualScanner::CurrentFrame(
    TaskEngine::ITaskExecutionContext& context,
    std::string& error) const
{
    auto frame = context.GetCurrentFrame();
    if (!frame)
    {
        if (!context.CaptureScreen())
        {
            error = "Could not capture the training selection screen.";
            return {};
        }
        frame = context.GetCurrentFrame();
    }
    if (!frame || frame->width <= 0 || frame->height <= 0 || !MakeFrameImage(*frame).has_value())
    {
        error = "The training selection frame has no usable image pixels.";
        return {};
    }
    return frame;
}

std::shared_ptr<const TaskEngine::TaskFrameSnapshot> UraTrainingVisualScanner::CaptureFreshFrame(
    TaskEngine::ITaskExecutionContext& context,
    const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& previous,
    std::string& error) const
{
    if (!previous)
    {
        error = "Cannot verify a fresh frame without a prior training selection frame.";
        return {};
    }
    if (!context.CaptureScreen())
    {
        error = "Could not capture a fresh training selection frame.";
        return {};
    }
    auto frame = context.GetCurrentFrame();
    if (!frame || !MakeFrameImage(*frame).has_value())
    {
        error = "The fresh training selection frame has no usable image pixels.";
        return {};
    }
    if (previous.get() == frame.get() ||
        (previous->frameId != 0 && frame->frameId != 0 && previous->frameId == frame->frameId))
    {
        error = "The training selection capture did not produce a new frame.";
        return {};
    }
    if ((previous->sessionGeneration != 0 && frame->sessionGeneration != 0 &&
            previous->sessionGeneration != frame->sessionGeneration) ||
        (previous->geometryGeneration != 0 && frame->geometryGeneration != 0 &&
            previous->geometryGeneration != frame->geometryGeneration))
    {
        error = "The device session or display geometry changed during training selection.";
        return {};
    }
    return frame;
}

UraTrainingVisualScanner::SelectionFrame UraTrainingVisualScanner::DetectSelection(
    TaskEngine::ITaskExecutionContext& context,
    const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& frame) const
{
    SelectionFrame result;
    result.frame = frame;
    if (!m_resources || !m_resources->IsReady())
    {
        result.error = "Career visual resources are not ready for the training scan.";
        return result;
    }
    if (!frame || !SameFrame(context, frame))
    {
        result.error = "Training marker matching requires the current captured frame.";
        return result;
    }
    if (m_scanCache) m_scanCache->BindFrame(*frame);
    const auto* screen = m_resources->FindScreen("training_selection");
    if (!screen || screen->templates.empty())
    {
        result.error = "The training selection header template is not configured.";
        return result;
    }
    const auto& header = screen->templates.front();
    const auto headerMatch = context.MatchTemplateAdvanced(header.path, header.threshold, header.roi,
        header.algorithm, kReferenceWidth, kReferenceHeight, {});
    if (!headerMatch.found || !MatchBelongsToFrame(headerMatch, *frame))
    {
        result.screenChanged = true;
        return result;
    }

    const auto* backTask = m_resources->FindExecutionTask("training_selection_back");
    if (!backTask || !backTask->templatePath || !backTask->roi)
    {
        result.error = "The training selection Back marker is not configured.";
        return result;
    }
    const auto backMatch = context.MatchTemplateAdvanced(*backTask->templatePath,
        backTask->templateThreshold, ToRoi(backTask->roi), "MatchTemplateExhaustiveColor",
        backTask->referenceWidth, backTask->referenceHeight, backTask->scaleCandidates);
    if (!backMatch.found || !MatchBelongsToFrame(backMatch, *frame))
    {
        result.screenChanged = true;
        return result;
    }

    result.evidence.trainingSelectionScreen = true;
    result.evidence.matches.reserve(std::size(kTrainingTypes));
    int highestLogoY = (std::numeric_limits<int>::max)();
    std::string raisedType;
    for (const auto* trainingType : kTrainingTypes)
    {
        const auto* normalTask = m_resources->FindExecutionTask(
            std::string("training_selection_training_") + trainingType);
        const auto* raisedTask = m_resources->FindExecutionTask(
            std::string("training_selection_") + trainingType + "_raised_probe");
        if (!normalTask || !raisedTask || !normalTask->templatePath ||
            *normalTask->templatePath != *raisedTask->templatePath || !normalTask->roi || !raisedTask->roi)
        {
            result.error = std::string("The ") + trainingType + " training marker is not configured.";
            return result;
        }
        const auto roi = UnionRoi(normalTask->roi, raisedTask->roi);
        if (!roi)
        {
            result.error = std::string("The ") + trainingType + " training marker has invalid search regions.";
            return result;
        }
        TaskEngine::TaskMatchResult match;
        if (m_scanCache)
        {
            const auto hint = m_scanCache->logoHints.find(trainingType);
            if (hint != m_scanCache->logoHints.end())
            {
                const auto& old = hint->second;
                const int left = (std::max)(roi->x, old.x - 22);
                const int top = (std::max)(roi->y, old.y - 22);
                const int right = (std::min)(roi->x + roi->width, old.x + old.width + 22);
                const int bottom = (std::min)(roi->y + roi->height, old.y + old.height + 22);
                if (right > left && bottom > top)
                    match = context.MatchTemplateAdvanced(*normalTask->templatePath,
                        normalTask->templateThreshold, TaskEngine::TaskRoi{left, top, right - left, bottom - top},
                        "MatchTemplateExhaustiveColor", normalTask->referenceWidth,
                        normalTask->referenceHeight, normalTask->scaleCandidates);
            }
        }
        // A weak local match is only a hint. Use the original search region
        // on miss, movement, or ambiguity; chevron/row verification still runs.
        if (!match.found || match.score < 0.9 || !MatchBelongsToFrame(match, *frame))
            match = context.MatchTemplateAdvanced(*normalTask->templatePath,
                normalTask->templateThreshold, roi, "MatchTemplateExhaustiveColor",
                normalTask->referenceWidth, normalTask->referenceHeight, normalTask->scaleCandidates);
        if (!match.found || !MatchBelongsToFrame(match, *frame))
        {
            result.error = std::string("Could not find the ") + trainingType + " training card marker.";
            return result;
        }
        result.evidence.matches.push_back({trainingType, true, match.score});
        result.logoMatches.emplace(trainingType, match);
        if (match.CenterY() < highestLogoY)
        {
            highestLogoY = match.CenterY();
            raisedType = trainingType;
        }
    }
    if (raisedType.empty())
    {
        result.error = "The raised training card could not be identified.";
        return result;
    }

    const auto* chevronTask = m_resources->FindExecutionTask("training_selection_selected_chevron");
    if (!chevronTask || !chevronTask->templatePath || !chevronTask->roi)
    {
        result.error = "The selected training card marker is not configured.";
        return result;
    }
    const auto chevronRoi = ToRoi(chevronTask->roi);
    if (!chevronRoi)
    {
        result.error = "The selected training card marker has an invalid search region.";
        return result;
    }
    std::vector<std::string> markedTypes;
    for (const auto* trainingType : kTrainingTypes)
    {
        const auto* cardTask = m_resources->FindExecutionTask(
            std::string("training_selection_training_") + trainingType);
        const auto cardRoi = cardTask ? ToRoi(cardTask->roi) : std::nullopt;
        if (!cardRoi)
        {
            result.error = std::string("The ") + trainingType + " card marker region is missing.";
            return result;
        }
        const TaskEngine::TaskRoi markerSearch{cardRoi->x, chevronRoi->y,
            cardRoi->width, chevronRoi->height};
        const auto marker = context.MatchTemplateAdvanced(*chevronTask->templatePath,
            chevronTask->templateThreshold, markerSearch, "MatchTemplateExhaustiveColor",
            chevronTask->referenceWidth, chevronTask->referenceHeight, chevronTask->scaleCandidates);
        if (marker.found && MatchBelongsToFrame(marker, *frame))
            markedTypes.emplace_back(trainingType);
    }
    if (markedTypes.size() != 1)
    {
        result.error = "The selected training card marker is missing or ambiguous.";
        return result;
    }
    std::vector<int> flatRows;
    for (const auto* trainingType : kTrainingTypes)
    {
        if (NormalizeSemantic(trainingType) == NormalizeSemantic(markedTypes.front()))
            continue;
        const auto match = result.logoMatches.find(trainingType);
        if (match != result.logoMatches.end() && match->second.score >= 0.9)
            flatRows.push_back(match->second.CenterY());
    }
    std::sort(flatRows.begin(), flatRows.end());
    if (flatRows.size() < 2)
    {
        result.error = "The flat training card row could not be verified.";
        return result;
    }
    const int flatY = flatRows[flatRows.size() / 2];
    // A Duel badge or sparkle can beat the icon inside a broad search region.
    // The selected chevron provides independent column evidence. Verify each
    // icon on the raised/flat row before accepting that broad match, as the
    // managed smart detector does; a failed re-search stays unknown.
    for (const auto* trainingType : kTrainingTypes)
    {
        const auto* normalTask = m_resources->FindExecutionTask(
            std::string("training_selection_training_") + trainingType);
        const auto* clickTask = m_resources->FindExecutionTask(
            std::string("training_selection_") + trainingType + "_raised_click");
        if (!normalTask || !normalTask->roi || normalTask->roi->size() < 4 ||
            !normalTask->templatePath || !clickTask || !clickTask->clickOffset ||
            clickTask->clickOffset->size() < 2 || (*clickTask->clickOffset)[1] <= 0)
        {
            result.error = "The raised training card geometry is not configured.";
            return result;
        }
        const auto& bounds = *normalTask->roi;
        const int centerX = bounds[0] + bounds[2] / 2;
        const int centerY = flatY - (trainingType == markedTypes.front() ? (*clickTask->clickOffset)[1] : 0);
        auto& match = result.logoMatches.at(trainingType);
        if (std::abs(match.CenterX() - centerX) <= 22 && std::abs(match.CenterY() - centerY) <= 22)
            continue;
        const auto templ = context.LoadTemplateImage(*normalTask->templatePath);
        if (!templ)
        {
            result.error = std::string("The ") + trainingType + " training logo could not be loaded.";
            return result;
        }
        const TaskEngine::TaskRoi narrow{centerX - templ->width / 2 - 22,
            centerY - templ->height / 2 - 22, templ->width + 44, templ->height + 44};
        match = context.MatchTemplateAdvanced(*normalTask->templatePath,
            normalTask->templateThreshold, narrow, "MatchTemplateExhaustiveColor",
            normalTask->referenceWidth, normalTask->referenceHeight, normalTask->scaleCandidates);
        if (!match.found || !MatchBelongsToFrame(match, *frame))
        {
            result.error = std::string("The ") + trainingType + " training logo could not be verified on its card.";
            return result;
        }
    }
    highestLogoY = std::numeric_limits<int>::max();
    raisedType.clear();
    result.evidence.matches.clear();
    for (const auto* trainingType : kTrainingTypes)
    {
        const auto& match = result.logoMatches.at(trainingType);
        result.evidence.matches.push_back({trainingType, true, match.score});
        if (match.CenterY() < highestLogoY)
        {
            highestLogoY = match.CenterY();
            raisedType = trainingType;
        }
    }
    if (!SameFrame(context, frame))
    {
        result.screenChanged = true;
        result.error = "The captured training frame changed during marker verification.";
        return result;
    }
    if (NormalizeSemantic(markedTypes.front()) != NormalizeSemantic(raisedType))
    {
        result.error = "The selected card marker and verified logo heights disagree.";
        return result;
    }
    const auto* raisedClickTask = m_resources->FindExecutionTask(
        std::string("training_selection_") + markedTypes.front() + "_raised_click");
    if (!raisedClickTask || !raisedClickTask->clickOffset || raisedClickTask->clickOffset->size() < 2 ||
        (*raisedClickTask->clickOffset)[1] <= 0 ||
        std::abs(result.logoMatches.at(markedTypes.front()).CenterY() -
            (flatY - (*raisedClickTask->clickOffset)[1])) > 22)
    {
        result.error = "The selected marker and raised-card geometry disagree.";
        return result;
    }
    result.evidence.raisedTrainingType = std::move(raisedType);
    if (m_scanCache) m_scanCache->logoHints = result.logoMatches;
    return result;
}

bool UraTrainingVisualScanner::SelectPreview(
    TaskEngine::ITaskExecutionContext& context,
    SelectionFrame& current,
    const std::string& trainingType,
    std::string& error) const
{
    if (!current.IsValid() || !current.evidence.raisedTrainingType.has_value())
    {
        error = "The training card selection is not in a verified state.";
        return false;
    }
    if (NormalizeSemantic(*current.evidence.raisedTrainingType) == NormalizeSemantic(trainingType))
        return true;

    const auto target = current.logoMatches.find(trainingType);
    if (target == current.logoMatches.end() || !target->second.found ||
        !MatchBelongsToFrame(target->second, *current.frame))
    {
        error = std::string("Could not verify the ") + trainingType + " preview card in the current frame.";
        return false;
    }

    if (!SameFrame(context, current.frame))
    {
        error = "The training preview tap no longer refers to the current frame.";
        return false;
    }
    if (!context.TapMatch(target->second.CenterX(), target->second.CenterY(), target->second))
    {
        error = std::string("Could not tap the ") + trainingType + " preview button.";
        return false;
    }

    for (int attempt = 0; attempt < kMaximumPreviewAttempts; ++attempt)
    {
        context.Sleep(kPreviewSettleMilliseconds);
        auto nextFrame = CaptureFreshFrame(context, current.frame, error);
        if (!nextFrame)
            return false;
        auto next = DetectSelection(context, nextFrame);
        if (next.screenChanged)
        {
            current = std::move(next);
            error = "The training selection screen changed while opening a card preview.";
            return false;
        }
        if (next.IsValid() && next.evidence.raisedTrainingType.has_value() &&
            NormalizeSemantic(*next.evidence.raisedTrainingType) == NormalizeSemantic(trainingType))
        {
            current = std::move(next);
            return true;
        }
        current = std::move(next);
        if (!current.IsValid())
        {
            error = current.error.empty() ? "Training card markers became incomplete after a preview tap."
                                          : current.error;
            return false;
        }
    }
    error = std::string("Could not verify the ") + trainingType + " card as the raised preview.";
    return false;
}

UraTrainingVisualScanner::CandidateRead UraTrainingVisualScanner::ReadCandidate(
    TaskEngine::ITaskExecutionContext& context,
    const std::shared_ptr<const TaskEngine::TaskFrameSnapshot>& frame,
    const std::string& trainingType,
    const TaskEngine::TaskMatchResult& selectedLogo) const
{
    CandidateRead result;
    result.candidate = UnknownCandidate(trainingType);
    if (!m_resources || !frame || !SameFrame(context, frame) || !MatchBelongsToFrame(selectedLogo, *frame))
    {
        result.screenChanged = !SameFrame(context, frame);
        result.error = "Candidate OCR must use the captured frame and selected logo from one observation.";
        return result;
    }
    const auto* screen = m_resources->FindScreen("training_selection");
    const auto frameImage = MakeFrameImage(*frame);
    if (!screen || !frameImage)
    {
        result.error = "Training OCR regions or frame pixels are unavailable.";
        return result;
    }

    auto findRegion = [&](const std::string& semanticId) -> const CareerOcrRegionDefinition* {
        const auto wanted = NormalizeSemantic(semanticId);
        const auto it = std::find_if(screen->ocrRegions.begin(), screen->ocrRegions.end(), [&](const auto& item) {
            return NormalizeSemantic(item.semanticId) == wanted;
        });
        return it == screen->ocrRegions.end() ? nullptr : &*it;
    };
    const auto* stripRegion = findRegion("training.gain_strip");
    const auto stripPixels = stripRegion ? WarmColorCount(*frameImage, stripRegion->roi)
                                         : std::pair<std::uint64_t, std::uint64_t>{0, 0};
    const bool gainStripVisible = stripPixels.second > 0 && stripPixels.first >= 30 &&
        static_cast<double>(stripPixels.first) / static_cast<double>(stripPixels.second) >= 0.002;

    struct PreparedField
    {
        TaskEngine::TaskRoi roi;
        std::optional<UmaAssistant::Vision::GrayImage> crop;
        std::optional<UmaAssistant::Vision::GrayImage> image;
        std::optional<CareerDomain::ObservedValue<int>> cached;
        bool blank{false};
        std::vector<TaskEngine::TaskOcrDetection> detections;
    };
    constexpr const char* fields[] = {"speed", "stamina", "power", "guts", "wit", "skill_points", "failure_rate"};
    std::unordered_map<std::string, PreparedField> preparedFields;
    std::vector<std::string> batchFields;
    std::vector<UmaAssistant::Vision::GrayImage> batchImages;
    std::vector<UmaImageRect> batchPlacements;
    for (const auto* field : fields)
    {
        const bool failureRate = std::string(field) == "failure_rate";
        const auto* configured = findRegion(std::string("training.") + field);
        if (!configured)
            continue;
        auto roi = configured->roi;
        if (failureRate)
            roi.x = std::clamp(selectedLogo.CenterX() - roi.width / 2, 0, kReferenceWidth - roi.width);
        PreparedField preparedField;
        preparedField.roi = roi;
        auto crop = CropReferenceRoi(*frameImage, roi);
        if (!crop)
        {
            preparedFields.emplace(field, std::move(preparedField));
            continue;
        }
        if (m_scanCache)
            preparedField.cached = m_scanCache->Find(trainingType + "." + field, roi, *crop);
        preparedField.crop = std::move(crop);
        if (preparedField.cached)
        {
            preparedFields.emplace(field, std::move(preparedField));
            continue;
        }
        preparedField.image = UmaAssistant::Vision::prepare_numeric_glyphs(*preparedField.crop,
            !failureRate, GainSeedRule());
        if (!preparedField.image.has_value())
            preparedField.image = *preparedField.crop;
        if (!failureRate && gainStripVisible &&
            std::all_of(preparedField.image->gray.begin(), preparedField.image->gray.end(),
                [](std::uint8_t pixel) { return pixel >= 250; }))
        {
            const auto color = WarmColorCount(*frameImage, roi);
            preparedField.blank = color.second > 0 &&
                static_cast<double>(color.first) / static_cast<double>(color.second) < 0.0005;
        }
        if (!preparedField.blank)
        {
            batchFields.emplace_back(field);
        }
        preparedFields.emplace(field, std::move(preparedField));
    }

    std::vector<Recognition::NumericOcrBatchField> numericFields;
    numericFields.reserve(batchFields.size());
    for (const auto& field : batchFields)
        numericFields.push_back({field, *preparedFields.at(field).image,
            field == "failure_rate" ? 100 : 999, field != "failure_rate"});
    const auto numericReadings = numericFields.empty() ? Recognition::NumericOcrBatchResult{}
        : context.RecognizeNumericImages(numericFields);
    if (context.IsCancelled())
    {
        result.error = "Training numeric recognition was cancelled.";
        return result;
    }
    // Preserve the managed optimized reader: optional local numeric OCR reads
    // the isolated pages first, and Windows OCR receives unresolved fields.
    batchFields.erase(std::remove_if(batchFields.begin(), batchFields.end(), [&](const auto& field) {
        const auto reading = numericReadings.find(field);
        return reading != numericReadings.end() && reading->second.value.has_value();
    }), batchFields.end());
    batchImages.clear();
    for (const auto& field : batchFields)
        batchImages.push_back(UpscaleForOcr(*preparedFields.at(field).image));

    if (!batchImages.empty())
    {
        constexpr int margin = 24;
        const int rowHeight = std::max_element(batchImages.begin(), batchImages.end(),
            [](const auto& left, const auto& right) { return left.height < right.height; })->height + 2 * margin;
        const int width = std::max_element(batchImages.begin(), batchImages.end(),
            [](const auto& left, const auto& right) { return left.width < right.width; })->width + 2 * margin;
        const int height = rowHeight * static_cast<int>(batchImages.size());
        batchPlacements.reserve(batchImages.size());
        for (std::size_t index = 0; index < batchImages.size(); ++index)
            batchPlacements.push_back({margin, static_cast<int>(index) * rowHeight + margin,
                batchImages[index].width, batchImages[index].height});
        const auto batch = UmaAssistant::Vision::compose_gray(
            std::span<const UmaAssistant::Vision::GrayImage>(batchImages.data(), batchImages.size()),
            std::span<const UmaImageRect>(batchPlacements.data(), batchPlacements.size()), width, height);
        const auto detections = context.RecognizeImageText(batch, "en-US");
        for (std::size_t row = 0; row < batchFields.size(); ++row)
        {
            auto& fieldRead = preparedFields[batchFields[row]];
            const int rowTop = static_cast<int>(row) * rowHeight;
            const int rowBottom = rowTop + rowHeight;
            for (const auto& detection : detections)
            {
                if (detection.width <= 0 || detection.height <= 0 || detection.y < rowTop ||
                    detection.y + detection.height > rowBottom)
                    continue;
                fieldRead.detections.push_back(detection);
            }
        }
    }

    using Value = CareerDomain::ObservedValue<int>;
    auto parseField = [&](const std::string& field, bool failureRate, bool gain) {
        CareerDomain::ObservedValue<int> observed = CareerDomain::ObservedValue<int>::Unknown();
        const auto* configured = findRegion("training." + field);
        if (!configured)
            return observed;
        auto roi = configured->roi;
        if (failureRate)
            roi.x = std::clamp(selectedLogo.CenterX() - roi.width / 2, 0, kReferenceWidth - roi.width);
        const auto fieldRead = preparedFields.find(field);
        if (fieldRead == preparedFields.end())
            return observed;
        if (fieldRead->second.cached) return *fieldRead->second.cached;
        if (!fieldRead->second.image.has_value()) return observed;
        auto remember = [&](const Value& reading) {
            if (m_scanCache && fieldRead->second.crop && SameFrame(context, frame) && !context.IsCancelled())
                m_scanCache->Remember(trainingType + "." + field, roi, *fieldRead->second.crop, reading);
            return reading;
        };
        if (fieldRead->second.blank)
            return CareerDomain::ObservedValue<int>::Make(0, 0.82,
                CareerDomain::ObservationSource::ColorProbe,
                "Authored warm gain strip plus a visibly blank field");
        const auto numeric = numericReadings.find(field);
        if (numeric != numericReadings.end() && numeric->second.value.has_value())
            return remember(CareerDomain::ObservedValue<int>::Make(*numeric->second.value, 0.82,
                CareerDomain::ObservationSource::Ocr, "optional native numeric OCR on the same prepared crop"));
        const auto& prepared = *fieldRead->second.image;
        auto detections = fieldRead->second.detections;
        if (detections.empty())
        {
            const auto upscaled = UpscaleForOcr(prepared);
            detections = context.RecognizeImageText(upscaled, "en-US");
        }
        double confidence = 0.0;
        const auto value = ParseStrictValue(detections, failureRate, failureRate ? 100 : 999, confidence);
        if (value.has_value())
        {
            if (gain &&
                UmaAssistant::Vision::count_ink_groups(prepared, 128) !=
                    1 + static_cast<int>(std::to_string(*value).size()))
                return remember(observed);
            observed = CareerDomain::ObservedValue<int>::Make(*value, confidence,
                CareerDomain::ObservationSource::Ocr, "native Windows OCR on prepared training glyphs");
            return remember(observed);
        }
        return remember(observed);
    };

    const auto speed = parseField("speed", false, true);
    const auto stamina = parseField("stamina", false, true);
    const auto power = parseField("power", false, true);
    const auto guts = parseField("guts", false, true);
    const auto wit = parseField("wit", false, true);
    const auto skill = parseField("skill_points", false, true);
    const auto failure = parseField("failure_rate", true, false);
    result.candidate.speedGain = speed.value;
    result.candidate.staminaGain = stamina.value;
    result.candidate.powerGain = power.value;
    result.candidate.gutsGain = guts.value;
    result.candidate.witGain = wit.value;
    result.candidate.skillPointGain = skill.value;
    result.candidate.failureRatePercent = failure.value;

    const std::array<Value, 6> core{speed, stamina, power, guts, wit, skill};
    double coreConfidence = 1.0;
    bool allCoreKnown = true;
    CareerDomain::ObservationSource coreSource = core.front().source;
    for (const auto& value : core)
    {
        if (!value.value.has_value() || !value.IsReliable())
        {
            allCoreKnown = false;
            break;
        }
        coreConfidence = std::min(coreConfidence, value.confidence);
        if (value.source != coreSource)
            coreSource = CareerDomain::ObservationSource::Derived;
    }
    result.candidate.coreConfidence = allCoreKnown ? coreConfidence : 0.0;
    result.candidate.coreSource = allCoreKnown ? coreSource : CareerDomain::ObservationSource::Unknown;
    result.candidate.failureRateConfidence = failure.value.has_value() ? failure.confidence : 0.0;
    result.candidate.failureRateSource = failure.value.has_value() ? failure.source
        : CareerDomain::ObservationSource::Unknown;

    if (const auto* levelRegion = findRegion("training.training_level"))
    {
        const auto crop = CropReferenceRoi(*frameImage, levelRegion->roi);
        if (crop)
        {
            const auto key = trainingType + ".training_level";
            auto reading = m_scanCache ? m_scanCache->Find(key, levelRegion->roi, *crop) : std::nullopt;
            if (!reading)
            {
                const auto detections = context.RecognizeImageText(*crop, "en-US");
                double confidence = 0.0;
                const auto level = ParseAuxiliaryValue(detections, 10, confidence);
                reading = level ? Value::Make(*level, confidence, CareerDomain::ObservationSource::Ocr)
                    : Value::Unknown();
                if (m_scanCache && SameFrame(context, frame) && !context.IsCancelled())
                    m_scanCache->Remember(key, levelRegion->roi, *crop, *reading);
            }
            result.candidate.trainingLevel = reading->value;
            result.candidate.auxiliaryConfidence = reading->confidence;
            result.candidate.auxiliarySource = reading->source;
        }
    }
    if (!SameFrame(context, frame))
    {
        result.candidate = UnknownCandidate(trainingType);
        result.screenChanged = true;
        result.error = "The captured training frame changed during candidate recognition.";
    }
    return result;
}

CareerDomain::Ura::TrainingCandidate UraTrainingVisualScanner::UnknownCandidate(
    const std::string& trainingType) const
{
    CareerDomain::Ura::TrainingCandidate candidate;
    candidate.trainingType = trainingType;
    candidate.coreConfidence = 0.0;
    candidate.failureRateConfidence = 0.0;
    candidate.auxiliaryConfidence = 0.0;
    candidate.coreSource = CareerDomain::ObservationSource::Unknown;
    candidate.failureRateSource = CareerDomain::ObservationSource::Unknown;
    candidate.auxiliarySource = CareerDomain::ObservationSource::Unknown;
    return candidate;
}

} // namespace UmaAssistant::Career::Ura
