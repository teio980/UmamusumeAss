#include "NativeTaskExecutionContext.hpp"
#include "Resources/WicImageDecoder.hpp"
#include "Resources/ImageTransforms.hpp"
#include "Vision/ColorAnalysis.hpp"
#include "Session/DeviceSession.hpp"
#include "Common/PathEncoding.hpp"

#include <nlohmann/json.hpp>
#include <chrono>
#include <thread>
#include <cmath>
#include <algorithm>
#include <cctype>
#include <limits>
#include <sstream>
#include <iostream>

#ifdef _WIN32
#include <Windows.h>
#endif

namespace UmaAssistant::TaskEngine
{

namespace
{

class NativeImageRecognitionContext final : public IImageRecognitionContext
{
public:
    NativeImageRecognitionContext(std::stop_token cancellation, bool windowsOcr)
        : m_cancellation(cancellation), m_windowsOcr(windowsOcr) {}

    std::vector<TaskOcrDetection> RecognizeImageText(
        const Vision::GrayImage& image, const std::string& language) override
    {
        std::vector<TaskOcrDetection> result;
        if (!m_windowsOcr || IsCancelled()) return result;
        try
        {
            if (!m_ocr) m_ocr = std::make_unique<Recognition::WindowsOcrRecognizer>();
            for (const auto& item : m_ocr->recognize(image, language).detections)
                result.push_back({item.text, item.bounds.x, item.bounds.y,
                    item.bounds.width, item.bounds.height, item.confidence});
        }
        catch (const std::exception&) { return {}; }
        return result;
    }

    Recognition::NumericOcrBatchResult RecognizeNumericImages(
        const std::vector<Recognition::NumericOcrBatchField>& fields) override
    {
        if (IsCancelled() || fields.empty()) return {};
        try
        {
            if (!m_numericOcr) m_numericOcr = std::make_unique<Recognition::NumericOcrBatch>();
            return m_numericOcr->read(fields, m_cancellation);
        }
        catch (const std::exception&) { return {}; }
    }

    [[nodiscard]] bool IsCancelled() const override { return m_cancellation.stop_requested(); }

private:
    std::stop_token m_cancellation;
    bool m_windowsOcr;
    std::unique_ptr<Recognition::WindowsOcrRecognizer> m_ocr;
    std::unique_ptr<Recognition::NumericOcrBatch> m_numericOcr;
};

int ScaleRoundEven(double value)
{
    return static_cast<int>(std::nearbyint(value));
}

int ScaleReferenceCoordinate(int value, int source, int target)
{
    if (source <= 0 || target <= 0)
        return value;
    return ScaleRoundEven(static_cast<double>(value) * target / source);
}


std::string NormalizeName(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

} // namespace

NativeTaskExecutionContext::NativeTaskExecutionContext(
    std::shared_ptr<DeviceSession> session,
    std::shared_ptr<Resources::ResourceSnapshot const> resources,
    Recognition::WindowsOcrRecognizer* ocr,
    Api::CallbackSender* sender,
    std::uint64_t operation_id,
    std::stop_token cancellation,
    std::filesystem::path resource_base_dir,
    std::string event_task_id,
    std::uint64_t task_instance_id,
    std::function<void(std::int32_t, std::int32_t)> progress_callback)
    : m_session(std::move(session))
    , m_resources(std::move(resources))
    , m_ocr(ocr)
    , m_sender(sender)
    , m_operation_id(operation_id)
    , m_cancellation(std::move(cancellation))
    , m_resource_base_dir(std::move(resource_base_dir))
    , m_event_task_id(std::move(event_task_id))
    , m_task_instance_id(task_instance_id)
    , m_progress_callback(std::move(progress_callback))
{
}

NativeTaskExecutionContext::~NativeTaskExecutionContext()
{
    if (m_session)
    {
        if (m_held_frame_id != 0)
            static_cast<void>(TouchCancel());
        if (m_current_frame_id != 0)
            static_cast<void>(m_session->release_frame(m_current_frame_id));
        if (m_retained_frame_id != 0 && m_retained_frame_id != m_current_frame_id)
            static_cast<void>(m_session->release_frame(m_retained_frame_id));
        m_current_frame_id = 0;
        m_retained_frame_id = 0;
    }
}

std::filesystem::path NativeTaskExecutionContext::ResolveTemplatePath(const std::string& path) const
{
    std::filesystem::path p = Paths::FromUtf8(path);
    if (p.is_absolute() && std::filesystem::exists(p))
    {
        return p;
    }

    if (!m_template_base_dir.empty())
    {
        auto candidate = m_template_base_dir / p;
        if (std::filesystem::exists(candidate))
            return candidate;
    }

    if (!m_resource_base_dir.empty())
    {
        auto candidate = m_resource_base_dir / p;
        if (std::filesystem::exists(candidate))
            return candidate;

        candidate = m_resource_base_dir / "resource" / p;
        if (std::filesystem::exists(candidate))
            return candidate;
    }

    return p;
}

bool NativeTaskExecutionContext::CaptureScreen()
{
    m_last_error_code = 0;
    if (m_cancellation.stop_requested())
    {
        m_last_error_code = 9;
        return false;
    }
    if (!m_session)
    {
        m_last_error_code = 15;
        return false;
    }
    if (m_retained_frame_id != 0 && std::chrono::steady_clock::now() >= m_retained_match_expires)
        ReleaseRetainedMatch();

    if (m_current_frame_id != 0)
    {
        m_previous_screen = m_current_screen;
        m_previous_frame_snapshot = m_current_frame_snapshot;
        if (m_current_frame_id != m_retained_frame_id && m_current_frame_id != m_held_frame_id)
            static_cast<void>(m_session->release_frame(m_current_frame_id));
        m_current_frame_id = 0;
        m_current_screen.reset();
        m_current_metadata.reset();
        m_current_frame_snapshot.reset();
        m_current_frame_input_consumed = false;
    }

    auto opRes = m_session->capture(m_cancellation, true);
    if (opRes.failure.has_value() || !opRes.frame.has_value())
    {
        m_last_error_code = opRes.failure
            ? opRes.failure->error_code
            : static_cast<int>(ConnectionErrorCode::InvalidDeviceResponse);
        return false;
    }

    m_current_metadata = opRes.frame;
    m_current_frame_id = opRes.frame->id;

    m_current_screen = m_session->frame_pixels(m_current_frame_id);
    if (!m_current_screen)
    {
        static_cast<void>(m_session->release_frame(m_current_frame_id));
        m_current_frame_id = 0;
        m_current_metadata.reset();
        m_current_frame_input_consumed = false;
        m_last_error_code = static_cast<int>(ConnectionErrorCode::InvalidDeviceResponse);
        return false;
    }

    try
    {
        auto frame = std::make_shared<TaskFrameSnapshot>();
        frame->width = m_current_screen->width;
        frame->height = m_current_screen->height;
        frame->orientation = m_current_metadata->orientation;
        frame->frameId = m_current_metadata->id;
        frame->sessionGeneration = m_current_metadata->session_generation;
        frame->geometryGeneration = m_current_metadata->geometry_generation;
        frame->grayPixels = m_current_screen->gray;
        frame->rgbaPixels = m_current_screen->rgba;
        m_current_frame_snapshot = std::move(frame);
        m_current_frame_input_consumed = false;
        m_current_frame_captured_at = std::chrono::steady_clock::now();
        return true;
    }
    catch (...)
    {
        static_cast<void>(m_session->release_frame(m_current_frame_id));
        m_current_frame_id = 0;
        m_current_metadata.reset();
        m_current_screen.reset();
        m_current_frame_snapshot.reset();
        m_current_frame_input_consumed = false;
        m_last_error_code = static_cast<int>(ConnectionErrorCode::InvalidDeviceResponse);
        return false;
    }
}

std::shared_ptr<const TaskFrameSnapshot> NativeTaskExecutionContext::CaptureInspectionFrame()
{
    if (!m_session || m_cancellation.stop_requested()) return {};
    auto const captured = m_session->capture(m_cancellation, true);
    if (captured.failure || !captured.frame) return {};
    struct ReleaseFrame
    {
        DeviceSession& session;
        std::uint64_t id;
        ~ReleaseFrame() { static_cast<void>(session.release_frame(id)); }
    } release{*m_session, captured.frame->id};
    try
    {
        auto const image = m_session->frame_pixels(captured.frame->id);
        if (!image) return {};
        auto frame = std::make_shared<TaskFrameSnapshot>();
        frame->width = image->width;
        frame->height = image->height;
        frame->orientation = captured.frame->orientation;
        frame->frameId = captured.frame->id;
        frame->sessionGeneration = captured.frame->session_generation;
        frame->geometryGeneration = captured.frame->geometry_generation;
        frame->grayPixels = image->gray;
        frame->rgbaPixels = image->rgba;
        return frame;
    }
    catch (...) { return {}; }
}

TaskMatchResult NativeTaskExecutionContext::MatchTemplate(
    const std::string& templatePath,
    double threshold,
    const std::optional<TaskRoi>& roi,
    const std::string& algorithm)
{
    return MatchTemplateAdvanced(templatePath, threshold, roi, algorithm,
                                 m_reference_width, m_reference_height, {});
}

TaskMatchResult NativeTaskExecutionContext::MatchTemplateAdvanced(
    const std::string& templatePath,
    double threshold,
    const std::optional<TaskRoi>& roi,
    const std::string& algorithm,
    int referenceWidth,
    int referenceHeight,
    const std::vector<double>& scaleCandidates)
{
    if (!m_current_screen || !m_resources || referenceWidth <= 0 || referenceHeight <= 0)
        return TaskMatchResult{false, 0.0, 0, 0, 0, 0};

    auto resolvedPath = ResolveTemplatePath(templatePath);
    auto prep = Resources::TemplatePreparation::original;
    auto templ = m_resources->load_template(Paths::ToUtf8(resolvedPath), prep);
    if (!templ)
    {
        return TaskMatchResult{false, 0.0, 0, 0, 0, 0};
    }

    Vision::Roi matchRoi = std::nullopt;
    if (roi.has_value())
    {
        matchRoi = std::array<std::int32_t, 4>{roi->x, roi->y, roi->width, roi->height};
    }

    Vision::MatchResult match;
    const auto normalizedAlgorithm = NormalizeName(algorithm);
    if (!scaleCandidates.empty() && normalizedAlgorithm != "matchtemplateexhaustivecolor"
        && normalizedAlgorithm != "matchtemplatecolor"
        && normalizedAlgorithm != "matchtemplatecol" && normalizedAlgorithm != "matchtemplatecolortext"
        && normalizedAlgorithm != "color" && normalizedAlgorithm != "button")
    {
        Vision::ScaledOptions options;
        options.scales = scaleCandidates;
        if (normalizedAlgorithm == "matchtemplatescaledfast")
        {
            options.refine_scale = false;
            options.full_search_on_miss = false;
            options.candidate_step = 4;
        }
        match = Vision::find_scaled(*m_current_screen, templ->image, matchRoi, threshold,
                                    referenceWidth, referenceHeight, options);
    }
    else if (normalizedAlgorithm == "matchtemplateexhaustivecolor")
    {
        match = Vision::find_exhaustive_color(*m_current_screen, templ->image, matchRoi, threshold,
                                             referenceWidth, referenceHeight);
    }
    else if (normalizedAlgorithm == "matchtemplatecolor" || normalizedAlgorithm == "matchtemplatecol"
             || normalizedAlgorithm == "matchtemplatecolortext" || normalizedAlgorithm == "color")
    {
        match = Vision::find_color(*m_current_screen, templ->image, matchRoi, threshold,
                                   referenceWidth, referenceHeight,
                                   normalizedAlgorithm == "matchtemplatecolortext");
    }
    else if (normalizedAlgorithm == "button")
    {
        match = Vision::find_button(*m_current_screen, templ->image, matchRoi, threshold,
                                    referenceWidth, referenceHeight);
    }
    else
    {
        match = Vision::find(*m_current_screen, templ->image, matchRoi, threshold,
                             referenceWidth, referenceHeight);
    }

    TaskMatchResult result;
    result.found = match.found;
    result.score = match.score;
    result.x = ScaleReferenceCoordinate(match.x, m_current_screen->width, referenceWidth);
    result.y = ScaleReferenceCoordinate(match.y, m_current_screen->height, referenceHeight);
    result.width = ScaleReferenceCoordinate(match.width, m_current_screen->width, referenceWidth);
    result.height = ScaleReferenceCoordinate(match.height, m_current_screen->height, referenceHeight);
    result.referenceWidth = referenceWidth;
    result.referenceHeight = referenceHeight;
    if (m_current_metadata)
    {
        result.frameId = m_current_metadata->id;
        result.sessionGeneration = m_current_metadata->session_generation;
        result.geometryGeneration = m_current_metadata->geometry_generation;
    }
    return result;
}

bool NativeTaskExecutionContext::CheckImageChange(const TaskMatchResult& match)
{
    if (!m_previous_screen || !m_current_screen)
        return false;

    Vision::MatchResult vr;
    vr.found = match.found;
    vr.score = match.score;
    vr.x = ScaleReferenceCoordinate(match.x, match.referenceWidth, m_current_screen->width);
    vr.y = ScaleReferenceCoordinate(match.y, match.referenceHeight, m_current_screen->height);
    vr.width = ScaleReferenceCoordinate(match.width, match.referenceWidth, m_current_screen->width);
    vr.height = ScaleReferenceCoordinate(match.height, match.referenceHeight, m_current_screen->height);

    return Vision::has_meaningful_change(*m_previous_screen, *m_current_screen, vr);
}

std::vector<TaskOcrDetection> NativeTaskExecutionContext::RecognizeText(
    const std::string& language,
    const std::optional<TaskRoi>& roi)
{
    std::vector<TaskOcrDetection> result;
    if (!m_current_screen || m_ocr == nullptr)
        return result;

    try
    {
        Vision::GrayImage const* imageToRecognize = &(*m_current_screen);
        Vision::GrayImage cropped;
        int offsetX = 0;
        int offsetY = 0;

        if (roi.has_value() && roi->width > 0 && roi->height > 0)
        {
            int const x = ScaleReferenceCoordinate(roi->x, m_reference_width, m_current_screen->width);
            int const y = ScaleReferenceCoordinate(roi->y, m_reference_height, m_current_screen->height);
            int const width = ScaleReferenceCoordinate(roi->width, m_reference_width, m_current_screen->width);
            int const height = ScaleReferenceCoordinate(roi->height, m_reference_height, m_current_screen->height);
            cropped = Resources::crop_image(*m_current_screen, x, y, width, height);
            imageToRecognize = &cropped;
            offsetX = x;
            offsetY = y;
        }

        auto ocrResult = m_ocr->recognize(*imageToRecognize, language);
        for (const auto& det : ocrResult.detections)
        {
            TaskOcrDetection d;
            d.text = det.text;
            d.x = ScaleReferenceCoordinate(det.bounds.x + offsetX, m_current_screen->width, m_reference_width);
            d.y = ScaleReferenceCoordinate(det.bounds.y + offsetY, m_current_screen->height, m_reference_height);
            d.width = ScaleReferenceCoordinate(det.bounds.width, m_current_screen->width, m_reference_width);
            d.height = ScaleReferenceCoordinate(det.bounds.height, m_current_screen->height, m_reference_height);
            d.confidence = det.confidence;
            result.push_back(std::move(d));
        }
    }
    catch (...)
    {
    }

    return result;
}

bool NativeTaskExecutionContext::ProbeHsv(
    int x, int y, int radius,
    double hMin, double hMax,
    double sMin, double sMax,
    double vMin, double vMax,
    double minRatio)
{
    if (!m_current_screen)
        return false;

    UmaColorRule rule{};
    rule.use_hsv = 1;
    rule.hue_min = hMin;
    rule.hue_max = hMax;
    rule.saturation_min = sMin;
    rule.saturation_max = sMax;
    rule.value_min = vMin;
    rule.value_max = vMax;

    int const pixelX = ScaleReferenceCoordinate(x, m_reference_width, m_current_screen->width);
    int const pixelY = ScaleReferenceCoordinate(y, m_reference_height, m_current_screen->height);
    int const pixelRadiusX = ScaleReferenceCoordinate(radius, m_reference_width, m_current_screen->width);
    int const pixelRadiusY = ScaleReferenceCoordinate(radius, m_reference_height, m_current_screen->height);
    UmaImageRect region{pixelX - pixelRadiusX, pixelY - pixelRadiusY, pixelRadiusX * 2, pixelRadiusY * 2};
    auto [matching, sampled] = Vision::count_color(*m_current_screen, rule, region, nullptr);
    if (sampled == 0)
        return false;

    return (static_cast<double>(matching) / static_cast<double>(sampled)) >= minRatio;
}

std::vector<TaskOcrDetection> NativeTaskExecutionContext::RecognizeImageText(
    const Vision::GrayImage& image, const std::string& language)
{
    std::vector<TaskOcrDetection> result;
    if (!m_ocr || IsCancelled()) return result;
    try
    {
        for (const auto& item : m_ocr->recognize(image, language).detections)
            result.push_back({item.text, item.bounds.x, item.bounds.y,
                item.bounds.width, item.bounds.height, item.confidence});
    }
    catch (const std::exception& error)
    {
        Log("WARN", std::string("Processed-image OCR failed: ") + error.what());
    }
    return result;
}

bool NativeTaskExecutionContext::PrepareHeldTouch()
{
    if (!PrepareCurrentFrameInput()) return false;
    const auto result = m_session->prepare_touch(m_current_frame_id, m_cancellation);
    if (result.failure) m_last_error_code = result.failure->error_code;
    return !result.failure;
}

Recognition::NumericOcrBatchResult NativeTaskExecutionContext::RecognizeNumericImages(
    const std::vector<Recognition::NumericOcrBatchField>& fields)
{
    if (IsCancelled() || fields.empty()) return {};
    if (!m_numeric_ocr) m_numeric_ocr = std::make_unique<Recognition::NumericOcrBatch>();
    try
    {
        return m_numeric_ocr->read(fields, m_cancellation);
    }
    catch (const std::exception& error)
    {
        if (!IsCancelled()) Log("WARN", std::string("Numeric-image OCR failed: ") + error.what());
        return {};
    }
}

std::shared_ptr<IImageRecognitionContext> NativeTaskExecutionContext::CreateImageRecognitionContext()
{
    if (!m_image_recognition)
        m_image_recognition = std::make_shared<NativeImageRecognitionContext>(m_cancellation, m_ocr != nullptr);
    return m_image_recognition;
}

bool NativeTaskExecutionContext::TouchDownMatch(int x, int y, const TaskMatchResult& source)
{
    if (m_held_frame_id != 0 || !source.found || !PrepareCurrentFrameInput()
        || source.frameId == 0 || source.frameId != m_current_frame_id
        || !m_current_metadata || source.referenceWidth <= 0 || source.referenceHeight <= 0
        || source.sessionGeneration != m_current_metadata->session_generation
        || source.geometryGeneration != m_current_metadata->geometry_generation)
        return false;
    const auto [canonicalX, canonicalY] = MapImagePointToCanonical(
        ScaleReferenceCoordinate(x, source.referenceWidth, m_current_metadata->width),
        ScaleReferenceCoordinate(y, source.referenceHeight, m_current_metadata->height), *m_current_metadata);
    if (canonicalX < 0 || canonicalY < 0) return false;
    // Retain the attempted press before DOWN: a failing write may still have
    // reached the device and must be canceled by the owner or destructor.
    m_held_frame_id = source.frameId;
    m_held_canonical_x = canonicalX;
    m_held_canonical_y = canonicalY;
    if (m_retained_frame_id == source.frameId)
        ReleaseRetainedMatch();
    m_current_frame_input_consumed = true;
    const auto result = m_session->touch(source.frameId, canonicalX, canonicalY,
        HeldTouchPhase::down, m_cancellation);
    if (result.failure) m_last_error_code = result.failure->error_code;
    return !result.failure;
}

bool NativeTaskExecutionContext::ReleaseHeldTouch(HeldTouchPhase phase)
{
    if (!m_session || m_held_frame_id == 0) return false;
    const auto frameId = m_held_frame_id;
    const auto result = m_session->touch(frameId, m_held_canonical_x,
        m_held_canonical_y, phase, std::stop_token{});
    if (result.failure)
    {
        m_last_error_code = result.failure->error_code;
        if (phase == HeldTouchPhase::up) return false; // retain for CANCEL retry
    }
    m_held_frame_id = 0;
    if (frameId != m_retained_frame_id)
        static_cast<void>(m_session->release_frame(frameId));
    return !result.failure;
}

bool NativeTaskExecutionContext::TouchUp() { return ReleaseHeldTouch(HeldTouchPhase::up); }
bool NativeTaskExecutionContext::TouchCancel() { return ReleaseHeldTouch(HeldTouchPhase::cancel); }

bool NativeTaskExecutionContext::Tap(int x, int y)
{
    if (!PrepareCurrentFrameInput())
        return false;

    int const canonicalX = ScaleReferenceCoordinate(x, m_reference_width, 1080);
    int const canonicalY = ScaleReferenceCoordinate(y, m_reference_height, 1920);
    auto res = m_session->tap(m_current_frame_id, canonicalX, canonicalY, m_cancellation);
    m_current_frame_input_consumed = true;
    if (m_current_frame_id != m_retained_frame_id)
        static_cast<void>(m_session->release_frame(m_current_frame_id));
    if (res.failure)
        m_last_error_code = res.failure->error_code;
    return !res.failure.has_value();
}

bool NativeTaskExecutionContext::Swipe(int x1, int y1, int x2, int y2, int durationMs)
{
    if (!PrepareCurrentFrameInput())
        return false;

    int const canonicalX1 = ScaleReferenceCoordinate(x1, m_reference_width, 1080);
    int const canonicalY1 = ScaleReferenceCoordinate(y1, m_reference_height, 1920);
    int const canonicalX2 = ScaleReferenceCoordinate(x2, m_reference_width, 1080);
    int const canonicalY2 = ScaleReferenceCoordinate(y2, m_reference_height, 1920);
    auto res = m_session->swipe(m_current_frame_id, canonicalX1, canonicalY1,
                                canonicalX2, canonicalY2, durationMs, false, m_cancellation);
    m_current_frame_input_consumed = true;
    if (m_current_frame_id != m_retained_frame_id)
        static_cast<void>(m_session->release_frame(m_current_frame_id));
    if (res.failure)
        m_last_error_code = res.failure->error_code;
    return !res.failure.has_value();
}

std::shared_ptr<const TaskFrameSnapshot> NativeTaskExecutionContext::GetCurrentFrame() const
{
    return m_current_frame_snapshot;
}

std::string NativeTaskExecutionContext::GetDeviceSerial() const
{
    return m_session ? m_session->serial() : std::string{};
}

std::shared_ptr<const TaskFrameSnapshot> NativeTaskExecutionContext::GetPreviousFrame() const
{
    return m_previous_frame_snapshot;
}

std::shared_ptr<const Vision::GrayImage> NativeTaskExecutionContext::LoadTemplateImage(const std::string& path)
{
    return m_resources ? m_resources->load_image(Paths::ToUtf8(ResolveTemplatePath(path))) : nullptr;
}

void NativeTaskExecutionContext::SetCoordinateReference(int width, int height)
{
    if (width > 0 && height > 0)
    {
        m_reference_width = width;
        m_reference_height = height;
    }
}

std::pair<int, int> NativeTaskExecutionContext::GetCoordinateReference() const
{
    return {m_reference_width, m_reference_height};
}

void NativeTaskExecutionContext::SetTemplateBaseDirectory(const std::string& directory)
{
    m_template_base_dir = directory.empty() ? std::filesystem::path{} : Paths::FromUtf8(directory);
}

std::string NativeTaskExecutionContext::GetTemplateBaseDirectory() const
{
    return Paths::ToUtf8(m_template_base_dir);
}

bool NativeTaskExecutionContext::TapCoordinate(int x, int y)
{
    m_last_error_code = 0;
    if (m_cancellation.stop_requested() || !m_session)
    {
        m_last_error_code = static_cast<int>(m_cancellation.stop_requested()
            ? ConnectionErrorCode::Canceled : ConnectionErrorCode::DeviceDisconnected);
        return false;
    }
    const auto result = m_session->tap_coordinate(x, y, m_reference_width, m_reference_height, m_cancellation);
    m_current_frame_input_consumed = true;
    if (m_current_frame_id != 0 && m_current_frame_id != m_retained_frame_id)
        static_cast<void>(m_session->release_frame(m_current_frame_id));
    if (result.failure) m_last_error_code = result.failure->error_code;
    return !result.failure;
}

bool NativeTaskExecutionContext::TapMatch(int x, int y, const TaskMatchResult& match)
{
    m_last_error_code = 0;
    const auto reject = [&](int errorCode, const std::string& reason) {
        m_last_error_code = errorCode;
        Log("WARN", "TapMatch failed (error=" + std::to_string(errorCode)
            + ", frame=" + std::to_string(match.frameId)
            + ", point=" + std::to_string(x) + "," + std::to_string(y)
            + "): " + reason);
        return false;
    };
    if (m_cancellation.stop_requested())
    {
        return reject(static_cast<int>(ConnectionErrorCode::Canceled), "Operation was canceled.");
    }
    if (!m_session)
    {
        return reject(static_cast<int>(ConnectionErrorCode::DeviceDisconnected), "No connected device session.");
    }

    auto const frameId = match.frameId != 0 ? match.frameId : m_current_frame_id;
    if (frameId == 0 || (frameId != m_current_frame_id && frameId != m_retained_frame_id))
    {
        return reject(static_cast<int>(ConnectionErrorCode::InvalidArgument),
            "Source frame is neither current nor retained for retry.");
    }
    const bool isRetainedMatch = frameId == m_retained_frame_id;
    if (isRetainedMatch)
    {
        if (m_retained_taps_remaining <= 0 || std::chrono::steady_clock::now() >= m_retained_match_expires)
        {
            const auto expiredFrameId = m_retained_frame_id;
            ReleaseRetainedMatch();
            if (expiredFrameId != 0 && expiredFrameId == m_current_frame_id)
            {
                static_cast<void>(m_session->release_frame(expiredFrameId));
                m_current_frame_id = 0;
                m_current_metadata.reset();
                m_current_screen.reset();
                m_current_frame_snapshot.reset();
                m_current_frame_input_consumed = true;
            }
            return reject(static_cast<int>(ConnectionErrorCode::InvalidArgument),
                "Retained retry frame expired or exhausted its tap budget.");
        }
        if (match.referenceWidth <= 0 || match.referenceHeight <= 0)
        {
            return reject(static_cast<int>(ConnectionErrorCode::InvalidArgument),
                "Retained match has invalid reference dimensions.");
        }
    }
    else if (frameId == m_current_frame_id && m_current_frame_input_consumed)
    {
        return reject(static_cast<int>(ConnectionErrorCode::InvalidArgument),
            "Current screenshot evidence was already consumed by an input.");
    }
    else if (frameId == m_current_frame_id &&
             std::chrono::steady_clock::now() - m_current_frame_captured_at > std::chrono::seconds(20))
    {
        if (frameId != m_retained_frame_id)
            static_cast<void>(m_session->release_frame(frameId));
        m_current_frame_id = 0;
        m_current_metadata.reset();
        m_current_screen.reset();
        m_current_frame_snapshot.reset();
        m_current_frame_input_consumed = true;
        return reject(static_cast<int>(ConnectionErrorCode::InvalidArgument),
            "Current screenshot evidence is older than 20 seconds.");
    }
    auto const metadata = m_session->frame_metadata(frameId);
    if (!metadata || (match.sessionGeneration != 0
        && metadata->session_generation != match.sessionGeneration)
        || (match.geometryGeneration != 0
        && metadata->geometry_generation != match.geometryGeneration))
    {
        return reject(static_cast<int>(ConnectionErrorCode::InvalidArgument),
            "Source frame was released or its session/geometry generation no longer matches.");
    }

    const int pixelX = ScaleReferenceCoordinate(x, match.referenceWidth, metadata->width);
    const int pixelY = ScaleReferenceCoordinate(y, match.referenceHeight, metadata->height);
    auto [canonicalX, canonicalY] = MapImagePointToCanonical(pixelX, pixelY, *metadata);
    if (isRetainedMatch)
    {
        if (canonicalX != m_retained_canonical_x || canonicalY != m_retained_canonical_y)
        {
            return reject(static_cast<int>(ConnectionErrorCode::InvalidArgument),
                "Retry coordinates differ from the retained target point.");
        }
        canonicalX = m_retained_canonical_x;
        canonicalY = m_retained_canonical_y;
    }
    if (canonicalX < 0 || canonicalY < 0)
    {
        return reject(static_cast<int>(ConnectionErrorCode::InvalidArgument),
            "Match coordinates cannot be mapped to the canonical screen.");
    }
    auto res = m_session->tap(frameId, canonicalX, canonicalY, m_cancellation);
    if (frameId == m_current_frame_id)
    {
        m_current_frame_input_consumed = true;
        if (frameId != m_retained_frame_id)
            static_cast<void>(m_session->release_frame(frameId));
    }
    if (isRetainedMatch)
    {
        // A failed input can still have reached the device. Consume the lease
        // budget for every actual attempt, matching the managed retry intent.
        --m_retained_taps_remaining;
        if (m_retained_taps_remaining <= 0)
            ReleaseRetainedMatch();
    }
    if (res.failure)
        return reject(res.failure->error_code, res.failure->phase + ": " + res.failure->message);
    return true;
}

bool NativeTaskExecutionContext::RetainMatchForRetry(
    const TaskMatchResult& match,
    int maxTapAttempts,
    int lifetimeMilliseconds,
    int fixedReferenceX,
    int fixedReferenceY)
{
    if (!m_session || match.frameId == 0
        || (match.frameId != m_current_frame_id && match.frameId != m_retained_frame_id))
        return false;
    if (maxTapAttempts <= 0 || maxTapAttempts > 100 || lifetimeMilliseconds <= 0 ||
        match.referenceWidth <= 0 || match.referenceHeight <= 0)
        return false;
    auto const metadata = m_session->frame_metadata(match.frameId);
    if (!metadata || metadata->session_generation != match.sessionGeneration
        || metadata->geometry_generation != match.geometryGeneration)
        return false;
    if ((fixedReferenceX < 0) != (fixedReferenceY < 0))
        return false;
    const int referenceX = fixedReferenceX < 0 ? match.CenterX() : fixedReferenceX;
    const int referenceY = fixedReferenceY < 0 ? match.CenterY() : fixedReferenceY;
    const int pixelX = ScaleReferenceCoordinate(referenceX, match.referenceWidth, metadata->width);
    const int pixelY = ScaleReferenceCoordinate(referenceY, match.referenceHeight, metadata->height);
    const auto [canonicalX, canonicalY] = MapImagePointToCanonical(pixelX, pixelY, *metadata);
    if (canonicalX < 0 || canonicalY < 0)
        return false;

    const auto now = std::chrono::steady_clock::now();
    if (m_retained_frame_id != 0)
    {
        const bool leaseActive = m_retained_taps_remaining > 0 && now < m_retained_match_expires;
        if (m_retained_frame_id == match.frameId)
        {
            // Retaining an already leased frame is idempotent. Never let a
            // caller renew its expiry, restore spent attempts, or move the
            // frozen target point; retry loops must consume the original
            // intent rather than manufacture a fresh budget.
            if (!leaseActive || canonicalX != m_retained_canonical_x || canonicalY != m_retained_canonical_y)
                return false;
            return true;
        }
        if (leaseActive)
        {
            // An active explicit retry intent belongs to a different source
            // frame. It must be released by its owner before another frame can
            // establish an intent.
            return false;
        }

        // An expired or exhausted lease on an older frame cannot be revived.
        // It may be discarded so a separately captured, fresh frame can start
        // its own ordinary bounded retry intent.
        ReleaseRetainedMatch();
    }

    if (match.frameId != m_current_frame_id || m_current_frame_input_consumed ||
        now - m_current_frame_captured_at > std::chrono::seconds(20))
        return false;

    m_retained_frame_id = match.frameId;
    m_retained_taps_remaining = maxTapAttempts;
    m_retained_reference_x = referenceX;
    m_retained_reference_y = referenceY;
    m_retained_reference_width = match.referenceWidth;
    m_retained_reference_height = match.referenceHeight;
    m_retained_canonical_x = canonicalX;
    m_retained_canonical_y = canonicalY;
    m_retained_match_expires = now +
        std::chrono::milliseconds((std::max)(1, lifetimeMilliseconds));
    return true;
}

void NativeTaskExecutionContext::ReleaseRetainedMatch()
{
    if (m_retained_frame_id != 0 && m_retained_frame_id != m_current_frame_id
        && m_retained_frame_id != m_held_frame_id && m_session)
        static_cast<void>(m_session->release_frame(m_retained_frame_id));
    m_retained_frame_id = 0;
    m_retained_taps_remaining = 0;
    m_retained_match_expires = {};
    m_retained_reference_x = 0;
    m_retained_reference_y = 0;
    m_retained_reference_width = 900;
    m_retained_reference_height = 1600;
    m_retained_canonical_x = 0;
    m_retained_canonical_y = 0;
}

bool NativeTaskExecutionContext::HasRetainedMatch() const
{
    return m_retained_frame_id != 0 &&
        std::chrono::steady_clock::now() < m_retained_match_expires &&
        m_retained_taps_remaining > 0;
}

bool NativeTaskExecutionContext::IsMatchRetained(const TaskMatchResult& match) const
{
    return HasRetainedMatch() && match.frameId != 0 && match.frameId == m_retained_frame_id;
}

bool NativeTaskExecutionContext::InputText(const std::string& text)
{
    if (!PrepareCurrentFrameInput())
        return false;

    auto res = m_session->input_text(m_current_frame_id, text, m_cancellation);
    m_current_frame_input_consumed = true;
    if (m_current_frame_id != m_retained_frame_id)
        static_cast<void>(m_session->release_frame(m_current_frame_id));
    if (res.failure)
        m_last_error_code = res.failure->error_code;
    return !res.failure.has_value();
}

bool NativeTaskExecutionContext::SendKey(const std::string& keyCode)
{
    if (!PrepareCurrentFrameInput())
        return false;

    auto res = m_session->key_event(m_current_frame_id, keyCode, m_cancellation);
    m_current_frame_input_consumed = true;
    if (m_current_frame_id != m_retained_frame_id)
        static_cast<void>(m_session->release_frame(m_current_frame_id));
    if (res.failure)
        m_last_error_code = res.failure->error_code;
    return !res.failure.has_value();
}

bool NativeTaskExecutionContext::PrepareCurrentFrameInput()
{
    m_last_error_code = 0;
    if (m_held_frame_id != 0)
    {
        m_last_error_code = static_cast<int>(ConnectionErrorCode::Busy);
        return false;
    }
    if (m_cancellation.stop_requested())
    {
        m_last_error_code = static_cast<int>(ConnectionErrorCode::Canceled);
        return false;
    }
    if (!m_session)
    {
        m_last_error_code = static_cast<int>(ConnectionErrorCode::DeviceDisconnected);
        return false;
    }
    if (m_current_frame_id == 0 || m_current_frame_input_consumed
        || m_current_frame_id == m_retained_frame_id)
    {
        m_last_error_code = static_cast<int>(ConnectionErrorCode::InvalidArgument);
        return false;
    }
    if (std::chrono::steady_clock::now() - m_current_frame_captured_at > std::chrono::seconds(20))
    {
        m_last_error_code = static_cast<int>(ConnectionErrorCode::InvalidArgument);
        static_cast<void>(m_session->release_frame(m_current_frame_id));
        m_current_frame_id = 0;
        m_current_metadata.reset();
        m_current_screen.reset();
        m_current_frame_snapshot.reset();
        m_current_frame_input_consumed = true;
        return false;
    }
    return true;
}

void NativeTaskExecutionContext::Sleep(int milliseconds)
{
    if (milliseconds <= 0)
        return;

    auto start = std::chrono::steady_clock::now();
    while (!m_cancellation.stop_requested())
    {
        auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - start).count();
        if (elapsed >= milliseconds)
            break;

        auto remaining = milliseconds - elapsed;
        auto step = std::min<int>(50, static_cast<int>(remaining));
        std::this_thread::sleep_for(std::chrono::milliseconds(step));
    }
}

bool NativeTaskExecutionContext::IsCancelled() const
{
    return m_cancellation.stop_requested();
}

int NativeTaskExecutionContext::GetLastErrorCode() const
{
    return m_last_error_code;
}

void NativeTaskExecutionContext::Log(const std::string& level, const std::string& message)
{
    const auto normalized = NormalizeName(level);
    const std::string wireLevel = normalized == "info" ? "INFO"
        : normalized == "warn" || normalized == "warning" ? "WARN"
        : normalized == "error" ? "ERROR"
        : normalized == "debug" ? "DEBUG"
        : "DEBUG";
    std::string line = "UmaAssistant TaskEngine [" + wireLevel + "] " + message + "\n";
#ifdef _WIN32
    OutputDebugStringA(line.c_str());
#else
    std::clog << line;
#endif
    if (m_sender != nullptr)
    {
        nlohmann::json payload = {
            {"task_id", m_event_task_id},
            {"task_instance_id", m_task_instance_id},
            {"level", wireLevel},
            {"message", message}
        };
        m_sender->send_runtime(UMA_MSG_RUNTIME_DIAGNOSTIC, m_operation_id, "RuntimeDiagnostic", std::move(payload));
    }
}

void NativeTaskExecutionContext::EmitEvent(const TaskExecutionEvent& event)
{
    if (m_sender == nullptr)
        return;

    if (m_progress_callback)
    {
        try
        {
            m_progress_callback(event.completedUnits, event.totalUnits);
        }
        catch (...)
        {
            // Snapshot reporting must not interrupt a task.
        }
    }

    nlohmann::json payload = {
        {"task_id", m_event_task_id.empty()
            ? (event.taskId.empty() ? event.taskName : event.taskId)
            : m_event_task_id},
        {"task_instance_id", m_task_instance_id},
        {"event_type", static_cast<int>(event.type)},
        {"task_name", event.taskName},
        {"pipeline_name", event.pipelineName},
        {"step_index", event.stepIndex},
        {"succeeded", event.succeeded},
        {"score", event.score},
        {"target_x", event.targetX},
        {"target_y", event.targetY},
        {"message", event.message},
        {"timestamp_ms", event.timestampMs},
        {"completed_units", event.completedUnits},
        {"total_units", event.totalUnits}
    };

    if (!m_event_task_id.empty())
        m_sender->send_runtime(17, m_operation_id, "GameTaskProgress", std::move(payload));
    else
        m_sender->send_runtime(14, m_operation_id, "TaskProgress", std::move(payload));
}

} // namespace UmaAssistant::TaskEngine
